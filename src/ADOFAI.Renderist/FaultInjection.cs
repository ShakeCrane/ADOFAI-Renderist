// ============================================================================
// TEMPORARY fault injection for 0.3.5.1 stability-fix verification (V1-V13).
// Debug-only (#if DEBUG); NEVER compiled into Release builds.
// 实机验证完成后必须整文件删除，并重新 build / diff / grep 确认正式代码干净。
//
// 注入点（全部一次性：触发即自动复位）：
//   F1   EditorExportController.StartSession
//        —— scheduler TryStart 成功后抛异常（验证统一 cleanup 的 StartSession 异常路径）
//   F2   DeterministicFrameScheduler.RestoreRdcAuto
//        —— 首次 cleanup 尝试失败一次（验证 residual 保留 + 二次调用只补做剩余项）
//   F3a  FrameCaptureDriver.Start —— AddComponent 后、Configure 前抛异常
//   F3b  FrameCaptureDriver.Start —— Configure 成功后、ownership 交接前抛异常
//   F4   RenderistAutoPlay.CatchUp
//        —— 跳过一次官方 Hit（progression 不前进，验证严格单调 fail-closed）
// ============================================================================
#if DEBUG
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist
{
    /// <summary>临时验证开关。仅 Debug 构建存在；Release 中本类型不存在。</summary>
    internal static class FaultInjection
    {
        internal static bool F1_StartSessionAfterSchedulerStart;
        internal static bool F2_RdcAutoRestoreFailOnce;
        internal static bool F3a_CaptureStartAfterAddComponent;
        internal static bool F3b_CaptureStartAfterConfigure;
        internal static bool F4_SkipOneHit;

        /// <summary>一次性消费：触发即复位并打 Warn 日志，便于按顺序执行验证场景。</summary>
        internal static bool Consume(ref bool flag, string name)
        {
            if (!flag) return false;
            flag = false;
            Log.Warn("fault-injection: " + name + " triggered");
            return true;
        }
    }
}
#endif
