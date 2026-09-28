using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// L3-A（RGB24 帧 ownership 事务）回归测试。
    ///
    /// 覆盖的生产源码刻意**不依赖 Unity**（Rgb24FrameLayout / Rgb24RowOrderPolicy /
    /// Rgb24FrameBufferPool / Rgb24FrameTransaction 与薄适配器 FfmpegRgb24FrameTransport
    /// 直接编译进本测试程序集）；真实 Unity 接线另由源码契约检查及实机验收覆盖。
    ///
    /// 断言的是**行为契约**，不是某次实验的数值：
    ///   * 事务阶段推进的唯一合法路径，以及 Awaiting* 期间禁止第二次 Prepare；
    ///   * stale generation / wrong frame / token mismatch / 重复消费一律拒绝；
    ///   * completion 被主线程消费之前，缓冲绝不可复用（pin / pinned lease）；
    ///   * 长度以 long 精确表达（不 clamp、不退化成更低分辨率）、分段与行边界无关；
    ///   * 行序 authority 只有 Rgb24RowOrderPolicy 一处（bottom-up 源 → top-first 交付）；
    ///   * 主线程桥 fail-closed，以及"Task 已结束但信封始终不到达"的桥接停滞检测。
    ///
    /// 全部用例都是事件驱动的：不给任何顺序留 Thread.Sleep 的余地，只使用
    /// TaskCompletionSource / 显式 pump。
    /// </summary>
    internal static class Rgb24FrameTransactionTests
    {
        /// <summary>默认测试几何：4x2 像素。按默认 1 MiB 段大小规划 → 单段 24 字节。</summary>
        private const int DefaultWidth = 4;
        private const int DefaultHeight = 2;
        private const long DefaultGeneration = 7L;
        private const long DefaultFrameIndex = 41L;

        public static void Run()
        {
            BeginFrameTests();
            EofTests();
            DeliveryEnvelopeTests();
            TransportStatusTests();
            AdapterSeamTests();
            LayoutTests();
            SegmentWriteTests();
            LeaseOwnershipTests();
            RowOrderTests();
            MainThreadBridgeTests();
            AbortAndStallTests();
        }

        // ==================================================================== helpers

        private static Rgb24FrameLayout DefaultLayout()
        {
            Rgb24FrameLayout layout;
            string error;
            if (!Rgb24FrameLayout.TryCreate(DefaultWidth, DefaultHeight, out layout, out error))
                throw new Exception("cannot create the default test layout: " + error);
            return layout;
        }

        /// <summary>
        /// 在受控 SynchronizationContext 下捕获一个主线程桥，并返回可恢复的捕获结果。
        /// 桥本身持有该上下文，因此用完必须立刻恢复测试线程的上下文。
        /// </summary>
        private static Rgb24MainThreadBridge CaptureBridge(SynchronizationContext context)
        {
            SynchronizationContext original = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                Rgb24MainThreadBridge bridge;
                string error;
                if (!Rgb24MainThreadBridge.TryCapture(out bridge, out error))
                    throw new Exception("cannot capture the test bridge: " + error);
                return bridge;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(original);
            }
        }

        /// <summary>该帧交付缓冲中的某一个字节（经由生产代码自己的分段定位读取）。</summary>
        private static byte ReadDeliveryByte(OwnedRgb24Frame frame, long byteOffset)
        {
            int segmentIndex;
            int offsetInSegment;
            if (!frame.Layout.TryLocate(byteOffset, out segmentIndex, out offsetInSegment))
                throw new Exception("test offset " + byteOffset + " is not locatable");

            return frame.SegmentBuffer(segmentIndex)[offsetInSegment];
        }

        private static Rgb24DeliveryEnvelope Envelope(
            long generation, long frameIndex, long tokenId, Rgb24FrameWriteOutcome outcome)
        {
            return new Rgb24DeliveryEnvelope(generation, frameIndex, tokenId, outcome);
        }

        private static Rgb24FrameWriteOutcome SuccessOutcome(long deliveredFrameCount = 1)
        {
            return new Rgb24FrameWriteOutcome(true, null, null, deliveredFrameCount);
        }

        /// <summary>
        /// 一个已经走到"交付已被接受、等测试决定写结果"的事务。
        /// pumpContext != null 时信封必须由测试显式 Pump 才能到达主线程。
        /// </summary>
        private sealed class DeliveryFixture
        {
            internal Rgb24FrameBufferPool Pool;
            internal FakeRgb24Transport Transport;
            internal Rgb24FrameTransaction Transaction;
            internal OwnedRgb24Frame Frame;
            internal long TokenId;
        }

        private static DeliveryFixture AcceptedDelivery(PumpSyncContext pumpContext)
        {
            var fixture = new DeliveryFixture();
            fixture.Pool = new Rgb24FrameBufferPool(DefaultLayout());
            fixture.Transport = new FakeRgb24Transport();
            fixture.Transaction = new Rgb24FrameTransaction(
                fixture.Pool, fixture.Transport, CaptureBridge(pumpContext));

            string error;
            TestKit.CheckEqual(
                Rgb24TransactionOutcome.Accepted,
                fixture.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                "prepare frame: " + error);

            string eofError;
            TestKit.CheckEqual(
                Rgb24TransactionOutcome.Accepted,
                fixture.Transaction.TryCompleteEof(
                    DefaultGeneration, DefaultFrameIndex, out fixture.Frame, out eofError),
                "complete eof: " + eofError);

            fixture.TokenId = fixture.Frame.TokenId;

            string deliveryError;
            string deliveryDetail;
            TestKit.CheckEqual(
                Rgb24TransactionOutcome.Accepted,
                fixture.Transaction.TryBeginDelivery(out deliveryError, out deliveryDetail),
                "begin delivery: " + deliveryError + " " + deliveryDetail);

            return fixture;
        }

        /// <summary>完成写入并取回信封；返回主线程消费结果。</summary>
        private static Rgb24TransactionOutcome CompleteAndConsume(
            DeliveryFixture fixture, PumpSyncContext pumpContext,
            Rgb24FrameWriteOutcome outcome, out string errorCode, out string errorDetail)
        {
            fixture.Transport.Complete(outcome);
            if (pumpContext != null)
            {
                TestKit.Check(pumpContext.WaitForHandOff(30000),
                    "the bridge must post the completion envelope");
                pumpContext.Pump();
            }

            Rgb24DeliveryEnvelope envelope;
            string bridgeError;
            TestKit.Check(
                fixture.Transaction.TryTakePendingEnvelope(out envelope, out bridgeError),
                "pending envelope expected, bridge error: " + bridgeError);
            TestKit.Check(envelope != null, "envelope must not be null");

            return fixture.Transaction.ConsumeEnvelope(true, envelope, out errorCode, out errorDetail);
        }

        private static void CheckPhase(Rgb24FrameTransaction transaction, Rgb24TransactionPhase expected, string what)
        {
            TestKit.CheckEqual(expected, transaction.Phase, what);
        }

        // ============================================================ await EOF / delivery

        private static void BeginFrameTests()
        {
            TestKit.Run("rgb24: TryBeginFrame moves Idle to AwaitingEOF", () =>
            {
                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                var flow = new TransactionFlow(pool, new FakeRgb24Transport(), new InlineSyncContext());

                TestKit.CheckEqual(Rgb24TransactionPhase.Idle, flow.Transaction.Phase, "phase before prepare");

                string error;
                Rgb24TransactionOutcome outcome =
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error);

                TestKit.CheckEqual(Rgb24TransactionOutcome.Accepted, outcome, "outcome: " + error);
                TestKit.CheckEqual(null, error, "error");
                CheckPhase(flow.Transaction, Rgb24TransactionPhase.AwaitingEOF, "phase after prepare");
                TestKit.CheckEqual(DefaultGeneration, flow.Transaction.Generation, "generation");
                TestKit.CheckEqual(DefaultFrameIndex, flow.Transaction.FrameIndex, "frame index");
                TestKit.Check(flow.Pool.HasOutstandingLease, "prepare must acquire exactly one lease");
                TestKit.CheckEqual(1L, flow.Pool.AcquireCount, "acquire count");
            });

            TestKit.Run("rgb24: TryCompleteEof moves AwaitingEOF to AwaitingDelivery and exposes the owned frame", () =>
            {
                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                var flow = new TransactionFlow(pool, new FakeRgb24Transport(), new InlineSyncContext());

                string error;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                    "prepare: " + error);

                OwnedRgb24Frame frame;
                string eofError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryCompleteEof(
                        DefaultGeneration, DefaultFrameIndex, out frame, out eofError),
                    "eof: " + eofError);

                TestKit.CheckEqual(null, eofError, "eof error");
                TestKit.Check(frame != null, "owned frame must be returned");
                CheckPhase(flow.Transaction, Rgb24TransactionPhase.AwaitingDelivery, "phase after eof");

                TestKit.CheckEqual(DefaultGeneration, frame.Generation, "frame generation");
                TestKit.CheckEqual(DefaultFrameIndex, frame.AbsoluteFrameIndex, "frame absolute index");
                TestKit.CheckEqual(DefaultWidth, frame.Width, "frame width");
                TestKit.CheckEqual(DefaultHeight, frame.Height, "frame height");
                TestKit.CheckEqual(24L, frame.ByteLength, "frame byte length");

                TestKit.Check(frame.TokenId > 0, "token id must be a positive one-shot token");
                TestKit.CheckEqual(frame.TokenId, flow.Pool.OutstandingLease.Frame.TokenId, "lease token matches");
                TestKit.CheckEqual(frame.TokenId, flow.Transaction.LeaseTokenId, "transaction token matches");
                TestKit.Check(!flow.Pool.OutstandingLease.DeliveryPinned,
                    "a frame that is only formed (not delivered) must not be pinned");
            });

            TestKit.Run("rgb24: Awaiting phases forbid a repeated Prepare and never create a second in-flight frame", () =>
            {
                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                var flow = new TransactionFlow(pool, new FakeRgb24Transport(), new InlineSyncContext());

                string error;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                    "prepare: " + error);

                // AwaitingEOF：第二次 Prepare 是 invariant failure。
                string firstError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedWrongPhase,
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex + 1, out firstError),
                    "repeated prepare while AwaitingEOF");
                TestKit.Check(
                    firstError != null && firstError.StartsWith("rgb24-transaction-frame-in-flight", StringComparison.Ordinal),
                    "awaiting-eof error code: " + firstError);
                CheckPhase(flow.Transaction, Rgb24TransactionPhase.AwaitingEOF, "phase after rejected prepare");

                OwnedRgb24Frame frame;
                string eofError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryCompleteEof(
                        DefaultGeneration, DefaultFrameIndex, out frame, out eofError),
                    "eof: " + eofError);

                // AwaitingDelivery：第二次 Prepare 同样是 invariant failure。
                string secondError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedWrongPhase,
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex + 2, out secondError),
                    "repeated prepare while AwaitingDelivery");
                TestKit.Check(
                    secondError != null && secondError.StartsWith("rgb24-transaction-frame-in-flight", StringComparison.Ordinal),
                    "awaiting-delivery error code: " + secondError);
                CheckPhase(flow.Transaction, Rgb24TransactionPhase.AwaitingDelivery, "phase after rejected prepare");

                TestKit.CheckEqual(1L, pool.AcquireCount, "exactly one acquire must ever happen");
                TestKit.Check(pool.HasOutstandingLease, "the single outstanding lease must be preserved");
                TestKit.CheckEqual(frame.TokenId, pool.OutstandingLease.Frame.TokenId,
                    "the outstanding lease must still be the original frame");
            });
        }

        // ==================================================================== EOF validation

        private static void EofTests()
        {
            TestKit.Run("rgb24: stale generation EOF is rejected and the phase stays AwaitingEOF", () =>
            {
                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                var flow = new TransactionFlow(pool, new FakeRgb24Transport(), new InlineSyncContext());

                string error;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                    "prepare: " + error);

                OwnedRgb24Frame frame;
                string eofError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedStaleGeneration,
                    flow.Transaction.TryCompleteEof(
                        DefaultGeneration + 1, DefaultFrameIndex, out frame, out eofError),
                    "stale generation eof");
                TestKit.CheckEqual("rgb24-transaction-stale-generation-eof", eofError, "error code");
                TestKit.CheckEqual(null, frame, "no frame may be produced for a stale EOF");
                CheckPhase(flow.Transaction, Rgb24TransactionPhase.AwaitingEOF, "phase must not advance");
                TestKit.Check(pool.HasOutstandingLease, "lease must be preserved for the still-pending frame");
            });

            TestKit.Run("rgb24: wrong frame EOF is rejected and the phase stays AwaitingEOF", () =>
            {
                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                var flow = new TransactionFlow(pool, new FakeRgb24Transport(), new InlineSyncContext());

                string error;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                    "prepare: " + error);

                OwnedRgb24Frame frame;
                string eofError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedWrongFrame,
                    flow.Transaction.TryCompleteEof(
                        DefaultGeneration, DefaultFrameIndex + 1, out frame, out eofError),
                    "wrong frame eof");
                TestKit.CheckEqual("rgb24-transaction-wrong-frame-eof", eofError, "error code");
                TestKit.CheckEqual(null, frame, "no frame may be produced for a wrong-frame EOF");
                CheckPhase(flow.Transaction, Rgb24TransactionPhase.AwaitingEOF, "phase must not advance");
            });
        }

        // ============================================================ delivery envelope checks

        private static void DeliveryEnvelopeTests()
        {
            TestKit.Run("rgb24: delivery envelope with a stale generation is rejected", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                var stale = Envelope(
                    DefaultGeneration + 1, DefaultFrameIndex, fixture.TokenId, SuccessOutcome());

                string errorCode;
                string errorDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedStaleGeneration,
                    fixture.Transaction.ConsumeEnvelope(true, stale, out errorCode, out errorDetail),
                    "stale generation envelope: " + errorCode);
                TestKit.CheckEqual("rgb24-envelope-stale-generation", errorCode, "error code");
                TestKit.CheckEqual(null, errorDetail, "error detail");
                TestKit.Check(!fixture.Pool.OutstandingLease.CompletionConsumed,
                    "a rejected envelope must not consume the completion");
                TestKit.CheckEqual(true, fixture.Pool.OutstandingLease.DeliveryPinned,
                    "a rejected envelope must not unpin the lease");
                CheckPhase(fixture.Transaction, Rgb24TransactionPhase.AwaitingDelivery, "phase must not advance");
            });

            TestKit.Run("rgb24: duplicate delivery consumption is reported as RejectedDuplicateCompletion", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                string errorCode;
                string errorDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Committed,
                    CompleteAndConsume(fixture, pump, SuccessOutcome(), out errorCode, out errorDetail),
                    "first consumption must commit: " + errorCode);

                string secondCode;
                string secondDetail;
                Rgb24TransactionOutcome second = fixture.Transaction.ConsumeEnvelope(true,
                    Envelope(DefaultGeneration, DefaultFrameIndex, fixture.TokenId, SuccessOutcome()),
                    out secondCode, out secondDetail);

                // 一次性 token：同一 (generation, frameIndex, tokenId) 的第二次消费必须被稳定识别为
                // duplicate。该判定依赖消费后保留的历史身份，因此必须先于 "_lease == null" 生效。
                TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedDuplicateCompletion, second,
                    "duplicate consumption must be reported as duplicate, got " + second + " (" + secondCode + ")");
                TestKit.CheckEqual("rgb24-envelope-duplicate-completion", secondCode, "duplicate error code");
            });

            TestKit.Run("rgb24: a repeated envelope after commit is rejected and never commits a second time", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                string errorCode;
                string errorDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Committed,
                    CompleteAndConsume(fixture, pump, SuccessOutcome(), out errorCode, out errorDetail),
                    "first consumption must commit: " + errorCode);

                // 同一个信封（已消费）再次投递：必须被拒绝，绝不产生第二次提交、第二次释放。
                string secondCode;
                string secondDetail;
                Rgb24TransactionOutcome second = fixture.Transaction.ConsumeEnvelope(true,
                    Envelope(DefaultGeneration, DefaultFrameIndex, fixture.TokenId, SuccessOutcome()),
                    out secondCode, out secondDetail);

                TestKit.Check(second != Rgb24TransactionOutcome.Committed,
                    "a repeated envelope must never commit a second time");
                TestKit.CheckEqual(Rgb24TransactionOutcome.RejectedDuplicateCompletion, second,
                    "the reachable rejection for a repeated envelope: " + secondCode);
                TestKit.CheckEqual("rgb24-envelope-duplicate-completion", secondCode, "error code");
                TestKit.CheckEqual(null, fixture.Pool.OutstandingLease, "no lease may be left outstanding");
                TestKit.CheckEqual(1L, fixture.Pool.ReleaseCount, "the buffer must be released exactly once");
                TestKit.CheckEqual(1L, fixture.Pool.AcquireCount, "no second frame may be prepared");
                CheckPhase(fixture.Transaction, Rgb24TransactionPhase.Idle, "phase must stay Idle");
            });

            TestKit.Run("rgb24: delivery envelope with a wrong absolute frame index is rejected", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                var wrongFrame = Envelope(
                    DefaultGeneration, DefaultFrameIndex + 1, fixture.TokenId, SuccessOutcome());

                string errorCode;
                string errorDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedWrongFrame,
                    fixture.Transaction.ConsumeEnvelope(true, wrongFrame, out errorCode, out errorDetail),
                    "wrong frame envelope");
                TestKit.CheckEqual("rgb24-envelope-wrong-frame", errorCode, "error code");
                CheckPhase(fixture.Transaction, Rgb24TransactionPhase.AwaitingDelivery, "phase must not advance");
                TestKit.CheckEqual(0L, fixture.Pool.ReleaseCount, "buffer must not be released");
            });

            TestKit.Run("rgb24: delivery envelope with a mismatched token is rejected", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                var wrongToken = Envelope(
                    DefaultGeneration, DefaultFrameIndex, fixture.TokenId + 1000, SuccessOutcome());

                string errorCode;
                string errorDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedTokenMismatch,
                    fixture.Transaction.ConsumeEnvelope(true, wrongToken, out errorCode, out errorDetail),
                    "token mismatch envelope");
                TestKit.CheckEqual("rgb24-envelope-token-mismatch", errorCode, "error code");
                TestKit.CheckEqual(true, fixture.Pool.OutstandingLease.DeliveryPinned,
                    "a mismatched token must not unpin the lease");
                CheckPhase(fixture.Transaction, Rgb24TransactionPhase.AwaitingDelivery, "phase must not advance");
            });

            TestKit.Run("rgb24: a successful delivery commits exactly once for that frame", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                string errorCode;
                string errorDetail;
                Rgb24TransactionOutcome first =
                    CompleteAndConsume(fixture, pump, SuccessOutcome(1), out errorCode, out errorDetail);

                TestKit.CheckEqual(Rgb24TransactionOutcome.Committed, first, "outcome: " + errorCode);
                TestKit.CheckEqual(null, errorCode, "committed frame must not report an error code");
                CheckPhase(fixture.Transaction, Rgb24TransactionPhase.Idle, "phase after commit");
                TestKit.Check(!fixture.Transaction.HasUnconvergedDelivery, "delivery must be converged");
                TestKit.Check(!fixture.Transaction.HasResidualOwnership, "no residual ownership after commit");
                TestKit.CheckEqual(1L, fixture.Pool.ReleaseCount, "exactly one release");
                TestKit.CheckEqual(1L, fixture.Pool.AcquireCount, "exactly one acquire for the committed frame");

                // 同一帧不可能再被消费一次。
                string secondCode;
                string secondDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedDuplicateCompletion,
                    fixture.Transaction.ConsumeEnvelope(true,
                        Envelope(DefaultGeneration, DefaultFrameIndex, fixture.TokenId, SuccessOutcome()),
                        out secondCode, out secondDetail),
                    "a committed frame cannot be consumed again");
                TestKit.CheckEqual("rgb24-envelope-duplicate-completion", secondCode, "error code");
            });

            TestKit.Run("rgb24: a failed write outcome reports Failed and never commits", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                var failure = Rgb24FrameWriteOutcome.Failure("pipe-poisoned", "stdin closed");

                string errorCode;
                string errorDetail;
                Rgb24TransactionOutcome outcome =
                    CompleteAndConsume(fixture, pump, failure, out errorCode, out errorDetail);

                TestKit.CheckEqual(Rgb24TransactionOutcome.Failed, outcome, "a failed write must not commit");
                TestKit.Check(outcome != Rgb24TransactionOutcome.Committed, "Failed must never be Committed");
                TestKit.CheckEqual("pipe-poisoned", errorCode, "failure error code must be propagated");
                TestKit.CheckEqual("stdin closed", errorDetail, "failure detail must be propagated");
                CheckPhase(fixture.Transaction, Rgb24TransactionPhase.Idle, "phase after failure");

                // 失败也必须释放 ownership（Completion 已结束，后台不再读取缓冲）。
                TestKit.CheckEqual(1L, fixture.Pool.ReleaseCount, "a failed delivery must release the lease once");
                TestKit.Check(!fixture.Pool.HasOutstandingLease, "a failed delivery must still release the lease");
                TestKit.Check(!fixture.Transaction.HasResidualOwnership, "no residual ownership after failure");
                TestKit.Check(!fixture.Transaction.HasUnconvergedDelivery, "the failed delivery must be converged");
            });
        }

        // ============================================================ transport status handling

        private static void TransportStatusTests()
        {
            TestKit.Run("rgb24: a busy transport is an invariant failure that keeps the lease outstanding", () =>
            {
                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                var flow = new TransactionFlow(pool, new FakeRgb24Transport(), new InlineSyncContext());

                string error;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                    "prepare: " + error);

                OwnedRgb24Frame frame;
                string eofError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryCompleteEof(
                        DefaultGeneration, DefaultFrameIndex, out frame, out eofError),
                    "eof: " + eofError);

                flow.Transport.Status = Rgb24TransportStatus.Busy;

                string deliveryError;
                string deliveryDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedBusy,
                    flow.Transaction.TryBeginDelivery(out deliveryError, out deliveryDetail),
                    "busy transport must map to RejectedBusy");
                TestKit.CheckEqual("rgb24-transport-busy", deliveryError, "error code");
                TestKit.CheckEqual("another frame is already in flight", deliveryDetail, "error detail");

                CheckPhase(flow.Transaction, Rgb24TransactionPhase.AwaitingDelivery, "phase must stay AwaitingDelivery");
                TestKit.Check(!flow.Transaction.HasUnconvergedDelivery, "nothing was accepted");
                TestKit.Check(pool.HasOutstandingLease, "the lease must stay outstanding");
                TestKit.CheckEqual(false, pool.OutstandingLease.DeliveryPinned,
                    "a busy rejection must not pin the lease");

                // busy 不释放：调用方必须继续观察该帧，而不是把它当作已归还。
                string releaseError;
                TestKit.Check(pool.TryRelease(pool.OutstandingLease, out releaseError),
                    "the lease is not pinned, so it stays releasable: " + releaseError);
            });

            TestKit.Run("rgb24: an immediately rejected transport does not pin the lease", () =>
            {
                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                var flow = new TransactionFlow(pool, new FakeRgb24Transport(), new InlineSyncContext());

                string error;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                    "prepare: " + error);

                OwnedRgb24Frame frame;
                string eofError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    flow.Transaction.TryCompleteEof(
                        DefaultGeneration, DefaultFrameIndex, out frame, out eofError),
                    "eof: " + eofError);

                flow.Transport.Status = Rgb24TransportStatus.Rejected;
                flow.Transport.RejectionCode = "not-running";
                flow.Transport.RejectionDetail = "the session is not running";

                string deliveryError;
                string deliveryDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedTransportRejected,
                    flow.Transaction.TryBeginDelivery(out deliveryError, out deliveryDetail),
                    "rejected transport");
                TestKit.CheckEqual("not-running", deliveryError, "rejection error code must be propagated");
                TestKit.CheckEqual("the session is not running", deliveryDetail, "rejection detail");

                TestKit.Check(!flow.Transaction.HasUnconvergedDelivery,
                    "an immediately rejected write must not create delivery ownership");
                TestKit.CheckEqual(false, pool.OutstandingLease.DeliveryPinned,
                    "an immediately rejected write must not pin the lease");

                string releaseError;
                TestKit.Check(pool.TryRelease(pool.OutstandingLease, out releaseError),
                    "the lease must be releasable right away: " + releaseError);
                TestKit.Check(pool.ReleaseCount == 1L, "exactly one release");
                TestKit.Check(!pool.HasOutstandingLease, "no outstanding lease after release");
            });
        }

        // ==================================================================== layout / long length

        private static void LayoutTests()
        {
            TestKit.Run("rgb24: a frame larger than int.MaxValue bytes keeps an exact long length and is segmented", () =>
            {
                Rgb24FrameLayout layout;
                string error;
                TestKit.Check(Rgb24FrameLayout.TryCreate(40000, 40000, out layout, out error),
                    "40000x40000 must be planned, not rejected: " + error);

                TestKit.CheckEqual(4800000000L, layout.ByteLength, "exact long byte length");
                TestKit.CheckEqual(40000, layout.Width, "width must not be clamped");
                TestKit.CheckEqual(40000, layout.Height, "height must not be clamped");
                TestKit.CheckEqual(120000L, layout.RowByteLength, "row byte length");
                TestKit.Check(layout.SegmentCount > 1, "a 4.8e9-byte frame must be expressed as multiple segments");
                TestKit.Check(layout.SegmentCount <= int.MaxValue, "segment count must stay representable as int");

                long expectedCount = layout.ByteLength / layout.SegmentByteSize;
                if (layout.ByteLength % layout.SegmentByteSize != 0)
                    expectedCount++;
                TestKit.CheckEqual(expectedCount, (long)layout.SegmentCount, "segment count must cover the whole frame");

                // 分段规划必须无缝且精确覆盖整个帧长度。
                long total = 0;
                for (int i = 0; i < layout.SegmentCount; i++)
                {
                    TestKit.CheckEqual(total, layout.SegmentOffset(i), "segment " + i + " offset");
                    TestKit.Check(layout.SegmentLength(i) > 0, "segment " + i + " must be non-empty");
                    total += layout.SegmentLength(i);
                }
                TestKit.CheckEqual(layout.ByteLength, total, "segments must exactly cover the frame");

                // 帧尾最后一个字节必须落在最后一段内。
                int lastSegment;
                int lastOffset;
                TestKit.Check(layout.TryLocate(layout.ByteLength - 1, out lastSegment, out lastOffset),
                    "the last byte must be locatable");
                TestKit.CheckEqual(layout.SegmentCount - 1, lastSegment, "the last byte lives in the last segment");
            });

            TestKit.Run("rgb24: overflowing geometry fails closed with rgb24-length-overflow and no clamped layout", () =>
            {
                Rgb24FrameLayout layout;
                string error;
                TestKit.Check(!Rgb24FrameLayout.TryCreate(int.MaxValue, int.MaxValue, out layout, out error),
                    "int.MaxValue x int.MaxValue must not produce a layout");
                TestKit.CheckEqual("rgb24-length-overflow", error, "error code");
                TestKit.CheckEqual(null, layout, "no layout may be produced on overflow");
            });
        }

        // ============================================================ segmented write ordering

        private static void SegmentWriteTests()
        {
            TestKit.Run("rgb24: delivery bytes land at the exact offsets across segment boundaries", () =>
            {
                Rgb24FrameLayout layout;
                string layoutError;
                TestKit.Check(Rgb24FrameLayout.TryCreate(2, 2, 8, out layout, out layoutError),
                    "explicit segment layout: " + layoutError);

                TestKit.CheckEqual(12L, layout.ByteLength, "byte length");
                TestKit.CheckEqual(2, layout.SegmentCount, "segment count");
                TestKit.CheckEqual(8, layout.SegmentLength(0), "first segment length");
                TestKit.CheckEqual(4, layout.SegmentLength(1), "last segment length");

                var pool = new Rgb24FrameBufferPool(layout);
                Rgb24FrameLease lease;
                string acquireError;
                TestKit.Check(pool.TryAcquire(1, 1, out lease, out acquireError), "acquire: " + acquireError);

                OwnedRgb24Frame frame = lease.Frame;
                var pattern = new byte[12];
                for (int i = 0; i < pattern.Length; i++)
                    pattern[i] = (byte)(0x10 + i);

                // 写满整帧：必然跨越 8 字节处的段边界。
                frame.WriteDeliveryBytes(0, pattern, 0, pattern.Length);

                for (int i = 0; i < pattern.Length; i++)
                {
                    TestKit.CheckEqual(pattern[i], ReadDeliveryByte(frame, i),
                        "delivery byte at offset " + i);
                }

                TestKit.CheckEqual(pattern[7], frame.SegmentBuffer(0)[7], "last byte of segment 0");
                TestKit.CheckEqual(pattern[8], frame.SegmentBuffer(1)[0], "first byte of segment 1");

                // 单个跨界的区间写入同样必须精确落位。
                frame.WriteDeliveryBytes(0, pattern, 0, pattern.Length);
                var partial = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
                frame.WriteDeliveryBytes(6, partial, 0, 4);
                TestKit.CheckEqual((byte)0xAA, ReadDeliveryByte(frame, 6), "cross-boundary byte 6");
                TestKit.CheckEqual((byte)0xBB, ReadDeliveryByte(frame, 7), "cross-boundary byte 7");
                TestKit.CheckEqual((byte)0xCC, ReadDeliveryByte(frame, 8), "cross-boundary byte 8");
                TestKit.CheckEqual((byte)0xDD, ReadDeliveryByte(frame, 9), "cross-boundary byte 9");

                // 越界写入必须 fail-closed，而不是静默截断。
                bool threw = false;
                try
                {
                    frame.WriteDeliveryBytes(layout.ByteLength, partial, 0, 1);
                }
                catch (ArgumentOutOfRangeException)
                {
                    threw = true;
                }
                TestKit.Check(threw, "a write past the end of the frame must throw");
            });

            TestKit.Run("rgb24: a row spanning more than one segment stays contiguous and correct", () =>
            {
                // rowByteLength = 17 * 3 = 51，段大小 64 刻意不是行长的整数倍，
                // 因此每一行都会跨越至少一个段边界。
                Rgb24FrameLayout layout;
                string layoutError;
                TestKit.Check(Rgb24FrameLayout.TryCreate(17, 3, 64, out layout, out layoutError),
                    "explicit layout: " + layoutError);

                TestKit.CheckEqual(51L, layout.RowByteLength, "row byte length");
                TestKit.CheckEqual(153L, layout.ByteLength, "frame byte length");
                TestKit.CheckEqual(3, layout.SegmentCount, "segment count");
                TestKit.Check(64 % layout.RowByteLength != 0,
                    "the test must use a segment size that is not a multiple of the row length");

                var pool = new Rgb24FrameBufferPool(layout);
                Rgb24FrameLease lease;
                string acquireError;
                TestKit.Check(pool.TryAcquire(1, 1, out lease, out acquireError), "acquire: " + acquireError);
                OwnedRgb24Frame frame = lease.Frame;

                // staging 按行紧密排列；每一行用独立可区分的字节填充。
                var staging = new byte[(int)layout.ByteLength];
                for (int row = 0; row < 3; row++)
                {
                    for (int i = 0; i < 51; i++)
                        staging[row * 51 + i] = (byte)(0xC0 + row * 0x10 + i);
                }

                for (int row = 0; row < 3; row++)
                    frame.CopyStagedSourceRowToDelivery(row, staging, row * 51);

                for (int row = 0; row < 3; row++)
                {
                    int deliveryRow = Rgb24RowOrderPolicy.MapSourceRowToDeliveryRow(row, 3);
                    long rowOffset = Rgb24RowOrderPolicy.DeliveryRowByteOffset(deliveryRow, 17);

                    for (int i = 0; i < 51; i++)
                    {
                        TestKit.CheckEqual((byte)(0xC0 + row * 0x10 + i), ReadDeliveryByte(frame, rowOffset + i),
                            "source row " + row + " byte " + i + " (delivery row " + deliveryRow + ")");
                    }
                }

                // 段边界与行边界无关：至少要有一整行真正跨越段边界。
                // 交付行 1 = 字节 51..101，段 0 只覆盖 0..63，因此该行必然跨越段 0 与段 1。
                long spanningRowOffset = Rgb24RowOrderPolicy.DeliveryRowByteOffset(1, 17);
                TestKit.CheckEqual(51L, spanningRowOffset, "delivery row 1 offset");

                int firstSegment;
                int firstOffset;
                TestKit.Check(layout.TryLocate(spanningRowOffset, out firstSegment, out firstOffset),
                    "the first byte of the spanning delivery row must be locatable");

                int lastSegment;
                int lastOffset;
                TestKit.Check(layout.TryLocate(spanningRowOffset + 50, out lastSegment, out lastOffset),
                    "the last byte of the spanning delivery row must be locatable");

                TestKit.CheckEqual(0, firstSegment, "the spanning row must start in segment 0");
                TestKit.CheckEqual(1, lastSegment, "the spanning row must end in segment 1");
                TestKit.Check(firstSegment != lastSegment,
                    "the delivery row must span more than one segment (segments " +
                    firstSegment + ".." + lastSegment + ")");
            });
        }

        // ==================================================================== lease ownership

        private static void LeaseOwnershipTests()
        {
            TestKit.Run("rgb24: a foreign lease cannot be released and a released lease cannot be released twice", () =>
            {
                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                var other = new Rgb24FrameBufferPool(DefaultLayout());

                Rgb24FrameLease lease;
                string acquireError;
                TestKit.Check(pool.TryAcquire(1, 1, out lease, out acquireError), "acquire: " + acquireError);

                Rgb24FrameLease foreign;
                string foreignAcquireError;
                TestKit.Check(other.TryAcquire(1, 1, out foreign, out foreignAcquireError),
                    "foreign acquire: " + foreignAcquireError);

                string foreignError;
                TestKit.Check(!pool.TryRelease(foreign, out foreignError),
                    "a foreign lease must never be released");
                TestKit.CheckEqual("rgb24-lease-foreign", foreignError, "foreign error code");
                TestKit.Check(pool.HasOutstandingLease, "the real lease must stay outstanding");

                string unknownError;
                TestKit.Check(!pool.TryRelease(null, out unknownError), "a null lease must be rejected");
                TestKit.CheckEqual("rgb24-lease-null", unknownError, "null error code");

                string releaseError;
                TestKit.Check(pool.TryRelease(lease, out releaseError), "first release: " + releaseError);
                TestKit.Check(lease.Released, "the lease must be marked released");

                string secondError;
                TestKit.Check(!pool.TryRelease(lease, out secondError), "second release must fail");
                TestKit.CheckEqual("rgb24-lease-foreign", secondError, "second release error code");
                TestKit.CheckEqual(1L, pool.ReleaseCount, "exactly one release may be counted");
                TestKit.CheckEqual(0L, other.AcquireCount - 1L, "the foreign pool is untouched");
            });

            TestKit.Run("rgb24: no buffer reuse before Completion and exact segment reuse afterwards", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                byte[] firstSegmentBuffer = fixture.Frame.SegmentBuffer(0);
                long allocatedAfterFirstAcquire = fixture.Pool.AllocatedSegmentCount;

                // 交付已被接受：后台写入线程正在直接读取这些数组。
                string pinnedError;
                TestKit.Check(!fixture.Pool.TryRelease(fixture.Pool.OutstandingLease, out pinnedError),
                    "a pinned lease must not be released");
                TestKit.CheckEqual("rgb24-lease-pinned", pinnedError, "pinned error code");
                TestKit.Check(fixture.Pool.HasOutstandingLease, "the pinned lease must stay outstanding");
                TestKit.CheckEqual(0L, fixture.Pool.ReleaseCount, "nothing may be released while pinned");

                string errorCode;
                string errorDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Committed,
                    CompleteAndConsume(fixture, pump, SuccessOutcome(), out errorCode, out errorDetail),
                    "consume: " + errorCode);

                TestKit.Check(!fixture.Pool.HasOutstandingLease,
                    "the buffer must become releasable once the completion is consumed");
                TestKit.CheckEqual(1L, fixture.Pool.ReleaseCount, "exactly one release");

                // 第二次 acquire 必须是**同一批段数组**：会话内复用，不重新分配。
                Rgb24FrameLease second;
                string secondAcquireError;
                TestKit.Check(fixture.Pool.TryAcquire(DefaultGeneration, DefaultFrameIndex + 1,
                    out second, out secondAcquireError), "second acquire: " + secondAcquireError);

                TestKit.Check(ReferenceEquals(firstSegmentBuffer, second.Frame.SegmentBuffer(0)),
                    "the second acquire must reuse the same segment arrays");
                TestKit.CheckEqual(allocatedAfterFirstAcquire, fixture.Pool.AllocatedSegmentCount,
                    "a reused session must not allocate additional segments");
                TestKit.CheckEqual(2L, fixture.Pool.AcquireCount, "two acquires");
                TestKit.Check(second.Frame.TokenId != fixture.TokenId,
                    "each acquire must mint a fresh one-shot token");
            });
        }

        // ============================================================ L2 adapter seam

        /// <summary>
        /// 薄适配器 <see cref="FfmpegRgb24FrameTransport"/> 的编译期 + 运行期接缝：
        /// 分段 RGB24 lease 映射为 L2 的帧分段，L2 的拒绝原样映射为事务可消费的拒绝。
        /// 未启动的 pipeline 会确定性返回 "not-running"，因此这里不需要真实 FFmpeg 进程。
        /// </summary>
        private static void AdapterSeamTests()
        {
            TestKit.Run("rgb24: the ffmpeg transport adapter maps the frame segments and surfaces L2 rejections", () =>
            {
                bool threw = false;
                try
                {
                    new FfmpegRgb24FrameTransport(null);
                }
                catch (ArgumentNullException)
                {
                    threw = true;
                }
                TestKit.Check(threw, "a null pipeline must be rejected at construction");

                var pipeline = new FfmpegVideoPipeline(new FfmpegVideoPipelineOptions
                {
                    Settings = new FfmpegVideoSettings { Width = DefaultWidth, Height = DefaultHeight, Fps = 30 },
                });
                TestKit.CheckEqual(FfmpegVideoPipelineState.NotStarted, pipeline.State, "the fixture pipeline must not be started");

                var adapter = new FfmpegRgb24FrameTransport(pipeline);

                // 空帧必须先被适配器自己拒绝，绝不进入 L2。
                Rgb24TransportAttempt nullFrame = adapter.TryBeginWrite(null);
                TestKit.CheckEqual(Rgb24TransportStatus.Rejected, nullFrame.Status, "null frame status");
                TestKit.CheckEqual("rgb24-frame-null", nullFrame.ErrorCode, "null frame error code");

                var pool = new Rgb24FrameBufferPool(DefaultLayout());
                Rgb24FrameLease lease;
                string acquireError;
                TestKit.Check(pool.TryAcquire(DefaultGeneration, DefaultFrameIndex, out lease, out acquireError),
                    "acquire: " + acquireError);

                // 未启动的 pipeline 立即拒绝：不排队、不返回 completion。
                Rgb24TransportAttempt attempt = adapter.TryBeginWrite(lease.Frame);
                TestKit.CheckEqual(Rgb24TransportStatus.Rejected, attempt.Status, "rejected status");
                TestKit.CheckEqual("not-running", attempt.ErrorCode, "L2 error code must be propagated verbatim");
                TestKit.CheckEqual("state=NotStarted", attempt.ErrorDetail, "L2 detail must be propagated verbatim");
                TestKit.CheckEqual(null, attempt.Completion, "a rejected attempt must carry no completion");

                TestKit.Check(ReferenceEquals(pipeline, adapter.Pipeline), "the adapter must expose its pipeline");

                // 同一个拒绝接进事务（使用独立的池：全链路最多一帧在途）：
                // 必须走 RejectedTransportRejected 且不 pin。
                var transactionPool = new Rgb24FrameBufferPool(DefaultLayout());
                var transaction = new Rgb24FrameTransaction(
                    transactionPool, adapter, CaptureBridge(new InlineSyncContext()));

                string error;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                    "prepare: " + error);

                OwnedRgb24Frame frame;
                string eofError;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.Accepted,
                    transaction.TryCompleteEof(
                        DefaultGeneration, DefaultFrameIndex, out frame, out eofError),
                    "eof: " + eofError);

                string deliveryError;
                string deliveryDetail;
                TestKit.CheckEqual(
                    Rgb24TransactionOutcome.RejectedTransportRejected,
                    transaction.TryBeginDelivery(out deliveryError, out deliveryDetail),
                    "a real L2 rejection must map to RejectedTransportRejected");
                TestKit.CheckEqual("not-running", deliveryError, "delivery error code");
                TestKit.Check(!transaction.HasUnconvergedDelivery, "nothing was accepted");
                TestKit.CheckEqual(false, transactionPool.OutstandingLease.DeliveryPinned,
                    "an immediately rejected L2 write must not pin the lease");

                string releaseError;
                TestKit.Check(transactionPool.TryRelease(transactionPool.OutstandingLease, out releaseError),
                    "the lease must stay releasable: " + releaseError);
            });
        }

        // ==================================================================== row order policy

        private static void RowOrderTests()
        {
            TestKit.Run("rgb24: row order maps the Unity bottom row to the last delivery row and flips the bytes", () =>
            {
                TestKit.CheckEqual(2, Rgb24RowOrderPolicy.MapSourceRowToDeliveryRow(0, 3),
                    "Unity source row 0 (bottom) maps to delivery row height-1");
                TestKit.CheckEqual(0, Rgb24RowOrderPolicy.MapSourceRowToDeliveryRow(2, 3),
                    "the Unity top row maps to delivery row 0");
                TestKit.CheckEqual(1, Rgb24RowOrderPolicy.MapSourceRowToDeliveryRow(1, 3),
                    "the middle row is symmetric");
                TestKit.CheckEqual(0L, Rgb24RowOrderPolicy.DeliveryRowByteOffset(0, 2), "delivery row 0 offset");
                TestKit.CheckEqual(6L, Rgb24RowOrderPolicy.DeliveryRowByteOffset(1, 2), "delivery row 1 offset");
                TestKit.CheckEqual(Rgb24RowOrder.BottomUpSourceToTopFirst, Rgb24RowOrderPolicy.Delivery,
                    "the frozen session delivery order");
                TestKit.CheckNotEmpty(Rgb24RowOrderPolicy.DeliveryLabel, "delivery label");

                Rgb24FrameLayout layout;
                string layoutError;
                TestKit.Check(Rgb24FrameLayout.TryCreate(3, 3, out layout, out layoutError),
                    "layout: " + layoutError);
                TestKit.CheckEqual(9L, layout.RowByteLength, "row byte length");

                var pool = new Rgb24FrameBufferPool(layout);
                Rgb24FrameLease lease;
                string acquireError;
                TestKit.Check(pool.TryAcquire(1, 1, out lease, out acquireError), "acquire: " + acquireError);
                OwnedRgb24Frame frame = lease.Frame;

                var staging = new byte[27];
                for (int row = 0; row < 3; row++)
                {
                    for (int i = 0; i < 9; i++)
                        staging[row * 9 + i] = (byte)(0xA0 + row * 0x10 + i);
                }

                for (int row = 0; row < 3; row++)
                    frame.CopyStagedSourceRowToDelivery(row, staging, row * 9);

                // 源行 0（Unity 底行）必须落在交付缓冲的最后一行。
                long bottomOffset = Rgb24RowOrderPolicy.DeliveryRowByteOffset(2, 3);
                TestKit.CheckEqual((byte)0xA0, ReadDeliveryByte(frame, bottomOffset),
                    "Unity bottom row must be written at the last delivery row");
                TestKit.CheckEqual((byte)0xA8, ReadDeliveryByte(frame, bottomOffset + 8),
                    "the whole bottom row must be contiguous");

                long topOffset = Rgb24RowOrderPolicy.DeliveryRowByteOffset(0, 3);
                TestKit.CheckEqual((byte)0xC0, ReadDeliveryByte(frame, topOffset),
                    "Unity top row must be written at delivery row 0");
                TestKit.CheckEqual((byte)0xC8, ReadDeliveryByte(frame, topOffset + 8),
                    "the whole top row must be contiguous");
            });
        }

        // ============================================================ main-thread bridge

        private static void MainThreadBridgeTests()
        {
            TestKit.Run("rgb24: the main-thread bridge captures the context and fails closed when it is missing", () =>
            {
                SynchronizationContext original = SynchronizationContext.Current;
                try
                {
                    // 没有可用上下文时必须 fail-closed（MP4 启动不能继续）。
                    SynchronizationContext.SetSynchronizationContext(null);

                    Rgb24MainThreadBridge missing;
                    string missingError;
                    TestKit.Check(!Rgb24MainThreadBridge.TryCapture(out missing, out missingError),
                        "capture without a SynchronizationContext must fail");
                    TestKit.CheckEqual(null, missing, "no bridge may be produced");
                    TestKit.CheckEqual("rgb24-main-thread-context-unavailable", missingError, "error code");

                    // 有可用上下文时必须捕获它以及当前主线程身份。
                    var inline = new InlineSyncContext();
                    SynchronizationContext.SetSynchronizationContext(inline);

                    Rgb24MainThreadBridge captured;
                    string captureError;
                    TestKit.Check(Rgb24MainThreadBridge.TryCapture(out captured, out captureError),
                        "capture with a context: " + captureError);
                    TestKit.CheckEqual(null, captureError, "capture error");
                    TestKit.Check(captured != null && captured.HasContext, "the captured bridge must have a context");
                    TestKit.Check(captured.IsMainThread, "capture must record the calling thread as the main thread");
                    TestKit.CheckEqual(Thread.CurrentThread.ManagedThreadId, captured.MainThreadId,
                        "captured main thread id");

                    string nullError;
                    TestKit.Check(!captured.TryPost(null, envelope => { }, out nullError),
                        "posting a null envelope must be rejected");
                    TestKit.CheckEqual("rgb24-bridge-envelope-null", nullError, "null envelope error code");
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                }
            });

            TestKit.Run("rgb24: the delivery path posts the completion envelope through the captured context", () =>
            {
                SynchronizationContext original = SynchronizationContext.Current;
                var inline = new InlineSyncContext();
                try
                {
                    SynchronizationContext.SetSynchronizationContext(inline);
                    Rgb24MainThreadBridge bridge = CaptureBridge(inline);

                    var pool = new Rgb24FrameBufferPool(DefaultLayout());
                    var transport = new FakeRgb24Transport();
                    var transaction = new Rgb24FrameTransaction(pool, transport, bridge);

                    string error;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                        "prepare: " + error);

                    OwnedRgb24Frame frame;
                    string eofError;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryCompleteEof(
                            DefaultGeneration, DefaultFrameIndex, out frame, out eofError),
                        "eof: " + eofError);

                    string deliveryError;
                    string deliveryDetail;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryBeginDelivery(out deliveryError, out deliveryDetail),
                        "begin delivery: " + deliveryError + " " + deliveryDetail);

                    TestKit.CheckEqual(0, inline.PostCount, "nothing may be posted before the write finishes");

                    transport.Complete(SuccessOutcome());

                    TestKit.CheckEqual(1, inline.PostCount,
                        "the completion envelope must be posted exactly once through the captured context");

                    Rgb24DeliveryEnvelope envelope;
                    string bridgeError;
                    TestKit.Check(transaction.TryTakePendingEnvelope(out envelope, out bridgeError),
                        "the posted envelope must be retrievable: " + bridgeError);
                    TestKit.Check(envelope != null, "envelope must not be null");
                    TestKit.CheckEqual(DefaultGeneration, envelope.Generation, "envelope generation");
                    TestKit.CheckEqual(DefaultFrameIndex, envelope.AbsoluteFrameIndex, "envelope frame index");
                    TestKit.CheckEqual(frame.TokenId, envelope.TokenId, "envelope token");
                    TestKit.Check(envelope.Success, "envelope must carry the success outcome");

                    string errorCode;
                    string errorDetail;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Committed,
                        transaction.ConsumeEnvelope(true, envelope, out errorCode, out errorDetail),
                        "consume: " + errorCode);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                }
            });

            TestKit.Run("rgb24: a bridge posting from another thread never runs the callback inline", () =>
            {
                SynchronizationContext original = SynchronizationContext.Current;
                var pumped = new PumpSyncContext();
                try
                {
                    SynchronizationContext.SetSynchronizationContext(pumped);
                    Rgb24MainThreadBridge bridge = CaptureBridge(pumped);
                    int mainThreadId = bridge.MainThreadId;

                    var pool = new Rgb24FrameBufferPool(DefaultLayout());
                    var transport = new FakeRgb24Transport();
                    var transaction = new Rgb24FrameTransaction(pool, transport, bridge);

                    string error;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                        "prepare: " + error);

                    OwnedRgb24Frame frame;
                    string eofError;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryCompleteEof(
                            DefaultGeneration, DefaultFrameIndex, out frame, out eofError),
                        "eof: " + eofError);

                    string deliveryError;
                    string deliveryDetail;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryBeginDelivery(out deliveryError, out deliveryDetail),
                        "begin delivery: " + deliveryError + " " + deliveryDetail);

                    // 从另一线程完成写入：continuation 在该线程上运行并 Post。
                    var completer = new ManualResetEventSlim(false);
                    Task.Run(() =>
                    {
                        transport.Complete(SuccessOutcome());
                        completer.Set();
                    });

                    TestKit.Check(completer.Wait(30000), "the write completion must run");
                    TestKit.Check(pumped.WaitForHandOff(30000),
                        "the bridge must post the envelope from the completing thread");

                    TestKit.CheckEqual(1, pumped.PostCount, "exactly one envelope may be posted");

                    // Post 只是入队：主线程没有 Pump 之前回调绝不执行。
                    Rgb24DeliveryEnvelope beforePump;
                    string beforeError;
                    TestKit.Check(!transaction.TryTakePendingEnvelope(out beforePump, out beforeError),
                        "the envelope must not be delivered before the main thread is pumped");
                    TestKit.CheckEqual(null, beforePump, "no envelope may arrive without a pump");

                    pumped.Pump();

                    Rgb24DeliveryEnvelope envelope;
                    string bridgeError;
                    TestKit.Check(transaction.TryTakePendingEnvelope(out envelope, out bridgeError),
                        "the pumped envelope must be retrievable: " + bridgeError);
                    TestKit.CheckEqual(mainThreadId, pumped.CallbackThreadId,
                        "the callback must run on the captured main thread, not the posting thread");

                    string errorCode;
                    string errorDetail;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Committed,
                        transaction.ConsumeEnvelope(true, envelope, out errorCode, out errorDetail),
                        "consume: " + errorCode);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                }
            });
        }

        // ============================================================ abort / stall

        private static void AbortAndStallTests()
        {
            TestKit.Run("rgb24: TryAbort refuses while a delivery is in flight and converges afterwards", () =>
            {
                var pump = new PumpSyncContext();
                var fixture = AcceptedDelivery(pump);

                string abortError;
                TestKit.Check(!fixture.Transaction.TryAbort(out abortError),
                    "aborting with a delivery in flight must fail");
                TestKit.CheckEqual("rgb24-abort-delivery-in-flight", abortError, "error code");
                TestKit.Check(fixture.Pool.HasOutstandingLease,
                    "the buffer must not be released while the delivery is in flight");
                TestKit.CheckEqual(0L, fixture.Pool.ReleaseCount, "nothing may be released by a refused abort");
                TestKit.CheckEqual("rgb24-abort-delivery-in-flight", abortError,
                    "the refusal must name the in-flight delivery");

                fixture.Transport.Complete(SuccessOutcome());
                TestKit.Check(pump.WaitForHandOff(30000), "completion handoff");
                pump.Pump();
                TestKit.CheckEqual(1L, fixture.Pool.ReleaseCount, "cancelled completion retires once");
                TestKit.Check(!fixture.Transaction.TryTakePendingEnvelope(out _, out _), "no commit envelope after cancellation");

                TestKit.Check(!fixture.Pool.HasOutstandingLease, "the pool must have no outstanding lease");
                TestKit.Check(!fixture.Transaction.HasResidualOwnership, "no residual ownership");

                string convergedError;
                TestKit.Check(fixture.Transaction.TryAbort(out convergedError),
                    "abort must converge after the completion was consumed: " + convergedError);
                TestKit.CheckEqual(null, convergedError, "converged abort error");
                CheckPhase(fixture.Transaction, Rgb24TransactionPhase.Idle, "phase after abort");

                fixture.Transaction.Reset();
                TestKit.Check(!fixture.Pool.HasOutstandingLease, "reset must not resurrect a lease");
                CheckPhase(fixture.Transaction, Rgb24TransactionPhase.Idle, "phase after reset");
            });

            TestKit.Run("rgb24: a finished write whose envelope never arrives is reported as a stalled bridge", () =>
            {
                SynchronizationContext original = SynchronizationContext.Current;
                var dropping = new DroppingSyncContext();
                try
                {
                    SynchronizationContext.SetSynchronizationContext(dropping);
                    Rgb24MainThreadBridge bridge = CaptureBridge(dropping);

                    var pool = new Rgb24FrameBufferPool(DefaultLayout());
                    var transport = new FakeRgb24Transport();
                    var transaction = new Rgb24FrameTransaction(pool, transport, bridge);

                    string error;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryBeginFrame(DefaultGeneration, DefaultFrameIndex, out error),
                        "prepare: " + error);

                    OwnedRgb24Frame frame;
                    string eofError;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryCompleteEof(
                            DefaultGeneration, DefaultFrameIndex, out frame, out eofError),
                        "eof: " + eofError);

                    string deliveryError;
                    string deliveryDetail;
                    TestKit.CheckEqual(
                        Rgb24TransactionOutcome.Accepted,
                        transaction.TryBeginDelivery(out deliveryError, out deliveryDetail),
                        "begin delivery: " + deliveryError + " " + deliveryDetail);

                    // Task 立刻结束；该上下文静默丢弃回调，信封永远无法交接。
                    transport.Complete(SuccessOutcome());
                    TestKit.Check(dropping.WaitForHandOff(30000),
                        "the bridge must have attempted the hand-off");
                    TestKit.Check(dropping.PostCount > 0, "the bridge must have attempted the hand-off");

                    for (int tick = 0; tick < Rgb24FrameTransaction.BridgeStallTickLimit; tick++)
                    {
                        Rgb24DeliveryEnvelope envelope;
                        string bridgeError;
                        TestKit.Check(!transaction.TryTakePendingEnvelope(out envelope, out bridgeError),
                            "no envelope can ever be taken");
                        TestKit.CheckEqual(null, envelope, "envelope must stay null");
                        TestKit.CheckEqual(null, bridgeError,
                            "the bridge must not be declared stalled before the tick limit (" + tick + ")");
                    }

                    // 第 (limit + 1) 次观察突破内部桥接不变量上限。
                    Rgb24DeliveryEnvelope stalledEnvelope;
                    string stalledError;
                    TestKit.Check(!transaction.TryTakePendingEnvelope(out stalledEnvelope, out stalledError),
                        "a stalled bridge never yields an envelope");
                    TestKit.CheckEqual("rgb24-bridge-notification-stalled", stalledError, "stall error code");

                    // 停滞只报告故障，绝不偷偷复用正在被后台读取的缓冲。
                    TestKit.Check(pool.HasOutstandingLease,
                        "a stalled notification must not release the still-pinned lease");
                    TestKit.CheckEqual(true, pool.OutstandingLease.DeliveryPinned, "the lease must stay pinned");
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                }
            });
        }

        // ==================================================================== test doubles

        /// <summary>一次事务 + 它使用的池、传输与主线程桥的组合。</summary>
        private sealed class TransactionFlow
        {
            internal TransactionFlow(
                Rgb24FrameBufferPool pool, FakeRgb24Transport transport, SynchronizationContext context)
            {
                Pool = pool;
                Transport = transport;
                Transaction = new Rgb24FrameTransaction(
                    pool, transport, CaptureBridge(context));
            }

            internal Rgb24FrameBufferPool Pool { get; private set; }
            internal FakeRgb24Transport Transport { get; private set; }
            internal Rgb24FrameTransaction Transaction { get; private set; }
        }

        /// <summary>可控制的交付目标假实现：Busy / Rejected 立即返回，Accepted 由测试决定何时结束。</summary>
        private sealed class FakeRgb24Transport : IRgb24FrameTransport
        {
            private readonly TaskCompletionSource<Rgb24FrameWriteOutcome> _completion =
                new TaskCompletionSource<Rgb24FrameWriteOutcome>();

            internal Rgb24TransportStatus Status = Rgb24TransportStatus.Accepted;
            internal string RejectionCode;
            internal string RejectionDetail;

            public Rgb24TransportAttempt TryBeginWrite(OwnedRgb24Frame frame)
            {
                if (Status == Rgb24TransportStatus.Busy)
                    return Rgb24TransportAttempt.Busy("another frame is already in flight");

                if (Status == Rgb24TransportStatus.Rejected)
                    return Rgb24TransportAttempt.Rejected(
                        RejectionCode ?? "rgb24-fake-rejected", RejectionDetail);

                return Rgb24TransportAttempt.Accepted(_completion.Task);
            }

            /// <summary>由测试决定写入何时完成（以及成功还是失败）。</summary>
            internal void Complete(Rgb24FrameWriteOutcome outcome)
            {
                _completion.TrySetResult(outcome);
            }
        }

        /// <summary>在当前线程立即执行回调的上下文。</summary>
        private sealed class InlineSyncContext : SynchronizationContext
        {
            private int _postCount;

            internal int PostCount { get { return _postCount; } }

            public override void Post(SendOrPostCallback callback, object state)
            {
                _postCount++;
                callback(state);
            }
        }

        /// <summary>
        /// 只入队、必须由测试显式 Pump 才执行的上下文（模拟"回调稍后发生在主线程"）。
        /// Pump 只消费当下已经入队的回调，绝不 sleep、绝不等别的线程。
        /// </summary>
        private sealed class PumpSyncContext : SynchronizationContext
        {
            private readonly Queue<KeyValuePair<SendOrPostCallback, object>> _queue =
                new Queue<KeyValuePair<SendOrPostCallback, object>>();

            private readonly ManualResetEventSlim _posted = new ManualResetEventSlim(false);

            internal int PostCount { get; private set; }
            internal int CallbackThreadId { get; private set; }

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_queue)
                {
                    _queue.Enqueue(new KeyValuePair<SendOrPostCallback, object>(callback, state));
                    PostCount++;
                }
                _posted.Set();
            }

            /// <summary>等到后台 continuation 至少完成一次 Post（事件驱动，不靠 Sleep）。</summary>
            internal bool WaitForHandOff(int timeoutMs)
            {
                return _posted.Wait(timeoutMs);
            }

            /// <summary>在调用线程上排空队列。</summary>
            internal void Pump()
            {
                while (true)
                {
                    KeyValuePair<SendOrPostCallback, object> item;
                    lock (_queue)
                    {
                        if (_queue.Count == 0)
                            return;
                        item = _queue.Dequeue();
                    }

                    CallbackThreadId = Thread.CurrentThread.ManagedThreadId;
                    item.Key(item.Value);
                }
            }
        }

        /// <summary>静默丢弃回调的上下文：模拟"Task 已结束但交接始终不到达"。</summary>
        private sealed class DroppingSyncContext : SynchronizationContext
        {
            private readonly ManualResetEventSlim _posted = new ManualResetEventSlim(false);

            internal int PostCount { get; private set; }

            public override void Post(SendOrPostCallback callback, object state)
            {
                PostCount++;
                _posted.Set();
                // 刻意不执行 callback。
            }

            /// <summary>等到后台 continuation 真的尝试过交接。</summary>
            internal bool WaitForHandOff(int timeoutMs)
            {
                return _posted.Wait(timeoutMs);
            }
        }
    }
}
