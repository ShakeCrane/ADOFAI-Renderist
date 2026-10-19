using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;

namespace ADOFAI.Renderist.FfmpegTests
{
    internal static class FrameLocalDeliverySafetyTests
    {
        internal static void Run()
        {
            EvidenceAndEmergency();
            TestKit.Run("f1 safety: publication between cancel and retirement cannot orphan an open claim", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                {
                    c.Begin(); var gate = c.NotificationGate;
                    TestKit.Check(!gate.TryGetPublished(out var staleNull) && staleNull == null, "pre-publication read");
                    TestKit.Check(gate.TryAcceptCancel("controlled-publication-race", false, out _), "cancel accepted");
                    c.Write.SetResult(new Rgb24FrameWriteOutcome(true, null, null, 1));
                    TestKit.Check(gate.TryGetPublished(out var canonical), "producer published between cancel and close");
                    TestKit.Check(gate.CloseForAbort() && gate.IsClosed, "abort close independent of stale reference");
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.AlreadyClaimed, gate.TryClaim(canonical).Status, "no permit after abort");
                    TestKit.Check(c.Transaction.TryAbort(out var error), "actual frame retirement: " + error);
                    TestKit.Check(!c.Pool.HasOutstandingLease && gate.TryDisposeReadyHandle(out error), "closed, releasable gate: " + error);
                }
            });
            TestKit.Run("f1 safety: second process attempt is rejected before invoking its launcher", () =>
            {
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var attempts = typeof(FrameLocalDeliveryProbe).GetField("_processStartAttempts", flags);
                var error = typeof(FrameLocalDeliveryProbe).GetField("_lastError", flags);
                object priorAttempts = attempts.GetValue(null), priorError = error.GetValue(null);
                try
                {
                    attempts.SetValue(null, 0);
                    int launches = 0;
                    var start = FrameLocalDeliveryProbe.WrapProcessStart((_, __, ___) => { launches++; return null; });
                    TestKit.Check(start("unused", "", "") == null && start("unused", "", "") == null, "attempts return no process");
                    TestKit.CheckEqual(1, launches, "second attempt never launches");
                    TestKit.CheckEqual("f1-second-process-start-attempt", error.GetValue(null) as string, "explicit diagnostic fault");
                }
                finally { attempts.SetValue(null, priorAttempts); error.SetValue(null, priorError); }
            });
            TestKit.Run("f1 safety: source SHA256 covers every byte across unequal segments", () =>
            {
                Rgb24FrameLayout.TryCreate(3, 2, 7, out var layout, out _);
                var snapshot = new byte[18];
                for (int i = 0; i < snapshot.Length; i++) snapshot[i] = (byte)(i + 1);
                var segments = new byte[layout.SegmentCount][];
                for (int i = 0; i < segments.Length; i++)
                {
                    segments[i] = new byte[layout.SegmentLength(i)];
                    Buffer.BlockCopy(snapshot, (int)layout.SegmentOffset(i), segments[i], 0, segments[i].Length);
                }
                var frame = new OwnedRgb24Frame(1, 0, layout, segments, 1);
                string expected;
                using (var hash = SHA256.Create()) expected = BitConverter.ToString(hash.ComputeHash(snapshot)).Replace("-", "");
                TestKit.CheckEqual(expected, FrameLocalDeliveryProbeSlot.HashSource(frame), "independent concatenated snapshot");
                segments[segments.Length - 1][segments[segments.Length - 1].Length - 1] ^= 0xff;
                TestKit.Check(expected != FrameLocalDeliveryProbeSlot.HashSource(frame), "last byte of short final segment included");
            });
            TestKit.Run("f1 safety: inner Dispose failure remains residual and is not retried or hidden", () =>
            {
                var inner = new DeferredIo { ThrowOnDispose = true };
                var gate = new FrameLocalDeliveryStreamGate(inner, new FrameLocalDeliveryEvidence());
                bool failed = false;
                try { gate.Dispose(); } catch (IOException) { failed = true; }
                gate.Dispose();
                TestKit.Check(failed && inner.Disposals == 1 && !gate.OwnershipSettled && gate.DisposeError.Contains("injected-dispose"), "honest residual after failed release");
            });
            TestKit.Run("f1 safety: successful IO cannot authorize delivery after exact peer exit", () =>
            {
                var inner = new MemoryStream();
                var evidence = new FrameLocalDeliveryEvidence();
                using (var gate = new FrameLocalDeliveryStreamGate(inner, evidence, null, () => true))
                {
                    gate.Open("release");
                    Task write = gate.WriteAsync(new byte[6], 0, 6, CancellationToken.None);
                    ObserveTerminal(write);
                    TestKit.Check(write.IsFaulted && inner.Length == 6, "real inner succeeded; diagnostic exit check failed");
                    TestKit.Check(evidence.Dump().Contains("encoder-exit-observed") && evidence.Dump().Contains("inner-flush-end=ok"), "IO success and exit fault both retained");
                }
            });
            TestKit.Run("f1 safety: stdin owns inner and repeated Dispose closes it once", () =>
            {
                var inner = new DeferredIo();
                var gate = new FrameLocalDeliveryStreamGate(inner, new FrameLocalDeliveryEvidence());
                gate.Dispose(); gate.Open("late-release"); gate.Dispose();
                TestKit.CheckEqual(1, inner.Disposals, "inner owner exactly once");
                TestKit.Check(!gate.CanWrite && !inner.CanWrite && gate.OwnershipSettled, "both closed");
                TestKit.Check(!gate.GateEntered, "opening/disposing is not a write-entry witness");
            });
            TestKit.Run("f1 safety: Dispose wakes a managed hold without ever starting inner IO", () =>
            {
                var inner = new DeferredIo();
                var gate = new FrameLocalDeliveryStreamGate(inner, new FrameLocalDeliveryEvidence());
                Task write = gate.WriteAsync(new byte[6], 0, 6, CancellationToken.None);
                TestKit.Check(gate.GateEntered && !write.IsCompleted, "actual held write");
                gate.Dispose();
                ObserveTerminal(write);
                TestKit.Check(write.IsFaulted && inner.Writes == 0 && gate.OwnershipSettled, "no post-dispose inner call");
            });
            TestKit.Run("f1 safety: cancellation after inner starts cannot truncate its write or flush lifetime", () =>
            {
                var inner = new DeferredIo();
                var gate = new FrameLocalDeliveryStreamGate(inner, new FrameLocalDeliveryEvidence());
                using (var cancel = new CancellationTokenSource())
                {
                    gate.Open("release");
                    Task write = gate.WriteAsync(new byte[6], 0, 6, cancel.Token);
                    TestKit.CheckEqual(1, inner.Writes, "inner really entered");
                    cancel.Cancel(); gate.Dispose(); gate.Dispose();
                    TestKit.Check(!write.IsCompleted && !gate.OwnershipSettled, "Dispose/token do not synthesize terminal");
                    inner.WriteResult.TrySetResult(true);
                    TestKit.Check(inner.FlushEntered.Task.Wait(2000), "flush starts after write terminal");
                    TestKit.Check(!write.IsCompleted, "outer still holds bytes across inner flush");
                    inner.FlushResult.TrySetResult(true);
                    TestKit.Check(write.Wait(2000), "outer terminal after real inner terminal");
                    TestKit.Check(gate.OwnershipSettled && inner.Disposals == 1, "resource convergence");
                }
            });
            TestKit.Run("f1 safety: failed inner write is retained and does not invent a successful flush", () =>
            {
                var inner = new DeferredIo();
                var evidence = new FrameLocalDeliveryEvidence();
                using (var gate = new FrameLocalDeliveryStreamGate(inner, evidence))
                {
                    gate.Open("release");
                    Task write = gate.WriteAsync(new byte[6], 0, 6, CancellationToken.None);
                    inner.WriteResult.TrySetException(new IOException("native-write-failure"));
                    ObserveTerminal(write);
                    TestKit.Check(write.IsFaulted && inner.Flushes == 0, "failure is not success");
                    TestKit.Check(!evidence.Dump().Contains("inner-flush-end=ok"), "no false successful IO evidence");
                }
            });
        }

        private static void EvidenceAndEmergency()
        {
            TestKit.Run("f1 safety: emergency after a real claim preserves actual Commit and fails the diagnostic", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                using (var emergency = new EventWaitHandle(false, EventResetMode.ManualReset))
                {
                    c.Begin(); var gate = c.NotificationGate; c.Write.SetResult(new Rgb24FrameWriteOutcome(true, null, null, 1));
                    int commits = 0;
                    c.Slot.RunSingleFrame(c.Transaction, c.Transaction.Generation, 0, true, emergency, 1000,
                        () => { commits++; c.Slot.RecordActualCommit(c.Transaction.Generation, 0, -1); emergency.Set(); },
                        (_, __) => { });
                    TestKit.Check(commits == 1 && c.Slot.CommitCount == 1 && c.Slot.EvidenceFailed, "late emergency cannot erase an actual commit or report diagnostic success");
                    TestKit.Check(gate.ClaimClosedAsSuccess, "original permit remains immutable");
                }
            });
            TestKit.Run("f1 safety: local soft budget starts only at WaitingArmed", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                using (var release = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var cancel = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var emergency = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var ack = new EventWaitHandle(false, EventResetMode.ManualReset))
                {
                    c.Begin();
                    using (var monitor = new FrameLocalDeliveryProbeMonitor(c.Slot, c.Slot.Evidence,
                        release, cancel, shutdown, emergency, ack, _ => { }, 40, 1000))
                    {
                        monitor.Start(); release.Set();
                        TestKit.Check(ack.WaitOne(2000), "monitor is running");
                        TestKit.Check(!c.NotificationGate.WaitReady(120), "setup may exceed 40ms without consuming wait budget");
                        TestKit.Check(!c.NotificationGate.CancelAccepted, "no pre-wait cancellation");
                        c.RunEof();
                        TestKit.Check(c.Commits == 0 && c.NotificationGate.CancelAccepted, "the actual wait is bounded by local soft abort");
                    }
                }
            });
            TestKit.Run("f1 safety: a wrong-thread canary is No-Go before any consumption", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                {
                    c.Begin();
                    c.Slot.PrepareFrameEvidence(c.Transaction.CurrentFrame, c.Context.Bridge);
                    TestKit.Check(Task.Run(() => c.DrainPosts()).Wait(2000), "wrong-thread callback actually ran");
                    c.RunEof();
                    TestKit.Check(c.Slot.EvidenceFailed && c.Commits == 0 && c.Pool.HasOutstandingLease, "no fake main-thread progress");
                }
            });
            TestKit.Run("f1 safety: inline canary context is rejected before entering a wait", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                {
                    c.Begin();
                    var prior = SynchronizationContext.Current;
                    Rgb24MainThreadBridge inline;
                    try { SynchronizationContext.SetSynchronizationContext(new InlineContext()); TestKit.Check(Rgb24MainThreadBridge.TryCapture(out inline, out _), "inline bridge captured"); }
                    finally { SynchronizationContext.SetSynchronizationContext(prior); }
                    bool rejected = false;
                    try { c.Slot.PrepareFrameEvidence(c.Transaction.CurrentFrame, inline); }
                    catch (InvalidOperationException ex) { rejected = ex.Message == "f1-canary-inline"; }
                    TestKit.Check(rejected && c.Slot.EvidenceFailed && c.Slot.WaitingTimestamp == 0, "pre-wait fail closed");
                }
            });
            TestKit.Run("f1 safety: canary stays queued throughout real wait and retires only after EOF", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                {
                    c.Begin();
                    c.Slot.PrepareFrameEvidence(c.Transaction.CurrentFrame, c.Context.Bridge);
                    var producer = Task.Run(() =>
                    {
                        TestKit.Check(SpinWait.SpinUntil(() => c.NotificationGate.WaiterCount == 1, 2000), "actual blocked EOF");
                        TestKit.Check(!c.Slot.CanarySettled, "canary not pumped");
                        c.Write.SetResult(new Rgb24FrameWriteOutcome(true, null, null, 1));
                    });
                    c.RunEof();
                    TestKit.Check(producer.Wait(2000), "producer joined");
                    TestKit.CheckEqual(1, c.Commits, "single commit with stable source");
                    TestKit.Check(!c.Slot.CanarySettled, "canary cannot be delivery progress");
                    c.DrainPosts();
                    TestKit.Check(c.Slot.CanarySettled && !c.Slot.EvidenceFailed, "natural post-EOF callback");
                    TestKit.Check(c.Slot.Evidence.Dump().Contains("source-sha256-after"), "independent recomputation");
                }
            });
            TestKit.Run("f1 safety: source mutation during wait fails before Consume releases the lease", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                {
                    c.Begin();
                    byte[] bytes = c.Transaction.CurrentFrame.SegmentBuffer(0);
                    c.Slot.PrepareFrameEvidence(c.Transaction.CurrentFrame, c.Context.Bridge);
                    var producer = Task.Run(() =>
                    {
                        TestKit.Check(SpinWait.SpinUntil(() => c.NotificationGate.WaiterCount == 1, 2000), "actual blocked EOF");
                        bytes[bytes.Length - 1] ^= 0xff;
                        c.Write.SetResult(new Rgb24FrameWriteOutcome(true, null, null, 1));
                    });
                    c.RunEof();
                    TestKit.Check(producer.Wait(2000), "mutation producer joined");
                    TestKit.CheckEqual(0, c.Commits, "no mutated frame commit");
                    TestKit.Check(c.Pool.HasOutstandingLease && c.StopDetail.Contains("source-or-canary"), "lease still owned until abort");
                }
            });
            TestKit.Run("f1 safety: direct Emergency wakes EOF without any monitor", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                using (var emergency = new EventWaitHandle(false, EventResetMode.ManualReset))
                {
                    c.Begin();
                    var external = Task.Run(() =>
                    {
                        TestKit.Check(SpinWait.SpinUntil(() => c.NotificationGate.WaiterCount == 1, 2000), "actual blocked EOF");
                        emergency.Set();
                    });
                    int commits = 0;
                    c.Slot.RunSingleFrame(c.Transaction, c.Transaction.Generation, 0, true, emergency, 3000,
                        () => commits++, (_, __) => { });
                    TestKit.Check(external.Wait(2000), "independent requester exited");
                    TestKit.CheckEqual(0, commits, "emergency before claim");
                    TestKit.Check(!c.Write.Task.IsCompleted && c.Pool.HasOutstandingLease, "wake is not IO terminal");
                    TestKit.Check(c.NotificationGate.CancelIsEmergency, "emergency accepted by captured thread");
                }
            });
            TestKit.Run("f1 safety: native frame drift fails before claim", () =>
            {
                using (var c = new FrameLocalDeliveryProbeTests.ProbeChain(true))
                {
                    c.Begin(); c.Write.SetResult(new Rgb24FrameWriteOutcome(true, null, null, 1));
                    int native = 10, commits = 0;
                    string reason = null;
                    c.Slot.RunSingleFrame(c.Transaction, c.Transaction.Generation, 0, true, null, 1000,
                        () => commits++, (_, detail) => reason = detail, () => native++);
                    TestKit.Check(commits == 0 && reason.Contains("native-frame-changed"), "native drift is No-Go");
                    TestKit.Check(c.Pool.HasOutstandingLease, "no premature release");
                }
            });
            TestKit.Run("f1 safety: descriptor expiration uses its stored UTC origin", () =>
            {
                DateTime now = DateTime.UtcNow;
                string text = "runNonce=f1-test\ngamePid=1\ngameStartUtcTicks=1\neventPrefix=Renderist.F1\nexpirySeconds=5\ncreatedUtcTicks=" + now.Ticks;
                TestKit.Check(FrameLocalDeliveryProbeDescriptor.TryParse(text, now, out _, out _), "fresh");
                TestKit.Check(!FrameLocalDeliveryProbeDescriptor.TryParse(text, now.AddSeconds(5), out _, out var error) && error == "f1-descriptor-expired", "expiry boundary");
                TestKit.Check(!FrameLocalDeliveryProbeDescriptor.TryParse(text, now.AddSeconds(-1), out _, out _), "future descriptor rejected");
            });
        }

        private sealed class InlineContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object state) => callback(state);
        }

        private static void ObserveTerminal(Task task)
        {
            try { TestKit.Check(task.Wait(2000), "bounded terminal"); }
            catch (AggregateException) { var observed = task.Exception; }
            TestKit.Check(task.IsCompleted, "actual task terminal");
        }

        private sealed class DeferredIo : Stream
        {
            internal readonly TaskCompletionSource<bool> WriteResult = NewSignal();
            internal readonly TaskCompletionSource<bool> FlushResult = NewSignal();
            internal readonly TaskCompletionSource<bool> FlushEntered = NewSignal();
            internal int Writes, Flushes, Disposals;
            internal bool ThrowOnDispose;
            private static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { Interlocked.Increment(ref Writes); return WriteResult.Task; }
            public override Task FlushAsync(CancellationToken token) { Interlocked.Increment(ref Flushes); FlushEntered.TrySetResult(true); return FlushResult.Task; }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Interlocked.Increment(ref Disposals); if (ThrowOnDispose) throw new IOException("injected-dispose"); }
                base.Dispose(disposing);
            }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => Disposals == 0;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
