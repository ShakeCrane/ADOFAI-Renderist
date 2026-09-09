using System;
using System.Globalization;
using System.IO;
using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器导出会话骨架；当前 Phase 3.2.0 提供 MasterTimeline Deterministic Gameplay Handoff GUI 启动入口。
    ///
    /// 本类维护会话生命周期（Preparing / Running / 终态）并把真实导出工作
    /// 交给 <see cref="DeterministicFrameScheduler"/>：
    ///   * Start：校验就绪 + 创建独立会话目录 + 启动 scheduler
    ///   * Stop：用户主动停止 → scheduler StopNow("user") → 终态 Completed
    ///   * Cancel：环境失效 / Mod 禁用 → scheduler StopNow("cancelled") → 终态 Cancelled
    ///   * Tick：推进 scheduler 并观察其是否进入 Completed / Cancelled / Failed
    ///
    /// 所有收尾流程幂等；不会重复恢复 scheduler 状态。
    /// </summary>
    internal static class EditorExportController
    {
        private static EditorExportSession _session;
        private static int _dirRecheckInterval = 60;

        /// <summary>当前会话（可能为 null 或处于终止状态）。</summary>
        public static EditorExportSession CurrentSession => _session;

        /// <summary>当前状态。无会话时为 Idle。</summary>
        public static EditorExportState CurrentState => _session?.State ?? EditorExportState.Idle;

        /// <summary>是否占用：Preparing / Running / Cleaning。</summary>
        public static bool IsBusy =>
            _session != null &&
            (_session.State == EditorExportState.Preparing ||
             _session.State == EditorExportState.Running ||
             _session.State == EditorExportState.Cleaning);

        /// <summary>最近一次 Start 被拒绝的原因（机器可读短句），null 表示无拒绝或已成功。</summary>
        internal static string LastStartRejectReason { get; private set; }

        /// <summary>
        /// 启动编辑器导出会话。成功返回 true。
        /// 防止重复启动；拒绝时不创建会话目录、不写 metadata、不动游戏状态。
        /// </summary>
        public static bool Start()
        {
            try
            {
                if (IsBusy)
                {
                    LastStartRejectReason = "已有会话进行中";
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, LastStartRejectReason));
                    return false;
                }

                Settings settings = ModEntry.Settings;
                if (settings == null)
                {
                    LastStartRejectReason = "Settings 未加载";
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, LastStartRejectReason));
                    return false;
                }

                if (!settings.EditorExportEnabled)
                {
                    LastStartRejectReason = "实验性开关未启用";
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, LastStartRejectReason));
                    return false;
                }

                EditorExportReadinessReport report = EditorExportPreflight.Run();
                if (report.Readiness != EditorExportReadiness.Ready)
                {
                    LastStartRejectReason = "就绪检查未通过：" + report.Reason;
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, LastStartRejectReason));
                    return false;
                }

                // 确定性唯一会话目录：绝不静默复用已存在的目录。
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string baseSessionName = "editor_" + stamp;
                string dir = OutputPath.ResolveUniqueSessionDirectory(
                    settings.OutputDirectory, baseSessionName, out string sessionId);
                if (string.IsNullOrEmpty(dir))
                {
                    LastStartRejectReason = "输出目录不可用";
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, LastStartRejectReason));
                    return false;
                }

                int outputFps = settings.EditorTargetFrameRate > 0
                    ? settings.EditorTargetFrameRate
                    : DeterministicFrameScheduler.OutputFps;
                int targetFrameCount = DeterministicFrameScheduler.DefaultTargetFrameCount;

                var session = new EditorExportSession(sessionId, dir, report.EditorEnv.SceneName)
                {
                    State = EditorExportState.Preparing,
                    StateDetail = "正在启动确定性帧调度器。",
                    OutputFps = outputFps,
                    TargetFrameCount = targetFrameCount,
                };
                _session = session;

                try
                {
                    session.WriteMetadata();
                }
                catch (Exception ex)
                {
                    LastStartRejectReason = "写入初始 metadata 失败";
                    Log.Exception("EditorExportController: 写入初始 metadata 失败", ex);
                    MarkSessionFailed(session, "写入初始 metadata 失败。", "failed");
                    return false;
                }

                // 启动正式 scheduler。失败时 session 置 Failed，且 scheduler 已恢复。
                string reject = DeterministicFrameScheduler.TryStart(
                    session.OutputDirectory, outputFps, targetFrameCount);
                if (reject != null)
                {
                    LastStartRejectReason = reject;
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, reject));
                    MarkSessionFailed(session, "无法启动确定性帧调度器：" + reject, "failed");
                    return false;
                }

                session.State = EditorExportState.Running;
                session.StateDetail = "确定性帧调度器运行中。";
                TryWriteMetadataBestEffort(session);

                LastStartRejectReason = null;
                Log.Info(UiText.Format(UiText.LogEditorExportStartedFormat, dir));
                return true;
            }
            catch (Exception ex)
            {
                LastStartRejectReason = "Start 异常";
                Log.Exception("EditorExportController.Start 异常", ex);
                Fail(ex, "Start 异常");
                return false;
            }
        }

        /// <summary>用户主动停止。仅 Running 可停止。</summary>
        public static void Stop()
        {
            EditorExportSession s = _session;
            if (s == null) return;
            if (s.State != EditorExportState.Running) return;

            try
            {
                if (DeterministicFrameScheduler.IsRunning)
                {
                    DeterministicFrameScheduler.StopNow("user", "user");
                }
                FinalizeFromScheduler();
            }
            catch (Exception ex)
            {
                Fail(ex, "Stop 异常");
            }
        }

        /// <summary>外部取消：环境失效 / Mod 禁用。对终止状态幂等。</summary>
        public static void Cancel(string reason)
        {
            EditorExportSession s = _session;
            if (s == null) return;
            if (s.State == EditorExportState.Completed ||
                s.State == EditorExportState.Cancelled ||
                s.State == EditorExportState.Failed)
            {
                return;
            }

            try
            {
                string cleanReason = string.IsNullOrEmpty(reason) ? "cancelled" : reason;
                if (DeterministicFrameScheduler.IsRunning)
                {
                    DeterministicFrameScheduler.StopNow("cancelled", cleanReason);
                }
                FinalizeFromScheduler();
            }
            catch (Exception ex)
            {
                Fail(ex, "Cancel 异常: " + (reason ?? "?"));
            }
        }

        /// <summary>每 OnUpdate 调用。推进 scheduler 并观察终态。</summary>
        public static void Tick()
        {
            EditorExportSession s = _session;
            if (s == null) return;
            if (s.State != EditorExportState.Running) return;

            try
            {
                if (!IsEnvironmentStillValid(s, out string reason))
                {
                    DeterministicFrameScheduler.StopNow("cancelled", reason);
                }
                else
                {
                    DeterministicFrameScheduler.Tick();
                }

                s.TickCount++;

                if (DeterministicFrameScheduler.Status == DeterministicFrameScheduler.SchedulerStatus.Completed ||
                    DeterministicFrameScheduler.Status == DeterministicFrameScheduler.SchedulerStatus.Cancelled ||
                    DeterministicFrameScheduler.Status == DeterministicFrameScheduler.SchedulerStatus.Failed)
                {
                    FinalizeFromScheduler();
                }
            }
            catch (Exception ex)
            {
                Fail(ex, "Tick 异常");
            }
        }

        /// <summary>根据 scheduler 的终态回填 session。非终态时无副作用。</summary>
        private static void FinalizeFromScheduler()
        {
            EditorExportSession s = _session;
            if (s == null) return;
            if (s.State != EditorExportState.Running) return;

            DeterministicFrameScheduler.SchedulerStatus status = DeterministicFrameScheduler.Status;
            switch (status)
            {
                case DeterministicFrameScheduler.SchedulerStatus.Completed:
                    s.State = EditorExportState.Completed;
                    s.StopReason = DeterministicFrameScheduler.StopReason ?? "user";
                    s.StateDetail = "导出已完成。";
                    break;
                case DeterministicFrameScheduler.SchedulerStatus.Cancelled:
                    s.State = EditorExportState.Cancelled;
                    s.StopReason = DeterministicFrameScheduler.StopReason ?? "cancelled";
                    s.StateDetail = "导出已取消。";
                    break;
                case DeterministicFrameScheduler.SchedulerStatus.Failed:
                    s.State = EditorExportState.Failed;
                    s.StopReason = DeterministicFrameScheduler.StopReason ?? "failed";
                    s.StateDetail = "导出失败。";
                    break;
                default:
                    return;
            }

            s.EndedAtUtc = DateTime.UtcNow;
            s.CaptureRequestCount = DeterministicFrameScheduler.CaptureRequestCount;
            s.CapturedFrameCount = DeterministicFrameScheduler.CapturedFrameCount;
            TryWriteMetadataBestEffort(s);
            Log.Info(UiText.Format(UiText.LogEditorExportFinishedFormat,
                s.State.ToString(), s.StopReason));
        }

        /// <summary>轻量环境校验：Mod 启用、未离开编辑器、F9/F10 未占用、定期校验当前会话固定目录。</summary>
        private static bool IsEnvironmentStillValid(EditorExportSession s, out string reason)
        {
            reason = null;

            if (!ModEntry.Enabled)
            {
                reason = "mod-disabled";
                return false;
            }

            if (CaptureService.IsRecording)
            {
                reason = "capture-busy";
                return false;
            }

            EditorEnvSnapshot env = EditorEnvSnapshot.Capture();
            if (env.EnvironmentReadFailed || env.Detection != EditorEnvDetection.ProbablyEditor)
            {
                reason = "left-editor";
                return false;
            }

            if (s.TickCount % _dirRecheckInterval == 0)
            {
                string sessionDir = s.OutputDirectory;
                if (string.IsNullOrEmpty(sessionDir) || !Directory.Exists(sessionDir))
                {
                    reason = "output-dir-invalid";
                    return false;
                }
            }

            return true;
        }

        private static void MarkSessionFailed(EditorExportSession s, string detail, string reason)
        {
            s.State = EditorExportState.Failed;
            s.StateDetail = detail;
            s.EndedAtUtc = DateTime.UtcNow;
            s.StopReason = reason;
            TryWriteMetadataBestEffort(s);
        }

        private static void TryWriteMetadataBestEffort(EditorExportSession s)
        {
            try
            {
                s.WriteMetadata();
            }
            catch (Exception ex)
            {
                Log.Exception("EditorExportController: metadata 写入失败（best-effort）", ex);
            }
        }

        private static void Fail(Exception ex, string context)
        {
            // 任何 controller 层未处理异常都必须保证 scheduler 回到恢复态。
            if (DeterministicFrameScheduler.IsRunning)
            {
                try { DeterministicFrameScheduler.StopNow("cancelled", "controller-fail"); } catch { }
            }

            EditorExportSession s = _session;
            if (s == null)
            {
                Log.Exception("EditorExportController: " + context, ex);
                return;
            }

            s.State = EditorExportState.Failed;
            s.EndedAtUtc = DateTime.UtcNow;
            s.StopReason = "failed";
            s.StateDetail = "会话失败：" + context;
            TryWriteMetadataBestEffort(s);
            Log.Exception("EditorExportController: " + context, ex);
        }
    }
}
