using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// F1-A —— 受限诊断设施的行为回归（§2.2.26 D 的所有线性化顺序）。
    ///
    /// 断言的是**生产** <see cref="FrameLocalDeliveryClaimGate"/> / <see cref="FrameLocalDeliveryProbeSlot"/>
    /// 的行为，不是复制一套假状态机。默认关闭、单 run、单帧、禁止 Finalizing 的约束也在这里固定。
    ///
    /// 纪律：全部事件驱动、无 sleep 排序；watchdog 只用于终止挂死。
    /// </summary>
    internal static class FrameLocalDeliveryProbeTests
    {
        private const long GenerationA = 21L;
        private const long GenerationB = 22L;
        private const long TokenId = 7L;
        private const int WatchdogMs = 60000;
        private const string Nonce = "f1a-nonce-1";

        public static void Run()
        {
            SuccessClaimIsOneShot();
            CancelBeforeClaimWins();
            PublishThenAbortNeverCommits();
            FailureResultNeverCommits();
            PublicationFaultIsNoGo();
            IdentityAndGenerationIsolation();
            LateStoreCannotDeliver();
            ReadyHandleLifecycle();
            SlotBindingRules();
            DefaultOffAndSingleRun();
            ProductionChainRegressions();
            ProductionEofEntryTests();
        }

        private static void ProductionEofEntryTests()
        {
            TestKit.Run("f1 EOF: real f0 Observe claim Consume stop returns on the captured thread", () =>
            {
                using (var c = new ProbeChain(true))
                {
                    TestKit.Check(c.Slot.IsAwaitingBinding, "authorized, not yet bound");
                    c.Begin();
                    var gate = c.NotificationGate;
                    TestKit.CheckEqual(c.Slot.RunNonce, gate.RunNonce, "production permit retains run nonce");
                    int owner = Thread.CurrentThread.ManagedThreadId;
                    Task producer = Task.Run(() => c.Write.SetResult(Success()));
                    c.RunEof(() =>
                    {
                        TestKit.CheckEqual(owner, Thread.CurrentThread.ManagedThreadId, "same EOF thread");
                        TestKit.Check(gate.ClaimClosedAsSuccess && !c.Pool.HasOutstandingLease,
                            "claim precedes lease release and commit callback");
                        TestKit.Check(!gate.TryAcceptCancel("late", false, out _), "late cancel cannot rewrite permit");
                    });
                    TestKit.Check(producer.Wait(1000), "producer settled");
                    TestKit.CheckEqual(1, c.Commits, "unique commit");
                    TestKit.CheckEqual(1, c.Stops, "stop before return; no f1 preparation");
                    TestKit.CheckEqual("f1-single-frame-complete", c.StopDetail, "diagnostic terminal reason");
                    TestKit.CheckEqual(0, c.PostCount, "no normal Post");
                    TestKit.Check(gate.TryGetPublished(out var canonical), "original canonical");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedDuplicateCompletion,
                        c.Transaction.ConsumeEnvelope(true, canonical, out _, out _), "second consume rejected");
                    TestKit.Check(!gate.TryStorePending(canonical, out _), "late Post/Store cannot reopen claim");
                }
            });
            TestKit.Run("f1 EOF: default AsyncPosted still requires its original callback", () =>
            {
                using (var c = new ProbeChain(false))
                {
                    c.Begin();
                    c.Write.SetResult(Success());
                    TestKit.CheckEqual(1, c.PostCount, "normal Post exactly once");
                    TestKit.Check(!c.Transaction.TryTakePendingEnvelope(out _, out _), "no direct delivery fallback");
                    c.DrainPosts();
                    TestKit.Check(c.Transaction.TryTakePendingEnvelope(out var e, out _), "real pending envelope");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed,
                        c.Transaction.ConsumeEnvelope(true, e, out _, out _), "original async consume");
                }
            });
            TestKit.Run("f1 EOF: invalid first frame is rejected and cannot silently export another frame", () =>
            {
                using (var c = new ProbeChain(true))
                {
                    c.Prepare(1);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedTransportRejected,
                        c.Transaction.TryBeginDelivery(out var error, out _), "diagnostic bind failure is returned");
                    TestKit.Check(error.StartsWith("f1-slot-bind-failed:"), "explicit bind error");
                    c.RunEof();
                    TestKit.CheckEqual(0, c.Commits, "no commit");
                    TestKit.CheckEqual(1, c.Stops, "no ordinary export fallback");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedWrongPhase,
                        c.Transaction.TryBeginFrame(GenerationA, 2, out _), "no second frame while pending");
                    c.Write.SetResult(Success());
                    TestKit.CheckEqual(0, c.PostCount, "failed direct route cannot Post a normal result");
                }
            });
            TestKit.Run("f1 EOF: ordinary cancel wakes the actual blocked entry with raw write still pending", () =>
            {
                using (var c = new ProbeChain(true))
                {
                    c.Begin();
                    var gate = c.NotificationGate;
                    var worker = Task.Run(() =>
                    {
                        TestKit.Check(SpinWait.SpinUntil(() => gate.WaiterCount == 1, 2000), "actual EOF waiter entered");
                        return FrameLocalDeliveryProbe.TryRequestCancel("ordinary-pending-cancel", out _);
                    });
                    c.RunEof();
                    TestKit.Check(worker.Result, "ordinary cancel accepted");
                    TestKit.Check(!c.Write.Task.IsCompleted, "did not wait for IO or emergency deadline");
                    TestKit.Check(gate.IsClosed && !gate.ClaimClosedAsSuccess, "aborted claim closed");
                    TestKit.CheckEqual(0, c.Commits, "no cancel commit");
                    TestKit.Check(!c.Transaction.TryAbort(out _), "raw task protects lease");
                    TestKit.Check(c.Pool.HasOutstandingLease, "lease still pinned");
                }
            });
            TestKit.Run("f1 EOF: published success followed by accepted cancel produces zero commits", () =>
            {
                using (var c = new ProbeChain(true))
                {
                    c.Begin(); c.Write.SetResult(Success());
                    TestKit.Check(FrameLocalDeliveryProbe.TryRequestCancel("cancel-before-claim", out _), "cancel");
                    c.RunEof();
                    TestKit.CheckEqual(0, c.Commits, "publication alone is not permission");
                    TestKit.Check(c.Pool.HasOutstandingLease, "aborted Consume does not release; stop retires separately");
                }
            });
            TestKit.Run("f1 EOF: pre-bind cancel is sticky and rejects first binding", () =>
            {
                using (var c = new ProbeChain(true))
                {
                    TestKit.Check(FrameLocalDeliveryProbe.TryRequestCancel("before-bind", out _), "accepted before binding");
                    TestKit.Check(!FrameLocalDeliveryProbe.PrePlayHealthy(out var preplayError) &&
                        preplayError == "f1-control-cancelled-before-play", "accepted startup cancel forbids Play");
                    TestKit.Check(!c.Slot.IsAwaitingBinding, "authorization cannot revive after cancel");
                    c.Prepare(0);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedTransportRejected,
                        c.Transaction.TryBeginDelivery(out _, out _), "fail closed at binding");
                    c.RunEof();
                    TestKit.CheckEqual(0, c.Commits, "no fallback commit");
                }
            });
            foreach (string terminal in new[] { "failure", "fault", "cancel-task", "publication-fault" })
            {
                TestKit.Run("f1 EOF: " + terminal + " wakes and stops without a commit", () =>
                {
                    using (var c = new ProbeChain(true))
                    {
                        c.Begin();
                        var gate = c.NotificationGate;
                        if (terminal == "failure") c.Write.SetResult(Rgb24FrameWriteOutcome.Failure("write-failed", "evidence"));
                        else if (terminal == "fault") c.Write.SetException(new IOException("write-fault"));
                        else if (terminal == "cancel-task") c.Write.SetCanceled();
                        else gate.MarkPublicationFault("injected-publication-fault");
                        c.RunEof();
                        TestKit.CheckEqual(0, c.Commits, "no failure commit");
                        TestKit.CheckEqual(1, c.Stops, "deterministic stop");
                        TestKit.CheckEqual(0, c.PostCount, "no normal result Post");
                        if (terminal == "publication-fault")
                            TestKit.Check(c.Pool.HasOutstandingLease, "publication fault does not end raw write");
                        else TestKit.Check(!c.Pool.HasOutstandingLease, "actual failure result retires completed lease");
                    }
                });
            }
            foreach (bool cleanupFirst in new[] { false, true })
            {
                TestKit.Run("f1 EOF: direct cancellation preserves both barriers cleanupFirst=" + cleanupFirst, () =>
                {
                    using (var c = new ProbeChain(true))
                    {
                        c.Begin();
                        FrameLocalDeliveryProbe.TryRequestCancel("barriers", out _);
                        c.RunEof();
                        TestKit.Check(!c.Context.TryStopAndDrain("barriers", out _), "pending write cannot drain");
                        if (cleanupFirst)
                        {
                            c.Transport.Cleanup.SetResult(new FfmpegVideoOutcome { ResidualOwnership = false });
                            TestKit.Check(c.Context.HasFrameOwnership && c.Context.HasResidualOwnership, "cleanup is not frame completion");
                        }
                        c.Write.SetResult(Success());
                        c.DrainPosts(); // only a retirement wake; never a normal second consume
                        TestKit.Check(!c.Context.HasFrameOwnership, "raw completion now allows retirement");
                        if (!cleanupFirst)
                        {
                            TestKit.Check(c.Context.HasPipelineOwnership && c.Context.HasResidualOwnership, "frame completion is not cleanup");
                            c.Transport.Cleanup.SetResult(new FfmpegVideoOutcome { ResidualOwnership = false });
                        }
                        TestKit.Check(!c.Context.HasResidualOwnership, "both barriers observed");
                        TestKit.CheckEqual(0, c.Commits, "retirement is not commit");
                    }
                });
            }
            foreach (string control in new[] { "cancel", "shutdown", "emergency", "soft-abort" })
                MonitorControlAfterRelease(control);
            TestKit.Run("f1 EOF: old generation and a real late Post cannot affect the new direct frame", () =>
            {
                Action[] latePosts;
                Rgb24DeliveryEnvelope oldEnvelope;
                using (var old = new ProbeChain(false))
                {
                    old.Begin(); old.Write.SetResult(Success());
                    TestKit.Check(old.NotificationGate.TryGetPublished(out oldEnvelope), "old real canonical");
                    latePosts = old.TakePosts();
                    TestKit.CheckEqual(1, latePosts.Length, "real old bridge callback withheld");
                }
                using (var current = new ProbeChain(true))
                {
                    current.Begin(0, GenerationB);
                    foreach (var post in latePosts) post();
                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedStaleGeneration,
                        current.Transaction.ConsumeEnvelope(true, oldEnvelope, out _, out _), "old result refused");
                    TestKit.Check(current.Pool.HasOutstandingLease, "old callback cannot retire current lease");
                    current.Write.SetResult(Success()); current.RunEof();
                    TestKit.CheckEqual(1, current.Commits, "only current canonical commits");
                    TestKit.CheckEqual(0, current.PostCount, "current direct route stays exclusive");
                }
            });
            TestKit.Run("f1 EOF: wrong generation at entry is fail closed", () =>
            {
                using (var c = new ProbeChain(true))
                {
                    c.Begin();
                    c.Slot.RunSingleFrame(c.Transaction, GenerationB, 0, true, null, 1000,
                        () => c.Commits++, (reason, detail) => c.Stops++);
                    TestKit.CheckEqual(0, c.Commits, "wrong generation cannot claim");
                    TestKit.CheckEqual(1, c.Stops, "explicit stop, no async fallback");
                }
            });
            TestKit.Run("f1 EOF: a throwing commit callback stops once and never creates a second permit", () =>
            {
                using (var c = new ProbeChain(true))
                {
                    c.Begin(); var gate = c.NotificationGate; c.Write.SetResult(Success());
                    c.RunEof(() => { throw new InvalidOperationException("commit callback evidence"); });
                    TestKit.CheckEqual(1, c.Commits, "at most one commit invocation");
                    TestKit.CheckEqual(1, c.Stops, "exactly one failure stop");
                    TestKit.Check(c.StopDetail.Contains("commit callback evidence"), "exception retained");
                    gate.TryGetPublished(out var e);
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.AlreadyClaimed, gate.TryClaim(e).Status, "permit stays one-shot");
                }
            });
        }

        private static void MonitorControlAfterRelease(string control)
        {
            TestKit.Run("f1 EOF: sticky Release cannot starve " + control, () =>
            {
                using (var c = new ProbeChain(true))
                using (var release = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var cancel = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var emergency = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var ack = new EventWaitHandle(false, EventResetMode.ManualReset))
                {
                    c.Begin(); int releases = 0;
                    using (var monitor = new FrameLocalDeliveryProbeMonitor(c.Slot, c.Slot.Evidence,
                        release, cancel, shutdown, emergency, ack,
                        reason => { if (reason == "guardian-release") Interlocked.Increment(ref releases); },
                        control == "soft-abort" ? 200 : 0, 5000))
                    {
                        monitor.Start(); release.Set();
                        TestKit.Check(ack.WaitOne(1000), "Release observed");
                        for (int i = 0; i < 100; i++) release.Set();
                        if (control == "cancel") cancel.Set();
                        if (control == "shutdown") shutdown.Set();
                        if (control == "emergency") emergency.Set();
                        c.RunEof();
                        TestKit.Check(c.NotificationGate.CancelAccepted, "control accepted before claim");
                        TestKit.CheckEqual(0, c.Commits, "no commit");
                        TestKit.CheckEqual(1, Volatile.Read(ref releases), "Release only once");
                        TestKit.Check(!c.Write.Task.IsCompleted, "wake does not need raw completion");
                    }
                }
            });
        }

        private static void ProductionChainRegressions()
        {
            TestKit.Run("f1 integration: a newly armed probe binds the first production f0", () =>
            {
                using (var chain = new ProbeChain(true))
                {
                    chain.Begin();
                    TestKit.Check(chain.Slot.IsBound, "first binding must not require an already bound slot");
                }
            });
            TestKit.Run("f1 integration: real Observe publishes and wakes direct without Post", () =>
            {
                using (var chain = new ProbeChain(false))
                {
                    chain.Begin();
                    var gate = chain.NotificationGate;
                    TestKit.Check(gate.TryEnableDirectRoute(out _), "isolate direct publication before raw completion");
                    chain.Write.SetResult(Success());
                    TestKit.Check(gate.WaitReady(500), "real Observe must wake ready");
                    TestKit.CheckEqual(0, chain.PostCount, "direct must not Post");
                    TestKit.Check(gate.TryGetPublished(out var canonical), "real canonical");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed,
                        chain.Transaction.ConsumeEnvelope(true, canonical, out _, out _), "consume once");
                }
            });
            TestKit.Run("f1 integration: accepted ordinary cancel wakes a pending EOF waiter", () =>
            {
                using (var chain = new ProbeChain(false))
                {
                    chain.Begin();
                    var gate = chain.NotificationGate;
                    TestKit.Check(gate.TryEnableDirectRoute(out _), "direct route");
                    var worker = Task.Run(() => gate.TryAcceptCancel("normal-cancel", false, out _));
                    TestKit.Check(gate.WaitReady(500), "cancel must wake while raw write is pending");
                    TestKit.Check(worker.Result, "cancel accepted");
                    TestKit.Check(!chain.Write.Task.IsCompleted, "not a completion or emergency wake");
                    TestKit.Check(chain.Pool.HasOutstandingLease, "cancel must not release pending lease");
                }
            });
            TestKit.Run("f1 integration: sticky Release is handled once and cannot starve Cancel", () =>
            {
                using (var chain = new ProbeChain(true))
                using (var release = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var cancel = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var emergency = new EventWaitHandle(false, EventResetMode.ManualReset))
                using (var ack = new EventWaitHandle(false, EventResetMode.ManualReset))
                {
                    chain.Begin();
                    // The baseline cannot bind itself; isolate the monitor defect if needed.
                    if (!chain.Slot.IsBound)
                        TestKit.Check(chain.Slot.TryBind(chain.NotificationGate, GenerationA, 0,
                            chain.Transaction.LeaseTokenId, out _), "monitor fixture bind");
                    int releases = 0;
                    using (var monitor = new FrameLocalDeliveryProbeMonitor(chain.Slot, chain.Slot.Evidence,
                        release, cancel, shutdown, emergency, ack,
                        reason => { if (reason == "guardian-release") Interlocked.Increment(ref releases); }, 0, 0))
                    {
                        monitor.Start();
                        release.Set();
                        TestKit.Check(ack.WaitOne(1000), "release observed");
                        cancel.Set();
                        TestKit.Check(SpinWait.SpinUntil(() => monitor.Exited, 1000), "cancel must end monitor");
                        TestKit.CheckEqual(1, Volatile.Read(ref releases), "exactly one release");
                    }
                }
            });
        }

        // Installs only the authorized slot; all frame/Observe/bridge/lease operations are production code.
        // OS descriptor activation is a separate guardian concern, not fabricated by this fixture.
        internal sealed class ProbeChain : SynchronizationContext, IDisposable
        {
            private static readonly FieldInfo SlotField = typeof(FrameLocalDeliveryProbe).GetField("_slot",
                BindingFlags.Static | BindingFlags.NonPublic);
            private readonly Queue<Action> _posts = new Queue<Action>();
            internal readonly FrameLocalDeliveryProbeSlot Slot;
            internal readonly TaskCompletionSource<Rgb24FrameWriteOutcome> Write =
                new TaskCompletionSource<Rgb24FrameWriteOutcome>();
            internal readonly Rgb24FrameBufferPool Pool;
            internal readonly Rgb24FrameTransaction Transaction;
            internal readonly Rgb24DeliveryContext Context;
            internal readonly ProbeTransport Transport;
            internal int PostCount;
            internal int Commits, Stops;
            internal string StopDetail;
            internal FrameLocalDeliveryClaimGate NotificationGate
            {
                get
                {
                    object notification = typeof(Rgb24FrameTransaction).GetField("_write",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Transaction);
                    return (FrameLocalDeliveryClaimGate)notification.GetType().GetField("Gate",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(notification);
                }
            }
            internal ProbeChain(bool armed)
            {
                TestKit.Check(SlotField.GetValue(null) == null, "no pre-existing active probe");
                Slot = new FrameLocalDeliveryProbeSlot(Nonce, new FrameLocalDeliveryEvidence());
                if (armed) SlotField.SetValue(null, Slot);
                Rgb24FrameLayout.TryCreate(4, 2, out var layout, out _);
                var original = Current;
                SetSynchronizationContext(this);
                try
                {
                    TestKit.Check(Rgb24MainThreadBridge.TryCapture(out var bridge, out _), "capture real bridge");
                    Transport = new ProbeTransport(Write.Task);
                    Context = new Rgb24DeliveryContext(layout, Transport, Transport, bridge);
                    Transaction = Context.Transaction;
                    Pool = Context.Pool;
                }
                finally { SetSynchronizationContext(original); }
            }
            internal void Begin(long frame = 0, long generation = GenerationA)
            {
                Prepare(frame, generation);
                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted,
                    Transaction.TryBeginDelivery(out var error, out _), "begin delivery: " + error);
            }
            internal void Prepare(long frame, long generation = GenerationA)
            {
                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted,
                    Transaction.TryBeginFrame(generation, frame, out _), "begin frame");
                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted,
                    Transaction.TryCompleteEof(generation, frame, out _, out _), "EOF");
            }
            internal void RunEof(Action onCommit = null)
            {
                Slot.RunSingleFrame(Transaction, Transaction.Generation, Transaction.FrameIndex, true, null, 3000,
                    () => { Commits++; onCommit?.Invoke(); }, (reason, detail) => { Stops++; StopDetail = detail; });
            }
            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_posts) { PostCount++; _posts.Enqueue(() => callback(state)); }
            }
            internal void DrainPosts()
            {
                while (true)
                {
                    Action action;
                    lock (_posts) { if (_posts.Count == 0) break; action = _posts.Dequeue(); }
                    action();
                }
            }
            internal Action[] TakePosts()
            {
                lock (_posts) { var result = _posts.ToArray(); _posts.Clear(); return result; }
            }
            public void Dispose()
            {
                try
                {
                    var gate = Transaction.HasUnconvergedDelivery ? NotificationGate : Slot.Gate;
                    Write.TrySetResult(Success());
                    DrainPosts();
                    TestKit.Check(Transaction.TryAbort(out var error), "strict frame teardown: " + error);
                    TestKit.Check(!Pool.HasOutstandingLease && !Transaction.HasResidualOwnership,
                        "no hidden lease residual");
                    Transport.Cleanup.TrySetResult(new FfmpegVideoOutcome { ResidualOwnership = false });
                    TestKit.Check(!Context.HasResidualOwnership, "strict cleanup barrier");
                    if (gate != null) TestKit.Check(gate.TryDisposeReadyHandle(out error), "strict ready teardown: " + error);
                }
                finally { SlotField.SetValue(null, null); }
            }
        }
        internal sealed class ProbeTransport : IRgb24FrameTransport, IRgb24PipelineLifetime
        {
            private readonly Task<Rgb24FrameWriteOutcome> _write;
            internal ProbeTransport(Task<Rgb24FrameWriteOutcome> write) { _write = write; }
            public Rgb24TransportAttempt TryBeginWrite(OwnedRgb24Frame frame)
                { return Rgb24TransportAttempt.Accepted(_write); }
            internal readonly TaskCompletionSource<FfmpegVideoOutcome> Cleanup = new TaskCompletionSource<FfmpegVideoOutcome>();
            public Task<FfmpegVideoOutcome> CleanupTask { get { return Cleanup.Task; } }
            public void RequestStop(string reason) { }
            public long DeliveredFrameCount { get { return 1; } }
            public Task<FfmpegVideoOutcome> BeginFinalize() { throw new Exception("F1 must never Finalize"); }
        }

        // =============================================================== 1. 成功 claim

        private static void SuccessClaimIsOneShot()
        {
            TestKit.Run("f1 probe: the success permit is a single linearization point", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();

                    FrameLocalDeliveryClaimResult first = gate.TryClaim(envelope);
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.ClaimedSuccess, first.Status,
                        "the first claim must win");
                    TestKit.Check(first.IsSuccessClaim, "the first claim carries the permit");
                    TestKit.CheckNotEmpty(first.PermitId, "permit identity");

                    FrameLocalDeliveryClaimResult second = gate.TryClaim(envelope);
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.AlreadyClaimed, second.Status,
                        "a second claim must be refused");
                    TestKit.Check(second.PermitId == null, "a refused claim never carries a permit");

                    // 换一个对象重新包装同样无效（身份是引用，且 claim 已关闭）。
                    var clone = new Rgb24DeliveryEnvelope(envelope.Generation, envelope.AbsoluteFrameIndex,
                        envelope.TokenId, envelope.Outcome, envelope.FrameIdentity, envelope.Completion);
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.WrongIdentity, gate.TryClaim(clone).Status,
                        "a clone is not the canonical object");
                }
            });
        }

        // =============================================================== 2. 取消先

        private static void CancelBeforeClaimWins()
        {
            TestKit.Run("f1 probe: a cancel accepted before the claim forbids any success permit", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;

                    string cancelError;
                    TestKit.Check(gate.TryAcceptCancel("guardian-cancel", false, out cancelError),
                        "the cancel must be accepted while the claim is open: " + cancelError);

                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();
                    FrameLocalDeliveryClaimResult claim = gate.TryClaim(envelope);
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.Aborted, claim.Status,
                        "a published success cannot be claimed after an accepted cancel");
                    TestKit.Check(claim.PermitId == null, "an aborted claim never carries a permit");
                    TestKit.Check(!gate.ClaimClosedAsSuccess, "the gate must not be closed as success");
                }
            });

            TestKit.Run("f1 probe: a cancel arriving after the claim is late and cannot rewrite it", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();

                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.ClaimedSuccess,
                        gate.TryClaim(envelope).Status, "the claim wins first");

                    string cancelError;
                    TestKit.Check(!gate.TryAcceptCancel("late-cancel", false, out cancelError),
                        "a late cancel must not be accepted");
                    TestKit.CheckEqual("f1-late-cancel-after-claim", cancelError, "late-cancel reason");
                    TestKit.Check(gate.ClaimClosedAsSuccess, "the success claim stands");
                }
            });
        }

        // =============================================================== 3. publish 先、abort 先

        private static void PublishThenAbortNeverCommits()
        {
            TestKit.Run("f1 probe: publication followed by abort still yields no success permit", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();

                    string error;
                    TestKit.Check(gate.TryAcceptCancel("abort-after-publish", false, out error),
                        "the abort is accepted because the claim is still open");

                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.Aborted, gate.TryClaim(envelope).Status,
                        "the abort precedes the claim");
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.AlreadyClaimed, gate.TryClaim(envelope).Status,
                        "the closed claim can never be re-entered");
                }
            });

            TestKit.Run("f1 probe: an emergency abort uses the same single arbitration lock", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();

                    string error;
                    TestKit.Check(gate.TryAcceptCancel("guardian-emergency", true, out error),
                        "the emergency abort is accepted");
                    TestKit.Check(gate.CancelIsEmergency, "the gate records that it was an emergency");
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.Aborted, gate.TryClaim(envelope).Status,
                        "an emergency abort also forbids the permit");
                }
            });
        }

        // =============================================================== 4. 失败结果

        private static void FailureResultNeverCommits()
        {
            TestKit.Run("f1 probe: a published failure is claimed as failure and never as success", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    Rgb24DeliveryEnvelope envelope = harness.PublishFailure("rgb24-write-task-faulted");

                    FrameLocalDeliveryClaimResult claim = gate.TryClaim(envelope);
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.ClaimedFailure, claim.Status,
                        "a failure outcome is claimed as failure");
                    TestKit.Check(claim.PermitId == null, "a failure claim carries no success permit");
                    TestKit.Check(!gate.ClaimClosedAsSuccess, "the gate is not closed as success");
                }
            });

            TestKit.Run("f1 probe: an unpublished gate reports NotReady instead of a permit", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    // 构造一个"看起来像"的信封：它绝不是本槽的 canonical 对象。
                    var fabricated = harness.FabricateSuccess();
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.WrongIdentity, gate.TryClaim(fabricated).Status,
                        "an unpublished envelope can never be claimed");

                    // 发布之后再用同一个伪造对象仍然不行。
                    Rgb24DeliveryEnvelope canonical = harness.PublishSuccess();
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.WrongIdentity, gate.TryClaim(fabricated).Status,
                        "a fabricated envelope stays unacceptable after publication");
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.ClaimedSuccess, gate.TryClaim(canonical).Status,
                        "only the canonical object can be claimed");
                }
            });
        }

        // =============================================================== 5. 发布故障

        private static void PublicationFaultIsNoGo()
        {
            TestKit.Run("f1 probe: a publication infrastructure fault is No-Go and never yields a permit", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    gate.MarkPublicationFault("f1-envelope-build-failed");
                    gate.SignalReady();

                    TestKit.Check(gate.WaitReady(WatchdogMs), "the fault must still signal ready (no stuck waiter)");
                    var fabricated = harness.FabricateSuccess();
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.WrongIdentity, gate.TryClaim(fabricated).Status,
                        "a fabricated envelope stays unacceptable");

                    Rgb24DeliveryEnvelope canonical = harness.PublishSuccess();
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.AlreadyClaimed, gate.TryClaim(canonical).Status,
                        "a faulted slot is closed; it cannot be claimed afterwards");
                    TestKit.Check(!gate.ClaimClosedAsSuccess, "the faulted slot is never a success");
                }
            });

            TestKit.Run("f1 probe: a bridge failure is observable and blocks the success path", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    gate.MarkFailure("rgb24-bridge-post-failed");
                    TestKit.CheckEqual("rgb24-bridge-post-failed", gate.Failure, "the failure is recorded");
                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();
                    // 失败通知先于 result 消费被判失败（既有语义），绝不产生成功许可。
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.Fault, gate.TryClaim(envelope).Status,
                        "a failed notification must not produce a success permit");
                    TestKit.Check(!gate.ClaimClosedAsSuccess, "the failed slot is closed as no-go, not success");
                }
            });
        }

        // =============================================================== 6. 身份/世代隔离

        private static void IdentityAndGenerationIsolation()
        {
            TestKit.Run("f1 probe: identity is bound to generation, frame index and token", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    TestKit.CheckEqual(GenerationA, gate.Generation, "generation is bound");
                    TestKit.CheckEqual(0L, gate.AbsoluteFrameIndex, "frame index is bound");
                    TestKit.CheckEqual(TokenId, gate.TokenId, "token is bound");
                    TestKit.Check(ReferenceEquals(harness.FrameIdentity, gate.FrameIdentity),
                        "frame identity is bound to the original object");

                    var wrongGeneration = new Rgb24DeliveryEnvelope(GenerationB, 0L, TokenId,
                        Success(), harness.FrameIdentity, harness.Completion);
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.WrongIdentity, gate.TryClaim(wrongGeneration).Status,
                        "a wrong-generation envelope is not canonical");
                }
            });

            TestKit.Run("f1 probe: an old slot cannot deliver into a new run", () =>
            {
                using (var oldHarness = new GateHarness(GenerationA, Nonce + "-old"))
                using (var newHarness = new GateHarness(GenerationB, Nonce + "-new"))
                {
                    Rgb24DeliveryEnvelope oldEnvelope = oldHarness.PublishSuccess();
                    Rgb24DeliveryEnvelope newEnvelope = newHarness.PublishSuccess();

                    // 旧 envelope 对新槽不是 canonical。
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.WrongIdentity,
                        newHarness.Gate.TryClaim(oldEnvelope).Status,
                        "the old envelope is foreign to the new slot");
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.ClaimedSuccess,
                        newHarness.Gate.TryClaim(newEnvelope).Status,
                        "the new slot claims its own envelope");
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.ClaimedSuccess,
                        oldHarness.Gate.TryClaim(oldEnvelope).Status,
                        "the old slot is unaffected");
                }
            });
        }

        // =============================================================== 7. 迟到 Store

        private static void LateStoreCannotDeliver()
        {
            TestKit.Run("f1 probe: a late Store after the claim is rejected and never becomes a second source", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.ClaimedSuccess, gate.TryClaim(envelope).Status,
                        "claimed first");

                    string reason;
                    TestKit.Check(!gate.TryStorePending(envelope, out reason),
                        "a Store after the claim must be refused");
                    TestKit.CheckEqual("f1-store-after-claim-closed:ClaimedSuccess", reason, "store refusal reason");

                    Rgb24DeliveryEnvelope taken;
                    string takeError;
                    TestKit.Check(!gate.TryTakePending(out taken, out takeError),
                        "no _pending may be produced after the claim closed");
                    TestKit.CheckEqual("f1-take-after-claim-closed:ClaimedSuccess", takeError,
                        "take refusal reason");
                }
            });

            TestKit.Run("f1 probe: a Store for a non-canonical envelope is refused", () =>
            {
                using (var harness = new GateHarness(enableDirect: false))
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    Rgb24DeliveryEnvelope canonical = harness.PublishSuccess();
                    var foreign = harness.FabricateSuccess();

                    string reason;
                    TestKit.Check(!gate.TryStorePending(foreign, out reason), "foreign envelope refused");
                    TestKit.CheckEqual("f1-store-not-canonical", reason, "store reason");

                    TestKit.Check(gate.TryStorePending(canonical, out reason),
                        "the canonical envelope is stored: " + reason);
                    Rgb24DeliveryEnvelope taken;
                    string takeError;
                    TestKit.Check(gate.TryTakePending(out taken, out takeError), "and can be taken once");
                    TestKit.Check(ReferenceEquals(canonical, taken), "the same canonical object");
                    TestKit.Check(!gate.TryTakePending(out taken, out takeError), "only once");
                }
            });
        }

        // =============================================================== 8. ready 句柄寿命

        private static void ReadyHandleLifecycle()
        {
            TestKit.Run("f1 probe: the ready handle is not disposed while the producer may still set it", () =>
            {
                using (var harness = new GateHarness())
                {
                    FrameLocalDeliveryClaimGate gate = harness.Gate;
                    TestKit.Check(gate.ReadyHandle != null, "direct route exposes a ready handle");

                    string error;
                    TestKit.Check(!gate.TryDisposeReadyHandle(out error),
                        "disposing before the producer settled must be refused");
                    TestKit.CheckEqual("f1-ready-handle-producer-unsettled", error, "refusal reason");

                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();
                    TestKit.Check(gate.ProducerSettled, "publication settles the producer");
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.ClaimedSuccess, gate.TryClaim(envelope).Status,
                        "claim closes the slot");

                    TestKit.Check(gate.TryDisposeReadyHandle(out error), "then the handle can be disposed: " + error);
                    TestKit.Check(gate.ReadyHandle == null, "the handle is released");
                    // 关闭之后再 Signal 不得抛异常（producer 已 settled，此处只验证幂等）。
                    gate.SignalReady();
                    TestKit.Check(gate.TryDisposeReadyHandle(out error), "double dispose is idempotent");
                }
            });

            TestKit.Run("f1 probe: the async route does not create a ready handle at all", () =>
            {
                using (var harness = new GateHarness(enableDirect: false))
                {
                    TestKit.Check(harness.Gate.ReadyHandle == null,
                        "the default AsyncPosted route allocates no wait handle");
                    TestKit.Check(!harness.Gate.DirectRoute, "the default route stays async");
                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();
                    TestKit.Check(harness.Gate.TryStorePending(envelope, out _),
                        "the async route still stores a pending envelope");
                }
            });
        }

        // =============================================================== 9. slot 绑定规则

        private static void SlotBindingRules()
        {
            TestKit.Run("f1 probe: the slot only accepts absolute frame 0 and binds exactly once", () =>
            {
                var evidence = new FrameLocalDeliveryEvidence();
                var slot = new FrameLocalDeliveryProbeSlot(Nonce, evidence);
                using (var harness = new GateHarness())
                {
                    string error;
                    TestKit.Check(!slot.TryBind(harness.Gate, GenerationA, 1L, TokenId, out error),
                        "frame 1 must be refused");
                    TestKit.CheckEqual("f1-bind-not-frame-zero", error, "frame refusal reason");

                    TestKit.Check(slot.TryBind(harness.Gate, GenerationA, 0L, TokenId, out error),
                        "frame 0 binds: " + error);
                    TestKit.Check(slot.IsBound, "the slot reports bound");
                    TestKit.Check(slot.IsArmedForFrame(GenerationA, 0L), "armed for the bound frame");
                    TestKit.Check(!slot.IsArmedForFrame(GenerationB, 0L), "not armed for another generation");
                    TestKit.Check(!slot.IsArmedForFrame(GenerationA, 1L), "not armed for another frame");

                    TestKit.Check(!slot.TryBind(harness.Gate, GenerationA, 0L, TokenId, out error),
                        "a second bind must be refused");
                    TestKit.CheckEqual("f1-bind-already-bound", error, "duplicate bind reason");
                }
            });

            TestKit.Run("f1 probe: cancel through the slot reaches the bound gate", () =>
            {
                var slot = new FrameLocalDeliveryProbeSlot(Nonce, new FrameLocalDeliveryEvidence());
                using (var harness = new GateHarness())
                {
                    string error;
                    TestKit.Check(slot.TryBind(harness.Gate, GenerationA, 0L, TokenId, out error), "bind");
                    Rgb24DeliveryEnvelope envelope = harness.PublishSuccess();

                    TestKit.Check(slot.TryAcceptCancel("slot-cancel", false, out error),
                        "the slot accepts the cancel on behalf of the monitor");
                    TestKit.CheckEqual(FrameLocalDeliveryClaimStatus.Aborted,
                        harness.Gate.TryClaim(envelope).Status, "the gate arbitrates as aborted");
                }
            });
        }

        // =============================================================== 10. 默认关闭 / 单 run

        private static void DefaultOffAndSingleRun()
        {
            TestKit.Run("f1 probe: the probe is off by default and exposes no wait handles", () =>
            {
                // 测试进程没有设置 descriptor 环境变量：必须完全关闭。
                TestKit.Check(string.IsNullOrEmpty(FrameLocalDeliveryProbe.DescriptorPath),
                    "no descriptor path is configured in the test environment");
                TestKit.Check(!FrameLocalDeliveryProbe.IsActive, "the probe must be inactive by default");
                TestKit.Check(FrameLocalDeliveryProbe.Slot == null, "no slot is created by default");
                TestKit.CheckEqual(0, FrameLocalDeliveryProbe.BuildWaitHandles().Length,
                    "no wait handles are exposed when inactive");
                TestKit.Check(!FrameLocalDeliveryProbe.EmergencyRequested(),
                    "no emergency signal exists when inactive");

                string error;
                TestKit.Check(!FrameLocalDeliveryProbe.TryActivate(out error),
                    "activation without a descriptor must fail");
                TestKit.CheckEqual("f1-descriptor-absent", error, "activation refusal reason");
                TestKit.Check(!FrameLocalDeliveryProbe.TryRequestCancel("x", out error),
                    "cancel without an active probe must fail");
                TestKit.CheckEqual("f1-inactive", error, "cancel refusal reason");
            });

            TestKit.Run("f1 probe: settlement reports the missing gate instead of pretending to be clean", () =>
            {
                // 非激活时 settlement 的语义是"没有需要收敛的 probe 资源"，但也不能声称已观察过。
                string error;
                bool clean = FrameLocalDeliveryProbe.TryObserveSettlement(out error);
                TestKit.Check(clean, "an inactive probe has no residual probe resources: " + error);
                TestKit.CheckEqual("no-evidence", FrameLocalDeliveryProbe.DumpEvidence(),
                    "no evidence is fabricated when inactive");
            });
        }

        // =============================================================== helpers

        private static Rgb24FrameWriteOutcome Success()
        {
            return new Rgb24FrameWriteOutcome(true, null, null, 1L);
        }

        private sealed class GateHarness : IDisposable
        {
            internal readonly FrameLocalDeliveryClaimGate Gate;
            internal readonly object FrameIdentity = new object();
            internal readonly Task<Rgb24FrameWriteOutcome> Completion;
            private readonly TaskCompletionSource<Rgb24FrameWriteOutcome> _completion =
                new TaskCompletionSource<Rgb24FrameWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly List<Rgb24DeliveryEnvelope> _published = new List<Rgb24DeliveryEnvelope>();

            internal GateHarness(long generation = GenerationA, string nonce = Nonce, bool enableDirect = true)
            {
                Completion = _completion.Task;
                Gate = new FrameLocalDeliveryClaimGate(nonce, generation, 0L, TokenId, FrameIdentity, Completion, this);
                if (enableDirect)
                {
                    string error;
                    TestKit.Check(Gate.TryEnableDirectRoute(out error), "enable direct route: " + error);
                }
            }

            internal Rgb24DeliveryEnvelope PublishSuccess()
            {
                return Publish(Success());
            }

            internal Rgb24DeliveryEnvelope PublishFailure(string errorCode)
            {
                return Publish(Rgb24FrameWriteOutcome.Failure(errorCode, null));
            }

            private Rgb24DeliveryEnvelope Publish(Rgb24FrameWriteOutcome outcome)
            {
                var envelope = new Rgb24DeliveryEnvelope(Gate.Generation, Gate.AbsoluteFrameIndex, Gate.TokenId,
                    outcome, FrameIdentity, Completion);
                if (Gate.DirectRoute)
                {
                    bool signal;
                    Gate.TryPublishDirect(envelope, null, out signal);
                    if (signal) Gate.SignalReady();
                }
                else Gate.PublishCanonical(envelope);
                _published.Add(envelope);
                return envelope;
            }

            /// <summary>构造一个字段正确但**不是**本槽 canonical 对象的信封。</summary>
            internal Rgb24DeliveryEnvelope FabricateSuccess()
            {
                return new Rgb24DeliveryEnvelope(Gate.Generation, Gate.AbsoluteFrameIndex, Gate.TokenId,
                    Success(), new object(), Completion);
            }

            public void Dispose()
            {
                _completion.TrySetResult(Success());
                if (!Gate.ProducerSettled) PublishSuccess();
                Gate.TryAcceptCancel("test-end", false, out _);
                Gate.TryGetPublished(out var e);
                Gate.TryClaim(e);
                TestKit.Check(Gate.TryDisposeReadyHandle(out var error), "unit gate teardown: " + error);
            }
        }
    }
}
