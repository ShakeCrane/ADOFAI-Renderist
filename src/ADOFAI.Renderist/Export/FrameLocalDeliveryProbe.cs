using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Ffmpeg;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    // =====================================================================================
    // F1-A 受限诊断设施（PROJECT_UNDERSTANDING §2.2.26 A–I 白名单第 1 项）
    //
    // 边界（刻意保持）：
    //   * 默认关闭：没有外部 guardian 提供的有效 descriptor/握手时，任何导出都完全走原有路径；
    //   * 一次性：只允许一个 run、一个 accepted write、最多一次 CommitFrame；
    //   * 不是第二套 scheduler / 时间 owner / 编码管线 / 捕获路径；
    //   * 本文件不调用 Unity API、不推进时间、不拥有 FrameCaptureDriver。
    //
    // 本文件承载三件纯托管的事：
    //   1. FrameLocalDeliveryClaimGate —— 通知槽的**单一短锁**状态机：发布、取消、一次性 claim。
    //   2. FrameLocalDeliveryProbeSlot  —— 把 gate 与 run nonce / 帧身份绑定，并向等待者发 ready。
    //   3. FrameLocalDeliveryProbe      —— 外部 descriptor 加载、guardian 握手、专用 monitor 线程、
    //      受控 stdin 门、encoder 身份观测与定向终止、settlement 判定。
    // =====================================================================================

    /// <summary>一次 claim 请求被拒绝/接受的原因。用于稳定日志与测试断言。</summary>
    internal enum FrameLocalDeliveryClaimStatus
    {
        /// <summary>成功许可：这是本帧唯一的线性化成功点。</summary>
        ClaimedSuccess = 0,

        /// <summary>真实已发布的失败结果：允许走原有失败消费/退休，但绝不 Commit。</summary>
        ClaimedFailure = 1,

        /// <summary>claim 之前已接受取消（含 guardian soft abort）：Commit=0。</summary>
        Aborted = 2,

        /// <summary>claim 已关闭（成功/失败/取消/NoGo 任一）。不得重试或换对象重新包装。</summary>
        AlreadyClaimed = 3,

        /// <summary>结果尚未发布且无故障/取消。调用方在锁外继续等待 ready。</summary>
        NotReady = 4,

        /// <summary>发布基础设施故障（Post 失败、observer 异常等）。禁止成功许可。</summary>
        Fault = 5,

        /// <summary>信封身份与本通知槽不符（错误线程/伪造引用/旧 generation）。</summary>
        WrongIdentity = 6,

        /// <summary>probe 已被判定 No-Go。</summary>
        NoGo = 7,
    }

    /// <summary>claim 结果：状态 + 说明 + （成功时）唯一 permit。</summary>
    internal sealed class FrameLocalDeliveryClaimResult
    {
        private FrameLocalDeliveryClaimResult(FrameLocalDeliveryClaimStatus status, string detail, string permitId)
        {
            Status = status;
            Detail = detail;
            PermitId = permitId;
        }

        internal FrameLocalDeliveryClaimStatus Status { get; private set; }
        internal string Detail { get; private set; }

        /// <summary>成功许可的不可重放标识（仅诊断/证据用；不能用于二次领取）。</summary>
        internal string PermitId { get; private set; }

        internal bool IsSuccessClaim { get { return Status == FrameLocalDeliveryClaimStatus.ClaimedSuccess; } }

        internal static FrameLocalDeliveryClaimResult Make(FrameLocalDeliveryClaimStatus status, string detail,
            string permitId)
        {
            return new FrameLocalDeliveryClaimResult(status, detail, permitId);
        }
    }

    /// <summary>
    /// 通知槽的**单一短锁**状态机：原始 canonical 发布、取消接受、一次性 claim 全部在同一个
    /// <c>_sync</c> 下线性化。锁内**绝不**执行 IO / 等待 / Unity API / 日志 / 资源释放。
    ///
    /// 状态迁移（单向，绝不重新打开）：
    ///   Open ──publish(success)──► Open(published)
    ///   Open ──publish(failure)──► Open(published,失败结果)
    ///   Open ──publish(fault)────► NoGo
    ///   Open ──acceptCancelled───► Aborted
    ///   Open(published) ──claim──► ClaimedSuccess | ClaimedFailure
    ///   任意已关闭状态：AlreadyClaimed
    /// </summary>
    internal sealed class FrameLocalDeliveryClaimGate : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _runNonce;
        private readonly long _generation;
        private readonly long _absoluteFrameIndex;
        private readonly long _tokenId;
        private readonly object _frameIdentity;
        private readonly Task<Rgb24FrameWriteOutcome> _completion;
        private readonly object _notificationIdentity;

        private Rgb24DeliveryEnvelope _published;
        private Rgb24DeliveryEnvelope _pending;
        private string _failure;
        private string _ignoredStores;
        private string _publicationFault;
        private int _stallTicks;

        private FrameLocalDeliveryClaimStatus? _closedAs;
        private string _closedDetail;
        private string _permitId;

        private bool _cancelledAccepted;
        private string _cancelReason;
        private bool _cancelIsEmergency;

        private bool _directRoute;
        private bool _retirementWakeRequested;
        private bool _retirementWakePosted;
        private bool _producerSettled;
        private ManualResetEventSlim _readyEvent;
        private bool _disposed;

        internal FrameLocalDeliveryClaimGate(string runNonce, long generation, long absoluteFrameIndex,
            long tokenId, object frameIdentity, Task<Rgb24FrameWriteOutcome> completion, object notificationIdentity)
        {
            _runNonce = runNonce ?? string.Empty;
            _generation = generation;
            _absoluteFrameIndex = absoluteFrameIndex;
            _tokenId = tokenId;
            _frameIdentity = frameIdentity;
            _completion = completion;
            _notificationIdentity = notificationIdentity;
        }

        internal string RunNonce { get { return _runNonce; } }
        internal long Generation { get { return _generation; } }
        internal long AbsoluteFrameIndex { get { return _absoluteFrameIndex; } }
        internal long TokenId { get { return _tokenId; } }
        internal object FrameIdentity { get { return _frameIdentity; } }
        internal Task<Rgb24FrameWriteOutcome> Completion { get { return _completion; } }
        internal object NotificationIdentity { get { return _notificationIdentity; } }

        /// <summary>默认 AsyncPosted；只有 probe 绑定后才能切到 direct。</summary>
        internal bool DirectRoute { get { lock (_sync) return _directRoute; } }

        internal string Failure { get { lock (_sync) return _failure; } }

        /// <summary>诊断：记录被忽略的 Store（迟到/重复/已 claim/退休唤醒）。</summary>
        internal void RecordIgnoredStore(string reason)
        {
            lock (_sync) _ignoredStores = (_ignoredStores ?? string.Empty) +
                                          (string.IsNullOrEmpty(_ignoredStores) ? "" : ";") + (reason ?? "unknown");
        }

        internal string IgnoredStores { get { lock (_sync) return _ignoredStores; } }

        internal bool IsClosed { get { lock (_sync) return _closedAs.HasValue; } }

        internal bool CancelAccepted { get { lock (_sync) return _cancelledAccepted; } }

        internal bool ProducerSettled { get { lock (_sync) return _producerSettled; } }

        private Task _observer;
        private int _signalers;
        private int _waiters;
        internal int WaiterCount { get { lock (_sync) return _waiters; } }
        internal bool ObserverSettled { get { lock (_sync) return _observer == null || _observer.IsCompleted; } }
        internal void AttachObserver(Task observer) { lock (_sync) _observer = observer; }
        internal void MarkProducerSettled() { lock (_sync) _producerSettled = true; }

        /// <summary>
        /// 切到 CanonicalDirectProbe 路由。只允许在本笔 write 的 Observe 之前调用；
        /// 切到 direct 后正常完成不再走 Post/Store。
        /// </summary>
        internal bool TryEnableDirectRoute(out string error)
        {
            error = null;
            lock (_sync)
            {
                if (_disposed) { error = "f1-gate-disposed"; return false; }
                if (_directRoute) return true;
                if (_published != null || _producerSettled || _closedAs.HasValue)
                {
                    error = "f1-direct-route-too-late";
                    return false;
                }
                _directRoute = true;
                if (_readyEvent == null && !_disposed)
                    _readyEvent = new ManualResetEventSlim(false);
                return true;
            }
        }

        /// <summary>waiters 使用的 ready 句柄；未启用 direct 时为 null。</summary>
        internal WaitHandle ReadyHandle
        {
            get { lock (_sync) return _readyEvent == null ? null : _readyEvent.WaitHandle; }
        }

        /// <summary>
        /// direct 路由的原始发布点：在**同一个短锁**内设置 canonical 引用 / 故障，
        /// 锁外才 Set ready。返回 true 表示调用方需要在锁外 signal。
        /// </summary>
        internal bool TryPublishDirect(Rgb24DeliveryEnvelope envelope, string fault, out bool signalReady)
        {
            signalReady = false;
            lock (_sync)
            {
                if (!_directRoute) return false;
                if (_producerSettled || _disposed) return false;
                if (envelope != null) _published = envelope;
                if (fault != null)
                {
                    _publicationFault = fault;
                    if (!_closedAs.HasValue)
                    {
                        _closedAs = FrameLocalDeliveryClaimStatus.NoGo;
                        _closedDetail = fault;
                    }
                }
                _producerSettled = true;
                signalReady = true;
            }
            return true;
        }

        /// <summary>锁外 signal（绝不持锁 Set/Wait）。</summary>
        internal void SignalReady()
        {
            ManualResetEventSlim evt;
            lock (_sync)
            {
                evt = _readyEvent;
                if (evt == null) return;
                _signalers++;
            }
            try { evt.Set(); }
            finally { lock (_sync) _signalers--; }
        }

        internal int WaitForWake(WaitHandle[] handles, int timeoutMs)
        {
            lock (_sync)
            {
                if (_disposed || _readyEvent == null) throw new InvalidOperationException("f1-ready-unavailable");
                _waiters++;
            }
            try { return WaitHandle.WaitAny(handles, timeoutMs); }
            finally { lock (_sync) _waiters--; }
        }

        /// <summary>等待 ready（测试/诊断用；超时只表示"没有信号"，不是产品背压期限）。</summary>
        internal bool WaitReady(int timeoutMs)
        {
            WaitHandle handle = ReadyHandle;
            return handle != null && handle.WaitOne(timeoutMs);
        }

        /// <summary>AsyncPosted 路径的 Store：只对仍 Open 的槽设置 _pending。</summary>
        internal bool TryStorePending(Rgb24DeliveryEnvelope envelope, out string reason)
        {
            reason = null;
            lock (_sync)
            {
                if (envelope == null) { reason = "f1-store-null-envelope"; return false; }
                if (_closedAs.HasValue)
                {
                    reason = "f1-store-after-claim-closed:" + _closedAs.Value;
                    return false;
                }
                if (!ReferenceEquals(envelope, _published))
                {
                    reason = "f1-store-not-canonical";
                    return false;
                }
                if (_directRoute) { reason = "f1-store-direct-route"; return false; }
                _pending = envelope;
                return true;
            }
        }

        /// <summary>AsyncPosted 路径的取信封：只取 _pending，且仅当槽仍 Open。</summary>
        internal bool TryTakePending(out Rgb24DeliveryEnvelope envelope, out string error)
        {
            lock (_sync)
            {
                envelope = null;
                error = _failure;
                if (error != null) return false;
                if (_closedAs.HasValue) { error = "f1-take-after-claim-closed:" + _closedAs.Value; return false; }
                if (_pending != null)
                {
                    envelope = _pending;
                    _pending = null;
                    _stallTicks = 0;
                    return true;
                }
                if (_completion != null && _completion.IsCompleted && ++_stallTicks > Rgb24FrameTransaction.BridgeStallTickLimit)
                    error = "rgb24-bridge-notification-stalled";
                return false;
            }
        }

        /// <summary>
        /// 记录一个已观察到的 canonical 信封（AsyncPosted 的 result 发布点，等价于原 _completed）。
        /// </summary>
        internal void PublishCanonical(Rgb24DeliveryEnvelope envelope)
        {
            lock (_sync)
            {
                if (_directRoute) throw new InvalidOperationException("f1-async-publish-on-direct-route");
                if (_producerSettled) return;
                _published = envelope;
                _producerSettled = true;
            }
        }

        /// <summary>通知失败（Post 抛错 / 错误线程 / observer 异常）。禁止成功许可。</summary>
        internal void MarkFailure(string error)
        {
            lock (_sync)
            {
                if (_failure == null) _failure = error ?? "rgb24-bridge-failed";
            }
            SignalReady();
        }

        /// <summary>发布基础设施故障：No-Go，绝不发成功许可。</summary>
        internal void MarkPublicationFault(string reason)
        {
            lock (_sync)
            {
                if (_publicationFault == null) _publicationFault = reason ?? "f1-publication-fault";
                if (_failure == null) _failure = _publicationFault;
                if (!_closedAs.HasValue)
                {
                    _closedAs = FrameLocalDeliveryClaimStatus.NoGo;
                    _closedDetail = _publicationFault;
                }
            }
            SignalReady();
        }

        internal bool IsAuthentic(Rgb24DeliveryEnvelope envelope)
        {
            lock (_sync) return ReferenceEquals(envelope, _published);
        }

        /// <summary>只读地取得已发布的 canonical 引用（取引用不等于取得消费许可）。</summary>
        internal bool TryGetPublished(out Rgb24DeliveryEnvelope envelope)
        {
            lock (_sync)
            {
                envelope = _published;
                return envelope != null;
            }
        }

        internal bool PublishedSuccess { get { lock (_sync) return _published != null && _published.Success; } }

        /// <summary>
        /// **一次性 claim**：发布、取消与 claim 共用本锁，因此这是本帧唯一的线性化点。
        /// 顺序优先级：已关闭 → 取消 → 故障 → 未发布 → 失败结果 → 成功许可。
        /// </summary>
        internal FrameLocalDeliveryClaimResult TryClaim(Rgb24DeliveryEnvelope envelope)
        {
            lock (_sync)
            {
                if (_disposed) return FrameLocalDeliveryClaimResult.Make(
                    FrameLocalDeliveryClaimStatus.NoGo, "f1-gate-disposed", null);

                if (!ReferenceEquals(envelope, _published))
                    return FrameLocalDeliveryClaimResult.Make(
                        FrameLocalDeliveryClaimStatus.WrongIdentity,
                        "f1-claim-wrong-identity", null);

                if (_closedAs.HasValue)
                    return FrameLocalDeliveryClaimResult.Make(
                        FrameLocalDeliveryClaimStatus.AlreadyClaimed,
                        "f1-claim-already-closed:" + _closedAs.Value + ":" + (_closedDetail ?? ""), null);

                if (_cancelledAccepted)
                {
                    _closedAs = FrameLocalDeliveryClaimStatus.Aborted;
                    _closedDetail = _cancelReason ?? "f1-cancelled";
                    return FrameLocalDeliveryClaimResult.Make(
                        FrameLocalDeliveryClaimStatus.Aborted, _closedDetail, null);
                }

                if (_publicationFault != null)
                {
                    _closedAs = FrameLocalDeliveryClaimStatus.NoGo;
                    _closedDetail = _publicationFault;
                    return FrameLocalDeliveryClaimResult.Make(
                        FrameLocalDeliveryClaimStatus.NoGo, _publicationFault, null);
                }

                // 通知/桥接失败（Post 抛错、错误线程等）同样禁止成功许可：
                // 即使 result 已发布，也不允许把它当成可提交的成功帧。
                if (_failure != null)
                {
                    _closedAs = FrameLocalDeliveryClaimStatus.NoGo;
                    _closedDetail = _failure;
                    return FrameLocalDeliveryClaimResult.Make(
                        FrameLocalDeliveryClaimStatus.Fault, _failure, null);
                }

                if (_published == null)
                    return FrameLocalDeliveryClaimResult.Make(
                        FrameLocalDeliveryClaimStatus.NotReady, "f1-claim-not-ready", null);

                if (!_published.Success)
                {
                    _closedAs = FrameLocalDeliveryClaimStatus.ClaimedFailure;
                    _closedDetail = _published.ErrorCode ?? "rgb24-delivery-failed";
                    return FrameLocalDeliveryClaimResult.Make(
                        FrameLocalDeliveryClaimStatus.ClaimedFailure, _closedDetail, null);
                }

                _closedAs = FrameLocalDeliveryClaimStatus.ClaimedSuccess;
                _closedDetail = "f1-consume-success";
                _permitId = _runNonce + ":" + _generation.ToString(CultureInfo.InvariantCulture) + ":" +
                            _absoluteFrameIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                            _tokenId.ToString(CultureInfo.InvariantCulture);
                return FrameLocalDeliveryClaimResult.Make(
                    FrameLocalDeliveryClaimStatus.ClaimedSuccess, "f1-consume-success", _permitId);
            }
        }

        /// <summary>
        /// 接受一次 cancel 请求。返回 true = 在 claim 之前被接受（此后绝不发成功许可）；
        /// 返回 false = 已有成功许可（迟到取消，不追溯改写），或已关闭。
        /// </summary>
        internal bool TryAcceptCancel(string reason, bool emergency, out string error)
        {
            error = null;
            lock (_sync)
            {
                if (_closedAs == FrameLocalDeliveryClaimStatus.ClaimedSuccess)
                {
                    error = "f1-late-cancel-after-claim";
                    return false;
                }
                if (_closedAs.HasValue)
                {
                    error = "f1-cancel-after-closed:" + _closedAs.Value;
                    return false;
                }
                if (!_cancelledAccepted)
                {
                    _cancelledAccepted = true;
                    _cancelReason = reason ?? "f1-cancel";
                    _cancelIsEmergency = emergency;
                }
            }
            // The same lock above orders acceptance against claim. The ready event is also a cancel wake;
            // it does not imply write completion or authorize releasing the lease.
            SignalReady();
            return true;
        }

        internal bool CancelIsEmergency { get { lock (_sync) return _cancelIsEmergency; } }

        /// <summary>
        /// 登记"abort 退休唤醒"请求。返回 true 表示由 Observe 在终态发布之后补一次退休 Post；
        /// false 表示 Task 已完成，调用方按原路径直接退休。
        /// </summary>
        internal bool RequestRetirementWake()
        {
            lock (_sync)
            {
                if (_retirementWakePosted) return false;
                _retirementWakeRequested = true;
                return true;
            }
        }

        /// <summary>Observe 在终态发布之后调用：是否需要发出一次退休唤醒。</summary>
        internal bool TakeRetirementWake()
        {
            lock (_sync)
            {
                if (!_retirementWakeRequested || _retirementWakePosted) return false;
                _retirementWakePosted = true;
                return true;
            }
        }

        internal bool ClaimClosedAsSuccess { get { lock (_sync) return _closedAs == FrameLocalDeliveryClaimStatus.ClaimedSuccess; } }

        internal string Describe()
        {
            lock (_sync)
            {
                return "nonce=" + _runNonce +
                       ",gen=" + _generation.ToString(CultureInfo.InvariantCulture) +
                       ",frame=" + _absoluteFrameIndex.ToString(CultureInfo.InvariantCulture) +
                       ",token=" + _tokenId.ToString(CultureInfo.InvariantCulture) +
                       ",direct=" + (_directRoute ? "true" : "false") +
                       ",published=" + (_published == null ? "none" : (_published.Success ? "success" : "failure")) +
                       ",fault=" + (_publicationFault ?? "none") +
                       ",cancel=" + (_cancelledAccepted ? (_cancelIsEmergency ? "emergency" : "accepted") : "none") +
                       ",claim=" + (_closedAs.HasValue ? _closedAs.Value.ToString() : "open") +
                       ",detail=" + (_closedDetail ?? "none") +
                       ",producerSettled=" + (_producerSettled ? "true" : "false") +
                       ",retirementWake=" + (_retirementWakePosted ? "posted" : (_retirementWakeRequested ? "requested" : "none"));
            }
        }

        /// <summary>
        /// 只在 producer settled 且 claim 已关闭时才释放 ready 句柄。
        /// 绝不在 producer 仍可能 Set 时 Dispose。
        /// </summary>
        internal bool TryDisposeReadyHandle(out string error)
        {
            error = null;
            ManualResetEventSlim evt;
            lock (_sync)
            {
                if (_disposed) return true;
                if (!_producerSettled)
                {
                    error = "f1-ready-handle-producer-unsettled";
                    return false;
                }
                if (_observer != null && !_observer.IsCompleted)
                { error = "f1-ready-handle-observer-unsettled"; return false; }
                if (_signalers != 0 || _waiters != 0)
                { error = "f1-ready-handle-in-use"; return false; }
                if (!_closedAs.HasValue)
                { error = "f1-ready-handle-claim-open"; return false; }
                evt = _readyEvent;
                _readyEvent = null;
                _disposed = true;
            }
            if (evt != null)
            {
                try { evt.Dispose(); }
                catch (Exception ex) { error = "f1-ready-handle-dispose:" + ex.GetType().Name; return false; }
            }
            return true;
        }

        public void Dispose()
        {
            TryDisposeReadyHandle(out _);
        }
    }

    /// <summary>run 的证据台账（有序、有限、纯字符串）。</summary>
    internal sealed class FrameLocalDeliveryEvidence
    {
        private readonly object _sync = new object();
        private readonly List<string> _entries = new List<string>();
        private readonly long _startTicks = Stopwatch.GetTimestamp();
        private const int MaxEntries = 256;

        internal void Record(string stage, string detail)
        {
            lock (_sync)
            {
                if (_entries.Count >= MaxEntries) return;
                double ms = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
                _entries.Add("+" + ms.ToString("0.0", CultureInfo.InvariantCulture) + "ms t" +
                             Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) +
                             " " + (stage ?? "?") +
                             (string.IsNullOrEmpty(detail) ? "" : "=" + detail));
            }
        }

        internal string Dump()
        {
            lock (_sync) return string.Join(" | ", _entries.ToArray());
        }

        internal int Count { get { lock (_sync) return _entries.Count; } }
    }

    /// <summary>
    /// 受控 stdin 门：首次写入先登记 WriteGateEntered 并等待独立 Release，门打开后
    /// **真的** await 内层 WriteAsync / FlushAsync（ConfigureAwait(false)），外层 Task 覆盖
    /// 内层完整寿命。这是 managed hold，不是真实 pipe 饱和。
    /// </summary>
    internal sealed class FrameLocalDeliveryStreamGate : Stream
    {
        private readonly Stream _inner;
        private readonly TaskCompletionSource<bool> _entered = NewSignal();
        private readonly TaskCompletionSource<bool> _opened = NewSignal();
        private readonly object _lifetime = new object();
        private int _operations;
        private bool _disposed;
        private bool _innerDisposed;
        private string _disposeError;
        private int _firstWrite = 1;

        internal FrameLocalDeliveryStreamGate(Stream inner, FrameLocalDeliveryEvidence evidence)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Evidence = evidence;
        }

        internal FrameLocalDeliveryEvidence Evidence { get; private set; }
        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool GateEntered => _entered.Task.IsCompleted;
        internal bool WaitEntered(TimeSpan timeout) => _entered.Task.Wait(timeout);
        internal bool OwnershipSettled { get { lock (_lifetime) return _disposed && _innerDisposed && _operations == 0 && _disposeError == null; } }
        internal string DisposeError { get { lock (_lifetime) return _disposeError; } }

        /// <summary>打开前置门（正常 Release / cancel / failsafe 共用）。</summary>
        internal void Open(string reason)
        {
            if (_opened.TrySetResult(true)) Evidence?.Record("gate-open", reason);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            BeginOperation();
            try
            {
                if (Interlocked.Exchange(ref _firstWrite, 0) == 1)
                {
                    Evidence?.Record("gate-enter", "count=" + count.ToString(CultureInfo.InvariantCulture));
                    _entered.TrySetResult(true);
                    // Cancel only opens the managed hold. Once inner IO starts, its true terminal
                    // task (including Flush) owns the buffer; no token races can shorten it.
                    using (cancellationToken.Register(() => Open("write-cancelled")))
                        await _opened.Task.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                lock (_lifetime) if (_disposed) throw new ObjectDisposedException(nameof(FrameLocalDeliveryStreamGate));
                Evidence?.Record("inner-write-begin", "count=" + count.ToString(CultureInfo.InvariantCulture));
                await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                Evidence?.Record("inner-write-end", "ok");
                Evidence?.Record("inner-flush-begin", "write");
                await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                Evidence?.Record("inner-flush-end", "ok");
            }
            catch (Exception ex)
            {
                Evidence?.Record("inner-io-failed", ex.GetType().Name + ":" + ex.Message);
                throw;
            }
            finally { EndOperation(); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
            BeginOperation();
            try { _inner.Flush(); }
            finally { EndOperation(); }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            BeginOperation();
            try
            {
                Evidence?.Record("inner-flush-begin", "pipeline");
                await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                Evidence?.Record("inner-flush-end", "pipeline-ok");
            }
            catch (Exception ex) { Evidence?.Record("inner-flush-failed", ex.GetType().Name); throw; }
            finally { EndOperation(); }
        }

        private void BeginOperation()
        {
            lock (_lifetime)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(FrameLocalDeliveryStreamGate));
                _operations++;
            }
        }
        private void EndOperation() { lock (_lifetime) _operations--; }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { lock (_lifetime) return !_disposed && _inner.CanWrite; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lifetime)
                {
                    if (_disposed) return;
                    _disposed = true;
                }
                Open("stream-disposed");
                // L2 may tear down before the frame task. Close the real pipe exactly once,
                // without waiting on the writer. The outer async task still awaits inner IO.
                try { _inner.Dispose(); lock (_lifetime) _innerDisposed = true; }
                catch (Exception ex) { lock (_lifetime) _disposeError = ex.GetType().Name + ":" + ex.Message; throw; }
                finally { Evidence?.Record("stdin-disposed", DisposeError ?? "inner-closed"); }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 外部 guardian 的 descriptor。只有它为**指定游戏 PID/启动时间 + 一次性 nonce**建立
    /// 有效记录并完成握手，本次诊断才可能启用。
    /// </summary>
    internal sealed class FrameLocalDeliveryProbeDescriptor
    {
        internal string RunNonce { get; private set; }
        internal int GameProcessId { get; private set; }
        internal long GameStartUtcTicks { get; private set; }
        internal string EventPrefix { get; private set; }
        internal int ExpirySeconds { get; private set; }

        internal bool IsExpired(DateTime utcNow)
        {
            return ExpirySeconds > 0 && utcNow > DateTime.UtcNow;
        }

        /// <summary>解析 `key=value` 行；任何缺项都 fail-closed。</summary>
        internal static bool TryParse(string text, DateTime utcNow, out FrameLocalDeliveryProbeDescriptor descriptor,
            out string error)
        {
            descriptor = null;
            error = null;
            if (string.IsNullOrEmpty(text)) { error = "f1-descriptor-empty"; return false; }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            string nonce, pidText, startText, prefix, expiryText;
            if (!values.TryGetValue("runNonce", out nonce) || string.IsNullOrEmpty(nonce))
            { error = "f1-descriptor-nonce"; return false; }
            if (!values.TryGetValue("gamePid", out pidText))
            { error = "f1-descriptor-pid"; return false; }
            if (!values.TryGetValue("gameStartUtcTicks", out startText))
            { error = "f1-descriptor-start"; return false; }
            if (!values.TryGetValue("eventPrefix", out prefix) || string.IsNullOrEmpty(prefix))
            { error = "f1-descriptor-prefix"; return false; }
            values.TryGetValue("expirySeconds", out expiryText);

            int pid;
            if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid <= 0)
            { error = "f1-descriptor-pid-invalid"; return false; }
            long start;
            if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start <= 0)
            { error = "f1-descriptor-start-invalid"; return false; }
            int expiry = 0;
            if (!string.IsNullOrEmpty(expiryText))
            {
                int.TryParse(expiryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out expiry);
            }

            descriptor = new FrameLocalDeliveryProbeDescriptor
            {
                RunNonce = nonce,
                GameProcessId = pid,
                GameStartUtcTicks = start,
                EventPrefix = prefix,
                ExpirySeconds = expiry,
            };
            return true;
        }
    }

    /// <summary>
    /// 单 run 的 probe 槽：把 claim gate 与 run nonce / f0 身份绑定，并把"本帧是否由 probe 接管"
    /// 暴露给 scheduler。它不是第二套 scheduler：不推进时间、不发授权、不创建 pipeline。
    /// </summary>
    internal sealed class FrameLocalDeliveryProbeSlot
    {
        private readonly object _sync = new object();
        private readonly FrameLocalDeliveryEvidence _evidence;
        private readonly string _nonce;
        private FrameLocalDeliveryClaimGate _gate;
        private long _generation = -1;
        private long _absoluteFrameIndex = -1;
        private long _tokenId = -1;
        private bool _bound;
        private string _pendingCancel;
        private bool _pendingEmergency;

        internal FrameLocalDeliveryProbeSlot(string nonce, FrameLocalDeliveryEvidence evidence)
        {
            _nonce = nonce ?? string.Empty;
            _evidence = evidence;
        }

        internal string RunNonce { get { return _nonce; } }
        internal FrameLocalDeliveryEvidence Evidence { get { return _evidence; } }

        internal bool IsBound { get { lock (_sync) return _bound; } }
        internal bool IsAwaitingBinding { get { lock (_sync) return !_bound && _pendingCancel == null; } }

        internal FrameLocalDeliveryClaimGate Gate { get { lock (_sync) return _gate; } }

        internal long BoundGeneration { get { lock (_sync) return _generation; } }
        internal long BoundFrameIndex { get { lock (_sync) return _absoluteFrameIndex; } }
        internal long BoundTokenId { get { lock (_sync) return _tokenId; } }

        /// <summary>绑定本帧（f0）与这个通知槽的 gate。只允许一次，且只允许 frame 0。</summary>
        internal bool TryBind(FrameLocalDeliveryClaimGate gate, long generation, long absoluteFrameIndex,
            long tokenId, out string error)
        {
            error = null;
            if (gate == null) { error = "f1-bind-null-gate"; return false; }
            if (absoluteFrameIndex != 0L) { error = "f1-bind-not-frame-zero"; return false; }
            lock (_sync)
            {
                if (_bound) { error = "f1-bind-already-bound"; return false; }
                if (_pendingCancel != null) { error = "f1-bind-cancelled:" + _pendingCancel; return false; }
                if (gate.Generation != generation || gate.AbsoluteFrameIndex != absoluteFrameIndex || gate.TokenId != tokenId)
                { error = "f1-bind-identity-mismatch"; return false; }
                _gate = gate;
                _generation = generation;
                _absoluteFrameIndex = absoluteFrameIndex;
                _tokenId = tokenId;
                _bound = true;
            }
            _evidence?.Record("slot-bind", "gen=" + generation.ToString(CultureInfo.InvariantCulture) +
                                           " token=" + tokenId.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        internal bool IsArmedForFrame(long generation, long absoluteFrameIndex)
        {
            lock (_sync)
            {
                return _bound && _generation == generation && _absoluteFrameIndex == absoluteFrameIndex &&
                       _absoluteFrameIndex == 0L;
            }
        }

        internal bool TryAcceptCancel(string reason, bool emergency, out string error)
        {
            FrameLocalDeliveryClaimGate gate;
            lock (_sync)
            {
                gate = _gate;
                if (gate == null)
                {
                    _pendingCancel = _pendingCancel ?? reason ?? "f1-cancel-before-bind";
                    _pendingEmergency |= emergency;
                    error = null;
                    return true; // authorized but not bound: cancellation is sticky, never discarded
                }
            }
            bool accepted = gate.TryAcceptCancel(reason, emergency, out error);
            _evidence?.Record(accepted ? "cancel-accepted" : "cancel-rejected",
                (emergency ? "emergency:" : "soft:") + (reason ?? "") + ":" + (error ?? "ok"));
            return accepted;
        }

        internal WaitHandle[] BuildWaitHandles(EventWaitHandle emergency)
        {
            var handles = new List<WaitHandle>();
            WaitHandle ready = Gate?.ReadyHandle;
            if (ready != null) handles.Add(ready);
            if (emergency != null) handles.Add(emergency);
            return handles.ToArray();
        }

        // The actual EOF control path, shared by the Unity adapter and managed integration tests.
        // Callbacks retain scheduler authority: this helper never prepares frames or owns logical time.
        internal void RunSingleFrame(Rgb24FrameTransaction transaction, long generation, long frameIndex,
            bool awaitingDelivery, EventWaitHandle emergency, int budgetMs,
            Action commit, Action<string, string> stop)
        {
            FrameLocalDeliveryClaimGate gate = Gate;
            if (!IsArmedForFrame(generation, frameIndex) || gate == null || !gate.DirectRoute ||
                transaction.Generation != generation || transaction.FrameIndex != frameIndex ||
                transaction.LeaseTokenId != BoundTokenId)
            {
                stop("f1-binding-invalid", "f1-no-async-fallback");
                return;
            }
            try
            {
                _evidence?.Record("waiting-armed", "frame=" + frameIndex.ToString(CultureInfo.InvariantCulture));
                int index = gate.WaitForWake(BuildWaitHandles(emergency), budgetMs);
                _evidence?.Record("wait-exit", "index=" + index.ToString(CultureInfo.InvariantCulture));
                if (index == WaitHandle.WaitTimeout || (emergency != null && emergency.WaitOne(0)))
                    TryAcceptCancel("f1-emergency-or-wait-timeout", true, out _);

                gate.TryGetPublished(out var envelope);
                if (gate.CancelAccepted)
                {
                    gate.TryClaim(envelope); // may be null while raw completion is still pending
                    stop("f1-single-frame-cancelled", "f1-cancel-accepted-before-claim");
                    return;
                }
                if (gate.Failure != null || envelope == null)
                {
                    stop("f1-no-canonical-envelope", gate.Failure ?? "f1-published-missing");
                    return;
                }
                var outcome = transaction.ConsumeEnvelope(awaitingDelivery, envelope, out var code, out var detail);
                _evidence?.Record("consume-result", outcome + ":" + (code ?? "ok") + ":" + (detail ?? ""));
                if (outcome == Rgb24TransactionOutcome.Committed)
                {
                    // A late cancel cannot rewrite the permit acquired inside ConsumeEnvelope.
                    try { commit(); }
                    catch (Exception ex)
                    {
                        stop("f1-commit-exception", ex.GetType().Name + ":" + ex.Message);
                        return;
                    }
                    stop("cancelled", "f1-single-frame-complete");
                    return;
                }
                stop("f1-single-frame-not-committed", code ?? outcome.ToString());
            }
            catch (Exception ex)
            {
                TryAcceptCancel("f1-wait-exception", true, out _);
                stop("f1-wait-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        internal string Describe()
        {
            FrameLocalDeliveryClaimGate gate;
            lock (_sync) gate = _gate;
            return "nonce=" + _nonce + ",bound=" + (_bound ? "true" : "false") +
                   ",pendingCancel=" + (_pendingCancel ?? "none") + ",pendingEmergency=" + _pendingEmergency +
                   (gate == null ? "" : " gate[" + gate.Describe() + "]");
        }
    }

    /// <summary>
    /// 专用 monitor 线程：阻塞等待外部 guardian 的命名事件与诊断 deadline，
    /// 把请求写入纯托管 slot 并唤醒 waiter。**不接触** Unity / scheduler / lease / context。
    /// </summary>
    internal sealed class FrameLocalDeliveryProbeMonitor : IDisposable
    {
        private readonly FrameLocalDeliveryProbeSlot _slot;
        private readonly FrameLocalDeliveryEvidence _evidence;
        private readonly EventWaitHandle _release;
        private readonly EventWaitHandle _cancel;
        private readonly EventWaitHandle _shutdown;
        private readonly EventWaitHandle _emergency;
        private readonly EventWaitHandle _ack;
        private readonly Action<string> _openStreamGate;
        private readonly int _softAbortMs;
        private readonly int _emergencyMs;
        private readonly Thread _thread;
        private volatile bool _stop;
        private int _exited;

        internal FrameLocalDeliveryProbeMonitor(FrameLocalDeliveryProbeSlot slot, FrameLocalDeliveryEvidence evidence,
            EventWaitHandle release, EventWaitHandle cancel, EventWaitHandle shutdown, EventWaitHandle emergency,
            EventWaitHandle ack, Action<string> openStreamGate, int softAbortMs, int emergencyMs)
        {
            _slot = slot;
            _evidence = evidence;
            _release = release;
            _cancel = cancel;
            _shutdown = shutdown;
            _emergency = emergency;
            _ack = ack;
            _openStreamGate = openStreamGate;
            _softAbortMs = softAbortMs;
            _emergencyMs = emergencyMs;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "Renderist.F1.Monitor";
        }

        internal int ThreadId { get { return _thread.ManagedThreadId; } }
        internal bool Exited { get { return Volatile.Read(ref _exited) != 0; } }

        internal void Start()
        {
            _thread.Start();
            _evidence?.Record("monitor-start", "tid=" + _thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture));
        }

        private void Loop()
        {
            try
            {
                // Control events precede Release. After its first hit remove Release permanently;
                // Reset alone could still be defeated by a producer repeatedly setting the MRE.
                var controls = new WaitHandle[] { _emergency, _shutdown, _cancel };
                var withRelease = new WaitHandle[] { _emergency, _shutdown, _cancel, _release };
                var started = Stopwatch.StartNew();
                bool softAbortIssued = false;
                bool releaseHandled = false;

                while (!_stop)
                {
                    int wait = 200;
                    int elapsed = (int)started.ElapsedMilliseconds;
                    if (!softAbortIssued && _softAbortMs > 0 && elapsed >= _softAbortMs) wait = 0;
                    else if (!softAbortIssued && _softAbortMs > 0) wait = Math.Max(1, Math.Min(200, _softAbortMs - elapsed));

                    int index = WaitHandle.WaitAny(releaseHandled ? controls : withRelease, wait);
                    if (index != WaitHandle.WaitTimeout)
                        _evidence?.Record("monitor-wake", "index=" + index.ToString(CultureInfo.InvariantCulture));

                    if (index == 3)
                    {
                        releaseHandled = true;
                        _openStreamGate?.Invoke("guardian-release");
                        SignalAck("release");
                        continue;   // Release 只处理一次；继续观察 cancel/emergency/shutdown
                    }
                    if (index == 2)
                    {
                        string error;
                        bool accepted = _slot.TryAcceptCancel("guardian-cancel", false, out error);
                        SignalAck(accepted ? "cancel-accepted" : "cancel-rejected");
                        _openStreamGate?.Invoke("guardian-cancel");
                        return; // also retire a rejected/late control; never spin on its sticky event
                    }
                    if (index == 1)
                    {
                        _slot.TryAcceptCancel("guardian-shutdown", false, out _);
                        SignalAck("shutdown");
                        _openStreamGate?.Invoke("guardian-shutdown");
                        return;
                    }
                    if (index == 0)
                    {
                        string error;
                        _slot.TryAcceptCancel("guardian-emergency", true, out error);
                        SignalAck("emergency");
                        _openStreamGate?.Invoke("guardian-emergency");
                        return;
                    }

                    // timeout：只在 PoC 保命预算内触发 soft abort（不是产品 IO 期限）。
                    if (!softAbortIssued && _softAbortMs > 0 && started.ElapsedMilliseconds >= _softAbortMs)
                    {
                        softAbortIssued = true;
                        string error;
                        _slot.TryAcceptCancel("f1-soft-abort-deadline", false, out error);
                        _evidence?.Record("soft-abort", "budget-ms=" + _softAbortMs.ToString(CultureInfo.InvariantCulture));
                        _openStreamGate?.Invoke("soft-abort");
                    }
                }
            }
            catch (Exception ex)
            {
                _evidence?.Record("monitor-exception", ex.GetType().Name);
                try { _slot.TryAcceptCancel("f1-monitor-exception", true, out _); } catch (Exception) { }
            }
            finally
            {
                Interlocked.Exchange(ref _exited, 1);
                _evidence?.Record("monitor-exit", "");
            }
        }

        private void SignalAck(string what)
        {
            try
            {
                if (_ack != null)
                {
                    _ack.Set();
                    _evidence?.Record("ack", what);
                }
            }
            catch (Exception ex)
            {
                _evidence?.Record("ack-failed", ex.GetType().Name);
            }
        }

        internal void RequestStop()
        {
            _stop = true;
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_thread.IsAlive) _thread.Join(5000); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// F1-A 诊断 session 的**唯一**入口。默认关闭：只有外部 guardian 提供有效 descriptor
    /// （指定游戏 PID/启动时间 + 一次性 run nonce）并完成命名事件握手后，本次导出才被接管。
    ///
    /// 没有 descriptor 时行为与既有产品路径**完全相同**（AsyncPosted）。descriptor 存在但
    /// 失效/过期/握手失败时，本次诊断被拒绝（fail-closed），绝不悄悄回退成普通 MP4 导出。
    /// </summary>
    internal static class FrameLocalDeliveryProbe
    {
        internal const string DescriptorPathVariable = "RENDERIST_F1_PROBE_FILE";

        /// <summary>PoC 保命预算（仅诊断；不是产品 IO 合法性期限）。</summary>
        internal const int SoftAbortMs = 5000;
        internal const int EmergencyMs = 10000;

        private static readonly object _sync = new object();
        private static FrameLocalDeliveryEvidence _evidence;
        private static FrameLocalDeliveryProbeSlot _slot;
        private static FrameLocalDeliveryProbeMonitor _monitor;
        private static FrameLocalDeliveryStreamGate _streamGate;
        private static EventWaitHandle _ready;
        private static EventWaitHandle _ack;
        private static EventWaitHandle _release;
        private static EventWaitHandle _cancel;
        private static EventWaitHandle _shutdown;
        private static EventWaitHandle _emergency;
        private static string _nonce;
        private static int _encoderStarts;
        private static int _encoderPid;
        private static long _encoderStartUtcTicks;
        private static string _encoderPath;
        private static string _lastError;

        /// <summary>本进程是否已被 F1 诊断接管。</summary>
        internal static bool IsActive { get { lock (_sync) return _slot != null; } }

        internal static FrameLocalDeliveryProbeSlot Slot { get { lock (_sync) return _slot; } }

        internal static FrameLocalDeliveryEvidence Evidence { get { lock (_sync) return _evidence; } }

        internal static string DescriptorPath
        {
            get
            {
                try { return Environment.GetEnvironmentVariable(DescriptorPathVariable); }
                catch (Exception) { return null; }
            }
        }

        /// <summary>
        /// 加载 descriptor 并完成握手。必须在创建 L2 pipeline 之前调用一次。
        /// 返回 false 时 <paramref name="error"/> 给出具体原因，调用方必须 fail-closed 本次诊断。
        /// </summary>
        internal static bool TryActivate(out string error)
        {
            error = null;
            lock (_sync)
            {
                if (_slot != null) return true;
            }

            var evidence = new FrameLocalDeliveryEvidence();
            string path = DescriptorPath;
            if (string.IsNullOrEmpty(path))
            {
                error = "f1-descriptor-absent";
                return false;
            }

            string text;
            try
            {
                if (!File.Exists(path)) { error = "f1-descriptor-missing"; return false; }
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = "f1-descriptor-unreadable:" + ex.GetType().Name;
                return false;
            }

            FrameLocalDeliveryProbeDescriptor descriptor;
            if (!FrameLocalDeliveryProbeDescriptor.TryParse(text, DateTime.UtcNow, out descriptor, out error))
                return false;

            evidence.Record("descriptor-loaded", "nonce=" + descriptor.RunNonce);

            int pid;
            long startTicks;
            if (!TryReadCurrentProcessIdentity(out pid, out startTicks, out error)) return false;
            if (pid != descriptor.GameProcessId)
            {
                error = "f1-descriptor-pid-mismatch:expected=" +
                        descriptor.GameProcessId.ToString(CultureInfo.InvariantCulture) + ",actual=" +
                        pid.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (startTicks != descriptor.GameStartUtcTicks)
            {
                error = "f1-descriptor-start-mismatch";
                return false;
            }

            string prefix = "Local\\" + descriptor.EventPrefix + "." + descriptor.RunNonce;
            EventWaitHandle ready = null, ack = null, release = null, cancel = null, shutdown = null, emergency = null;
            try
            {
                ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Ready");
                ack = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Ack");
                release = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Release");
                cancel = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Cancel");
                shutdown = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Shutdown");
                emergency = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Emergency");
            }
            catch (Exception ex)
            {
                CloseHandles(ready, ack, release, cancel, shutdown, emergency);
                error = "f1-events-unavailable:" + ex.GetType().Name;
                return false;
            }

            // 双向握手：runtime 先 Ready，guardian 必须回 Ack。
            try
            {
                ready.Set();
                if (!ack.WaitOne(SoftAbortMs))
                {
                    CloseHandles(ready, ack, release, cancel, shutdown, emergency);
                    error = "f1-handshake-timeout";
                    return false;
                }
            }
            catch (Exception ex)
            {
                CloseHandles(ready, ack, release, cancel, shutdown, emergency);
                error = "f1-handshake-failed:" + ex.GetType().Name;
                return false;
            }

            var slot = new FrameLocalDeliveryProbeSlot(descriptor.RunNonce, evidence);
            var monitor = new FrameLocalDeliveryProbeMonitor(slot, evidence, release, cancel, shutdown, emergency,
                ack, reason => OpenStreamGate(reason), SoftAbortMs, EmergencyMs);

            lock (_sync)
            {
                _evidence = evidence;
                _slot = slot;
                _monitor = monitor;
                _ready = ready;
                _ack = ack;
                _release = release;
                _cancel = cancel;
                _shutdown = shutdown;
                _emergency = emergency;
                _nonce = descriptor.RunNonce;
                _encoderStarts = 0;
                _encoderPid = 0;
                _encoderStartUtcTicks = 0;
                _encoderPath = null;
            }

            monitor.Start();
            evidence.Record("probe-active", "monitor-tid=" + monitor.ThreadId.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        private static bool TryReadCurrentProcessIdentity(out int pid, out long startTicks, out string error)
        {
            pid = 0;
            startTicks = 0;
            error = null;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    pid = process.Id;
                    startTicks = process.StartTime.ToUniversalTime().Ticks;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "f1-process-identity-unavailable:" + ex.GetType().Name;
                return false;
            }
        }

        // ---------------------------------------------------------------- stdin / encoder 缝

        /// <summary>把 L2 的 stdin 包装成受控门（只有 probe 激活时才包装）。</summary>
        internal static Stream WrapStandardInput(Stream inner)
        {
            if (inner == null) return null;
            FrameLocalDeliveryEvidence evidence;
            lock (_sync) evidence = _evidence;
            var gate = new FrameLocalDeliveryStreamGate(inner, evidence);
            lock (_sync) _streamGate = gate;
            evidence?.Record("stdin-gate-installed", "");
            return gate;
        }

        internal static FrameLocalDeliveryStreamGate StreamGate { get { lock (_sync) return _streamGate; } }

        private static void OpenStreamGate(string reason)
        {
            FrameLocalDeliveryStreamGate gate;
            lock (_sync) gate = _streamGate;
            gate?.Open(reason);
        }

        /// <summary>
        /// 包装 `Options.ProcessStart`：只观测并记录**唯一** encoder 的身份，
        /// 不改编码参数、不接管 Dispose、不创建第二个进程。
        /// </summary>
        internal static FfmpegVideoProcessStart WrapProcessStart(FfmpegVideoProcessStart inner)
        {
            return (executablePath, arguments, workingDirectory) =>
            {
                Process process = inner(executablePath, arguments, workingDirectory);
                if (process == null) return null;

                FrameLocalDeliveryEvidence evidence;
                int count;
                lock (_sync)
                {
                    _encoderStarts++;
                    count = _encoderStarts;
                    _encoderPid = process.Id;
                    try { _encoderStartUtcTicks = process.StartTime.ToUniversalTime().Ticks; }
                    catch (Exception) { _encoderStartUtcTicks = 0; }
                    _encoderPath = executablePath;
                    evidence = _evidence;
                }

                evidence?.Record("encoder-start", "count=" + count.ToString(CultureInfo.InvariantCulture) +
                                                   " pid=" + process.Id.ToString(CultureInfo.InvariantCulture) +
                                                   " path=" + (executablePath ?? "?"));
                if (count > 1)
                {
                    evidence?.Record("encoder-second-start", "f1 forbids a second encoder start");
                    lock (_sync) _lastError = "f1-second-encoder-start";
                }
                return process;
            };
        }

        internal static int EncoderStartCount { get { lock (_sync) return _encoderStarts; } }

        /// <summary>
        /// 只终止**经身份核实**的本 run encoder。禁止按进程名批量 kill、禁止终止游戏。
        /// </summary>
        internal static bool TryTerminateEncoder(out string error)
        {
            error = null;
            int pid;
            long expectedStart;
            string path;
            FrameLocalDeliveryEvidence evidence;
            lock (_sync)
            {
                pid = _encoderPid;
                expectedStart = _encoderStartUtcTicks;
                path = _encoderPath;
                evidence = _evidence;
            }

            if (pid <= 0 || string.IsNullOrEmpty(path))
            {
                error = "f1-encoder-not-identified";
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.HasExited)
                    {
                        evidence?.Record("encoder-kill", "already-exited pid=" + pid.ToString(CultureInfo.InvariantCulture));
                        return true;
                    }
                    long actualStart = process.StartTime.ToUniversalTime().Ticks;
                    if (expectedStart != 0 && actualStart != expectedStart)
                    {
                        error = "f1-encoder-identity-mismatch";
                        return false;
                    }
                    string actualPath = null;
                    try { actualPath = process.MainModule == null ? null : process.MainModule.FileName; }
                    catch (Exception) { }
                    if (!string.IsNullOrEmpty(actualPath) &&
                        !string.Equals(actualPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "f1-encoder-path-mismatch";
                        return false;
                    }

                    process.Kill();
                    bool exited = process.WaitForExit(5000);
                    evidence?.Record("encoder-kill", "pid=" + pid.ToString(CultureInfo.InvariantCulture) +
                                                     " exited=" + (exited ? "true" : "false"));
                    if (!exited)
                    {
                        error = "f1-encoder-kill-timeout";
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "f1-encoder-kill-failed:" + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// 生产默认进程启动（与 FfmpegVideoPipeline.StartDefaultProcess 参数完全一致）的等价实现。
        /// 只有在 probe 需要观测 encoder 身份、而调用方未提供 ProcessStart 缝时才安装；
        /// 它**不改变**编码参数，也不接管 Process 的 Dispose。
        /// </summary>
        internal static Process StartObservedDefaultProcess(string executablePath, string arguments,
            string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
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

        // ---------------------------------------------------------------- 取消入口

        /// <summary>软取消（可由 monitor 或 M 触发）；返回是否在 claim 之前被接受。</summary>
        internal static bool TryRequestCancel(string reason, out string error)
        {
            FrameLocalDeliveryProbeSlot slot;
            lock (_sync) slot = _slot;
            if (slot == null) { error = "f1-inactive"; return false; }
            return slot.TryAcceptCancel(reason, false, out error);
        }

        /// <summary>紧急中止（guardian 直接 Set 命名事件时，M 在 claim 前写入同一 slot）。</summary>
        internal static bool TryEmergencyAbort(string reason, out string error)
        {
            FrameLocalDeliveryProbeSlot slot;
            lock (_sync) slot = _slot;
            if (slot == null) { error = "f1-inactive"; return false; }
            return slot.TryAcceptCancel(reason, true, out error);
        }

        /// <summary>M 的等待集合：ready + guardian emergency。monitor 失效时 emergency 仍可唤醒。</summary>
        internal static WaitHandle[] BuildWaitHandles()
        {
            lock (_sync)
            {
                return _slot == null ? new WaitHandle[0] : _slot.BuildWaitHandles(_emergency);
            }
        }

        internal static EventWaitHandle EmergencyHandle { get { lock (_sync) return _emergency; } }

        internal static bool EmergencyRequested()
        {
            try
            {
                EventWaitHandle handle;
                lock (_sync) handle = _emergency;
                return handle != null && handle.WaitOne(0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- settlement

        /// <summary>
        /// 诊断资源是否全部收敛：monitor 退出、producer settled、claim 关闭、
        /// ready 句柄已安全释放、stdin 门已开、无第二个 encoder、encoder 已退出。
        /// </summary>
        internal static bool TryObserveSettlement(out string error)
        {
            error = null;
            FrameLocalDeliveryProbeSlot slot;
            FrameLocalDeliveryProbeMonitor monitor;
            FrameLocalDeliveryStreamGate streamGate;
            FrameLocalDeliveryEvidence evidence;
            int encoderStarts, encoderPid;
            lock (_sync)
            {
                slot = _slot;
                monitor = _monitor;
                streamGate = _streamGate;
                evidence = _evidence;
                encoderStarts = _encoderStarts;
                encoderPid = _encoderPid;
            }

            var problems = new List<string>();
            if (slot != null)
            {
                FrameLocalDeliveryClaimGate gate = slot.Gate;
                if (gate != null)
                {
                    if (!gate.ProducerSettled) problems.Add("producer-unsettled");
                    if (!gate.ObserverSettled) problems.Add("observer-unsettled");
                    if (!gate.IsClosed) problems.Add("claim-still-open");
                    string disposeError;
                    if (!gate.TryDisposeReadyHandle(out disposeError))
                        problems.Add("ready-handle:" + (disposeError ?? "unknown"));
                }
                else
                {
                    problems.Add("gate-missing");
                }
            }

            if (monitor != null && !monitor.Exited) problems.Add("monitor-not-exited");

            if (streamGate != null && !streamGate.GateEntered) problems.Add("stream-gate-never-entered");

            if (encoderStarts > 1) problems.Add("second-encoder-start");

            if (encoderPid > 0)
            {
                try
                {
                    using (Process process = Process.GetProcessById(encoderPid))
                    {
                        if (!process.HasExited) problems.Add("encoder-still-running");
                    }
                }
                catch (ArgumentException)
                {
                    // 进程已不存在：已收敛。
                }
                catch (Exception ex)
                {
                    problems.Add("encoder-probe:" + ex.GetType().Name);
                }
            }

            if (problems.Count > 0)
            {
                error = string.Join(";", problems.ToArray());
                evidence?.Record("settlement", "FAIL:" + error);
                return false;
            }

            evidence?.Record("settlement", "clean");
            return true;
        }

        internal static string Describe()
        {
            lock (_sync)
            {
                return "active=" + (_slot != null ? "true" : "false") +
                       ",nonce=" + (_nonce ?? "none") +
                       ",encoderStarts=" + _encoderStarts.ToString(CultureInfo.InvariantCulture) +
                       ",encoderPid=" + _encoderPid.ToString(CultureInfo.InvariantCulture) +
                       ",error=" + (_lastError ?? "none") +
                       (_slot == null ? "" : " slot[" + _slot.Describe() + "]");
            }
        }

        internal static string DumpEvidence()
        {
            lock (_sync) return _evidence == null ? "no-evidence" : _evidence.Dump();
        }

        /// <summary>诊断 session 收尾：停 monitor、放行 stdin 门、关闭命名事件。</summary>
        internal static void Shutdown()
        {
            FrameLocalDeliveryProbeMonitor monitor;
            lock (_sync)
            {
                monitor = _monitor;
                _monitor = null;
            }
            if (monitor != null)
            {
                TryRequestCancel("probe-shutdown", out _);
                monitor.RequestStop();
                monitor.Dispose();
            }
            OpenStreamGateRethrowSafe("probe-shutdown");

            lock (_sync)
            {
                CloseHandles(_ready, _ack, _release, _cancel, _shutdown, _emergency);
                _ready = _ack = _release = _cancel = _shutdown = _emergency = null;
            }
        }

        private static void OpenStreamGateRethrowSafe(string reason)
        {
            try { OpenStreamGate(reason); }
            catch (Exception) { }
        }

        private static void CloseHandles(params EventWaitHandle[] handles)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] == null) continue;
                try { handles[i].Dispose(); }
                catch (Exception) { }
            }
        }
    }
}
