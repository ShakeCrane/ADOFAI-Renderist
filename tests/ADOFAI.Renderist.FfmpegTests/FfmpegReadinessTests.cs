using System;
using System.IO;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// FFmpeg startup readiness（0.3.10.2）回归。
    ///
    /// 覆盖两类断言，测试名里明确区分：
    ///   * **behavior（行为）** —— 直接驱动生产类型 <see cref="FfmpegReadinessTracker"/>
    ///     与 <see cref="Mp4SessionStartup.TryCheckReadiness"/>：generation 绑定、dirty 失效、
    ///     in-flight 不得 Ready、过期结果丢弃、fault 不自动重试、PNG / Log-only 不受影响。
    ///   * **wiring（结构）** —— ModEntry / EditorExportController 依赖 UnityEngine，无法在
    ///     net48 harness 中实例化，因此这里按仓库既有做法断言生产源码的**调用点与顺序**
    ///     （首次检查在 Load、controller 门禁在创建目录与写 metadata 之前）。
    /// </summary>
    internal static class FfmpegReadinessTests
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        public static void Run()
        {
            BehaviorTests();
            GateTests();
            WiringTests();
        }

        // ============================================================ behavior

        private static void BehaviorTests()
        {
            TestKit.Run("readiness behavior: a fresh tracker is Preparing and demands a scan without any GUI", () =>
            {
                var tracker = new FfmpegReadinessTracker();

                FfmpegReadinessSnapshot snapshot = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Preparing, snapshot.State, "state");
                TestKit.CheckEqual(FfmpegReadinessReason.Pending, snapshot.ReasonCode, "reason");
                TestKit.CheckEqual(null, snapshot.Report, "no report may be remembered");
                TestKit.Check(tracker.NeedsScan,
                    "a fresh process must request its first scan without a GUI ever being drawn");
                TestKit.Check(!snapshot.ScanInFlight, "nothing is in flight yet");
            });

            TestKit.Run("readiness behavior: a published Ready report becomes Ready once and is not re-scanned", () =>
            {
                var tracker = new FfmpegReadinessTracker();

                int generation;
                TestKit.Check(tracker.TryBeginScan(out generation), "a pending scan can start");
                TestKit.Check(!tracker.TryBeginScan(out int second),
                    "a second concurrent scan must be refused");
                TestKit.CheckEqual(FfmpegReadinessTracker.NoGeneration, second, "refused scan reports no generation");

                TestKit.Check(tracker.TryPublishScan(generation, ReadyReport()), "the current generation publishes");
                TestKit.Check(!tracker.NeedsScan,
                    "a published conclusion must not be re-scanned every frame (no per-frame retry loop)");

                FfmpegReadinessSnapshot snapshot = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Ready, snapshot.State, "state");
                TestKit.CheckEqual(FfmpegReadinessReason.None, snapshot.ReasonCode, "reason");
                TestKit.Check(snapshot.Report != null && snapshot.Report.State == FfmpegComponentState.Ready,
                    "the Ready snapshot carries the current report");
                TestKit.CheckEqual(generation, snapshot.Generation, "snapshot generation");
            });

            TestKit.Run("readiness behavior: a non-Ready component is Failed and names its component state", () =>
            {
                var tracker = new FfmpegReadinessTracker();
                int generation;
                tracker.TryBeginScan(out generation);
                tracker.TryPublishScan(generation, new FfmpegComponentReport
                {
                    State = FfmpegComponentState.NotFound,
                    DiscoveryErrorCode = "ffmpeg-not-found",
                });

                FfmpegReadinessSnapshot snapshot = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Failed, snapshot.State, "state");
                TestKit.CheckEqual("component-NotFound", snapshot.ReasonCode, "reason code");
                TestKit.CheckEqual("ffmpeg-not-found", snapshot.ReasonDetail, "detail");
                TestKit.Check(!tracker.NeedsScan, "an unavailable component must not be re-scanned every frame");
            });

            TestKit.Run("readiness behavior: invalidation retires a Ready report immediately", () =>
            {
                var tracker = new FfmpegReadinessTracker();
                PublishReady(tracker);
                TestKit.CheckEqual(FfmpegReadinessState.Ready, tracker.Snapshot().State, "seeded Ready");

                tracker.Invalidate("explicit-path-changed");

                FfmpegReadinessSnapshot snapshot = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Preparing, snapshot.State,
                    "a retired report must never stay Ready");
                TestKit.CheckEqual(null, snapshot.Report,
                    "the stale report must not be handed out as the current conclusion");
                TestKit.Check(tracker.NeedsScan, "invalidation requests a new scan");
            });

            TestKit.Run("readiness behavior: an in-flight scan is never Ready and never duplicates scans", () =>
            {
                var tracker = new FfmpegReadinessTracker();
                PublishReady(tracker);
                tracker.Invalidate("user-refresh");

                int generation;
                TestKit.Check(tracker.TryBeginScan(out generation), "the refresh scan starts");

                FfmpegReadinessSnapshot snapshot = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Preparing, snapshot.State,
                    "MP4 must not start while a valid scan is in flight");
                TestKit.CheckEqual(FfmpegReadinessReason.Checking, snapshot.ReasonCode, "reason");
                TestKit.Check(snapshot.ScanInFlight, "in flight");
                TestKit.Check(!tracker.NeedsScan,
                    "the GUI may be drawn every frame without starting a second scan");
            });

            TestKit.Run("readiness behavior: a stale scan result never overwrites a newer generation", () =>
            {
                var tracker = new FfmpegReadinessTracker();
                int staleGeneration;
                tracker.TryBeginScan(out staleGeneration);

                // 检查在途期间输入再次变化：旧任务完成时不得发布。
                tracker.Invalidate("ffmpeg-download-settled");

                TestKit.Check(!tracker.TryPublishScan(staleGeneration, ReadyReport()),
                    "the superseded generation must be discarded, not published");

                FfmpegReadinessSnapshot afterDiscard = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Preparing, afterDiscard.State, "state after discard");
                TestKit.CheckEqual(null, afterDiscard.Report, "no stale report may be exposed");
                TestKit.Check(tracker.NeedsScan, "the newer generation still needs its own scan");

                int currentGeneration;
                TestKit.Check(tracker.TryBeginScan(out currentGeneration), "the newer scan starts");
                TestKit.Check(currentGeneration != staleGeneration, "the newer scan binds the newer generation");
                TestKit.Check(tracker.TryPublishScan(currentGeneration, new FfmpegComponentReport
                    {
                        State = FfmpegComponentState.Unsupported,
                    }),
                    "the newer generation publishes its own conclusion");

                FfmpegReadinessSnapshot current = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Failed, current.State,
                    "the newer (non-Ready) conclusion must win over the older Ready result");
                TestKit.CheckEqual("component-Unsupported", current.ReasonCode, "reason code");
            });

            TestKit.Run("readiness behavior: a fault fails closed, keeps no stale Ready, and never auto-retries", () =>
            {
                var tracker = new FfmpegReadinessTracker();
                PublishReady(tracker);
                tracker.Invalidate("user-refresh");
                int generation;
                tracker.TryBeginScan(out generation);

                TestKit.Check(tracker.TryFailScan(generation, FfmpegReadinessReason.InspectionFaulted, "boom"),
                    "the current generation accepts the fault");

                FfmpegReadinessSnapshot faulted = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Failed, faulted.State, "state");
                TestKit.CheckEqual(FfmpegReadinessReason.InspectionFaulted, faulted.ReasonCode, "reason");
                TestKit.CheckEqual("boom", faulted.ReasonDetail, "detail");
                TestKit.CheckEqual(null, faulted.Report, "the pre-fault Ready report must not survive");
                TestKit.Check(!tracker.NeedsScan,
                    "an accepted fault must not auto-retry (otherwise it loops every frame)");

                // 用户可以显式重新检查。
                tracker.Invalidate("user-recheck");
                TestKit.Check(tracker.NeedsScan, "the explicit re-check re-queues exactly once");

                int retryGeneration;
                TestKit.Check(tracker.TryBeginScan(out retryGeneration), "the explicit re-check runs");
                TestKit.Check(tracker.TryPublishScan(retryGeneration, ReadyReport()), "the retry publishes");
                TestKit.CheckEqual(FfmpegReadinessState.Ready, tracker.Snapshot().State,
                    "an explicit retry can restore Ready");
            });

            TestKit.Run("readiness behavior: a stale fault never blocks a newer generation", () =>
            {
                var tracker = new FfmpegReadinessTracker();
                int staleGeneration;
                tracker.TryBeginScan(out staleGeneration);
                tracker.Invalidate("explicit-path-changed");

                TestKit.Check(!tracker.TryFailScan(staleGeneration, FfmpegReadinessReason.InspectionFaulted, "boom"),
                    "a fault from a superseded generation must be discarded");

                FfmpegReadinessSnapshot snapshot = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Preparing, snapshot.State,
                    "a stale fault must not become the current failure state");
                TestKit.Check(tracker.NeedsScan, "the newer generation still scans");
            });

            TestKit.Run("readiness behavior: publishing without an in-flight scan is rejected", () =>
            {
                var tracker = new FfmpegReadinessTracker();
                TestKit.Check(!tracker.TryPublishScan(0, ReadyReport()), "no scan, no publish");

                int generation;
                tracker.TryBeginScan(out generation);
                tracker.TryPublishScan(generation, ReadyReport());
                TestKit.Check(!tracker.TryPublishScan(generation, ReadyReport()),
                    "a completed generation cannot be published twice");
            });

            TestKit.Run("readiness behavior: a null inspection result fails closed instead of parking in Preparing", () =>
            {
                var tracker = new FfmpegReadinessTracker();
                int generation;
                tracker.TryBeginScan(out generation);

                TestKit.Check(!tracker.TryPublishScan(generation, null), "a null result is never published");

                FfmpegReadinessSnapshot snapshot = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Failed, snapshot.State,
                    "an empty inspection result must be an explicit failure, never a silent Preparing");
                TestKit.CheckEqual(FfmpegReadinessReason.InspectionFaulted, snapshot.ReasonCode, "reason");
                TestKit.CheckEqual(null, snapshot.Report, "no report");
                TestKit.Check(!tracker.NeedsScan,
                    "a failed inspection must wait for an explicit re-check instead of spinning");
            });
        }

        // ============================================================ gate

        private static void GateTests()
        {
            TestKit.Run("readiness behavior: PNG and Log-only ignore every FFmpeg state", () =>
            {
                var states = new[]
                {
                    null,
                    Snapshot(FfmpegReadinessState.Preparing, FfmpegReadinessReason.Checking, null),
                    Snapshot(FfmpegReadinessState.Preparing, FfmpegReadinessReason.Pending, null),
                    Snapshot(FfmpegReadinessState.Failed, "component-NotFound", null),
                    Snapshot(FfmpegReadinessState.Failed, FfmpegReadinessReason.InspectionFaulted, "boom", null),
                    Snapshot(FfmpegReadinessState.Ready, FfmpegReadinessReason.None, ReadyReport()),
                };

                for (int i = 0; i < states.Length; i++)
                {
                    string error;
                    string detail;
                    TestKit.Check(Mp4SessionStartup.TryCheckReadiness(
                            CaptureOutputMode.PngSequence, states[i], out error, out detail),
                        "PNG must never depend on FFmpeg readiness (state #" + i + ")");
                    TestKit.CheckEqual(null, error, "PNG produces no rejection code");
                    TestKit.Check(Mp4SessionStartup.TryCheckReadiness(
                            CaptureOutputMode.LogOnly, states[i], out error, out detail),
                        "Log-only must never depend on FFmpeg readiness (state #" + i + ")");
                    TestKit.CheckEqual(null, error, "Log-only produces no rejection code");
                }
            });

            TestKit.Run("readiness behavior: the MP4 gate refuses Preparing and Failed with stable codes", () =>
            {
                string error;
                string detail;

                TestKit.Check(!Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        Snapshot(FfmpegReadinessState.Preparing, FfmpegReadinessReason.Checking, null),
                        out error, out detail),
                    "Preparing must not start MP4");
                TestKit.CheckEqual("mp4-ffmpeg-checking", error, "checking code");

                TestKit.Check(!Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        Snapshot(FfmpegReadinessState.Preparing, FfmpegReadinessReason.Pending, null),
                        out error, out detail),
                    "a queued scan must not start MP4");
                TestKit.CheckEqual("mp4-ffmpeg-pending", error, "pending code");

                TestKit.Check(!Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        Snapshot(FfmpegReadinessState.Failed, "component-NotFound", "ffmpeg-not-found", null),
                        out error, out detail),
                    "an unavailable component must not start MP4");
                TestKit.CheckEqual("mp4-ffmpeg-component-NotFound", error, "component code");
                TestKit.CheckEqual("ffmpeg-not-found", detail, "component detail is preserved");

                TestKit.Check(!Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        Snapshot(FfmpegReadinessState.Failed, FfmpegReadinessReason.InspectionFaulted, "boom", null),
                        out error, out detail),
                    "a faulted inspection must not start MP4");
                TestKit.CheckEqual("mp4-ffmpeg-inspection-faulted", error, "fault code");

                TestKit.Check(!Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        null, out error, out detail),
                    "an unknown readiness must fail closed");
                TestKit.CheckEqual("mp4-ffmpeg-readiness-unknown", error, "unknown code");
            });

            TestKit.Run("readiness behavior: the MP4 gate only accepts a current Ready report", () =>
            {
                string error;
                string detail;

                TestKit.Check(Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        Snapshot(FfmpegReadinessState.Ready, FfmpegReadinessReason.None, ReadyReport()),
                        out error, out detail),
                    "a current Ready report allows MP4: " + error);

                // 条件 2：即使状态自称 Ready，只要有有效的检查在途就不得启动 MP4。
                var inFlight = Snapshot(FfmpegReadinessState.Ready, FfmpegReadinessReason.None, ReadyReport());
                inFlight.ScanInFlight = true;
                TestKit.Check(!Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        inFlight, out error, out detail),
                    "an in-flight scan must block MP4 even with a Ready projection");
                TestKit.CheckEqual("mp4-ffmpeg-checking", error, "in-flight code");

                // 纵深防御：自称 Ready 但拿不出当前 Ready 报告的投影不得启动 MP4。
                TestKit.Check(!Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        Snapshot(FfmpegReadinessState.Ready, FfmpegReadinessReason.None, null),
                        out error, out detail),
                    "Ready without a report must fail closed");
                TestKit.CheckEqual("mp4-ffmpeg-report-not-ready", error, "missing report code");

                TestKit.Check(!Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                        Snapshot(FfmpegReadinessState.Ready, FfmpegReadinessReason.None,
                            new FfmpegComponentReport { State = FfmpegComponentState.Discovered }),
                        out error, out detail),
                    "Ready with a non-Ready report must fail closed");
                TestKit.CheckEqual("mp4-ffmpeg-report-not-ready", error, "non-ready report code");
            });

            TestKit.Run("readiness behavior: the gate agrees with the existing identity freeze on Ready", () =>
            {
                string dir = TestKit.NewWorkDirectory(Path.GetTempPath(), "readiness-freeze");
                try
                {
                    int generation;
                    var tracker = new FfmpegReadinessTracker();
                    tracker.TryBeginScan(out generation);
                    tracker.TryPublishScan(generation, ReadyReport());
                    FfmpegReadinessSnapshot snapshot = tracker.Snapshot();

                    string error;
                    string detail;
                    TestKit.Check(Mp4SessionStartup.TryCheckReadiness(CaptureOutputMode.Mp4Rgb24,
                            snapshot, out error, out detail),
                        "the shared gate must allow what the identity gate allows: " + error);

                    // 既有 MP4 startup 行为（冻结身份 / 唯一最终路径）在 Ready 下仍然通过。
                    var inputs = new Mp4StartupInputs
                    {
                        OutputWidth = 640,
                        OutputHeight = 360,
                        OutputFps = 30,
                        Crf = 18,
                        Preset = "medium",
                        SessionDirectory = dir,
                        Report = snapshot.Report,
                    };
                    Mp4StartupResult frozen = Mp4SessionStartup.Freeze(inputs);
                    TestKit.Check(frozen.Succeeded, "freeze must still succeed: " + frozen.ErrorCode);
                    TestKit.CheckEqual(Sha, frozen.Identity.ExecutableSha256, "identity frozen from the report");
                }
                finally
                {
                    TestKit.TryDeleteDirectory(dir);
                }
            });
        }

        // ============================================================ wiring (structural)

        private static void WiringTests()
        {
            TestKit.Run("readiness wiring: the first inspection starts at Load, never from the GUI", () =>
            {
                string mod = Source("ModEntry.cs");

                string load = Method(mod, "public static bool Load(UnityModManager.ModEntry modEntry)");
                TestKit.Check(load.Contains("TryStartInitialFfmpegInspection()"),
                    "Load must start the process-first inspection");
                int callbacks = load.IndexOf("modEntry.OnGUI = OnGUI;", StringComparison.Ordinal);
                int first = load.IndexOf("TryStartInitialFfmpegInspection();", StringComparison.Ordinal);
                TestKit.Check(callbacks > 0 && first > callbacks,
                    "it must run after Settings load and callback registration");
                TestKit.Check(load.IndexOf("EditorExportController", StringComparison.Ordinal) < 0,
                    "the first inspection must not depend on an editor scene / session");

                string initial = Method(mod, "private static void TryStartInitialFfmpegInspection()");
                TestKit.Check(initial.Contains("RequestFfmpegInspection()"),
                    "the startup helper starts the real inspection");
                TestKit.Check(initial.Contains("catch (Exception ex)"),
                    "a startup failure must never break Mod loading");

                string request = Method(mod, "private static void RequestFfmpegInspection()");
                TestKit.Check(request.Contains("Task.Run("),
                    "the inspection stays asynchronous (Load is never blocked)");
                TestKit.Check(request.Contains("TryBeginScan(out generation)"),
                    "the scan binds the current input generation");

                string gui = Method(mod, "private static void DrawFfmpegComponentGui()");
                TestKit.Check(!gui.Contains("RequestFfmpegInspection"),
                    "drawing the component GUI must not start scans");

                string pump = Method(mod, "private static void PumpFfmpegTasks()");
                TestKit.Check(pump.Contains("_ffmpegReadiness.NeedsScan") &&
                              pump.Contains("RequestFfmpegInspection()"),
                    "the single queueing entry point is the OnUpdate pump (no GUI required)");
                TestKit.Check(pump.Contains("TryPublishScan(") && pump.Contains("TryFailScan("),
                    "the pump decides publish vs discard through the tracker");

                int faultIndex = pump.IndexOf("TryFailScan(", StringComparison.Ordinal);
                string faultBranch = pump.Substring(faultIndex, Math.Min(400, pump.Length - faultIndex));
                TestKit.Check(!faultBranch.Contains("_ffmpegReadiness.Invalidate"),
                    "an accepted fault must not be followed by an automatic invalidation " +
                    "(otherwise it would retry every frame)");
            });

            TestKit.Run("readiness wiring: the controller gates readiness before any session side effect", () =>
            {
                string controller = Source("Export/EditorExportController.cs");
                string start = Method(controller, "private static bool StartSession(");

                int mode = start.IndexOf("TryResolveOutputMode(out outputMode", StringComparison.Ordinal);
                int gate = start.IndexOf("Mp4SessionStartup.TryCheckReadiness(", StringComparison.Ordinal);
                int dir = start.IndexOf("OutputPath.ResolveUniqueSessionDirectory(", StringComparison.Ordinal);
                int metadata = start.IndexOf("session.WriteMetadata();", StringComparison.Ordinal);
                int mp4 = start.IndexOf("TryStartMp4Session(", StringComparison.Ordinal);

                TestKit.Check(mode > 0, "the output mode is resolved in StartSession");
                TestKit.Check(gate > mode, "the readiness gate runs after the MP4 mode is resolved");
                TestKit.Check(dir > gate,
                    "the gate must run before the session directory is created");
                TestKit.Check(metadata > gate,
                    "the gate must run before the initial metadata is written");
                TestKit.Check(mp4 > gate,
                    "the gate must run before the MP4 pipeline / delivery context is created");
                TestKit.Check(start.Contains("ModEntry.GetFfmpegReadiness()"),
                    "the controller reads the same readiness projection as the GUI");
                TestKit.Check(start.Contains("if (!Mp4SessionStartup.TryCheckReadiness("),
                    "a negative verdict must return before creating anything");
                TestKit.Check(start.Contains("LastStartRejectReason = \"FFmpeg 未就绪，MP4 启动被拒绝：\""),
                    "the rejection reason is recorded for the user");

                // terminal re-arm 之后再次进入 StartSession 会重新读取 readiness（没有冻结结论）。
                string rearm = Method(controller, "private static void TickTerminalControllerRearm()");
                TestKit.Check(rearm.Contains("StartSession(settings, report)"),
                    "the re-arm path re-enters StartSession and therefore re-checks readiness");
                TestKit.Check(!controller.Contains("_frozenFfmpegReadiness"),
                    "no readiness conclusion may be frozen across Starts");

                // 不得把该门禁塞进通用 preflight（否则会波及 PNG / Log-only）。
                string preflight = Source("Export/EditorExportPreflight.cs");
                TestKit.Check(!preflight.Contains("Ffmpeg") && !preflight.Contains("TryCheckReadiness"),
                    "the generic preflight must stay FFmpeg-free");
            });

            TestKit.Run("readiness wiring: the GUI MP4 Start gate shares the controller's rule", () =>
            {
                string mod = Source("ModEntry.cs");

                string helper = Method(mod, "private static bool IsMp4StartBlockedByFfmpegReadiness(");
                TestKit.Check(helper.Contains("Mp4SessionStartup.TryCheckReadiness(mode, readiness,"),
                    "the GUI reuses the controller's pure decision function (no second rule)");
                TestKit.Check(helper.Contains("if (!OutputModePolicy.IsMp4(mode))") &&
                              helper.Contains("return false;"),
                    "PNG / Log-only can never be blocked by FFmpeg state");

                string start = Method(mod, "private static void DrawMasterTimelineHandoffGui()");
                TestKit.Check(start.Contains("IsMp4StartBlockedByFfmpegReadiness(out"),
                    "the Start button area consults the shared readiness gate");
                TestKit.Check(start.Contains("&& !mp4Blocked"),
                    "MP4 Start must be disabled while readiness is not Ready");
                TestKit.Check(start.Contains("_ffmpegReadiness.Invalidate(\"mp4-start-recheck\")"),
                    "a failed / preparing state offers an explicit re-check entry");
                TestKit.Check(!start.Contains("System.Threading"),
                    "Start must never wait synchronously for the inspection");

                string componentGui = Method(mod, "private static void DrawFfmpegComponentGui()");
                TestKit.Check(componentGui.Contains("_ffmpegReadiness.Snapshot()"),
                    "the component section renders the same projection");

                TestKit.Check(!mod.Contains("_cachedFfmpegReport"),
                    "the retired cached-report field must be gone");
                TestKit.Check(!mod.Contains("_ffmpegReportDirty"),
                    "the retired dirty flag must be replaced by the tracker");
            });
        }

        // ============================================================ helpers

        private static void PublishReady(FfmpegReadinessTracker tracker)
        {
            int generation;
            TestKit.Check(tracker.TryBeginScan(out generation), "seed scan starts");
            TestKit.Check(tracker.TryPublishScan(generation, ReadyReport()), "seed scan publishes");
        }

        private static FfmpegReadinessSnapshot Snapshot(
            FfmpegReadinessState state, string reasonCode, FfmpegComponentReport report)
        {
            return Snapshot(state, reasonCode, null, report);
        }

        private static FfmpegReadinessSnapshot Snapshot(
            FfmpegReadinessState state, string reasonCode, string reasonDetail, FfmpegComponentReport report)
        {
            return new FfmpegReadinessSnapshot
            {
                State = state,
                ReasonCode = reasonCode,
                ReasonDetail = reasonDetail,
                Report = report,
                Generation = 1,
            };
        }

        private static FfmpegComponentReport ReadyReport()
        {
            string path = Path.Combine(Path.GetTempPath(), "renderist-readiness-ffmpeg.exe");
            return new FfmpegComponentReport
            {
                State = FfmpegComponentState.Ready,
                Source = FfmpegCandidateSource.ManagedInstall,
                Candidate = new FfmpegCandidate
                {
                    Source = FfmpegCandidateSource.ManagedInstall,
                    Identity = new FfmpegBinaryIdentity
                    {
                        AbsolutePath = path,
                        Sha256 = Sha,
                        SizeBytes = 4096,
                    },
                },
                Capability = new FfmpegCapabilityReport
                {
                    Status = FfmpegCapabilityStatus.Probed,
                    ExecutablePath = path,
                    ExecutableSha256 = Sha,
                    VersionLine = "ffmpeg version fixture",
                    HasLibx264 = true,
                    HasMp4Muxer = true,
                    HasRawvideoDemuxer = true,
                    MissingCapabilities = new string[0],
                },
            };
        }

        private static string Source(string relative)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ADOFAI.Renderist")))
                dir = dir.Parent;
            if (dir == null) throw new Exception("repository source not found");
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "ADOFAI.Renderist", relative))
                .Replace("\r\n", "\n");
        }

        private static string Method(string source, string signature)
        {
            int signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
            TestKit.Check(signatureStart >= 0, "method exists: " + signature);
            int start = source.IndexOf('{', signatureStart), depth = 1, end = start + 1;
            while (depth > 0 && end < source.Length)
            {
                if (source[end] == '{') depth++;
                if (source[end] == '}') depth--;
                end++;
            }
            return source.Substring(signatureStart, end - signatureStart);
        }
    }
}
