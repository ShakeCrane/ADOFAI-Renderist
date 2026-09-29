using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// L3-B — Controlled IO Backpressure 的 Unity-free 合同回归。
    ///
    /// 与 <see cref="Rgb24LifecycleTests"/> 的分工：那边覆盖正常生命周期、取消收敛、
    /// identity 与既有 stalled-bridge 保护；**这里只覆盖"帧交付的 Completion 被可控地
    /// 保持 pending"这一形态**，即真正的 IO 背压，而不是快速连续交付。
    ///
    /// 本文件断言的行为合同：
    ///   1. pending delivery 冻结事务：active frame 只有 1、lease 保持有效、release=0、
    ///      Commit=0、不得进入下一 frame transaction；
    ///   2. 长期 pending 本身合法：不以观察次数（或任何墙钟量）判定失败，
    ///      并且**严格区分**「Task 尚未完成」与「Task 已完成但 main-thread 交接丢失」；
    ///   3. pending 期间取消：不 Commit、不提前释放、不进第二帧、residual 未收敛时
    ///      restart gate 关闭；frame Completion 与 CleanupTask 的**两种收敛顺序**都必须
    ///      exactly-once 收敛；
    ///   4. 旧 generation 的迟到通知：只能在旧 generation **真正达到可 restart 的条件**
    ///      之后启动新 generation，旧通知不得触碰新 lease / 新 buffer / 新帧号；
    ///   5. success exactly once：重复观察 / 重复 pump 不产生第二次 Commit eligibility；
    ///   6. fault / cancellation：不 Commit、无第二帧、lease 最终释放、
    ///      cleanup / residual 未完成前 restart 保持 fail-closed。
    ///
    /// 全部用例事件驱动：只用 TaskCompletionSource 与显式 pump，不 sleep、不依赖墙钟，
    /// 也不依赖真实 FFmpeg 性能。真实 Unity scheduler Tick 的验收留给 runtime acceptance。
    /// </summary>
    internal static class Rgb24BackpressureTests
    {
        private const long GenerationA = 7L;
        private const long GenerationB = 8L;
        private const long FrameIndex0 = 0L;
        private const long FrameIndex1 = 1L;
        private const long FrameIndex2 = 2L;

        public static void Run()
        {
            PendingDeliveryFreezesTransaction();
            LongPendingIsLegal();
            CancelDuringPendingDelivery();
            LateOldGenerationNotification();
            SuccessExactlyOnce();
            FaultAndCancellation();
            ProductionPendingPathContracts();
        }

        // ==================================================== 1. pending delivery freezes the transaction

        private static void PendingDeliveryFreezesTransaction()
        {
            TestKit.Run("rgb24 backpressure: a pending delivery keeps one active frame and no release or commit", () =>
            {
                var f = new Fixture();
                f.Begin(GenerationA, FrameIndex0);

                OwnedRgb24Frame active = f.Transaction.CurrentFrame;
                byte[] buffer = active.SegmentBuffer(0);
                long token = f.Transaction.LeaseTokenId;

                // 交付已被接受但 Completion 保持 pending：这就是可控 IO 背压。
                TestKit.Check(!f.Transport.Completion.IsCompleted, "the write must stay pending");

                AssertFrozenAtPending(f, GenerationA, FrameIndex0, token, buffer);
                AssertCommitImpossible(f);

                // 重复驱动当前可测试状态机：每帧一个 tick，绝不允许进入第二帧事务。
                for (int tick = 0; tick < 64; tick++)
                {
                    f.Tick();
                    AssertFrozenAtPending(f, GenerationA, FrameIndex0, token, buffer);
                    AssertCommitImpossible(f);
                }

                Check(1L, f.Context.Pool.AcquireCount);
                Check(0L, f.Context.Pool.ReleaseCount);
                TestKit.Check(f.Context.Pool.HasOutstandingLease, "the single lease must stay valid");
                TestKit.Check(ReferenceEquals(active, f.Transaction.CurrentFrame),
                    "the active frame must stay the original one");
                TestKit.CheckEqual(Rgb24TransactionPhase.AwaitingDelivery, f.Transaction.Phase, "phase stays pending");
                TestKit.Check(f.Transaction.HasUnconvergedDelivery, "a pending delivery is unconverged by definition");
            });

            TestKit.Run("rgb24 backpressure: releasing the pending completion converges exactly once and only then moves on", () =>
            {
                var f = new Fixture();
                f.Begin(GenerationA, FrameIndex0);

                long token = f.Transaction.LeaseTokenId;
                OwnedRgb24Frame first = f.Transaction.CurrentFrame;
                byte[] firstBuffer = first.SegmentBuffer(0);

                // 释放前：一次都不能消费 / 释放 / Commit。
                for (int tick = 0; tick < 8; tick++) f.Tick();
                Check(0L, f.Context.Pool.ReleaseCount);
                TestKit.Check(!f.Context.Pool.OutstandingLease.CompletionConsumed, "completion not consumed yet");
                AssertCommitImpossible(f);

                // 受控释放写入：Completion 只产生一次成功结果。
                f.Transport.Complete(true);
                Rgb24DeliveryEnvelope envelope = f.FinishWrite();

                Check(Rgb24TransactionOutcome.Committed, f.Consume(envelope));
                Check(1L, f.Context.Pool.ReleaseCount);
                TestKit.Check(f.Context.Pool.OutstandingLease == null, "the completed lease is retired");
                Check(Rgb24TransactionPhase.Idle, f.Transaction.Phase);

                // 只有消费之后才可能进入下一帧事务，而且必须是独立的 lease / token。
                f.Transport.NextWrite();
                f.Begin(GenerationA, FrameIndex1);
                TestKit.Check(f.Transaction.LeaseTokenId != token, "a fresh one-shot token per frame");
                TestKit.Check(!ReferenceEquals(first, f.Transaction.CurrentFrame), "a new frame identity");
                Check(2L, f.Context.Pool.AcquireCount);
                TestKit.Check(f.Transaction.CurrentFrame.AbsoluteFrameIndex == FrameIndex1, "frame N+1");
                // 会话内复用的是同一批段数组 —— 只有在上一帧 Completion 被消费之后才允许。
                TestKit.Check(ReferenceEquals(firstBuffer, f.Transaction.CurrentFrame.SegmentBuffer(0)),
                    "the session reuses the segment arrays after the completion was consumed");
            });
        }

        // ==================================================== 2. long pending is legal

        private static void LongPendingIsLegal()
        {
            TestKit.Run("rgb24 backpressure: a long pending delivery never reports a stall or a progress timeout", () =>
            {
                var f = new Fixture();
                f.Begin(GenerationA, FrameIndex0);

                byte[] buffer = f.Transaction.CurrentFrame.SegmentBuffer(0);

                // 远超既有桥接不变量上限（BridgeStallTickLimit）的观察次数：仍然必须合法。
                int ticks = (Rgb24FrameTransaction.BridgeStallTickLimit * 4) + 17;
                for (int tick = 0; tick < ticks; tick++)
                {
                    f.Tick();
                    Check(0L, f.Context.Pool.ReleaseCount);
                }

                TestKit.Check(!f.Transport.Completion.IsCompleted, "the write is still pending");
                Check(0L, f.Context.Pool.ReleaseCount);
                TestKit.Check(f.Context.Pool.OutstandingLease.DeliveryPinned, "the lease stays pinned");
                TestKit.Check(!f.Context.Pool.OutstandingLease.CompletionConsumed, "nothing was consumed");
                TestKit.Check(ReferenceEquals(buffer, f.Transaction.CurrentFrame.SegmentBuffer(0)),
                    "the pending frame buffer is untouched");
                Check(1L, f.Context.Pool.AcquireCount);
                AssertCommitImpossible(f);

                // 合法背压必须以完全相同的 exactly-once 结果收敛。
                f.Transport.Complete(true);
                Check(Rgb24TransactionOutcome.Committed, f.Consume(f.FinishWrite()));
                Check(1L, f.Context.Pool.ReleaseCount);
                Check(Rgb24TransactionPhase.Idle, f.Transaction.Phase);
            });

            TestKit.Run("rgb24 backpressure: pending Task and lost main-thread notification are distinguished", () =>
            {
                // (a) Task 尚未 Completion：无论观察多少次都不构成故障，也不产生任何错误。
                var pending = new Fixture();
                pending.Begin(GenerationA, FrameIndex0);
                for (int tick = 0; tick < Rgb24FrameTransaction.BridgeStallTickLimit * 3; tick++)
                {
                    TestKit.Check(!pending.Transaction.TryTakePendingEnvelope(out _, out string error),
                        "a pending write has no envelope");
                    Check(null, error);
                }
                TestKit.Check(pending.Transport.Completion.IsCompleted == false,
                    "a legal pending write stays incomplete");

                // (b) Task 已 Completion 但 main-thread 通知丢失：只有这种形态才允许触发既有保护。
                var lost = new Fixture(new DroppingContext());
                lost.Begin(GenerationA, FrameIndex0);
                lost.Transport.Complete(true);
                TestKit.Check(lost.Drop.WaitForHandOff(30000), "the bridge must have attempted the hand-off");

                for (int tick = 0; tick < Rgb24FrameTransaction.BridgeStallTickLimit; tick++)
                {
                    TestKit.Check(!lost.Transaction.TryTakePendingEnvelope(out _, out string error),
                        "the lost notification can never yield an envelope");
                    Check(null, error);
                }
                TestKit.Check(!lost.Transaction.TryTakePendingEnvelope(out _, out string stalled), "still no envelope");
                Check("rgb24-bridge-notification-stalled", stalled);

                // 保护只报告故障：绝不因此释放正在被后台读取的缓冲，也绝不 Commit。
                Check(true, lost.Context.Pool.OutstandingLease.DeliveryPinned);
                Check(0L, lost.Context.Pool.ReleaseCount);
                lost.Cleanup();
                TestKit.Check(lost.Context.TryStopAndDrain("bridge-stalled", out _), "no pinned leak");
                Check(1L, lost.Context.Pool.ReleaseCount);
            });
        }

        // ==================================================== 3. cancel during pending delivery

        private static void CancelDuringPendingDelivery()
        {
            TestKit.Run("rgb24 backpressure: cancel while the write is pending never commits, never releases early, never starts frame N+1", () =>
            {
                var f = new Fixture();
                f.Begin(GenerationA, FrameIndex0);

                byte[] buffer = f.Transaction.CurrentFrame.SegmentBuffer(0);
                long token = f.Transaction.LeaseTokenId;

                // Stop 已传播到 pipeline，但真实写入仍 pending。
                TestKit.Check(!f.Context.TryStopAndDrain("user-stop", out _),
                    "a pending frame delivery cannot converge yet");
                TestKit.Check(f.Context.Stopping, "the stop request has been propagated");

                AssertFrozenAtPending(f, GenerationA, FrameIndex0, token, buffer);
                AssertCommitImpossible(f);
                Check(0L, f.Context.Pool.ReleaseCount);

                // residual 尚未收敛：restart gate 必须保持关闭。
                Rgb24DeliveryContext slot = f.Context;
                TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, null, false, out string blocked),
                    "the re-arm gate must stay closed");
                Check("rgb24-context-active-or-residual", blocked);
                TestKit.Check(ReferenceEquals(f.Context, slot), "the residual context must be retained");

                // 只有真实的 frame Completion + CleanupTask 都不再持有 ownership 才允许重开。
                // 先推进 pipeline 侧的 Cancel / 资源收敛，再释放 pending 的写入。
                f.Cleanup();
                AssertCommitImpossible(f);
                f.Transport.Complete(true);
                TestKit.Check(!f.TryFinishWrite(out _), "a cancelled delivery can never surface a commitable envelope");
                TestKit.Check(!f.Context.HasFrameOwnership, "the frame barrier is clear");
                TestKit.Check(!f.Context.HasPipelineOwnership, "the pipeline barrier is clear");
                TestKit.Check(f.Context.TryStopAndDrain("user-stop", out _), "converged");
                Check(1L, f.Context.Pool.ReleaseCount);
                TestKit.Check(Rgb24DeliveryContext.TryReplace(ref slot, null, false, out _), "re-arm allowed");
            });

            foreach (bool cleanupFirst in new[] { false, true })
                TestKit.Run("rgb24 backpressure: cancel ordering converges exactly once cleanupFirst=" + cleanupFirst, () =>
                {
                    var f = new Fixture();
                    f.Begin(GenerationA, FrameIndex0);

                    byte[] buffer = f.Transaction.CurrentFrame.SegmentBuffer(0);
                    TestKit.Check(!f.Context.TryStopAndDrain("cancel", out _), "both barriers pending");
                    TestKit.Check(f.Context.Pool.OutstandingLease.DeliveryPinned,
                        "the lease stays pinned until the real Completion");

                    Rgb24DeliveryContext slot = f.Context;
                    TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, new Fixture().Context, false, out _),
                        "re-arm blocked while either barrier is pending");

                    bool wroteFirst = !cleanupFirst;
                    if (wroteFirst)
                    {
                        f.Transport.Complete(true);
                        TestKit.Check(!f.TryFinishWrite(out _),
                            "a cancelled delivery can never surface a commitable envelope");
                        f.Cleanup();
                    }
                    else
                    {
                        // CleanupTask 先收敛：frame buffer 仍在后台读取，必须逐字节保持不变。
                        f.Cleanup();
                        Check(buffer, f.Context.Pool.OutstandingLease.Frame.SegmentBuffer(0));
                        AssertCommitImpossible(f);
                        f.Transport.Complete(true);
                        TestKit.Check(!f.TryFinishWrite(out _),
                            "a cancelled delivery can never surface a commitable envelope");
                    }

                    TestKit.Check(!f.Context.HasFrameOwnership, "the frame barrier is clear");
                    Check(1L, f.Context.Pool.ReleaseCount);
                    TestKit.Check(!f.Context.HasPipelineOwnership, "the pipeline barrier is clear");
                    TestKit.Check(f.Context.TryStopAndDrain("cancel", out _), "both barriers converged");
                    Check(1L, f.Context.Pool.ReleaseCount);
                    TestKit.Check(!f.Context.HasResidualOwnership, "no residual ownership");
                    TestKit.Check(Rgb24DeliveryContext.TryReplace(ref slot, new Fixture().Context, false, out _),
                        "re-arm allowed");
                });
        }

        // ==================================================== 4. late old-generation notification

        private static void LateOldGenerationNotification()
        {
            TestKit.Run("rgb24 backpressure: a late old-generation envelope cannot touch the new generation", () =>
            {
                // 旧 generation 先被取消，并且**真正**收敛到完成状态（不是绕过 gate）。
                var old = new Fixture();
                old.Begin(GenerationA, FrameIndex0);
                byte[] oldBuffer = old.Transaction.CurrentFrame.SegmentBuffer(0);
                old.Context.TryStopAndDrain("cancel", out _);
                old.Transport.Complete(true);
                TestKit.Check(!old.TryFinishWrite(out _),
                    "the cancelled old frame can never surface a commitable envelope");
                old.Cleanup();
                TestKit.Check(old.Context.TryStopAndDrain("cancel", out _), "the old generation converged");
                Check(1L, old.Context.Pool.ReleaseCount);
                TestKit.Check(!old.Context.HasResidualOwnership,
                    "the old generation must be truly restartable, not bypassed");

                // 旧 generation 达到允许 restart 的条件之后才启动新 generation。
                // 旧 context 已被 Stop 标记（_stopping 不可逆），因此 restart 的目标是**新** context；
                // 这里要证明的是收敛后的旧 generation 不再以 residual 挡住替换。
                var next = new Fixture();
                Rgb24DeliveryContext slot = old.Context;
                TestKit.Check(Rgb24DeliveryContext.TryReplace(ref slot, next.Context, false, out string replaceError),
                    "the converged old generation must not block a fresh context: " + replaceError);
                TestKit.Check(ReferenceEquals(next.Context, slot), "the fresh context is installed");
                next.Begin(GenerationB, FrameIndex0);

                byte[] newBuffer = next.Transaction.CurrentFrame.SegmentBuffer(0);
                long newToken = next.Transaction.LeaseTokenId;
                TestKit.Check(!ReferenceEquals(oldBuffer, newBuffer), "no cross-generation buffer reuse");

                // 旧 generation 的迟到 main-thread notification：在旧 context 上 pump。
                old.Pump.Pump();

                TestKit.Check(!next.Transaction.TryTakePendingEnvelope(out _, out _),
                    "the old notification must not appear on the new generation");

                // 即使把旧信封直接投给新 generation，也必须被 generation 隔离拒绝。
                var lateOld = new Rgb24DeliveryEnvelope(
                    GenerationA, FrameIndex0, newToken, SuccessOutcome(), null, null);
                Check(Rgb24TransactionOutcome.RejectedStaleGeneration, next.Consume(lateOld));

                AssertFrozenAtPending(next, GenerationB, FrameIndex0, newToken, newBuffer);
                Check(0L, next.Context.Pool.ReleaseCount);
                TestKit.Check(!next.Context.Pool.OutstandingLease.CompletionConsumed,
                    "the new completion is untouched");

                // 新 generation 仍然正常工作。
                next.Transport.Complete(true);
                Check(Rgb24TransactionOutcome.Committed, next.Consume(next.FinishWrite()));
                Check(1L, next.Context.Pool.ReleaseCount);
                Check(Rgb24TransactionPhase.Idle, next.Transaction.Phase);
            });
        }

        // ==================================================== 5. success exactly once

        private static void SuccessExactlyOnce()
        {
            TestKit.Run("rgb24 backpressure: repeated observation and pumping still commit exactly once", () =>
            {
                var f = new Fixture();
                f.Begin(GenerationA, FrameIndex0);

                f.Transport.Complete(true);
                Rgb24DeliveryEnvelope envelope = f.FinishWrite();

                int commits = 0;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Rgb24TransactionOutcome outcome = f.Consume(envelope);
                    if (outcome == Rgb24TransactionOutcome.Committed) commits++;
                    else Check(Rgb24TransactionOutcome.RejectedDuplicateCompletion, outcome);
                }
                Check(1, commits);
                Check(1L, f.Context.Pool.ReleaseCount);
                Check(1L, f.Context.Pool.AcquireCount);

                // 重复 pump / 重复观察不得产生第二次 Commit eligibility 或第二次释放。
                f.Pump.Pump();
                for (int tick = 0; tick < 8; tick++)
                {
                    f.Tick();
                    Check(1L, f.Context.Pool.ReleaseCount);
                }
                TestKit.Check(!f.Transaction.TryTakePendingEnvelope(out _, out _), "no envelope remains");
                Check(Rgb24TransactionPhase.Idle, f.Transaction.Phase);
                TestKit.Check(!f.Transaction.HasResidualOwnership, "no residual ownership");
            });
        }

        // ==================================================== 6. fault / cancellation

        private static void FaultAndCancellation()
        {
            TestKit.Run("rgb24 backpressure: a failed pending delivery never commits and still converges once", () =>
            {
                var f = new Fixture();
                f.Begin(GenerationA, FrameIndex0);

                // 写入仍 pending 时绝不能准备第二帧。
                TestKit.Check(f.Transaction.TryBeginFrame(GenerationA, FrameIndex1, out string pendingError) !=
                    Rgb24TransactionOutcome.Accepted, "no second frame while the write is pending");
                TestKit.Check(pendingError != null && pendingError.StartsWith("rgb24-transaction-frame-in-flight",
                    StringComparison.Ordinal), "the invariant error must name the in-flight frame");
                Check(1L, f.Context.Pool.AcquireCount);

                // 受控失败：写出过程以失败结果结束（不是快速连续交付）。
                f.Transport.Complete(false);
                Rgb24DeliveryEnvelope envelope = f.FinishWrite();

                Check(Rgb24TransactionOutcome.Failed, f.Consume(envelope));
                Check(1L, f.Context.Pool.AcquireCount);

                // 失败帧的 Completion 已消费：lease 恰好释放一次，且不再持有 frame ownership。
                TestKit.Check(!f.Context.HasFrameOwnership, "the frame barrier is clear");
                Check(1L, f.Context.Pool.ReleaseCount);
                Check(1L, f.Context.Pool.AcquireCount);

                // 完成取消收敛：此后 session 处于 aborting 状态。
                f.Cleanup();
                TestKit.Check(f.Context.TryStopAndDrain("delivery-failed", out _), "converged");
                Check(1L, f.Context.Pool.ReleaseCount);
                TestKit.Check(!f.Context.HasResidualOwnership, "no residual ownership");

                // aborting 状态下不得静默开始下一帧；只有显式 Reset 才放行。
                Check(Rgb24TransactionOutcome.RejectedWrongPhase,
                    f.Transaction.TryBeginFrame(GenerationA, FrameIndex1, out string abortingError));
                Check("rgb24-transaction-frame-in-flight-or-aborting", abortingError);
                Check(1L, f.Context.Pool.AcquireCount);

                f.Transaction.Reset();
                f.Transport.NextWrite();
                f.Begin(GenerationA, FrameIndex1);
                Check(2L, f.Context.Pool.AcquireCount);
                TestKit.Check(f.Transaction.CurrentFrame.AbsoluteFrameIndex == FrameIndex1, "frame N+1");
            });

            TestKit.Run("rgb24 backpressure: a faulted write Task is reported and cannot unlock a commit", () =>
            {
                var f = new Fixture();
                f.Begin(GenerationA, FrameIndex0);

                f.Transport.CompleteFaulted();
                Rgb24DeliveryEnvelope envelope = f.FinishWrite();

                // 写入 Task fault：绝不 Commit，但 Completion 本身是可消费的终态。
                Check(Rgb24TransactionOutcome.Failed, f.Consume(envelope));
                TestKit.Check(!f.Transaction.HasUnconvergedDelivery, "the faulted notification converged");
                TestKit.Check(!f.Context.HasFrameOwnership, "the faulted frame is retired exactly once");
                Check(1L, f.Context.Pool.AcquireCount);

                // residual 未完成时 restart 保持 fail-closed。
                Rgb24DeliveryContext slot = f.Context;
                TestKit.Check(!Rgb24DeliveryContext.TryReplace(ref slot, new Fixture().Context, false, out _),
                    "restart stays fail-closed until cleanup settles");

                f.Cleanup();
                TestKit.Check(f.Context.TryStopAndDrain("delivery-faulted", out _), "cleanup converged");
                Check(1L, f.Context.Pool.ReleaseCount);
                TestKit.Check(!f.Context.HasResidualOwnership, "no residual ownership");

                // aborting 状态下仍不得静默准备第二帧；显式 Reset 才放行。
                Check(Rgb24TransactionOutcome.RejectedWrongPhase,
                    f.Transaction.TryBeginFrame(GenerationA, FrameIndex1, out _));
                f.Transaction.Reset();
                TestKit.Check(Rgb24DeliveryContext.TryReplace(ref slot, new Fixture().Context, false, out _),
                    "restart allowed after convergence");
            });
        }

        // ==================================================== production pending-path contracts

        private static void ProductionPendingPathContracts()
        {
            TestKit.Run("rgb24 backpressure: production pending-delivery path cannot advance or commit a frame", () =>
            {
                string scheduler = Source("Export/DeterministicFrameScheduler.cs");
                string tick = Method(scheduler, "private static void TickAwaitingDelivery()");

                // pending / stalled 都必须先返回；CommitFrame 只存在于 Committed 分支。
                int take = tick.IndexOf("TryTakePendingEnvelope(out envelope, out bridgeError)", StringComparison.Ordinal);
                int commit = tick.IndexOf("CommitFrame(envelope.AbsoluteFrameIndex", StringComparison.Ordinal);
                TestKit.Check(take >= 0, "the pending path observes the envelope");
                TestKit.Check(commit > take, "CommitFrame must come after the pending-return");
                TestKit.Check(CountOf(tick, "CommitFrame(") == 1, "exactly one CommitFrame call site");
                TestKit.Check(tick.IndexOf("CheckArmedCaptureWatchdogs", StringComparison.Ordinal) < 0,
                    "no IO watchdog in the pending path");
                TestKit.Check(tick.Contains("ResumeAfterRgb24Delivery(_preEntryCapturing)"),
                    "the lifecycle resumes only from the Committed branch");

                // AwaitingDelivery 的 tick 分派不得触碰 capture watchdog / 时间推进。
                string tickEntry = Method(scheduler, "public static void Tick()");
                int awaitingDelivery = tickEntry.IndexOf("case SchedulerStatus.AwaitingDelivery:", StringComparison.Ordinal);
                int deliveryCall = tickEntry.IndexOf("TickAwaitingDelivery();", StringComparison.Ordinal);
                TestKit.Check(awaitingDelivery >= 0 && deliveryCall > awaitingDelivery,
                    "AwaitingDelivery dispatches to the pending path");
                string awaitingDeliveryBlock = tickEntry.Substring(awaitingDelivery, deliveryCall - awaitingDelivery);
                TestKit.Check(awaitingDeliveryBlock.IndexOf("CheckArmedCaptureWatchdogs", StringComparison.Ordinal) < 0,
                    "the AwaitingDelivery branch deliberately skips the capture watchdog");
            });

            TestKit.Run("rgb24 backpressure: residual convergence is shown read-only and never as an actionable stop", () =>
            {
                string controller = Source("Export/EditorExportController.cs");
                string busy = ExpressionBody(controller, "public static bool IsBusy =>");

                // 只读投影必须建立在既有 IsBusy 之上：安全语义不被削弱。
                TestKit.Check(busy.Contains("DeterministicFrameScheduler.HasPendingRgb24Cleanup"),
                    "IsBusy still counts pending RGB24 cleanup");
                string converging = ExpressionBody(controller, "public static bool IsConvergingTerminalResources =>");
                TestKit.Check(converging.Contains("IsBusy"), "the converging projection derives from IsBusy");
                TestKit.Check(converging.Contains("_terminalRearmPending"),
                    "an in-progress terminal re-arm is not a converging state (Stop is still meaningful)");
                TestKit.Check(converging.Contains("EditorExportState.Preparing") &&
                              converging.Contains("EditorExportState.Running"),
                    "only a terminated session counts as converging");
                // 该投影必须是纯只读的：不得引入新的 gate / timeout。
                foreach (string forbidden in new[] { "TryStopAndDrain", "RequestStop", "Cancel(", "Timeout" })
                    TestKit.Check(converging.IndexOf(forbidden, StringComparison.Ordinal) < 0,
                        "the converging projection must stay a pure read-only projection (no " + forbidden + ")");

                // GUI 必须在渲染 Stop 按钮之前就以只读状态返回。
                string draw = Method(Source("ModEntry.cs"), "private static void DrawMasterTimelineHandoffGui()");
                int convergingCheck = draw.IndexOf("EditorExportController.IsConvergingTerminalResources", StringComparison.Ordinal);
                int stopButton = draw.IndexOf("UiText.GuiMasterTimelineHandoffBtnStop", StringComparison.Ordinal);
                TestKit.Check(convergingCheck >= 0, "the GUI consults the converging state");
                TestKit.Check(stopButton > convergingCheck, "the read-only branch precedes the stop button");
                string convergingBranch = draw.Substring(convergingCheck, stopButton - convergingCheck);
                TestKit.Check(convergingBranch.Contains("return;"),
                    "the converging state returns before offering an actionable button");
                TestKit.Check(convergingBranch.Contains("GuiMasterTimelineHandoffStateConverging"),
                    "the converging state is surfaced with explicit read-only text");
            });
        }

        // ==================================================== helpers

        private static void AssertFrozenAtPending(Fixture f, long generation, long frameIndex, long token, byte[] buffer)
        {
            Check(1L, f.Context.Pool.AcquireCount);
            TestKit.Check(f.Context.Pool.HasOutstandingLease, "exactly one active frame transaction");
            TestKit.Check(f.Context.Pool.OutstandingLease.DeliveryPinned, "the lease stays pinned while pending");
            TestKit.Check(!f.Context.Pool.OutstandingLease.CompletionConsumed, "no completion consumed");
            Check(Rgb24TransactionPhase.AwaitingDelivery, f.Transaction.Phase);
            TestKit.Check(f.Transaction.CurrentFrame.AbsoluteFrameIndex == frameIndex, "frame index unchanged");
            TestKit.Check(f.Transaction.Generation == generation, "generation unchanged");
            TestKit.Check(f.Transaction.LeaseTokenId == token, "token unchanged");
            TestKit.Check(ReferenceEquals(buffer, f.Transaction.CurrentFrame.SegmentBuffer(0)),
                "the delivery buffer is the same object");
        }

        private static void AssertCommitImpossible(Fixture f)
        {
            TestKit.Check(!f.Transaction.TryTakePendingEnvelope(out Rgb24DeliveryEnvelope envelope, out _),
                "no envelope may be consumable while the write is pending");
            Check(null, envelope);
            Check(0L, f.Context.Pool.ReleaseCount);
            TestKit.Check(f.Context.Pool.OutstandingLease == null || !f.Context.Pool.OutstandingLease.CompletionConsumed,
                "no completion may be consumed while pending");
        }

        private static int CountOf(string text, string needle)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        private static void Check(object expected, object actual) => TestKit.CheckEqual(expected, actual, "contract");

        private static Rgb24FrameWriteOutcome SuccessOutcome() => new Rgb24FrameWriteOutcome(true, null, null, 1);

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

        /// <summary>表达式体成员（<c>=&gt; … ;</c>）的源码切片；不能用花括号平衡来提取。</summary>
        private static string ExpressionBody(string source, string signature)
        {
            int signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
            TestKit.Check(signatureStart >= 0, "expression-bodied member exists: " + signature);
            int end = source.IndexOf(';', signatureStart);
            TestKit.Check(end > signatureStart, "expression-bodied member terminates: " + signature);
            return source.Substring(signatureStart, end - signatureStart);
        }

        /// <summary>
        /// 一个已经走到"交付被接受、写入保持 pending"的事务。
        /// 写入只能由测试显式 Complete；信 envelope 只能由测试显式 Pump 交接。
        /// </summary>
        private sealed class Fixture
        {
            internal readonly PumpContext Pump = new PumpContext();
            internal readonly ControlledTransport Transport;
            internal readonly DroppingContext Drop;
            internal readonly Rgb24DeliveryContext Context;
            internal Rgb24FrameTransaction Transaction => Context.Transaction;

            internal Fixture(DroppingContext drop = null)
            {
                Drop = drop;
                Transport = new ControlledTransport(Pump);
                SynchronizationContext context = (SynchronizationContext)drop ?? Pump;
                var original = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    Rgb24MainThreadBridge.TryCapture(out var bridge, out _);
                    Rgb24FrameLayout.TryCreate(3, 2, 5, out var layout, out _); // 18-byte frame, 3 segments
                    Context = new Rgb24DeliveryContext(layout, Transport, Transport, bridge);
                }
                finally { SynchronizationContext.SetSynchronizationContext(original); }
            }

            /// <summary>Prepare(N) → EOF → 交付被接受（Completion 保持 pending）。</summary>
            internal void Begin(long generation = GenerationA, long frameIndex = FrameIndex0)
            {
                Check(Rgb24TransactionOutcome.Accepted, Transaction.TryBeginFrame(generation, frameIndex, out _));
                Check(Rgb24TransactionOutcome.Accepted,
                    Transaction.TryCompleteEof(Transaction.Generation, Transaction.FrameIndex, out _, out _));
                Check(Rgb24TransactionOutcome.Accepted, Transaction.TryBeginDelivery(out _, out _));
            }

            /// <summary>模拟一次主线程 tick（只观察，不推进任何时间）。</summary>
            internal void Tick() => Transaction.TryTakePendingEnvelope(out _, out _);

            /// <summary>Pump 主线程上下文；返回当时是否真的存在可取回的信封。</summary>
            internal bool TryFinishWrite(out Rgb24DeliveryEnvelope envelope)
            {
                // 事件驱动，不靠 Sleep：反复「排空已入队的回调 → 等待后台 continuation 至少一次
                // Post → 再排空」，直到信封真的可取回。同步完成与延迟 Post 两种形态都能覆盖。
                for (int attempt = 0; attempt < 1000; attempt++)
                {
                    Pump.Pump();
                    if (Transaction.TryTakePendingEnvelope(out envelope, out _)) return true;
                    TestKit.Check(Pump.WaitForHandOff(30000), "the bridge must post the completion envelope");
                }
                Pump.Pump();
                return Transaction.TryTakePendingEnvelope(out envelope, out _);
            }

            /// <summary>Pump 主线程上下文并取回已交接的信封。</summary>
            internal Rgb24DeliveryEnvelope FinishWrite()
            {
                Rgb24DeliveryEnvelope envelope;
                TestKit.Check(TryFinishWrite(out envelope), "expected a commitable envelope");
                return envelope;
            }

            internal Rgb24TransactionOutcome Consume(Rgb24DeliveryEnvelope envelope)
                => Transaction.ConsumeEnvelope(true, envelope, out _, out _);

            internal void Cleanup(bool residual = false)
                => Transport.Cleanup.TrySetResult(new FfmpegVideoOutcome
                {
                    State = FfmpegVideoPipelineState.Cancelled,
                    ResidualOwnership = residual,
                });
        }

        /// <summary>写入保持 pending，直到测试显式完成（成功 / 失败结果 / fault）。</summary>
        private sealed class ControlledTransport : IRgb24FrameTransport, IRgb24PipelineLifetime
        {
            private readonly PumpContext _pump;

            internal ControlledTransport(PumpContext pump)
            {
                _pump = pump;
            }

            internal TaskCompletionSource<Rgb24FrameWriteOutcome> Write =
                new TaskCompletionSource<Rgb24FrameWriteOutcome>();
            internal readonly TaskCompletionSource<FfmpegVideoOutcome> Cleanup =
                new TaskCompletionSource<FfmpegVideoOutcome>();

            internal Task<Rgb24FrameWriteOutcome> Completion => Write.Task;

            public Task<FfmpegVideoOutcome> CleanupTask => Cleanup.Task;
            public void RequestStop(string reason) { }

            public Rgb24TransportAttempt TryBeginWrite(OwnedRgb24Frame frame)
            {
                return Rgb24TransportAttempt.Accepted(Write.Task);
            }

            internal void Complete(bool success)
                => CompleteWrite(Write, success
                    ? SuccessOutcome()
                    : Rgb24FrameWriteOutcome.Failure("pipe-poisoned", "test"));

            internal void CompleteFaulted() => FailWrite(Write, new IOException("stdin write failed"));

            private void CompleteWrite(TaskCompletionSource<Rgb24FrameWriteOutcome> write, Rgb24FrameWriteOutcome outcome)
            {
                // 先清掉上一次交接的信号，再释放写入：WaitForHandOff 才能等到**本次**的 Post。
                _pump.ResetHandOff();
                write.TrySetResult(outcome);
            }

            private void FailWrite(TaskCompletionSource<Rgb24FrameWriteOutcome> write, Exception error)
            {
                _pump.ResetHandOff();
                write.TrySetException(error);
            }

            internal void NextWrite() { Write = new TaskCompletionSource<Rgb24FrameWriteOutcome>(); }
        }

        /// <summary>只入队、必须由测试显式 Pump 才执行（模拟延迟到达的 Unity 主线程交接）。</summary>
        private sealed class PumpContext : SynchronizationContext
        {
            private readonly Queue<Action> _callbacks = new Queue<Action>();
            private readonly object _sync = new object();
            private readonly ManualResetEventSlim _posted = new ManualResetEventSlim(false);

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_sync) _callbacks.Enqueue(() => callback(state));
                _posted.Set();
            }

            /// <summary>
            /// 开始一次新的交接等待。**原地**清信号而不是换对象：Complete 在触发写入完成之前
            /// 调用它，因此本次 Post 必然发生在其后，不会与等待方持有的引用竞争。
            /// </summary>
            internal void ResetHandOff() => _posted.Reset();

            /// <summary>等到本次写入真正完成一次 Post（事件驱动，不靠 Sleep）。</summary>
            internal bool WaitForHandOff(int timeoutMs) => _posted.Wait(timeoutMs);

            internal void Pump()
            {
                while (true)
                {
                    Action action;
                    lock (_sync)
                    {
                        if (_callbacks.Count == 0) return;
                        action = _callbacks.Dequeue();
                    }
                    action();
                }
            }
        }

        /// <summary>静默丢弃回调：模拟"Task 已结束但 main-thread 交接丢失"。</summary>
        private sealed class DroppingContext : SynchronizationContext
        {
            private readonly ManualResetEventSlim _posted = new ManualResetEventSlim(false);
            internal int PostCount { get; private set; }

            public override void Post(SendOrPostCallback callback, object state)
            {
                PostCount++;
                _posted.Set();
                // 刻意不执行 callback。
            }

            internal bool WaitForHandOff(int timeoutMs) => _posted.Wait(timeoutMs);
        }
    }
}
