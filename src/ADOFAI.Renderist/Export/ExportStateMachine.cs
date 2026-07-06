using ADOFAI.Renderist.Capture;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 只读导出状态视图（Phase 2.2）。
    ///
    /// 从 <see cref="CaptureService.IsRecording"/> 与协调层最近一次 Preflight
    /// 报告派生。不修改 CaptureService 状态，不替代 IsRecording。
    /// </summary>
    internal static class ExportStateMachine
    {
        /// <summary>
        /// 当前导出状态（只读派生）。
        /// - Capturing：CaptureService.IsRecording == true
        /// - Failed：最近一次 Preflight 返回 Fail
        /// - Idle：其他
        /// </summary>
        public static ExportState CurrentState
        {
            get
            {
                if (CaptureService.IsRecording)
                {
                    return ExportState.Capturing;
                }

                if (ExportCoordinator.LastPreflightReport != null &&
                    ExportCoordinator.LastPreflightReport.Overall == PreflightResult.Fail)
                {
                    return ExportState.Failed;
                }

                return ExportState.Idle;
            }
        }
    }
}
