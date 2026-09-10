using System;

namespace ADOFAI.Renderist.Export
{
    /// <summary>玩家输入的 End Tail 单位。</summary>
    public enum EndTailUnit
    {
        Frames = 0,
        Seconds = 1,
        Beats = 2,
    }

    /// <summary>Start 时冻结的单一 End Tail 输入。</summary>
    internal readonly struct EndTailInput
    {
        internal EndTailInput(double value, EndTailUnit unit)
        {
            Value = value;
            Unit = unit;
        }

        internal double Value { get; }
        internal EndTailUnit Unit { get; }
    }

    /// <summary>最终供 scheduler 使用的 output-frame 解析结果。</summary>
    internal readonly struct EndTailResolution
    {
        internal EndTailResolution(int frameCount, double seconds, double? beats)
        {
            FrameCount = frameCount;
            Seconds = seconds;
            Beats = beats;
        }

        internal int FrameCount { get; }
        internal double Seconds { get; }
        internal double? Beats { get; }
    }

    /// <summary>
    /// End Tail 的纯换算层。所有单位最终使用 ceil 量化为 output frames；
    /// 不读取 wall clock，也不拥有 scheduler 生命周期。
    /// </summary>
    internal static class EndTailPolicy
    {
        internal const double DefaultValue = 12.0;
        internal const EndTailUnit DefaultUnit = EndTailUnit.Frames;

        private const double MinimumPositiveValue = 0.0001;
        private const double IntegerSnapRelativeTolerance = 1e-10;

        internal static bool TryValidateInput(EndTailInput input, out string error)
        {
            error = null;
            if (!IsFinite(input.Value) || input.Value < 0.0)
            {
                error = "end-tail-value-invalid";
                return false;
            }
            if (!Enum.IsDefined(typeof(EndTailUnit), input.Unit))
            {
                error = "end-tail-unit-invalid";
                return false;
            }
            if (input.Unit == EndTailUnit.Frames && !IsNearlyInteger(input.Value))
            {
                error = "end-tail-frames-must-be-integer";
                return false;
            }
            return true;
        }

        internal static bool TryResolve(
            EndTailInput input,
            int outputFps,
            double? completionBpm,
            double? pitch,
            int safetyFrameLimit,
            out EndTailResolution resolution,
            out string error)
        {
            resolution = default;
            if (!TryValidateInput(input, out error))
                return false;
            if (outputFps <= 0)
            {
                error = "end-tail-output-fps-invalid";
                return false;
            }

            double rawFrames;
            switch (input.Unit)
            {
                case EndTailUnit.Frames:
                    rawFrames = Math.Round(input.Value);
                    break;
                case EndTailUnit.Seconds:
                    rawFrames = input.Value * outputFps;
                    break;
                case EndTailUnit.Beats:
                    if (!IsPositiveFinite(completionBpm))
                    {
                        error = "end-tail-completion-bpm-unavailable";
                        return false;
                    }
                    if (!IsPositiveFinite(pitch))
                    {
                        error = "end-tail-pitch-unavailable";
                        return false;
                    }
                    rawFrames = input.Value * 60.0 * outputFps /
                                (completionBpm.Value * pitch.Value);
                    break;
                default:
                    error = "end-tail-unit-invalid";
                    return false;
            }

            if (!IsFinite(rawFrames) || rawFrames < 0.0 || rawFrames > int.MaxValue)
            {
                error = "end-tail-frame-count-overflow";
                return false;
            }

            rawFrames = SnapNearInteger(rawFrames);
            long frameCount = (long)Math.Ceiling(rawFrames);
            if (frameCount < 0 || frameCount > int.MaxValue)
            {
                error = "end-tail-frame-count-overflow";
                return false;
            }
            if (safetyFrameLimit > 0 && frameCount >= safetyFrameLimit)
            {
                error = "end-tail-exceeds-safety-limit";
                return false;
            }

            double resolvedSeconds = frameCount / (double)outputFps;
            double? resolvedBeats = IsPositiveFinite(completionBpm) && IsPositiveFinite(pitch)
                ? resolvedSeconds * completionBpm.Value * pitch.Value / 60.0
                : (double?)null;
            resolution = new EndTailResolution((int)frameCount, resolvedSeconds, resolvedBeats);
            error = null;
            return true;
        }

        /// <summary>UI 单位切换使用；返回未量化的 canonical output duration。</summary>
        internal static bool TryToOutputSeconds(
            EndTailInput input,
            int outputFps,
            double? completionBpm,
            double? pitch,
            out double seconds,
            out string error)
        {
            seconds = 0.0;
            if (!TryValidateInput(input, out error))
                return false;
            if (outputFps <= 0)
            {
                error = "end-tail-output-fps-invalid";
                return false;
            }

            switch (input.Unit)
            {
                case EndTailUnit.Frames:
                    seconds = Math.Round(input.Value) / outputFps;
                    break;
                case EndTailUnit.Seconds:
                    seconds = input.Value;
                    break;
                case EndTailUnit.Beats:
                    if (!IsPositiveFinite(completionBpm))
                    {
                        error = "end-tail-completion-bpm-unavailable";
                        return false;
                    }
                    if (!IsPositiveFinite(pitch))
                    {
                        error = "end-tail-pitch-unavailable";
                        return false;
                    }
                    seconds = input.Value * 60.0 /
                              (completionBpm.Value * pitch.Value);
                    break;
                default:
                    error = "end-tail-unit-invalid";
                    return false;
            }

            if (!IsFinite(seconds) || seconds < 0.0)
            {
                error = "end-tail-duration-overflow";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// 把 canonical output duration 转为目标显示单位。Frames 使用 ceil；
        /// Seconds / Beats 保留 duration，不做链式格式化换算。
        /// </summary>
        internal static bool TryFromOutputSeconds(
            double seconds,
            EndTailUnit targetUnit,
            int outputFps,
            double? completionBpm,
            double? pitch,
            out double value,
            out string error)
        {
            value = 0.0;
            error = null;
            if (!IsFinite(seconds) || seconds < 0.0 || outputFps <= 0)
            {
                error = "end-tail-duration-invalid";
                return false;
            }

            switch (targetUnit)
            {
                case EndTailUnit.Frames:
                    double rawFrames = SnapNearInteger(seconds * outputFps);
                    if (!IsFinite(rawFrames) || rawFrames > int.MaxValue)
                    {
                        error = "end-tail-frame-count-overflow";
                        return false;
                    }
                    value = Math.Ceiling(rawFrames);
                    return true;
                case EndTailUnit.Seconds:
                    value = seconds;
                    return true;
                case EndTailUnit.Beats:
                    if (!IsPositiveFinite(completionBpm))
                    {
                        error = "end-tail-completion-bpm-unavailable";
                        return false;
                    }
                    if (!IsPositiveFinite(pitch))
                    {
                        error = "end-tail-pitch-unavailable";
                        return false;
                    }
                    value = seconds * completionBpm.Value * pitch.Value / 60.0;
                    if (!IsFinite(value))
                    {
                        error = "end-tail-value-overflow";
                        return false;
                    }
                    return true;
                default:
                    error = "end-tail-unit-invalid";
                    return false;
            }
        }

        private static bool IsPositiveFinite(double? value)
        {
            return value.HasValue && IsFinite(value.Value) && value.Value > MinimumPositiveValue;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsNearlyInteger(double value)
        {
            return Math.Abs(value - Math.Round(value)) <=
                   IntegerSnapRelativeTolerance * Math.Max(1.0, Math.Abs(value));
        }

        private static double SnapNearInteger(double value)
        {
            double nearest = Math.Round(value);
            return IsNearlyInteger(value) ? nearest : value;
        }
    }
}
