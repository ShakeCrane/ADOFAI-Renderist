using System;
using ADOFAI.Renderist.Ffmpeg;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// L3-A 的 RGB24 交付上下文：把"本 session 的交付目标 + 帧缓冲池 + main-thread 桥 + 事务"
    /// 组装成 scheduler 可以编排的单一对象。
    ///
    /// 由 session 启动路径在 **Unity 主线程** 创建（因此必须在此刻捕获
    /// SynchronizationContext 与主线程身份）；如果 MP4 所需的 Unity
    /// SynchronizationContext 不可用，创建即 fail-closed。
    ///
    /// 本类只做组装，不拥有时间推进、EOF、进程或 scheduler 生命周期。
    /// </summary>
    internal sealed class Rgb24DeliveryContext
    {
        private Rgb24DeliveryContext(
            Rgb24FrameLayout layout,
            Rgb24FrameBufferPool pool,
            IRgb24FrameTransport transport,
            Rgb24MainThreadBridge bridge,
            FfmpegVideoPipeline pipeline)
        {
            Layout = layout;
            Pool = pool;
            Transport = transport;
            Bridge = bridge;
            Pipeline = pipeline;
            Transaction = new Rgb24FrameTransaction(pool, transport, bridge, OnEnvelopePosted);
        }

        internal Rgb24FrameLayout Layout { get; private set; }
        internal Rgb24FrameBufferPool Pool { get; private set; }
        internal IRgb24FrameTransport Transport { get; private set; }
        internal Rgb24MainThreadBridge Bridge { get; private set; }

        /// <summary>生产交付目标；测试 / 独立装配时可以为 null。</summary>
        internal FfmpegVideoPipeline Pipeline { get; private set; }

        internal Rgb24FrameTransaction Transaction { get; private set; }

        /// <summary>
        /// 已被 main-thread 上下文投递、但尚未被 scheduler 消费的信封。
        /// 只在 Unity 主线程读写。
        /// </summary>
        internal Rgb24DeliveryEnvelope PendingEnvelope { get; private set; }

        /// <summary>
        /// 为给定冻结几何与交付目标创建上下文。**必须在 Unity 主线程调用**。
        /// </summary>
        internal static bool TryCreate(
            int outputWidth, int outputHeight,
            FfmpegVideoPipeline pipeline,
            out Rgb24DeliveryContext context, out string error)
        {
            context = null;
            error = null;

            Rgb24FrameLayout layout;
            string layoutError;
            if (!Rgb24FrameLayout.TryCreate(outputWidth, outputHeight, out layout, out layoutError))
            {
                error = layoutError ?? "rgb24-layout-invalid";
                return false;
            }

            Rgb24MainThreadBridge bridge;
            string bridgeError;
            if (!Rgb24MainThreadBridge.TryCapture(out bridge, out bridgeError))
            {
                // MP4 所需的 Unity SynchronizationContext 不可用 ⇒ 启动 fail-closed。
                error = bridgeError ?? "rgb24-main-thread-context-unavailable";
                return false;
            }

            if (pipeline == null)
            {
                error = "rgb24-pipeline-missing";
                return false;
            }

            var pool = new Rgb24FrameBufferPool(layout);
            var transport = new FfmpegRgb24FrameTransport(pipeline);
            context = new Rgb24DeliveryContext(layout, pool, transport, bridge, pipeline);
            return true;
        }

        private void OnEnvelopePosted(Rgb24DeliveryEnvelope envelope)
        {
            // 由 main-thread 上下文调用：只登记，不做校验、不 Commit。
            PendingEnvelope = envelope;
        }

        /// <summary>主线程取走待消费信封（事件驱动，不轮询 IO）。</summary>
        internal bool TryTakePendingEnvelope(out Rgb24DeliveryEnvelope envelope, out string bridgeError)
        {
            if (PendingEnvelope != null)
            {
                envelope = PendingEnvelope;
                PendingEnvelope = null;
                bridgeError = null;
                return true;
            }

            // 同时暴露"后台已完成但主线程始终没有收到交接"的内部桥接故障。
            return Transaction.TryTakePendingEnvelope(out envelope, out bridgeError);
        }

        /// <summary>是否仍有本上下文拥有的未收敛资源（residual gate 的输入之一）。</summary>
        internal bool HasResidualOwnership
        {
            get { return Transaction.HasResidualOwnership || Pool.HasOutstandingLease; }
        }

        internal void Reset()
        {
            PendingEnvelope = null;
            Transaction.Reset();
            Pool.Reset();
        }

        /// <summary>诊断用：把 main-thread 桥的身份写入日志（只记录事实，不参与判定）。</summary>
        internal void LogBridgeIdentity()
        {
            Log.Info("Rgb24DeliveryContext: mainThreadId=" +
                     Bridge.MainThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                     " hasContext=" + (Bridge.HasContext ? "true" : "false") +
                     " rowOrder=" + Rgb24RowOrderPolicy.DeliveryLabel +
                     " bytesPerFrame=" + Layout.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                     " segments=" + Layout.SegmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
