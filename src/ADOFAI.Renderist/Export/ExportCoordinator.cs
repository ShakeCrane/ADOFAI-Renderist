using System;
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
        /// <summary>
        /// 最近一次单帧截图拒绝原因（Phase 2.2.2）。null 表示未拒绝或已被接受覆盖。
        /// 供 GUI 单帧状态段显示。
        /// </summary>
        internal static string LastSingleCaptureRejectReason { get; private set; }

        /// <summary>
        /// 最近一次单帧截图拒绝时间（本地时间）。null 表示未拒绝。
        /// </summary>
        internal static DateTime? LastSingleCaptureRejectTimeLocal { get; private set; }

        /// <summary>
        /// 尝试单帧截图（Phase 2.2.2）。前置轻量路径检查（ValidateDirectory）。
        /// Reject 时不调用 CaptureService.RequestSingleCaptureNow，不创建 session 目录。
        /// Accept / FallBackToDefault 时委托 CaptureService.RequestSingleCaptureNow（Phase 2.0 既有落盘逻辑）。
        /// </summary>
        /// <param name="trigger">触发来源："hotkey" / "gui" 等。</param>
        /// <returns>true 表示已委托 RequestSingleCaptureNow；false 表示被路径检查阻断。</returns>
        public static bool TrySingleCapture(string trigger)
        {
            if (!CheckSingleCapturePath())
            {
                return false;
            }
            CaptureService.RequestSingleCaptureNow();
            return true;
        }

        /// <summary>
        /// 尝试单帧截图（下一帧延迟，Phase 2.2.2）。前置轻量路径检查。
        /// Reject 时不调用 CaptureService.RequestSingleCaptureNextTick，不排延迟帧。
        /// Accept / FallBackToDefault 时委托 CaptureService.RequestSingleCaptureNextTick。
        /// </summary>
        public static bool TrySingleCaptureNextTick(string trigger)
        {
            if (!CheckSingleCapturePath())
            {
                return false;
            }
            CaptureService.RequestSingleCaptureNextTick();
            return true;
        }

        /// <summary>
        /// 轻量路径检查：只验证 OutputDirectory 合法性。
        /// 不执行完整 Preflight（不检查 CaptureService 状态、不检查 EditorEnv）。
        /// 无副作用：ValidateDirectory 不创建目录、不写文件。
        /// </summary>
        private static bool CheckSingleCapturePath()
        {
            Settings settings = ModEntry.Settings;
            if (settings == null) return false;

            DirectoryValidationResult dirVal = OutputPath.ValidateDirectory(settings.OutputDirectory);
            if (dirVal.Outcome == DirectoryValidationOutcome.Reject)
            {
                LastSingleCaptureRejectReason = dirVal.RejectReason ?? "Unknown";
                LastSingleCaptureRejectTimeLocal = DateTime.Now;
                Log.Warn(UiText.Format(UiText.LogSingleCaptureRejectedFormat, LastSingleCaptureRejectReason));
                return false;
            }

            // Accept / FallBackToDefault：清除拒绝状态
            LastSingleCaptureRejectReason = null;
            LastSingleCaptureRejectTimeLocal = null;
            return true;
        }

        public static void ResetLastReport()
        {
            LastPreflightReport = null;
        }
    }
}
