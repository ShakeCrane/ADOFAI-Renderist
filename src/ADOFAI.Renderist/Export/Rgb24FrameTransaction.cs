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
    /// 只含值类型与字符串，不含 Unity 对象、不含 scheduler 可变状态、不含 buffer 引用。
    /// </summary>
    internal sealed class Rgb24DeliveryEnvelope
    {
        internal Rgb24DeliveryEnvelope(
            long generation, long absoluteFrameIndex, long tokenId, Rgb24FrameWriteOutcome outcome)
        {
            Generation = generation;
            AbsoluteFrameIndex = absoluteFrameIndex;
            TokenId = tokenId;
            Outcome = outcome ?? Rgb24FrameWriteOutcome.Failure("rgb24-delivery-no-outcome", null);
        }

        internal long Generation { get; private set; }
        internal long AbsoluteFrameIndex { get; private set; }
        internal long TokenId { get; private set; }
        internal Rgb24FrameWriteOutcome Outcome { get; private set; }

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
        internal bool TryPost(Rgb24DeliveryEnvelope envelope, Action<Rgb24DeliveryEnvelope> onMainThread, out string error)
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
                _context.Post(state => onMainThread((Rgb24DeliveryEnvelope)state), envelope);
                return true;
            }
            catch (Exception ex)
            {
                error = "rgb24-bridge-post-failed:" + ex.Message;
                return false;
            }
        }
    }

    /// <summary>事务阶段。**不是**第二套 scheduler 状态机：scheduler 的 SchedulerStatus 只是它的镜像。</summary>
    internal enum Rgb24TransactionPhase
    {
        Idle = 0,

        /// <summary>本帧已 Prepare 并请求 EOF；此时**禁止**再次 Prepare。</summary>
        AwaitingEOF = 1,

        /// <summary>CPU frame 已形成并交付；此时**禁止**推进 outputFrameIndex / forced time / 下一个 EOF。</summary>
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
    }

    /// <summary>
    /// L3-A 单帧 RGB24 事务（Unity-free，可被独立 net48 harness 完整覆盖）。
    ///
    /// 主链路的中间三段由本类拥有：
    ///   Prepare(N) → **AwaitingEOF** → EOF 形成 CPU frame → **AwaitingDelivery**
    ///   → 交付（L2 TryWriteFrame）→ Completion → Unity 主线程校验 → 允许 CommitFrame(N)
    ///
    /// 本类**不**推进时间、不拥有 EOF coroutine、不拥有 FFmpeg 进程、不 Commit：
    /// 它只在主线程校验通过后返回"可以提交一次"的决定。
    /// </summary>
    internal sealed class Rgb24FrameTransaction
    {
        /// <summary>
        /// "Completion 已结束但主线程交接始终没有到达"的内部桥接不变量上限（主线程观察次数）。
        /// 这是**内部桥接** deadline，不是 IO timeout：合法 FFmpeg 背压可以任意长，
        /// 只有"Task 已完成却始终没有信封"才计数。
        /// </summary>
        internal const int BridgeStallTickLimit = 120;

        private readonly object _sync = new object();
        private readonly Rgb24FrameBufferPool _pool;
        private readonly IRgb24FrameTransport _transport;
        private readonly Rgb24MainThreadBridge _bridge;
        private readonly Action<Rgb24DeliveryEnvelope> _onEnvelopePosted;

        private Rgb24TransactionPhase _phase = Rgb24TransactionPhase.Idle;
        private long _generation = -1;
        private long _frameIndex = -1;
        private Rgb24FrameLease _lease;
        private bool _deliveryAccepted;
        private bool _aborting;

        // 后台 continuation 只写这两项；主线程只读。
        private bool _completionTaskFinished;
        private string _bridgeFailure;
        private Rgb24DeliveryEnvelope _pendingEnvelope;

        private int _stallTicks;

        // 一次性消费的**历史身份**：消费成功后 _lease 会被清空，因此"重复消费"必须靠这份
        // 身份记录来判定，否则 duplicate 分支会被 _lease == null 遮蔽而变成死代码。
        private bool _hasConsumedCompletion;
        private long _lastConsumedGeneration = -1;
        private long _lastConsumedFrameIndex = -1;
        private long _lastConsumedTokenId = -1;

        internal Rgb24FrameTransaction(
            Rgb24FrameBufferPool pool,
            IRgb24FrameTransport transport,
            Rgb24MainThreadBridge bridge,
            Action<Rgb24DeliveryEnvelope> onEnvelopePosted)
        {
            if (pool == null) throw new ArgumentNullException("pool");
            if (transport == null) throw new ArgumentNullException("transport");
            if (bridge == null) throw new ArgumentNullException("bridge");
            if (onEnvelopePosted == null) throw new ArgumentNullException("onEnvelopePosted");

            _pool = pool;
            _transport = transport;
            _bridge = bridge;
            _onEnvelopePosted = onEnvelopePosted;
        }

        internal Rgb24TransactionPhase Phase { get { return _phase; } }
        internal long Generation { get { return _generation; } }
        internal long FrameIndex { get { return _frameIndex; } }

        /// <summary>true = 本帧已交付且其结果尚未被主线程消费（缓冲仍被后台读取）。</summary>
        internal bool HasUnconvergedDelivery { get { return _deliveryAccepted; } }

        /// <summary>true = 仍有本事务拥有的资源未收敛（residual gate 的输入之一）。</summary>
        internal bool HasResidualOwnership
        {
            get
            {
                if (_deliveryAccepted) return true;
                if (_lease != null && !_lease.Released) return true;
                return false;
            }
        }

        internal long LeaseTokenId
        {
            get { return _lease == null ? -1 : _lease.Frame.TokenId; }
        }

        /// <summary>当前 in-flight 帧（Idle 时为 null）。driver 用它填充 CPU 数据。</summary>
        internal OwnedRgb24Frame CurrentFrame
        {
            get { return _lease == null ? null : _lease.Frame; }
        }

        /// <summary>
        /// Prepare 边界：Idle → AwaitingEOF。Awaiting* 期间重复调用是 invariant failure，
        /// 绝不建立第二个 in-flight frame。
        /// </summary>
        internal Rgb24TransactionOutcome TryBeginFrame(long generation, long frameIndex, out string error)
        {
            error = null;

            if (_aborting)
            {
                error = "rgb24-transaction-aborting";
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }

            if (_phase != Rgb24TransactionPhase.Idle)
            {
                error = "rgb24-transaction-frame-in-flight:" + _phase;
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }

            Rgb24FrameLease lease;
            string acquireError;
            if (!_pool.TryAcquire(generation, frameIndex, out lease, out acquireError))
            {
                error = acquireError;
                return acquireError == "rgb24-lease-busy"
                    ? Rgb24TransactionOutcome.RejectedBusy
                    : Rgb24TransactionOutcome.RejectedLeaseAcquireFailed;
            }

            _lease = lease;
            _generation = generation;
            _frameIndex = frameIndex;
            _deliveryAccepted = false;
            _completionTaskFinished = false;
            _bridgeFailure = null;
            _pendingEnvelope = null;
            _stallTicks = 0;
            _phase = Rgb24TransactionPhase.AwaitingEOF;
            return Rgb24TransactionOutcome.Accepted;
        }

        /// <summary>
        /// EOF 形成 CPU frame：AwaitingEOF → AwaitingDelivery。
        /// generation / frame index 任一不匹配都必须拒绝（stale EOF / wrong frame EOF）。
        /// </summary>
        internal Rgb24TransactionOutcome TryCompleteEof(
            long generation, long frameIndex, out OwnedRgb24Frame frame, out string error)
        {
            frame = null;
            error = null;

            if (_phase != Rgb24TransactionPhase.AwaitingEOF)
            {
                error = "rgb24-transaction-not-awaiting-eof:" + _phase;
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

            _phase = Rgb24TransactionPhase.AwaitingDelivery;
            frame = _lease.Frame;
            return Rgb24TransactionOutcome.Accepted;
        }

        /// <summary>
        /// 交付本帧。接受之后 lease 被 pin 住：Completion 被消费之前缓冲绝不可复用。
        /// 立即拒绝（未接受）时 L2 未取得长期读取 ownership，因此不 pin。
        /// </summary>
        internal Rgb24TransactionOutcome TryBeginDelivery(out string error, out string errorDetail)
        {
            error = null;
            errorDetail = null;

            if (_phase != Rgb24TransactionPhase.AwaitingDelivery)
            {
                error = "rgb24-transaction-not-awaiting-delivery:" + _phase;
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }

            if (_deliveryAccepted)
            {
                error = "rgb24-transaction-delivery-already-accepted";
                return Rgb24TransactionOutcome.RejectedDuplicateCompletion;
            }

            Rgb24TransportAttempt attempt;
            try
            {
                attempt = _transport.TryBeginWrite(_lease.Frame);
            }
            catch (Exception ex)
            {
                error = "rgb24-transport-exception";
                errorDetail = ex.Message;
                return Rgb24TransactionOutcome.RejectedTransportRejected;
            }

            if (attempt == null)
            {
                error = "rgb24-transport-null-attempt";
                return Rgb24TransactionOutcome.RejectedTransportRejected;
            }

            if (attempt.Status == Rgb24TransportStatus.Busy)
            {
                // 全链路最多一帧在途：busy 是 scheduler invariant failure，不重试、不排队。
                error = "rgb24-transport-busy";
                errorDetail = attempt.ErrorDetail;
                return Rgb24TransactionOutcome.RejectedBusy;
            }

            if (attempt.Status != Rgb24TransportStatus.Accepted || attempt.Completion == null)
            {
                error = attempt.ErrorCode ?? "rgb24-transport-rejected";
                errorDetail = attempt.ErrorDetail;
                return Rgb24TransactionOutcome.RejectedTransportRejected;
            }

            _deliveryAccepted = true;
            _lease.DeliveryPinned = true;

            // continuation 的初值必须在交付线程之外冻结：事务字段可能在下一次 Prepare 时被改写。
            long generation = _generation;
            long frameIndex = _frameIndex;
            long tokenId = _lease.Frame.TokenId;
            Task<Rgb24FrameWriteOutcome> completion = attempt.Completion;

            completion.ContinueWith(
                task => OnDeliveryTaskCompleted(task, generation, frameIndex, tokenId),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return Rgb24TransactionOutcome.Accepted;
        }

        /// <summary>
        /// 后台 continuation。**只做三件事**：读取 Task outcome、封装异常、Post 不可变信封。
        /// 绝不调用 Unity / Harmony / GUI，也绝不接触 scheduler 可变状态。
        /// </summary>
        private void OnDeliveryTaskCompleted(
            Task<Rgb24FrameWriteOutcome> task, long generation, long frameIndex, long tokenId)
        {
            Rgb24FrameWriteOutcome outcome;
            try
            {
                if (task.IsFaulted)
                {
                    Exception inner = task.Exception == null ? null : task.Exception.GetBaseException();
                    outcome = Rgb24FrameWriteOutcome.Failure(
                        "rgb24-delivery-task-faulted", inner == null ? null : inner.Message);
                }
                else if (task.IsCanceled)
                {
                    outcome = Rgb24FrameWriteOutcome.Failure("rgb24-delivery-task-canceled", null);
                }
                else
                {
                    outcome = task.Result ??
                              Rgb24FrameWriteOutcome.Failure("rgb24-delivery-task-no-result", null);
                }
            }
            catch (Exception ex)
            {
                outcome = Rgb24FrameWriteOutcome.Failure("rgb24-delivery-task-exception", ex.Message);
            }

            lock (_sync)
            {
                _completionTaskFinished = true;
            }

            var envelope = new Rgb24DeliveryEnvelope(generation, frameIndex, tokenId, outcome);
            string postError;
            if (!_bridge.TryPost(envelope, StorePostedEnvelope, out postError))
            {
                lock (_sync)
                {
                    _bridgeFailure = postError ?? "rgb24-bridge-post-failed";
                }
            }
        }

        /// <summary>由 main-thread 上下文调用：只入队，不在 Post 回调里做校验或提交。</summary>
        private void StorePostedEnvelope(Rgb24DeliveryEnvelope envelope)
        {
            lock (_sync)
            {
                _pendingEnvelope = envelope;
            }
        }

        /// <summary>
        /// 主线程取走已投递的信封（事件驱动，不轮询 IO）。
        /// 同时把"后台已完成但主线程始终没有收到交接"的内部桥接故障暴露出来。
        /// </summary>
        internal bool TryTakePendingEnvelope(out Rgb24DeliveryEnvelope envelope, out string bridgeError)
        {
            envelope = null;
            bridgeError = null;

            lock (_sync)
            {
                if (_pendingEnvelope != null)
                {
                    envelope = _pendingEnvelope;
                    _pendingEnvelope = null;
                    _stallTicks = 0;
                    return true;
                }

                if (_bridgeFailure != null)
                {
                    bridgeError = _bridgeFailure;
                    return false;
                }

                if (_completionTaskFinished && _deliveryAccepted)
                {
                    // Task 已结束，但信封始终没有完成交接：内部桥接不变量失败。
                    _stallTicks++;
                    if (_stallTicks > BridgeStallTickLimit)
                        bridgeError = "rgb24-bridge-notification-stalled";
                }
            }

            return false;
        }

        /// <summary>
        /// 主线程消费交付结果。必须逐项校验 generation / frame index / token / phase，
        /// 且**只能消费一次**：重复回调、stale generation、wrong frame、token 不匹配一律拒绝。
        /// </summary>
        internal Rgb24TransactionOutcome ConsumeEnvelope(
            Rgb24DeliveryEnvelope envelope, out string errorCode, out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;

            if (envelope == null)
            {
                errorCode = "rgb24-envelope-null";
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }

            // 一次性 token：同一 (generation, frameIndex, tokenId) 的第二次消费必须**稳定**映射为
            // duplicate。这一步必须先于 _lease == null 判断 —— 消费成功会清空 _lease，
            // 否则该分支永远不可达（曾被独立回归准确指出）。
            if (_hasConsumedCompletion &&
                envelope.Generation == _lastConsumedGeneration &&
                envelope.AbsoluteFrameIndex == _lastConsumedFrameIndex &&
                envelope.TokenId == _lastConsumedTokenId)
            {
                errorCode = "rgb24-envelope-duplicate-completion";
                return Rgb24TransactionOutcome.RejectedDuplicateCompletion;
            }

            if (_lease == null)
            {
                errorCode = "rgb24-transaction-no-frame-in-flight";
                return Rgb24TransactionOutcome.RejectedNoFrameInFlight;
            }

            // 次级防线：lease 自身的消费标记（同一次 acquire 内重复消费）。
            if (_lease.CompletionConsumed)
            {
                errorCode = "rgb24-envelope-duplicate-completion";
                return Rgb24TransactionOutcome.RejectedDuplicateCompletion;
            }

            if (envelope.Generation != _generation)
            {
                errorCode = "rgb24-envelope-stale-generation";
                return Rgb24TransactionOutcome.RejectedStaleGeneration;
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

            if (_phase != Rgb24TransactionPhase.AwaitingDelivery)
            {
                errorCode = "rgb24-envelope-wrong-phase:" + _phase;
                return Rgb24TransactionOutcome.RejectedWrongPhase;
            }

            _lease.TryMarkCompletionConsumed();

            // 记录已消费身份（_lease 随后会被清空）：这是 duplicate 判定的唯一依据。
            _hasConsumedCompletion = true;
            _lastConsumedGeneration = envelope.Generation;
            _lastConsumedFrameIndex = envelope.AbsoluteFrameIndex;
            _lastConsumedTokenId = envelope.TokenId;

            // Completion 已结束（无论成功或失败），后台不再读取这些数组：可以解除 pin。
            _lease.DeliveryPinned = false;
            _deliveryAccepted = false;

            Rgb24FrameLease lease = _lease;
            _lease = null;
            _phase = Rgb24TransactionPhase.Idle;

            string releaseError;
            if (!_pool.TryRelease(lease, out releaseError))
            {
                errorCode = releaseError ?? "rgb24-lease-release-failed";
                return Rgb24TransactionOutcome.Failed;
            }

            if (!envelope.Success)
            {
                errorCode = envelope.ErrorCode ?? "rgb24-delivery-failed";
                errorDetail = envelope.ErrorDetail;
                return Rgb24TransactionOutcome.Failed;
            }

            return Rgb24TransactionOutcome.Committed;
        }

        /// <summary>
        /// 取消 / 失败收敛入口。**不**强行释放仍被后台读取的 lease：
        /// 只有该帧 Completion 被消费之后缓冲才可复用。返回 false 表示仍有 residual ownership。
        /// </summary>
        internal bool TryAbort(out string error)
        {
            error = null;
            _aborting = true;

            if (_deliveryAccepted)
            {
                error = "rgb24-abort-delivery-in-flight";
                return false;
            }

            if (_lease == null)
            {
                _phase = Rgb24TransactionPhase.Idle;
                return true;
            }

            Rgb24FrameLease lease = _lease;
            string releaseError;
            if (!_pool.TryRelease(lease, out releaseError))
            {
                error = releaseError ?? "rgb24-lease-release-failed";
                return false;
            }

            _lease = null;
            _phase = Rgb24TransactionPhase.Idle;
            return true;
        }

        /// <summary>会话结束：清空全部引用（不影响已经释放的缓冲）。</summary>
        internal void Reset()
        {
            _lease = null;
            _phase = Rgb24TransactionPhase.Idle;
            _generation = -1;
            _frameIndex = -1;
            _deliveryAccepted = false;
            _aborting = false;
            _completionTaskFinished = false;
            _bridgeFailure = null;
            _pendingEnvelope = null;
            _stallTicks = 0;
            _hasConsumedCompletion = false;
            _lastConsumedGeneration = -1;
            _lastConsumedFrameIndex = -1;
            _lastConsumedTokenId = -1;
        }
    }
}
