using System;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// Route B 的唯一逻辑时间源。
    ///
    /// FrameIndex 是唯一可推进的状态；wall clock、Unity Update 次数、音频采样游标
    /// 和官方 AutoPlay 都不能改变这里的时间。pitch 只在 chart-time 映射中应用一次。
    /// </summary>
    internal sealed class MasterTimeline
    {
        internal readonly struct FrameSample
        {
            internal FrameSample(int frameIndex, double outputTime, double chartTime)
            {
                FrameIndex = frameIndex;
                OutputTime = outputTime;
                ChartTime = chartTime;
            }

            internal int FrameIndex { get; }
            internal double OutputTime { get; }
            internal double ChartTime { get; }
        }

        internal MasterTimeline(int outputFps, double canonicalStart, double pitch)
        {
            if (outputFps <= 0) throw new ArgumentOutOfRangeException(nameof(outputFps));
            if (double.IsNaN(canonicalStart) || double.IsInfinity(canonicalStart))
                throw new ArgumentOutOfRangeException(nameof(canonicalStart));
            if (double.IsNaN(pitch) || double.IsInfinity(pitch) || pitch <= 0.0001)
                throw new ArgumentOutOfRangeException(nameof(pitch));

            OutputFps = outputFps;
            CanonicalStart = canonicalStart;
            Pitch = pitch;
        }

        internal int OutputFps { get; }
        internal double CanonicalStart { get; }
        internal double Pitch { get; }

        internal FrameSample Prepare(int frameIndex)
        {
            if (frameIndex < 0) throw new ArgumentOutOfRangeException(nameof(frameIndex));

            double outputTime = (double)frameIndex / OutputFps;
            double chartTime = CanonicalStart + outputTime * Pitch;
            return new FrameSample(frameIndex, outputTime, chartTime);
        }
    }
}
