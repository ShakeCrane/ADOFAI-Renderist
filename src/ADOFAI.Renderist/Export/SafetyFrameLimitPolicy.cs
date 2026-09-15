using System.Globalization;

namespace ADOFAI.Renderist.Export
{
    /// <summary>safety 上限的解释方式（写入 session metadata 与日志）。</summary>
    internal enum SafetyLimitKind
    {
        /// <summary>未配置：不存在总帧数或总时长上限（内建默认）。</summary>
        Disabled = 0,

        /// <summary>显式配置的 output-frame 上限，按配置值原样使用。</summary>
        ExplicitFrames = 1,
    }

    /// <summary>一个 session 实际采用的 safety policy 解析结果。</summary>
    internal readonly struct SafetyLimitResolution
    {
        internal SafetyLimitResolution(SafetyLimitKind kind, long frameLimit, int configuredFrameLimit)
        {
            Kind = kind;
            FrameLimit = frameLimit;
            ConfiguredFrameLimit = configuredFrameLimit;
        }

        internal SafetyLimitKind Kind { get; }

        /// <summary>
        /// 实际生效的 output-frame 上限。<c>0</c> 表示 **未配置**（disabled / unbounded），
        /// 不是"允许 0 帧"；判定一律经 <see cref="SafetyFrameLimitPolicy.IsFrameLimitReached"/>。
        /// </summary>
        internal long FrameLimit { get; }

        /// <summary>Settings.EditorExportSafetyFrameLimit 的原始值（诊断用，未解析）。</summary>
        internal int ConfiguredFrameLimit { get; }
    }

    /// <summary>
    /// Safety 上限的唯一解析点与判定点。
    ///
    /// 产品原则：Renderist 是非实时导出工具。Output FPS、正常谱面长度与正常总输出帧数
    /// **不因**实时性能、磁盘量、预计耗时或文件数量被产品层 fail-closed；这些未来只用
    /// warning / estimate / disk estimate / benchmark / recommendation 表达。
    ///
    /// 因此本层语义是：
    ///   * **默认 = 未配置（disabled / unbounded）**：不存在内建最大导出时长，也不存在
    ///     内建最大总帧数。0.3.6.0 的历史默认 `36000` 与 0.3.6.1 草案中的
    ///     `ceil(600 × outputFps)`、`min(value, 1000000)` 都已移除。
    ///   * **显式正整数 = 显式 output-frame 上限**：按配置值**原样**使用，不再夹取。
    ///     `int.MaxValue` 只是 Settings 当前数据类型的结构边界，既不是产品推荐值，
    ///     也不是性能限制。
    ///   * 判定统一走 <see cref="IsFrameLimitReached"/>：`frameLimit <= 0` 时恒为 false，
    ///     即 unbounded 状态下永远不因帧数终止导出。
    ///
    /// legacy Settings 兼容（不使用配置版本系统）：
    ///   UMM 的 `OnSaveGUI → ModSettings.Save` 会把整个 Settings 对象（含默认值）序列化到
    ///   `Settings.xml`，历史默认 `EditorExportSafetyFrameLimit = 36000` 因此会真实落盘
    ///   （实测本机 `Mods\ADOFAI.Renderist\Settings.xml` 即带该元素）。
    ///
    ///   已知歧义（刻意接受）：safety 配置从未在 GUI 中暴露，所以正常 GUI 使用下写盘的
    ///   36000 就是 legacy 默认值；但用户**仍可手工编辑** `Settings.xml` 写成 36000，
    ///   Renderist 无法区分"legacy 默认 36000"与"手工显式 36000"。
    ///
    ///   当前兼容策略：把历史内建默认值本身统一迁移解释为"未配置"（与 `<= 0` 同义），
    ///   其它正整数才按显式 frame limit 解释——这是最简单且可解释的规则，
    ///   代价是手工写入的 36000 也会被当作 unbounded。
    /// </summary>
    internal static class SafetyFrameLimitPolicy
    {
        /// <summary>
        /// 历史内建默认值（0.3.6.0 及更早）。自 0.3.6.1 起它是"未配置"的 legacy 标记。
        /// </summary>
        internal const int LegacyDefaultFrameLimit = 36000;

        /// <summary>metadata / 日志中表示"未配置"的机器可读标签。</summary>
        internal const string UnboundedLabel = "unbounded";

        /// <summary>该配置值是否表示"未配置 safety 上限"。</summary>
        internal static bool IsDisabled(int configuredFrameLimit)
        {
            return configuredFrameLimit <= 0 || configuredFrameLimit == LegacyDefaultFrameLimit;
        }

        /// <summary>解析本 session 的 safety policy。纯函数，不依赖 Output FPS。</summary>
        internal static SafetyLimitResolution Resolve(int configuredFrameLimit)
        {
            if (IsDisabled(configuredFrameLimit))
                return new SafetyLimitResolution(SafetyLimitKind.Disabled, 0L, configuredFrameLimit);

            return new SafetyLimitResolution(
                SafetyLimitKind.ExplicitFrames, configuredFrameLimit, configuredFrameLimit);
        }

        /// <summary>
        /// scheduler / End Tail 的唯一帧数判定。frameLimit 为 0（或负）时表示 unbounded，
        /// 恒不触发；显式配置时严格按配置值触发，不做任何额外夹取或提前终止。
        /// </summary>
        internal static bool IsFrameLimitReached(long frameLimit, long outputFrameCount)
        {
            return frameLimit > 0 && outputFrameCount >= frameLimit;
        }

        /// <summary>metadata / 日志用的机器可读 policy 标签。</summary>
        internal static string KindLabel(SafetyLimitKind kind)
        {
            return kind == SafetyLimitKind.Disabled ? UnboundedLabel : "explicit-frames";
        }

        /// <summary>
        /// 日志 / 显示用的 frame 上限文本。unbounded 状态返回 `unbounded`，
        /// 绝不用 `0` 或 sentinel 冒充一个真实生效的上限。
        /// </summary>
        internal static string DescribeFrameLimit(long frameLimit)
        {
            return frameLimit > 0
                ? frameLimit.ToString(CultureInfo.InvariantCulture)
                : UnboundedLabel;
        }
    }
}
