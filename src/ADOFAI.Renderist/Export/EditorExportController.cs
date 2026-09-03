using System;
using System.Globalization;
using System.IO;
using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器导出会话控制器（Phase 2.4）。
    ///
    /// 本类只维护会话生命周期与 Tick 计数，不执行截图、不推进游戏时间、不控制相机 / UI。
    ///
    /// 入口：
    ///   * Start：校验就绪并创建会话目录 + 初始 metadata，进入 Running
    ///   * Stop：用户主动停止，Running -> Cleaning -> Completed
    ///   * Cancel：环境失效 / Mod 禁用，进入 Cleaning -> Cancelled
    ///   * Tick：每 OnUpdate 调用，推进 TickCount 并校验环境
    ///
    /// 所有收尾流程幂等，重复调用不会重复破坏状态或抛出异常。
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
        /// 防止重复启动；拒绝时不创建会话目录、不写 metadata。
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

                // Phase 2.4 缺陷修复：使用确定性唯一会话目录。
                // 基准名仍为 editor_<yyyyMMdd_HHmmss>；已存在时自动追加 _001 / _002 ...，
                // 绝不静默复用已存在的会话目录，因此快速 Stop → Start 不会覆盖上一会话 metadata。
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

                // sessionId 使用唯一解析后的真实目录名（与返回目录末段一致），
                // 保证 sessionId 与实际 session directory 的身份语义一致。
                var session = new EditorExportSession(sessionId, dir, report.EditorEnv.SceneName)
                {
                    State = EditorExportState.Preparing,
                    StateDetail = "正在准备会话。",
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
                    session.State = EditorExportState.Failed;
                    session.StateDetail = "写入初始 metadata 失败。";
                    session.EndedAtUtc = DateTime.UtcNow;
                    session.StopReason = "failed";
                    TryWriteMetadataBestEffort(session);
                    return false;
                }

                session.State = EditorExportState.Running;
                session.StateDetail = "会话运行中（Phase 2.4 未实现截图）。";
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
                s.State = EditorExportState.Cleaning;
                s.StateDetail = "正在清理会话（用户停止）。";
                TryWriteMetadataBestEffort(s);

                s.State = EditorExportState.Completed;
                s.StateDetail = "会话已完成（用户停止）。";
                s.EndedAtUtc = DateTime.UtcNow;
                s.StopReason = "user";
                TryWriteMetadataBestEffort(s);
                Log.Info(UiText.LogEditorExportStopped);
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
                s.State = EditorExportState.Cleaning;
                s.StateDetail = "正在清理会话（取消：" + (reason ?? "?") + "）。";
                TryWriteMetadataBestEffort(s);

                s.State = EditorExportState.Cancelled;
                s.EndedAtUtc = DateTime.UtcNow;
                s.StopReason = "cancelled";
                s.StateDetail = "会话已取消：" + (reason ?? "?");
                TryWriteMetadataBestEffort(s);
                Log.Info(UiText.Format(UiText.LogEditorExportCancelledFormat, reason ?? "?"));
            }
            catch (Exception ex)
            {
                Fail(ex, "Cancel 异常: " + (reason ?? "?"));
            }
        }

        /// <summary>每 OnUpdate 调用。只维护生命周期与 Tick 计数，不截图。</summary>
        public static void Tick()
        {
            EditorExportSession s = _session;
            if (s == null) return;
            if (s.State != EditorExportState.Running) return;

            try
            {
                if (!IsEnvironmentStillValid(s, out string reason))
                {
                    Cancel(reason);
                    return;
                }
                s.TickCount++;
            }
            catch (Exception ex)
            {
                Fail(ex, "Tick 异常");
            }
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
                // Phase 2.4 缺陷修复：复核对象是“当前会话自己的固定目录”，
                // 而不是实时全局 Settings.OutputDirectory。
                // 运行中修改 Settings 只影响下一次 Start，
                // 不得因新全局路径非法而取消、切换或改写当前健康会话。
                string sessionDir = s.OutputDirectory;
                if (string.IsNullOrEmpty(sessionDir) || !Directory.Exists(sessionDir))
                {
                    reason = "output-dir-invalid";
                    return false;
                }
            }

            return true;
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
