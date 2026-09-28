namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 本 session 冻结的**唯一**输出模式。三种模式互斥：PNG / MP4 / Log-only。
    ///
    /// 这是 output mode 的唯一 authority：
    ///   * 旧的 <c>EditorImageOutputEnabled</c> 只作为迁移输入，不再是第二个 authority；
    ///   * session metadata 里的 image-output 相关字段一律由**冻结后的**模式派生。
    /// </summary>
    internal enum CaptureOutputMode
    {
        /// <summary>PNG 序列（image output enabled）。</summary>
        PngSequence = 0,

        /// <summary>L3-A：RGB24 帧事务交付到唯一 L2 FFmpeg 管线的 MP4 方向。</summary>
        Mp4Rgb24 = 1,

        /// <summary>Log-only：帧事务照常，但绝不读回图像 / 不写盘 / 不创建 FFmpeg 进程。</summary>
        LogOnly = 2,
    }

    /// <summary>
    /// 输出模式的解析、迁移与标签（Unity-free，可被独立 net48 harness 直接覆盖）。
    ///
    /// 持久化字段是三态：<see cref="UnsetSentinel"/> = 尚未迁移（由旧布尔派生）；其余必须是
    /// <see cref="CaptureOutputMode"/> 的合法值。**非法 persisted 值不自动修复**：保持非法并
    /// fail-closed，直到用户显式改正（与 persisted End Tail / 几何的方案 A 语义一致）。
    /// </summary>
    internal static class OutputModePolicy
    {
        /// <summary>持久化字段的"尚未迁移"哨兵。刻意不用 0（= PngSequence）以区分"未设置"。</summary>
        internal const int UnsetSentinel = -1;

        internal const string PngLabel = "png-sequence";
        internal const string Mp4Label = "mp4-rgb24";
        internal const string LogOnlyLabel = "log-only";

        /// <summary>旧设置的迁移规则：true → PNG，false → Log-only。MP4 只能由用户显式选择。</summary>
        internal static CaptureOutputMode MigrateFromLegacy(bool legacyImageOutputEnabled)
        {
            return legacyImageOutputEnabled ? CaptureOutputMode.PngSequence : CaptureOutputMode.LogOnly;
        }

        internal static bool IsDefined(int persisted)
        {
            return persisted == (int)CaptureOutputMode.PngSequence ||
                   persisted == (int)CaptureOutputMode.Mp4Rgb24 ||
                   persisted == (int)CaptureOutputMode.LogOnly;
        }

        /// <summary>
        /// 解析本 session 的输出模式。未迁移时按旧布尔迁移（<paramref name="migrated"/> = true），
        /// 调用方负责把迁移结果持久化。
        /// </summary>
        internal static bool TryResolve(
            int persistedMode, bool legacyImageOutputEnabled,
            out CaptureOutputMode mode, out bool migrated, out string error)
        {
            mode = CaptureOutputMode.PngSequence;
            migrated = false;
            error = null;

            if (persistedMode == UnsetSentinel)
            {
                mode = MigrateFromLegacy(legacyImageOutputEnabled);
                migrated = true;
                return true;
            }

            if (!IsDefined(persistedMode))
            {
                error = "output-mode-invalid:" + persistedMode;
                return false;
            }

            mode = (CaptureOutputMode)persistedMode;
            return true;
        }

        /// <summary>image output（读回图像并写出文件）是否启用。metadata 的布尔字段由此派生。</summary>
        internal static bool IsImageOutput(CaptureOutputMode mode)
        {
            return mode == CaptureOutputMode.PngSequence;
        }

        internal static bool IsMp4(CaptureOutputMode mode)
        {
            return mode == CaptureOutputMode.Mp4Rgb24;
        }

        // context 是 MP4 的资源，不参与决定模式。缺失或误 Arm 均在 Play 前拒绝。
        internal static bool TryValidateRuntimeBinding(CaptureOutputMode mode, bool armed,
            bool hasContext, out string error)
        {
            error = null;
            if (!IsDefined((int)mode)) { error = "output-mode-invalid"; return false; }
            if (IsMp4(mode) && (!armed || !hasContext))
            { error = "mp4-delivery-context-missing"; return false; }
            if (!IsMp4(mode) && (armed || hasContext))
            { error = "non-mp4-delivery-context-present"; return false; }
            return true;
        }

        internal static string Label(CaptureOutputMode mode)
        {
            switch (mode)
            {
                case CaptureOutputMode.PngSequence: return PngLabel;
                case CaptureOutputMode.Mp4Rgb24: return Mp4Label;
                case CaptureOutputMode.LogOnly: return LogOnlyLabel;
                default: return "unknown";
            }
        }
    }
}
