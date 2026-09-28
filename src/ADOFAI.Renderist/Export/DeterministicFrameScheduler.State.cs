namespace ADOFAI.Renderist.Export
{
    // scheduler 的纯状态判定与 Unity 执行部分同属一个类，harness 编译本文件。
    internal static partial class DeterministicFrameScheduler
    {
        public enum SchedulerStatus
        {
            Idle,
            Preparing,
            InitializationHold,
            Capturing,
            /// <summary>
            /// L3-A：本帧已 Prepare 并请求帧末事务，正在等 EOF 形成 CPU frame。
            /// 该阶段**禁止**再次 Prepare，也**禁止**推进 outputFrameIndex。
            /// </summary>
            AwaitingEOF,
            /// <summary>
            /// L3-A：CPU frame 已形成并交付给 L2，正在等 Completion 回到 Unity 主线程。
            /// 该阶段**禁止**推进 outputFrameIndex / forced chart time / Planet frame-local
            /// position，也**禁止**开启下一个 EOF。合法 FFmpeg 背压可以持续任意长时间。
            /// </summary>
            AwaitingDelivery,
            Completed,
            Cancelled,
            Failed,
        }


        internal static SchedulerStatus ResumeAfterRgb24Delivery(bool preEntryCapturing)
        {
            return preEntryCapturing ? SchedulerStatus.InitializationHold : SchedulerStatus.Capturing;
        }

        internal static bool CanPrepareGameplay(SchedulerStatus status, bool preEntryCapturing, bool hasTimeline)
        {
            return status == SchedulerStatus.Capturing && !preEntryCapturing && hasTimeline;
        }

        internal static bool IsPlaybackPhase(SchedulerStatus status)
        {
            return status == SchedulerStatus.InitializationHold || status == SchedulerStatus.Capturing ||
                   status == SchedulerStatus.AwaitingEOF || status == SchedulerStatus.AwaitingDelivery;
        }

        internal static bool CanObservePreparedGameplay(SchedulerStatus status, bool preEntryCapturing,
            bool pendingCapture)
        {
            return !preEntryCapturing &&
                (status == SchedulerStatus.Capturing || (status == SchedulerStatus.AwaitingEOF && pendingCapture));
        }
    }
}
