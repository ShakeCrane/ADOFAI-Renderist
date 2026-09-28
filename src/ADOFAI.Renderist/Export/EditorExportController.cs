using System;
using System.Globalization;
using System.IO;
using ADOFAI.Renderist.Ffmpeg;
using ADOFAI.Renderist.Logging;
using UnityEngine;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器确定性导出会话（Phase 3.4.0）。
    ///
    /// 本类维护会话生命周期（Preparing / Running / 终态）并把真实导出工作
    /// 交给 <see cref="DeterministicFrameScheduler"/>：
    ///   * Start：校验就绪 + 创建独立会话目录 + 启动 scheduler
    ///   * Stop：用户主动停止 → scheduler StopNow("user", "user-stop") → 终态 Cancelled
    ///   * Cancel：环境失效 / Mod 禁用 → scheduler StopNow("cancelled") → 终态 Cancelled
    ///   * Tick：推进 scheduler 并观察 canonical completion / tail / safety 终态
    ///
    /// 所有收尾流程幂等；不会重复恢复 scheduler 状态。
    /// </summary>
    internal static class EditorExportController
    {
        private static EditorExportSession _session;
        private static int _dirRecheckInterval = 60;
        private const double TerminalRearmTimeoutSeconds = 2.0;
        private static bool _terminalRearmPending;
        private static double _terminalRearmDeadlineRealtime;

        /// <summary>
        /// 本 session 冻结的输出模式（唯一 authority）。只在 StartSession 内一次性解析。
        /// 运行中修改 Settings 不影响本 session。
        /// </summary>
        private static CaptureOutputMode _frozenOutputMode = CaptureOutputMode.PngSequence;

        /// <summary>
        /// L3-A 启动阶段的收敛槽：pipeline 已构造/已启动，但尚未被 scheduler 接管（Arm 失败或
        /// TryStart 被拒绝）时，由本槽持有同一个 <see cref="Rgb24DeliveryContext"/>。
        /// 它**不是**第二套 process owner：收敛仍然全部经 L2 自己的 Cancel / CleanupTask，
        /// 只是把 residual 门禁与重试入口接到既有 Tick 上。
        /// </summary>
        private static Rgb24DeliveryContext _startupContext;

        /// <summary>本 session 冻结的输出模式（唯一 authority）。</summary>
        public static CaptureOutputMode FrozenOutputMode => _frozenOutputMode;

        /// <summary>当前会话（可能为 null 或处于终止状态）。</summary>
        public static EditorExportSession CurrentSession => _session;

        /// <summary>当前状态。无会话时为 Idle。</summary>
        public static EditorExportState CurrentState =>
            _terminalRearmPending ? EditorExportState.Preparing : _session?.State ?? EditorExportState.Idle;

        /// <summary>是否仍占用：Preparing / Running，或终态 RGB24 资源仍在收敛。</summary>
        public static bool IsBusy =>
            _terminalRearmPending || _startupContext != null ||
            DeterministicFrameScheduler.HasPendingRgb24Cleanup ||
            (_session != null &&
             (_session.State == EditorExportState.Preparing ||
              _session.State == EditorExportState.Running));

        private static bool HasTerminalSession =>
            _session != null &&
            (_session.State == EditorExportState.Completed ||
             _session.State == EditorExportState.Cancelled ||
             _session.State == EditorExportState.Failed);

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

                // terminal session 只是上一轮结果；只有 residual gate 通过后，
                // 才允许把正常的 ADOFAI controller Fail/Fail2 交给新一轮 editor.Play() 重置。
                bool allowExpectedTerminalControllerFail = HasTerminalSession;

                // scheduler 的 residual / game-state gate 必须在创建 session 目录前完成。
                string schedulerReject = DeterministicFrameScheduler.ValidatePreStartConditions(
                    allowExpectedTerminalControllerFail);
                if (schedulerReject != null)
                {
                    LastStartRejectReason = schedulerReject;
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, schedulerReject));
                    return false;
                }

                // ADOFAI 在上一轮失败/完成后可能仍处于 terminal Fail/Fail2。
                // 该状态迁移是异步的，不能在同一调用中 Play 两次，也不能先创建
                // 一个注定失败的 Renderist session；先只请求官方 re-arm，后续
                // Tick 确认 Start 后再进入正常 session / editor.Play 流程。
                if (allowExpectedTerminalControllerFail &&
                    DeterministicFrameScheduler.IsTerminalControllerFailure)
                {
                    if (!BeginTerminalControllerRearm(out string rearmError))
                    {
                        LastStartRejectReason = rearmError;
                        Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, rearmError));
                        return false;
                    }

                    _terminalRearmPending = true;
                    _terminalRearmDeadlineRealtime =
                        Time.realtimeSinceStartupAsDouble + TerminalRearmTimeoutSeconds;
                    LastStartRejectReason = null;
                    return true;
                }

                return StartSession(settings, report);
            }
            catch (Exception ex)
            {
                LastStartRejectReason = "Start 异常";
                Log.Exception("EditorExportController.Start 异常", ex);
                Fail(ex, "Start 异常");
                return false;
            }
        }

        private static bool BeginTerminalControllerRearm(out string error)
        {
            error = null;
            if (!DeterministicFrameScheduler.IsTerminalControllerFailure)
            {
                error = "controller-state-changed";
                return false;
            }

            return DeterministicFrameScheduler.TryBeginTerminalControllerRearm(out error);
        }

        /// <summary>
        /// L3-A：把启动阶段尚未被 scheduler 接管的 context 收敛掉（幂等、非阻塞）。
        /// 收敛全部经 <see cref="Rgb24DeliveryContext.TryStopAndDrain"/>：它转发既有 L2 Cancel，
        /// 并分别检查 frame Completion 与 pipeline CleanupTask。未收敛前保持占用（IsBusy）。
        /// </summary>
        private static void TickStartupContextConvergence()
        {
            Rgb24DeliveryContext context = _startupContext;
            if (context == null) return;

            string error;
            if (context.TryStopAndDrain("mp4-startup-abandoned", out error))
            {
                _startupContext = null;
                Log.Info("EditorExportController: L3-A 启动阶段 context 已收敛。");
                return;
            }

            Log.Debug("EditorExportController: L3-A 启动阶段 context 仍在收敛： " + (error ?? "unknown"));
        }

        // 失败路径统一先尝试收敛；未能收敛时保留引用，由 Tick 继续 + IsBusy 门禁。
        private static void RetireStartupContext(Rgb24DeliveryContext context)
        {
            if (context == null) return;
            string error;
            if (context.TryStopAndDrain("mp4-startup-failed", out error))
                return;
            _startupContext = context;
        }

        /// <summary>
        /// L3-A：在 editor.Play() / <c>TryStart</c> **之前**建立本 session 的 MP4 交付链路。
        ///
        /// 顺序：冻结参数与 L1 Ready 身份 → 解析唯一最终路径 → 构造唯一 L2 pipeline →
        /// 建立 Rgb24DeliveryContext（此刻在主线程捕获 SynchronizationContext）→ Start pipeline
        /// → Arm。任一失败都不会留下没有 owner 的已启动进程：context 建立之前失败时进程尚未
        /// 启动；之后失败时经 context 的 TryStopAndDrain 收敛。
        /// </summary>
        private static bool TryStartMp4Session(
            EditorExportSession session, GeometryResolution geometry, int outputFps, Settings settings,
            out Rgb24DeliveryContext delivery, out string error)
        {
            delivery = null;
            error = null;

            FfmpegComponentReport report;
            if (!ModEntry.TryGetFfmpegComponentReport(out report))
            {
                error = "ffmpeg-report-unavailable";
                return false;
            }

            var inputs = new Mp4StartupInputs
            {
                OutputWidth = geometry.Width,
                OutputHeight = geometry.Height,
                OutputFps = outputFps,
                Crf = settings.EditorMp4Crf,
                Preset = settings.EditorMp4Preset,
                SessionDirectory = session.OutputDirectory,
                Report = report,
            };

            Mp4StartupResult frozen = Mp4SessionStartup.Freeze(inputs);
            if (!frozen.Succeeded)
            {
                error = frozen.ErrorCode +
                        (string.IsNullOrEmpty(frozen.ErrorDetail) ? string.Empty : (":" + frozen.ErrorDetail));
                return false;
            }

            session.FinalVideoPath = frozen.FinalVideoPath;
            session.FfmpegExecutablePath = frozen.Identity.ExecutablePath;
            session.FfmpegExecutableSha256 = frozen.Identity.ExecutableSha256;
            session.FfmpegVersionLine = frozen.Identity.VersionLine;
            session.Mp4Crf = frozen.Settings.Crf;
            session.Mp4Preset = frozen.Settings.Preset;
            session.Mp4InputPixelFormat = "rgb24";
            session.Mp4OutputPixelFormatPolicy = "auto-by-geometry";

            Mp4SessionBinding binding = Mp4SessionStartup.Bind(
                frozen, geometry.Width, geometry.Height,
                FfmpegPipelineFactory.Instance, DefaultMp4DeliveryFactory.Instance);
            if (!binding.Succeeded)
            {
                _startupContext = binding.PendingConvergence;
                error = binding.ErrorCode +
                        (string.IsNullOrEmpty(binding.ErrorDetail) ? string.Empty : (":" + binding.ErrorDetail));
                return false;
            }

            Log.Info("EditorExportController: MP4 FFmpeg executable=" + frozen.Identity.ExecutablePath +
                     " arguments=" + binding.Pipeline.EncodeArguments);

            string armError;
            if (!DeterministicFrameScheduler.ArmRgb24Delivery(binding.Context, out armError))
            {
                _startupContext = Mp4SessionStartup.RetireOrKeep(binding.Context);
                error = "mp4-arm-failed:" + (armError ?? "unknown");
                return false;
            }

            delivery = binding.Context;
            return true;
        }

        /// <summary>
        /// 只有完成 terminal controller re-arm 且重新通过普通 gate 后，才创建
        /// session 目录并进入唯一一次 editor.Play()。
        /// </summary>
        private static bool StartSession(Settings settings, EditorExportReadinessReport report)
        {
            EditorExportSession session = null;
            try
            {
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

                // 与 preflight 共用同一范围规则（OutputFpsPolicy）；非法值在这里
                // 不会被静默替换为可用值，而是交由 scheduler 的启动 gate fail-closed。
                // 该 fallback 只保留原有语义：非法/缺失时沿用上一次会话的 outputFps
                // 作为 metadata 记录值；真正决定能否启动的是 scheduler gate。
                int outputFps = OutputFpsPolicy.IsValid(settings.EditorTargetFrameRate)
                    ? settings.EditorTargetFrameRate
                    : DeterministicFrameScheduler.OutputFps;
                // safety 默认未配置（unbounded）：0 表示不存在总帧数 / 总时长上限。
                // authority 是 scheduler 自己用同一个纯函数解析的结果；这里先按同一
                // 配置值解析一次，用于启动前写 metadata。
                int configuredSafetyFrameLimit = settings.EditorExportSafetyFrameLimit;
                SafetyLimitResolution safety = SafetyFrameLimitPolicy.Resolve(configuredSafetyFrameLimit);
                var endTailInput = new EndTailInput(
                    settings.EditorEndTailValue, settings.EditorEndTailUnit);

                // 输出模式在 session 开始时一次性冻结，并且是**唯一** authority：
                // 旧的 EditorImageOutputEnabled 只作为一次性迁移输入（true → PNG，
                // false → Log-only）；MP4 只能由用户显式选择。此后 image-output 相关字段
                // 一律由冻结后的模式派生，不再是第二个模式 authority。
                CaptureOutputMode outputMode;
                bool modeMigrated;
                string modeError;
                if (!settings.TryResolveOutputMode(out outputMode, out modeMigrated, out modeError))
                {
                    LastStartRejectReason = "输出模式非法：" + modeError;
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, LastStartRejectReason));
                    return false;
                }

                if (modeMigrated)
                {
                    settings.EditorOutputModeValue = (int)outputMode;
                    try { settings.Save(ModEntry.Mod); }
                    catch (Exception migrateEx)
                    {
                        Log.Exception("EditorExportController: 输出模式迁移保存失败（本 session 仍继续）", migrateEx);
                    }
                    Log.Info("EditorExportController: output mode migrated from legacy image-output flag -> " +
                             OutputModePolicy.Label(outputMode));
                }

                _frozenOutputMode = outputMode;

                // 输出几何同样在 session 开始时一次性冻结。authority 是 scheduler 自己用
                // 同一个纯函数解析的结果；这里先按同一配置解析一次，用于启动前写 metadata。
                var geometryInput = new GeometryInput(
                    settings.EditorCustomResolutionEnabled,
                    settings.EditorCustomResolutionWidth,
                    settings.EditorCustomResolutionHeight,
                    settings.EditorSupersamplingScale);
                OutputGeometryPolicy.TryResolve(
                    geometryInput, out GeometryResolution geometry, out _);

                session = new EditorExportSession(sessionId, dir, report.EditorEnv.SceneName)
                {
                    State = EditorExportState.Preparing,
                    StateDetail = "正在启动确定性帧调度器。",
                    OutputFps = outputFps,
                    OutputModeKind = outputMode,
                    // L3-A 终态边界：本轮没有 Finalizing，因此本 session 到达终态时不会产出成品。
                    FinalizingNotImplemented = OutputModePolicy.IsMp4(outputMode),
                    OutputGeometryMode = OutputGeometryPolicy.KindLabel(geometry.Mode),
                    GeometryCustomResolutionEnabled = geometryInput.CustomResolutionEnabled,
                    GeometryConfiguredWidth = geometryInput.Width,
                    GeometryConfiguredHeight = geometryInput.Height,
                    GeometryConfiguredSupersamplingScale = geometryInput.SupersamplingScale,
                    SupersamplingScale = geometry.Scale,
                    RenderWidth = geometry.RenderWidth,
                    RenderHeight = geometry.RenderHeight,
                    DownsampleAlgorithm = OutputGeometryPolicy.DownsampleAlgorithmLabel,
                    // DownsampleLevelCount 刻意不在此处写入：本方法的初值 0 才真实
                    // （此刻尚未激活 Source，链也尚未创建）。实际级数由 scheduler 在
                    // activation 成功后快照，并在终态 metadata 中回填。
                    OutputWidth = geometry.Width,
                    OutputHeight = geometry.Height,
                    OutputAspect = geometry.Aspect,
                    SafetyPolicy = SafetyFrameLimitPolicy.KindLabel(safety.Kind),
                    SafetyFrameLimit = safety.FrameLimit > 0 ? safety.FrameLimit : (long?)null,
                    SafetyDurationSeconds = safety.FrameLimit > 0
                        ? safety.FrameLimit / (double)outputFps
                        : (double?)null,
                    EndTailInputValue = endTailInput.Value,
                    EndTailInputUnit = endTailInput.Unit.ToString(),
                    ResolvedTailFrames = report.ResolvedTailFrames,
                    ResolvedTailSeconds = report.ResolvedTailSeconds,
                    ResolvedTailBeats = report.ResolvedTailBeats,
                    CompletionBpm = report.CompletionBpm,
                    Pitch = report.Pitch,
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

                // ---- L3-A：MP4 交付链路必须在 editor.Play() / TryStart 之前就绪 ----
                // 顺序严格：冻结参数与 L1 Ready 身份 → 唯一最终路径 → 唯一 L2 pipeline →
                // Rgb24DeliveryContext（主线程捕获 SynchronizationContext）→ Start → Arm。
                // 绝不出现"editor.Play() 已开始但 pipeline/context 尚未 Ready"。
                Rgb24DeliveryContext delivery = null;
                if (OutputModePolicy.IsMp4(outputMode))
                {
                    if (!TryStartMp4Session(session, geometry, outputFps, settings, out delivery,
                            out string mp4Error))
                    {
                        LastStartRejectReason = "MP4 启动失败：" + mp4Error;
                        Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, LastStartRejectReason));
                        MarkSessionFailed(session, "无法建立 MP4 帧事务链路：" + mp4Error, "mp4-startup-failed");
                        // 已 Arm 的 context 已在失败路径退休；若仍有 residual，由 Tick 继续收敛。
                        TickStartupContextConvergence();
                        return false;
                    }

                    TryWriteMetadataBestEffort(session);
                }

                // 这里是 terminal re-arm 后的正常路径；只允许一次 official Play。
                string reject = DeterministicFrameScheduler.TryStart(
                    session.OutputDirectory, outputFps, configuredSafetyFrameLimit, endTailInput,
                    geometryInput, outputMode, false);
                if (reject != null)
                {
                    LastStartRejectReason = reject;
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, reject));
                    if (delivery != null) RetireStartupContext(delivery);
                    TickStartupContextConvergence();
                    MarkSessionFailed(session, "无法启动确定性帧调度器：" + reject, "failed");
                    return false;
                }

                CopyFrozenPolicyFromScheduler(session);
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
                Log.Exception("EditorExportController.StartSession 异常", ex);
                // TryStart 成功后的异常不得留下 scheduler ownership：session 终态只是
                // 结果记录，不代表 scheduler 干净。先收敛 ownership 再标记 session 失败；
                // cleanup 自身的失败只记录，不掩盖原始启动异常，residual 留待下次 retry。
                try
                {
                    DeterministicFrameScheduler.EnsureCleanedUp("failed", "controller-fail");
                }
                catch (Exception cleanupEx)
                {
                    Log.Exception("EditorExportController.StartSession 异常后 scheduler cleanup 失败", cleanupEx);
                }
                if (session != null)
                    MarkSessionFailed(session, "会话启动异常。", "controller-fail");
                return false;
            }
        }

        private static void RejectTerminalControllerRearm(string reason)
        {
            _terminalRearmPending = false;
            _terminalRearmDeadlineRealtime = 0.0;
            LastStartRejectReason = reason;
            Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, reason));
        }

        private static void CancelTerminalControllerRearm(string reason)
        {
            _terminalRearmPending = false;
            _terminalRearmDeadlineRealtime = 0.0;
            Log.Info("EditorExportController: terminal controller re-arm cancelled: " +
                     (string.IsNullOrEmpty(reason) ? "cancelled" : reason));
        }

        private static void TickTerminalControllerRearm()
        {
            if (!_terminalRearmPending) return;

            try
            {
                if (!ModEntry.Enabled)
                {
                    CancelTerminalControllerRearm("mod-disabled");
                    return;
                }

                if (Time.realtimeSinceStartupAsDouble > _terminalRearmDeadlineRealtime)
                {
                    RejectTerminalControllerRearm("terminal-controller-rearm-timeout");
                    return;
                }

                if (!DeterministicFrameScheduler.IsControllerStartState)
                    return;

                Settings settings = ModEntry.Settings;
                if (settings == null)
                {
                    RejectTerminalControllerRearm("Settings 未加载");
                    return;
                }
                if (!settings.EditorExportEnabled)
                {
                    RejectTerminalControllerRearm("实验性开关未启用");
                    return;
                }

                EditorExportReadinessReport report = EditorExportPreflight.Run();
                if (report.Readiness != EditorExportReadiness.Ready)
                {
                    RejectTerminalControllerRearm("就绪检查未通过：" + report.Reason);
                    return;
                }

                // re-arm 完成后回到普通安全门；这里不再允许 Fail/Fail2。
                string schedulerReject = DeterministicFrameScheduler.ValidatePreStartConditions();
                if (schedulerReject != null)
                {
                    RejectTerminalControllerRearm(schedulerReject);
                    return;
                }

                Log.Info("EditorExportController: terminal controller re-arm confirmed: state=Start; beginning normal session");
                _terminalRearmPending = false;
                _terminalRearmDeadlineRealtime = 0.0;
                StartSession(settings, report);
            }
            catch (Exception ex)
            {
                _terminalRearmPending = false;
                _terminalRearmDeadlineRealtime = 0.0;
                LastStartRejectReason = "terminal-controller-rearm-exception";
                Log.Exception("EditorExportController: terminal controller re-arm Tick 异常", ex);
            }
        }

        /// <summary>
        /// 用户主动停止。仅 Running 可停止；terminal session 不改写记录，
        /// 但 scheduler 可能仍持有 residual ownership，需经统一入口收敛。
        /// </summary>
        public static void Stop()
        {
            if (_terminalRearmPending)
            {
                CancelTerminalControllerRearm("user-stop");
                return;
            }

            EditorExportSession s = _session;
            if (s == null) return;
            if (s.State != EditorExportState.Running)
            {
                // 与 Cancel 的 terminal 分支同一 invariant：正常 Completed/Cancelled/Failed
                // 且已干净时此处为 no-op；存在 residual（StartSession 异常路径 / 此前
                // cleanup 失败）时补做收敛，retry 能力由 EnsureCleanedUp 保证。
                try
                {
                    DeterministicFrameScheduler.EnsureCleanedUp("cancelled", "user-stop");
                }
                catch (Exception cleanupEx)
                {
                    Log.Exception("EditorExportController.Stop terminal cleanup 失败", cleanupEx);
                }
                return;
            }

            try
            {
                if (DeterministicFrameScheduler.IsRunning)
                {
                    DeterministicFrameScheduler.StopNow("user", "user-stop");
                }
                FinalizeFromScheduler();
            }
            catch (Exception ex)
            {
                Fail(ex, "Stop 异常");
            }
        }

        /// <summary>
        /// 外部取消：环境失效 / Mod 禁用。对终止状态幂等。
        /// terminal session 不再改写状态记录，但 scheduler 可能仍持有 residual
        /// ownership（StartSession 异常路径 / 此前 cleanup 失败），必须尝试收敛——
        /// 这是 Mod disable 链路（OnToggle(false) → Cancel → UnpatchAll）中非 Harmony
        /// ownership（camera / Unity timing / RDC / selection / capture host）唯一的
        /// 恢复机会，不得因 terminal 而跳过。
        /// </summary>
        public static void Cancel(string reason)
        {
            if (_terminalRearmPending)
            {
                CancelTerminalControllerRearm(reason);
                return;
            }

            EditorExportSession s = _session;
            if (s == null) return;
            if (s.State == EditorExportState.Completed ||
                s.State == EditorExportState.Cancelled ||
                s.State == EditorExportState.Failed)
            {
                string terminalCleanReason = string.IsNullOrEmpty(reason) ? "cancelled" : reason;
                try
                {
                    DeterministicFrameScheduler.EnsureCleanedUp("cancelled", terminalCleanReason);
                }
                catch (Exception cleanupEx)
                {
                    Log.Exception("EditorExportController.Cancel terminal cleanup 失败", cleanupEx);
                }
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
            DeterministicFrameScheduler.TickResidualOwnership();
            // L3-A 启动阶段残留：非阻塞收敛入口（与 scheduler 的同一个 TryStopAndDrain）。
            TickStartupContextConvergence();
            if (_terminalRearmPending)
            {
                TickTerminalControllerRearm();
                return;
            }

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
                    if (!Mp4TerminalBoundaryPolicy.CanMapToCompleted(_frozenOutputMode))
                    {
                        // L3-A 验证边界：本轮**没有** L3-C Finalizing（无 FinishAsync / 核验 /
                        // 原子发布），因此到达导出终态绝不等于成功导出。fail-closed，并且绝不
                        // 把只存在于 L2 ownership 下的临时产物呈现为最终 MP4 成品。
                        s.State = EditorExportState.Failed;
                        s.StopReason = Mp4TerminalBoundaryPolicy.ValidationBoundaryStopReason;
                        s.StateDetail = Mp4TerminalBoundaryPolicy.ValidationBoundaryDetail;
                        s.TerminationKind = "l3a-validation-boundary";
                        Log.Warn("EditorExportController: L3-A 验证边界到达导出终态（未执行 FFmpeg " +
                                 "Finish / 核验 / 发布）。本 session 记为 Failed，且没有可用的最终 MP4 成品。");
                        break;
                    }
                    s.State = EditorExportState.Completed;
                    s.StopReason = DeterministicFrameScheduler.StopReason ?? "completed";
                    s.StateDetail = "已观察到 canonical completion，且视觉尾帧已排空。";
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
            s.FrameTransactionRequestCount = DeterministicFrameScheduler.FrameTransactionRequestCount;
            s.LogicalFrameCount = DeterministicFrameScheduler.LogicalFrameCount;
            s.WrittenPngFrameCount = DeterministicFrameScheduler.WrittenPngFrameCount;
            s.CaptureSource = DeterministicFrameScheduler.CaptureSource;
            s.CaptureWidth = DeterministicFrameScheduler.CaptureWidth;
            s.CaptureHeight = DeterministicFrameScheduler.CaptureHeight;
            CopyFrozenPolicyFromScheduler(s);
            s.TailFramesCaptured = DeterministicFrameScheduler.TailFramesCaptured;
            s.TailFramesCommitted = DeterministicFrameScheduler.TailFramesCommitted;
            s.CanonicalCompletionCallbackSeen = DeterministicFrameScheduler.CanonicalCompletionCallbackSeen;
            s.CanonicalCompletionStateSeen = DeterministicFrameScheduler.CanonicalCompletionStateSeen;
            s.CompletionFrameIndex = DeterministicFrameScheduler.CanonicalCompletionFrameIndex;
            s.CompletionSignal = DeterministicFrameScheduler.CompletionSignal;
            s.TerminationKind = DeterministicFrameScheduler.TerminationKind;
            TryWriteMetadataBestEffort(s);
            Log.Info(UiText.Format(UiText.LogEditorExportFinishedFormat,
                s.State.ToString(), s.StopReason));
        }

        /// <summary>
        /// 把 scheduler 在本 session 实际冻结的 policy 回填到 session：
        /// 输出模式（PNG / log-only）、safety（policy 标签 / 可选 frame 上限 / 可选逻辑时长，
        /// unbounded 时为 null）与 End Tail。
        /// </summary>
        private static void CopyFrozenPolicyFromScheduler(EditorExportSession session)
        {
            // 以 scheduler 实际冻结的模式为准（它才是本 session 执行时使用的值）。
            session.OutputModeKind = DeterministicFrameScheduler.FrozenOutputMode;
            // 输出几何：以 scheduler 冻结的解析结果为准（legacy 模式的窗口尺寸也在那里冻结）。
            session.OutputGeometryMode = DeterministicFrameScheduler.GeometryModeLabel;
            session.GeometryCustomResolutionEnabled = DeterministicFrameScheduler.GeometryCustomResolutionEnabled;
            session.GeometryConfiguredWidth = DeterministicFrameScheduler.GeometryConfiguredWidth;
            session.GeometryConfiguredHeight = DeterministicFrameScheduler.GeometryConfiguredHeight;
            session.GeometryConfiguredSupersamplingScale =
                DeterministicFrameScheduler.GeometryConfiguredSupersamplingScale;
            session.SupersamplingScale = DeterministicFrameScheduler.SupersamplingScale;
            session.RenderWidth = DeterministicFrameScheduler.RenderWidth;
            session.RenderHeight = DeterministicFrameScheduler.RenderHeight;
            session.DownsampleLevelCount = DeterministicFrameScheduler.DownsampleLevelCount;
            session.DownsampleAlgorithm = OutputGeometryPolicy.DownsampleAlgorithmLabel;
            session.OutputWidth = DeterministicFrameScheduler.OutputWidth;
            session.OutputHeight = DeterministicFrameScheduler.OutputHeight;
            session.OutputAspect = DeterministicFrameScheduler.OutputAspect;
            CopyRenderEnvironmentInventory(session);
            session.SafetyPolicy = DeterministicFrameScheduler.SafetyPolicy;
            session.SafetyFrameLimit = DeterministicFrameScheduler.SafetyFrameLimit;
            session.SafetyDurationSeconds = DeterministicFrameScheduler.SafetyDurationSeconds;
            session.EndTailInputValue = DeterministicFrameScheduler.EndTailInputValue;
            session.EndTailInputUnit = DeterministicFrameScheduler.EndTailInputUnit.ToString();
            session.ResolvedTailFrames = DeterministicFrameScheduler.ResolvedTailFrameCount;
            session.ResolvedTailSeconds = DeterministicFrameScheduler.ResolvedTailSeconds;
            session.ResolvedTailBeats = DeterministicFrameScheduler.ResolvedTailBeats;
            session.CompletionBpm = DeterministicFrameScheduler.CompletionBpm;
            session.Pitch = DeterministicFrameScheduler.Pitch;
        }

        /// <summary>
        /// 把 scheduler 冻结的只读运行时渲染环境 inventory 回填到 session metadata。
        /// inventory 缺失（未启动成功）时字段保持 null，绝不伪造值。
        /// </summary>
        private static void CopyRenderEnvironmentInventory(EditorExportSession session)
        {
            RenderEnvironmentInventory inventory = DeterministicFrameScheduler.EnvironmentInventory;
            if (inventory == null)
                return;

            session.ColorSpace = inventory.ColorSpace;
            session.GraphicsDeviceType = inventory.GraphicsDeviceType;
            session.GraphicsDeviceName = inventory.GraphicsDeviceName;
            session.GraphicsDeviceVersion = inventory.GraphicsDeviceVersion;
            session.GraphicsShaderLevel = inventory.GraphicsShaderLevel;
            session.MaxTextureSize = inventory.MaxTextureSize;
            session.SupportsComputeShaders = inventory.SupportsComputeShaders;
            session.SystemMemorySizeMb = inventory.SystemMemorySizeMb;
            session.RenderTextureFormat = inventory.RenderTextureFormat;
            session.RenderTextureGraphicsFormat = inventory.RenderTextureGraphicsFormat;
            session.RenderTextureAntiAliasing = inventory.RenderTextureAntiAliasing;
            session.RenderTextureUseMipMap = inventory.RenderTextureUseMipMap;
            session.DownsampleRenderTextureFormat = inventory.DownsampleRenderTextureFormat;
            session.DownsampleRenderTextureGraphicsFormat = inventory.DownsampleRenderTextureGraphicsFormat;
        }

        /// <summary>轻量环境校验：Mod 启用、未离开编辑器、定期校验当前会话固定目录。</summary>
        private static bool IsEnvironmentStillValid(EditorExportSession s, out string reason)
        {
            reason = null;

            if (!ModEntry.Enabled)
            {
                reason = "mod-disabled";
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
            s.TerminationKind = "lifecycle-failure";
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
                try { DeterministicFrameScheduler.StopNow("failed", "controller-fail"); } catch { }
            }

            EditorExportSession s = _session;
            if (s == null)
            {
                Log.Exception("EditorExportController: " + context, ex);
                return;
            }

            s.State = EditorExportState.Failed;
            s.EndedAtUtc = DateTime.UtcNow;
            s.StopReason = DeterministicFrameScheduler.Status == DeterministicFrameScheduler.SchedulerStatus.Failed
                ? (DeterministicFrameScheduler.StopReason ?? "controller-fail")
                : "controller-fail";
            s.StateDetail = "会话失败：" + context;
            s.TerminationKind = "lifecycle-failure";
            TryWriteMetadataBestEffort(s);
            Log.Exception("EditorExportController: " + context, ex);
        }
    }
}
