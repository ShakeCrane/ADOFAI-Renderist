using System;
using System.Collections.Generic;
using System.IO;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>FFmpeg 的发现来源。顺序即优先级。</summary>
    internal enum FfmpegCandidateSource
    {
        None = 0,

        /// <summary>用户在设置中明确指定的可执行文件路径。</summary>
        ExplicitPath = 1,

        /// <summary>Renderist 托管安装目录。</summary>
        ManagedInstall = 2,

        /// <summary>系统 PATH。</summary>
        SystemPath = 3,
    }

    /// <summary>
    /// 被冻结的二进制身份：绝对路径 + 内容哈希 + 字节数。
    ///
    /// 只记录路径是不够的 —— PATH 顺序会改变被发现的版本，因此任何"当前使用的 FFmpeg"
    /// 都必须能回答"到底是哪一个二进制"。
    /// </summary>
    internal sealed class FfmpegBinaryIdentity
    {
        public string AbsolutePath { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
    }

    internal sealed class FfmpegCandidate
    {
        public FfmpegCandidateSource Source { get; set; }
        public FfmpegBinaryIdentity Identity { get; set; }

        /// <summary>来源为托管安装时的安装信息。</summary>
        public FfmpegManagedInstall ManagedInstall { get; set; }

        /// <summary>来源为 PATH 时命中的 PATH 条目序号（从 0 开始），用于诊断。</summary>
        public int PathEntryIndex { get; set; } = -1;

        public string Note { get; set; }
    }

    internal sealed class FfmpegDiscoveryRequest
    {
        /// <summary>用户显式指定路径（来自 Settings）。空 = 未指定。</summary>
        public string ExplicitPath { get; set; }

        /// <summary>托管安装布局。null = 未配置托管目录，跳过该来源。</summary>
        public FfmpegInstallLayout Layout { get; set; }

        /// <summary>参与托管安装发现的资产。null = 使用 <see cref="FfmpegAssetManifest.All"/>。</summary>
        public IReadOnlyList<FfmpegAsset> Assets { get; set; }

        /// <summary>PATH 内容。null = 读取当前进程环境变量（测试可注入）。</summary>
        public string PathEnvironment { get; set; }
    }

    internal sealed class FfmpegDiscoveryResult
    {
        public FfmpegCandidateSource Source { get; set; }

        /// <summary>命中的候选；未找到时为 null。</summary>
        public FfmpegCandidate Candidate { get; set; }

        /// <summary>失败原因码（机读）。空 = 未失败（可能是 None）。</summary>
        public string ErrorCode { get; set; }

        public string ErrorDetail { get; set; }

        /// <summary>PATH 中实际被检查的目录数（诊断用）。</summary>
        public int PathEntriesScanned { get; set; }

        public bool Found
        {
            get { return Candidate != null && Candidate.Identity != null; }
        }
    }

    /// <summary>
    /// 候选发现（唯一单点）：显式路径 → 托管安装 → 系统 PATH → 无。
    ///
    /// 关键语义：
    ///   * 用户显式指定的路径如果失效，**必须报错**，不得静默改用托管安装或 PATH ——
    ///     否则用户以为在使用自己的 FFmpeg，实际却在用别的二进制。
    ///   * 发现只读，不创建目录、不修改 PATH、不下载任何东西。
    ///   * 不接触 Unity / UMM / Harmony。
    /// </summary>
    internal static class FfmpegDiscovery
    {
        public const string ExecutableFileName = "ffmpeg.exe";

        public static FfmpegDiscoveryResult Discover(FfmpegDiscoveryRequest request)
        {
            var result = new FfmpegDiscoveryResult();
            if (request == null)
            {
                result.ErrorCode = "request-null";
                return result;
            }

            // 1) 显式路径：优先级最高，且失败即终止（不静默回退）。
            if (!string.IsNullOrWhiteSpace(request.ExplicitPath))
            {
                result.Source = FfmpegCandidateSource.ExplicitPath;

                FfmpegCandidate explicitCandidate;
                string explicitError;
                if (!TryBuildCandidate(request.ExplicitPath.Trim(), FfmpegCandidateSource.ExplicitPath,
                        out explicitCandidate, out explicitError))
                {
                    result.ErrorCode = "explicit-path-invalid";
                    result.ErrorDetail = explicitError;
                    return result;
                }

                result.Candidate = explicitCandidate;
                return result;
            }

            // 2) 托管安装。
            IReadOnlyList<FfmpegAsset> assets = request.Assets ?? FfmpegAssetManifest.All;
            if (request.Layout != null)
            {
                for (int i = 0; i < assets.Count; i++)
                {
                    FfmpegManagedInstall install = FfmpegManagedInstallLocator.Locate(request.Layout, assets[i]);
                    if (!install.IsValid)
                        continue;

                    FfmpegCandidate managedCandidate;
                    string managedError;
                    if (!TryBuildCandidate(install.PrimaryExecutablePath, FfmpegCandidateSource.ManagedInstall,
                            out managedCandidate, out managedError))
                    {
                        continue;
                    }

                    managedCandidate.ManagedInstall = install;
                    managedCandidate.Note = assets[i].DisplayName;
                    result.Source = FfmpegCandidateSource.ManagedInstall;
                    result.Candidate = managedCandidate;
                    return result;
                }
            }

            // 3) 系统 PATH。
            string pathValue = request.PathEnvironment ?? SafeReadPathEnvironment();
            FfmpegCandidate pathCandidate = SearchPath(pathValue, result);
            if (pathCandidate != null)
            {
                result.Source = FfmpegCandidateSource.SystemPath;
                result.Candidate = pathCandidate;
                return result;
            }

            result.Source = FfmpegCandidateSource.None;
            result.ErrorCode = "ffmpeg-not-found";
            return result;
        }

        private static string SafeReadPathEnvironment()
        {
            try
            {
                return Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 按 PATH 顺序查找第一个可用的 ffmpeg.exe。
        /// 顺序敏感：命中位置记入 <see cref="FfmpegCandidate.PathEntryIndex"/> 供展示。
        /// </summary>
        private static FfmpegCandidate SearchPath(string pathValue, FfmpegDiscoveryResult result)
        {
            if (string.IsNullOrEmpty(pathValue))
                return null;

            string[] entries = pathValue.Split(';');
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i];
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                string directory;
                try
                {
                    directory = entry.Trim().Trim('"');
                    if (!Directory.Exists(directory))
                        continue;
                    directory = Path.GetFullPath(directory);
                }
                catch
                {
                    continue;
                }

                if (!seen.Add(directory))
                    continue;

                result.PathEntriesScanned++;

                string candidatePath = Path.Combine(directory, ExecutableFileName);
                if (!File.Exists(candidatePath))
                    continue;

                FfmpegCandidate candidate;
                string error;
                if (!TryBuildCandidate(candidatePath, FfmpegCandidateSource.SystemPath, out candidate, out error))
                {
                    // 该条目不可用（不可读 / 哈希失败）：继续按 PATH 顺序尝试下一个。
                    continue;
                }

                candidate.PathEntryIndex = i;
                return candidate;
            }

            return null;
        }

        private static bool TryBuildCandidate(
            string path, FfmpegCandidateSource source, out FfmpegCandidate candidate, out string error)
        {
            candidate = null;
            error = null;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                error = "path-invalid: " + ex.Message;
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = "file-not-found: " + fullPath;
                return false;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "not-an-executable: " + fullPath;
                return false;
            }

            string sha256;
            string hashError;
            if (!FfmpegFileHash.TryCompute(fullPath, out sha256, out hashError))
            {
                error = "identity-unavailable: " + hashError;
                return false;
            }

            long size;
            try
            {
                size = new FileInfo(fullPath).Length;
            }
            catch (Exception ex)
            {
                error = "size-unavailable: " + ex.Message;
                return false;
            }

            candidate = new FfmpegCandidate
            {
                Source = source,
                Identity = new FfmpegBinaryIdentity
                {
                    AbsolutePath = fullPath,
                    Sha256 = sha256,
                    SizeBytes = size,
                },
            };
            return true;
        }
    }
}
