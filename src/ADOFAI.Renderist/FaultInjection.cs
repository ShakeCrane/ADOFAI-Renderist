// ============================================================================
// TEMPORARY fault injection for 0.3.5.1 stability-fix verification (V1-V13).
// Debug-only (#if DEBUG); NEVER compiled into Release builds.
// 实机验证完成后必须整文件删除，并重新 build / diff / grep 确认正式代码干净。
//
// 注入点：
//   F1   EditorExportController.StartSession（一次性：触发即自动复位）
//        —— scheduler TryStart 成功后抛异常（验证统一 cleanup 的 StartSession 异常路径）
//   F2   DeterministicFrameScheduler.RestoreRdcAuto（持续失败开关：不自动复位）
//        —— 开启期间每一次 RestoreRdcAuto 都直接失败，直到用户手动关闭。
//           Consume 式一次性失败无法验证 AR-REV-002：EnsureCleanedUp → StopNow 的
//           第一次 RestoreAll 消费掉开关后，同一次 EnsureCleanedUp 内的第二次
//           RestoreAll 可能立即成功，residual 未必保留到下一次 Start / Cancel /
//           Mod disable。持续开关使 RDC.auto residual ownership 的跨调用保留成为
//           可观测事实（每次被阻断都会打日志）。
//   F3a  FrameCaptureDriver.Start —— AddComponent 后、Configure 前抛异常（一次性）
//   F3b  FrameCaptureDriver.Start —— Configure 成功后、ownership 交接前抛异常（一次性）
//   F4   RenderistAutoPlay.CatchUp（一次性）
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
        /// <summary>F2 持续失败开关：true 期间每一次 RestoreRdcAuto 都失败，不自动复位。</summary>
        internal static bool F2_RdcAutoRestoreForceFail;
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

        /// <summary>
        /// 持续失败开关（与 Consume 相对）：不消费、不复位，每次调用都返回当前开关状态；
        /// 处于开启状态时每次都打 Warn 日志，作为“本注入点正在阻断恢复”的持续证据。
        /// 只有用户手动关闭开关后，下一次 cleanup retry 才被允许成功。
        /// </summary>
        internal static bool Persistent(string name, bool flag)
        {
            if (!flag) return false;
            Log.Warn("fault-injection: " + name + " force-fail active, blocking restore");
            return true;
        }
    }
}
#endif
