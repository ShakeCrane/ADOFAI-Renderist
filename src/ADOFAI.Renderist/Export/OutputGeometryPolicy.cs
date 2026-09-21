using System;
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
        internal GeometryInput(bool customResolutionEnabled, int width, int height)
        {
            CustomResolutionEnabled = customResolutionEnabled;
            Width = width;
            Height = height;
        }

        internal bool CustomResolutionEnabled { get; }

        internal int Width { get; }

        internal int Height { get; }
    }

    /// <summary>session 开始时冻结的输出几何解析结果。</summary>
    internal readonly struct GeometryResolution
    {
        internal GeometryResolution(GeometryMode mode, int width, int height)
        {
            Mode = mode;
            Width = width;
            Height = height;
        }

        internal GeometryMode Mode { get; }

        internal int Width { get; }

        internal int Height { get; }

        /// <summary>
        /// 统一输出 aspect = width / height。三台原生 Camera 与 capture RenderTexture
        /// 都使用这同一个值（Renderist 不再按屏幕 aspect 推断任何东西）。
        /// </summary>
        internal double Aspect => (double)Width / (double)Height;
    }

    /// <summary>
    /// 输出分辨率的唯一判定 / 解析 / 派生点（Phase 3.7.0 Custom Resolution）。
    ///
    /// 语义：
    ///   * 自定义分辨率关闭（默认）→ 沿用 Screen.width / Screen.height，
    ///     保持 0.3.6.4 的渲染分辨率行为。
    ///   * 自定义分辨率开启 → 使用用户指定的正整数宽高，不 clamp、不取整、
    ///     不替换为默认值，也不因宽高比“看起来不合理”而拒绝。
    ///
    /// 合法性只包含两类**真实**约束：
    ///   * 表达能力：Settings 字段与 Unity RenderTexture API 都是 int，
    ///     宽高必须是 int 可表达的正整数。
    ///   * 硬件能力：不得超过当前 GPU 的 SystemInfo.maxTextureSize。
    ///
    /// 本层**不**定义任何人为性能上限。像素总数、显存估算、预计编码耗时、磁盘占用与
    /// 预计导出时长都不是合法性条件；超出硬件能力时 RenderTexture 创建本身仍是最终
    /// backstop（见 FrameCaptureDriver 的 capture-target-create-failed）。
    ///
    /// 消费方：
    ///   * Settings.EditorCustomResolution* —— 唯一配置来源
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

        /// <summary>
        /// 宽高必须是正整数：Unity 不接受 0 或负尺寸的 RenderTexture，
        /// Time / 屏幕 API 也不存在 0 宽窗口的合法渲染结果。
        /// </summary>
        internal const int MinimumDimension = 1;

        /// <summary>metadata / 日志中表示“沿用窗口”的机器可读标签。</summary>
        internal const string LegacyWindowLabel = "legacy-window";

        /// <summary>metadata / 日志中表示“使用自定义分辨率”的机器可读标签。</summary>
        internal const string CustomResolutionLabel = "custom-resolution";

        /// <summary>
        /// 校验 persisted 输入的结构合法性（表达能力 + 硬件能力）。
        /// 关闭自定义分辨率时恒为合法：此时 Width / Height 不参与导出，
        /// 因此非法 persisted 宽高也不构成阻断条件。
        /// </summary>
        internal static bool TryValidateInput(GeometryInput input, out string error)
        {
            error = null;
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

            if (input.CustomResolutionEnabled)
            {
                resolution = new GeometryResolution(GeometryMode.CustomResolution, input.Width, input.Height);
                return true;
            }

            int width;
            int height;
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

            resolution = new GeometryResolution(GeometryMode.LegacyWindow, width, height);
            error = null;
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

        /// <summary>完整描述（含模式标签），供 session 启动日志使用。</summary>
        internal static string DescribeGeometryWithMode(GeometryResolution resolution)
        {
            return KindLabel(resolution.Mode) + " " + DescribeGeometry(resolution);
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
