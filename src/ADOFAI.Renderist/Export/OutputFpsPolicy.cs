using System;
using System.Globalization;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// Output FPS 的合法性判定与派生计算（唯一单点定义）。
    ///
    /// 语义：Renderist 是非实时导出工具，Output FPS 表示 output sampling density
    /// （每个逻辑秒输出多少张 PNG），**不要求** wall-clock realtime throughput。
    /// 1000 FPS 不要求现实时间每秒完成 1000 张 PNG；只要 export / chart timeline
    /// 按 1/1000 s per output frame 正确推进，离线导出语义即成立。
    ///
    /// 因此本层只负责两件事：
    ///   * 结构合法性：必须是当前配置 / API 能表达的正整数
    ///     （Settings.EditorTargetFrameRate 与 Time.captureFramerate 都是 int）。
    ///   * 数值安全：派生量不得溢出。
    ///
    /// 本层**不**定义产品级上限。性能、PNG 编码耗时、磁盘占用、预计导出时长都不是
    /// 合法性条件；它们未来只通过 warning / estimate / benchmark / disk estimate /
    /// recommendation 表达，绝不用 legality gate 禁止用户参数。
    ///
    /// 消费方：
    ///   * Settings.EditorTargetFrameRate —— 唯一配置来源
    ///   * EditorExportPreflight        —— GUI readiness 判定
    ///   * DeterministicFrameScheduler  —— session 启动 gate 与 Unity 时间设置
    ///   * EndTailPolicy                —— Frames / Seconds / Beats 换算
    ///   * ModEntry GUI                 —— 输入校验与显示
    /// </summary>
    internal static class OutputFpsPolicy
    {
        /// <summary>
        /// 最小合法值。Unity 约定 Time.captureFramerate == 0 表示"未启用捕获节拍"，
        /// 因此 0 必须被拒绝，1 是最小有意义值。
        /// </summary>
        internal const int Minimum = 1;

        internal const int Default = 60;

        /// <summary>
        /// Application.targetFrameRate 的下限。它只用于让 Unity 不节流导出循环，
        /// 与 outputFps 的具体取值无关。
        /// </summary>
        internal const int MinimumUnityTargetFrameRate = 1000;

        private const int TargetFrameRateMultiplier = 4;

        /// <summary>
        /// Output FPS 是 authority 参数：合法即"正整数"。
        /// int.MaxValue 只是当前数据类型的自然表达边界，既不是推荐值，也不是性能目标。
        /// </summary>
        internal static bool IsValid(int value)
        {
            return value >= Minimum;
        }

        internal static bool TryValidate(int value, out string error)
        {
            if (value < Minimum)
            {
                error = "output-fps-not-positive";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// GUI 输入解析：只接受纯十进制正整数（拒绝符号、分隔符、空白、溢出），
        /// 且必须通过同一条合法性规则。
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
        /// outputFps -> Application.targetFrameRate（best-effort derived hint）。
        ///
        /// 该值只用于让 Unity 不把导出循环节流到显示器刷新率，**不是** authority：
        /// 任何 outputFps 的合法性都不取决于它能否被精确表达。乘法一律走 long，
        /// 超过 int.MaxValue 时饱和到 int.MaxValue；用户 Output FPS 绝不因为这个
        /// 内部派生量被拒绝。
        /// </summary>
        internal static int ResolveUnityTargetFrameRate(int outputFps)
        {
            if (outputFps <= 0) return MinimumUnityTargetFrameRate;
            long derived = (long)outputFps * TargetFrameRateMultiplier;
            if (derived > int.MaxValue) return int.MaxValue;
            return (int)Math.Max(MinimumUnityTargetFrameRate, derived);
        }
    }
}
