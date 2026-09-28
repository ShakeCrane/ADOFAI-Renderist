using System;
using System.IO;
using System.Text;
using System.Threading;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// L3-A 最小 MP4 session 启动接线的回归：输出模式 authority / 旧设置迁移 / 冻结参数 /
    /// L1 Ready 身份 gate / 唯一最终路径 / 管线创建与 Start 失败收敛 / 终态验证边界。
    ///
    /// 这些断言全部基于生产源码（Unity-free 模块 + 真实调用点源码契约），
    /// 不模拟 Unity、不启动真实 FFmpeg（真实 fixture 由既有 SKIP 用例承担）。
    /// </summary>
    internal static class Mp4StartupTests
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        public static void Run()
        {
            OutputModeTests();
            FreezeTests();
            BindTests();
            TerminalBoundaryTests();
            ProductionSourceContracts();
        }

        // ---------------------------------------------------------------- output mode

        private static void OutputModeTests()
        {
            TestKit.Run("mp4 startup: legacy image-output flags migrate to PNG and Log-only", () =>
            {
                CaptureOutputMode mode; bool migrated; string error;
                TestKit.Check(OutputModePolicy.TryResolve(OutputModePolicy.UnsetSentinel, true, out mode, out migrated, out error),
                    "unset + legacy true resolves: " + error);
                TestKit.Check(migrated, "unset must report a migration");
                TestKit.CheckEqual(CaptureOutputMode.PngSequence, mode, "legacy true -> PNG");

                TestKit.Check(OutputModePolicy.TryResolve(OutputModePolicy.UnsetSentinel, false, out mode, out migrated, out error),
                    "unset + legacy false resolves: " + error);
                TestKit.Check(migrated, "unset must report a migration");
                TestKit.CheckEqual(CaptureOutputMode.LogOnly, mode, "legacy false -> Log-only");
            });

            TestKit.Run("mp4 startup: an unknown persisted output mode fails closed", () =>
            {
                CaptureOutputMode mode; bool migrated; string error;
                TestKit.Check(!OutputModePolicy.TryResolve(7, true, out mode, out migrated, out error),
                    "an unknown persisted mode must be rejected, never repaired");
                TestKit.Check(error != null && error.StartsWith("output-mode-invalid", StringComparison.Ordinal),
                    "machine readable reason: " + error);
                TestKit.Check(!migrated, "an invalid persisted value is not a migration");
            });

            TestKit.Run("mp4 startup: an explicit persisted mode wins over the legacy flag", () =>
            {
                CaptureOutputMode mode; bool migrated; string error;
                TestKit.Check(OutputModePolicy.TryResolve((int)CaptureOutputMode.Mp4Rgb24, true, out mode, out migrated, out error),
                    "explicit MP4 resolves: " + error);
                TestKit.CheckEqual(CaptureOutputMode.Mp4Rgb24, mode, "explicit mode is the authority");
                TestKit.Check(!migrated, "an explicit mode is not a migration");
            });

            TestKit.Run("mp4 startup: only the MP4 mode selects the frame-transaction path", () =>
            {
                TestKit.Check(OutputModePolicy.IsMp4(CaptureOutputMode.Mp4Rgb24), "MP4 selects it");
                TestKit.Check(!OutputModePolicy.IsMp4(CaptureOutputMode.PngSequence), "PNG never selects it");
                TestKit.Check(!OutputModePolicy.IsMp4(CaptureOutputMode.LogOnly), "Log-only never selects it");
                TestKit.Check(OutputModePolicy.IsImageOutput(CaptureOutputMode.PngSequence), "PNG is image output");
                TestKit.Check(!OutputModePolicy.IsImageOutput(CaptureOutputMode.Mp4Rgb24), "MP4 is not the PNG image-output mode");
                TestKit.Check(!OutputModePolicy.IsImageOutput(CaptureOutputMode.LogOnly), "Log-only is not image output");
                Check("mp4-rgb24", OutputModePolicy.Label(CaptureOutputMode.Mp4Rgb24));
            });

            TestKit.Run("mp4 runtime mode: context cannot define or override the frozen mode", () =>
            {
                string error;
                TestKit.Check(OutputModePolicy.TryValidateRuntimeBinding(CaptureOutputMode.Mp4Rgb24,
                    true, true, out error), "MP4 with an armed context: " + error);
                TestKit.Check(!OutputModePolicy.TryValidateRuntimeBinding(CaptureOutputMode.Mp4Rgb24,
                    false, false, out error) && error == "mp4-delivery-context-missing",
                    "MP4 without context must fail before Play");
                TestKit.Check(!OutputModePolicy.TryValidateRuntimeBinding(CaptureOutputMode.PngSequence,
                    true, true, out error) && error == "non-mp4-delivery-context-present",
                    "a stale context cannot turn PNG into RGB24");
                TestKit.Check(!OutputModePolicy.TryValidateRuntimeBinding(CaptureOutputMode.LogOnly,
                    true, true, out error) && error == "non-mp4-delivery-context-present",
                    "a stale context cannot turn Log-only into RGB24");
                TestKit.Check(OutputModePolicy.TryValidateRuntimeBinding(CaptureOutputMode.PngSequence,
                    false, false, out error), "PNG without context");
                TestKit.Check(OutputModePolicy.TryValidateRuntimeBinding(CaptureOutputMode.LogOnly,
                    false, false, out error), "Log-only without context");
            });
        }

        // ---------------------------------------------------------------- freeze

        private static void FreezeTests()
        {
            TestKit.Run("mp4 startup: frozen settings carry geometry, fps and encoder params", () =>
            {
                string dir = NewDir("mp4-freeze");
                try
                {
                    Mp4StartupResult result = Mp4SessionStartup.Freeze(Inputs(dir, 1920, 1080, 60, 18, "medium"));
                    TestKit.Check(result.Succeeded, "freeze must succeed: " + result.ErrorCode);
                    TestKit.CheckEqual(1920, result.Settings.Width, "width frozen");
                    TestKit.CheckEqual(1080, result.Settings.Height, "height frozen");
                    TestKit.CheckEqual(60, result.Settings.Fps, "fps frozen");
                    TestKit.CheckEqual(18, result.Settings.Crf, "crf frozen");
                    TestKit.CheckEqual("medium", result.Settings.Preset, "preset frozen");
                    // null = L2 按真实几何选择输出像素格式，本层绝不预设、也绝不改写几何。
                    TestKit.CheckEqual(null, result.Settings.PixelFormat, "pixel format stays L2-owned");
                    TestKit.CheckEqual("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                        result.Identity.ExecutableSha256, "identity frozen from the L1 report");
                    TestKit.Check(result.Options != null, "options are assembled for the factory");
                }
                finally { TestKit.TryDeleteDirectory(dir); }
            });

            TestKit.Run("mp4 startup: the final video path is frozen inside the session directory", () =>
            {
                string path; string error;
                string dir = Path.Combine(Path.GetTempPath(), "renderist-session");
                TestKit.Check(Mp4SessionStartup.TryResolveFinalVideoPath(dir, out path, out error), "resolves: " + error);
                string expected = Path.Combine(dir, Mp4SessionStartup.FinalVideoFileName);
                Check(expected, path);
                TestKit.Check(Path.GetFileName(path) == "video.mp4", "single deterministic target name");
            });

            TestKit.Run("mp4 startup: an existing final video is never overwritten", () =>
            {
                string dir = NewDir("mp4-existing");
                try
                {
                    string final = Path.Combine(dir, Mp4SessionStartup.FinalVideoFileName);
                    File.WriteAllBytes(final, new byte[] { 1, 2, 3 });

                    string path; string error;
                    TestKit.Check(!Mp4SessionStartup.TryResolveFinalVideoPath(dir, out path, out error),
                        "an existing final target must be rejected, never overwritten");
                    Check("final-video-exists", error);
                }
                finally { TestKit.TryDeleteDirectory(dir); }
            });

            TestKit.Run("mp4 startup: a missing session directory is rejected", () =>
            {
                string path; string error;
                TestKit.Check(!Mp4SessionStartup.TryResolveFinalVideoPath(null, out path, out error), "null dir");
                Check("session-directory-missing", error);
                TestKit.Check(!Mp4SessionStartup.TryResolveFinalVideoPath("   ", out path, out error), "blank dir");
            });

            TestKit.Run("mp4 startup: structural parameter errors fail closed before any process", () =>
            {
                string dir = NewDir("mp4-params");
                try
                {
                    Check("mp4-geometry-invalid", Mp4SessionStartup.Freeze(Inputs(dir, 0, 1080, 60, 18, "medium")).ErrorCode);
                    Check("mp4-geometry-invalid", Mp4SessionStartup.Freeze(Inputs(dir, 1920, -1, 60, 18, "medium")).ErrorCode);
                    Check("mp4-fps-invalid", Mp4SessionStartup.Freeze(Inputs(dir, 1920, 1080, 0, 18, "medium")).ErrorCode);
                    Check("mp4-crf-invalid", Mp4SessionStartup.Freeze(Inputs(dir, 1920, 1080, 60, -1, "medium")).ErrorCode);
                    Check("mp4-preset-invalid", Mp4SessionStartup.Freeze(Inputs(dir, 1920, 1080, 60, 18, " ")).ErrorCode);
                }
                finally { TestKit.TryDeleteDirectory(dir); }
            });

            TestKit.Run("mp4 startup: a non-Ready L1 report fails closed at the identity gate", () =>
            {
                string dir = NewDir("mp4-identity");
                try
                {
                    Mp4StartupInputs inputs = Inputs(dir, 1920, 1080, 60, 18, "medium");
                    inputs.Report = ReadyReport();
                    inputs.Report.State = FfmpegComponentState.Discovered;

                    Mp4StartupResult result = Mp4SessionStartup.Freeze(inputs);
                    TestKit.Check(!result.Succeeded, "a non-Ready component must not start a session");
                    TestKit.Check(result.ErrorCode != null &&
                                  result.ErrorCode.StartsWith("mp4-identity:component-not-ready", StringComparison.Ordinal),
                        "state must be reported: " + result.ErrorCode);
                }
                finally { TestKit.TryDeleteDirectory(dir); }
            });
        }

        // ---------------------------------------------------------------- bind

        private static void BindTests()
        {
            TestKit.Run("mp4 startup: a pipeline creation failure fails closed without starting anything", () =>
            {
                string dir = NewDir("mp4-create");
                try
                {
                    Mp4StartupResult frozen = Mp4SessionStartup.Freeze(Inputs(dir, 640, 360, 30, 18, "medium"));
                    var factory = new FakePipelineFactory { CreateResult = false, CreateError = "identity-hash-changed" };

                    Mp4SessionBinding binding = Mp4SessionStartup.Bind(
                        frozen, 640, 360, factory, new FakeDeliveryFactory());

                    TestKit.Check(!binding.Succeeded, "must fail closed");
                    Check("mp4-pipeline-create-failed:identity-hash-changed", binding.ErrorCode);
                    TestKit.CheckEqual(0, factory.StartCalls, "no process may be started");
                    TestKit.CheckEqual(null, binding.PendingConvergence, "nothing to converge");
                }
                finally { TestKit.TryDeleteDirectory(dir); }
            });

            TestKit.Run("mp4 startup: a delivery context failure fails closed before the process starts", () =>
            {
                string dir = NewDir("mp4-context");
                try
                {
                    Mp4StartupResult frozen = Mp4SessionStartup.Freeze(Inputs(dir, 640, 360, 30, 18, "medium"));
                    var factory = new FakePipelineFactory();
                    var deliveries = new FakeDeliveryFactory { CreateResult = false, CreateError = "rgb24-width-invalid" };

                    Mp4SessionBinding binding = Mp4SessionStartup.Bind(frozen, 640, 360, factory, deliveries);

                    TestKit.Check(!binding.Succeeded, "must fail closed");
                    Check("mp4-context-create-failed:rgb24-width-invalid", binding.ErrorCode);
                    TestKit.CheckEqual(0, factory.StartCalls, "context是 Start 之前建立的前提，失败时不得启动进程");
                }
                finally { TestKit.TryDeleteDirectory(dir); }
            });

            TestKit.Run("mp4 startup: an abandoned context is never dropped before its cleanup converges", () =>
            {
                string dir = NewDir("mp4-startfail");
                SynchronizationContext previous = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
                    Mp4StartupResult frozen = Mp4SessionStartup.Freeze(Inputs(dir, 640, 360, 30, 18, "medium"));
                    var factory = new FakePipelineFactory { StartResult = false, StartError = "component-not-ready" };

                    Mp4SessionBinding binding = Mp4SessionStartup.Bind(
                        frozen, 640, 360, factory, DefaultMp4DeliveryFactory.Instance);

                    TestKit.Check(!binding.Succeeded, "must fail closed");
                    Check("mp4-pipeline-start-failed:component-not-ready", binding.ErrorCode);
                    TestKit.CheckEqual(1, factory.StartCalls, "start was attempted exactly once");

                    // 假管线未启动进程，Cancel 可在 Bind 返回前后任一时刻完成。
                    // 只有仍持有资源时才必须保留 context；已完成则允许立即退休。
                    if (binding.PendingConvergence != null)
                    {
                        // Cleanup 可以恰好在 Bind 返回与断言之间完成；此时保留的引用
                        // 由主线程下一次收敛入口清除，不能把时序变化误报为 ownership 丢失。
                        TestKit.Check(binding.PendingConvergence.HasResidualOwnership ||
                            (factory.LastCreated.CleanupTask != null &&
                             factory.LastCreated.CleanupTask.IsCompleted &&
                             !factory.LastCreated.CleanupTask.Result.ResidualOwnership),
                            "pending context is either still owned or already safely converged");
                    }
                    else
                        TestKit.Check(factory.LastCreated.CleanupTask != null &&
                            factory.LastCreated.CleanupTask.IsCompleted &&
                            !factory.LastCreated.CleanupTask.Result.ResidualOwnership,
                            "the fake pipeline has no started process and may converge immediately");
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                    TestKit.TryDeleteDirectory(dir);
                }
            });

            TestKit.Run("mp4 startup: a successful bind returns the started pipeline and its context", () =>
            {
                string dir = NewDir("mp4-bind");
                SynchronizationContext previous = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
                    Mp4StartupResult frozen = Mp4SessionStartup.Freeze(Inputs(dir, 640, 360, 30, 18, "medium"));
                    var factory = new FakePipelineFactory();

                    Mp4SessionBinding binding = Mp4SessionStartup.Bind(
                        frozen, 640, 360, factory, DefaultMp4DeliveryFactory.Instance);

                    TestKit.Check(binding.Succeeded, "bind must succeed: " + binding.ErrorCode);
                    TestKit.CheckEqual(1, factory.StartCalls, "start exactly once");
                    TestKit.Check(ReferenceEquals(factory.LastCreated, binding.Pipeline),
                        "the bound pipeline is the one the factory created");
                    TestKit.Check(binding.Context != null && binding.Context.Bridge.IsMainThread,
                        "context is bound to the calling (main) thread");
                    TestKit.CheckEqual(640, binding.Context.Layout.Width, "frozen output width");
                    TestKit.CheckEqual(360, binding.Context.Layout.Height, "frozen output height");
                    TestKit.CheckEqual(640L * 360L * 3L, binding.Context.Layout.ByteLength, "exact long frame length");
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                    TestKit.TryDeleteDirectory(dir);
                }
            });
        }

        // ---------------------------------------------------------------- terminal boundary

        private static void TerminalBoundaryTests()
        {
            TestKit.Run("mp4 boundary: an MP4 session can never be mapped to Completed without Finalizing", () =>
            {
                TestKit.Check(!Mp4TerminalBoundaryPolicy.CanMapToCompleted(CaptureOutputMode.Mp4Rgb24),
                    "L3-A has no Finalizing, so the terminal boundary must fail closed");
                TestKit.Check(Mp4TerminalBoundaryPolicy.CanMapToCompleted(CaptureOutputMode.PngSequence),
                    "PNG keeps the existing Completed mapping");
                TestKit.Check(Mp4TerminalBoundaryPolicy.CanMapToCompleted(CaptureOutputMode.LogOnly),
                    "Log-only keeps the existing Completed mapping");
                TestKit.Check(Mp4TerminalBoundaryPolicy.ValidationBoundaryStopReason.Contains("no-finalizing"),
                    "the stop reason names the missing Finalizing step");
            });
        }

        // ---------------------------------------------------------------- production wiring

        private static void ProductionSourceContracts()
        {
            TestKit.Run("mp4 wiring: the controller gates the MP4 chain on the frozen mode before Play", () =>
            {
                string start = Method(Source("Export/EditorExportController.cs"),
                    "private static bool StartSession(");

                TestKit.Check(start.Contains("if (OutputModePolicy.IsMp4(outputMode))"),
                    "the MP4 chain is gated on the frozen output mode");
                int gate = start.IndexOf("if (OutputModePolicy.IsMp4(outputMode))", StringComparison.Ordinal);
                int play = start.IndexOf("DeterministicFrameScheduler.TryStart(", StringComparison.Ordinal);
                TestKit.Check(gate > 0 && play > gate,
                    "pipeline/context must be ready before the single editor.Play() path");
                TestKit.Check(start.Contains("geometryInput, outputMode, false)"),
                    "the explicit frozen mode is passed to scheduler");
                string scheduler = Source("Export/DeterministicFrameScheduler.cs");
                TestKit.Check(scheduler.Contains("TryValidateRuntimeBinding(outputMode, _rgb24DeliveryArmed,") &&
                    scheduler.Contains("_rgb24DeliveryEnabled = OutputModePolicy.IsMp4(outputMode)"),
                    "scheduler validates the resource and derives RGB24 from mode");
                TestKit.Check(scheduler.Contains("OnCaptureResult, _frozenOutputMode,"),
                    "driver receives the explicit frozen mode");
                string session = Source("Export/EditorExportSession.cs");
                TestKit.Check(session.Contains("Mode => OutputModeKind == CaptureOutputMode.PngSequence") &&
                    session.Contains("public string OutputMode => OutputModePolicy.Label(OutputModeKind)"),
                    "metadata mode and outputMode share the frozen enum");
                TestKit.Check(start.Contains("TryResolveOutputMode(out outputMode, out modeMigrated"),
                    "the mode is resolved once per session");
                TestKit.Check(start.Contains("FinalizingNotImplemented = OutputModePolicy.IsMp4(outputMode)"),
                    "metadata records that Finalizing was never implemented for this session");
            });

            TestKit.Run("mp4 wiring: the MP4 startup helper freezes, binds, starts and arms in order", () =>
            {
                string helper = Method(Source("Export/EditorExportController.cs"),
                    "private static bool TryStartMp4Session(");

                int freeze = helper.IndexOf("Mp4SessionStartup.Freeze(", StringComparison.Ordinal);
                int bind = helper.IndexOf("Mp4SessionStartup.Bind(", StringComparison.Ordinal);
                int arm = helper.IndexOf("ArmRgb24Delivery(", StringComparison.Ordinal);
                TestKit.Check(freeze > 0 && bind > freeze && arm > bind,
                    "freeze -> bind (create/context/start) -> Arm");
                TestKit.Check(helper.Contains("ModEntry.TryGetFfmpegComponentReport(out report)"),
                    "L1 readiness comes from the existing component report, never re-discovered");
                TestKit.Check(helper.Contains("_startupContext = Mp4SessionStartup.RetireOrKeep(binding.Context)"),
                    "an Arm failure keeps the context for residual convergence");
                TestKit.Check(!helper.Contains("new FfmpegVideoPipeline("),
                    "the startup path never constructs a second process owner directly");
            });

            TestKit.Run("mp4 wiring: a failed startup leftover blocks restart and converges from Tick", () =>
            {
                string controller = Source("Export/EditorExportController.cs");
                TestKit.Check(controller.Contains("_terminalRearmPending || _startupContext != null ||"),
                    "IsBusy includes the startup convergence slot");
                string tick = Method(controller, "public static void Tick()");
                TestKit.Check(tick.IndexOf("TickStartupContextConvergence()") < tick.IndexOf("TickTerminalControllerRearm()"),
                    "Tick drains the startup slot before doing anything else");
                string retire = Method(controller, "private static void TickStartupContextConvergence()");
                TestKit.Check(retire.Contains("context.TryStopAndDrain("),
                    "convergence reuses the tested L2 Cancel + CleanupTask path");
                TestKit.Check(retire.IndexOf("_startupContext = null;") > retire.IndexOf("TryStopAndDrain("),
                    "the slot is only cleared after convergence succeeded");
            });

            TestKit.Run("mp4 wiring: the terminal boundary fails closed instead of showing a successful MP4", () =>
            {
                string finalize = Method(Source("Export/EditorExportController.cs"),
                    "private static void FinalizeFromScheduler()");
                TestKit.Check(finalize.Contains("Mp4TerminalBoundaryPolicy.CanMapToCompleted(_frozenOutputMode)"),
                    "Completed mapping is gated by the L3-A boundary policy");
                TestKit.Check(finalize.Contains("s.State = EditorExportState.Failed;"),
                    "an MP4 session at the boundary is recorded as Failed, never Completed");
                TestKit.Check(finalize.Contains("Mp4TerminalBoundaryPolicy.ValidationBoundaryDetail"),
                    "the user-visible detail states this is an L3-A validation boundary");
                TestKit.Check(finalize.Contains("Log.Warn("),
                    "the boundary is logged explicitly");
            });

            TestKit.Run("mp4 wiring: settings keep a single output-mode authority", () =>
            {
                string settings = Source("Settings.cs");
                TestKit.Check(settings.Contains("public int EditorOutputModeValue = OutputModePolicy.UnsetSentinel;"),
                    "persisted mode with an explicit unset sentinel");
                TestKit.Check(settings.Contains("OutputModePolicy.TryResolve("),
                    "resolution goes through the tested policy");
                TestKit.Check(!settings.Contains("EditorImageOutputEnabled = !"),
                    "the legacy flag is never toggled as an independent authority");
            });
        }

        // ---------------------------------------------------------------- helpers

        private static Mp4StartupInputs Inputs(string dir, int w, int h, int fps, int crf, string preset)
        {
            return new Mp4StartupInputs
            {
                OutputWidth = w,
                OutputHeight = h,
                OutputFps = fps,
                Crf = crf,
                Preset = preset,
                SessionDirectory = dir,
                Report = ReadyReport(),
            };
        }

        private static FfmpegComponentReport ReadyReport()
        {
            string path = Path.Combine(Path.GetTempPath(), "renderist-fixture-ffmpeg.exe");
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

        private static string NewDir(string label)
        {
            return TestKit.NewWorkDirectory(Path.GetTempPath(), label);
        }

        private static void Check(object expected, object actual)
        {
            TestKit.CheckEqual(expected, actual, "value");
        }

        private sealed class FakePipelineFactory : IMp4PipelineFactory
        {
            internal bool CreateResult = true;
            internal string CreateError;
            internal bool StartResult = true;
            internal string StartError;
            internal int StartCalls;
            internal FfmpegVideoPipeline LastCreated;

            public FfmpegVideoPipeline Create(
                FfmpegVideoPipelineOptions options, out string errorCode, out string errorDetail)
            {
                errorCode = CreateError;
                errorDetail = null;
                if (!CreateResult) return null;
                LastCreated = new FfmpegVideoPipeline(options);
                return LastCreated;
            }

            public bool Start(FfmpegVideoPipeline pipeline, out string errorCode, out string errorDetail)
            {
                StartCalls++;
                errorCode = StartError;
                errorDetail = null;
                return StartResult;
            }
        }

        private sealed class FakeDeliveryFactory : IMp4DeliveryFactory
        {
            internal bool CreateResult = true;
            internal string CreateError;

            public bool TryCreate(int width, int height, FfmpegVideoPipeline pipeline,
                out Rgb24DeliveryContext context, out string error)
            {
                error = CreateError;
                context = null;
                // 真实 context 需要 Unity 主线程上下文；本假实现只用于断言"失败时不启动进程"。
                return CreateResult;
            }
        }

        private sealed class InlineSyncContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state)
            {
                d(state);
            }
        }

        private static string Source(string relative)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ADOFAI.Renderist"))) dir = dir.Parent;
            if (dir == null) throw new Exception("repository source not found");
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "ADOFAI.Renderist", relative)).Replace("\r\n", "\n");
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
