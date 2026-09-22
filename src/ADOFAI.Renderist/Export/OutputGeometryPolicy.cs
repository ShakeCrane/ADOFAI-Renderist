using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ADOFAI.Renderist.Export
{
    /// <summary>输出几何的来源方式（写入 session metadata 与日志）。</summary>
    internal enum GeometryMode
    {
        /// <summary>
        /// 沿用当前游戏窗口渲染分辨率（Screen.width × Screen.height）。
        /// 这是默认模式，与 0.3.6.4 行为一致。
        /// </summary>
        LegacyWindow = 0,

        /// <summary>使用用户在 Settings 中显式指定的正整数宽高。</summary>
        CustomResolution = 1,
    }

    /// <summary>
    /// 输出几何的 persisted 输入（Settings 原始值，未解析、未 sanitize）。
    ///
    /// 非法 persisted 值**不自动修复**：它保持非法并 fail-closed，直到用户显式改成
    /// 合法值。与 persisted End Tail 的方案 A 语义一致。
    /// </summary>
    internal readonly struct GeometryInput
    {
        internal GeometryInput(bool customResolutionEnabled, int width, int height, int supersamplingScale)
        {
            CustomResolutionEnabled = customResolutionEnabled;
            Width = width;
            Height = height;
            SupersamplingScale = supersamplingScale;
        }

        internal bool CustomResolutionEnabled { get; }

        internal int Width { get; }

        internal int Height { get; }

        /// <summary>
        /// persisted 的超采样倍率（Phase 3.7.0 第二闭环）。语义同宽高：原始值、
        /// 未 sanitize；非法值保持非法并 fail-closed。倍率与自定义分辨率**互相独立**：
        /// legacy-window 模式下同样生效（此时 render = 冻结窗口尺寸 × scale）。
        /// </summary>
        internal int SupersamplingScale { get; }
    }

    /// <summary>
    /// 降采样链的一级目标尺寸。
    ///
    /// 级别按**相对 output 的整数倍率**表达，而不是对宽高分别做 ceil-halving：
    /// 每一级都精确等于 (outputWidth × factor, outputHeight × factor)，因此
    /// **每一级保持精确宽高比**，不会因为奇数尺寸而引入逐级累积的比例误差。
    /// </summary>
    internal readonly struct DownsampleStep
    {
        internal DownsampleStep(int width, int height)
        {
            Width = width;
            Height = height;
        }

        /// <summary>该级的 render 宽度（= outputWidth × factor）。</summary>
        internal int Width { get; }

        /// <summary>该级的 render 高度（= outputHeight × factor）。</summary>
        internal int Height { get; }
    }

    /// <summary>session 开始时冻结的输出几何解析结果。</summary>
    internal readonly struct GeometryResolution
    {
        internal GeometryResolution(
            GeometryMode mode, int width, int height,
            int scale, int renderWidth, int renderHeight,
            DownsampleStep[] downsampleSteps)
        {
            Mode = mode;
            Width = width;
            Height = height;
            Scale = scale;
            RenderWidth = renderWidth;
            RenderHeight = renderHeight;
            DownsampleSteps = downsampleSteps;
        }

        internal GeometryMode Mode { get; }

        /// <summary>最终输出宽度（PNG / ReadPixels 的尺寸）。</summary>
        internal int Width { get; }

        /// <summary>最终输出高度（PNG / ReadPixels 的尺寸）。</summary>
        internal int Height { get; }

        /// <summary>冻结的超采样倍率；1 = 关闭（与第一闭环完全一致）。</summary>
        internal int Scale { get; }

        /// <summary>Source RenderTexture 宽度 = checked(Width × Scale)。</summary>
        internal int RenderWidth { get; }

        /// <summary>Source RenderTexture 高度 = checked(Height × Scale)。</summary>
        internal int RenderHeight { get; }

        /// <summary>降采样链（source 之后的各级目标尺寸）；scale=1 时为空。</summary>
        internal DownsampleStep[] DownsampleSteps { get; }

        /// <summary>降采样级数（不含 source）。</summary>
        internal int DownsampleLevelCount =>
            DownsampleSteps == null ? 0 : DownsampleSteps.Length;

        /// <summary>是否需要多级降采样。</summary>
        internal bool RequiresDownsample => Scale > 1;

        /// <summary>
        /// 统一输出 aspect = width / height。三台原生 Camera 与 capture RenderTexture
        /// 都使用这同一个值（Renderist 不再按屏幕 aspect 推断任何东西）。
        /// aspect 与倍率无关：(S·W)/(S·H) == W/H，因此恒用 **output** 尺寸派生。
        /// </summary>
        internal double Aspect => (double)Width / (double)Height;
    }

    /// <summary>
    /// 输出分辨率的唯一判定 / 解析 / 派生点（Phase 3.7.0 Custom Resolution + Supersampling）。
    ///
    /// 语义：
    ///   * 自定义分辨率关闭（默认）→ 沿用 Screen.width / Screen.height，
    ///     保持 0.3.6.4 的渲染分辨率行为。
    ///   * 自定义分辨率开启 → 使用用户指定的正整数宽高，不 clamp、不取整、
    ///     不替换为默认值，也不因宽高比“看起来不合理”而拒绝。
    ///   * 超采样倍率（SupersamplingScale，默认 1）→ render = output × scale；
    ///     scale=1 表示不启用超采样（与第一闭环完全一致）。
    ///
    /// 合法性只包含两类**真实**约束：
    ///   * 表达能力：Settings 字段与 Unity RenderTexture API 都是 int，
    ///     宽高必须是 int 可表达的正整数；倍率同理，且 render 尺寸的 checked
    ///     乘法不得溢出。
    ///   * 硬件能力：不得超过当前 GPU 的 SystemInfo.maxTextureSize。
    ///     该判定对**实际创建的 render 尺寸**生效（真正被创建的是 renderW×renderH）。
    ///
    /// 本层**不**定义任何人为性能上限。像素总数、显存估算、预计编码耗时、磁盘占用与
    /// 预计导出时长都不是合法性条件；超出硬件能力时 RenderTexture 创建本身仍是最终
    /// backstop（见 FrameCaptureDriver 的 capture-target-create-failed）。
    ///
    /// 消费方：
    ///   * Settings.EditorCustomResolution* / EditorSupersamplingScale —— 唯一配置来源
    ///   * EditorExportPreflight          —— GUI readiness 判定
    ///   * DeterministicFrameScheduler    —— session 启动 gate 与一次性冻结
    ///   * FrameCaptureDriver             —— 只消费冻结值，**不再**自行读取 Screen
    ///   * ModEntry GUI                   —— 输入校验与显示
    /// </summary>
    internal static class OutputGeometryPolicy
    {
        /// <summary>自定义分辨率的初始默认值（仅在用户首次开启该功能时作为 GUI 初值）。</summary>
        internal const int DefaultCustomWidth = 1920;

        internal const int DefaultCustomHeight = 1080;

        /// <summary>超采样倍率默认值：1 = 不启用（保持第一闭环行为）。</summary>
        internal const int DefaultSupersamplingScale = 1;

        /// <summary>超采样倍率最小值。1 表示关闭超采样。</summary>
        internal const int MinimumSupersamplingScale = 1;

        /// <summary>
        /// 宽高必须是正整数：Unity 不接受 0 或负尺寸的 RenderTexture，
        /// Time / 屏幕 API 也不存在 0 宽窗口的合法渲染结果。
        /// </summary>
        internal const int MinimumDimension = 1;

        /// <summary>metadata / 日志中表示“沿用窗口”的机器可读标签。</summary>
        internal const string LegacyWindowLabel = "legacy-window";

        /// <summary>metadata / 日志中表示“使用自定义分辨率”的机器可读标签。</summary>
        internal const string CustomResolutionLabel = "custom-resolution";

        /// <summary>降采样算法标识（metadata / 日志）。多级 bilinear 逐级降采样。</summary>
        internal const string DownsampleAlgorithmLabel = "multi-stage-bilinear";

        private static readonly DownsampleStep[] EmptySteps = new DownsampleStep[0];

        /// <summary>
        /// 校验 persisted 输入的结构合法性（表达能力 + 硬件能力）。
        ///
        /// 倍率**在任何模式下都参与**（legacy-window 也要乘），因此先于
        /// 自定义分辨率开关判定；关闭自定义分辨率时宽高自身不参与导出，故非法
        /// persisted 宽高不构成阻断条件（与第一闭环语义一致）。
        /// </summary>
        internal static bool TryValidateInput(GeometryInput input, out string error)
        {
            error = null;

            if (!TryValidateSupersamplingScale(input.SupersamplingScale, out error))
                return false;

            if (!input.CustomResolutionEnabled)
                return true;

            return TryValidateDimension(input.Width, "width", out error) &&
                   TryValidateDimension(input.Height, "height", out error);
        }

        /// <summary>
        /// 解析本 session 的输出几何。这是**唯一**读取分辨率来源的地方：
        /// 自定义模式使用配置值；legacy 模式读取 Screen。
        ///
        /// 调用点必须在 session 开始时调用一次，并把结果冻结后传给
        /// FrameCaptureDriver（驱动不再自行读取 Screen，避免 session 中途窗口
        /// 尺寸变化导致 RT 尺寸与 Camera aspect ownership 不一致）。
        /// </summary>
        internal static bool TryResolve(GeometryInput input, out GeometryResolution resolution, out string error)
        {
            resolution = default;

            if (!TryValidateInput(input, out error))
                return false;

            int width;
            int height;
            GeometryMode mode;

            if (input.CustomResolutionEnabled)
            {
                width = input.Width;
                height = input.Height;
                mode = GeometryMode.CustomResolution;
            }
            else
            {
                try
                {
                    width = Screen.width;
                    height = Screen.height;
                }
                catch (Exception ex)
                {
                    Logging.Log.Exception("OutputGeometryPolicy: 读取 Screen 尺寸失败", ex);
                    error = "geometry-legacy-window-size-unavailable";
                    return false;
                }

                if (width < MinimumDimension || height < MinimumDimension)
                {
                    error = "geometry-legacy-window-size-invalid:" +
                            width.ToString(CultureInfo.InvariantCulture) + "x" +
                            height.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                mode = GeometryMode.LegacyWindow;

                // legacy 模式读取到的窗口尺寸仍必须通过硬件能力判定；否则会创建出
                // 超过 maxTextureSize 的 render 尺寸。错误码与自定义模式区分，便于定位。
                if (!TryValidateDimension(width, "legacy-width", out error) ||
                    !TryValidateDimension(height, "legacy-height", out error))
                {
                    return false;
                }
            }

            int scale = input.SupersamplingScale;

            // render = output × scale，checked：溢出即 fail-closed，不 clamp、不降级。
            int renderWidth;
            int renderHeight;
            try
            {
                renderWidth = checked(width * scale);
                renderHeight = checked(height * scale);
            }
            catch (OverflowException)
            {
                error = "geometry-supersampling-scale-overflow:" +
                        width.ToString(CultureInfo.InvariantCulture) + "x" +
                        height.ToString(CultureInfo.InvariantCulture) +
                        "*" + scale.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            // 真实硬件能力对**实际 render 尺寸**生效。
            // scale=1 时 render == output，因此 hardware 判定与第一闭环完全等价。
            if (!TryValidateDimension(renderWidth, "render-width", out error) ||
                !TryValidateDimension(renderHeight, "render-height", out error))
            {
                return false;
            }

            if (!TryBuildDownsampleSteps(width, height, scale, out DownsampleStep[] steps, out error))
                return false;

            resolution = new GeometryResolution(mode, width, height, scale, renderWidth, renderHeight, steps);
            error = null;
            return true;
        }

        /// <summary>
        /// 倍率规划器（纯函数，无 GPU 依赖，可被 harness 全覆盖）。
        ///
        /// factor 以**相对 output 的整数倍率**递减：
        ///   nextFactor = factor / 2 + factor % 2      （即 ceil(factor / 2)）
        /// 每一级尺寸 = (outputWidth × nextFactor, outputHeight × nextFactor)。
        ///
        /// 性质：
        ///   * 1 → 空链（不降采样）。
        ///   * 2 → [2→1]；3 → [3→2, 2→1]；4 → [4→2, 2→1]；5 → [5→3, 3→2, 2→1]。
        ///   * 每相邻两级的比例 ≤ 2:1（因此逐级 bilinear 采样等价于 2×2 box 平均）。
        ///   * 每一级精确保持宽高比（因为每级都是同一个整数 factor 作用于 W 与 H）。
        ///   * 最后一级精确等于 output 尺寸（factor 收敛到 1）。
        /// 非法 / 溢出即 fail-closed，不产生部分链。
        /// </summary>
        internal static bool TryBuildDownsampleSteps(
            int outputWidth, int outputHeight, int scale,
            out DownsampleStep[] steps, out string error)
        {
            error = null;
            steps = EmptySteps;

            if (scale <= 1)
                return true;

            var list = new List<DownsampleStep>();
            int factor = scale;

            while (factor > 1)
            {
                int nextFactor = factor / 2 + factor % 2;

                // 防御性：规划器必须严格递减，否则 fail-closed（绝不死循环）。
                if (nextFactor >= factor || nextFactor < 1)
                {
                    error = "downsample-chain-no-progress:factor=" +
                            factor.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                int levelWidth;
                int levelHeight;
                try
                {
                    levelWidth = checked(outputWidth * nextFactor);
                    levelHeight = checked(outputHeight * nextFactor);
                }
                catch (OverflowException)
                {
                    error = "downsample-chain-level-overflow:factor=" +
                            nextFactor.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                list.Add(new DownsampleStep(levelWidth, levelHeight));
                factor = nextFactor;
            }

            steps = list.ToArray();
            return true;
        }

        /// <summary>
        /// GUI 输入解析：只接受纯十进制正整数（拒绝符号、分隔符、空白与溢出），
        /// 并通过与 preflight / scheduler 完全相同的合法性规则。
        /// </summary>
        internal static bool TryParseDimension(string text, string dimension, out int value, out string error)
        {
            value = 0;
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0 ||
                !int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                error = "geometry-" + dimension + "-not-a-positive-integer";
                return false;
            }

            return TryValidateDimension(value, dimension, out error);
        }

        /// <summary>
        /// GUI 输入解析：超采样倍率的文本入口。解析规则与宽高完全一致
        /// （纯十进制正整数，拒绝符号 / 空白 / 小数 / 指数 / 溢出），
        /// 因此 GUI、preflight 与 scheduler 的合法集合始终相同。
        /// </summary>
        internal static bool TryParseSupersamplingScale(string text, out int value, out string error)
        {
            value = 0;
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0 ||
                !int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                error = "geometry-supersampling-scale-not-a-positive-integer";
                return false;
            }

            return TryValidateSupersamplingScale(value, out error);
        }

        /// <summary>
        /// 倍率合法性：只有「int 可表达的正整数且 ≥ 1」这一条。
        /// **没有产品级上限**：像素总数 / 显存 / 耗时 / 体积都不是判定条件；
        /// 真实边界是 int 表达能力与 maxTextureSize（后者作用于 render 尺寸）。
        /// </summary>
        internal static bool TryValidateSupersamplingScale(int scale, out string error)
        {
            if (scale < MinimumSupersamplingScale)
            {
                error = "geometry-supersampling-scale-not-positive";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// 当前 GPU 的最大纹理边长（真实硬件能力）。读取失败返回 0，
        /// 表示“能力未知”，调用方不得据此判定非法。
        /// </summary>
        internal static int TryReadMaxTextureSize()
        {
            try
            {
                return SystemInfo.maxTextureSize;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>metadata / 日志用的机器可读几何模式标签。</summary>
        internal static string KindLabel(GeometryMode mode)
        {
            return mode == GeometryMode.CustomResolution ? CustomResolutionLabel : LegacyWindowLabel;
        }

        /// <summary>日志 / GUI 用的几何描述，例如 <c>1920x1080 aspect=1.777778</c>。</summary>
        internal static string DescribeGeometry(GeometryResolution resolution)
        {
            return resolution.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                   resolution.Height.ToString(CultureInfo.InvariantCulture) +
                   " aspect=" + resolution.Aspect.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 完整描述（含模式标签 + 超采样 render 尺寸 + 降采样级数），供 session 启动日志使用。
        /// scale=1 时**不**输出任何超采样后缀，保持第一闭环日志文本不变。
        /// </summary>
        internal static string DescribeGeometryWithMode(GeometryResolution resolution)
        {
            string text = KindLabel(resolution.Mode) + " " + DescribeGeometry(resolution);
            if (resolution.Scale <= 1)
                return text;

            return text +
                   " supersamplingScale=" + resolution.Scale.ToString(CultureInfo.InvariantCulture) +
                   " renderSize=" + resolution.RenderWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                   resolution.RenderHeight.ToString(CultureInfo.InvariantCulture) +
                   " downsampleLevels=" + resolution.DownsampleLevelCount.ToString(CultureInfo.InvariantCulture) +
                   " downsampleAlgorithm=" + DownsampleAlgorithmLabel;
        }

        /// <summary>
        /// 单边合法性：正整数，且不超过真实硬件上限。
        /// maxTextureSize 读不到（0）时跳过硬件判定——能力未知不构成非法，
        /// 也不在此处发明一个上限。
        /// </summary>
        private static bool TryValidateDimension(int value, string dimension, out string error)
        {
            if (value < MinimumDimension)
            {
                error = "geometry-" + dimension + "-not-positive";
                return false;
            }

            int maxTextureSize = TryReadMaxTextureSize();
            if (maxTextureSize > 0 && value > maxTextureSize)
            {
                error = "geometry-" + dimension + "-exceeds-hardware-max:" +
                        maxTextureSize.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            error = null;
            return true;
        }
    }
}
