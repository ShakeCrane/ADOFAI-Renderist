using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// L3-C —— MP4 Finalizing（FFmpeg Finish → 独立核验 → 同卷原子发布 → Completed）的回归。
    ///
    /// 覆盖三层：
    ///   1. **判决策略**（Unity-free）：只有 L2 Completed + 核验通过 + 最终路径一致才算产出；
    ///   2. **ownership 交接 / 完成关口闸门**：一次性、主线程、前提不成立即 fail-closed；
    ///   3. **跨层端到端**：真实 <see cref="FfmpegVideoPipeline"/> + 假进程（真子进程）+
    ///      真实 <see cref="Rgb24DeliveryContext"/> / transaction / transport，验证
    ///      "帧交付 → 交接 → 发布"、"核验失败不发布"、"取消不发布"。
    ///
    /// 生产接线（scheduler 交接顺序 / controller ownership / 无第二 process owner）以源码契约固定，
    /// 因为这些类型依赖 Unity 而无法在 net48 harness 中执行。
    /// </summary>
    internal static class Mp4FinalizingTests
    {
        private const int TimeoutMs = 120000;

        public static void Run(string workRoot)
        {
            PolicyTests();
            GateTests();
            EndToEndTests(workRoot);
            HardeningTests(workRoot);
            ProductionSourceContracts();
        }

        // ================================================================ policy

        private static void PolicyTests()
        {
            TestKit.Run("finalizing policy: only a verified Completed outcome publishes", () =>
            {
                string finalPath = Path.Combine(Path.GetTempPath(), "renderist-policy-video.mp4");
                Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(
                    Completed(finalPath, Verified()), finalPath);

                TestKit.CheckEqual(Mp4FinalizationVerdict.Published, verdict.Verdict, "verdict");
                TestKit.Check(verdict.IsPublished, "published");
                TestKit.CheckEqual(finalPath, verdict.FinalPath, "published path");
                TestKit.CheckEqual("Verified", verdict.VerificationStatus, "verification status");
                TestKit.CheckEqual(7L, verdict.DecodedFrameCount, "decoded frames");
                TestKit.CheckEqual(7L, verdict.PacketCount, "container packets");
                TestKit.CheckEqual("h264", verdict.CodecName, "codec");
                TestKit.CheckEqual("yuv420p", verdict.PixelFormat, "pixel format");
                TestKit.Check(!verdict.ResidualOwnership, "no residual");
                TestKit.CheckEqual(Mp4FinalizationPolicy.PublishedLabel,
                    Mp4FinalizationPolicy.Label(verdict.Verdict), "metadata label");
            });

            TestKit.Run("finalizing policy: an unverified Completed outcome is never a success", () =>
            {
                string finalPath = Path.Combine(Path.GetTempPath(), "renderist-policy-video.mp4");

                foreach (FfmpegVideoVerificationResult evidence in new[] { null, FailedVerification() })
                {
                    Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(
                        Completed(finalPath, evidence), finalPath);
                    TestKit.CheckEqual(Mp4FinalizationVerdict.Failed, verdict.Verdict,
                        "an outcome without a passing verification must fail closed");
                    TestKit.CheckEqual(null, verdict.FinalPath, "no published path may be reported");
                    TestKit.CheckEqual(Mp4FinalizationPolicy.UnverifiedOutcomeError, verdict.ErrorCode,
                        "machine readable reason");
                }
            });

            TestKit.Run("finalizing policy: a differing published path fails closed", () =>
            {
                Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(
                    Completed(@"C:\somewhere\else.mp4", Verified()), @"C:\session\video.mp4");

                TestKit.CheckEqual(Mp4FinalizationVerdict.Failed, verdict.Verdict, "verdict");
                TestKit.CheckEqual(Mp4FinalizationPolicy.FinalPathMismatchError, verdict.ErrorCode, "error code");
                TestKit.CheckEqual(null, verdict.FinalPath, "no path");
            });

            TestKit.Run("finalizing policy: cancelled, failed and missing outcomes never publish", () =>
            {
                Mp4FinalizationOutcome cancelled = Mp4FinalizationPolicy.Map(
                    new FfmpegVideoOutcome { State = FfmpegVideoPipelineState.Cancelled, Reason = "user-stop" }, null);
                TestKit.CheckEqual(Mp4FinalizationVerdict.Cancelled, cancelled.Verdict, "cancelled verdict");
                TestKit.CheckEqual("user-stop", cancelled.ErrorCode, "cancel reason");
                TestKit.CheckEqual(null, cancelled.FinalPath, "cancelled must not report a path");
                TestKit.CheckEqual(Mp4FinalizationPolicy.CancelledLabel,
                    Mp4FinalizationPolicy.Label(cancelled.Verdict), "cancelled label");

                Mp4FinalizationOutcome failed = Mp4FinalizationPolicy.Map(
                    new FfmpegVideoOutcome
                    {
                        State = FfmpegVideoPipelineState.Failed,
                        ErrorCode = "verification-failed",
                        ErrorDetail = "verify-frame-count-mismatch",
                    }, null);
                TestKit.CheckEqual(Mp4FinalizationVerdict.Failed, failed.Verdict, "failed verdict");
                TestKit.CheckEqual("verification-failed", failed.ErrorCode, "failure code");

                Mp4FinalizationOutcome missing = Mp4FinalizationPolicy.Map(null, null);
                TestKit.CheckEqual(Mp4FinalizationVerdict.Failed, missing.Verdict, "null outcome fails closed");
                TestKit.CheckEqual(Mp4FinalizationPolicy.MissingOutcomeError, missing.ErrorCode, "null outcome code");

                Mp4FinalizationOutcome faulted = Mp4FinalizationPolicy.Faulted("boom");
                TestKit.CheckEqual(Mp4FinalizationVerdict.Failed, faulted.Verdict, "a faulted task is never a success");
                TestKit.CheckEqual(Mp4FinalizationPolicy.FaultedError, faulted.ErrorCode, "faulted code");

                Mp4FinalizationOutcome taskCancelled = Mp4FinalizationPolicy.CancelledTask("nope");
                TestKit.CheckEqual(Mp4FinalizationVerdict.Cancelled, taskCancelled.Verdict, "cancelled task");
                TestKit.CheckEqual(null, taskCancelled.FinalPath, "cancelled task must not report a path");
            });

            TestKit.Run("finalizing policy: residual ownership is reported, never hidden", () =>
            {
                var outcome = Completed(Path.Combine(Path.GetTempPath(), "video.mp4"), Verified());
                outcome.ResidualOwnership = true;
                outcome.ResidualDetail = "process-not-reaped";

                Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(outcome, null);
                TestKit.CheckEqual(Mp4FinalizationVerdict.Published, verdict.Verdict,
                    "residual evidence does not change a verified publish");
                TestKit.Check(verdict.ResidualOwnership, "residual must stay visible");
                TestKit.CheckEqual("process-not-reaped", verdict.ResidualDetail, "residual detail");
            });
        }

        private static FfmpegVideoOutcome Completed(string finalPath, FfmpegVideoVerificationResult verification)
        {
            return new FfmpegVideoOutcome
            {
                State = FfmpegVideoPipelineState.Completed,
                FinalPath = finalPath,
                TempPath = finalPath + ".partial",
                DeliveredFrameCount = 7,
                EncoderExitCode = 0,
                ProcessStarted = true,
                ProcessReaped = true,
                ProcessDisposed = true,
                TempOwnershipReleased = true,
                TempFileRemoved = true,
                Verification = verification,
            };
        }

        private static FfmpegVideoVerificationResult Verified()
        {
            return new FfmpegVideoVerificationResult
            {
                Status = FfmpegVideoVerificationStatus.Verified,
                DecodedFrameCount = 7,
                ContainerPacketCount = 7,
                DecodedWidth = 64,
                DecodedHeight = 48,
                CodecName = "h264",
                PixelFormat = "yuv420p",
                HasTimeBase = true,
                TimeBaseNumerator = 1,
                TimeBaseDenominator = 30,
                HasContainerTimeBase = true,
                ContainerTimeBaseNumerator = 1,
                ContainerTimeBaseDenominator = 30,
            };
        }

        private static FfmpegVideoVerificationResult FailedVerification()
        {
            return new FfmpegVideoVerificationResult
            {
                Status = FfmpegVideoVerificationStatus.Failed,
                ErrorCode = "verify-frame-count-mismatch",
                ErrorDetail = "expected=7 actual=6",
            };
        }

        // ================================================================ gate

        private static void GateTests()
        {
            TestKit.Run("finalizing gate: a context finalizes once, on the main thread only", () =>
            {
                WithMainThreadContext(() =>
                {
                    var lifetime = new FakeLifetime { Delivered = 5 };
                    Rgb24DeliveryContext context = NewContext(lifetime);

                    Rgb24FinalizeStart first = context.TryBeginFinalizing(5);
                    TestKit.Check(first.Started, "the first attempt must be accepted: " + first.ErrorCode);
                    TestKit.Check(context.FinalizeStarted, "context records the one-shot gate");
                    TestKit.Check(ReferenceEquals(lifetime.LastFinalizeTask, first.Completion),
                        "the accepted completion is exactly the L2 finish task");

                    Rgb24FinalizeStart second = context.TryBeginFinalizing(5);
                    TestKit.Check(!second.Started, "a second attempt must be refused");
                    TestKit.CheckEqual("rgb24-finalize-already-started", second.ErrorCode, "error code");
                });
            });

            TestKit.Run("finalizing gate: a rejected finish falls back to the cancel path", () =>
            {
                WithMainThreadContext(() =>
                {
                    var lifetime = new FakeLifetime { PublishCleanupTask = false, Delivered = 5 };
                    Rgb24DeliveryContext context = NewContext(lifetime);

                    Rgb24FinalizeStart result = context.TryBeginFinalizing(5);
                    TestKit.Check(!result.Started, "a finish without a convergence task must be refused");
                    TestKit.CheckEqual("rgb24-finalize-rejected", result.ErrorCode, "error code");
                    TestKit.CheckEqual(1, lifetime.StopRequests,
                        "refusing must request cancellation so the context can still be reclaimed");
                    TestKit.Check(!context.FinalizeStarted, "a refused attempt is not recorded as started");
                });

                WithMainThreadContext(() =>
                {
                    var lifetime = new FakeLifetime { FinalizeAvailable = false, Delivered = 5 };
                    Rgb24DeliveryContext context = NewContext(lifetime);

                    Rgb24FinalizeStart result = context.TryBeginFinalizing(5);
                    TestKit.Check(!result.Started, "an unavailable finish must be refused");
                    TestKit.CheckEqual("rgb24-finalize-unavailable", result.ErrorCode, "error code");
                    TestKit.CheckEqual(1, lifetime.StopRequests, "unavailable must also fall back to cancel");
                });
            });

            TestKit.Run("finalizing gate: frame ownership, stopping and wrong thread all fail closed", () =>
            {
                WithMainThreadContext(() =>
                {
                    var lifetime = new FakeLifetime { Delivered = 5 };
                    Rgb24DeliveryContext context = NewContext(lifetime);

                    Rgb24TransactionOutcome begin = context.Transaction.TryBeginFrame(3, 0, out string beginError);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, begin, "begin frame: " + beginError);

                    Rgb24FinalizeStart owned = context.TryBeginFinalizing(5);
                    TestKit.Check(!owned.Started, "frame ownership must block the handoff");
                    TestKit.CheckEqual("rgb24-frame-ownership-residual", owned.ErrorCode, "error code");
                    TestKit.Check(context.Transaction.TryAbort(out _), "abort the lease");
                });

                WithMainThreadContext(() =>
                {
                    var lifetime = new FakeLifetime { Delivered = 5 };
                    Rgb24DeliveryContext context = NewContext(lifetime);

                    string stopError;
                    TestKit.Check(context.TryRequestStop("user-stop", out stopError), "stop: " + stopError);
                    TestKit.CheckEqual("user-stop", lifetime.LastStopReason, "the stop reason is forwarded");

                    Rgb24FinalizeStart stopped = context.TryBeginFinalizing(5);
                    TestKit.Check(!stopped.Started, "a stopping context must not finalize");
                    TestKit.CheckEqual("rgb24-context-stopping", stopped.ErrorCode, "error code");
                });
            });

            TestKit.Run("finalizing gate: the frame-count invariant is checked before FinishAsync", () =>
            {
                WithMainThreadContext(() =>
                {
                    // 交付计数必须与 session 的逻辑提交数精确相等；不一致时**不得**开始完成关口。
                    var lifetime = new FakeLifetime { Delivered = 4 };
                    Rgb24DeliveryContext context = NewContext(lifetime);

                    Rgb24FinalizeStart mismatch = context.TryBeginFinalizing(5);
                    TestKit.Check(!mismatch.Started, "a delivered/expected mismatch must be refused");
                    TestKit.CheckEqual("rgb24-finalize-frame-count-mismatch", mismatch.ErrorCode, "error code");
                    TestKit.Check(mismatch.ErrorDetail != null &&
                                  mismatch.ErrorDetail.Contains("delivered=4") &&
                                  mismatch.ErrorDetail.Contains("expected=5"),
                        "both counts must be reported: " + mismatch.ErrorDetail);
                    TestKit.CheckEqual(null, lifetime.LastFinalizeTask,
                        "FinishAsync must not even be attempted on a mismatch");
                    TestKit.CheckEqual(1, lifetime.StopRequests,
                        "a mismatch is fail-closed: request cancellation instead of publishing");
                    TestKit.Check(!context.FinalizeStarted, "a refused attempt is not recorded as started");
                });

                WithMainThreadContext(() =>
                {
                    var lifetime = new FakeLifetime { Delivered = 0 };
                    Rgb24DeliveryContext context = NewContext(lifetime);

                    Rgb24FinalizeStart empty = context.TryBeginFinalizing(0);
                    TestKit.Check(!empty.Started, "an empty session must never finalize");
                    TestKit.CheckEqual("rgb24-finalize-frame-count-invalid", empty.ErrorCode, "error code");
                    TestKit.CheckEqual(null, lifetime.LastFinalizeTask, "no finish attempt");
                });
            });
        }

        private sealed class FakeLifetime : IRgb24FrameTransport, IRgb24PipelineLifetime
        {
            private readonly TaskCompletionSource<FfmpegVideoOutcome> _cleanup =
                new TaskCompletionSource<FfmpegVideoOutcome>();
            private readonly TaskCompletionSource<FfmpegVideoOutcome> _finalize =
                new TaskCompletionSource<FfmpegVideoOutcome>();

            internal bool FinalizeAvailable = true;
            internal bool PublishCleanupTask = true;
            internal int StopRequests;
            internal string LastStopReason;
            internal Task<FfmpegVideoOutcome> LastFinalizeTask;
            internal long Delivered;

            public Task<FfmpegVideoOutcome> CleanupTask => PublishCleanupTask ? _cleanup.Task : null;

            public long DeliveredFrameCount => Delivered;

            public Task<FfmpegVideoOutcome> BeginFinalize()
            {
                LastFinalizeTask = FinalizeAvailable ? _finalize.Task : null;
                return LastFinalizeTask;
            }

            public void RequestStop(string reason)
            {
                StopRequests++;
                LastStopReason = reason;
            }

            public Rgb24TransportAttempt TryBeginWrite(OwnedRgb24Frame frame)
            {
                return Rgb24TransportAttempt.Rejected("not-running", null);
            }
        }

        // ================================================================ end to end

        private static void EndToEndTests(string workRoot)
        {
            TestKit.Run("finalizing e2e: a fully delivered session publishes only through the same context", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-publish");
                WithPumpedMainThreadContext(pump =>
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        Encoder = new FakeVideoEncoderScript { OutputBytes = 512 },
                        Verifier = new FakeVideoVerifierScript { Frames = 2, Width = 64, Height = 48, Fps = 30 },
                    };

                    FfmpegVideoPipeline pipeline = CreatePipeline(directory, launcher.Create());
                    TestKit.Check(pipeline.Start().Started, "pipeline start");

                    Rgb24DeliveryContext context = NewContext(pipeline);
                    DeliverFrame(context, pump, 1, 0);
                    DeliverFrame(context, pump, 1, 1);
                    TestKit.CheckEqual(2L, pipeline.DeliveredFrameCount, "delivered frames");
                    TestKit.Check(!context.HasFrameOwnership, "no frame ownership is left at completion");

                    Rgb24FinalizeStart start = context.TryBeginFinalizing(2);
                    TestKit.Check(start.Started, "finalize must be accepted: " + start.ErrorCode);
                    TestKit.Check(ReferenceEquals(start.Completion, pipeline.CleanupTask),
                        "finalizing must reuse the single L2 convergence task (no second state machine)");

                    FfmpegVideoOutcome outcome = Await(start.Completion, "finalize");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                        "state: " + outcome.ErrorCode + " " + outcome.ErrorDetail);

                    string finalPath = Path.Combine(directory, "video.mp4");
                    Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(outcome, finalPath);
                    TestKit.CheckEqual(Mp4FinalizationVerdict.Published, verdict.Verdict,
                        "verdict: " + Mp4FinalizationPolicy.Describe(verdict));
                    TestKit.Check(File.Exists(finalPath), "the published MP4 must exist");
                    TestKit.Check(pipeline.TempPath == null || !File.Exists(pipeline.TempPath),
                        "the temp product must be gone after publish");
                    TestKit.CheckEqual(2L, verdict.DecodedFrameCount, "verified decode count matches delivery");
                    TestKit.CheckEqual(2L, verdict.PacketCount, "verified container packet count");
                    TestKit.CheckEqual(1, launcher.EncoderStarts, "exactly one encoder process");
                    TestKit.CheckEqual(2, launcher.VerifierStarts, "verification really ran");

                    // 判决之后仍必须能收敛：IsBusy 才会解除。
                    string stopError;
                    TestKit.Check(context.TryStopAndDrain("mp4-finalizing-complete", out stopError),
                        "the context must converge after publish: " + stopError);
                    TestKit.Check(!context.HasResidualOwnership, "no residual ownership");

                    pipeline.Dispose();
                });
                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("finalizing fixture: a real FFmpeg publishes through the delivered RGB24 context", () =>
            {
                FfmpegVideoIdentity identity = RealFixtureIdentity();
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-real");
                WithPumpedMainThreadContext(pump =>
                {
                    // 真实编码器 + 真实核验器：完整走一遍 L3-C 主链。
                    FfmpegVideoPipeline pipeline = CreatePipeline(directory, null, identity);
                    TestKit.Check(pipeline.Start().Started, "real pipeline start");

                    Rgb24DeliveryContext context = NewContext(pipeline);
                    DeliverFrame(context, pump, 1, 0);
                    DeliverFrame(context, pump, 1, 1);
                    DeliverFrame(context, pump, 1, 2);

                    FfmpegVideoOutcome outcome = AwaitOutcome(context, 3);
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                        "real finalize state: " + outcome.ErrorCode + " " + outcome.ErrorDetail);

                    string finalPath = Path.Combine(directory, "video.mp4");
                    Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(outcome, finalPath);
                    TestKit.CheckEqual(Mp4FinalizationVerdict.Published, verdict.Verdict,
                        "verdict: " + Mp4FinalizationPolicy.Describe(verdict));
                    TestKit.Check(File.Exists(finalPath), "the real MP4 must be published");
                    TestKit.Check(new FileInfo(finalPath).Length > 0, "the published MP4 must not be empty");
                    TestKit.CheckEqual(3L, verdict.DecodedFrameCount, "verified decoded frame count");
                    TestKit.CheckEqual(3L, verdict.PacketCount, "verified container packet count");
                    TestKit.CheckEqual("h264", verdict.CodecName, "codec");
                    TestKit.CheckEqual(0, outcome.EncoderExitCode ?? -1, "encoder exit code");

                    string stopError;
                    TestKit.Check(context.TryStopAndDrain("mp4-finalizing-complete", out stopError),
                        "the real context must converge: " + stopError);
                    TestKit.Check(!context.HasResidualOwnership, "no residual ownership");
                    pipeline.Dispose();
                });
                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("finalizing e2e: a verification failure publishes nothing", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-verify-fail");
                WithPumpedMainThreadContext(pump =>
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        // 交付 2 帧，但核验只报 1 帧：这是"容器看起来成功、内容不对"的形态。
                        Verifier = new FakeVideoVerifierScript { Frames = 1, Width = 64, Height = 48, Fps = 30 },
                    };

                    FfmpegVideoPipeline pipeline = CreatePipeline(directory, launcher.Create());
                    TestKit.Check(pipeline.Start().Started, "pipeline start");
                    string tempPath = pipeline.TempPath;

                    Rgb24DeliveryContext context = NewContext(pipeline);
                    DeliverFrame(context, pump, 1, 0);
                    DeliverFrame(context, pump, 1, 1);

                    FfmpegVideoOutcome outcome = AwaitOutcome(context, 2);
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State,
                        "verification failure must fail closed");
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")),
                        "no final file may exist after a verification failure");
                    TestKit.Check(!File.Exists(tempPath), "the temp product must be removed");

                    Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(
                        outcome, Path.Combine(directory, "video.mp4"));
                    TestKit.CheckEqual(Mp4FinalizationVerdict.Failed, verdict.Verdict, "verdict");
                    TestKit.Check(!verdict.IsPublished, "not published");
                    TestKit.CheckEqual("verification-failed", verdict.ErrorCode, "machine readable reason: " +
                        verdict.ErrorCode + " " + verdict.ErrorDetail);
                    TestKit.CheckEqual("Failed", verdict.VerificationStatus, "verification evidence is kept");

                    string stopError;
                    TestKit.Check(context.TryStopAndDrain("test", out stopError), "converge: " + stopError);
                    pipeline.Dispose();
                });
                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("finalizing e2e: a cancel during finalizing never publishes", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-cancel");
                WithPumpedMainThreadContext(pump =>
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        // 进程在 stdin EOF 之后停留一段时间，给取消一个确定的窗口。
                        Encoder = new FakeVideoEncoderScript { OutputBytes = 512, SleepBeforeExitMs = 1500 },
                        Verifier = new FakeVideoVerifierScript { Frames = 1, Width = 64, Height = 48, Fps = 30 },
                    };

                    FfmpegVideoPipeline pipeline = CreatePipeline(directory, launcher.Create());
                    TestKit.Check(pipeline.Start().Started, "pipeline start");
                    string tempPath = pipeline.TempPath;

                    Rgb24DeliveryContext context = NewContext(pipeline);
                    DeliverFrame(context, pump, 1, 0);

                    Rgb24FinalizeStart start = context.TryBeginFinalizing(1);
                    TestKit.Check(start.Started, "finalize accepted: " + start.ErrorCode);

                    Thread.Sleep(250);
                    string stopError;
                    TestKit.Check(context.TryRequestStop("user-stop", out stopError), "cancel request: " + stopError);

                    FfmpegVideoOutcome outcome = Await(start.Completion, "cancelled finalize");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State,
                        "a cancel must win before publish (state=" + outcome.State + ")");
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")),
                        "a cancelled session must not publish anything");
                    TestKit.Check(!File.Exists(tempPath), "the temp product must be removed on cancel");

                    Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(
                        outcome, Path.Combine(directory, "video.mp4"));
                    TestKit.CheckEqual(Mp4FinalizationVerdict.Cancelled, verdict.Verdict, "verdict");
                    TestKit.CheckEqual("user-stop", verdict.ErrorCode, "reason");
                    TestKit.CheckEqual(null, verdict.FinalPath, "no path");

                    TestKit.Check(context.TryStopAndDrain("user-stop", out stopError), "converge: " + stopError);
                    pipeline.Dispose();
                });
                TestKit.TryDeleteDirectory(directory);
            });
        }

        // ================================================================ hardening

        private static void HardeningTests(string workRoot)
        {
            TestKit.Run("finalizing hardening: an exceptional convergence never faults and never claims clean", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-convergence-guard");
                try
                {
                    FfmpegVideoPipeline pipeline = CreatePipeline(directory, null);

                    MethodInfo guard = typeof(FfmpegVideoPipeline).GetMethod(
                        "RunConvergenceGuarded", BindingFlags.Instance | BindingFlags.NonPublic);
                    TestKit.Check(guard != null, "the convergence guard exists");

                    var throwing = new Func<FfmpegVideoOutcome>(() =>
                    {
                        throw new InvalidOperationException("induced convergence failure");
                    });

                    var outcome = (FfmpegVideoOutcome)guard.Invoke(pipeline, new object[] { throwing });
                    TestKit.Check(outcome != null, "the guard must always produce an outcome");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                    TestKit.Check(outcome.ResidualOwnership,
                        "an exceptional convergence must report residual ownership, never pretend to be clean");
                    TestKit.Check(outcome.ResidualDetail != null &&
                                  outcome.ResidualDetail.Contains("induced convergence failure"),
                        "the induced failure must stay visible: " + outcome.ResidualDetail);
                    TestKit.Check(pipeline.CleanupTask == null || !pipeline.CleanupTask.IsFaulted,
                        "a faulted convergence task would make ownership undecidable forever");
                }
                finally
                {
                    TestKit.TryDeleteDirectory(directory);
                }
            });

            TestKit.Run("finalizing hardening: a throwing verifier still converges and deletes the temp product", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-verifier-throw");
                try
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        Encoder = new FakeVideoEncoderScript { OutputBytes = 256 },
                    };

                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                        Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        ProcessStart = launcher.Create(),
                        Verifier = new ThrowingVerifier(),
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "start");
                    string tempPath = pipeline.TempPath;

                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame accepted");
                    TestKit.Check(Await(attempt.Completion, "write").Success, "frame written");

                    FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                    TestKit.CheckEqual("verification-threw", outcome.ErrorCode, "error code");
                    TestKit.Check(!pipeline.CleanupTask.IsFaulted, "the convergence task must never fault");
                    TestKit.Check(!outcome.ResidualOwnership, "no residual: " + outcome.ResidualDetail);
                    TestKit.Check(!File.Exists(tempPath), "the temp product must be removed");
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                    pipeline.Dispose();
                }
                finally
                {
                    TestKit.TryDeleteDirectory(directory);
                }
            });

            // ---- 定向故障注入：完成关口抛异常 **且** 收敛再次抛异常 ----
            TestKit.Run("finalizing hardening: a failing finalize whose teardown also fails still never faults", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-double-fault");
                try
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        // 编码进程自行快速退出：本用例只验证收敛闭合，不需要它继续存活。
                        Encoder = new FakeVideoEncoderScript { ExitAfterBytes = 0 },
                    };

                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                        Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        ProcessStart = launcher.Create(),
                        // 两次注入：完成关口自身抛异常，且唯一回收路径也抛异常。
                        FaultInjection = point =>
                        {
                            if (point == "finalize" || point == "teardown")
                                throw new InvalidOperationException("induced " + point + " failure");
                        },
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "start");

                    Task<FfmpegVideoOutcome> finish = pipeline.FinishAsync();
                    TestKit.Check(finish != null, "FinishAsync must return an observable task");
                    TestKit.Check(ReferenceEquals(finish, pipeline.CleanupTask),
                        "finalizing must reuse the single convergence task");

                    // Await 在 task faulted 时会抛异常 —— 这正是本用例要排除的形态。
                    FfmpegVideoOutcome outcome = Await(finish, "finish");
                    TestKit.Check(!finish.IsFaulted && !finish.IsCanceled,
                        "the finalizing task must never fault, even when teardown throws too");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                    TestKit.CheckEqual("finalize-exception", outcome.ErrorCode, "error code");
                    TestKit.Check(outcome.ResidualOwnership,
                        "an unconfirmable cleanup must be reported as residual ownership");
                    TestKit.Check(outcome.ResidualDetail != null &&
                                  outcome.ResidualDetail.Contains("induced teardown failure"),
                        "the induced failure must stay visible: " + outcome.ResidualDetail);
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")),
                        "a doubly exceptional finalize must never publish");
                    pipeline.Dispose();
                }
                finally
                {
                    TestKit.TryDeleteDirectory(directory);
                }
            });

            TestKit.Run("finalizing hardening: a cancel whose teardown also fails still never faults", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-cancel-teardown-fault");
                try
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        Encoder = new FakeVideoEncoderScript { ExitAfterBytes = 0 },
                    };

                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                        Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        ProcessStart = launcher.Create(),
                        FaultInjection = point =>
                        {
                            if (point == "teardown")
                                throw new InvalidOperationException("induced teardown failure");
                        },
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "start");

                    pipeline.Cancel("test");
                    FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                    TestKit.Check(!pipeline.CleanupTask.IsFaulted,
                        "the cancel convergence task must never fault");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State,
                        "an unconfirmable cancel convergence is reported as Failed, not as clean");
                    TestKit.Check(outcome.ResidualOwnership, "residual must be reported");
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                    pipeline.Dispose();
                }
                finally
                {
                    TestKit.TryDeleteDirectory(directory);
                }
            });

            // ---- 核验自身资源的收敛证据 ----
            TestKit.Run("finalizing hardening: a verification process that cannot be reaped is never clean", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-verify-unreapable");
                try
                {
                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                        Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        ProcessStart = StartUnreapableVerifier,
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "encoder start");
                    string tempPath = pipeline.TempPath;

                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame accepted");
                    TestKit.Check(Await(attempt.Completion, "write").Success, "frame written");

                    FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                    TestKit.Check(outcome.Verification != null, "verification evidence must exist");
                    TestKit.Check(outcome.Verification.ResidualProcessOwnership,
                        "an unreapable verification process must be reported");
                    TestKit.Check(outcome.Verification.ResidualDetail != null &&
                                  outcome.Verification.ResidualDetail.Contains("decode:"),
                        "the failing pass must be named: " + outcome.Verification.ResidualDetail);
                    TestKit.Check(outcome.ResidualOwnership,
                        "the pipeline must carry the verification residual, never claim clean");
                    TestKit.Check(outcome.ResidualDetail != null &&
                                  outcome.ResidualDetail.Contains("verification-residual"),
                        "the residual must name its origin: " + outcome.ResidualDetail);
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                    TestKit.Check(!File.Exists(tempPath), "the temp product must be removed");
                    pipeline.Dispose();
                }
                finally
                {
                    TestKit.TryDeleteDirectory(directory);
                }
            });

            TestKit.Run("finalizing hardening: an initialization failure still reaps the started verifier", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-verify-init-failure");
                try
                {
                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                        Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        // 核验进程已启动但 stdout 未重定向：初始化必然中途失败。
                        ProcessStart = StartUnredirectedVerifier,
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "encoder start");

                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame accepted");
                    TestKit.Check(Await(attempt.Completion, "write").Success, "frame written");

                    FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                    TestKit.CheckEqual("verification-failed", outcome.ErrorCode,
                        "error code: " + outcome.ErrorCode + " " + outcome.ErrorDetail);
                    TestKit.Check(outcome.Verification != null &&
                                  outcome.Verification.ErrorCode != null &&
                                  outcome.Verification.ErrorCode.StartsWith("verify-", StringComparison.Ordinal),
                        "the initialization failure must be machine readable: " +
                        (outcome.Verification == null ? "no evidence" : outcome.Verification.ErrorCode));
                    // 进程确实被回收（Kill 之后等到真实退出），因此这里**不应**有核验侧 residual。
                    TestKit.Check(!outcome.Verification.ResidualProcessOwnership,
                        "a reaped verifier must not be reported as residual: " +
                        outcome.Verification.ResidualDetail);
                    pipeline.Dispose();
                }
                finally
                {
                    TestKit.TryDeleteDirectory(directory);
                }
            });

            TestKit.Run("finalizing hardening: a normal verification never reports a process residual", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-verify-no-false-residual");
                try
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        Encoder = new FakeVideoEncoderScript { OutputBytes = 256 },
                        Verifier = new FakeVideoVerifierScript { Frames = 1, Width = 64, Height = 48, Fps = 30 },
                    };

                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                        Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        ProcessStart = launcher.Create(),
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "start");

                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame accepted");
                    TestKit.Check(Await(attempt.Completion, "write").Success, "frame written");

                    FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                        "state: " + outcome.ErrorCode + " " + outcome.ErrorDetail);
                    TestKit.Check(outcome.Verification != null && !outcome.Verification.ResidualProcessOwnership,
                        "a clean verification must not fabricate residual: " +
                        (outcome.Verification == null ? "no evidence" : outcome.Verification.ResidualDetail));
                    TestKit.Check(!outcome.ResidualOwnership,
                        "no residual expected: " + outcome.ResidualDetail);
                    pipeline.Dispose();
                }
                finally
                {
                    TestKit.TryDeleteDirectory(directory);
                }
            });

            TestKit.Run("finalizing hardening: a verification residual defeats a clean report in the outcome", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "l3c-verify-residual-propagation");
                try
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        Encoder = new FakeVideoEncoderScript { OutputBytes = 256 },
                    };

                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                        Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        ProcessStart = launcher.Create(),
                        Verifier = new ResidualReportingVerifier(),
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "start");

                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame accepted");
                    TestKit.Check(Await(attempt.Completion, "write").Success, "frame written");

                    FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                        "the product itself is still completed and verified");
                    TestKit.Check(File.Exists(Path.Combine(directory, "video.mp4")), "the product is published");
                    TestKit.Check(outcome.ResidualOwnership,
                        "an unconfirmed verification cleanup must still be reported as residual");
                    TestKit.Check(outcome.ResidualDetail != null &&
                                  outcome.ResidualDetail.Contains("verification-residual"),
                        "residual origin must be named: " + outcome.ResidualDetail);

                    Mp4FinalizationOutcome verdict = Mp4FinalizationPolicy.Map(
                        outcome, Path.Combine(directory, "video.mp4"));
                    TestKit.CheckEqual(Mp4FinalizationVerdict.Published, verdict.Verdict,
                        "the verdict stays Published (the artifact is verified) but carries the residual");
                    TestKit.Check(verdict.ResidualOwnership, "the verdict must carry the residual");
                    pipeline.Dispose();
                }
                finally
                {
                    TestKit.TryDeleteDirectory(directory);
                }
            });
        }

        private sealed class ThrowingVerifier : IFfmpegVideoVerifier
        {
            public Task<FfmpegVideoVerificationResult> VerifyAsync(FfmpegVideoVerificationRequest request)
            {
                throw new InvalidOperationException("induced verifier failure");
            }
        }

        /// <summary>
        /// 假核验器：报告"核验通过"，但同时声明**自身的子进程未能确认收敛**。
        /// 用于验证 L2 是否把这份不确定性如实带进终态（绝不宣称 clean）。
        /// </summary>
        private sealed class ResidualReportingVerifier : IFfmpegVideoVerifier
        {
            public Task<FfmpegVideoVerificationResult> VerifyAsync(FfmpegVideoVerificationRequest request)
            {
                return Task.FromResult(new FfmpegVideoVerificationResult
                {
                    Status = FfmpegVideoVerificationStatus.Verified,
                    DecodedFrameCount = request.ExpectedFrameCount,
                    ContainerPacketCount = request.ExpectedFrameCount,
                    DecodedWidth = request.ExpectedWidth,
                    DecodedHeight = request.ExpectedHeight,
                    CodecName = request.ExpectedCodecName,
                    PixelFormat = request.ExpectedPixelFormat,
                    ResidualProcessOwnership = true,
                    ResidualDetail = "decode:verify-process-not-reaped",
                });
            }
        }

        /// <summary>
        /// 定向故障注入：核验进程返回一个**从未启动**的 Process 对象 ——
        /// 既不能 Kill，也不能等待退出（<c>HasExited</c> / <c>WaitForExit</c> 都会抛异常）。
        /// 编码进程仍走真实的假子进程。
        /// </summary>
        private static Process StartUnreapableVerifier(string executablePath, string arguments, string workingDirectory)
        {
            if (arguments.IndexOf("-f framemd5", StringComparison.Ordinal) >= 0)
                return new Process();

            var startInfo = new ProcessStartInfo
            {
                FileName = FakeVideoProcess.ExecutablePath,
                Arguments = FakeVideoProcess.BuildArguments(arguments, "output-bytes=256"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory ?? string.Empty,
            };

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            return process;
        }

        /// <summary>核验进程的 stdout 未重定向：进程已启动，但初始化中途必然失败。</summary>
        private static Process StartUnredirectedVerifier(string executablePath, string arguments, string workingDirectory)
        {
            bool verifier = arguments.IndexOf("-f framemd5", StringComparison.Ordinal) >= 0;

            var startInfo = new ProcessStartInfo
            {
                FileName = FakeVideoProcess.ExecutablePath,
                Arguments = FakeVideoProcess.BuildArguments(
                    arguments, verifier ? "exit-after-bytes=0" : "output-bytes=256"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                // 核验进程刻意不重定向 stdout/stderr：访问 StandardOutput 会抛异常，
                // 从而复现"进程已启动、初始化中途失败"的形态。
                RedirectStandardOutput = !verifier,
                RedirectStandardError = !verifier,
                WorkingDirectory = workingDirectory ?? string.Empty,
            };

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            return process;
        }

        // ================================================================ production wiring

        private static void ProductionSourceContracts()
        {
            TestKit.Run("l3c wiring: the completion path hands the encoder over instead of cancelling it", () =>
            {
                string scheduler = Source("Export/DeterministicFrameScheduler.cs");
                string processStop = Method(scheduler, "private static void ProcessStop()");

                TestKit.Check(processStop.Contains("bool completionTerminal = MapTerminal(stopEvent) == SchedulerStatus.Completed;"),
                    "the handoff decision is derived from the same tested terminal mapping");
                TestKit.Check(processStop.Contains("RestoreAll(completionTerminal, out string cleanupError)"),
                    "cleanup is told whether a completion handoff is allowed");
                TestKit.Check(processStop.Contains("RevokeRgb24CompletionForCleanupFailure(\"cleanup-failed\")") &&
                              processStop.Contains("RevokeRgb24CompletionForCleanupFailure(\"cleanup-exception\")"),
                    "a failed Unity cleanup must fall back to the original cancel path immediately");

                // C：cleanup 失败时交接**尚未发生**，因此必须主动取消原来的 _rgb24Delivery，
                // 不能只撤销空的交接槽、也不能等下一次 Tick 才第一次发起取消。
                string revokeForCleanup = Method(scheduler,
                    "private static void RevokeRgb24CompletionForCleanupFailure(string reason)");
                TestKit.Check(revokeForCleanup.Contains("_rgb24Delivery"),
                    "the fallback must target the not-yet-handed-off context");
                TestKit.Check(revokeForCleanup.IndexOf("_rgb24Delivery", StringComparison.Ordinal) <
                              revokeForCleanup.IndexOf("TryStopAndDrain(", StringComparison.Ordinal),
                    "it must request cancellation immediately (no Tick dependency)");
                TestKit.Check(revokeForCleanup.Contains("RevokeRgb24CompletionHandoff(reason)"),
                    "an already-performed handoff is revoked as well");
                TestKit.Check(!revokeForCleanup.Contains("TickResidualOwnership"),
                    "the immediate fallback must not rely on a later Tick");

                int cleanup = processStop.IndexOf("RestoreAll(completionTerminal", StringComparison.Ordinal);
                int mapped = processStop.IndexOf("_status = MapTerminal(stopEvent);", StringComparison.Ordinal);
                int detach = processStop.IndexOf("TryDetachRgb24CompletionHandoff()", StringComparison.Ordinal);
                TestKit.Check(cleanup > 0 && mapped > cleanup && detach > mapped,
                    "FinishAsync may only be reachable after cleanup succeeded and the terminal state was mapped");

                string restore = Method(scheduler,
                    "private static bool RestoreAll(bool allowRgb24CompletionHandoff, out string error)");
                int guard = restore.IndexOf("if (!allowRgb24CompletionHandoff)", StringComparison.Ordinal);
                int drain = restore.IndexOf("_rgb24Delivery.TryStopAndDrain(", StringComparison.Ordinal);
                TestKit.Check(guard > 0 && drain > guard,
                    "the original cancel path is skipped **only** on the completion handoff path");

                string detachBody = Method(scheduler, "private static bool TryDetachRgb24CompletionHandoff()");
                TestKit.Check(!detachBody.Contains("RequestStop") && !detachBody.Contains("Cancel("),
                    "the handoff must never cancel a successfully delivered context");
                TestKit.Check(detachBody.Contains("context.HasFrameOwnership") &&
                              detachBody.Contains("context.Stopping"),
                    "the handoff refuses while frame ownership or a stop request is outstanding");

                TestKit.Check(scheduler.Contains("public static bool TryTakeCompletedRgb24Delivery(out Rgb24DeliveryContext context, out string error)"),
                    "the handoff is taken exactly once");
                TestKit.Check(scheduler.Contains("_rgb24CompletionHandoff != null ||"),
                    "an untaken handoff stays residual ownership (restart stays blocked)");
                TestKit.Check(scheduler.Contains("public static void RevokeCompletedRgb24Delivery(string reason)"),
                    "an abandoned handoff can be put back on the cancel path");

                string take = Method(scheduler,
                    "public static bool TryTakeCompletedRgb24Delivery(out Rgb24DeliveryContext context, out string error)");
                TestKit.Check(take.Contains("_rgb24HandoffRevoked"),
                    "a revoked handoff can never be taken for finalizing");
                string revokeHandoff = Method(scheduler,
                    "private static void RevokeRgb24CompletionHandoff(string reason)");
                TestKit.Check(revokeHandoff.Contains("TryConvergeRevokedRgb24Handoff"),
                    "revoking keeps converging instead of dropping ownership");
                TestKit.Check(Method(scheduler, "public static void TickResidualOwnership()")
                        .Contains("TryConvergeRevokedRgb24Handoff(_terminalStopReason)"),
                    "a revoked handoff converges on the existing residual pump");
                TestKit.Check(!scheduler.Contains("new FfmpegVideoPipeline("),
                    "the scheduler never constructs a second process owner");
            });

            TestKit.Run("l3c wiring: the controller owns finalizing, blocks restart and cancels fail-closed", () =>
            {
                string controller = Source("Export/EditorExportController.cs");

                TestKit.Check(controller.Contains("_startupContext != null || _finalizingContext != null ||"),
                    "IsBusy covers the finalizing slot, so no new session may start");
                TestKit.Check(controller.Contains("public static bool IsFinalizing =>"),
                    "the finalizing projection exists (session/UI only)");
                TestKit.Check(controller.Contains("!IsFinalizing &&"),
                    "finalizing is not reported as terminal convergence (Stop stays meaningful)");

                string tick = Method(controller, "public static void Tick()");
                TestKit.Check(tick.IndexOf("TickFinalizingConvergence()", StringComparison.Ordinal) <
                              tick.IndexOf("TickTerminalControllerRearm()", StringComparison.Ordinal),
                    "the finalizing slot converges before the re-arm path");
                TestKit.Check(tick.Contains("RevokeAbandonedFinalizingHandoff()"),
                    "an untaken handoff is revoked instead of blocking restarts forever");
                TestKit.Check(Method(controller, "private static void RevokeAbandonedFinalizingHandoff()")
                        .Contains("RevokeCompletedRgb24Delivery("),
                    "revoking reuses the scheduler's tested cancel path");
                int finalizingBranch = tick.IndexOf("if (s.State == EditorExportState.Finalizing)",
                    StringComparison.Ordinal);
                int runningOnly = tick.IndexOf("if (s.State != EditorExportState.Running) return;",
                    StringComparison.Ordinal);
                TestKit.Check(finalizingBranch > 0 && runningOnly > finalizingBranch,
                    "finalizing is handled before the running-only path");

                string tickFinalizing = Method(controller, "private static void TickFinalizing()");
                TestKit.Check(tickFinalizing.Contains("if (!task.IsCompleted) return;"),
                    "finalizing observation is non-blocking (no wall-clock deadline)");
                TestKit.Check(!tickFinalizing.Contains(".Wait(") &&
                              !tickFinalizing.Contains("GetAwaiter().GetResult()"),
                    "the Unity main thread never blocks on the encoder");

                string stop = Method(controller, "public static void Stop()");
                TestKit.Check(stop.Contains("CancelFinalizing(\"user-stop\")"),
                    "Stop during finalizing requests a cancel");
                string cancel = Method(controller, "public static void Cancel(string reason)");
                TestKit.Check(cancel.Contains("CancelFinalizing("),
                    "disable / unload cancel the finalizing pipeline (fail-closed)");

                string begin = Method(controller, "private static void BeginFinalizing(EditorExportSession s)");
                TestKit.Check(begin.Contains("DeterministicFrameScheduler.TryTakeCompletedRgb24Delivery("),
                    "the controller takes the scheduler handoff exactly once");
                TestKit.Check(begin.IndexOf("context.TryBeginFinalizing(s.LogicalFrameCount)", StringComparison.Ordinal) >
                              begin.IndexOf("TryTakeCompletedRgb24Delivery(", StringComparison.Ordinal),
                    "the handoff is taken before the finish is started, with the frozen logical frame count");
                TestKit.Check(begin.Contains("MarkFinalizingUnavailable("),
                    "a missing handoff or a refused finish fails closed instead of reporting success");

                string verdict = Method(controller, "private static void ApplyFinalizingVerdict(EditorExportSession s, Mp4FinalizationOutcome outcome)");
                TestKit.Check(verdict.Contains("Mp4FinalizationPolicy.Label(outcome.Verdict)"),
                    "the session projection comes from the tested policy");
                TestKit.Check(verdict.Contains("case Mp4FinalizationVerdict.Published:"),
                    "only a Published verdict may map to Completed");

                string map = Method(controller, "private static void TickFinalizing()");
                TestKit.Check(map.Contains("Mp4FinalizationPolicy.Map(task.Result, s.FinalVideoPath)"),
                    "the verdict is mapped against the frozen final path");
                TestKit.Check(map.Contains("task.IsFaulted"),
                    "a faulted finish task is a failure, never a success");

                TestKit.Check(!controller.Contains("new FfmpegVideoPipeline("),
                    "the controller never constructs a second process owner");
            });

            TestKit.Run("l3c wiring: finalizing residual metadata never claims clean while resources are owned", () =>
            {
                string controller = Source("Export/EditorExportController.cs");

                string unavailable = Method(controller,
                    "private static void MarkFinalizingUnavailable(EditorExportSession s, string errorCode)");
                TestKit.Check(!unavailable.Contains("Mp4FinalizingResidual = false"),
                    "an unavailable finalize must never unconditionally record residual=false");
                TestKit.Check(unavailable.Contains("CurrentFinalizingResidual(null)"),
                    "it must project the real convergence state instead");

                string residual = Method(controller,
                    "private static bool CurrentFinalizingResidual(Mp4FinalizationOutcome outcome)");
                TestKit.Check(residual.Contains("outcome.ResidualOwnership") &&
                              residual.Contains("context.HasResidualOwnership"),
                    "residual must consider both the L2 outcome and the still-owned context");

                string verdict = Method(controller,
                    "private static void ApplyFinalizingVerdict(EditorExportSession s, Mp4FinalizationOutcome outcome)");
                TestKit.Check(verdict.Contains("CurrentFinalizingResidual(outcome)"),
                    "the verdict must not overwrite the residual with a bare L2 flag");

                string convergence = Method(controller, "private static void TickFinalizingConvergence()");
                TestKit.Check(convergence.Contains("session.Mp4FinalizingResidual = false") &&
                              convergence.Contains("TryWriteMetadataBestEffort(session)"),
                    "a converged slot must correct an earlier residual snapshot in metadata");
                TestKit.Check(convergence.IndexOf("TryStopAndDrain(", StringComparison.Ordinal) <
                              convergence.IndexOf("Mp4FinalizingResidual = false", StringComparison.Ordinal),
                    "the correction may only happen after convergence actually succeeded");
            });

            TestKit.Run("l3c wiring: the pipeline convergence task can never fault", () =>
            {
                string pipeline = Source("Ffmpeg/FfmpegVideoPipeline.cs");
                TestKit.Check(pipeline.Contains("_convergence = Task.Run(() => RunFinalizeGuarded());"),
                    "finishing runs inside the exception guard");
                TestKit.Check(pipeline.Contains("_convergence = Task.Run(() => RunConvergenceGuarded(body));"),
                    "failure convergence (start / write failure) runs inside the exception guard");
                // 取消路径同样必须封闭：faulted CleanupTask 会让 ownership 永远不可判定。
                TestKit.Check(pipeline.Contains("_convergence = Task.Run(() => RunConvergenceGuarded(() => ConvergeCancel()));"),
                    "the cancel convergence path runs inside the same exception guard");
                TestKit.Check(!pipeline.Contains("Task.Run(() => ConvergeCancel())"),
                    "no unguarded convergence task may remain");

                string finalizeGuard = Method(pipeline, "private FfmpegVideoOutcome RunFinalizeGuarded()");
                TestKit.Check(finalizeGuard.Contains("FinalizeCore()") && finalizeGuard.Contains("ConvergeByState()"),
                    "the guard still converges the session");
                // A：第一次 catch 之后的收敛自身再抛异常时，必须复用同一个 failsafe，
                // 而不是直接调用 ConvergeByState() 让它逃逸出 Task。
                TestKit.Check(finalizeGuard.Contains("RunConvergenceGuarded(() => ConvergeByState())"),
                    "a second-level exception inside the finalize guard is closed by the same failsafe");

                string convergeGuard = Method(pipeline,
                    "private FfmpegVideoOutcome RunConvergenceGuarded(Func<FfmpegVideoOutcome> body)");
                TestKit.Check(convergeGuard.Contains("Teardown()"),
                    "an exceptional convergence still attempts the single teardown path");
                TestKit.Check(convergeGuard.Contains("ResidualOwnership = true"),
                    "an exceptional convergence never claims to be clean");
                TestKit.Check(convergeGuard.Contains("BuildFailsafeOutcome("),
                    "a teardown that also throws still produces an honest outcome");

                string verifier = Source("Ffmpeg/FfmpegVideoVerifier.cs");
                TestKit.Check(verifier.Contains("private FfmpegVideoVerificationResult VerifyGuarded(FfmpegVideoVerificationRequest request)"),
                    "verification is guarded too");
                TestKit.Check(verifier.Contains("\"verify-harness-exception\""),
                    "a harness failure has a stable machine-readable code");
                TestKit.Check(!verifier.Contains("int.Parse("),
                    "pathological numeric input must never throw out of the verifier");

                // B：每一个返回路径都必须收敛已经取得的 task / process ownership。
                string runPass = Method(verifier, "private PassOutcome RunPass(");
                TestKit.Check(runPass.Contains("Task exitTask = null;") &&
                              runPass.Contains("Task stdoutTask = null;") &&
                              runPass.Contains("Task stderrTask = null;"),
                    "started tasks are tracked from the beginning (no ownership loss mid-init)");
                TestKit.Check(runPass.Contains("ReapPass(process, exitTask, stdoutTask, stderrTask, outcome)"),
                    "the single reap path runs on every return path");

                string reap = Method(verifier, "private static void ReapPass(");
                TestKit.Check(reap.Contains("exitTask.Wait()") || reap.Contains("process.WaitForExit()"),
                    "a kill must be followed by a real exit confirmation");
                TestKit.Check(reap.Contains("Task.WaitAll(pumps.ToArray())"),
                    "started drain pumps must be awaited, not equated with EOF");
                TestKit.Check(reap.Contains("verify-process-not-associated"),
                    "an unusable process object must not block the convergence thread");
                TestKit.Check(reap.Contains("ResidualDetail"),
                    "unconfirmed convergence must be reported, never silently dropped");
                TestKit.Check(verifier.Contains("NoteProcessResidual(result, \"decode\", decode)") &&
                              verifier.Contains("NoteProcessResidual(result, \"packet\", packets)"),
                    "both verification passes report their own process residual");
                TestKit.Check(pipeline.Contains("verification.ResidualProcessOwnership"),
                    "a verification residual must reach the pipeline outcome");
            });

            TestKit.Run("l3c wiring: finalizing reuses the existing L2 finish protocol", () =>
            {
                string transport = Source("Export/FfmpegRgb24FrameTransport.cs");
                TestKit.Check(transport.Contains("return _pipeline.FinishAsync();"),
                    "the transport only forwards the existing FinishAsync protocol");

                string context = Source("Export/Rgb24DeliveryContext.cs");
                TestKit.Check(context.Contains("internal Rgb24FinalizeStart TryBeginFinalizing(long expectedFrameCount)"),
                    "the context owns the one-shot finalizing gate");
                TestKit.Check(context.Contains("if (_finalizeStarted)"),
                    "the gate is one-shot");
                TestKit.Check(context.Contains("_lifetime.CleanupTask == null"),
                    "an accepted finish must have a real convergence task");
                // 计数一致性关口必须在 BeginFinalize() **之前**（进入 FinishAsync 之前 fail-closed）。
                TestKit.Check(context.IndexOf("rgb24-finalize-frame-count-mismatch", StringComparison.Ordinal) <
                              context.IndexOf("completion = _lifetime.BeginFinalize();", StringComparison.Ordinal),
                    "the frame-count invariant is verified before FinishAsync is attempted");
                TestKit.Check(context.Contains("long DeliveredFrameCount { get; }"),
                    "the count comes from the existing L2 lifetime (no second counting authority)");
                TestKit.Check(Source("Export/FfmpegRgb24FrameTransport.cs")
                        .Contains("public long DeliveredFrameCount => _pipeline.DeliveredFrameCount;"),
                    "the transport only reads L2's own count");
                TestKit.Check(!context.Contains("new FfmpegVideoPipeline("),
                    "no second FFmpeg pipeline may be created");
                TestKit.Check(Source("Export/Mp4FinalizationPolicy.cs").Contains("verification.IsVerified"),
                    "success requires a passing verification");

                string session = Source("Export/EditorExportSession.cs");
                TestKit.Check(session.Contains("public string Mp4FinalizingState;") &&
                              session.Contains("AppendStringNullable(sb, \"mp4FinalizingState\""),
                    "the verdict is projected into session metadata");
                TestKit.Check(session.Contains("public bool FinalizingNotImplemented;") &&
                              !Source("Export/EditorExportController.cs").Contains("FinalizingNotImplemented = OutputModePolicy.IsMp4("),
                    "the L3-A boundary flag is no longer derived from the mode");
            });
        }

        // ================================================================ helpers

        private static Rgb24DeliveryContext NewContext(IRgb24FrameTransport transport, IRgb24PipelineLifetime lifetime)
        {
            Rgb24FrameLayout layout;
            string layoutError;
            TestKit.Check(Rgb24FrameLayout.TryCreate(64, 48, out layout, out layoutError), "layout: " + layoutError);

            Rgb24MainThreadBridge bridge;
            string bridgeError;
            TestKit.Check(Rgb24MainThreadBridge.TryCapture(out bridge, out bridgeError), "bridge: " + bridgeError);

            return new Rgb24DeliveryContext(layout, transport, lifetime, bridge);
        }

        private static Rgb24DeliveryContext NewContext(FfmpegVideoPipeline pipeline)
        {
            Rgb24DeliveryContext context;
            string error;
            TestKit.Check(Rgb24DeliveryContext.TryCreate(64, 48, pipeline, out context, out error),
                "context: " + error);
            return context;
        }

        private static Rgb24DeliveryContext NewContext(FakeLifetime lifetime)
        {
            return NewContext(lifetime, lifetime);
        }

        private static void DeliverFrame(Rgb24DeliveryContext context, PumpContext pump, long generation, long frameIndex)
        {
            Rgb24TransactionOutcome begin = context.Transaction.TryBeginFrame(generation, frameIndex, out string beginError);
            TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, begin, "begin frame: " + beginError);

            OwnedRgb24Frame frame;
            Rgb24TransactionOutcome eof = context.Transaction.TryCompleteEof(
                generation, frameIndex, out frame, out string eofError);
            TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, eof, "complete eof: " + eofError);

            Rgb24TransactionOutcome delivery = context.Transaction.TryBeginDelivery(
                out string deliveryError, out string deliveryDetail);
            TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, delivery,
                "begin delivery: " + deliveryError + " " + deliveryDetail);

            Rgb24DeliveryEnvelope envelope = WaitForEnvelope(context, pump);
            TestKit.CheckEqual(generation, envelope.Generation, "envelope generation");
            TestKit.CheckEqual(frameIndex, envelope.AbsoluteFrameIndex, "envelope frame index");

            Rgb24TransactionOutcome consumed = context.Transaction.ConsumeEnvelope(
                true, envelope, out string errorCode, out string errorDetail);
            TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, consumed,
                "consume: " + consumed + " " + errorCode + " " + errorDetail);
        }

        private static FfmpegVideoOutcome AwaitOutcome(Rgb24DeliveryContext context, long expectedFrameCount)
        {
            Rgb24FinalizeStart start = context.TryBeginFinalizing(expectedFrameCount);
            TestKit.Check(start.Started, "finalize accepted: " + start.ErrorCode);
            return Await(start.Completion, "finalize");
        }

        private static Rgb24DeliveryEnvelope WaitForEnvelope(Rgb24DeliveryContext context, PumpContext pump)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                // 先在主线程上执行入队的回调（真实 Unity 主线程模型），再消费通知槽。
                pump.Pump();

                Rgb24DeliveryEnvelope envelope;
                string bridgeError;
                if (context.TryTakePendingEnvelope(out envelope, out bridgeError))
                    return envelope;

                TestKit.Check(bridgeError == null, "bridge error: " + bridgeError);
                TestKit.Check(stopwatch.ElapsedMilliseconds < TimeoutMs,
                    "timed out waiting for the RGB24 delivery completion");
                Thread.Sleep(5);
            }
        }

        private static FfmpegVideoPipeline CreatePipeline(string directory, FfmpegVideoProcessStart processStart)
        {
            return CreatePipeline(directory, processStart,
                FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath));
        }

        private static FfmpegVideoPipeline CreatePipeline(
            string directory, FfmpegVideoProcessStart processStart, FfmpegVideoIdentity identity)
        {
            var options = new FfmpegVideoPipelineOptions
            {
                Settings = new FfmpegVideoSettings { Width = 64, Height = 48, Fps = 30 },
                Identity = identity,
                FinalPath = Path.Combine(directory, "video.mp4"),
                ProcessStart = processStart,
            };

            return new FfmpegVideoPipeline(options);
        }

        /// <summary>真实 FFmpeg fixture；缺失时如实 SKIP（二进制刻意不入库）。</summary>
        private static FfmpegVideoIdentity RealFixtureIdentity()
        {
            System.Collections.Generic.IReadOnlyList<string> candidates =
                Program.FindLocalFfmpegBinaries();
            if (candidates.Count == 0)
            {
                throw new SkipTestException(
                    "no local FFmpeg fixture; set RENDERIST_TEST_FFMPEG_DIR to a directory containing " +
                    "ffmpeg.exe (binaries are intentionally not committed)");
            }

            return FfmpegVideoPipelineTests.MakeRealIdentity(candidates[0]);
        }

        private static FfmpegVideoFrame Frame(int width, int height)        {
            long length = (long)width * height * 3;
            var buffer = new byte[length];
            for (long i = 0; i < length; i++)
                buffer[i] = (byte)(i & 0xFF);
            return FfmpegVideoFrame.FromBuffer(buffer);
        }

        private static T Await<T>(Task<T> task, string what)
        {
            if (task == null)
                throw new Exception(what + ": task is null");

            if (!task.Wait(TimeoutMs))
                throw new Exception(what + ": timed out after " + TimeoutMs + "ms");

            return task.Result;
        }

        private static void WithMainThreadContext(Action body)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
                body();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        /// <summary>
        /// 端到端用例使用的"真实主线程模型"：Post 只入队，由测试在**主线程**显式 Pump。
        /// 直接用 InlineContext 会让 Post 回调在后台写线程上同步执行，从而被正确判为
        /// "wrong thread" —— 那不是被测行为，而是测试自身缺少主线程泵。
        /// </summary>
        private static void WithPumpedMainThreadContext(Action<PumpContext> body)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                var pump = new PumpContext();
                SynchronizationContext.SetSynchronizationContext(pump);
                body(pump);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private sealed class InlineSyncContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state)
            {
                d(state);
            }
        }

        private sealed class PumpContext : SynchronizationContext
        {
            private readonly System.Collections.Generic.Queue<Action> _callbacks =
                new System.Collections.Generic.Queue<Action>();

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_callbacks) _callbacks.Enqueue(() => callback(state));
            }

            internal void Pump()
            {
                while (true)
                {
                    Action action;
                    lock (_callbacks)
                    {
                        if (_callbacks.Count == 0) return;
                        action = _callbacks.Dequeue();
                    }
                    action();
                }
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
