using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 导出协调层（Phase 2.2）。
    ///
    /// 在 F10 / GUI 启动序列与 <see cref="CaptureService.StartSequence"/> 之间
    /// 插入 Preflight 检查。不修改 CaptureService.StartSequence 内部实现。
    ///
    /// StopSequence 路径保持原样，仍直接调用 CaptureService.StopSequence。
    /// </summary>
    internal static class ExportCoordinator
    {
        /// <summary>
        /// 最近一次 Preflight 报告（由 TryStartSequence 写入）。
        /// 用于 <see cref="ExportStateMachine"/> 派生 Failed 状态。
        /// null 表示尚未触发过 Preflight。
        /// </summary>
        internal static PreflightReport LastPreflightReport { get; private set; }

        /// <summary>
        /// 尝试启动序列。先执行即时 Preflight，通过或仅警告时委托
        /// <see cref="CaptureService.StartSequence"/>。
        /// Preflight 失败时不调用 CaptureService.StartSequence。
        /// </summary>
        /// <param name="trigger">触发来源标识，与原 StartSequence 一致："gui" / "hotkey" 等。</param>
        /// <returns>true 表示已委托 StartSequence；false 表示被 Preflight 阻断。</returns>
        public static bool TryStartSequence(string trigger)
        {
            PreflightReport report = Preflight.Run();
            LastPreflightReport = report;

            if (report.Overall == PreflightResult.Fail)
            {
                Log.Warn(UiText.LogPreflightFailed);
                return false;
            }

            if (report.Overall == PreflightResult.Warn)
            {
                Log.Warn(UiText.LogPreflightWarn);
            }
            else
            {
                Log.Info(UiText.LogPreflightPassed);
            }

            CaptureService.StartSequence(trigger);
            return true;
        }

        /// <summary>
        /// 重置最近一次 Preflight 报告（用于 Failed 状态手动复位）。
        /// 不修改 CaptureService 状态。
        /// </summary>
        public static void ResetLastReport()
        {
            LastPreflightReport = null;
        }
    }
}
