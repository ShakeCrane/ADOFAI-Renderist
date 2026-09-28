using System;
using System.Threading;
using System.Threading.Tasks;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 交付目标的返回状态。**Busy 与 Rejected 必须分开**：
    ///   * <see cref="Busy"/> 表示"已有另一帧在途" —— 在本设计里这**不可能合法发生**，
    ///     因为全链路最多一帧在途，因此它是 scheduler invariant failure；
    ///   * <see cref="Rejected"/> 表示管道/会话本身拒绝（如 not-running / pipe-poisoned），
    ///     走既有失败收敛路径。
    /// 两种情况都**不重试、不排队、不启动第二个 frame write**。
    /// </summary>
    internal enum Rgb24TransportStatus
    {
        Accepted = 0,
        Busy = 1,
        Rejected = 2,
    }

    /// <summary>一次帧写入的终态结果（交付目标的原始结果，已脱离具体管线类型）。</summary>
    internal sealed class Rgb24FrameWriteOutcome
    {
        internal Rgb24FrameWriteOutcome(
            bool success, string errorCode, string errorDetail, long deliveredFrameCount)
        {
            Success = success;
            ErrorCode = errorCode;
            ErrorDetail = errorDetail;
            DeliveredFrameCount = deliveredFrameCount;
        }

        internal bool Success { get; private set; }
        internal string ErrorCode { get; private set; }
        internal string ErrorDetail { get; private set; }
        internal long DeliveredFrameCount { get; private set; }

        internal static Rgb24FrameWriteOutcome Failure(string errorCode, string errorDetail)
        {
            return new Rgb24FrameWriteOutcome(false, errorCode ?? "rgb24-delivery-failed", errorDetail, 0);
        }
    }

    /// <summary>交付尝试：状态 + 失败原因 + （被接受时的）完成通知。</summary>
    internal sealed class Rgb24TransportAttempt
    {
        private Rgb24TransportAttempt(
            Rgb24TransportStatus status, string errorCode, string errorDetail,
            Task<Rgb24FrameWriteOutcome> completion)
        {
            Status = status;
            ErrorCode = errorCode;
            ErrorDetail = errorDetail;
            Completion = completion;
        }

        internal Rgb24TransportStatus Status { get; private set; }
        internal string ErrorCode { get; private set; }
        internal string ErrorDetail { get; private set; }

        /// <summary>被接受时：本帧完整写入（或失败）的完成通知。绝不阻塞主线程。</summary>
        internal Task<Rgb24FrameWriteOutcome> Completion { get; private set; }

        internal static Rgb24TransportAttempt Accepted(Task<Rgb24FrameWriteOutcome> completion)
        {
            return new Rgb24TransportAttempt(Rgb24TransportStatus.Accepted, null, null, completion);
        }

        internal static Rgb24TransportAttempt Busy(string detail)
        {
            return new Rgb24TransportAttempt(Rgb24TransportStatus.Busy, "busy", detail, null);
        }

        internal static Rgb24TransportAttempt Rejected(string errorCode, string errorDetail)
        {
            return new Rgb24TransportAttempt(Rgb24TransportStatus.Rejected, errorCode, errorDetail, null);
        }
    }

    /// <summary>
    /// 交付目标的抽象缝。生产实现是 L2 <c>FfmpegVideoPipeline</c> 的薄适配器；
    /// 测试用假实现可以在没有 FFmpeg、没有 Unity 的情况下确定性覆盖全部事务分支。
    /// 本接口**不**拥有时间推进、EOF、进程或 scheduler 生命周期。
    /// </summary>
    internal interface IRgb24FrameTransport
    {
        Rgb24TransportAttempt TryBeginWrite(OwnedRgb24Frame frame);
    }

    /// <summary>
    /// 不可变的交付结果信封。它是**后台 → Unity 主线程**唯一允许跨越边界的对象：
    /// 包含不可变结果与不透明 frame/Task 身份，不含 Unity 对象或 scheduler 可变状态。
    /// </summary>
    internal sealed class Rgb24DeliveryEnvelope
    {
        internal Rgb24DeliveryEnvelope(
            long generation, long absoluteFrameIndex, long tokenId, Rgb24FrameWriteOutcome outcome,
            object frameIdentity = null, Task<Rgb24FrameWriteOutcome> completion = null)
        {
            Generation = generation;
            AbsoluteFrameIndex = absoluteFrameIndex;
            TokenId = tokenId;
            Outcome = outcome ?? Rgb24FrameWriteOutcome.Failure("rgb24-delivery-no-outcome", null);
            FrameIdentity = frameIdentity;
            Completion = completion;
        }

        internal long Generation { get; private set; }
        internal long AbsoluteFrameIndex { get; private set; }
        internal long TokenId { get; private set; }
        internal Rgb24FrameWriteOutcome Outcome { get; private set; }
        internal object FrameIdentity { get; private set; }
        internal Task<Rgb24FrameWriteOutcome> Completion { get; private set; }

        internal bool Success { get { return Outcome.Success; } }
        internal string ErrorCode { get { return Outcome.ErrorCode; } }
        internal string ErrorDetail { get { return Outcome.ErrorDetail; } }
        internal long DeliveredFrameCount { get { return Outcome.DeliveredFrameCount; } }
    }

    /// <summary>
    /// 在 MP4 session 启动时（Unity 主线程）捕获的 main-thread 投递桥。
    ///
    /// 契约：
    ///   * Unity 侧 <see cref="SynchronizationContext"/> 不可用时，MP4 启动 fail-closed；
    ///   * 后台 continuation 只允许 Post 一个不可变信封，不得调用 Unity / Harmony / GUI，
    ///     也不得直接访问 scheduler 可变状态；
    ///   * Post 失败（抛错或上下文失效）必须被主线程观察到并触发 RequestStop。
    /// </summary>
    internal sealed class Rgb24MainThreadBridge
    {
        private readonly SynchronizationContext _context;
        private readonly int _mainThreadId;

        private Rgb24MainThreadBridge(SynchronizationContext context, int mainThreadId)
        {
            _context = context;
            _mainThreadId = mainThreadId;
        }

        internal bool HasContext { get { return _context != null; } }
        internal int MainThreadId { get { return _mainThreadId; } }
        internal bool IsMainThread { get { return Thread.CurrentThread.ManagedThreadId == _mainThreadId; } }

        /// <summary>必须在 Unity 主线程调用。上下文不可用时返回 false（MP4 启动 fail-closed）。</summary>
        internal static bool TryCapture(out Rgb24MainThreadBridge bridge, out string error)
        {
            bridge = null;
            error = null;

            SynchronizationContext context;
            try
            {
                context = SynchronizationContext.Current;
            }
            catch (Exception ex)
            {
                error = "rgb24-main-thread-context-unavailable:" + ex.Message;
                return false;
            }

            if (context == null)
            {
                error = "rgb24-main-thread-context-unavailable";
                return false;
            }

            bridge = new Rgb24MainThreadBridge(context, Thread.CurrentThread.ManagedThreadId);
            return true;
        }

        /// <summary>从任意线程调用；回调在捕获到的上下文（Unity 主线程）上执行。</summary>
        internal bool TryPost(Rgb24DeliveryEnvelope envelope, Action<Rgb24DeliveryEnvelope> onMainThread,
            out string error, Action<string> onFailure = null)
        {
            error = null;
            if (envelope == null)
            {
                error = "rgb24-bridge-envelope-null";
                return false;
            }

            if (_context == null)
            {
                error = "rgb24-main-thread-context-unavailable";
                return false;
            }

            try
            {
                _context.Post(state =>
                {
                    if (!IsMainThread)
                    {
                        onFailure?.Invoke("rgb24-bridge-wrong-thread");
                        return;
                    }
                    onMainThread((Rgb24DeliveryEnvelope)state);
                }, envelope);
                return true;
            }
            catch (Exception ex)
            {
                error = "rgb24-bridge-post-failed:" + ex.Message;
                return false;
            }
        }
    }

    /// <summary>局部 lease/readback guard 的只读投影；不决定 scheduler 的下一阶段。</summary>
    internal enum Rgb24TransactionPhase
    {
        Idle = 0,

        /// <summary>本帧已 Prepare 并请求 EOF；此时**禁止**再次 Prepare。</summary>
        AwaitingEOF = 1,

        /// <summary>CPU frame 已形成；接受事实另外由本 lease 的 write identity 表达。</summary>
        AwaitingDelivery = 2,
    }

    internal enum Rgb24TransactionOutcome
    {
        Accepted = 0,
        Committed = 1,
        Failed = 2,
        RejectedNoFrameInFlight = 3,
        RejectedWrongPhase = 4,
        RejectedStaleGeneration = 5,
        RejectedWrongFrame = 6,
        RejectedDuplicateCompletion = 7,
        RejectedTokenMismatch = 8,
        RejectedBusy = 9,
        RejectedLeaseAcquireFailed = 10,
        RejectedTransportRejected = 11,
        RejectedWrongThread = 12,
        RejectedCompletionIdentity = 13,
    }

    /// <summary>
    /// L3-A 单帧 RGB24 事务（Unity-free，可被独立 net48 harness 完整覆盖）。
    ///
    /// 本类只验证 scheduler 发起的操作与局部 ownership 是否一致：
    ///   Prepare(N) → **AwaitingEOF** → EOF 形成 CPU frame → **AwaitingDelivery**
    ///   → 交付（L2 TryWriteFrame）→ Completion → Unity 主线程校验 → 允许 CommitFrame(N)
    ///
    /// 本类**不**推进时间、不拥有 EOF coroutine、不拥有 FFmpeg 进程、不 Commit：
    /// 它只在主线程校验通过后返回"可以提交一次"的决定。
    /// </summary>
    internal sealed class Rgb24FrameTransaction
    {
        internal const int BridgeStallTickLimit = 120;
        private readonly Rgb24FrameBufferPool _pool;
        private readonly IRgb24FrameTransport _transport;
        private readonly Rgb24MainThreadBridge _bridge;
        private Rgb24FrameLease _lease;
        private object _frameIdentity;
        private bool _eofCompleted;
        private bool _beginningDelivery;
        private bool _aborting;
        private DeliveryNotification _write;
        private long _generation = -1;
        private long _frameIndex = -1;
        private bool _hasConsumedCompletion;
        private long _lastConsumedGeneration;
        private long _lastConsumedFrameIndex;
        private long _lastConsumedTokenId;

        internal Rgb24FrameTransaction(Rgb24FrameBufferPool pool, IRgb24FrameTransport transport,
            Rgb24MainThreadBridge bridge)
        {
            _pool = pool ?? throw new ArgumentNullException("pool");
            _transport = transport ?? throw new ArgumentNullException("transport");
            _bridge = bridge ?? throw new ArgumentNullException("bridge");
        }

        // 只读 ownership 投影。所有调用顺序及下一 export 状态由 scheduler 决定。
        internal Rgb24TransactionPhase Phase => _lease == null ? Rgb24TransactionPhase.Idle
            : _eofCompleted ? Rgb24TransactionPhase.AwaitingDelivery : Rgb24TransactionPhase.AwaitingEOF;
        internal long Generation => _generation;
        internal long FrameIndex => _frameIndex;
        internal long LeaseTokenId => _lease == null ? -1 : _lease.Frame.TokenId;
        internal OwnedRgb24Frame CurrentFrame => _lease?.Frame;
        internal bool HasUnconvergedDelivery => _write != null || _beginningDelivery;
        internal bool HasResidualOwnership => _lease != null || _write != null || _beginningDelivery;

        internal Rgb24TransactionOutcome TryBeginFrame(long generation, long frameIndex, out string error)
        {
            error = null;
            if (!_bridge.IsMainThread)
            {
                error = "rgb24-wrong-main-thread";
                return Rgb24TransactionOutcome.RejectedWrongThread;
            }
            if (_aborting || HasResidualOwnership)
            {
                error = "rgb24-transaction-frame-in-flight-or-aborting";
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }
            if (!_pool.TryAcquire(generation, frameIndex, out _lease, out error))
                return error == "rgb24-lease-busy" ? Rgb24TransactionOutcome.RejectedBusy
                    : Rgb24TransactionOutcome.RejectedLeaseAcquireFailed;
            _generation = generation;
            _frameIndex = frameIndex;
            _frameIdentity = new object();
            _eofCompleted = false;
            return Rgb24TransactionOutcome.Accepted;
        }

        internal Rgb24TransactionOutcome TryCompleteEof(long generation, long frameIndex,
            out OwnedRgb24Frame frame, out string error)
        {
            frame = null;
            error = null;
            if (!_bridge.IsMainThread)
            {
                error = "rgb24-wrong-main-thread";
                return Rgb24TransactionOutcome.RejectedWrongThread;
            }
            if (_aborting || _lease == null || _eofCompleted)
            {
                error = "rgb24-transaction-not-awaiting-eof";
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }
            if (generation != _generation)
            {
                error = "rgb24-transaction-stale-generation-eof";
                return Rgb24TransactionOutcome.RejectedStaleGeneration;
            }
            if (frameIndex != _frameIndex)
            {
                error = "rgb24-transaction-wrong-frame-eof";
                return Rgb24TransactionOutcome.RejectedWrongFrame;
            }
            _eofCompleted = true;
            frame = _lease.Frame;
            return Rgb24TransactionOutcome.Accepted;
        }

        internal Rgb24TransactionOutcome TryBeginDelivery(out string error, out string errorDetail)
        {
            error = null;
            errorDetail = null;
            if (!_bridge.IsMainThread)
            {
                error = "rgb24-wrong-main-thread";
                return Rgb24TransactionOutcome.RejectedWrongThread;
            }
            if (_aborting || _lease == null || !_eofCompleted || _lease.DeliveryPinned)
            {
                error = "rgb24-transaction-not-deliverable";
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }

            // L2 调用之前就 pin。即使 transport 同步回入 abort，也不能释放它正在读取的数组。
            _lease.DeliveryPinned = true;
            _beginningDelivery = true;
            Rgb24TransportAttempt attempt;
            try
            {
                attempt = _transport.TryBeginWrite(_lease.Frame);
            }
            catch (Exception ex)
            {
                // 无法证明 transport 未接受：保留 pinned ownership，禁止冒充可安全复用。
                error = "rgb24-transport-acceptance-unknown";
                errorDetail = ex.Message;
                return Rgb24TransactionOutcome.RejectedTransportRejected;
            }
            finally
            {
                _beginningDelivery = false;
            }

            if (attempt == null || (attempt.Status == Rgb24TransportStatus.Accepted && attempt.Completion == null))
            {
                error = "rgb24-transport-acceptance-unknown";
                return Rgb24TransactionOutcome.RejectedTransportRejected;
            }
            if (attempt.Status != Rgb24TransportStatus.Accepted)
            {
                // 明确拒绝才可以解除保守 pin；scheduler 随后统一 abort。
                _lease.DeliveryPinned = false;
                error = attempt.Status == Rgb24TransportStatus.Busy ? "rgb24-transport-busy" : attempt.ErrorCode;
                errorDetail = attempt.ErrorDetail;
                return attempt.Status == Rgb24TransportStatus.Busy ? Rgb24TransactionOutcome.RejectedBusy
                    : Rgb24TransactionOutcome.RejectedTransportRejected;
            }

            // 接受身份先发布，最后才注册 continuation；已完成 Task 也不能抢跑。
            _write = new DeliveryNotification(_lease.Frame, _frameIdentity, attempt.Completion, _bridge, OnPostedCompletion);
            _write.Observe();
            return Rgb24TransactionOutcome.Accepted;
        }

        private void OnPostedCompletion(DeliveryNotification notification)
        {
            // Unity 上下文即使在 UMM 停止 OnUpdate 后仍可回投；仅退休所属 lease，不触碰 scheduler。
            if (_bridge.IsMainThread && _aborting && ReferenceEquals(_write, notification))
                TryAbort(out _);
        }

        internal bool TryTakePendingEnvelope(out Rgb24DeliveryEnvelope envelope, out string bridgeError)
        {
            envelope = null;
            bridgeError = null;
            if (!_bridge.IsMainThread)
            {
                bridgeError = "rgb24-wrong-main-thread";
                return false;
            }
            return _write != null && _write.TryTake(out envelope, out bridgeError);
        }

        // scheduler phase 是外部 authority；transaction 仅检查本 lease 的 accepted/completion 证据。
        internal Rgb24TransactionOutcome ConsumeEnvelope(bool schedulerAwaitingDelivery,
            Rgb24DeliveryEnvelope envelope, out string errorCode, out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;
            if (!_bridge.IsMainThread)
            {
                errorCode = "rgb24-wrong-main-thread";
                return Rgb24TransactionOutcome.RejectedWrongThread;
            }
            if (envelope == null)
            {
                errorCode = "rgb24-envelope-null";
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }
            if (envelope.Generation != _generation)
            {
                errorCode = "rgb24-envelope-stale-generation";
                return Rgb24TransactionOutcome.RejectedStaleGeneration;
            }
            if (_hasConsumedCompletion && envelope.Generation == _lastConsumedGeneration &&
                envelope.AbsoluteFrameIndex == _lastConsumedFrameIndex && envelope.TokenId == _lastConsumedTokenId)
            {
                errorCode = "rgb24-envelope-duplicate-completion";
                return Rgb24TransactionOutcome.RejectedDuplicateCompletion;
            }
            if (_lease == null)
            {
                errorCode = "rgb24-transaction-no-frame-in-flight";
                return Rgb24TransactionOutcome.RejectedNoFrameInFlight;
            }
            if (envelope.AbsoluteFrameIndex != _frameIndex)
            {
                errorCode = "rgb24-envelope-wrong-frame";
                return Rgb24TransactionOutcome.RejectedWrongFrame;
            }
            if (envelope.TokenId != _lease.Frame.TokenId)
            {
                errorCode = "rgb24-envelope-token-mismatch";
                return Rgb24TransactionOutcome.RejectedTokenMismatch;
            }
            if (_aborting || !schedulerAwaitingDelivery || !_eofCompleted)
            {
                errorCode = "rgb24-envelope-wrong-phase";
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }
            if (_write == null || !ReferenceEquals(envelope.FrameIdentity, _frameIdentity) ||
                !ReferenceEquals(envelope.Completion, _write.Completion) || !_write.Completion.IsCompleted ||
                !_write.IsAuthentic(envelope))
            {
                errorCode = "rgb24-envelope-completion-identity";
                return Rgb24TransactionOutcome.RejectedCompletionIdentity;
            }
            string notificationError = _write.Failure;
            if (notificationError != null)
            {
                errorCode = notificationError;
                return Rgb24TransactionOutcome.Failed;
            }
            if (!ReleaseCompletedLease(out errorCode)) return Rgb24TransactionOutcome.Failed;
            if (!envelope.Success)
            {
                errorCode = envelope.ErrorCode ?? "rgb24-delivery-failed";
                errorDetail = envelope.ErrorDetail;
                return Rgb24TransactionOutcome.Failed;
            }
            return Rgb24TransactionOutcome.Committed;
        }

        // Cancel 的帧收敛不依赖 Post 成功。唯一释放依据是当前写入 Task 的真实完成。
        // 该入口只退休 ownership，绝不返回可 Commit 的结果。
        internal bool TryAbort(out string error)
        {
            error = null;
            if (!_bridge.IsMainThread)
            {
                error = "rgb24-wrong-main-thread";
                return false;
            }
            _aborting = true;
            if (_beginningDelivery)
            {
                error = "rgb24-abort-delivery-in-flight";
                return false;
            }
            if (_write != null)
            {
                if (!_write.Completion.IsCompleted)
                {
                    error = "rgb24-abort-delivery-in-flight";
                    return false;
                }
                ReadOutcome(_write.Completion); // 观察 fault/cancel；不把失败写入当作成功帧。
                return ReleaseCompletedLease(out error);
            }
            if (_lease == null) return true;
            if (_lease.DeliveryPinned)
            {
                error = "rgb24-transport-acceptance-unknown";
                return false;
            }
            if (!_pool.TryRelease(_lease, out error)) return false;
            _lease = null;
            _frameIdentity = null;
            _eofCompleted = false;
            return true;
        }

        private bool ReleaseCompletedLease(out string error)
        {
            _lease.DeliveryPinned = false;
            if (!_pool.TryRelease(_lease, out error)) return false;
            _lease.TryMarkCompletionConsumed();
            _hasConsumedCompletion = true;
            _lastConsumedGeneration = _generation;
            _lastConsumedFrameIndex = _frameIndex;
            _lastConsumedTokenId = _lease.Frame.TokenId;
            _lease = null;
            _write = null;
            _frameIdentity = null;
            _eofCompleted = false;
            return true;
        }

        internal void Reset()
        {
            if (!_bridge.IsMainThread || HasResidualOwnership)
                throw new InvalidOperationException("rgb24-reset-with-residual-or-wrong-thread");
            _aborting = false;
            _generation = -1;
            _frameIndex = -1;
            _hasConsumedCompletion = false;
        }

        private static Rgb24FrameWriteOutcome ReadOutcome(Task<Rgb24FrameWriteOutcome> task)
        {
            if (task.IsFaulted)
                return Rgb24FrameWriteOutcome.Failure("rgb24-delivery-task-faulted", task.Exception.GetBaseException().Message);
            if (task.IsCanceled)
                return Rgb24FrameWriteOutcome.Failure("rgb24-delivery-task-canceled", null);
            return task.Result ?? Rgb24FrameWriteOutcome.Failure("rgb24-delivery-task-no-result", null);
        }

        // 每次 accepted write 独占一个通知槽。迟到 Post 只接触旧槽，不能覆盖下一帧或新 session。
        // 后台只写锁保护的通知数据；只有验证线程后的 Post 回调允许退休局部 lease。
        private sealed class DeliveryNotification
        {
            private readonly object _sync = new object();
            private readonly long _generation, _index, _token;
            private readonly object _identity;
            private readonly Rgb24MainThreadBridge _bridge;
            private readonly Action<DeliveryNotification> _onMainThread;
            private Rgb24DeliveryEnvelope _completed, _pending;
            private string _failure;
            private int _stallTicks;
            internal Task<Rgb24FrameWriteOutcome> Completion { get; }
            internal string Failure { get { lock (_sync) return _failure; } }

            internal DeliveryNotification(OwnedRgb24Frame frame, object identity,
                Task<Rgb24FrameWriteOutcome> completion, Rgb24MainThreadBridge bridge,
                Action<DeliveryNotification> onMainThread)
            {
                _generation = frame.Generation;
                _index = frame.AbsoluteFrameIndex;
                _token = frame.TokenId;
                _identity = identity;
                Completion = completion;
                _bridge = bridge;
                _onMainThread = onMainThread;
            }

            internal void Observe()
            {
                Completion.ContinueWith(task =>
                {
                    var envelope = new Rgb24DeliveryEnvelope(_generation, _index, _token,
                        ReadOutcome(task), _identity, task);
                    lock (_sync) _completed = envelope;
                    if (!_bridge.TryPost(envelope, Store, out string error, Fail))
                        Fail(error ?? "rgb24-bridge-post-failed");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            private void Store(Rgb24DeliveryEnvelope envelope)
            {
                lock (_sync) _pending = envelope;
                _onMainThread(this);
            }

            private void Fail(string error)
            {
                lock (_sync) _failure = error;
            }

            internal bool IsAuthentic(Rgb24DeliveryEnvelope envelope)
            {
                lock (_sync) return ReferenceEquals(envelope, _completed);
            }

            internal bool TryTake(out Rgb24DeliveryEnvelope envelope, out string error)
            {
                lock (_sync)
                {
                    envelope = null;
                    error = _failure;
                    if (error != null) return false;
                    if (_pending != null)
                    {
                        envelope = _pending;
                        _pending = null;
                        _stallTicks = 0;
                        return true;
                    }
                    // IO 未结束时不计数。低 FPS/暂停不会增加墙钟 deadline。
                    if (Completion.IsCompleted && ++_stallTicks > BridgeStallTickLimit)
                        error = "rgb24-bridge-notification-stalled";
                    return false;
                }
            }
        }
    }
}
