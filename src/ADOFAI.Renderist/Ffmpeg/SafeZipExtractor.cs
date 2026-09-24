using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>归档条目到托管安装路径的提取规则。</summary>
    internal sealed class SafeZipEntryRule
    {
        public SafeZipEntryRule(string matchFileName, string targetRelativePath, string expectedSha256, bool required)
        {
            MatchFileName = matchFileName;
            TargetRelativePath = targetRelativePath;
            ExpectedSha256 = expectedSha256;
            Required = required;
        }

        /// <summary>归档内要匹配的文件名（最后一个路径段，大小写不敏感精确相等）。</summary>
        public string MatchFileName { get; private set; }

        /// <summary>提取目标相对路径（'/' 分隔，必须通过安全相对路径校验）。</summary>
        public string TargetRelativePath { get; private set; }

        public string ExpectedSha256 { get; private set; }

        public bool Required { get; private set; }
    }

    internal sealed class SafeZipExtractedFile
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
    }

    internal sealed class SafeZipExtractionResult
    {
        public bool Success { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }
        public IReadOnlyList<SafeZipExtractedFile> Files { get; set; }

        public static SafeZipExtractionResult Failure(string code, string detail)
        {
            return new SafeZipExtractionResult
            {
                Success = false,
                ErrorCode = code,
                ErrorDetail = detail,
                Files = new SafeZipExtractedFile[0],
            };
        }
    }

    /// <summary>
    /// 安全 ZIP 解压（唯一单点）。
    ///
    /// 设计原则：**默认拒绝**。只有明确匹配白名单规则的文件才会被写出，
    /// 归档中任何不安全条目都会让整次解压失败（而不是被静默跳过）。
    ///
    /// 防护项：
    ///   * 绝对路径 / 盘符 / UNC；
    ///   * '..' 与 '.' 路径段（路径穿越）；
    ///   * 大小写不敏感的重复条目名（在 Windows 上会互相覆盖）；
    ///   * 符号链接 / 重解析点；
    ///   * 非法文件名字符；
    ///   * 单条目与归档总量上限（结构性下限，来自钉住资产的声明尺寸，不是产品性能上限）；
    ///   * 写出前再次确认目标绝对路径仍在目标目录之内；
    ///   * 解压后对声明了哈希的文件逐字节校验。
    ///
    /// 不接触 Unity / UMM / Harmony。
    /// </summary>
    internal static class SafeZipExtractor
    {
        private const int CopyBufferSize = 1 << 20;

        public static SafeZipExtractionResult Extract(
            string archivePath,
            string destinationDirectory,
            IReadOnlyList<SafeZipEntryRule> rules,
            long maxTotalBytes,
            long maxEntryBytes,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                return SafeZipExtractionResult.Failure("zip-not-found", archivePath);

            if (string.IsNullOrWhiteSpace(destinationDirectory))
                return SafeZipExtractionResult.Failure("destination-empty", null);

            if (rules == null || rules.Count == 0)
                return SafeZipExtractionResult.Failure("no-rules", null);

            for (int i = 0; i < rules.Count; i++)
            {
                if (!FfmpegAssetManifest.IsSafeRelativePath(rules[i].TargetRelativePath))
                {
                    return SafeZipExtractionResult.Failure(
                        "rule-target-path-unsafe", rules[i].TargetRelativePath);
                }
            }

            string destinationFull;
            try
            {
                destinationFull = Path.GetFullPath(destinationDirectory);
            }
            catch (Exception ex)
            {
                return SafeZipExtractionResult.Failure("destination-invalid", ex.Message);
            }

            try
            {
                Directory.CreateDirectory(destinationFull);
            }
            catch (Exception ex)
            {
                return SafeZipExtractionResult.Failure("destination-create-failed", ex.Message);
            }

            try
            {
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    return ExtractCore(archive, destinationFull, rules, maxTotalBytes, maxEntryBytes, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return SafeZipExtractionResult.Failure("cancelled", null);
            }
            catch (InvalidDataException ex)
            {
                return SafeZipExtractionResult.Failure("zip-corrupt", ex.Message);
            }
            catch (Exception ex)
            {
                return SafeZipExtractionResult.Failure("zip-read-failed", ex.Message);
            }
        }

        private static SafeZipExtractionResult ExtractCore(
            ZipArchive archive,
            string destinationFull,
            IReadOnlyList<SafeZipEntryRule> rules,
            long maxTotalBytes,
            long maxEntryBytes,
            CancellationToken cancellationToken)
        {
            // ---- 阶段 1：先把整份归档的安全性与规则匹配一次算清，再写任何字节。----
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedEntry = new ZipArchiveEntry[rules.Count];
            var matchedCount = new int[rules.Count];
            long declaredTotal = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string normalized;
                string unsafeReason;
                if (!TryNormalizeEntryName(entry.FullName, out normalized, out unsafeReason))
                {
                    return SafeZipExtractionResult.Failure(
                        "unsafe-entry", entry.FullName + " (" + unsafeReason + ")");
                }

                if (!seenNames.Add(normalized))
                {
                    // Windows 上大小写不敏感，重复名会互相覆盖。
                    return SafeZipExtractionResult.Failure("duplicate-entry", normalized);
                }

                if (IsSymbolicLinkOrReparsePoint(entry))
                {
                    return SafeZipExtractionResult.Failure(
                        "unsafe-entry", normalized + " (symbolic link / reparse point)");
                }

                // 目录条目：安全性已校验，不参与规则匹配。
                if (normalized.EndsWith("/", StringComparison.Ordinal))
                    continue;

                long length = entry.Length;
                if (length < 0)
                    return SafeZipExtractionResult.Failure("entry-length-invalid", normalized);

                if (maxEntryBytes > 0 && length > maxEntryBytes)
                {
                    return SafeZipExtractionResult.Failure(
                        "entry-too-large",
                        normalized + " (" + length + " > " + maxEntryBytes + ")");
                }

                if (maxTotalBytes > 0)
                {
                    declaredTotal += length;
                    if (declaredTotal > maxTotalBytes)
                    {
                        return SafeZipExtractionResult.Failure(
                            "archive-too-large", declaredTotal + " > " + maxTotalBytes);
                    }
                }

                string fileName = GetFileNameSegment(normalized);
                for (int r = 0; r < rules.Count; r++)
                {
                    if (!string.Equals(rules[r].MatchFileName, fileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    matchedCount[r]++;
                    if (matchedCount[r] > 1)
                    {
                        return SafeZipExtractionResult.Failure(
                            "ambiguous-rule-match",
                            rules[r].MatchFileName + " matched more than one archive entry");
                    }
                    matchedEntry[r] = entry;
                }
            }

            for (int r = 0; r < rules.Count; r++)
            {
                if (rules[r].Required && matchedCount[r] == 0)
                {
                    return SafeZipExtractionResult.Failure(
                        "required-entry-missing", rules[r].MatchFileName);
                }
            }

            // ---- 阶段 2：只写出白名单匹配到的条目。----
            var extracted = new List<SafeZipExtractedFile>();
            long actualTotal = 0;

            for (int r = 0; r < rules.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ZipArchiveEntry entry = matchedEntry[r];
                if (entry == null)
                    continue;

                SafeZipEntryRule rule = rules[r];

                string targetPath;
                if (!FfmpegInstallLayout.TryResolveRelative(destinationFull, rule.TargetRelativePath, out targetPath))
                {
                    return SafeZipExtractionResult.Failure(
                        "target-path-unsafe", rule.TargetRelativePath);
                }

                // 纵深防御：写出前再确认一次目标仍在目标目录内。
                if (!FfmpegInstallLayout.IsUnder(destinationFull, targetPath))
                {
                    return SafeZipExtractionResult.Failure(
                        "target-path-escapes-destination", rule.TargetRelativePath);
                }

                try
                {
                    string parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);
                }
                catch (Exception ex)
                {
                    return SafeZipExtractionResult.Failure("target-directory-failed", ex.Message);
                }

                string actualSha256;
                long writtenBytes;
                try
                {
                    using (Stream source = entry.Open())
                    using (var target = new FileStream(
                               targetPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize))
                    using (var sha = SHA256.Create())
                    {
                        writtenBytes = CopyAndHash(source, target, sha, maxEntryBytes,
                            maxTotalBytes, actualTotal, cancellationToken);
                        actualSha256 = FfmpegFileHash.ToHex(sha.Hash);
                    }
                }
                catch (OperationCanceledException)
                {
                    return SafeZipExtractionResult.Failure("cancelled", rule.TargetRelativePath);
                }
                catch (Exception ex)
                {
                    return SafeZipExtractionResult.Failure("extract-failed", ex.Message);
                }

                if (rule.ExpectedSha256 != null && !FfmpegFileHash.Matches(rule.ExpectedSha256, actualSha256))
                {
                    return SafeZipExtractionResult.Failure(
                        "hash-mismatch",
                        rule.TargetRelativePath + " expected=" + rule.ExpectedSha256 + " actual=" + actualSha256);
                }

                actualTotal += writtenBytes;

                extracted.Add(new SafeZipExtractedFile
                {
                    RelativePath = rule.TargetRelativePath,
                    FullPath = targetPath,
                    Sha256 = actualSha256,
                    SizeBytes = writtenBytes,
                });
            }

            return new SafeZipExtractionResult
            {
                Success = true,
                Files = extracted,
            };
        }

        /// <summary>
        /// 复制流并同时计算哈希。归档声明的 <c>entry.Length</c> 可能被伪造，
        /// 因此运行期同时限制当前条目和跨条目的真实写出总量。
        /// </summary>
        private static long CopyAndHash(
            Stream source, Stream target, HashAlgorithm sha, long maxEntryBytes,
            long maxTotalBytes, long previousBytes, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[CopyBufferSize];
            long total = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = source.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                total += read;
                if (maxEntryBytes > 0 && total > maxEntryBytes)
                    throw new InvalidDataException("entry exceeded declared maximum while extracting");
                if (maxTotalBytes > 0 && total > maxTotalBytes - previousBytes)
                    throw new InvalidDataException("archive exceeded actual extraction maximum");

                target.Write(buffer, 0, read);
                sha.TransformBlock(buffer, 0, read, null, 0);
            }

            sha.TransformFinalBlock(new byte[0], 0, 0);
            return total;
        }

        /// <summary>
        /// 规范化归档条目名并做安全校验。
        /// 返回 false 表示该条目不安全，整份归档都必须被拒绝。
        /// </summary>
        private static bool TryNormalizeEntryName(string rawName, out string normalized, out string unsafeReason)
        {
            normalized = null;
            unsafeReason = null;

            if (string.IsNullOrEmpty(rawName))
            {
                unsafeReason = "empty-name";
                return false;
            }

            string name = rawName.Replace('\\', '/');

            // 去掉前导 "./"（合法但无意义）。
            while (name.StartsWith("./", StringComparison.Ordinal))
                name = name.Substring(2);

            if (name.Length == 0)
            {
                unsafeReason = "empty-name";
                return false;
            }

            if (name[0] == '/')
            {
                unsafeReason = "rooted-path";
                return false;
            }

            if (name.Length >= 2 && name[1] == ':')
            {
                unsafeReason = "drive-qualified-path";
                return false;
            }

            bool isDirectory = name.EndsWith("/", StringComparison.Ordinal);
            string[] segments = name.Split('/');
            char[] invalidChars = Path.GetInvalidFileNameChars();

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];

                // 末段在目录条目中为空，属正常。
                if (segment.Length == 0 && isDirectory && i == segments.Length - 1)
                    continue;

                if (segment.Length == 0)
                {
                    unsafeReason = "empty-segment";
                    return false;
                }

                if (segment == "." || segment == "..")
                {
                    unsafeReason = "relative-segment";
                    return false;
                }

                if (segment.IndexOfAny(invalidChars) >= 0)
                {
                    unsafeReason = "invalid-character";
                    return false;
                }
            }

            normalized = name;
            return true;
        }

        private static string GetFileNameSegment(string normalizedName)
        {
            int slash = normalizedName.LastIndexOf('/');
            return slash >= 0 ? normalizedName.Substring(slash + 1) : normalizedName;
        }

        /// <summary>
        /// 检测符号链接 / 重解析点。Unix 模式位于外部属性的高 16 位（S_IFLNK = 0xA000），
        /// Windows 的 reparse point 标志位于低位 FILE_ATTRIBUTE_REPARSE_POINT (0x400)。
        /// 我们只写出普通文件，因此这类条目一律拒绝。
        /// </summary>
        private static bool IsSymbolicLinkOrReparsePoint(ZipArchiveEntry entry)
        {
            try
            {
                int external = entry.ExternalAttributes;
                int unixFileType = (external >> 16) & 0xF000;
                if (unixFileType == 0xA000)
                    return true;

                const int fileAttributeReparsePoint = 0x400;
                if ((external & fileAttributeReparsePoint) != 0)
                    return true;
            }
            catch
            {
                // 读取外部属性失败时按"未知"处理：不因此拒绝整份归档，
                // 因为写出路径与内容哈希仍受白名单与校验保护。
            }
            return false;
        }
    }
}
