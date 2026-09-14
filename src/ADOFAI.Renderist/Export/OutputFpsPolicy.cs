using System;
using System.Globalization;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// Output FPS 的唯一合法范围与派生计算。
    ///
    /// Output FPS 是输出采样密度（output frame per second），既不是渲染分辨率也不是
    /// 引擎帧率。它被多处消费，因此范围必须只有一处定义，GUI 与 preflight 共用：
    ///   * Settings.EditorTargetFrameRate —— 唯一配置来源
    ///   * EditorExportPreflight        —— GUI readiness 判定
    ///   * DeterministicFrameScheduler  —— session 启动 gate 与 Unity 时间设置
    ///   * EndTailPolicy                —— Frames / Seconds / Beats 换算
    ///   * ModEntry GUI                 —— 输入校验与显示
    ///
    /// 上限依据（保守工程上限，不是 ADOFAI 原生约束，DLL 内没有任何 outputFps 范围证据）：
    ///   * 下界 1：Unity 约定 Time.captureFramerate == 0 表示"未启用捕获节拍"，
    ///     因此 0 必须被拒绝，1 是最小有意义值。
    ///   * 上界 1000：超过 1000 时单帧时间预算 < 1 ms，已低于本 Mod 自身的
    ///     ReadPixels + EncodeToPNG + 落盘成本（已验证捕获尺寸 3072x1920），
    ///     该值无法被真实兑现，只会制造不可解释的失败；同时它让 4 倍 headroom
    ///     的乘法远离任何整数溢出。
    /// </summary>
    internal static class OutputFpsPolicy
    {
        internal const int Minimum = 1;
        internal const int Maximum = 1000;
        internal const int Default = 60;

        /// <summary>
        /// Application.targetFrameRate 的下限。它只用于让 Unity 不节流导出循环，
        /// 与 outputFps 的具体取值无关。
        /// </summary>
        internal const int MinimumUnityTargetFrameRate = 1000;

        /// <summary>
        /// 面向玩家的范围文本。由 Minimum / Maximum 派生，避免范围在 GUI 文案里
        /// 出现第二份硬编码。
        /// </summary>
        internal static readonly string RangeText =
            Minimum.ToString(CultureInfo.InvariantCulture) + "-" + Maximum.ToString(CultureInfo.InvariantCulture);

        private const int TargetFrameRateMultiplier = 4;

        internal static bool IsValid(int value)
        {
            return value >= Minimum && value <= Maximum;
        }

        internal static bool TryValidate(int value, out string error)
        {
            if (value < Minimum)
            {
                error = "output-fps-below-minimum";
                return false;
            }
            if (value > Maximum)
            {
                error = "output-fps-above-maximum";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// GUI 输入解析：只接受纯十进制正整数（拒绝符号、分隔符、空白、溢出），
        /// 且必须落在合法范围内。
        /// </summary>
        internal static bool TryParse(string text, out int value, out string error)
        {
            value = 0;
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0 ||
                !int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                error = "output-fps-not-a-positive-integer";
                return false;
            }
            return TryValidate(value, out error);
        }

        /// <summary>
        /// outputFps -> Application.targetFrameRate。调用方应已通过 <see cref="IsValid"/>；
        /// 此处仍做溢出安全计算，绝不依赖整数回绕后由 Math.Max 兜底。
        /// </summary>
        internal static int ResolveUnityTargetFrameRate(int outputFps)
        {
            if (outputFps <= 0) return MinimumUnityTargetFrameRate;
            if (outputFps > int.MaxValue / TargetFrameRateMultiplier) return int.MaxValue;
            return Math.Max(MinimumUnityTargetFrameRate, outputFps * TargetFrameRateMultiplier);
        }
    }
}
