namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器导出捕获后端抽象占位（Phase 2.4）。
    ///
    /// 本类是未来捕获后端的抽象占位，Phase 2.4 不实现任何真实截图能力。
    ///
    /// 硬性约束：
    ///   * 不调用 ScreenCapture.CaptureScreenshot。
    ///   * 不调用 F9/F10 或 CaptureService 单帧 / 序列入口。
    ///   * 不创建截图文件，不修改捕获状态，不修改 Unity 状态。
    ///   * TryEmitFrame 固定返回未实现结果。
    ///
    /// Controller.Tick 不会每帧自动调用 TryEmitFrame 产生截图请求。
    /// </summary>
    internal static class EditorExportCaptureDriver
    {
        /// <summary>当前是否已实现真实捕获。Phase 2.4 固定为 false。</summary>
        public const bool IsCaptureImplemented = false;

        /// <summary>返回未实现原因，供 GUI / metadata / 日志引用。</summary>
        public static string GetUnavailableReason()
        {
            return "Phase 2.4 编辑器导出骨架：捕获后端尚未实现，不会产生截图。";
        }

        /// <summary>
        /// 尝试发出一帧截图请求。Phase 2.4 固定返回未实现结果，无副作用。
        /// </summary>
        public static EditorExportCaptureAttempt TryEmitFrame()
        {
            return new EditorExportCaptureAttempt
            {
                Success = false,
                Reason = GetUnavailableReason(),
            };
        }
    }

    internal struct EditorExportCaptureAttempt
    {
        /// <summary>是否成功发出截图请求。Phase 2.4 恒为 false。</summary>
        public bool Success;

        /// <summary>未成功时的原因说明。</summary>
        public string Reason;
    }
}
