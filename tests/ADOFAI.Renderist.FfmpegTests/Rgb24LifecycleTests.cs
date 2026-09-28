using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;
using Status = ADOFAI.Renderist.Export.DeterministicFrameScheduler.SchedulerStatus;

namespace ADOFAI.Renderist.FfmpegTests
{
    internal static class Rgb24LifecycleTests
    {
        public static void Run()
        {
            TestKit.Run("rgb24 lifecycle: pre-entry delivery resumes InitializationHold without gameplay Prepare", () =>
            {
                var f = new Fixture();
                f.Begin(7, 0);
                var envelope = f.FinishWrite();
                Check(Rgb24TransactionOutcome.Committed, f.Consume(envelope));
                Status resumed = DeterministicFrameScheduler.ResumeAfterRgb24Delivery(true);
                Check(Status.InitializationHold, resumed);
                TestKit.Check(!DeterministicFrameScheduler.CanPrepareGameplay(resumed, true, false), "no gameplay timeline");
            });
            TestKit.Run("rgb24 lifecycle: final pre-entry frame keeps handoff as the only gameplay entry", () =>
            {
                Status resumed = DeterministicFrameScheduler.ResumeAfterRgb24Delivery(true);
                TestKit.Check(!DeterministicFrameScheduler.CanPrepareGameplay(resumed, false, true), "handoff must set Capturing");
                TestKit.Check(!DeterministicFrameScheduler.CanPrepareGameplay(Status.Capturing, true, true), "pre-entry location");
                TestKit.Check(!DeterministicFrameScheduler.CanPrepareGameplay(Status.Capturing, false, false), "timeline required");
                TestKit.Check(DeterministicFrameScheduler.CanPrepareGameplay(Status.Capturing, false, true), "handoff completed");
            });
            TestKit.Run("rgb24 lifecycle: gameplay delivery permits the next distinct frame", () =>
            {
                var f = new Fixture();
                f.Begin(7, 0);
                long token = f.Transaction.LeaseTokenId;
                Check(Rgb24TransactionOutcome.Committed, f.Consume(f.FinishWrite()));
                Check(Status.Capturing, DeterministicFrameScheduler.ResumeAfterRgb24Delivery(false));
                f.Transport.NextWrite();
                f.Begin(7, 1);
                TestKit.Check(f.Transaction.LeaseTokenId != token, "fresh token");
                Check(Rgb24TransactionOutcome.Committed, f.Consume(f.FinishWrite()));
                Check(2L, f.Context.Pool.ReleaseCount);
            });
            TestKit.Run("rgb24 lifecycle: PNG and log-only retain pre-entry and gameplay gates", () =>
            {
                TestKit.Check(!DeterministicFrameScheduler.CanPrepareGameplay(Status.InitializationHold, true, false), "pre-entry");
                TestKit.Check(DeterministicFrameScheduler.CanObservePreparedGameplay(Status.Capturing, false, true), "legacy gameplay postfix");
                TestKit.Check(!DeterministicFrameScheduler.CanObservePreparedGameplay(Status.AwaitingEOF, true, true), "no pre-entry autoplay");
                TestKit.Check(DeterministicFrameScheduler.CanObservePreparedGameplay(Status.AwaitingEOF, false, true), "prepared RGB24 autoplay");
                TestKit.Check(!DeterministicFrameScheduler.CanObservePreparedGameplay(Status.AwaitingDelivery, false, false), "no repeated autoplay");
                TestKit.Check(DeterministicFrameScheduler.IsPlaybackPhase(Status.AwaitingDelivery), "input protection stays active");
            });

            foreach (bool afterEof in new[] { false, true })
                TestKit.Run("rgb24 cancel: abort before accept afterEof=" + afterEof, () =>
                {
                    var f = new Fixture();
                    f.Prepare();
                    if (afterEof) f.Eof();
                    TestKit.Check(!f.Context.TryStopAndDrain("cancel", out _), "pipeline still pending");
                    Check(1L, f.Context.Pool.ReleaseCount);
                    TestKit.Check(!f.Context.HasFrameOwnership && f.Context.HasPipelineOwnership, "separate facts");
                    f.Cleanup();
                    TestKit.Check(f.Context.TryStopAndDrain("cancel", out _), "converged");
                    Check(1L, f.Context.Pool.ReleaseCount);
                });

            foreach (bool cleanupFirst in new[] { false, true })
            foreach (bool success in new[] { false, true })
                TestKit.Run("rgb24 cancel: independent barriers cleanupFirst=" + cleanupFirst + " success=" + success, () =>
                {
                    var f = new Fixture();
                    f.Begin();
                    byte[] buffer = f.Transaction.CurrentFrame.SegmentBuffer(0);
                    TestKit.Check(!f.Context.TryStopAndDrain("cancel", out _), "both tasks pending");
                    TestKit.Check(f.Context.Pool.OutstandingLease.DeliveryPinned, "pinned until actual Completion");
                    Rgb24DeliveryContext slot = f.Context;
                    var next = new Fixture();
                    TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, next.Context, false, out _), "re-arm blocked");
                    if (cleanupFirst) f.Cleanup(); else f.Transport.Complete(success);
                    TestKit.Check(!f.Context.TryStopAndDrain("cancel", out _), "one barrier is insufficient");
                    Check(cleanupFirst, f.Context.HasFrameOwnership);
                    Check(!cleanupFirst, f.Context.HasPipelineOwnership);
                    if (cleanupFirst)
                    {
                        Check(buffer, f.Context.Pool.OutstandingLease.Frame.SegmentBuffer(0));
                        f.Transport.Complete(success);
                    }
                    else f.Cleanup();
                    TestKit.Check(f.Context.TryStopAndDrain("cancel", out _), "both converged");
                    Check(1L, f.Context.Pool.ReleaseCount);
                    TestKit.Check(Rgb24DeliveryContext.TryReplace(ref slot, next.Context, false, out _), "re-arm allowed");
                });

            TestKit.Run("rgb24 cancel: a main-thread Post retires the frame even without another UMM Tick", () =>
            {
                var f = new Fixture(); f.Begin();
                TestKit.Check(!f.Context.TryStopAndDrain("mod-disabled", out _), "pending write");
                f.Transport.Complete(true);
                f.Pump.Pump();
                Check(1L, f.Context.Pool.ReleaseCount);
                TestKit.Check(!f.Context.HasFrameOwnership && f.Context.HasPipelineOwnership, "only pipeline remains");
                f.Cleanup();
                TestKit.Check(!f.Context.HasResidualOwnership, "cleanup evidence is independently observable");
            });
            TestKit.Run("rgb24 cancel: completed but unconsumed notification can retire without Post delivery", () =>
            {
                var f = new Fixture();
                f.Begin();
                f.Transport.Complete(true);
                f.Cleanup();
                TestKit.Check(f.Context.TryStopAndDrain("cancel", out _), "does not require context pump");
                Check(1L, f.Context.Pool.ReleaseCount);
                f.Pump.Pump();
                TestKit.Check(!f.Transaction.TryTakePendingEnvelope(out _, out _), "old notification detached");
            });
            TestKit.Run("rgb24 cancel: Post throws but completed frame and pipeline still converge", () =>
            {
                var f = new Fixture(new ThrowingContext());
                f.Begin();
                f.Transport.Complete(true);
                TestKit.Check(!f.Transaction.TryTakePendingEnvelope(out _, out string error), "no valid notification");
                TestKit.Check(error.StartsWith("rgb24-bridge-post-failed"), error);
                f.Cleanup();
                TestKit.Check(f.Context.TryStopAndDrain("bridge-failed", out _), "retired via actual Task");
                Check(1L, f.Context.Pool.ReleaseCount);
            });
            TestKit.Run("rgb24 cancel: wrong-thread Post is rejected and later drained on main thread", () =>
            {
                var f = new Fixture(new InlineContext());
                f.Begin();
                Task.Run(() => f.Transport.Complete(true)).GetAwaiter().GetResult();
                TestKit.Check(!f.Transaction.TryTakePendingEnvelope(out _, out string error), "wrong-thread callback blocked");
                Check("rgb24-bridge-wrong-thread", error);
                f.Cleanup();
                TestKit.Check(f.Context.TryStopAndDrain("bridge-failed", out _), "main-thread fallback");
            });
            TestKit.Run("rgb24 cancel: late old-generation Post cannot touch a new-generation lease", () =>
            {
                var old = new Fixture();
                old.Begin(7, 0);
                byte[] oldBuffer = old.Transaction.CurrentFrame.SegmentBuffer(0);
                var next = new Fixture();
                next.Begin(8, 0); // new context can exist; production Arm still rejects it until old resources converge
                old.Context.TryStopAndDrain("cancel", out _);
                old.Transport.Complete(true);
                old.Cleanup();
                TestKit.Check(old.Context.TryStopAndDrain("cancel", out _), "old frame retired");
                old.Pump.Pump(); // delayed callback runs after new generation owns its own frame
                TestKit.Check(next.Context.Pool.OutstandingLease.DeliveryPinned, "new lease remains pinned");
                TestKit.Check(!ReferenceEquals(oldBuffer, next.Transaction.CurrentFrame.SegmentBuffer(0)), "no cross-context reuse");
                Check(0L, next.Context.Pool.ReleaseCount);
                Check(Rgb24TransactionOutcome.Committed, next.Consume(next.FinishWrite()));
                Check(1L, old.Context.Pool.ReleaseCount);
            });
            TestKit.Run("rgb24 gate: cleanup residual and active scheduler both refuse context replacement", () =>
            {
                var f = new Fixture();
                f.Cleanup(true);
                f.Context.TryStopAndDrain("cancel", out _);
                Rgb24DeliveryContext slot = f.Context;
                TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, null, false, out _), "cleanup result reports residual");
                Check(f.Context, slot);
                slot = null;
                TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, new Fixture().Context, true, out _), "active scheduler");
            });
            TestKit.Run("rgb24 gate: faulted cleanup cannot be treated as released resources", () =>
            {
                var f = new Fixture();
                f.Transport.Cleanup.TrySetException(new IOException("cleanup failed"));
                TestKit.Check(!f.Context.TryStopAndDrain("cancel", out _), "fault is residual");
                TestKit.Check(f.Context.HasPipelineOwnership, "gate retained");
            });
            TestKit.Run("rgb24 identity: matching fake envelope before accept is rejected", () =>
            {
                var f = new Fixture();
                f.Prepare();
                f.Eof();
                var frame = f.Transaction.CurrentFrame;
                var fake = new Rgb24DeliveryEnvelope(frame.Generation, frame.AbsoluteFrameIndex, frame.TokenId, Success());
                Check(Rgb24TransactionOutcome.RejectedCompletionIdentity, f.Consume(fake));
                Check(0L, f.Context.Pool.ReleaseCount);
            });
            TestKit.Run("rgb24 identity: authentic completion requires scheduler AwaitingDelivery", () =>
            {
                var f = new Fixture(); f.Begin();
                var envelope = f.FinishWrite();
                Check(Rgb24TransactionOutcome.RejectedWrongPhase,
                    f.Transaction.ConsumeEnvelope(false, envelope, out _, out _));
                Check(0L, f.Context.Pool.ReleaseCount);
                Check(Rgb24TransactionOutcome.Committed, f.Consume(envelope));
            });
            TestKit.Run("rgb24 identity: forged Task or transaction identity cannot unlock a real write", () =>
            {
                var f = new Fixture(); f.Begin();
                var envelope = f.FinishWrite();
                var wrongTask = new Rgb24DeliveryEnvelope(7, 0, envelope.TokenId, Success(),
                    envelope.FrameIdentity, Task.FromResult(Success()));
                Check(Rgb24TransactionOutcome.RejectedCompletionIdentity, f.Consume(wrongTask));
                var wrongFrame = new Rgb24DeliveryEnvelope(7, 0, envelope.TokenId, Success(),
                    new object(), envelope.Completion);
                Check(Rgb24TransactionOutcome.RejectedCompletionIdentity, f.Consume(wrongFrame));
                Check(0L, f.Context.Pool.ReleaseCount);
                Check(Rgb24TransactionOutcome.Committed, f.Consume(envelope));
            });
            TestKit.Run("rgb24 identity: wrong-thread consume cannot mutate or release the lease", () =>
            {
                var f = new Fixture(); f.Begin();
                var envelope = f.FinishWrite();
                Check(Rgb24TransactionOutcome.RejectedWrongThread,
                    Task.Run(() => f.Consume(envelope)).GetAwaiter().GetResult());
                Check(0L, f.Context.Pool.ReleaseCount);
                Check(Rgb24TransactionOutcome.Committed, f.Consume(envelope));
            });
            TestKit.Run("rgb24 identity: pin precedes transport and immediate Completion registration", () =>
            {
                var f = new Fixture(new InlineContext());
                f.Transport.BeforeReturn = () =>
                {
                    TestKit.Check(f.Context.Pool.OutstandingLease.DeliveryPinned, "pin before any L2 read");
                    f.Transport.Complete(true);
                };
                f.Begin();
                TestKit.Check(f.Transaction.TryTakePendingEnvelope(out var envelope, out _), "immediate completion available");
                Check(Rgb24TransactionOutcome.Committed, f.Consume(envelope));
                Check(1L, f.Context.Pool.ReleaseCount);
            });
            TestKit.Run("rgb24 identity: reentrant abort during transport cannot release before acceptance returns", () =>
            {
                var f = new Fixture();
                f.Transport.BeforeReturn = () =>
                {
                    TestKit.Check(!f.Transaction.TryAbort(out _), "acceptance unresolved");
                    Check(0L, f.Context.Pool.ReleaseCount);
                };
                f.Begin();
                f.Transport.Complete(true);
                f.Pump.Pump();
                TestKit.Check(!f.Transaction.TryTakePendingEnvelope(out _, out _), "cancelled write never permits Commit");
                Check(1L, f.Context.Pool.ReleaseCount);
            });
            TestKit.Run("rgb24 identity: late duplicate never releases or misclassifies the next frame", () =>
            {
                var f = new Fixture(); f.Begin();
                var old = f.FinishWrite();
                Check(Rgb24TransactionOutcome.Committed, f.Consume(old));
                f.Transport.NextWrite(); f.Begin(7, 1);
                Check(Rgb24TransactionOutcome.RejectedDuplicateCompletion, f.Consume(old));
                Check(1L, f.Context.Pool.ReleaseCount);
                Check(Rgb24TransactionOutcome.Committed, f.Consume(f.FinishWrite()));
                Check(2L, f.Context.Pool.ReleaseCount);
            });
            TestKit.Run("rgb24 ownership: reset refuses a pinned lease", () =>
            {
                var f = new Fixture(); f.Begin();
                bool poolThrew = false, transactionThrew = false;
                try { f.Context.Pool.Reset(); } catch (InvalidOperationException) { poolThrew = true; }
                try { f.Transaction.Reset(); } catch (InvalidOperationException) { transactionThrew = true; }
                TestKit.Check(poolThrew && transactionThrew, "both ownership guards");
                TestKit.Check(f.Context.Pool.OutstandingLease.DeliveryPinned, "still owned");
            });
            TestKit.Run("rgb24 ownership: immediate rejection is unpinned and cancel releases exactly once", () =>
            {
                var f = new Fixture();
                f.Transport.Reject = true;
                f.Prepare(); f.Eof();
                Check(Rgb24TransactionOutcome.RejectedTransportRejected, f.Transaction.TryBeginDelivery(out _, out _));
                TestKit.Check(!f.Context.Pool.OutstandingLease.DeliveryPinned, "known rejection");
                f.Cleanup();
                TestKit.Check(f.Context.TryStopAndDrain("rejected", out _), "abort releases rejected frame");
                f.Context.TryStopAndDrain("rejected", out _);
                Check(1L, f.Context.Pool.ReleaseCount);
            });
            TestKit.Run("rgb24 watchdog: pending IO has no observation-count timeout", () =>
            {
                var f = new Fixture(); f.Begin();
                for (int i = 0; i < 1000; i++)
                {
                    TestKit.Check(!f.Transaction.TryTakePendingEnvelope(out _, out string error), "not complete");
                    Check(null, error);
                }
                Check(Rgb24TransactionOutcome.Committed, f.Consume(f.FinishWrite()));
            });
            TestKit.Run("rgb24 watchdog: lost Post fails after completed Task and can still retire", () =>
            {
                var f = new Fixture(new DroppingContext()); f.Begin();
                f.Transport.Complete(true);
                for (int i = 0; i < Rgb24FrameTransaction.BridgeStallTickLimit; i++)
                {
                    f.Transaction.TryTakePendingEnvelope(out _, out string error);
                    Check(null, error);
                }
                f.Transaction.TryTakePendingEnvelope(out _, out string stalled);
                Check("rgb24-bridge-notification-stalled", stalled);
                f.Cleanup();
                TestKit.Check(f.Context.TryStopAndDrain("bridge-stalled", out _), "no pinned leak");
            });

            foreach (var geometry in new[] { new[] { 1, 8 }, new[] { 3, 8 }, new[] { 8, 8 },
                new[] { 9, 8 }, new[] { 19, 8 }, new[] { 16, 8 }, new[] { 5, 1 } })
            {
                int h = geometry[0], b = geometry[1];
                TestKit.Run("rgb24 bands: full source coverage and absolute overlap h=" + h + " rows=" + b,
                    () => VerifyBands(h, b));
            }
            TestKit.Run("rgb24 bands: final band near int.MaxValue cannot overflow", () =>
            {
                TestKit.Check(Rgb24ReadbackBand.TryPlan(int.MaxValue, 1024, int.MaxValue - 1, out var band), "last band");
                Check(int.MaxValue - 1024, band.StartRow);
                Check(int.MaxValue, band.NextRow);
                TestKit.Check(!Rgb24ReadbackBand.TryPlan(int.MaxValue, 1024, band.NextRow, out _), "terminates");
            });
            ProductionSourceContracts();
        }

        private static void VerifyBands(int height, int rows)
        {
            Rgb24FrameLayout.TryCreate(3, height, 5, out var layout, out _); // 9-byte row, 5-byte segment
            var pool = new Rgb24FrameBufferPool(layout);
            pool.TryAcquire(1, 0, out var lease, out _);
            int[] seen = new int[height];
            int next = 0, iterations = 0;
            while (Rgb24ReadbackBand.TryPlan(height, rows, next, out var band))
            {
                TestKit.Check(++iterations <= height, "must advance and terminate");
                byte[] staging = new byte[band.RowCount * 9];
                for (int r = 0; r < band.RowCount; r++)
                {
                    int source = band.StartRow + r;
                    seen[source]++;
                    for (int x = 0; x < 9; x++) staging[r * 9 + x] = (byte)(source * 9 + x);
                    lease.Frame.CopyStagedSourceRowToDelivery(source, staging, r * 9);
                }
                next = band.NextRow;
            }
            for (int r = 0; r < height; r++)
            {
                TestKit.Check(seen[r] > 0, "no missing source row " + r);
                for (int x = 0; x < 9; x++)
                {
                    long offset = (height - 1 - r) * 9L + x;
                    layout.TryLocate(offset, out int segment, out int inside);
                    Check((byte)(r * 9 + x), lease.Frame.SegmentBuffer(segment)[inside]);
                }
            }
            if (height > rows && height % rows != 0)
                TestKit.Check(Array.Exists(seen, count => count == 2), "last full band overlaps");
        }

        // 可执行源码接线检查：验证 pure guards 被真实 Unity 路径使用，不模拟 ReadPixels 或游戏行为。
        private static void ProductionSourceContracts()
        {
            TestKit.Run("rgb24 wiring: scheduler uses lifecycle guards and terminal residual pump", () =>
            {
                string scheduler = Source("Export/DeterministicFrameScheduler.cs");
                string delivery = Method(scheduler, "private static void TickAwaitingDelivery()");
                TestKit.Check(delivery.Contains("ResumeAfterRgb24Delivery(_preEntryCapturing)"), "real completion resumes lifecycle");
                TestKit.Check(delivery.Contains("_status == SchedulerStatus.AwaitingDelivery, envelope"), "real consume phase check");
                TestKit.Check(!delivery.Contains("CheckArmedCaptureWatchdogs"), "no IO watchdog");
                string prefix = Method(scheduler, "private static void ConductorUpdatePrefix()");
                TestKit.Check(prefix.Contains("CanPrepareGameplay(_status, _preEntryCapturing, _timeline != null)"), "handoff guard wired");
                string postfix = Method(scheduler, "private static void ConductorUpdatePostfix()");
                TestKit.Check(postfix.Contains("CanObservePreparedGameplay") && postfix.Contains("_lastAutoplayFrameIndex == _outputFrameIndex"), "autoplay exactly once");
                TestKit.Check(Method(scheduler, "private static bool RestoreAll(out string error)").Contains("TryStopAndDrain"), "cancel owns both barriers");
                string handoff = Method(scheduler, "private static void TickInitializationHold()");
                TestKit.Check(handoff.Contains("if (_preEntryCapturing && _pendingCapture)"), "handoff waits for pending capture");
                TestKit.Check(handoff.IndexOf("_timeline = new MasterTimeline") < handoff.IndexOf("_status = SchedulerStatus.Capturing"), "timeline before gameplay");
                string arm = Method(scheduler, "public static bool ArmRgb24Delivery(");
                TestKit.Check(arm.Contains("_rgb24DeliveryArmed") && arm.Contains("Rgb24DeliveryContext.TryReplace"), "Arm uses tested ownership gate");
                TestKit.Check(arm.IndexOf("EnsurePreviousRunCleanedUp") < arm.IndexOf("TryReplace"), "old Unity and RGB24 ownership first");
                TestKit.Check(Method(scheduler, "public static string TryStart(").Contains("EnsurePreviousRunCleanedUp"), "direct start residual gate");
                TestKit.Check(Method(scheduler, "internal static string ValidatePreStartConditions(").Contains("EnsurePreviousRunCleanedUp"), "controller pre-start gate");
                string controller = Method(Source("Export/EditorExportController.cs"), "public static void Tick()");
                TestKit.Check(controller.IndexOf("TickResidualOwnership") < controller.IndexOf("if (s == null)"), "terminal session still drains");
                string update = Method(Source("ModEntry.cs"), "private static void OnUpdate(");
                TestKit.Check(update.IndexOf("TickResidualOwnership") < update.IndexOf("if (!Enabled)"), "disable fallback still drains");
            });
            TestKit.Run("rgb24 wiring: PNG and RGB24 preserve the shared GPU state contract", () =>
            {
                string driver = Source("Export/FrameCaptureDriver.cs");
                string blit = Method(driver, "private RenderTexture BlitDownsampleChain(");
                TestKit.Check(blit.Contains("if (trackSrgbWrite)\n                    GL.sRGBWrite = GraphicsFormatUtility.IsSRGBFormat(readback.graphicsFormat);"),
                    "Linear readback target state restored; Gamma does not write it");
                foreach (string signature in new[] { "private bool TryRunGpuCapturePipeline(", "private bool TryRunRgb24CapturePipeline(" })
                {
                    string method = Method(driver, signature);
                    TestKit.Check(method.Contains("_linearColorSpace && _downsampleTargets != null"), "Linear-only tracking");
                    TestKit.Check(method.IndexOf("TrySaveGpuState") < method.IndexOf("BlitDownsampleChain"), "save before GPU mutation");
                    TestKit.Check(method.IndexOf("TryRestoreGpuState") > method.IndexOf("catch (Exception"), "restore after readback exception");
                    Check(1, method.Split(new[] { "BlitDownsampleChain(" }, StringSplitOptions.None).Length - 1);
                }
                string capture = Method(driver, "private void CaptureNow(");
                TestKit.Check(capture.IndexOf("if (!_imageOutputEnabled)") < capture.IndexOf("EnsureTexture()"), "log-only no readback");
                TestKit.Check(capture.IndexOf("_texture.Apply(false)") < capture.IndexOf("_texture.EncodeToPNG()"), "PNG Apply retained");
                TestKit.Check(capture.Contains("File.WriteAllBytes(filePath, png)"), "PNG write retained");
                Check(1, driver.Split(new[] { "yield return new WaitForEndOfFrame()" }, StringSplitOptions.None).Length - 1);
                TestKit.Check(Method(driver, "private void ReadBackRgb24Bands(").Contains("Rgb24ReadbackBand.TryPlan"), "tested planner is wired");
            });
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
            return source.Substring(start, end - start);
        }
        private static void Check(object expected, object actual) => TestKit.CheckEqual(expected, actual, "contract");
        private static Rgb24FrameWriteOutcome Success() => new Rgb24FrameWriteOutcome(true, null, null, 1);

        private sealed class Fixture
        {
            internal readonly ControlledTransport Transport = new ControlledTransport();
            internal readonly PumpContext Pump = new PumpContext();
            internal readonly Rgb24DeliveryContext Context;
            internal Rgb24FrameTransaction Transaction => Context.Transaction;
            internal Fixture(SynchronizationContext context = null)
            {
                var original = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(context ?? Pump);
                    Rgb24MainThreadBridge.TryCapture(out var bridge, out _);
                    Rgb24FrameLayout.TryCreate(3, 2, 5, out var layout, out _);
                    Context = new Rgb24DeliveryContext(layout, Transport, Transport, bridge);
                }
                finally { SynchronizationContext.SetSynchronizationContext(original); }
            }
            internal void Prepare(long generation = 7, long index = 0)
                => Check(Rgb24TransactionOutcome.Accepted, Transaction.TryBeginFrame(generation, index, out _));
            internal void Eof()
                => Check(Rgb24TransactionOutcome.Accepted, Transaction.TryCompleteEof(Transaction.Generation, Transaction.FrameIndex, out _, out _));
            internal void Begin(long generation = 7, long index = 0)
            {
                Prepare(generation, index); Eof();
                Check(Rgb24TransactionOutcome.Accepted, Transaction.TryBeginDelivery(out _, out _));
            }
            internal Rgb24DeliveryEnvelope FinishWrite()
            {
                Transport.Complete(true); Pump.Pump();
                TestKit.Check(Transaction.TryTakePendingEnvelope(out var envelope, out string error), error ?? "expected envelope");
                return envelope;
            }
            internal Rgb24TransactionOutcome Consume(Rgb24DeliveryEnvelope envelope)
                => Transaction.ConsumeEnvelope(true, envelope, out _, out _);
            internal void Cleanup(bool residual = false)
                => Transport.Cleanup.TrySetResult(new FfmpegVideoOutcome { State = FfmpegVideoPipelineState.Cancelled, ResidualOwnership = residual });
        }
        private sealed class ControlledTransport : IRgb24FrameTransport, IRgb24PipelineLifetime
        {
            internal TaskCompletionSource<Rgb24FrameWriteOutcome> Write = new TaskCompletionSource<Rgb24FrameWriteOutcome>();
            internal readonly TaskCompletionSource<FfmpegVideoOutcome> Cleanup = new TaskCompletionSource<FfmpegVideoOutcome>();
            internal Action BeforeReturn;
            internal bool Reject;
            public Task<FfmpegVideoOutcome> CleanupTask => Cleanup.Task;
            public void RequestStop(string reason) { }
            public Rgb24TransportAttempt TryBeginWrite(OwnedRgb24Frame frame)
            {
                BeforeReturn?.Invoke();
                return Reject ? Rgb24TransportAttempt.Rejected("not-running", null) : Rgb24TransportAttempt.Accepted(Write.Task);
            }
            internal void Complete(bool success) => Write.TrySetResult(success ? Success() : Rgb24FrameWriteOutcome.Failure("pipe-poisoned", "test"));
            internal void NextWrite() { Write = new TaskCompletionSource<Rgb24FrameWriteOutcome>(); }
        }
        private sealed class PumpContext : SynchronizationContext
        {
            private readonly Queue<Action> _callbacks = new Queue<Action>();
            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_callbacks) _callbacks.Enqueue(() => callback(state));
            }
            internal void Pump()
            {
                while (true)
                {
                    Action action;
                    lock (_callbacks) { if (_callbacks.Count == 0) return; action = _callbacks.Dequeue(); }
                    action();
                }
            }
        }
        private sealed class InlineContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object state) => callback(state);
        }
        private sealed class ThrowingContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object state) => throw new InvalidOperationException("post unavailable");
        }
        private sealed class DroppingContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object state) { }
        }
    }
}
