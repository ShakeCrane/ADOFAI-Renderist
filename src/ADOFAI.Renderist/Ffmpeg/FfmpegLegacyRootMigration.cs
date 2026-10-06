using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>旧托管根一次性迁移的结果分类。</summary>
    internal enum FfmpegLegacyMigrationOutcome
    {
        /// <summary>新托管根已经存在该资产的目标目录（有效，或按 fail-closed 处理）：无需迁移。</summary>
        NotNeeded = 0,

        /// <summary>旧托管根不存在、不属于 Renderist 或未通过核验：不迁移、不删除。</summary>
        Skipped = 1,

        /// <summary>迁移成功：新托管根出现一份可被 Locator 判为有效的安装。</summary>
        Migrated = 2,

        /// <summary>新托管根存在目标目录但无效：fail-closed，不覆盖、不回退、不执行旧二进制。</summary>
        Blocked = 3,

        /// <summary>迁移失败：旧安装原样保留，最终目标目录不得留下半成品。</summary>
        Failed = 4,

        /// <summary>迁移被取消：只清理本次调用自己创建的 staging。</summary>
        Cancelled = 5,
    }

    /// <summary>迁移结果的机读原因码（唯一单点，测试与日志都引用这里）。</summary>
    internal static class FfmpegLegacyMigrationReason
    {
        internal const string RequestNull = "request-null";
        internal const string InstallRootUnavailable = "install-root-unavailable";
        internal const string LegacyRootUnavailable = "legacy-root-unavailable";
        internal const string LegacyRootEqualsTarget = "legacy-root-equals-target";
        internal const string ManifestInvalid = "manifest-invalid";

        // 新托管根优先。
        internal const string TargetPresent = "new-target-present";
        internal const string TargetUnowned = "new-install-unowned";

        // 旧托管根不可迁移。
        internal const string LegacyAbsent = "legacy-absent";
        internal const string LegacyUnowned = "legacy-unowned";
        internal const string LegacyMarkerUnreadable = "legacy-marker-unreadable";
        internal const string LegacyAssetMismatch = "legacy-asset-mismatch";
        internal const string LegacyArchiveMismatch = "legacy-archive-mismatch";
        internal const string LegacyInstallInvalid = "legacy-install-invalid";

        // 迁移执行。
        internal const string Migrated = "migrated";
        internal const string StagingCollision = "staging-collision";
        internal const string StagingMarkerFailed = "staging-marker-failed";
        internal const string InstallFilePathUnsafe = "install-file-path-unsafe";
        internal const string LegacyFileMissing = "legacy-file-missing";
        internal const string StagedHashFailed = "staged-hash-failed";
        internal const string StagedHashMismatch = "staged-hash-mismatch";
        internal const string ProbeExecutableMissing = "probe-executable-missing";
        internal const string CapabilityProbeFailed = "capability-probe-failed";
        internal const string TargetAppeared = "target-appeared";
        internal const string PublishedInstallInvalid = "published-install-invalid";
        internal const string MigrationFailed = "migration-failed";
        internal const string Cancelled = "cancelled";
    }

    internal sealed class FfmpegLegacyMigrationRequest
    {
        /// <summary>新（当前）托管安装布局。必填。</summary>
        public FfmpegInstallLayout TargetLayout { get; set; }

        /// <summary>旧（<c>Application.persistentDataPath</c> 下）托管安装布局。必填。</summary>
        public FfmpegInstallLayout LegacyLayout { get; set; }

        /// <summary>要迁移的资产。null = <see cref="FfmpegAssetManifest.Primary"/>。</summary>
        public FfmpegAsset Asset { get; set; }

        /// <summary>发布前是否对 staging 中的副本做能力探测。发布前核验的一部分。</summary>
        public bool ProbeCapabilities { get; set; }

        public int ProbeTimeoutSeconds { get; set; }
    }

    internal sealed class FfmpegLegacyMigrationResult
    {
        public FfmpegLegacyMigrationOutcome Outcome { get; set; }

        /// <summary>机读原因码（见 <see cref="FfmpegLegacyMigrationReason"/>）。</summary>
        public string ReasonCode { get; set; }

        public string ReasonDetail { get; set; }

        public string AssetId { get; set; }
        public string Version { get; set; }

        /// <summary>旧托管根中的候选安装目录（始终只读，绝不被本模块修改或删除）。</summary>
        public string LegacyInstallDirectory { get; set; }

        /// <summary>新托管根中的目标安装目录。</summary>
        public string TargetDirectory { get; set; }

        /// <summary>发布前对 staging 副本做能力探测的结果（未探测时为 null）。</summary>
        public FfmpegCapabilityReport Capability { get; set; }

        /// <summary>本次取锁后清理掉的自身孤儿 staging（诊断用）。</summary>
        public IReadOnlyList<string> OrphanStagingRemoved { get; set; }

        /// <summary>清理自身 staging 时的失败（诊断用，不改变结果）。</summary>
        public IReadOnlyList<string> CleanupWarnings { get; set; }
    }

    /// <summary>
    /// 旧托管安装根（0.3.10.0 之前 = <c>Application.persistentDataPath</c>，Windows 实际位于
    /// <c>%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\ADOFAI.Renderist\ffmpeg</c>）
    /// 到当前托管根的**一次性最小安全迁移**。
    ///
    /// 为什么必须迁移而不是继续使用旧路径：旧路径下的可执行文件带继承的 Low Mandatory Level，
    /// 无法向普通完整性级别的输出目录写入（§2.2.9），所以**绝不允许直接执行旧 LocalLow 中的二进制**。
    ///
    /// 状态机（只读判定 → 迁移）：
    /// <code>
    /// 新目标目录存在？
    ///   是 → 带我们的 ownership 标记 → NotNeeded / new-target-present（不读旧根，不创建任何目录；
    ///                                该安装是否 hash 完好由紧随其后的 inspection 如实报告）
    ///        无我们的标记           → Blocked / new-install-unowned（fail-closed）
    ///   否 → 旧目标目录不存在        → Skipped / legacy-absent
    ///        旧目录存在但无我们的标记 → Skipped / legacy-unowned
    ///        标记不可解析             → Skipped / legacy-marker-unreadable
    ///        标记 assetId 不符        → Skipped / legacy-asset-mismatch
    ///        标记 archiveSha256 不符  → Skipped / legacy-archive-mismatch
    ///        文件/哈希核验不通过      → Skipped / legacy-install-invalid
    ///        全部通过                 → 取新根安装锁 → 同卷 staging → 复制清单文件 → 新 marker
    ///                                   → 复制后重算哈希 → 能力探测 → 原子 Directory.Move 发布
    /// </code>
    ///
    /// 安全不变量：
    ///   * 旧目录**只读**：本模块从不删除、从不移动、从不修改旧根中的任何文件；
    ///   * 旧根中的未知文件**不复制**：只复制 manifest 声明的托管文件；
    ///   * 目标目录已存在时**绝不覆盖**（无论有效与否）；
    ///   * 失败 / 取消只删除**本次调用自己创建**的 staging，以及位于新根 StagingRoot 下且带
    ///     Renderist ownership 标记的自身孤儿 staging；
    ///   * 取消的**真实边界**（不得过度表述）：本模块用同步 <see cref="File.Copy(string,string,bool)"/>，
    ///     一旦某次 copy 已经开始，token **不能**中断它本身 —— 取消只保证“**不再启动**新的
    ///     copy / probe / publish”。若取消恰好落在一次 copy 执行期间，该次 copy 可能完成之后
    ///     才被观察，随后只清理本次调用自己创建的 staging，**绝不 publish**。
    ///     Locator 与 hash API 同样没有细粒度 token（刻意不为本修复重写它们），
    ///     旧安装的哈希阶段也只能在这些检查点之间收敛；
    ///   * 取消观察点：入口（已取消则连哈希 / 锁 / 建目录都不做）、staging 创建前、每个文件复制前、
    ///     能力探测之前与之后（token 一路传进短进程，可被 kill）、发布之前；
    ///   * 发布用同卷 <see cref="Directory.Move(string,string)"/>，因此最终目标不会出现半成品；
    ///     极端情况下发布后复核失败也会回滚**本次刚发布**的目录；
    ///   * 迁移成功后正常逻辑仍只认识新托管根，不引入长期双根 discovery，也不新增一次性迁移标记
    ///     （有效的托管安装本身就是幂等完成判据）。
    ///
    /// 不接触 Unity / UMM / Harmony，也不接触任何 Unity API。
    /// </summary>
    internal static class FfmpegLegacyRootMigration
    {
        public static FfmpegLegacyMigrationResult Migrate(
            FfmpegLegacyMigrationRequest request, CancellationToken cancellationToken)
        {
            var result = new FfmpegLegacyMigrationResult
            {
                Outcome = FfmpegLegacyMigrationOutcome.Skipped,
                ReasonCode = FfmpegLegacyMigrationReason.RequestNull,
                OrphanStagingRemoved = new string[0],
                CleanupWarnings = new string[0],
            };

            if (request == null)
                return result;

            if (request.TargetLayout == null)
            {
                result.ReasonCode = FfmpegLegacyMigrationReason.InstallRootUnavailable;
                return result;
            }

            if (request.LegacyLayout == null)
            {
                result.ReasonCode = FfmpegLegacyMigrationReason.LegacyRootUnavailable;
                return result;
            }

            // 迁移是写磁盘行为：已经取消时立刻收敛，连一次哈希 / 锁 / 目录创建都不做。
            // （Locator / hash / File.Copy 都没有细粒度 token，因此其余取消观察点落在
            //   staging 与复制前后的既有安全检查点上：取消只保证不再启动新的 copy / probe /
            //   publish，已在执行的那一次同步 File.Copy 可能跑完才被观察到。）
            if (cancellationToken.IsCancellationRequested)
            {
                result.Outcome = FfmpegLegacyMigrationOutcome.Cancelled;
                result.ReasonCode = FfmpegLegacyMigrationReason.Cancelled;
                return result;
            }

            FfmpegAsset asset = request.Asset ?? FfmpegAssetManifest.Primary;
            result.AssetId = asset.Id;
            result.Version = asset.Version;

            // 纵深防御：两个根相同时绝不自我复制。
            if (string.Equals(request.TargetLayout.InstallRoot, request.LegacyLayout.InstallRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                result.ReasonCode = FfmpegLegacyMigrationReason.LegacyRootEqualsTarget;
                return result;
            }

            IReadOnlyList<string> manifestProblems = FfmpegAssetManifest.Validate();
            if (manifestProblems.Count > 0)
            {
                result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                result.ReasonCode = FfmpegLegacyMigrationReason.ManifestInvalid;
                result.ReasonDetail = string.Join("; ", ToArray(manifestProblems));
                return result;
            }

            string targetDirectory = request.TargetLayout.VersionDirectory(asset.Id, asset.Version);
            string legacyDirectory = request.LegacyLayout.VersionDirectory(asset.Id, asset.Version);
            result.TargetDirectory = targetDirectory;
            result.LegacyInstallDirectory = legacyDirectory;

            // ---- 1) 新托管根优先：只要目标目录存在，就绝不迁移（不覆盖、不回退）。----
            //
            // 这里刻意**只做廉价判定**（目录 + ownership 标记是否存在），不对新安装做内容哈希：
            //   * 带我们的 marker = Renderist 自己已有的安装（其 hash 是否完好由紧随其后的
            //     inspection 负责，并在 ManagedInstalls[].InvalidReason 里如实给出）；
            //   * 无 marker = 确定不是有效托管安装 → fail-closed。
            // 两种情形都不迁移、不覆盖、不 fallback、不执行旧二进制。这样做的原因很实际：
            // 迁移成功后旧根**仍然保留**，因此"新旧目录同时存在"是已迁移用户的**常态**，
            // 若在这里额外 Hash 一次新安装，就会让每一次 readiness 扫描永久多读一份完整安装。
            if (Directory.Exists(targetDirectory))
            {
                if (File.Exists(request.TargetLayout.OwnershipMarkerPath(targetDirectory)))
                {
                    result.Outcome = FfmpegLegacyMigrationOutcome.NotNeeded;
                    result.ReasonCode = FfmpegLegacyMigrationReason.TargetPresent;
                    return result;
                }

                result.Outcome = FfmpegLegacyMigrationOutcome.Blocked;
                result.ReasonCode = FfmpegLegacyMigrationReason.TargetUnowned;
                result.ReasonDetail = targetDirectory;
                return result;
            }

            // ---- 2) 旧托管根的 ownership 与完整性判定（全部只读）。----
            if (!Directory.Exists(legacyDirectory))
            {
                result.Outcome = FfmpegLegacyMigrationOutcome.Skipped;
                result.ReasonCode = FfmpegLegacyMigrationReason.LegacyAbsent;
                return result;
            }

            string legacyMarkerPath = request.LegacyLayout.OwnershipMarkerPath(legacyDirectory);
            if (!File.Exists(legacyMarkerPath))
            {
                // 没有 Renderist 标记的目录不属于我们：既不迁移也不删除。
                result.ReasonCode = FfmpegLegacyMigrationReason.LegacyUnowned;
                return result;
            }

            FfmpegOwnershipMarker marker;
            string markerError;
            if (!FfmpegOwnershipMarker.TryRead(legacyMarkerPath, out marker, out markerError))
            {
                result.ReasonCode = FfmpegLegacyMigrationReason.LegacyMarkerUnreadable;
                result.ReasonDetail = markerError;
                return result;
            }

            if (!string.Equals(marker.AssetId, asset.Id, StringComparison.Ordinal))
            {
                result.ReasonCode = FfmpegLegacyMigrationReason.LegacyAssetMismatch;
                result.ReasonDetail = "expected=" + asset.Id + " actual=" + marker.AssetId;
                return result;
            }

            // migration eligibility 的额外条件：marker 记录的归档哈希必须与当前 manifest 一致。
            if (!FfmpegFileHash.Matches(asset.ArchiveSha256, marker.ArchiveSha256))
            {
                result.ReasonCode = FfmpegLegacyMigrationReason.LegacyArchiveMismatch;
                result.ReasonDetail = "expected=" + asset.ArchiveSha256 +
                                      " actual=" + (marker.ArchiveSha256 ?? "null");
                return result;
            }

            // 复用既有 Locator：必需文件存在且内容哈希与 manifest 一致。
            FfmpegManagedInstall legacy = FfmpegManagedInstallLocator.Locate(request.LegacyLayout, asset);
            if (!legacy.IsValid)
            {
                result.ReasonCode = FfmpegLegacyMigrationReason.LegacyInstallInvalid;
                result.ReasonDetail = legacy.InvalidReason;
                return result;
            }

            return MigrateEligible(request, asset, result, legacyDirectory, targetDirectory, cancellationToken);
        }

        /// <summary>
        /// 已经通过 ownership 判定的迁移：取锁 → 同卷 staging → 复制 → 复核 → 原子发布。
        /// </summary>
        private static FfmpegLegacyMigrationResult MigrateEligible(
            FfmpegLegacyMigrationRequest request,
            FfmpegAsset asset,
            FfmpegLegacyMigrationResult result,
            string legacyDirectory,
            string targetDirectory,
            CancellationToken cancellationToken)
        {
            var cleanupWarnings = new List<string>();
            result.CleanupWarnings = cleanupWarnings;

            FfmpegInstallLock installLock;
            string lockErrorCode;
            string lockErrorDetail;
            if (!FfmpegInstallLock.TryAcquire(request.TargetLayout.LockFilePath, out installLock,
                    out lockErrorCode, out lockErrorDetail))
            {
                result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                result.ReasonCode = lockErrorCode ?? "install-lock-failed";
                result.ReasonDetail = lockErrorDetail;
                return result;
            }

            string stagingDirectory = null;
            using (installLock)
            {
                try
                {
                    // 持锁后重新确认：兼容与正常安装器并发的情形。
                    if (Directory.Exists(targetDirectory))
                    {
                        result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                        result.ReasonCode = FfmpegLegacyMigrationReason.TargetAppeared;
                        result.ReasonDetail = targetDirectory;
                        return result;
                    }

                    // 只清理位于新根 StagingRoot 下、且带我们 ownership 标记的自身孤儿 staging。
                    result.OrphanStagingRemoved = FfmpegInstaller.CleanupOrphanStaging(
                        request.TargetLayout, cleanupWarnings);

                    cancellationToken.ThrowIfCancellationRequested();

                    string token = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) +
                                   "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    stagingDirectory = request.TargetLayout.StagingDirectory(asset.Id, asset.Version, token);

                    if (Directory.Exists(stagingDirectory))
                    {
                        result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                        result.ReasonCode = FfmpegLegacyMigrationReason.StagingCollision;
                        result.ReasonDetail = stagingDirectory;
                        return result;
                    }

                    Directory.CreateDirectory(stagingDirectory);

                    // 新 marker 先写：即使后续步骤失败，该 staging 也能被识别为 Renderist 自己的产物。
                    string markerError;
                    if (!FfmpegOwnershipMarker.TryWrite(
                            request.TargetLayout.OwnershipMarkerPath(stagingDirectory), asset.Id,
                            asset.ArchiveSha256, DateTime.UtcNow, out markerError))
                    {
                        result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                        result.ReasonCode = FfmpegLegacyMigrationReason.StagingMarkerFailed;
                        result.ReasonDetail = markerError;
                        return result;
                    }

                    // 只复制 manifest 声明的托管文件；旧根中的未知文件一律不进入新安装。
                    for (int i = 0; i < asset.Files.Count; i++)
                    {
                        FfmpegAssetFile file = asset.Files[i];

                        string sourceFull;
                        string destinationFull;
                        if (!FfmpegInstallLayout.TryResolveRelative(legacyDirectory, file.RelativePath, out sourceFull) ||
                            !FfmpegInstallLayout.TryResolveRelative(
                                stagingDirectory, file.RelativePath, out destinationFull))
                        {
                            result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                            result.ReasonCode = FfmpegLegacyMigrationReason.InstallFilePathUnsafe;
                            result.ReasonDetail = file.RelativePath;
                            return result;
                        }

                        if (!File.Exists(sourceFull))
                        {
                            if (!file.Required)
                                continue;

                            result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                            result.ReasonCode = FfmpegLegacyMigrationReason.LegacyFileMissing;
                            result.ReasonDetail = file.RelativePath;
                            return result;
                        }

                        // 取消只保证“不再启动新的 copy”：已取消时在这里收敛；
                        // 一旦下面的同步 File.Copy 已经开始，本次 copy 不能被 token 中断，
                        // 它可能完成之后才在下一次检查点被观察到。
                        cancellationToken.ThrowIfCancellationRequested();

                        string destinationDirectory = Path.GetDirectoryName(destinationFull);
                        if (!string.IsNullOrEmpty(destinationDirectory))
                            Directory.CreateDirectory(destinationDirectory);

                        File.Copy(sourceFull, destinationFull, false);

                        if (file.Sha256 == null)
                            continue;

                        // 以**新位置的实际字节**为准复核，而不是信任复制动作本身。
                        string actualSha256;
                        string hashError;
                        if (!FfmpegFileHash.TryCompute(destinationFull, out actualSha256, out hashError))
                        {
                            result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                            result.ReasonCode = FfmpegLegacyMigrationReason.StagedHashFailed;
                            result.ReasonDetail = file.RelativePath + " (" + hashError + ")";
                            return result;
                        }

                        if (!FfmpegFileHash.Matches(file.Sha256, actualSha256))
                        {
                            result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                            result.ReasonCode = FfmpegLegacyMigrationReason.StagedHashMismatch;
                            result.ReasonDetail = file.RelativePath;
                            return result;
                        }
                    }

                    if (request.ProbeCapabilities)
                    {
                        // 已取消时绝不启动新的短进程探测。
                        cancellationToken.ThrowIfCancellationRequested();

                        string stagingPrimary;
                        if (!FfmpegInstallLayout.TryResolveRelative(
                                stagingDirectory, asset.PrimaryExecutableRelativePath, out stagingPrimary) ||
                            !File.Exists(stagingPrimary))
                        {
                            result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                            result.ReasonCode = FfmpegLegacyMigrationReason.ProbeExecutableMissing;
                            result.ReasonDetail = asset.PrimaryExecutableRelativePath;
                            return result;
                        }

                        int timeout = request.ProbeTimeoutSeconds > 0
                            ? request.ProbeTimeoutSeconds
                            : FfmpegCapabilityProbe.DefaultTimeoutSeconds;

                        // 能力探测只针对**新位置**的副本：绝不执行旧 LocalLow 路径中的二进制。
                        result.Capability = FfmpegCapabilityProbe.Probe(stagingPrimary, timeout, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!result.Capability.IsUsableForMp4)
                        {
                            result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                            result.ReasonCode = FfmpegLegacyMigrationReason.CapabilityProbeFailed;
                            result.ReasonDetail = result.Capability.ErrorCode ??
                                string.Join(",", ToArray(result.Capability.MissingCapabilities));
                            return result;
                        }
                    }

                    string assetDirectory = Path.GetDirectoryName(targetDirectory);
                    if (!string.IsNullOrEmpty(assetDirectory))
                        Directory.CreateDirectory(assetDirectory);

                    if (Directory.Exists(targetDirectory))
                    {
                        // 竞态或外部创建：绝不覆盖。
                        result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                        result.ReasonCode = FfmpegLegacyMigrationReason.TargetAppeared;
                        result.ReasonDetail = targetDirectory;
                        return result;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // 同卷原子发布：staging 与 target 都在新托管根下。
                    Directory.Move(stagingDirectory, targetDirectory);
                    stagingDirectory = null;
                    result.TargetDirectory = targetDirectory;

                    FfmpegManagedInstall published = FfmpegManagedInstallLocator.Locate(
                        request.TargetLayout, asset);
                    if (!published.IsValid)
                    {
                        // 发布后复核失败：该目录是本次调用在持锁状态下刚刚创建的，ownership 明确，
                        // 因此可以安全回滚，保证最终目标不留下无法识别的半成品。
                        string rollbackError = TryDeleteOwnStaging(targetDirectory);
                        if (rollbackError != null)
                            cleanupWarnings.Add(rollbackError);

                        result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                        result.ReasonCode = FfmpegLegacyMigrationReason.PublishedInstallInvalid;
                        result.ReasonDetail = published.InvalidReason;
                        return result;
                    }

                    result.Outcome = FfmpegLegacyMigrationOutcome.Migrated;
                    result.ReasonCode = FfmpegLegacyMigrationReason.Migrated;
                    return result;
                }
                catch (OperationCanceledException)
                {
                    result.Outcome = FfmpegLegacyMigrationOutcome.Cancelled;
                    result.ReasonCode = FfmpegLegacyMigrationReason.Cancelled;
                    return result;
                }
                catch (Exception ex)
                {
                    result.Outcome = FfmpegLegacyMigrationOutcome.Failed;
                    result.ReasonCode = FfmpegLegacyMigrationReason.MigrationFailed;
                    result.ReasonDetail = ex.Message;
                    return result;
                }
                finally
                {
                    // 回滚：只删除本次调用自己创建的 staging。
                    if (stagingDirectory != null)
                    {
                        string cleanupError = TryDeleteOwnStaging(stagingDirectory);
                        if (cleanupError != null)
                            cleanupWarnings.Add(cleanupError);
                    }
                }
            }
        }

        /// <summary>删除一个由本模块在本次调用中创建的 staging 目录。返回 null = 成功。</summary>
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
