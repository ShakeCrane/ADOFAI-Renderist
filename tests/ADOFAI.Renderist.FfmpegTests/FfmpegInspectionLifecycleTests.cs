using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// FFmpeg 组件检查的 **lifecycle cancellation** 回归。
    ///
    /// 背景（本轮修复的缺陷）：legacy 托管根迁移是**写磁盘**行为（复制 / 写新 marker /
    /// 原子发布），但生产接线把 <c>CancellationToken.None</c> 传给
    /// <see cref="FfmpegLegacyRootMigration.Migrate"/>，因此 Mod disable / unload 时
    /// 在途迁移无法收敛。helper 自己支持 token 并不等于生产合同支持取消。
    ///
    /// 这里覆盖的是**生产序列本身**（<see cref="FfmpegInspectionSequence"/>）与
    /// readiness 的 in-flight 释放语义，而不只是 helper：
    ///   * 同一个 token 真正贯穿迁移与 inspection；
    ///   * 取消之后不再启动任何新的 inspector / capability probe（用假 FFmpeg 的调用记录硬观测）；
    ///   * 被取消的检查永远不会成为 readiness 的当前结论；
    ///   * disable → enable 之后旧 scan-in-flight 被释放，新 generation 能正常启动；
    ///   * CTS 的 Cancel / Dispose 契约（只请求取消；settle 之后才释放）。
    ///
    /// 另有 <c>ModEntry</c> 的 wiring guard：它是 UMM/Unity 代码，net48 无法编译，
    /// 因此用源码级断言固定“生产接线不是 CancellationToken.None，且 shutdown 会取消在途检查”。
    /// </summary>
    internal static class FfmpegInspectionLifecycleTests
    {
        private const string ProbeTouchVariable = "RENDERIST_FAKE_FFMPEG_TOUCH";

        private const string TestArchiveSha256 =
            "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";

        private static string _workRoot;

        private sealed class Scenario
        {
            public string Work;
            public FfmpegInstallLayout Target;
            public FfmpegInstallLayout Legacy;
            public FfmpegAsset Asset;

            public string TargetRoot { get { return Target.InstallRoot; } }
            public string LegacyRoot { get { return Legacy.InstallRoot; } }
            public string TargetDirectory { get { return Target.VersionDirectory(Asset.Id, Asset.Version); } }
            public string LegacyDirectory { get { return Legacy.VersionDirectory(Asset.Id, Asset.Version); } }
        }

        public static void Run(string workRoot)
        {
            _workRoot = workRoot;

            byte[] fakeExe = File.ReadAllBytes(FakeFfmpeg.ExecutablePath);
            byte[] small = Fixtures.DeterministicBytes(4096, 71);

            CancellationContractIsSafe();
            LiveTokenRunsMigrationThenInspection();
            PreCancelledTokenDoesNoDiskWork(small);
            CancellationDuringMigrationStopsBeforeAnyProbe();
            CancelledInspectionIsNeverPublishedAndReleasesTheScanMarker(fakeExe);
            RealPinnedFixtureRunsThroughTheProductionSequence();
            WiringGuard();
            DisabledSafetyNetWiringGuard();
        }

        // ==================== CTS 契约 ====================

        private static void CancellationContractIsSafe()
        {
            TestKit.Run("lifecycle: the inspection cancellation contract is safe in every order", () =>
            {
                var cancellation = new FfmpegInspectionCancellation();
                CancellationToken frozen = cancellation.Token;

                TestKit.Check(frozen.CanBeCanceled,
                    "the frozen token must be cancellable (production must never use CancellationToken.None)");
                TestKit.Check(!frozen.IsCancellationRequested, "a fresh token must not be cancelled");

                cancellation.Cancel();
                TestKit.Check(cancellation.IsCancellationRequested, "cancel must be observable");
                TestKit.Check(frozen.IsCancellationRequested, "the frozen token must observe the cancel");

                cancellation.Cancel();
                TestKit.Check(cancellation.IsCancellationRequested, "cancel must be idempotent");

                // 释放：幂等，且在“先取消后释放”与“释放后再取消”两个顺序下都不抛异常。
                cancellation.Dispose();
                cancellation.Dispose();
                cancellation.Cancel();
                TestKit.Check(frozen.IsCancellationRequested,
                    "a token frozen before dispose must still report cancellation");

                var disposedFirst = new FfmpegInspectionCancellation();
                disposedFirst.Dispose();
                disposedFirst.Cancel();
                disposedFirst.Dispose();
                // 只要没有异常即可：释放之后的 Cancel 必须是安全 no-op（收敛路径绝不因取消本身出错）。
            });
        }

        // ==================== 正常路径（同一 token 贯穿） ====================

        private static void LiveTokenRunsMigrationThenInspection()
        {
            TestKit.Run("lifecycle: a live token runs migration and inspection and reaches Ready", () =>
            {
                byte[] fakeExe = File.ReadAllBytes(FakeFfmpeg.ExecutablePath);
                Scenario s = NewScenario("lifecycle-live", fakeExe, fakeExe);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { fakeExe, fakeExe }, TestArchiveSha256);

                string probeLog = StartProbeRecording(s.Work, "live");
                FfmpegComponentReport report;
                try
                {
                    using (var cancellation = new FfmpegInspectionCancellation())
                    {
                        report = FfmpegInspectionSequence.Run(new FfmpegInspectionSequenceRequest
                        {
                            InstallRoot = s.TargetRoot,
                            LegacyRoot = s.LegacyRoot,
                            Asset = s.Asset,
                            CancellationToken = cancellation.Token,
                        });
                    }
                }
                finally
                {
                    StopProbeRecording();
                }

                TestKit.Check(!FfmpegInspectionSequence.IsCancelled(report),
                    "an uncancelled run must produce a real conclusion");
                TestKit.CheckEqual(FfmpegComponentState.Ready, report.State,
                    "the migrated install must be Ready (" + report.DiscoveryErrorCode + ")");
                TestKit.CheckEqual(FfmpegCandidateSource.ManagedInstall, report.Source,
                    "the candidate must come from the migrated managed root");
                CheckOutcome(report.Migration, FfmpegLegacyMigrationOutcome.Migrated,
                    FfmpegLegacyMigrationReason.Migrated);
                TestKit.Check(ProbeCount(probeLog) > 0,
                    "the capability probe must actually run on the uncancelled path");
                CheckNoStaging(s.Target);
            });
        }

        // ==================== 取消：不继续做新工作 ====================

        private static void PreCancelledTokenDoesNoDiskWork(byte[] content)
        {
            TestKit.Run("lifecycle: a pre-cancelled token performs no migration, probe or directory creation", () =>
            {
                Scenario s = NewScenario("lifecycle-pre-cancelled", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);

                string probeLog = StartProbeRecording(s.Work, "pre-cancelled");
                FfmpegComponentReport report;
                try
                {
                    var cancellation = new FfmpegInspectionCancellation();
                    cancellation.Cancel();
                    CancellationToken token = cancellation.Token;

                    report = FfmpegInspectionSequence.Run(new FfmpegInspectionSequenceRequest
                    {
                        InstallRoot = s.TargetRoot,
                        LegacyRoot = s.LegacyRoot,
                        Asset = s.Asset,
                        CancellationToken = token,
                    });

                    cancellation.Dispose();
                }
                finally
                {
                    StopProbeRecording();
                }

                TestKit.Check(FfmpegInspectionSequence.IsCancelled(report),
                    "the report must carry the cancelled marker");
                CheckOutcome(report.Migration, FfmpegLegacyMigrationOutcome.Cancelled,
                    FfmpegLegacyMigrationReason.Cancelled);
                TestKit.CheckEqual(0, ProbeCount(probeLog),
                    "no capability probe may run for a pre-cancelled inspection");
                TestKit.Check(!Directory.Exists(s.TargetRoot),
                    "a pre-cancelled inspection must not create the target root (no lock, no staging)");
                TestKit.Check(Directory.Exists(s.LegacyDirectory), "the legacy install must be preserved");
            });
        }

        private static void CancellationDuringMigrationStopsBeforeAnyProbe()
        {
            TestKit.Run("lifecycle: cancelling during the copy stops before any inspector probe", () =>
            {
                byte[] big = Fixtures.DeterministicBytes(48 * 1024 * 1024, 72);
                byte[] small = Fixtures.DeterministicBytes(4096, 73);
                Scenario s = NewScenario("lifecycle-cancel-mid-copy", big, small);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { big, small }, TestArchiveSha256);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyRoot);

                string probeLog = StartProbeRecording(s.Work, "cancel-mid-copy");
                try
                {
                    var cancellation = new FfmpegInspectionCancellation();
                    CancellationToken token = cancellation.Token;

                    FfmpegComponentReport report = null;
                    Task task = Task.Run(() =>
                    {
                        report = FfmpegInspectionSequence.Run(new FfmpegInspectionSequenceRequest
                        {
                            InstallRoot = s.TargetRoot,
                            LegacyRoot = s.LegacyRoot,
                            Asset = s.Asset,
                            CancellationToken = token,
                        });
                    });

                    TestKit.Check(WaitForStaging(s.Target, 60000),
                        "the migration copy must actually start before we cancel");
                    cancellation.Cancel();
                    TestKit.Check(task.Wait(180000), "the inspection must settle");

                    TestKit.Check(FfmpegInspectionSequence.IsCancelled(report),
                        "the report must carry the cancelled marker");
                    CheckOutcome(report.Migration, FfmpegLegacyMigrationOutcome.Cancelled,
                        FfmpegLegacyMigrationReason.Cancelled);

                    TestKit.CheckEqual(0, ProbeCount(probeLog),
                        "no capability probe may run once cancellation was observed");
                    TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be published");
                    CheckNoStaging(s.Target);
                    CheckTreeUnchanged("legacy root", s.LegacyRoot, legacyBefore);

                    cancellation.Dispose();
                }
                finally
                {
                    StopProbeRecording();
                }
            });
        }

        // ==================== readiness：不发布 / 释放 in-flight / 新 generation ====================

        private static void CancelledInspectionIsNeverPublishedAndReleasesTheScanMarker(byte[] fakeExe)
        {
            TestKit.Run("lifecycle: shutdown cancels, drops the result, releases the scan marker and rescans", () =>
            {
                byte[] big = Fixtures.DeterministicBytes(48 * 1024 * 1024, 74);
                byte[] small = Fixtures.DeterministicBytes(4096, 75);
                Scenario s = NewScenario("lifecycle-shutdown", big, small);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { big, small }, TestArchiveSha256);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyRoot);

                string probeLog = StartProbeRecording(s.Work, "shutdown");
                try
                {
                    // 与 ModEntry 完全相同的顺序：冻结 generation → 启动 → shutdown → 收取。
                    var tracker = new FfmpegReadinessTracker();
                    int generation;
                    TestKit.Check(tracker.TryBeginScan(out generation), "the scan must start");
                    TestKit.CheckEqual(0, generation, "the first generation");

                    var cancellation = new FfmpegInspectionCancellation();
                    CancellationToken token = cancellation.Token;

                    FfmpegComponentReport report = null;
                    Task task = Task.Run(() =>
                    {
                        report = FfmpegInspectionSequence.Run(new FfmpegInspectionSequenceRequest
                        {
                            InstallRoot = s.TargetRoot,
                            LegacyRoot = s.LegacyRoot,
                            Asset = s.Asset,
                            CancellationToken = token,
                        });
                    });

                    TestKit.Check(WaitForStaging(s.Target, 60000), "the migration copy must start");

                    // ShutdownFfmpegComponent：请求取消 + invalidate generation。
                    cancellation.Cancel();
                    tracker.Invalidate("ffmpeg-shutdown");

                    TestKit.Check(task.Wait(180000), "the cancelled inspection must settle");

                    // Pump 收取：取消的检查先失效，再走既有发布路径（用于释放 in-flight 标记）。
                    TestKit.Check(FfmpegInspectionSequence.IsCancelled(report),
                        "the settled result must be marked cancelled");
                    if (FfmpegInspectionSequence.IsCancelled(report))
                        tracker.Invalidate("ffmpeg-inspection-cancelled");

                    TestKit.Check(!tracker.TryPublishScan(generation, report),
                        "a cancelled inspection must never be published");
                    TestKit.Check(!tracker.ScanInFlight,
                        "the in-flight marker must be released so a later scan can start");
                    TestKit.Check(tracker.NeedsScan, "a new scan must be queued");
                    TestKit.CheckEqual(FfmpegReadinessState.Preparing, tracker.Snapshot().State,
                        "no conclusion may be current after a cancelled inspection");

                    TestKit.CheckEqual(0, ProbeCount(probeLog),
                        "no capability probe may run after cancellation");
                    TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be published");
                    CheckNoStaging(s.Target);
                    CheckTreeUnchanged("legacy root", s.LegacyRoot, legacyBefore);

                    // disable → enable：下一 generation 必须能正常启动并真实工作。
                    // 用**可执行**的假 FFmpeg 场景，使重扫真的通过能力探测到达 Ready。
                    int nextGeneration;
                    TestKit.Check(tracker.TryBeginScan(out nextGeneration), "the next generation must start");
                    TestKit.Check(nextGeneration > generation,
                        "the generation must advance after the shutdown invalidations");

                    byte[] executable = File.ReadAllBytes(FakeFfmpeg.ExecutablePath);
                    Scenario rescan = NewScenario("lifecycle-shutdown-rescan", executable, executable);
                    Fixtures.CreateManagedInstall(rescan.Legacy, rescan.Asset,
                        new[] { executable, executable }, TestArchiveSha256);

                    using (var nextCancellation = new FfmpegInspectionCancellation())
                    {
                        FfmpegComponentReport next = FfmpegInspectionSequence.Run(
                            new FfmpegInspectionSequenceRequest
                            {
                                InstallRoot = rescan.TargetRoot,
                                LegacyRoot = rescan.LegacyRoot,
                                Asset = rescan.Asset,
                                CancellationToken = nextCancellation.Token,
                            });

                        TestKit.Check(!FfmpegInspectionSequence.IsCancelled(next), "the new scan must conclude");
                        TestKit.CheckEqual(FfmpegComponentState.Ready, next.State,
                            "the rescan must reach Ready (" + next.DiscoveryErrorCode + ")");
                        TestKit.Check(tracker.TryPublishScan(nextGeneration, next),
                            "the new generation must be publishable again");
                    }

                    TestKit.Check(ProbeCount(probeLog) > 0, "the new scan must really probe");
                    TestKit.CheckEqual(FfmpegReadinessState.Ready, tracker.Snapshot().State,
                        "readiness must be Ready after the rescan");

                    cancellation.Dispose();
                }
                finally
                {
                    StopProbeRecording();
                }
            });
        }

        // ==================== 真实 fixture 走生产序列（可选） ====================

        private static void RealPinnedFixtureRunsThroughTheProductionSequence()
        {
            TestKit.Run("lifecycle: a real pinned fixture reaches Ready through the production sequence", () =>
            {
                string fixtureFfmpeg = FindPinnedFixture();
                if (fixtureFfmpeg == null)
                {
                    throw new SkipTestException(
                        "no local FFmpeg fixture matching the pinned manifest; set RENDERIST_TEST_FFMPEG_DIR to a " +
                        "directory containing the pinned ffmpeg.exe and ffprobe.exe (binaries are intentionally " +
                        "not committed)");
                }

                FfmpegAsset asset = FfmpegAssetManifest.Primary;
                Scenario s = NewScenarioWithAsset("lifecycle-real-fixture", asset);

                string legacyDirectory = s.LegacyDirectory;
                Directory.CreateDirectory(Path.Combine(legacyDirectory, "bin"));
                File.Copy(fixtureFfmpeg, Path.Combine(legacyDirectory, "bin", "ffmpeg.exe"));
                File.Copy(Path.Combine(Path.GetDirectoryName(fixtureFfmpeg), "ffprobe.exe"),
                    Path.Combine(legacyDirectory, "bin", "ffprobe.exe"));

                string markerError;
                if (!FfmpegOwnershipMarker.TryWrite(s.Legacy.OwnershipMarkerPath(legacyDirectory), asset.Id,
                        asset.ArchiveSha256, DateTime.UtcNow, out markerError))
                {
                    throw new Exception("failed to write the legacy fixture marker: " + markerError);
                }

                if (!FfmpegManagedInstallLocator.Locate(s.Legacy, asset).IsValid)
                {
                    throw new SkipTestException(
                        "the configured FFmpeg fixture does not match the pinned manifest hashes");
                }

                var tracker = new FfmpegReadinessTracker();
                int generation;
                TestKit.Check(tracker.TryBeginScan(out generation), "the scan must start");

                // Asset = null：完全走生产默认（真实 manifest + 真实资产）。
                FfmpegComponentReport report = FfmpegInspectionSequence.Run(
                    new FfmpegInspectionSequenceRequest
                    {
                        InstallRoot = s.TargetRoot,
                        LegacyRoot = s.LegacyRoot,
                        CancellationToken = CancellationToken.None,
                    });

                CheckOutcome(report.Migration, FfmpegLegacyMigrationOutcome.Migrated,
                    FfmpegLegacyMigrationReason.Migrated);
                TestKit.CheckEqual(FfmpegComponentState.Ready, report.State,
                    "the production sequence must reach Ready with the real fixture (" +
                    report.DiscoveryErrorCode + ")");
                TestKit.Check(tracker.TryPublishScan(generation, report), "the result must be published");
                TestKit.CheckEqual(FfmpegReadinessState.Ready, tracker.Snapshot().State, "readiness");
                CheckNoStaging(s.Target);
            });
        }

        private static string FindPinnedFixture()
        {
            FfmpegAsset asset = FfmpegAssetManifest.Primary;
            string expectedFfmpeg = null;
            string expectedFfprobe = null;
            for (int i = 0; i < asset.Files.Count; i++)
            {
                if (string.Equals(asset.Files[i].RelativePath, "bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    expectedFfmpeg = asset.Files[i].Sha256;
                else if (string.Equals(asset.Files[i].RelativePath, "bin/ffprobe.exe",
                             StringComparison.OrdinalIgnoreCase))
                    expectedFfprobe = asset.Files[i].Sha256;
            }

            IReadOnlyList<string> candidates = Program.FindLocalFfmpegBinaries();
            for (int i = 0; i < candidates.Count; i++)
            {
                string directory = Path.GetDirectoryName(candidates[i]);
                if (string.IsNullOrEmpty(directory))
                    continue;

                string ffprobe = Path.Combine(directory, "ffprobe.exe");
                if (!File.Exists(ffprobe))
                    continue;

                if (HashMatches(candidates[i], expectedFfmpeg) && HashMatches(ffprobe, expectedFfprobe))
                    return candidates[i];
            }

            return null;
        }

        private static bool HashMatches(string path, string expectedSha256)
        {
            if (string.IsNullOrEmpty(expectedSha256))
                return false;

            string actual;
            string error;
            return FfmpegFileHash.TryCompute(path, out actual, out error) &&
                   FfmpegFileHash.Matches(expectedSha256, actual);
        }

        // ==================== ModEntry wiring guard ====================

        /// <summary>
        /// ModEntry 是 UMM / Unity 代码，net48 harness 刻意不编译它，因此这里用**源码级断言**
        /// 固定本轮修复的生产接线。它只断言结构性事实（谁调用谁），不复制任何逻辑。
        /// </summary>
        private static void WiringGuard()
        {
            TestKit.Run("lifecycle wiring: ModEntry passes a real token and cancels it on shutdown", () =>
            {
                string path = FindModEntrySource();
                if (path == null)
                {
                    throw new SkipTestException(
                        "src/ADOFAI.Renderist/ModEntry.cs not found relative to the test executable; " +
                        "the wiring guard only runs from the repository build output");
                }

                string source = File.ReadAllText(path, Encoding.UTF8);

                string requestInspection = ExtractMethodBody(source, "private static void RequestFfmpegInspection()");
                string shutdown = ExtractMethodBody(source, "private static void ShutdownFfmpegComponent(string reason)");
                string cancel = ExtractMethodBody(source, "private static void RequestFfmpegInspectionCancel()");

                TestKit.Check(requestInspection.Contains("FfmpegInspectionSequence.Run"),
                    "the production inspection must run through FfmpegInspectionSequence");
                TestKit.Check(Regex.IsMatch(requestInspection, @"CancellationToken\s*=\s*cancellationToken\b"),
                    "the frozen token must be passed into the inspection sequence");
                TestKit.Check(!requestInspection.Contains("CancellationToken.None"),
                    "the production inspection must never start with CancellationToken.None");
                TestKit.Check(requestInspection.Contains("ContinueWith"),
                    "the CTS must be released by a continuation that runs after the task settles");
                TestKit.Check(requestInspection.Contains("_ffmpegInspectCancellation"),
                    "the in-flight cancellation must be published for shutdown to cancel");

                TestKit.Check(cancel.Contains(".Cancel()"),
                    "the cancel entry point must request cancellation");
                TestKit.Check(!cancel.Contains("Dispose()"),
                    "the cancel entry point must never dispose the CTS (the task may still be running)");

                TestKit.Check(shutdown.Contains("RequestFfmpegInspectionCancel()"),
                    "ShutdownFfmpegComponent must cancel the in-flight inspection");
                TestKit.Check(shutdown.Contains("Invalidate"),
                    "ShutdownFfmpegComponent must still invalidate the readiness generation");

                TestKit.Check(!source.Contains("CancellationToken.None"),
                    "ModEntry must not start any FFmpeg inspection work with CancellationToken.None");
            });
        }

        /// <summary>
        /// 本轮修复的漏口：disabled safety-net 曾经位于
        /// <c>if (_ffmpegDownloadController == null) return;</c> **之后**。
        /// controller 只在用户真正发起过下载之后才存在，普通已有托管 FFmpeg 的用户恒为 null，
        /// 因此“只依赖 OnUpdate 的 disabled 兜底”在那种机器上根本不会取消在途迁移（写磁盘行为）。
        ///
        /// 这里固定三条结构性事实：取消在任何 controller 早退之前；没有任何 early return 能绕过它；
        /// 且“尚未启用”（Enabled 初值 false）不被误判为 disabled（保护 Load 启动的 initial scan）。
        /// </summary>
        private static void DisabledSafetyNetWiringGuard()
        {
            TestKit.Run("lifecycle wiring: the disabled safety-net cancels inspection without a download controller", () =>
            {
                string path = FindModEntrySource();
                if (path == null)
                {
                    throw new SkipTestException(
                        "src/ADOFAI.Renderist/ModEntry.cs not found relative to the test executable; " +
                        "the wiring guard only runs from the repository build output");
                }

                string source = File.ReadAllText(path, Encoding.UTF8);
                string body = StripLineComments(ExtractMethodBody(
                    source, "private static void PumpFfmpegComponentLifecycle()"));

                int cancelIndex = body.IndexOf("RequestFfmpegInspectionCancel()", StringComparison.Ordinal);
                int controllerNullIndex = body.IndexOf("controller == null", StringComparison.Ordinal);
                int firstReturnIndex = body.IndexOf("return;", StringComparison.Ordinal);

                TestKit.Check(cancelIndex >= 0,
                    "the disabled safety-net must request cancellation");
                TestKit.Check(controllerNullIndex > cancelIndex,
                    "cancellation must happen BEFORE the download-controller null early return");
                TestKit.Check(firstReturnIndex > cancelIndex,
                    "no early return may bypass the disabled safety-net cancellation");
                TestKit.Check(Regex.IsMatch(body, @"shuttingDown\s*=\s*_ffmpegLifecycleShutdown\s*\|\|"),
                    "the shutdown condition must still include the observed lifecycle shutdown flag");
                TestKit.Check(body.Contains("!Enabled && _ffmpegEverEnabled"),
                    "a not-yet-enabled process must not be treated as disabled (initial scan protection)");
                TestKit.Check(body.Contains("_ffmpegLifecycleShutdown = true"),
                    "the no-controller disabled path must still latch the lifecycle shutdown flag");

                string toggle = StripLineComments(ExtractMethodBody(
                    source, "private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)"));
                TestKit.Check(toggle.Contains("_ffmpegEverEnabled = true"),
                    "OnToggle(true) must latch that this process has been enabled");
                TestKit.Check(!toggle.Contains("_ffmpegReadiness"),
                    "the first enable must still not invalidate readiness (the initial scan must be adopted)");

                string pumpTasks = StripLineComments(ExtractMethodBody(source, "private static void PumpFfmpegTasks()"));
                TestKit.Check(pumpTasks.Contains("!_ffmpegLifecycleShutdown && _ffmpegReadiness.NeedsScan"),
                    "new scans must stay gated on the lifecycle shutdown flag");
            });
        }

        /// <summary>去掉行注释，避免注释文字干扰结构性断言。</summary>
        private static string StripLineComments(string source)
        {
            return Regex.Replace(source, @"(?m)//[^\r\n]*", string.Empty);
        }

        private static string FindModEntrySource()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && directory != null; i++)
            {
                string candidate = Path.Combine(
                    directory.FullName, "src", "ADOFAI.Renderist", "ModEntry.cs");
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            return null;
        }

        /// <summary>
        /// 取出一个方法体（按签名行定位，再按花括号配平）。只用于 wiring guard 的结构断言。
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0)
                throw new Exception("method not found in ModEntry.cs: " + signature);

            int open = source.IndexOf('{', start);
            if (open < 0)
                throw new Exception("method body not found: " + signature);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            throw new Exception("unbalanced method body: " + signature);
        }

        // ==================== helpers ====================

        private static Scenario NewScenario(string label, byte[] ffmpegBytes, byte[] ffprobeBytes)
        {
            return NewScenarioWithAsset(label, BuildAsset(ffmpegBytes, ffprobeBytes));
        }

        private static Scenario NewScenarioWithAsset(string label, FfmpegAsset asset)
        {
            string work = TestKit.NewWorkDirectory(_workRoot, label);

            FfmpegInstallLayout target;
            string targetError;
            if (!FfmpegInstallLayout.TryCreate(Path.Combine(work, "target"), out target, out targetError))
                throw new Exception("target layout: " + targetError);

            FfmpegInstallLayout legacy;
            string legacyError;
            if (!FfmpegInstallLayout.TryCreate(Path.Combine(work, "legacy"), out legacy, out legacyError))
                throw new Exception("legacy layout: " + legacyError);

            return new Scenario
            {
                Work = work,
                Target = target,
                Legacy = legacy,
                Asset = asset,
            };
        }

        private static FfmpegAsset BuildAsset(byte[] ffmpegBytes, byte[] ffprobeBytes)
        {
            return new FfmpegAsset(
                Fixtures.TestAssetId,
                Fixtures.TestAssetVersion,
                "lifecycle test asset",
                "https://example.invalid/lifecycle.zip",
                TestArchiveSha256,
                4096,
                "GPLv3",
                "https://example.invalid/license",
                "https://example.invalid/source",
                "synthetic fixture",
                "bin/ffmpeg.exe",
                new[]
                {
                    new FfmpegAssetFile("ffmpeg.exe", "bin/ffmpeg.exe", Fixtures.Sha256Of(ffmpegBytes), true),
                    new FfmpegAssetFile("ffprobe.exe", "bin/ffprobe.exe", Fixtures.Sha256Of(ffprobeBytes), true),
                });
        }

        private static string StartProbeRecording(string work, string label)
        {
            string path = Path.Combine(work, label + "-probes.log");
            Environment.SetEnvironmentVariable(ProbeTouchVariable, path);
            return path;
        }

        private static void StopProbeRecording()
        {
            Environment.SetEnvironmentVariable(ProbeTouchVariable, null);
        }

        private static int ProbeCount(string path)
        {
            if (!File.Exists(path))
                return 0;

            return File.ReadAllLines(path).Length;
        }

        private static void CheckOutcome(
            FfmpegLegacyMigrationResult result, FfmpegLegacyMigrationOutcome outcome, string reasonCode)
        {
            TestKit.Check(result != null, "the migration must produce a result");
            if (result.Outcome != outcome || !string.Equals(result.ReasonCode, reasonCode, StringComparison.Ordinal))
            {
                throw new Exception(string.Format(CultureInfo.InvariantCulture,
                    "outcome mismatch: expected <{0}/{1}> actual <{2}/{3}> detail={4}",
                    outcome, reasonCode, result.Outcome, result.ReasonCode, result.ReasonDetail));
            }
        }

        private static void CheckNoStaging(FfmpegInstallLayout layout)
        {
            TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout),
                "no staging directory may be left behind");
        }

        private static SortedDictionary<string, string> SnapshotTree(string root)
        {
            var snapshot = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(root))
                return snapshot;

            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string relative = files[i].Substring(root.Length).TrimStart('\\', '/');
                string sha256;
                string error;
                if (!FfmpegFileHash.TryCompute(files[i], out sha256, out error))
                    sha256 = "unreadable:" + error;

                snapshot[relative] = sha256;
            }

            return snapshot;
        }

        private static void CheckTreeUnchanged(
            string what, string root, SortedDictionary<string, string> before)
        {
            SortedDictionary<string, string> after = SnapshotTree(root);
            TestKit.CheckEqual(before.Count, after.Count, what + ": file count must not change");
            foreach (KeyValuePair<string, string> entry in before)
            {
                TestKit.Check(after.ContainsKey(entry.Key), what + ": file must still exist: " + entry.Key);
                TestKit.CheckEqual(entry.Value, after[entry.Key],
                    what + ": content must not change: " + entry.Key);
            }
        }

        private static bool WaitForStaging(FfmpegInstallLayout layout, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (Directory.Exists(layout.StagingRoot) &&
                        Directory.GetDirectories(layout.StagingRoot).Length > 0)
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    // 目录正在被创建：继续轮询。
                }

                Thread.Sleep(1);
            }

            return false;
        }
    }
}
