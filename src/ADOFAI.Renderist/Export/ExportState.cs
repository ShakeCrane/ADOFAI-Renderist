namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 最小导出状态枚举（Phase 2.2）。
    ///
    /// 仅作为只读视图，由 <see cref="ExportStateMachine"/> 从
    /// <see cref="Capture.CaptureService.IsRecording"/> 与协调层最近一次
    /// Preflight 报告派生。不替代 CaptureService.IsRecording。
    ///
    /// 不实现 Ready / Paused / Cancelled：Phase 2.2 没有这些状态的入口。
    /// 不实现 Completed：停止原因无法在所有路径可靠记录，避免误导。
    /// </summary>
    internal enum ExportState
    {
        /// <summary>空闲：当前未在录制，且最近一次 Preflight 未 Fail。</summary>
        Idle,

        /// <summary>录制中：CaptureService.IsRecording == true。</summary>
        Capturing,

        /// <summary>失败：最近一次 Preflight 返回 Fail。</summary>
        Failed,
    }
}
