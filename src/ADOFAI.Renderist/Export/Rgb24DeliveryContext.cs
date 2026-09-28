using System;
using System.Threading.Tasks;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.Export
{
    // 只暴露已有 L2 的取消与资源收敛证据；不创建或接管第二个 process。
    internal interface IRgb24PipelineLifetime
    {
        void RequestStop(string reason);
        Task<FfmpegVideoOutcome> CleanupTask { get; }
    }

    internal sealed class Rgb24DeliveryContext
    {
        private readonly IRgb24PipelineLifetime _lifetime;
        private bool _stopping;
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
