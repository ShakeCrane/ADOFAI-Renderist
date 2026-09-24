using System;
using System.Collections.Generic;
using System.Globalization;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 一个需要从 FFmpeg 归档中提取到托管安装目录的文件。
    ///
    /// 匹配规则刻意基于**文件名**（最后一个路径段，大小写不敏感）而不是完整归档路径：
    /// 归档内的顶层目录名随后续构建可能变化，而内容哈希才是真正的完整性锚点。
    /// </summary>
    internal sealed class FfmpegAssetFile
    {
        public FfmpegAssetFile(string matchFileName, string relativePath, string sha256, bool required)
        {
            MatchFileName = matchFileName;
            RelativePath = relativePath;
            Sha256 = sha256;
            Required = required;
        }

        /// <summary>归档内需要匹配的文件名（最后一个路径段，大小写不敏感精确相等）。</summary>
        public string MatchFileName { get; private set; }

        /// <summary>提取到托管安装目录内的相对路径（'/' 分隔）。</summary>
        public string RelativePath { get; private set; }

        /// <summary>期望的 SHA-256（小写十六进制）。null = 只提取、不校验内容哈希。</summary>
        public string Sha256 { get; private set; }

        /// <summary>true = 归档中必须存在且必须匹配；false = 存在则提取，缺失不视为失败。</summary>
        public bool Required { get; private set; }
    }

    /// <summary>
    /// 一份固定的、已审核的 FFmpeg 发行资产。
    ///
    /// 硬约束：
    ///   * 只使用固定版本地址，**禁止**可变 `latest` 地址；
    ///   * 归档与其中的可执行文件都必须有固定 SHA-256；
    ///   * 资产只描述"从哪里取、取到什么"，不代表已经安装。
    /// </summary>
    internal sealed class FfmpegAsset
    {
        public FfmpegAsset(
            string id,
            string version,
            string displayName,
            string archiveUrl,
            string archiveSha256,
            long archiveSizeBytes,
            string licenseName,
            string licenseUrl,
            string sourceCodeUrl,
            string sourceNote,
            string primaryExecutableRelativePath,
            IReadOnlyList<FfmpegAssetFile> files)
        {
            Id = id;
            Version = version;
            DisplayName = displayName;
            ArchiveUrl = archiveUrl;
            ArchiveSha256 = archiveSha256;
            ArchiveSizeBytes = archiveSizeBytes;
            LicenseName = licenseName;
            LicenseUrl = licenseUrl;
            SourceCodeUrl = sourceCodeUrl;
            SourceNote = sourceNote;
            PrimaryExecutableRelativePath = primaryExecutableRelativePath;
            Files = files ?? new FfmpegAssetFile[0];
        }

        /// <summary>稳定标识，同时用作托管安装目录名的一部分。</summary>
        public string Id { get; private set; }

        /// <summary>发行版本号（例如 "9.0.2"）。</summary>
        public string Version { get; private set; }

        public string DisplayName { get; private set; }

        /// <summary>固定 HTTPS 归档地址。禁止可变 `latest`。</summary>
        public string ArchiveUrl { get; private set; }

        /// <summary>归档文件的期望 SHA-256（小写十六进制）。</summary>
        public string ArchiveSha256 { get; private set; }

        /// <summary>归档期望字节数；只用于下载完整性与解压合理性判定，不是产品级体积上限。</summary>
        public long ArchiveSizeBytes { get; private set; }

        public string LicenseName { get; private set; }

        public string LicenseUrl { get; private set; }

        /// <summary>
        /// 该构建对应的上游源码入口（GPL 告知义务用）。
        /// 与 <see cref="SourceNote"/> 一起构成"构建方 / 版本 / 来源 / 源码"的展示信息。
        /// </summary>
        public string SourceCodeUrl { get; private set; }

        /// <summary>来源说明（构建页 / 发布页），用于向用户如实展示信任边界。</summary>
        public string SourceNote { get; private set; }

        /// <summary>托管安装中的主可执行文件相对路径（'/' 分隔）。</summary>
        public string PrimaryExecutableRelativePath { get; private set; }

        public IReadOnlyList<FfmpegAssetFile> Files { get; private set; }
    }

    /// <summary>
    /// 已审核 FFmpeg 资产的唯一来源。
    ///
    /// 这里的每一个数值都来自 PROJECT_UNDERSTANDING.md §2.3 记录的实际归档核对，
    /// 不是估计值。未核对过的资产**不得**加入本清单。
    ///
    /// 重要：清单只覆盖**可安装资产**。FFmpeg 本身不进入 Renderist 发布 ZIP；
    /// 它由用户主动触发后下载到用户自己的托管目录。
    /// </summary>
    internal static class FfmpegAssetManifest
    {
        /// <summary>当前唯一的可安装资产：Gyan.dev 9.0.2 Essentials（Windows x64 静态构建）。</summary>
        public const string Gyan902EssentialsId = "gyan-9.0.2-essentials";

        private const string Gyan902ArchiveUrl =
            "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";

        private const string Gyan902ArchiveSha256 =
            "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";

        // 归档字节数来自 §2.3 记录的中断下载实测（已取得 155363 / 114768076 字节）。
        private const long Gyan902ArchiveSizeBytes = 114768076L;

        private const string Gyan902FfmpegSha256 =
            "3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec";

        private const string Gyan902FfprobeSha256 =
            "f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52";

        private static readonly FfmpegAsset[] Assets = new[]
        {
            new FfmpegAsset(
                Gyan902EssentialsId,
                "9.0.2",
                "Gyan.dev FFmpeg 9.0.2 Essentials（Windows x64，静态，GPLv3）",
                Gyan902ArchiveUrl,
                Gyan902ArchiveSha256,
                Gyan902ArchiveSizeBytes,
                "GPLv3",
                "https://ffmpeg.org/legal.html",
                "https://github.com/FFmpeg/FFmpeg/commit/946fcce07b",
                "Gyan.dev 构建页 https://www.gyan.dev/ffmpeg/builds/ 与 GitHub 发布 " +
                "https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2 两处公布的 9.0.2 SHA-256 " +
                "与实际下载一致。该来源对应 FFmpeg 源码提交 946fcce07b。" +
                "归档内的可执行文件**没有** Authenticode 签名：固定哈希可以发现下载截断或资产变化，" +
                "但校验值与包由同一构建方发布，不等同于独立代码签名或第三方审计。",
                "bin/ffmpeg.exe",
                new[]
                {
                    new FfmpegAssetFile("ffmpeg.exe", "bin/ffmpeg.exe", Gyan902FfmpegSha256, true),
                    new FfmpegAssetFile("ffprobe.exe", "bin/ffprobe.exe", Gyan902FfprobeSha256, true),
                }),
        };

        /// <summary>一键安装首选的资产。</summary>
        public static FfmpegAsset Primary
        {
            get { return Assets[0]; }
        }

        public static IReadOnlyList<FfmpegAsset> All
        {
            get { return Assets; }
        }

        public static bool TryGet(string assetId, out FfmpegAsset asset)
        {
            asset = null;
            if (string.IsNullOrEmpty(assetId))
                return false;

            for (int i = 0; i < Assets.Length; i++)
            {
                if (string.Equals(Assets[i].Id, assetId, StringComparison.Ordinal))
                {
                    asset = Assets[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 校验清单自身的结构不变量。返回问题列表（空 = 合法）。
        /// 清单非法时安装必须 fail-closed，绝不使用未经审核的地址或哈希。
        /// </summary>
        public static IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();

            if (Assets.Length == 0)
                problems.Add("manifest has no assets");

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Assets.Length; i++)
            {
                FfmpegAsset asset = Assets[i];
                string label = string.IsNullOrEmpty(asset.Id)
                    ? ("#" + i.ToString(CultureInfo.InvariantCulture))
                    : asset.Id;

                if (string.IsNullOrEmpty(asset.Id))
                    problems.Add(label + ": missing id");
                else if (!seenIds.Add(asset.Id))
                    problems.Add(label + ": duplicate id");

                if (string.IsNullOrEmpty(asset.Version))
                    problems.Add(label + ": missing version");

                ValidateArchiveUrl(asset, label, problems);
                ValidateSha256(asset.ArchiveSha256, label + ": archiveSha256", problems);

                if (asset.ArchiveSizeBytes <= 0)
                    problems.Add(label + ": archiveSizeBytes must be positive");

                if (string.IsNullOrEmpty(asset.LicenseName))
                    problems.Add(label + ": missing license name");

                if (string.IsNullOrEmpty(asset.PrimaryExecutableRelativePath))
                    problems.Add(label + ": missing primary executable path");

                ValidateFiles(asset, label, problems);
            }

            return problems;
        }

        private static void ValidateArchiveUrl(FfmpegAsset asset, string label, List<string> problems)
        {
            Uri uri;
            if (string.IsNullOrEmpty(asset.ArchiveUrl) ||
                !Uri.TryCreate(asset.ArchiveUrl, UriKind.Absolute, out uri))
            {
                problems.Add(label + ": archive url is not an absolute uri");
                return;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                problems.Add(label + ": archive url must use https");

            // 可变地址会让哈希固定失去意义：资产可能被静默替换。
            if (asset.ArchiveUrl.IndexOf("latest", StringComparison.OrdinalIgnoreCase) >= 0)
                problems.Add(label + ": archive url must not be a mutable 'latest' address");
        }

        private static void ValidateFiles(FfmpegAsset asset, string label, List<string> problems)
        {
            if (asset.Files.Count == 0)
            {
                problems.Add(label + ": no files declared");
                return;
            }

            bool primaryDeclared = false;
            var seenRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int requiredCount = 0;

            for (int i = 0; i < asset.Files.Count; i++)
            {
                FfmpegAssetFile file = asset.Files[i];
                string fileLabel = label + ": file #" + i.ToString(CultureInfo.InvariantCulture);

                if (string.IsNullOrEmpty(file.MatchFileName) ||
                    file.MatchFileName.IndexOf('/') >= 0 ||
                    file.MatchFileName.IndexOf('\\') >= 0)
                {
                    problems.Add(fileLabel + ": match file name must be a bare file name");
                }
                else if (!seenMatch.Add(file.MatchFileName))
                {
                    // 同一文件名匹配两条规则会产生歧义，必须拒绝。
                    problems.Add(fileLabel + ": duplicate match file name '" + file.MatchFileName + "'");
                }

                if (!IsSafeRelativePath(file.RelativePath))
                    problems.Add(fileLabel + ": relative path must be a safe relative path");
                else if (!seenRelative.Add(file.RelativePath))
                    problems.Add(fileLabel + ": duplicate relative path '" + file.RelativePath + "'");

                if (file.Sha256 != null)
                    ValidateSha256(file.Sha256, fileLabel + ": sha256", problems);

                if (file.Required)
                    requiredCount++;

                if (string.Equals(file.RelativePath, asset.PrimaryExecutableRelativePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    primaryDeclared = true;
                }
            }

            if (requiredCount == 0)
                problems.Add(label + ": no required file declared");

            if (!primaryDeclared)
                problems.Add(label + ": primary executable path is not one of the declared files");
        }

        private static void ValidateSha256(string value, string label, List<string> problems)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                problems.Add(label + ": must be a 64 character sha-256 hex string");
                return;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex)
                {
                    problems.Add(label + ": must be lowercase hexadecimal");
                    return;
                }
            }
        }

        /// <summary>
        /// 相对路径必须是安全的：非空、非绝对、无 '..' 段、无盘符、无反向斜杠。
        /// 这是解压目标路径的安全前提。
        /// </summary>
        public static bool IsSafeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            if (path.IndexOf('\\') >= 0)
                return false;
            if (path[0] == '/')
                return false;
            if (path.Length >= 2 && path[1] == ':')
                return false;

            string[] segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment.Length == 0)
                    return false;
                if (segment == "." || segment == "..")
                    return false;
            }
            return true;
        }
    }
}
