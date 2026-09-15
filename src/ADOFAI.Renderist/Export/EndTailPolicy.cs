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
        internal EndTailResolution(long frameCount, double seconds, double? beats)
        {
            FrameCount = frameCount;
            Seconds = seconds;
            Beats = beats;
        }

        /// <summary>tail 的 output-frame 数（long：与 canonical output frame 计数同一域）。</summary>
        internal long FrameCount { get; }

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

        /// <summary>
        /// Frames 是离散计数。玩家直接输入 Frames 时只允许固定绝对误差范围内的整数；
        /// 容差绝不能随数值大小增长，否则大数会把明显的小数误判成整数。
        /// 该值与 ModEntry 的 GUI 输入语义一致。
        /// </summary>
        internal const double FrameIntegerAbsoluteTolerance = 1e-10;

        /// <summary>
        /// double → long 转换的可表达边界（2^63）。这是**数据类型结构边界**，不是产品级
        /// 上限：超过它的 tail 帧数无法用 long 表达，因此必须显式 fail-closed，
        /// 绝不依赖 unchecked 转换（C# 的 checked 对浮点→整数转换无效）。
        /// </summary>
        private const double LongFrameCountExclusiveLimit = 9223372036854775808.0;

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
            if (input.Unit == EndTailUnit.Frames && !IsFrameInputInteger(input.Value))
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
            long safetyFrameLimit,
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

            if (!IsFinite(rawFrames) || rawFrames < 0.0 || rawFrames >= LongFrameCountExclusiveLimit)
            {
                error = "end-tail-frame-count-overflow";
                return false;
            }

            rawFrames = SnapComputedFrameCountNearInteger(rawFrames);
            // 上面的边界检查已保证 Ceiling 结果落在 long 可表达范围内。
            long frameCount = (long)Math.Ceiling(rawFrames);
            // safetyFrameLimit == 0 表示未配置上限（unbounded）：End Tail 不受 safety 限制。
            if (SafetyFrameLimitPolicy.IsFrameLimitReached(safetyFrameLimit, frameCount))
            {
                error = "end-tail-exceeds-safety-limit";
                return false;
            }

            double resolvedSeconds = frameCount / (double)outputFps;
            double? resolvedBeats = IsPositiveFinite(completionBpm) && IsPositiveFinite(pitch)
                ? resolvedSeconds * completionBpm.Value * pitch.Value / 60.0
                : (double?)null;
            resolution = new EndTailResolution(frameCount, resolvedSeconds, resolvedBeats);
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
                    double rawFrames = SnapComputedFrameCountNearInteger(seconds * outputFps);
                    if (!IsFinite(rawFrames) || rawFrames < 0.0 || rawFrames >= LongFrameCountExclusiveLimit)
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

        /// <summary>
        /// 直接 Frames 输入的整数判定。使用固定绝对容差，不随 magnitude 放大。
        /// </summary>
        internal static bool IsFrameInputInteger(double value)
        {
            return IsFinite(value) && value >= 0.0 &&
                   Math.Abs(value - Math.Round(value)) <= FrameIntegerAbsoluteTolerance;
        }

        private static bool IsPositiveFinite(double? value)
        {
            return value.HasValue && IsFinite(value.Value) && value.Value > MinimumPositiveValue;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Seconds / Beats 换算出的 raw frame count 允许吸收**表示误差**，但不能沿用
        /// magnitude-relative 产品容差。这里最多吸收到最近整数的半个 ULP（并保留原有
        /// 1e-10 绝对下限以覆盖常见十进制乘法误差）；超过该范围仍交给 Ceiling。
        /// </summary>
        private static double SnapComputedFrameCountNearInteger(double value)
        {
            double nearest = Math.Round(value);
            double tolerance = Math.Max(FrameIntegerAbsoluteTolerance, HalfUlp(nearest));
            return Math.Abs(value - nearest) <= tolerance ? nearest : value;
        }

        private static double HalfUlp(double value)
        {
            double magnitude = Math.Abs(value);
            if (!IsFinite(magnitude) || magnitude == 0.0)
                return 0.0;

            long bits = BitConverter.DoubleToInt64Bits(magnitude);
            double next = BitConverter.Int64BitsToDouble(bits + 1);
            double spacing = next - magnitude;
            return IsFinite(spacing) && spacing > 0.0 ? spacing * 0.5 : 0.0;
        }
    }
}
