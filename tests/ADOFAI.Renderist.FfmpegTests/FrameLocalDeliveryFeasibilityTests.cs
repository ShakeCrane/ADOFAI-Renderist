using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// F0 — Frame-local Delivery 可行性（Unity-free 行为验证）+ F1-A 前必须的夹具修紧。
    ///
    /// 修紧项（PROJECT_UNDERSTANDING §2.2.26 A / G）：
    ///   * DisposeStrict 不再吞异常：cleanup 的 timeout / fault / cancel / null outcome /
    ///     ResidualOwnership / frame ownership 任一不 clean 都使测试失败；测试主体异常与
    ///     cleanup 异常**同时保留**。
    ///   * BeginOn 返回**独立快照**，不与 lease 段共享 byte[] 引用。
    ///   * 独立信号只在测试主线程真的进入 WaitAny（worker 观测到 WaitSleepJoin）之后才触发。
    ///   * 逆序屏障使用独立 stdout EOF 门闩，真正扣住 cleanup 的 EOF。
    ///   * 成功/取消的先后由 controlled interleaving 显式驱动，不靠 sleep 赢竞态。
    ///   * 真实 mapped Task 的 fault / cancel 终态单独覆盖。
    ///   * 信号、worker、通知、文件资源都有可断言的收敛结果。
    ///
    /// 纪律：等待窗口内绝不执行被扣留的 Post delegate，也不 Pump/Send。WatchdogMs 只用于
    /// 终止挂死并报告失败，不是产品合法背压超时。fake 进程/上下文不等于 Unity/Mono/GPU 验证。
    /// </summary>
    internal static class FrameLocalDeliveryFeasibilityTests
    {
        private const long GenerationA = 11L;
        private const long GenerationB = 12L;

        /// <summary>仅用于报告挂死；不是产品背压期限。</summary>
        private const int WatchdogMs = 60000;

        private const int Width = 64;
        private const int Height = 48;
        private const int Fps = 30;

        public static void Run(string root)
        {
            NoPumpCanonicalEnvelopeConsumption(root);
            CompletionTiming(root);
            ExceptionsAndIdentity(root);
            CancelAndResourceOwnership(root);
            OldNotificationIsolation(root);
        }

        // =============================================================== 1. 无 Pump 的真实信封消费

        private static void NoPumpCanonicalEnvelopeConsumption(string root)
        {
            RunTest("frame-local delivery: a canonical envelope is consumed on the capturing thread with Post withheld", () =>
            {
                WithChain(root, "f0-nopump", ControllableStdinMode.DelayedSuccess, chain =>
                {
                    byte[] expected = chain.Begin(GenerationA, 0L);
                    long token = chain.Transaction.LeaseTokenId;

                    TestKit.Check(chain.Stream.IsWritePending,
                        "the L2 stdin write must still be pending when delivery is accepted");

                    Rgb24DeliveryEnvelope pending;
                    string bridgeError;
                    TestKit.Check(!chain.Context.TryTakePendingEnvelope(out pending, out bridgeError),
                        "no _pending may exist before the Post delegate runs");
                    TestKit.Check(pending == null, "TryTake must not fabricate an envelope");
                    TestKit.CheckEqual(0, chain.Hold.ExecutedCount,
                        "the held Post delegate must never have been executed");

                    TestKit.Check(chain.Stream.ReleaseSuccess(), "release the intercepted stdin write");
                    TestKit.Check(chain.WaitForPost(),
                        "the background must publish and post the completion envelope");

                    TestKit.Check(!chain.Context.TryTakePendingEnvelope(out pending, out bridgeError),
                        "Store must not have run while the Post delegate is withheld");
                    TestKit.Check(pending == null, "still no _pending");
                    TestKit.CheckEqual(0, chain.Hold.ExecutedCount,
                        "Post was called but its delegate was not executed");

                    object state = chain.Hold.StateAt(0);
                    var envelope = state as Rgb24DeliveryEnvelope;
                    TestKit.Check(envelope != null, "the posted state must be the real Rgb24DeliveryEnvelope");
                    TestKit.Check(envelope.Success, "the accepted write must report success");
                    TestKit.CheckEqual(GenerationA, envelope.Generation, "envelope generation");
                    TestKit.CheckEqual(0L, envelope.AbsoluteFrameIndex, "envelope frame index");
                    TestKit.CheckEqual(token, envelope.TokenId, "envelope token");
                    TestKit.CheckEqual(1L, envelope.DeliveredFrameCount, "L2 delivered frame count");

                    TestKit.Check(BytesEqual(expected, chain.FrameBuffer()),
                        "the lease bytes must be unchanged before consumption");
                    TestKit.Check(!ReferenceEquals(expected, chain.FrameBuffer()),
                        "the expected snapshot must be an independent array");
                    TestKit.CheckEqual(0L, chain.Context.Pool.ReleaseCount, "no release before consumption");

                    string errorCode;
                    string errorDetail;
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed,
                        chain.Context.Transaction.ConsumeEnvelope(true, envelope, out errorCode, out errorDetail),
                        "the canonical envelope must commit on the capturing thread: " + errorCode + " " + errorDetail);

                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "lease released exactly once");
                    TestKit.Check(chain.Context.Pool.OutstandingLease == null, "lease retired");
                    TestKit.CheckEqual(Rgb24TransactionPhase.Idle, chain.Transaction.Phase, "phase returns to idle");
                    TestKit.Check(!chain.Context.HasFrameOwnership, "no residual frame ownership");

                    int posted = chain.Hold.PostCount;
                    TestKit.CheckEqual(1, posted, "exactly one envelope was posted for this frame");
                    chain.Hold.ExecuteAll();
                    TestKit.CheckEqual(posted, chain.Hold.ExecutedCount, "every held delegate ran exactly once");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "a late Post releases nothing");
                    TestKit.CheckEqual(Rgb24TransactionPhase.Idle, chain.Transaction.Phase,
                        "the late Post cannot resurrect the retired frame");

                    chain.Begin(GenerationA, 1L);
                    TestKit.Check(chain.Transaction.LeaseTokenId != token, "a fresh one-shot token per frame");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "still exactly one release");
                });
            });

            RunTest("frame-local delivery: the lease is not released and the buffer is not reused while the write is pending", () =>
            {
                WithChain(root, "f0-nopump-pending", ControllableStdinMode.DelayedSuccess, chain =>
                {
                    byte[] expected = chain.Begin(GenerationA, 0L);
                    long token = chain.Transaction.LeaseTokenId;

                    for (int i = 0; i < 32; i++)
                    {
                        chain.Context.TryTakePendingEnvelope(out _, out _);
                    }

                    TestKit.CheckEqual(0L, chain.Context.Pool.ReleaseCount, "no release while the write is pending");
                    TestKit.Check(chain.Context.Pool.HasOutstandingLease, "the single lease stays valid");
                    TestKit.Check(chain.Context.Transaction.HasUnconvergedDelivery,
                        "a pending delivery is unconverged by definition");
                    TestKit.Check(BytesEqual(expected, chain.FrameBuffer()),
                        "the pinned buffer still holds the frame bytes");

                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedWrongPhase,
                        chain.Transaction.TryBeginFrame(GenerationA, 1L, out _),
                        "a second frame cannot start while one delivery is pending");

                    TestKit.Check(chain.Stream.ReleaseSuccess(), "release the write");
                    TestKit.Check(chain.WaitForPost(), "completion posted");
                    var envelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed,
                        chain.Consume(envelope), "then and only then it commits");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "exactly one release");
                    TestKit.CheckEqual(token, envelope.TokenId, "the consumed envelope belongs to that lease");
                });
            });
        }

        // =============================================================== 2. 完成时序

        private static void CompletionTiming(string root)
        {
            RunTest("frame-local delivery: an immediate real write still publishes exactly one envelope", () =>
            {
                WithChain(root, "f0-immediate", null, chain =>
                {
                    chain.Begin(GenerationA, 0L);
                    TestKit.Check(chain.WaitForPost(), "an immediate write still posts exactly one envelope");
                    TestKit.CheckEqual(1, chain.Hold.PostCount, "exactly one Post");

                    var envelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, chain.Consume(envelope),
                        "immediate completion commits once");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "one release");
                });
            });

            RunTest("frame-local delivery: a deferred real write publishes only after the bytes are flushed", () =>
            {
                WithChain(root, "f0-deferred", ControllableStdinMode.DelayedSuccess, chain =>
                {
                    chain.Begin(GenerationA, 0L);

                    TestKit.CheckEqual(0, chain.Hold.PostCount, "nothing posted while the write is pending");
                    TestKit.Check(!chain.Context.Transaction.TryTakePendingEnvelope(out _, out _),
                        "no envelope while the write is pending");

                    TestKit.Check(chain.Stream.ReleaseSuccess(), "release the deferred write");
                    TestKit.Check(chain.WaitForPost(), "posted after the write finished");
                    var envelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.Check(envelope.Success, "the deferred write succeeded");

                    Rgb24DeliveryEnvelope never;
                    string bridgeError;
                    TestKit.Check(!chain.Context.TryTakePendingEnvelope(out never, out bridgeError),
                        "Store still has not run (Post withheld)");
                });
            });

            RunTest("frame-local delivery: an already-completed mapped Task is not treated as a published envelope", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    Rgb24FrameWriteOutcome outcome = Success();
                    harness.Transport.Complete(outcome);

                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");

                    Rgb24DeliveryEnvelope fabricated = harness.FabricateEnvelope(outcome);
                    string errorCode;
                    string errorDetail;
                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedCompletionIdentity,
                        harness.Consume(fabricated, out errorCode, out errorDetail),
                        "a fabricated envelope must be rejected even though the Task is completed");
                    TestKit.CheckEqual("rgb24-envelope-completion-identity", errorCode, "identity error code");
                    TestKit.CheckEqual(0L, harness.Pool.ReleaseCount, "no release on a rejected identity");
                });
            });

            RunTest("frame-local delivery: the completed canonical envelope commits regardless of Post timing", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Transport.Complete(Success());

                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");
                    TestKit.Check(harness.Hold.WaitForPost(),
                        "the continuation posted the canonical envelope");

                    var envelope = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, harness.Consume(envelope),
                        "the canonical envelope commits");
                    TestKit.CheckEqual(1L, harness.Pool.ReleaseCount, "one release");
                });
            });
        }

        // =============================================================== 3. 异常与身份

        private static void ExceptionsAndIdentity(string root)
        {
            RunTest("frame-local delivery: a failed write never commits and retires the lease exactly once", () =>
            {
                WithChain(root, "f0-write-failure", ControllableStdinMode.DelayedSuccess, chain =>
                {
                    chain.Begin(GenerationA, 0L);
                    TestKit.Check(chain.Stream.Fault(new IOException("injected-stdin-failure")),
                        "inject a real stdin write failure");
                    TestKit.Check(chain.WaitForPost(), "the failure is still published as an envelope");

                    var envelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.Check(!envelope.Success, "the envelope must carry the failure");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Failed, chain.Consume(envelope),
                        "a failed envelope can never commit");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "lease retired exactly once");
                    TestKit.Check(!chain.Context.HasFrameOwnership, "no frame ownership remains");
                });
            });

            RunTest("frame-local delivery: a faulted mapped Task is propagated as a failure envelope", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Transport.Fault(new InvalidOperationException("injected-mapped-task-fault"));
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "the fault is published as an envelope");

                    var envelope = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);
                    TestKit.Check(!envelope.Success, "a faulted Task must produce a failure envelope");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Failed, harness.Consume(envelope),
                        "a faulted Task can never commit");
                    TestKit.CheckEqual(1L, harness.Pool.ReleaseCount, "the lease is retired exactly once");
                });
            });

            RunTest("frame-local delivery: a canceled mapped Task is propagated as a failure envelope", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Transport.Cancel();
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "the cancellation is published as an envelope");

                    var envelope = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);
                    TestKit.Check(!envelope.Success, "a canceled Task must produce a failure envelope");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Failed, harness.Consume(envelope),
                        "a canceled Task can never commit");
                    TestKit.CheckEqual(1L, harness.Pool.ReleaseCount, "the lease is retired exactly once");
                });
            });

            RunTest("frame-local delivery: wrong generation, frame and token are rejected before any release", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Transport.Complete(Success());
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "posted");

                    var canonical = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);

                    string errorCode;
                    string errorDetail;
                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedStaleGeneration,
                        harness.Consume(harness.Fabricate(GenerationB, 0L, canonical.TokenId, Success()), out errorCode, out errorDetail),
                        "wrong generation");
                    TestKit.CheckEqual("rgb24-envelope-stale-generation", errorCode, "generation error code");

                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedWrongFrame,
                        harness.Consume(harness.Fabricate(GenerationA, 5L, canonical.TokenId, Success()), out errorCode, out errorDetail),
                        "wrong frame");
                    TestKit.CheckEqual("rgb24-envelope-wrong-frame", errorCode, "frame error code");

                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedTokenMismatch,
                        harness.Consume(harness.Fabricate(GenerationA, 0L, canonical.TokenId + 99L, Success()), out errorCode, out errorDetail),
                        "wrong token");
                    TestKit.CheckEqual("rgb24-envelope-token-mismatch", errorCode, "token error code");

                    TestKit.CheckEqual(0L, harness.Pool.ReleaseCount, "no rejected envelope may release the lease");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, harness.Consume(canonical),
                        "the canonical envelope still commits afterwards");
                    TestKit.CheckEqual(1L, harness.Pool.ReleaseCount, "exactly one release");
                });
            });

            RunTest("frame-local delivery: a re-constructed envelope cannot bypass IsAuthentic", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Transport.Complete(Success());
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "posted");

                    var canonical = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);

                    Rgb24DeliveryEnvelope clone;
                    string errorCode;
                    string errorDetail;
                    TestKit.Check(harness.TryCloneEnvelope(canonical, out clone), "clone built");
                    TestKit.Check(!ReferenceEquals(clone, canonical), "the clone must be a different object");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedCompletionIdentity,
                        harness.Consume(clone, out errorCode, out errorDetail),
                        "a value-identical clone must not authenticate");
                    TestKit.CheckEqual("rgb24-envelope-completion-identity", errorCode, "identity error code");
                    TestKit.CheckEqual(0L, harness.Pool.ReleaseCount, "no release for a clone");

                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedCompletionIdentity,
                        harness.Consume(harness.Fabricate(GenerationA, 0L, canonical.TokenId, Success()), out errorCode, out errorDetail),
                        "an envelope without the real identity is rejected");

                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, harness.Consume(canonical),
                        "only the canonical object commits");
                });
            });

            RunTest("frame-local delivery: duplicate consumption and a late Post never create a second commit", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Transport.Complete(Success());
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "posted");

                    var canonical = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);
                    string errorCode;
                    string errorDetail;
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed,
                        harness.Consume(canonical, out errorCode, out errorDetail), "first consume commits");
                    TestKit.CheckEqual(1L, harness.Pool.ReleaseCount, "one release");

                    TestKit.Check(harness.Consume(canonical, out errorCode, out errorDetail)
                            != Rgb24TransactionOutcome.Committed,
                        "the same envelope cannot commit twice");
                    TestKit.CheckEqual("rgb24-envelope-duplicate-completion", errorCode, "duplicate error code");
                    TestKit.CheckEqual(1L, harness.Pool.ReleaseCount, "still exactly one release");

                    harness.Hold.ExecuteAll();
                    TestKit.CheckEqual(1L, harness.Pool.ReleaseCount, "a late Post releases nothing");
                    TestKit.CheckEqual(Rgb24TransactionPhase.Idle, harness.Transaction.Phase, "phase unchanged");
                });
            });

            RunTest("frame-local delivery: consuming from a worker thread is rejected as wrong thread", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Transport.Complete(Success());
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "posted");
                    var canonical = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);

                    Rgb24TransactionOutcome outcome = Rgb24TransactionOutcome.Accepted;
                    string errorCode = null;
                    string takeError = null;
                    bool takeOk = true;
                    RunOnWorker(() =>
                    {
                        string code;
                        string detail;
                        outcome = harness.Transaction.ConsumeEnvelope(true, canonical, out code, out detail);
                        errorCode = code;
                        takeOk = harness.Context.TryTakePendingEnvelope(out _, out takeError);
                    });

                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedWrongThread, outcome,
                        "a worker thread cannot consume the frame");
                    TestKit.CheckEqual("rgb24-wrong-main-thread", errorCode, "wrong-thread error code");
                    TestKit.Check(!takeOk, "a worker thread cannot take the pending envelope");
                    TestKit.CheckEqual("rgb24-wrong-main-thread", takeError, "wrong-thread take error");
                    TestKit.CheckEqual(0L, harness.Pool.ReleaseCount, "no release from the wrong thread");

                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, harness.Consume(canonical),
                        "the capturing thread can still commit");
                });
            });

            RunTest("frame-local delivery: a throwing Post is reported and never allows a commit", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Hold.ThrowAfterStore = true;
                    harness.Transport.Complete(Success());
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "delivery accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "the attempt to post happened");

                    Rgb24DeliveryEnvelope never;
                    string bridgeError;
                    TestKit.Check(!harness.Context.TryTakePendingEnvelope(out never, out bridgeError),
                        "a failed Post leaves no _pending");
                    TestKit.Check(bridgeError != null && bridgeError.IndexOf("post", StringComparison.Ordinal) >= 0,
                        "the bridge failure must be reported, got: " + bridgeError);

                    var canonical = (Rgb24DeliveryEnvelope)harness.StoredState;
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Failed, harness.Consume(canonical),
                        "a canonical envelope under a failed notification must not commit");
                    TestKit.CheckEqual(0L, harness.Pool.ReleaseCount,
                        "the failed notification path does not silently release the lease");
                });
            });
        }

        // =============================================================== 4. 取消与资源归属

        private static void CancelAndResourceOwnership(string root)
        {
            RunTest("frame-local delivery: an independent signal wakes a main thread that is really waiting", () =>
            {
                WithChain(root, "f0-wake", ControllableStdinMode.DelayedSuccess, chain =>
                {
                    chain.Begin(GenerationA, 0L);

                    using (var armed = new ManualResetEventSlim(false))
                    using (var cancel = new ManualResetEventSlim(false))
                    {
                        bool observedBlocked = false;
                        Exception workerFailure = null;
                        Thread main = Thread.CurrentThread;

                        var worker = new Thread(() =>
                        {
                            try
                            {
                                TestKit.Check(armed.Wait(WatchdogMs), "the main thread announced its wait");
                                var deadline = Stopwatch.StartNew();
                                while (true)
                                {
                                    if ((main.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0) break;
                                    if (deadline.ElapsedMilliseconds > WatchdogMs)
                                        throw new Exception("the main thread never entered the wait");
                                    Thread.Yield();
                                }
                                observedBlocked = true;
                                cancel.Set();
                            }
                            catch (Exception ex)
                            {
                                workerFailure = ex;
                            }
                        });
                        worker.IsBackground = true;

                        armed.Set();
                        worker.Start();
                        int index = WaitHandle.WaitAny(
                            new[] { chain.Hold.PostedHandle, cancel.WaitHandle }, WatchdogMs);

                        TestKit.Check(worker.Join(WatchdogMs), "the probe worker must finish");
                        TestKit.Check(workerFailure == null,
                            "the probe worker failed: " + (workerFailure == null ? "" : workerFailure.Message));
                        TestKit.Check(observedBlocked,
                            "the signal must be sent only after the main thread was observed waiting");
                        TestKit.CheckEqual(1, index, "the independent cancel signal must wake the wait");
                        TestKit.CheckEqual(0, chain.Hold.ExecutedCount,
                            "no Post delegate was executed while waiting");
                    }

                    using (var never = new ManualResetEventSlim(false))
                    {
                        TestKit.Check(chain.Stream.ReleaseSuccess(), "release the write");
                        int index = WaitHandle.WaitAny(
                            new[] { chain.Hold.PostedHandle, never.WaitHandle }, WatchdogMs);
                        TestKit.CheckEqual(0, index, "the completion signal must win once the write finished");
                    }

                    var envelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, chain.Consume(envelope),
                        "the woken main thread consumes the canonical envelope");
                });
            });

            RunTest("frame-local delivery: cancel while the write is pending keeps the lease pinned and blocks restart", () =>
            {
                WithChain(root, "f0-cancel", ControllableStdinMode.CancelObservedDelayedTerminal, chain =>
                {
                    byte[] expected = chain.Begin(GenerationA, 0L);
                    var slot = chain.Context;
                    var next = chain.FreshContext();

                    TestKit.Check(chain.Stream.IsWritePending, "the write must be pending");

                    TestKit.Check(!chain.Context.TryStopAndDrain("f0-cancel", out _),
                        "stop cannot report clean while the write is pending");
                    TestKit.Check(chain.Stream.CancellationObserved.Wait(WatchdogMs),
                        "L2 observed the cancellation");
                    TestKit.Check(chain.Stream.IsWritePending,
                        "cancellation observation is not a write terminal");
                    TestKit.Check(chain.Context.Pool.OutstandingLease.DeliveryPinned,
                        "the buffer must stay pinned while the worker may still read it");
                    TestKit.CheckEqual(0L, chain.Context.Pool.ReleaseCount, "no early release");
                    TestKit.Check(BytesEqual(expected, chain.FrameBuffer()), "the buffer is untouched");
                    TestKit.Check(chain.Context.HasResidualOwnership, "residual ownership remains");
                    TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _),
                        "restart must be rejected before both barriers converge");
                    TestKit.Check(ReferenceEquals(slot, chain.Context), "the old context is retained");

                    chain.AwaitCleanup();
                    TestKit.Check(chain.Stream.IsWritePending, "cleanup first, frame second");
                    TestKit.Check(chain.Context.HasResidualOwnership, "still residual after cleanup");
                    TestKit.Check(!chain.Context.TryStopAndDrain("f0-cancel", out _),
                        "restart still blocked by the frame barrier");
                    TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _),
                        "restart rejected while the frame barrier is open");

                    TestKit.Check(chain.Stream.ReleaseTerminal(), "release the write terminal exactly once");
                    TestKit.Check(!chain.Stream.ReleaseTerminal(), "a duplicate terminal is rejected");
                    chain.Stream.WriteTerminal.GetResultIfCanceled();

                    TestKit.Check(chain.WaitForPost(), "the terminated write still publishes its envelope");
                    var cancelledEnvelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.Check(!cancelledEnvelope.Success, "the cancelled write must report failure");
                    TestKit.Check(chain.Consume(cancelledEnvelope) != Rgb24TransactionOutcome.Committed,
                        "a cancelled write can never commit");

                    TestKit.Check(chain.Context.TryStopAndDrain("f0-cancel", out _),
                        "both barriers converged");
                    TestKit.Check(!chain.Context.HasResidualOwnership, "residual cleared");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "the lease is released exactly once");
                    TestKit.Check(Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _),
                        "restart is accepted only after both barriers");
                    TestKit.Check(ReferenceEquals(slot, next), "the fresh context took over");
                });
            });

            RunTest("frame-local delivery: frame terminal before cleanup uses an independent stdout EOF gate", () =>
            {
                WithChain(root, "f0-cancel-reverse", ControllableStdinMode.CancelObservedDelayedTerminal,
                    chain =>
                    {
                        chain.Begin(GenerationA, 0L);
                        var slot = chain.Context;
                        var next = chain.FreshContext();

                        TestKit.Check(!chain.Context.TryStopAndDrain("f0-cancel", out _),
                            "stop begins convergence");
                        TestKit.Check(chain.Stream.CancellationObserved.Wait(WatchdogMs), "cancellation observed");
                        TestKit.Check(chain.StdoutGate.WaitEntered(),
                            "L2 must be blocked reading the deferred stdout EOF");

                        TestKit.Check(chain.Stream.ReleaseTerminal(), "frame terminal first");
                        chain.Stream.WriteTerminal.GetResultIfCanceled();
                        TestKit.Check(!chain.Stream.IsWritePending, "the write terminal is settled");
                        TestKit.Check(!chain.StdoutGate.Released, "the stdout EOF gate is still closed");
                        TestKit.Check(chain.Pipeline.CleanupTask != null && !chain.Pipeline.CleanupTask.IsCompleted,
                            "cleanup must still be pending on the independent EOF gate");
                        TestKit.Check(!chain.Context.TryStopAndDrain("f0-cancel", out _),
                            "the pipeline barrier is still open");
                        TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _),
                            "restart rejected while cleanup is outstanding");

                        chain.StdoutGate.ReleaseEof();
                        chain.AwaitCleanup();
                        TestKit.Check(chain.Context.TryStopAndDrain("f0-cancel", out _),
                            "both barriers converged in the reverse order");
                        TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "exactly one release");
                        TestKit.Check(Rgb24DeliveryContext.TryReplace(ref slot, next, false, out _),
                            "restart accepted after both barriers");
                    }, deferStdout: true);
            });

            RunTest("frame-local delivery: controlled interleaving pins claim-before-cancel, publish-before-cancel and cancel-before-publish", () =>
            {
                // 顺序 1：claim（consume）先于 cancel —— 成功许可成立，迟到取消不改写它。
                WithChain(root, "f0-order-claim-first", ControllableStdinMode.DelayedSuccess, chain =>
                {
                    chain.Begin(GenerationA, 0L);
                    TestKit.Check(chain.Stream.ReleaseSuccess(), "write succeeds");
                    TestKit.Check(chain.WaitForPost(), "envelope published");

                    var envelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, chain.Consume(envelope),
                        "the claim that precedes cancel commits once");

                    TestKit.Check(!chain.Context.TryStopAndDrain("f0-order-late", out _),
                        "a late cancel cannot rewrite the already claimed frame");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "exactly one release");
                    TestKit.CheckEqual(Rgb24TransactionPhase.Idle, chain.Transaction.Phase,
                        "the claimed frame stays retired");
                });

                // 顺序 2：publication 先，但 cancel/abort 在 claim 之前到达 ——
                // abort 会退休这笔已完成但未 claim 的交付，因此绝不产生 Commit。
                WithChain(root, "f0-order-publish-then-abort", ControllableStdinMode.DelayedSuccess, chain =>
                {
                    chain.Begin(GenerationA, 0L);
                    TestKit.Check(chain.Stream.ReleaseSuccess(), "write succeeds");
                    TestKit.Check(chain.WaitForPost(), "envelope published before cancel");

                    var envelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.Check(envelope.Success, "the published envelope is a success");

                    // abort 先到达：它退休这笔已完成但未 claim 的交付（frame barrier 收敛），
                    // 但 pipeline barrier 仍未完成，所以 TryStopAndDrain 此刻返回 false。
                    TestKit.Check(!chain.Context.TryStopAndDrain("f0-order-abort", out _),
                        "abort retires the frame but the pipeline barrier is still open");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount,
                        "the abort retires the published-but-unclaimed delivery exactly once");
                    TestKit.Check(!chain.Context.HasFrameOwnership, "the frame barrier converged via abort");

                    chain.AwaitCleanup();
                    TestKit.Check(chain.Context.TryStopAndDrain("f0-order-abort", out _),
                        "both barriers converge after cleanup");

                    TestKit.Check(chain.Consume(envelope) != Rgb24TransactionOutcome.Committed,
                        "a delivery retired by abort can never be claimed afterwards");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "no second release");
                    TestKit.Check(!chain.Context.HasFrameOwnership, "no residual frame ownership");
                });

                // 顺序 3：cancel 先被接受，publication 之后才到 —— 同样绝不提交。
                WithChain(root, "f0-order-cancel-first", ControllableStdinMode.DelayedSuccess, chain =>
                {
                    chain.Begin(GenerationA, 0L);
                    TestKit.Check(!chain.Context.TryStopAndDrain("f0-order-cancel", out _),
                        "cancel is accepted while the write is pending");

                    bool released = chain.Stream.ReleaseSuccess();
                    if (!released) chain.Stream.Fault(new IOException("f0-order-cancel"));

                    TestKit.Check(chain.WaitForPost(), "the terminal envelope is still published");
                    var envelope = (Rgb24DeliveryEnvelope)chain.Hold.StateAt(0);
                    TestKit.Check(chain.Consume(envelope) != Rgb24TransactionOutcome.Committed,
                        "a cancel accepted before publication can never commit (released=" + released + ")");
                    TestKit.CheckEqual(0L, chain.Context.Pool.ReleaseCount, "no release through rejection");

                    chain.AwaitCleanup();
                    TestKit.Check(chain.Context.TryStopAndDrain("f0-order-cancel", out _),
                        "abort converges after both barriers");
                    TestKit.CheckEqual(1L, chain.Context.Pool.ReleaseCount, "exactly one release overall");
                });
            });
        }

        // =============================================================== 5. 旧通知隔离

        private static void OldNotificationIsolation(string root)
        {
            RunTest("frame-local delivery: a late old-generation Post cannot touch the restarted session", () =>
            {
                WithChain(root, "f0-old-post", ControllableStdinMode.DelayedSuccess, oldChain =>
                {
                    WithChain(root, "f0-new-post", ControllableStdinMode.DelayedSuccess, newChain =>
                    {
                        oldChain.Begin(GenerationA, 0L);
                        TestKit.Check(oldChain.Stream.ReleaseSuccess(), "old write succeeds");
                        TestKit.Check(oldChain.WaitForPost(), "old envelope posted and withheld");

                        var oldEnvelope = (Rgb24DeliveryEnvelope)oldChain.Hold.StateAt(0);
                        TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, oldChain.Consume(oldEnvelope),
                            "the old session commits its own frame once");
                        TestKit.CheckEqual(1L, oldChain.Context.Pool.ReleaseCount, "old lease released once");

                        TestKit.Check(!oldChain.Context.TryStopAndDrain("f0-old-post", out _),
                            "stop begins convergence (pipeline barrier still open)");
                        oldChain.AwaitCleanup();
                        TestKit.Check(oldChain.Context.TryStopAndDrain("f0-old-post", out _),
                            "old session converges after its own barriers");
                        TestKit.Check(!oldChain.Context.HasResidualOwnership, "old context is clean");

                        newChain.Begin(GenerationB, 0L);
                        long newToken = newChain.Transaction.LeaseTokenId;
                        var newBuffer = newChain.Transaction.CurrentFrame;

                        oldChain.Hold.ExecuteAll();
                        TestKit.CheckEqual(1, oldChain.Hold.ExecutedCount, "the old delegate ran once");

                        Rgb24DeliveryEnvelope polluted;
                        string bridgeError;
                        TestKit.Check(!newChain.Context.TryTakePendingEnvelope(out polluted, out bridgeError),
                            "the old Post must not write the new transaction's _pending");
                        TestKit.Check(polluted == null, "no late envelope leaks into the new session");
                        TestKit.Check(ReferenceEquals(newBuffer, newChain.Transaction.CurrentFrame),
                            "the new lease is untouched");
                        TestKit.CheckEqual(newToken, newChain.Transaction.LeaseTokenId, "the new token is untouched");
                        TestKit.CheckEqual(0L, newChain.Context.Pool.ReleaseCount, "the new session released nothing");

                        string errorCode;
                        string errorDetail;
                        TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedStaleGeneration,
                            newChain.Transaction.ConsumeEnvelope(true, oldEnvelope, out errorCode, out errorDetail),
                            "an old-generation envelope is stale in the new session");
                        TestKit.CheckEqual(0L, newChain.Context.Pool.ReleaseCount, "no release for the stale envelope");
                    });
                });
            });

            RunTest("frame-local delivery: notification exactly-once across two frames of one session", () =>
            {
                WithHarness(harness =>
                {
                    harness.Prepare(GenerationA, 0L);
                    harness.Transport.Complete(Success());
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "frame 0 accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "frame 0 posted");
                    var first = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, harness.Consume(first), "frame 0 commits");

                    harness.Hold.Reset();
                    harness.Transport.Next();
                    harness.Prepare(GenerationA, 1L);
                    harness.Transport.Complete(Success());
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, harness.BeginDelivery(), "frame 1 accepted");
                    TestKit.Check(harness.Hold.WaitForPost(), "frame 1 posted");
                    TestKit.CheckEqual(1, harness.Hold.PostCount, "exactly one Post per accepted write");

                    var second = (Rgb24DeliveryEnvelope)harness.Hold.StateAt(0);
                    TestKit.Check(!ReferenceEquals(first, second), "each frame gets its own envelope");
                    TestKit.CheckEqual(1L, second.AbsoluteFrameIndex, "frame 1 index");

                    TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedDuplicateCompletion,
                        harness.Consume(first), "the previous frame's envelope is rejected");
                    TestKit.CheckEqual(Rgb24TransactionOutcome.Committed,
                        harness.Consume(second), "the current envelope commits");
                    TestKit.CheckEqual(2L, harness.Pool.ReleaseCount, "exactly one release per frame");
                    TestKit.Check(!harness.Context.HasFrameOwnership, "no residual after two frames");
                });
            });
        }

        // =============================================================== helpers

        private static void RunTest(string name, Action body)
        {
            TestKit.Run(name, () =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace, ex);
                }
            });
        }

        /// <summary>
        /// 建立真实链路并保证两个方向的异常都被保留；cleanup 不 clean 一律失败。
        /// </summary>
        private static void WithChain(string root, string name, ControllableStdinMode? stdinMode,
            Action<RealChain> body, bool deferStdout = false)
        {
            var chain = new RealChain(root, name, stdinMode, deferStdout);
            Exception bodyFailure = null;
            try
            {
                body(chain);
            }
            catch (Exception ex)
            {
                bodyFailure = ex;
            }

            string cleanupFailure = chain.DisposeStrict();
            if (bodyFailure != null && cleanupFailure != null)
            {
                throw new Exception(
                    "body failed AND cleanup failed: body=" + bodyFailure.Message +
                    " | cleanup=" + cleanupFailure,
                    new AggregateException(bodyFailure, new InvalidOperationException(cleanupFailure)));
            }
            if (bodyFailure != null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
            if (cleanupFailure != null) throw new Exception("cleanup failed: " + cleanupFailure);
        }

        private static void WithHarness(Action<TransactionHarness> body)
        {
            var harness = new TransactionHarness();
            Exception bodyFailure = null;
            try
            {
                body(harness);
            }
            catch (Exception ex)
            {
                bodyFailure = ex;
            }

            string cleanupFailure = harness.DisposeStrict();
            if (bodyFailure != null && cleanupFailure != null)
            {
                throw new Exception(
                    "body failed AND cleanup failed: body=" + bodyFailure.Message +
                    " | cleanup=" + cleanupFailure,
                    new AggregateException(bodyFailure, new InvalidOperationException(cleanupFailure)));
            }
            if (bodyFailure != null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
            if (cleanupFailure != null) throw new Exception("cleanup failed: " + cleanupFailure);
        }

        private static Rgb24FrameWriteOutcome Success()
        {
            return new Rgb24FrameWriteOutcome(true, null, null, 1L);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        private static void RunOnWorker(Action body)
        {
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            worker.IsBackground = true;
            worker.Start();
            TestKit.Check(worker.Join(WatchdogMs), "the test worker must finish (watchdog)");
            if (failure != null) throw new Exception("worker failed: " + failure.Message, failure);
        }

        // =============================================================== 真实链路夹具

        /// <summary>独立 stdout EOF 门闩：让 cleanup 的第二道屏障可被单独扣住。</summary>
        private sealed class DeferredStdoutGate : Stream
        {
            private readonly Stream _inner;
            private readonly TaskCompletionSource<int> _eof =
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
            private int _released;

            internal DeferredStdoutGate(Stream inner) { _inner = inner; }

            internal bool Released { get { return Volatile.Read(ref _released) != 0; } }
            internal bool WaitEntered() { return _entered.Wait(WatchdogMs); }
            internal void ReleaseEof() { Interlocked.Exchange(ref _released, 1); _eof.TrySetResult(0); }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                _entered.Set();
                return _eof.Task;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _entered.Set();
                return _eof.Task.GetAwaiter().GetResult();
            }

            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _eof.TrySetResult(0);
                    _entered.Dispose();
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// 真实 L2 + 真实 transport + 真实 transaction。子进程是既有 fake，stdin 可拦截，
        /// stdout EOF 可被独立门闩扣住。捕获桥的线程是测试主线程。
        /// </summary>
        private sealed class RealChain
        {
            internal readonly FfmpegVideoPipeline Pipeline;
            internal readonly HoldingContext Hold = new HoldingContext();
            internal ControllableStdinStream Stream { get; private set; }
            internal DeferredStdoutGate StdoutGate { get; private set; }
            internal Rgb24DeliveryContext Current { get; private set; }
            internal Rgb24DeliveryContext Context { get { return Current; } }
            internal Rgb24FrameTransaction Transaction { get { return Current.Transaction; } }

            private readonly string _directory;

            internal RealChain(string root, string name, ControllableStdinMode? stdinMode, bool deferStdout)
            {
                _directory = Path.Combine(root, "f0-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_directory);

                ControllableStdinMode mode = stdinMode ?? ControllableStdinMode.DelayedSuccess;
                var options = new FfmpegVideoPipelineOptions
                {
                    Settings = new FfmpegVideoSettings { Width = Width, Height = Height, Fps = Fps },
                    Identity = FfmpegVideoPipelineTests.MakeIdentity(FakeVideoProcess.ExecutablePath),
                    FinalPath = Path.Combine(_directory, "out.mp4"),
                    ProcessStart = new FakeVideoProcessLauncher().Create(),
                    StreamWrapper = (inner, role) =>
                    {
                        if (role == "stdin" && stdinMode != null)
                        {
                            Stream = new ControllableStdinStream(inner, mode);
                            return Stream;
                        }
                        if (role == "stdout" && deferStdout)
                        {
                            StdoutGate = new DeferredStdoutGate(inner);
                            return StdoutGate;
                        }
                        return inner;
                    },
                };

                Pipeline = new FfmpegVideoPipeline(options);
                TestKit.Check(Pipeline.Start().Started, "the L2 pipeline must start");
                Current = CreateContext();
            }

            private Rgb24DeliveryContext CreateContext()
            {
                var original = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(Hold);
                    Rgb24DeliveryContext context;
                    string error;
                    TestKit.Check(Rgb24DeliveryContext.TryCreate(Width, Height, Pipeline, out context, out error),
                        "L3 context must be created on the capturing thread: " + error);
                    return context;
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                }
            }

            internal Rgb24DeliveryContext FreshContext()
            {
                return CreateContext();
            }

            internal byte[] Begin(long generation, long frameIndex)
            {
                return BeginOn(Current, generation, frameIndex);
            }

            /// <summary>
            /// Prepare → 填固定字节 → 独立快照 → EOF → 交付被接受 → 等待写入真的进入 stdin。
            /// 返回的快照与 lease 段不共享引用。
            /// </summary>
            internal byte[] BeginOn(Rgb24DeliveryContext context, long generation, long frameIndex)
            {
                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted,
                    context.Transaction.TryBeginFrame(generation, frameIndex, out string beginError),
                    "begin frame: " + beginError);

                OwnedRgb24Frame frame = context.Transaction.CurrentFrame;
                byte[] buffer = frame.SegmentBuffer(0);
                for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)((i * 31 + 7) & 0xFF);
                for (int segment = 1; segment < frame.SegmentCount; segment++)
                {
                    byte[] extra = frame.SegmentBuffer(segment);
                    for (int i = 0; i < extra.Length; i++) extra[i] = (byte)((segment + i) & 0xFF);
                }

                var snapshot = new byte[buffer.Length];
                Buffer.BlockCopy(buffer, 0, snapshot, 0, buffer.Length);

                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted,
                    context.Transaction.TryCompleteEof(generation, frameIndex, out _, out string eofError),
                    "complete EOF: " + eofError);
                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted,
                    context.Transaction.TryBeginDelivery(out _, out string deliveryError),
                    "L2 must accept the write: " + deliveryError);

                if (Stream != null)
                {
                    TestKit.Check(Stream.Entered.Wait(WatchdogMs),
                        "the L2 write must reach stdin.WriteAsync");
                }
                return snapshot;
            }

            internal byte[] FrameBuffer()
            {
                OwnedRgb24Frame frame = Current.Transaction.CurrentFrame;
                return frame == null ? null : frame.SegmentBuffer(0);
            }

            internal Rgb24TransactionOutcome Consume(Rgb24DeliveryEnvelope envelope)
            {
                string errorCode;
                string errorDetail;
                return Current.Transaction.ConsumeEnvelope(true, envelope, out errorCode, out errorDetail);
            }

            internal bool WaitForPost()
            {
                return Hold.WaitForPost();
            }

            /// <summary>等待 L2 的实际 cleanup 完成（不吞 timeout/fault/cancel/null outcome）。</summary>
            internal FfmpegVideoOutcome AwaitCleanup()
            {
                Task<FfmpegVideoOutcome> cleanup = Pipeline.CleanupTask;
                TestKit.Check(cleanup != null, "L2 must own a cleanup task after cancel");
                TestKit.Check(cleanup.Wait(WatchdogMs), "L2 cleanup must finish (watchdog)");
                TestKit.Check(!cleanup.IsFaulted, "L2 cleanup must not fault");
                TestKit.Check(!cleanup.IsCanceled, "L2 cleanup must not be canceled");
                TestKit.Check(cleanup.Result != null, "L2 cleanup must return an outcome");
                return cleanup.Result;
            }

            /// <summary>严格 teardown：null 表示完全 clean，否则返回具体问题。</summary>
            internal string DisposeStrict()
            {
                var problems = new List<string>();
                try
                {
                    Pipeline.Cancel("f0-test-end");

                    if (Stream != null && Stream.IsWritePending)
                    {
                        if (Stream.CancellationObserved.IsCompleted) Stream.ReleaseTerminal();
                        else if (!Stream.Fault(new IOException("f0-test-end")))
                            problems.Add("stdin-write-neither-terminal-nor-faulted");
                    }

                    if (StdoutGate != null && !StdoutGate.Released) StdoutGate.ReleaseEof();

                    Task<FfmpegVideoOutcome> cleanup = Pipeline.CleanupTask;
                    if (cleanup == null)
                    {
                        problems.Add("cleanup-task-null");
                    }
                    else if (!cleanup.Wait(WatchdogMs))
                    {
                        problems.Add("cleanup-timeout");
                    }
                    else if (cleanup.IsFaulted)
                    {
                        problems.Add("cleanup-faulted:" + DescribeFault(cleanup));
                    }
                    else if (cleanup.IsCanceled)
                    {
                        problems.Add("cleanup-canceled");
                    }
                    else if (cleanup.Result == null)
                    {
                        problems.Add("cleanup-null-outcome");
                    }
                    else if (cleanup.Result.ResidualOwnership)
                    {
                        problems.Add("cleanup-residual-ownership");
                    }

                    if (Stream != null && Stream.IsWritePending) problems.Add("stdin-write-still-pending");

                    if (Current.HasFrameOwnership)
                    {
                        if (!Current.TryStopAndDrain("f0-test-end", out string abortError))
                            problems.Add("frame-ownership-not-converged:" + (abortError ?? "unknown"));
                    }
                    if (Current.HasFrameOwnership) problems.Add("frame-ownership-residual");

                    if (Stream != null) Stream.Dispose();
                    if (StdoutGate != null) StdoutGate.Dispose();
                }
                catch (Exception ex)
                {
                    problems.Add("teardown-exception:" + ex.GetType().Name + ":" + ex.Message);
                }

                try
                {
                    if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
                }
                catch (Exception ex)
                {
                    problems.Add("directory-cleanup-failed:" + ex.GetType().Name);
                }

                return problems.Count == 0 ? null : string.Join(";", problems);
            }

            private static string DescribeFault(Task task)
            {
                AggregateException aggregate = task.Exception;
                if (aggregate == null) return "unknown";
                return aggregate.GetBaseException().GetType().Name + ":" + aggregate.GetBaseException().Message;
            }
        }

        // =============================================================== transaction 级夹具

        private sealed class TransactionHarness
        {
            internal readonly Rgb24DeliveryContext Context;
            internal readonly HoldingContext Hold = new HoldingContext();
            internal readonly StubTransport Transport = new StubTransport();
            internal Rgb24FrameTransaction Transaction { get { return Context.Transaction; } }
            internal Rgb24FrameBufferPool Pool { get { return Context.Pool; } }

            private long _generation = -1;
            private long _frameIndex = -1;

            internal TransactionHarness()
            {
                var original = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(Hold);
                    Rgb24MainThreadBridge bridge;
                    TestKit.Check(Rgb24MainThreadBridge.TryCapture(out bridge, out _), "capture the bridge");
                    Rgb24FrameLayout layout;
                    TestKit.Check(Rgb24FrameLayout.TryCreate(6, 4, out layout, out string layoutError),
                        "layout: " + layoutError);
                    Context = new Rgb24DeliveryContext(layout, Transport, Transport, bridge);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                }
            }

            internal void Prepare(long generation, long frameIndex)
            {
                _generation = generation;
                _frameIndex = frameIndex;
                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted,
                    Transaction.TryBeginFrame(generation, frameIndex, out string error), "begin: " + error);
                OwnedRgb24Frame frame = Transaction.CurrentFrame;
                for (int segment = 0; segment < frame.SegmentCount; segment++)
                {
                    byte[] buffer = frame.SegmentBuffer(segment);
                    for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)((i + segment) & 0xFF);
                }
                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted,
                    Transaction.TryCompleteEof(generation, frameIndex, out _, out string eofError),
                    "eof: " + eofError);
            }

            internal Rgb24TransactionOutcome BeginDelivery()
            {
                return Transaction.TryBeginDelivery(out _, out _);
            }

            internal Rgb24TransactionOutcome Consume(Rgb24DeliveryEnvelope envelope)
            {
                string errorCode;
                string errorDetail;
                return Consume(envelope, out errorCode, out errorDetail);
            }

            internal Rgb24TransactionOutcome Consume(Rgb24DeliveryEnvelope envelope, out string errorCode,
                out string errorDetail)
            {
                return Transaction.ConsumeEnvelope(true, envelope, out errorCode, out errorDetail);
            }

            /// <summary>值相同但对象不同 —— 证明 IsAuthentic 是引用身份而不是字段比较。</summary>
            internal bool TryCloneEnvelope(Rgb24DeliveryEnvelope original, out Rgb24DeliveryEnvelope clone)
            {
                clone = null;
                if (original == null) return false;
                clone = new Rgb24DeliveryEnvelope(
                    original.Generation, original.AbsoluteFrameIndex, original.TokenId,
                    original.Outcome, original.FrameIdentity, original.Completion);
                return true;
            }

            internal Rgb24DeliveryEnvelope Fabricate(long generation, long frameIndex, long tokenId,
                Rgb24FrameWriteOutcome outcome)
            {
                return new Rgb24DeliveryEnvelope(generation, frameIndex, tokenId, outcome, new object(), null);
            }

            internal Rgb24DeliveryEnvelope FabricateEnvelope(Rgb24FrameWriteOutcome outcome)
            {
                return Fabricate(_generation, _frameIndex, Transaction.LeaseTokenId, outcome);
            }

            internal object StoredState { get { return Hold.LastStoredState; } }

            internal string DisposeStrict()
            {
                var problems = new List<string>();
                try
                {
                    Transport.Cleanup.TrySetResult(new FfmpegVideoOutcome
                    {
                        State = FfmpegVideoPipelineState.Cancelled,
                        ResidualOwnership = false,
                    });
                    if (!Transport.Cleanup.Task.Wait(WatchdogMs)) problems.Add("stub-cleanup-timeout");

                    if (Context.HasFrameOwnership)
                    {
                        if (!Context.TryStopAndDrain("f0-harness-end", out string abortError))
                            problems.Add("frame-ownership-not-converged:" + (abortError ?? "unknown"));
                    }
                    if (Context.HasFrameOwnership) problems.Add("frame-ownership-residual");
                }
                catch (Exception ex)
                {
                    problems.Add("teardown-exception:" + ex.GetType().Name + ":" + ex.Message);
                }
                return problems.Count == 0 ? null : string.Join(";", problems);
            }
        }

        /// <summary>测试专用 completion：完成时机与终态（result / fault / cancel）都由测试决定。</summary>
        private sealed class StubTransport : IRgb24FrameTransport, IRgb24PipelineLifetime
        {
            private readonly object _sync = new object();
            private TaskCompletionSource<Rgb24FrameWriteOutcome> _write;
            internal readonly TaskCompletionSource<FfmpegVideoOutcome> Cleanup =
                new TaskCompletionSource<FfmpegVideoOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

            private TaskCompletionSource<Rgb24FrameWriteOutcome> Current
            {
                get
                {
                    lock (_sync)
                    {
                        if (_write == null)
                        {
                            _write = new TaskCompletionSource<Rgb24FrameWriteOutcome>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                        return _write;
                    }
                }
            }

            internal Task<Rgb24FrameWriteOutcome> Completion { get { return Current.Task; } }

            internal void Complete(Rgb24FrameWriteOutcome outcome) { Current.TrySetResult(outcome); }

            internal void Fault(Exception error) { Current.TrySetException(error); }

            internal void Cancel() { Current.TrySetCanceled(); }

            internal void Next()
            {
                lock (_sync) { _write = null; }
            }

            public Rgb24TransportAttempt TryBeginWrite(OwnedRgb24Frame frame)
            {
                return Rgb24TransportAttempt.Accepted(Completion);
            }

            public void RequestStop(string reason) { }

            public long DeliveredFrameCount { get { return 1L; } }

            public Task<FfmpegVideoOutcome> BeginFinalize() { return null; }

            public Task<FfmpegVideoOutcome> CleanupTask { get { return Cleanup.Task; } }
        }

        /// <summary>
        /// 扣留 Post delegate 的 SynchronizationContext：只保存 state 并 signal，绝不执行 delegate。
        /// ThrowAfterStore 模拟 Post 抛错。
        /// </summary>
        private sealed class HoldingContext : SynchronizationContext
        {
            private sealed class Record
            {
                internal SendOrPostCallback Callback;
                internal object State;
            }

            private readonly object _sync = new object();
            private readonly List<Record> _records = new List<Record>();
            private readonly ManualResetEventSlim _posted = new ManualResetEventSlim(false);
            private int _executed;

            internal bool ThrowAfterStore { get; set; }

            internal int PostCount { get { lock (_sync) return _records.Count; } }

            internal int ExecutedCount { get { lock (_sync) return _executed; } }

            internal object LastStoredState
            {
                get
                {
                    lock (_sync)
                    {
                        return _records.Count == 0 ? null : _records[_records.Count - 1].State;
                    }
                }
            }

            internal WaitHandle PostedHandle { get { return _posted.WaitHandle; } }

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_sync) _records.Add(new Record { Callback = callback, State = state });
                _posted.Set();
                if (ThrowAfterStore) throw new InvalidOperationException("f0-injected-post-failure");
                // 刻意不执行 callback。
            }

            internal bool WaitForPost(int timeoutMs) { return _posted.Wait(timeoutMs); }

            internal bool WaitForPost() { return _posted.Wait(WatchdogMs); }

            internal object StateAt(int index)
            {
                lock (_sync) return _records[index].State;
            }

            internal void ExecuteAll()
            {
                Record[] snapshot;
                lock (_sync)
                {
                    snapshot = _records.ToArray();
                    _executed += snapshot.Length;
                    _records.Clear();
                }
                for (int i = 0; i < snapshot.Length; i++)
                {
                    snapshot[i].Callback(snapshot[i].State);
                }
            }

            internal void Reset()
            {
                lock (_sync)
                {
                    _records.Clear();
                    _posted.Reset();
                }
            }
        }
    }
}
