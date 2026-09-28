using System;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 把 L3-A 的 <see cref="IRgb24FrameTransport"/> 接到现有 L2 <see cref="FfmpegVideoPipeline"/>。
    ///
    /// 这是**薄适配器**，不是第二套 FFmpeg 实现：
    ///   * 不创建进程、不管理 stdin、不做核验、不做发布；
    ///   * 只把分段 RGB24 lease 映射为 L2 的帧分段，并把 L2 的写入结果映射为事务能消费的结果；
    ///   * L2 仍然独占"写入在途 / 结果提交 / 管道毒化"的全部语义。
    ///
    /// 关键 ownership 事实：L2 的 <c>TryWriteFrame</c> **不复制**缓冲区，后台写入线程会直接读取
    /// 这些 <c>byte[]</c> 段，直到该帧的 Completion 结束。因此 lease 必须在 Completion 被消费
    /// 之前保持 pin（见 Rgb24FrameTransaction / Rgb24FrameBufferPool）。
    /// </summary>
    internal sealed class FfmpegRgb24FrameTransport : IRgb24FrameTransport, IRgb24PipelineLifetime
    {
        private readonly FfmpegVideoPipeline _pipeline;

        internal FfmpegRgb24FrameTransport(FfmpegVideoPipeline pipeline)
        {
            if (pipeline == null) throw new ArgumentNullException("pipeline");
            _pipeline = pipeline;
        }

        internal FfmpegVideoPipeline Pipeline { get { return _pipeline; } }
        internal string FailureDiagnostics => _pipeline.DescribeFailureDiagnostics();
        public Task<FfmpegVideoOutcome> CleanupTask => _pipeline.CleanupTask;
        public void RequestStop(string reason) { _pipeline.Cancel(reason); }

        public Rgb24TransportAttempt TryBeginWrite(OwnedRgb24Frame frame)
        {
            if (frame == null)
                return Rgb24TransportAttempt.Rejected("rgb24-frame-null", null);

            FfmpegFrameWriteAttempt attempt;
            try
            {
                FfmpegVideoFrame videoFrame = BuildVideoFrame(frame);
                attempt = _pipeline.TryWriteFrame(videoFrame);
            }
            catch (Exception ex)
            {
                return Rgb24TransportAttempt.Rejected("rgb24-transport-begin-exception", ex.Message);
            }

            if (attempt == null)
                return Rgb24TransportAttempt.Rejected("rgb24-transport-null-attempt", null);

            if (!attempt.Accepted)
            {
                // busy 是 scheduler invariant failure（全链路最多一帧在途），必须与普通
                // 管道拒绝区分开：前者是内部错误，后者走既有失败收敛。
                if (string.Equals(attempt.ErrorCode, "busy", StringComparison.Ordinal))
                    return Rgb24TransportAttempt.Busy(attempt.ErrorDetail);

                return Rgb24TransportAttempt.Rejected(attempt.ErrorCode, attempt.ErrorDetail);
            }

            Task<Rgb24FrameWriteOutcome> completion = attempt.Completion.ContinueWith(
                MapCompletion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return Rgb24TransportAttempt.Accepted(completion);
        }

        private static FfmpegVideoFrame BuildVideoFrame(OwnedRgb24Frame frame)
        {
            int count = frame.SegmentCount;
            var segments = new FfmpegFrameSegment[count];
            for (int i = 0; i < count; i++)
            {
                byte[] buffer = frame.SegmentBuffer(i);
                segments[i] = new FfmpegFrameSegment(buffer, 0, buffer.Length);
            }

            // L2 在接收前用 long 校验累计长度；这里不做任何 clamp / 补齐。
            return new FfmpegVideoFrame(segments);
        }

        private static Rgb24FrameWriteOutcome MapCompletion(Task<FfmpegFrameWriteResult> task)
        {
            if (task.IsFaulted)
            {
                Exception inner = task.Exception == null ? null : task.Exception.GetBaseException();
                return Rgb24FrameWriteOutcome.Failure(
                    "rgb24-write-task-faulted", inner == null ? null : inner.Message);
            }

            if (task.IsCanceled)
                return Rgb24FrameWriteOutcome.Failure("rgb24-write-task-canceled", null);

            FfmpegFrameWriteResult result = task.Result;
            if (result == null)
                return Rgb24FrameWriteOutcome.Failure("rgb24-write-task-no-result", null);

            return new Rgb24FrameWriteOutcome(
                result.Success, result.ErrorCode, result.ErrorDetail, result.DeliveredFrameCount);
        }
    }
}
