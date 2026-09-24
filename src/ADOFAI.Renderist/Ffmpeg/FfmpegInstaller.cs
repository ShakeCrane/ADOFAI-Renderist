using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ADOFAI.Renderist.Ffmpeg
{
    internal enum FfmpegInstallOutcome
    {
        Installed = 0,
        AlreadyInstalled = 1,
        Failed = 2,
        Cancelled = 3,
    }

    internal sealed class FfmpegInstallRequest
    {
        /// <summary>要安装的资产。null = <see cref="FfmpegAssetManifest.Primary"/>。</summary>
        public FfmpegAsset Asset { get; set; }

        /// <summary>托管安装布局（必填）。</summary>
        public FfmpegInstallLayout Layout { get; set; }

        /// <summary>
        /// 本地归档文件路径。
        ///
        /// 本阶段**不实现下载**：下载方案必须先在目标 Unity Mono 环境收敛并验证。
        /// 因此安装入口只接受"已经在本地的归档"，其余步骤（哈希校验、安全解压、
        /// 能力检查、原子发布）已完整实现且可离线回归。
        /// </summary>
        public string ArchivePath { get; set; }

        /// <summary>安装完成后是否对托管副本做一次能力探测。</summary>
        public bool ProbeAfterInstall { get; set; }

        public int ProbeTimeoutSeconds { get; set; }
    }

    internal sealed class FfmpegInstallResult
    {
        public FfmpegInstallOutcome Outcome { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }
        public string AssetId { get; set; }
        public string Version { get; set; }

        /// <summary>发布后的安装目录（仅在成功时有意义）。</summary>
        public string TargetDirectory { get; set; }

        public IReadOnlyList<SafeZipExtractedFile> Files { get; set; }

        public FfmpegCapabilityReport Capability { get; set; }

        /// <summary>本次安装开始时被清理的孤儿 staging 目录（诊断用）。</summary>
        public IReadOnlyList<string> OrphanStagingRemoved { get; set; }

        /// <summary>清理自身 staging / 孤儿目录时遇到的失败（诊断用，不改变结果）。</summary>
        public IReadOnlyList<string> CleanupWarnings { get; set; }

        public long ArchiveSizeBytesObserved { get; set; }
    }

    /// <summary>
    /// 托管安装编排（唯一单点）。
    ///
    /// 步骤：取锁 → 清理自身孤儿 staging → 检查既有安装（绝不覆盖）→ 校验归档哈希 →
    /// 安全解压到同卷 staging → 校验提取文件 → 可选能力探测 → 原子发布版本目录。
    ///
    /// 安全不变量：
    ///   * 任何失败或取消都只删除**本次调用自己创建**的 staging 目录；
    ///   * 已存在的安装目录（无论是否有效）**绝不覆盖、绝不删除**；
    ///   * 孤儿清理只删除带有 Renderist ownership 标记且位于自身 StagingRoot 下的目录。
    ///
    /// 不接触 Unity / UMM / Harmony。
    /// </summary>
    internal static class FfmpegInstaller
    {
        /// <summary>
        /// 解压总量的结构性上限倍数（相对归档压缩体积）。
        ///
        /// 这是防止恶意 / 损坏归档造成磁盘耗尽的**资源安全边界**，不是产品性能参数上限：
        /// 它不限制任何导出几何、帧率或编码参数。实际内容仍由钉死的 SHA-256 决定，
        /// 因此该边界不影响合法资产。
        /// </summary>
        private const int ExtractionBoundMultiplier = 8;

        public static FfmpegInstallResult Install(FfmpegInstallRequest request, CancellationToken cancellationToken)
        {
            var result = new FfmpegInstallResult
            {
                Outcome = FfmpegInstallOutcome.Failed,
                Files = new SafeZipExtractedFile[0],
                OrphanStagingRemoved = new string[0],
                CleanupWarnings = new string[0],
            };

            var cleanupWarnings = new List<string>();
            result.CleanupWarnings = cleanupWarnings;

            if (request == null)
            {
                result.ErrorCode = "request-null";
                return result;
            }

            if (request.Layout == null)
            {
                result.ErrorCode = "install-root-not-configured";
                return result;
            }

            FfmpegAsset asset = request.Asset ?? FfmpegAssetManifest.Primary;
            result.AssetId = asset.Id;
            result.Version = asset.Version;

            IReadOnlyList<string> manifestProblems = FfmpegAssetManifest.Validate();
            if (manifestProblems.Count > 0)
            {
                result.ErrorCode = "manifest-invalid";
                result.ErrorDetail = string.Join("; ", ToArray(manifestProblems));
                return result;
            }

            if (string.IsNullOrWhiteSpace(request.ArchivePath))
            {
                result.ErrorCode = "archive-path-empty";
                return result;
            }

            string archivePath;
            try
            {
                archivePath = Path.GetFullPath(request.ArchivePath);
            }
            catch (Exception ex)
            {
                result.ErrorCode = "archive-path-invalid";
                result.ErrorDetail = ex.Message;
                return result;
            }

            if (!File.Exists(archivePath))
            {
                result.ErrorCode = "archive-not-found";
                result.ErrorDetail = archivePath;
                return result;
            }

            FfmpegInstallLock installLock;
            string lockErrorCode;
            string lockErrorDetail;
            if (!FfmpegInstallLock.TryAcquire(
                    request.Layout.LockFilePath, out installLock, out lockErrorCode, out lockErrorDetail))
            {
                result.ErrorCode = lockErrorCode;
                result.ErrorDetail = lockErrorDetail;
                return result;
            }

            using (installLock)
            {
                return InstallUnderLock(request, asset, archivePath, result, cleanupWarnings, cancellationToken);
            }
        }

        private static FfmpegInstallResult InstallUnderLock(
            FfmpegInstallRequest request,
            FfmpegAsset asset,
            string archivePath,
            FfmpegInstallResult result,
            List<string> cleanupWarnings,
            CancellationToken cancellationToken)
        {
            string stagingDirectory = null;

            try
            {
                long archiveSize;
                try
                {
                    archiveSize = new FileInfo(archivePath).Length;
                }
                catch (Exception ex)
                {
                    result.ErrorCode = "archive-stat-failed";
                    result.ErrorDetail = ex.Message;
                    return result;
                }

                result.ArchiveSizeBytesObserved = archiveSize;

                // 清理自身孤儿 staging（必须持锁后执行）。
                result.OrphanStagingRemoved = CleanupOrphanStaging(request.Layout, cleanupWarnings);

                // 既有安装：绝不覆盖。无效目录同样保留，交由用户显式处理。
                FfmpegManagedInstall existing = FfmpegManagedInstallLocator.Locate(request.Layout, asset);
                if (existing.IsValid)
                {
                    result.Outcome = FfmpegInstallOutcome.AlreadyInstalled;
                    result.TargetDirectory = existing.InstallDirectory;
                    return result;
                }

                if (existing.DirectoryPresent)
                {
                    result.ErrorCode = "managed-install-invalid";
                    result.ErrorDetail = existing.InvalidReason ?? "unknown";
                    return result;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 归档完整性：以内容哈希为权威判据。
                string archiveSha256;
                string hashError;
                if (!FfmpegFileHash.TryCompute(archivePath, out archiveSha256, out hashError))
                {
                    result.ErrorCode = "archive-hash-failed";
                    result.ErrorDetail = hashError;
                    return result;
                }

                if (!FfmpegFileHash.Matches(asset.ArchiveSha256, archiveSha256))
                {
                    result.ErrorCode = "archive-hash-mismatch";
                    result.ErrorDetail = "expected=" + asset.ArchiveSha256 + " actual=" + archiveSha256;
                    return result;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 暂存目录：与安装根同卷，便于用目录 Move 原子发布。
                string token = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) +
                               "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                stagingDirectory = request.Layout.StagingDirectory(asset.Id, asset.Version, token);

                if (Directory.Exists(stagingDirectory))
                {
                    result.ErrorCode = "staging-collision";
                    result.ErrorDetail = stagingDirectory;
                    return result;
                }

                Directory.CreateDirectory(stagingDirectory);

                // 先写 ownership 标记：即使后续步骤失败，该目录也可被识别为 Renderist 自己的
                // 暂存产物（供孤儿清理），而不会被误认为外部目录。
                string markerError;
                if (!FfmpegOwnershipMarker.TryWrite(
                        request.Layout.OwnershipMarkerPath(stagingDirectory), asset.Id, asset.ArchiveSha256,
                        DateTime.UtcNow, out markerError))
                {
                    result.ErrorCode = "staging-marker-failed";
                    result.ErrorDetail = markerError;
                    return result;
                }

                var rules = new List<SafeZipEntryRule>();
                for (int i = 0; i < asset.Files.Count; i++)
                {
                    FfmpegAssetFile file = asset.Files[i];
                    rules.Add(new SafeZipEntryRule(
                        file.MatchFileName, file.RelativePath, file.Sha256, file.Required));
                }

                long totalBound = archiveSize > 0 ? archiveSize * ExtractionBoundMultiplier : 0;
                long entryBound = totalBound;

                cancellationToken.ThrowIfCancellationRequested();

                SafeZipExtractionResult extraction = SafeZipExtractor.Extract(
                    archivePath, stagingDirectory, rules, totalBound, entryBound, cancellationToken);

                if (!extraction.Success)
                {
                    // 解压层把取消包装成失败结果（它刻意不向调用方抛异常），
                    // 但取消在语义上不是安装失败：必须如实报告 Cancelled，
                    // 否则调用方会把"用户取消"记成"安装出错"。
                    if (string.Equals(extraction.ErrorCode, "cancelled", StringComparison.Ordinal) ||
                        cancellationToken.IsCancellationRequested)
                    {
                        result.Outcome = FfmpegInstallOutcome.Cancelled;
                        result.ErrorCode = "cancelled";
                        result.ErrorDetail = extraction.ErrorDetail;
                        return result;
                    }

                    result.ErrorCode = extraction.ErrorCode;
                    result.ErrorDetail = extraction.ErrorDetail;
                    return result;
                }

                result.Files = extraction.Files;

                cancellationToken.ThrowIfCancellationRequested();

                if (request.ProbeAfterInstall)
                {
                    string stagingPrimary;
                    if (!FfmpegInstallLayout.TryResolveRelative(
                            stagingDirectory, asset.PrimaryExecutableRelativePath, out stagingPrimary) ||
                        !File.Exists(stagingPrimary))
                    {
                        result.ErrorCode = "probe-executable-missing";
                        return result;
                    }

                    int probeTimeout = request.ProbeTimeoutSeconds > 0
                        ? request.ProbeTimeoutSeconds
                        : FfmpegCapabilityProbe.DefaultTimeoutSeconds;
                    result.Capability = FfmpegCapabilityProbe.Probe(stagingPrimary, probeTimeout, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!result.Capability.IsUsableForMp4)
                    {
                        result.ErrorCode = "capability-probe-failed";
                        result.ErrorDetail = result.Capability.ErrorCode ??
                            string.Join(",", ToArray(result.Capability.MissingCapabilities));
                        return result;
                    }
                }

                // 原子发布：目标目录必须不存在（前面已确认）。
                string versionDirectory = request.Layout.VersionDirectory(asset.Id, asset.Version);
                string assetDirectory = Path.GetDirectoryName(versionDirectory);
                if (!string.IsNullOrEmpty(assetDirectory))
                    Directory.CreateDirectory(assetDirectory);

                if (Directory.Exists(versionDirectory))
                {
                    // 竞态或外部创建：绝不覆盖。
                    result.ErrorCode = "target-appeared";
                    result.ErrorDetail = versionDirectory;
                    return result;
                }

                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stagingDirectory, versionDirectory);
                stagingDirectory = null;
                result.TargetDirectory = versionDirectory;

                // 发布后复核：确认最终安装确实可被识别为有效托管安装。
                FfmpegManagedInstall published = FfmpegManagedInstallLocator.Locate(request.Layout, asset);
                if (!published.IsValid)
                {
                    result.ErrorCode = "published-install-invalid";
                    result.ErrorDetail = published.InvalidReason ?? "unknown";
                    return result;
                }

                result.Outcome = FfmpegInstallOutcome.Installed;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Outcome = FfmpegInstallOutcome.Cancelled;
                result.ErrorCode = "cancelled";
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorCode = "install-failed";
                result.ErrorDetail = ex.Message;
                return result;
            }
            finally
            {
                // 回滚：只删除本次调用自己创建的 staging 目录。
                if (stagingDirectory != null)
                {
                    string cleanupError = TryDeleteOwnStaging(stagingDirectory);
                    if (cleanupError != null)
                        cleanupWarnings.Add(cleanupError);
                }
            }
        }

        /// <summary>
        /// 清理自身孤儿 staging。
        ///
        /// 只删除**同时满足**以下条件的目录：
        ///   * 直接位于本布局的 StagingRoot 之下；
        ///   * 含可解析的 Renderist ownership 标记（schema 合法、assetId 非空）。
        /// 任何不含我们标记的目录都会被保留 —— 绝不递归误删外部文件。
        ///
        /// 刻意不要求标记里的 assetId 仍在当前 manifest 中：资产从清单移除后，
        /// 它的历史孤儿目录仍应可被清理。
        /// </summary>
        private static IReadOnlyList<string> CleanupOrphanStaging(
            FfmpegInstallLayout layout, List<string> warnings)
        {
            var removed = new List<string>();

            try
            {
                if (!Directory.Exists(layout.StagingRoot))
                    return removed;

                string[] candidates = Directory.GetDirectories(layout.StagingRoot);
                for (int i = 0; i < candidates.Length; i++)
                {
                    string candidate = candidates[i];

                    // 纵深防御：确认仍在 StagingRoot 之内。
                    if (!FfmpegInstallLayout.IsUnder(layout.StagingRoot, candidate))
                        continue;

                    FfmpegOwnershipMarker marker;
                    string markerError;
                    if (!FfmpegOwnershipMarker.TryRead(
                            layout.OwnershipMarkerPath(candidate), out marker, out markerError))
                    {
                        // 没有我们的标记：不是我们的产物，保留。
                        continue;
                    }

                    string deleteError = TryDeleteOwnStaging(candidate);
                    if (deleteError == null)
                        removed.Add(candidate);
                    else
                        warnings.Add(deleteError);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("orphan-staging-scan-failed: " + ex.Message);
            }

            return removed;
        }

        /// <summary>删除一个已确认属于 Renderist 的 staging 目录。返回 null = 成功。</summary>
        private static string TryDeleteOwnStaging(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
                return null;
            }
            catch (Exception ex)
            {
                return "staging-cleanup-failed: " + directory + " (" + ex.Message + ")";
            }
        }

        private static string[] ToArray(IReadOnlyList<string> values)
        {
            var array = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
                array[i] = values[i];
            return array;
        }
    }
}
