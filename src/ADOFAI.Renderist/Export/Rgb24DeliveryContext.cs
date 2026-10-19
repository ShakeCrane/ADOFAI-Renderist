using System;
using System.Globalization;
using System.Threading.Tasks;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.Export
{
    // 只暴露已有 L2 的取消、计数、完成与资源收敛证据；不创建或接管第二个 process。
    internal interface IRgb24PipelineLifetime
    {
        void RequestStop(string reason);

        /// <summary>
        /// **完整写入成功**的帧数（L2 自己的计数，也是核验使用的期望帧数）。
        /// 这里只读它做一致性交叉核对，**不**维护第二套计数 authority。
        /// </summary>
        long DeliveredFrameCount { get; }

        /// <summary>
        /// 让**同一个** L2 管线结束输入并等待产物就绪（既有 <c>FinishAsync</c> 协议）。
        /// 绝不创建第二个 FFmpeg pipeline / 第二个编码进程。
        /// 返回 null 表示连"开始 Finalizing"都无法成立（调用方按 fail-closed 处理）。
        /// </summary>
        Task<FfmpegVideoOutcome> BeginFinalize();

        Task<FfmpegVideoOutcome> CleanupTask { get; }
    }

    /// <summary>
    /// 开始 Finalizing 的结果。只有 <see cref="Started"/> 为 true 才代表 L2 已受理本次
    /// 完成关口；被拒绝时调用方必须 fail-closed（绝不把未受理的结果当作发布依据）。
    /// </summary>
    internal sealed class Rgb24FinalizeStart
    {
        private Rgb24FinalizeStart()
        {
        }

        internal bool Started { get; private set; }
        internal Task<FfmpegVideoOutcome> Completion { get; private set; }
        internal string ErrorCode { get; private set; }
        internal string ErrorDetail { get; private set; }

        internal static Rgb24FinalizeStart Accept(Task<FfmpegVideoOutcome> completion)
        {
            return new Rgb24FinalizeStart { Started = true, Completion = completion };
        }

        internal static Rgb24FinalizeStart Reject(string errorCode, string errorDetail)
        {
            return new Rgb24FinalizeStart { ErrorCode = errorCode, ErrorDetail = errorDetail };
        }
    }

    internal sealed class Rgb24DeliveryContext
    {
        private readonly IRgb24PipelineLifetime _lifetime;
        private bool _stopping;
        private bool _finalizeStarted;
        private Task<FfmpegVideoOutcome> _finalizeCompletion;
        internal Rgb24FrameLayout Layout { get; }
        internal Rgb24FrameBufferPool Pool { get; }
        internal Rgb24MainThreadBridge Bridge { get; }
        internal Rgb24FrameTransaction Transaction { get; }

        internal Rgb24DeliveryContext(Rgb24FrameLayout layout, IRgb24FrameTransport transport,
            IRgb24PipelineLifetime lifetime, Rgb24MainThreadBridge bridge)
        {
            Layout = layout ?? throw new ArgumentNullException("layout");
            _lifetime = lifetime ?? throw new ArgumentNullException("lifetime");
            Bridge = bridge ?? throw new ArgumentNullException("bridge");
            Pool = new Rgb24FrameBufferPool(layout);
            Transaction = new Rgb24FrameTransaction(Pool, transport, bridge);
        }

        internal static bool TryCreate(int outputWidth, int outputHeight, FfmpegVideoPipeline pipeline,
            out Rgb24DeliveryContext context, out string error)
        {
            context = null;
            if (!Rgb24FrameLayout.TryCreate(outputWidth, outputHeight, out var layout, out error)) return false;
            if (!Rgb24MainThreadBridge.TryCapture(out var bridge, out error)) return false;
            if (pipeline == null)
            {
                error = "rgb24-pipeline-missing";
                return false;
            }
            var transport = new FfmpegRgb24FrameTransport(pipeline);
            context = new Rgb24DeliveryContext(layout, transport, transport, bridge);
            return true;
        }

        internal bool TryTakePendingEnvelope(out Rgb24DeliveryEnvelope envelope, out string bridgeError)
        {
            return Transaction.TryTakePendingEnvelope(out envelope, out bridgeError);
        }

        // frame Completion 与 CleanupTask 分别检查。null CleanupTask 表示 L2 尚未开始终态收敛。
        internal bool HasFrameOwnership => Transaction.HasResidualOwnership || Pool.HasOutstandingLease;
        internal bool HasPipelineOwnership
        {
            get
            {
                Task<FfmpegVideoOutcome> task = _lifetime.CleanupTask;
                if (task == null || !task.IsCompleted) return true;
                if (task.IsFaulted) { var observed = task.Exception; return true; }
                return task.IsCanceled || task.Result == null || task.Result.ResidualOwnership;
            }
        }
        internal bool HasResidualOwnership => HasFrameOwnership || HasPipelineOwnership;
        internal bool Stopping => _stopping;
        internal bool PipelineCleanupSettled => _lifetime.CleanupTask != null && _lifetime.CleanupTask.IsCompleted;
        internal string PipelineFailureDiagnostics =>
            (_lifetime as FfmpegRgb24FrameTransport)?.FailureDiagnostics ?? "unavailable";

        /// <summary>本 context 是否已经受理过 Finalizing（一次性闸门）。</summary>
        internal bool FinalizeStarted => _finalizeStarted;

        /// <summary>已受理的 Finalizing 完成通知；未受理时为 null。</summary>
        internal Task<FfmpegVideoOutcome> FinalizeCompletion => _finalizeCompletion;

        /// <summary>
        /// L3-C：在 Unity 主线程、且**全部帧交付已结束**之后，用同一个 L2 管线开始 Finalizing
        /// （关闭 stdin → 等待进程退出与双流 EOF → 独立核验 → 同卷原子发布）。
        ///
        /// 一次性：同一 context 只允许一次。这里**不**复制 L2 状态机，只转交一次调用与它的
        /// 完成通知；发布与否始终由 L2 自己的终态仲裁决定。
        ///
        /// <paramref name="expectedFrameCount"/> 是 session 侧的逻辑提交帧数：它必须精确等于 L2
        /// 已完整写入的帧数，否则**在调用 FinishAsync 之前**就 fail-closed —— 因为 L2 会把
        /// <see cref="IRgb24PipelineLifetime.DeliveredFrameCount"/> 当作核验的期望帧数，
        /// 静默不一致会让产物与 metadata 互相矛盾。这里只做交叉核对，不新增计数 authority。
        ///
        /// 任一前提不成立即拒绝并 fail-closed；当 L2 拒绝受理且没有建立收敛任务时，
        /// 主动请求取消，使该 context 仍能经既有 Cancel / CleanupTask 路径收敛，
        /// 绝不留下"既不能 Finalize、也无法回收"的悬空 ownership。
        /// </summary>
        internal Rgb24FinalizeStart TryBeginFinalizing(long expectedFrameCount)
        {
            if (!Bridge.IsMainThread)
                return Rgb24FinalizeStart.Reject("rgb24-wrong-main-thread", null);
            if (_stopping)
                return Rgb24FinalizeStart.Reject("rgb24-context-stopping", null);
            if (_finalizeStarted)
                return Rgb24FinalizeStart.Reject("rgb24-finalize-already-started", null);
            if (HasFrameOwnership)
                return Rgb24FinalizeStart.Reject("rgb24-frame-ownership-residual", null);

            // ---- 计数一致性关口：必须在 BeginFinalize() **之前** ----
            if (expectedFrameCount <= 0)
            {
                FailClosedFinalizeStart();
                return Rgb24FinalizeStart.Reject("rgb24-finalize-frame-count-invalid",
                    "expected=" + expectedFrameCount.ToString(CultureInfo.InvariantCulture));
            }

            long delivered = _lifetime.DeliveredFrameCount;
            if (delivered != expectedFrameCount)
            {
                FailClosedFinalizeStart();
                return Rgb24FinalizeStart.Reject("rgb24-finalize-frame-count-mismatch",
                    "delivered=" + delivered.ToString(CultureInfo.InvariantCulture) +
                    " expected=" + expectedFrameCount.ToString(CultureInfo.InvariantCulture));
            }

            Task<FfmpegVideoOutcome> completion;
            try
            {
                completion = _lifetime.BeginFinalize();
            }
            catch (Exception ex)
            {
                FailClosedFinalizeStart();
                return Rgb24FinalizeStart.Reject("rgb24-finalize-threw", ex.Message);
            }

            if (completion == null)
            {
                FailClosedFinalizeStart();
                return Rgb24FinalizeStart.Reject("rgb24-finalize-unavailable", null);
            }

            // L2 的 FinishAsync 一旦受理就会建立唯一的收敛任务。这里观察到 null 说明本次
            // 尝试被拒绝（not-running / pipe-poisoned / write-in-flight）：没有收敛 owner，
            // 必须主动取消才能回收。
            if (_lifetime.CleanupTask == null)
            {
                FailClosedFinalizeStart();
                return Rgb24FinalizeStart.Reject("rgb24-finalize-rejected", null);
            }

            _finalizeStarted = true;
            _finalizeCompletion = completion;
            return Rgb24FinalizeStart.Accept(completion);
        }

        private void FailClosedFinalizeStart()
        {
            _stopping = true;
            try { _lifetime.RequestStop("rgb24-finalize-rejected"); }
            catch (Exception) { }
        }

        /// <summary>
        /// Finalizing 期间的取消门禁：只请求停止（L2 负责不发布已取消的产物），
        /// 不同步等待，也不假装资源已回收。
        /// </summary>
        internal bool TryRequestStop(string reason, out string error)
        {
            error = null;
            if (!Bridge.IsMainThread)
            {
                error = "rgb24-wrong-main-thread";
                return false;
            }

            _stopping = true;
            try { _lifetime.RequestStop(reason); }
            catch (Exception ex)
            {
                error = "rgb24-pipeline-stop-failed:" + ex.Message;
                return false;
            }

            return true;
        }

        // 幂等、非阻塞；终态 Update、取消入口及重开 gate 都可以调用。
        internal bool TryStopAndDrain(string reason, out string error)
        {
            error = null;
            if (!Bridge.IsMainThread)
            {
                error = "rgb24-wrong-main-thread";
                return false;
            }
            _stopping = true;
            string stopError = null;
            try { _lifetime.RequestStop(reason); }
            catch (Exception ex) { stopError = "rgb24-pipeline-stop-failed:" + ex.Message; }
            bool frameClean = Transaction.TryAbort(out error);
            if (!frameClean) return false;
            if (stopError != null)
            {
                error = stopError;
                return false;
            }
            if (HasPipelineOwnership)
            {
                error = "rgb24-pipeline-cleanup-pending-or-residual";
                return false;
            }
            Pool.Reset();
            return true;
        }

        // Arm 与新 session 共用的替换屏障；失败时原引用完全保留。
        internal static bool TryReplace(ref Rgb24DeliveryContext current, Rgb24DeliveryContext next,
            bool schedulerActive, out string error)
        {
            error = null;
            if (schedulerActive || (current != null &&
                (!current.Bridge.IsMainThread || current.HasResidualOwnership)))
            {
                error = "rgb24-context-active-or-residual";
                return false;
            }
            if (next != null && (!next.Bridge.IsMainThread || next.Stopping || next.HasFrameOwnership))
            {
                error = "rgb24-context-not-fresh";
                return false;
            }
            current = next;
            return true;
        }
    }
}
