using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
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

        /// <summary>
        /// L3-C Finalizing 槽：scheduler 在 canonical completion + End Tail 之后**一次性交接**
        /// 过来的、已完成全部帧交付的 context。
        ///
        /// 它仍是同一个 L2 pipeline / 同一个 RGB24 buffer 与进程 —— 本槽只转移 ownership，
        /// 不复制 L2 pipeline 状态、不建第二个 FFmpeg pipeline。判决应用之后槽位仍会保留，
        /// 直到 frame Completion 与 pipeline CleanupTask 都收敛（否则 IsBusy 一直为真）。
        /// </summary>
        private static Rgb24DeliveryContext _finalizingContext;

        /// <summary>已受理的 L2 完成通知（FinishAsync 的返回任务）。</summary>
        private static Task<FfmpegVideoOutcome> _finalizingTask;

        /// <summary>true = Finalizing 判决已经应用（终态已定），只剩资源收敛。</summary>
        private static bool _finalizingVerdictApplied;

        /// <summary>本 session 冻结的输出模式（唯一 authority）。</summary>
        public static CaptureOutputMode FrozenOutputMode => _frozenOutputMode;

        /// <summary>当前会话（可能为 null 或处于终止状态）。</summary>
        public static EditorExportSession CurrentSession => _session;

        /// <summary>当前状态。无会话时为 Idle。</summary>
        public static EditorExportState CurrentState =>
            _terminalRearmPending ? EditorExportState.Preparing : _session?.State ?? EditorExportState.Idle;

        /// <summary>是否仍占用：Preparing / Running / Finalizing，或终态 RGB24 资源仍在收敛。</summary>
        public static bool IsBusy =>
            _terminalRearmPending || _startupContext != null || _finalizingContext != null ||
            FrameLocalDeliveryProbe.HasResidualOwnership ||
            DeterministicFrameScheduler.HasPendingRgb24Cleanup ||
            (_session != null &&
             (_session.State == EditorExportState.Preparing ||
              _session.State == EditorExportState.Running ||
              _session.State == EditorExportState.Finalizing));

        /// <summary>
        /// true = 活动会话正处于 MP4 Finalizing（FFmpeg Finish / 核验 / 原子发布在途）。
        ///
        /// 这是**只读投影**，不是第二套状态机：L2 的管线状态仍是唯一 authority，
        /// 这里只回答"session 现在是不是在等完成关口"。
        /// </summary>
        public static bool IsFinalizing => _finalizingContext != null && !_finalizingVerdictApplied;

        private static bool HasTerminalSession =>
            _session != null &&
            (_session.State == EditorExportState.Completed ||
             _session.State == EditorExportState.Cancelled ||
             _session.State == EditorExportState.Failed);

        /// <summary>
        /// true = 活动会话已经结束，仅剩终态资源（frame Completion / pipeline CleanupTask）在收敛。
        ///
        /// 这是**只读投影**，不改变任何安全语义：<see cref="IsBusy"/> 仍为 true，Start 仍被拒绝。
        /// 它只用于让 GUI 不再把"residual 收敛中"错误表达成"仍可 Stop"：此时 Stop 没有实际效果。
        /// terminal controller re-arm 是**进行中**的过渡状态，Stop 在那里仍然有意义，因此不算收敛态；
        /// Finalizing 同样不是收敛态 —— 此时 Stop 仍然有效（请求 L2 取消并放弃发布）。
        /// </summary>
        public static bool IsConvergingTerminalResources =>
            IsBusy &&
            !_terminalRearmPending &&
            !IsFinalizing &&
            (_session == null ||
             (_session.State != EditorExportState.Preparing &&
              _session.State != EditorExportState.Running &&
              _session.State != EditorExportState.Finalizing));

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
                    // L3-C：Finalizing 期间禁止新 session（restart 门禁）。此时 Stop 仍然可用，
                    // 因此绝不把"正在完成封装"表达成"无法操作"。
                    LastStartRejectReason = IsFinalizing
                        ? "MP4 正在完成封装与核验，禁止启动新会话"
                        : "已有会话进行中";
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
        /// <summary>
        /// F1-A 诊断配置：只在外部 descriptor 存在时启用，且只配置**现有** Options 缝
        /// （StreamWrapper / ProcessStart）。不新建 pipeline、不改编码参数、不改 Finalizing 协议。
        ///
        /// 返回 false 表示本次**诊断**必须 fail-closed：descriptor 存在但无效/过期/握手失败，
        /// 或 probe 已激活却又要开第二个 session。绝不在此回退成普通导出。
        /// </summary>
        private static bool TryConfigureFrameLocalDeliveryProbe(FfmpegVideoPipelineOptions options, out string error)
        {
            error = null;
            if (options == null) { error = "f1-options-missing"; return false; }

            string descriptorPath = FrameLocalDeliveryProbe.DescriptorPath;
            if (string.IsNullOrEmpty(descriptorPath))
            {
                // 默认关闭：不安装任何缝，产品行为与既有完全一致。
                return true;
            }

            if (FrameLocalDeliveryProbe.IsActive)
            {
                error = "f1-probe-already-active:one-run-per-process";
                return false;
            }

            if (!FrameLocalDeliveryProbe.TryActivate(out error)) return false;
            FrameLocalDeliveryProbe.Evidence?.Record("ffmpeg-approved", options.Identity.ExecutablePath +
                ",sha256=" + options.Identity.ExecutableSha256 + ",version=" + options.Identity.VersionLine);

            Func<Stream, string, Stream> existing = options.StreamWrapper;
            options.StreamWrapper = (inner, role) =>
            {
                Stream wrapped = existing != null ? existing(inner, role) : inner;
                if (!string.Equals(role, "stdin", StringComparison.Ordinal)) return wrapped;
                return FrameLocalDeliveryProbe.WrapStandardInput(wrapped);
            };

            FfmpegVideoProcessStart start = options.ProcessStart ??
                                            FrameLocalDeliveryProbe.StartObservedDefaultProcess;
            options.ProcessStart = FrameLocalDeliveryProbe.WrapProcessStart(start);

            Log.Warn("F1 DIAGNOSTIC PROBE ACTIVE: runNonce=" +
                     (FrameLocalDeliveryProbe.Slot == null ? "?" : FrameLocalDeliveryProbe.Slot.RunNonce) +
                     " descriptor=" + descriptorPath +
                     " (single run, absolute frame 0 only, no Finalizing).");
            return true;
        }

        private static void RetireStartupContext(Rgb24DeliveryContext context)
        {
            if (context == null) return;
            string error;
            if (context.TryStopAndDrain("mp4-startup-failed", out error))
                return;
            _startupContext = context;
        }

        /// <summary>
        /// L3-C：判决已经应用之后，把仍被 controller 持有的 finalizing context 收敛掉
        /// （幂等、非阻塞）。收敛完成前 <see cref="IsBusy"/> 保持为真，因此仍然禁止新 session。
        /// </summary>
        private static void TickFinalizingConvergence()
        {
            Rgb24DeliveryContext context = _finalizingContext;
            if (context == null || !_finalizingVerdictApplied) return;

            string error;
            if (context.TryStopAndDrain("mp4-finalizing-complete", out error))
            {
                _finalizingContext = null;
                _finalizingTask = null;
                _finalizingVerdictApplied = false;
                Log.Info("EditorExportController: L3-C finalizing context 已收敛。");

                // 判决时刻记录的 residual 只是当时的快照：资源此后真正收敛了，
                // 就必须把它改正为 false（否则 metadata 会永久谎报仍有 residual）。
                // 反向不成立：仍未收敛时绝不改写。
                EditorExportSession session = _session;
                if (session != null && session.Mp4FinalizingResidual == true)
                {
                    session.Mp4FinalizingResidual = false;
                    TryWriteMetadataBestEffort(session);
                    Log.Info("EditorExportController: L3-C finalizing residual 已清除（metadata 已更新）。");
                }
                return;
            }

            Log.Debug("EditorExportController: L3-C finalizing context 仍在收敛： " + (error ?? "unknown"));
        }

        /// <summary>
        /// Finalizing 期间的取消门禁：只**请求**停止（L2 保证取消后绝不发布），
        /// 不同步等待、也不假装资源已回收。session 仍是 Finalizing，直到完成通知被观察到。
        /// </summary>
        private static void CancelFinalizing(string reason)
        {
            Rgb24DeliveryContext context = _finalizingContext;
            if (context == null || _finalizingVerdictApplied) return;

            string error;
            if (!context.TryRequestStop(string.IsNullOrEmpty(reason) ? "cancelled" : reason, out error))
            {
                Log.Warn("EditorExportController: L3-C finalizing 取消请求失败： " + (error ?? "unknown"));
                return;
            }

            Log.Info("EditorExportController: L3-C finalizing 已请求取消（" +
                     (string.IsNullOrEmpty(reason) ? "cancelled" : reason) + "），不会发布任何成品。");
        }

        /// <summary>
        /// 终止 / 重开路径上的 finalizing 槽统一收敛入口（幂等、非阻塞）：
        /// 仍有在途完成关口时只能请求取消；已有判决时走既有 frame + CleanupTask 收敛。
        /// </summary>
        private static void ConvergeFinalizingSlot(string reason)
        {
            if (_finalizingContext == null) return;
            if (!_finalizingVerdictApplied)
            {
                CancelFinalizing(reason);
                return;
            }

            TickFinalizingConvergence();
        }

        /// <summary>
        /// L3-C 自愈：交接 context 只可能被**同一次调用内**的 <see cref="FinalizeFromScheduler"/> 取走
        /// （scheduler 在 <c>Tick</c> / <c>StopNow</c> 内交接，紧接着就是终态映射）。
        /// 因此如果一次 Tick 开始时它仍然存在，就说明上一轮没有任何消费方 ——
        /// 此时绝不能让它变成永久占用：立刻回原取消路径（fail-closed，绝不发布）。
        /// </summary>
        private static void RevokeAbandonedFinalizingHandoff()
        {
            if (_finalizingContext != null) return;
            if (!DeterministicFrameScheduler.HasCompletedRgb24Handoff) return;

            DeterministicFrameScheduler.RevokeCompletedRgb24Delivery("controller-handoff-abandoned");
            Log.Warn("EditorExportController: L3-C 交接 context 未被接管，已回原取消路径" +
                     "（本 session 不会产出成品）。");
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

            // F1-A：唯一一次 descriptor/preflight。没有 descriptor 时完全走原产品路径；
            // descriptor 存在但失效/过期/握手失败时**拒绝本次诊断**（绝不悄悄回退成普通导出）。
            if (!TryConfigureFrameLocalDeliveryProbe(frozen.Options, out string probeError))
            {
                error = "f1-probe-preflight-failed:" + probeError;
                return false;
            }

            Mp4SessionBinding binding = Mp4SessionStartup.Bind(
                frozen, geometry.Width, geometry.Height,
                FfmpegPipelineFactory.Instance, DefaultMp4DeliveryFactory.Instance);
            if (!binding.Succeeded)
            {
                _startupContext = binding.PendingConvergence;
                error = binding.ErrorCode +
                        (string.IsNullOrEmpty(binding.ErrorDetail) ? string.Empty : (":" + binding.ErrorDetail));
                FrameLocalDeliveryProbe.Shutdown();
                return false;
            }

            Log.Info("EditorExportController: MP4 FFmpeg executable=" + frozen.Identity.ExecutablePath +
                     " arguments=" + binding.Pipeline.EncodeArguments);

            if (!FrameLocalDeliveryProbe.PrePlayHealthy(out probeError))
            {
                error = probeError;
                _startupContext = Mp4SessionStartup.RetireOrKeep(binding.Context);
                FrameLocalDeliveryProbe.Shutdown();
                return false;
            }

            string armError;
            if (!DeterministicFrameScheduler.ArmRgb24Delivery(binding.Context, out armError))
            {
                _startupContext = Mp4SessionStartup.RetireOrKeep(binding.Context);
                error = "mp4-arm-failed:" + (armError ?? "unknown");
                FrameLocalDeliveryProbe.Shutdown();
                return false;
            }

            delivery = binding.Context;
            return true;
        }

        /// <summary>
        /// 只有完成 terminal controller re-arm 且重新通过普通 gate 后，才创建
        /// session 目录并进入唯一一次 editor.Play()。
        ///
        /// 0.3.10.2：输出模式先解析，随后在任何 session 副作用之前执行 MP4 readiness
        /// 门禁。terminal re-arm 之后再次进入本方法时会**重新**读取 readiness，
        /// 绝不沿用第一次 Start 时的结论。
        /// </summary>
        private static bool StartSession(Settings settings, EditorExportReadinessReport report)
        {
            EditorExportSession session = null;
            try
            {
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

                // ---- MP4 readiness 最终门禁（唯一判定：Mp4SessionStartup.TryCheckReadiness）----
                // 它严格发生在以下三者**之前**：创建 session 目录、写初始 metadata、
                // 初始化 MP4 session 资源（pipeline / delivery context）。
                // 非 MP4 模式（PNG / Log-only）恒为通过，因此不影响既有导出路径。
                string readinessError;
                string readinessDetail;
                if (!Mp4SessionStartup.TryCheckReadiness(
                        outputMode, ModEntry.GetFfmpegReadiness(), out readinessError, out readinessDetail))
                {
                    LastStartRejectReason = "FFmpeg 未就绪，MP4 启动被拒绝：" + readinessError +
                                            (string.IsNullOrEmpty(readinessDetail)
                                                ? string.Empty
                                                : ("（" + readinessDetail + "）"));
                    Log.Warn(UiText.Format(UiText.LogEditorExportStartRejectedFormat, LastStartRejectReason));
                    return false;
                }

                _frozenOutputMode = outputMode;

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
                    // L3-C：本构建已经实现 Finalizing，因此该字段恒为 false。
                    // 它保留下来只为让既有 metadata 读者区分"到达终态时完全没有执行 FFmpeg
                    // Finish 的旧构建"（true）与之后的构建；它不是本次 Finalizing 的结果，
                    // 那由 mp4FinalizingState 记录。
                    FinalizingNotImplemented = false,
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
                    FrameLocalDeliveryProbe.Shutdown();
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
                FrameLocalDeliveryProbe.Shutdown();
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
        /// 用户主动停止。仅 Running 可停止；Finalizing 期间请求取消（绝不发布）；
        /// terminal session 不改写记录，但 scheduler / controller 可能仍持有 residual
        /// ownership，需经统一入口收敛。
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

            if (s.State == EditorExportState.Finalizing)
            {
                CancelFinalizing("user-stop");
                return;
            }

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
                ConvergeFinalizingSlot("user-stop");
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
            // F1-A：诊断 session 的收尾（停 monitor、放行 stdin 门、关命名事件）与产品取消
            // 解耦；只在 probe 激活时生效，普通导出完全不受影响。
            if (FrameLocalDeliveryProbe.IsActive)
            {
                try { FrameLocalDeliveryProbe.Shutdown(); }
                catch (Exception ex) { Log.Exception("EditorExportController: F1 probe shutdown 异常", ex); }
            }

            if (_terminalRearmPending)
            {
                CancelTerminalControllerRearm(reason);
                return;
            }

            EditorExportSession s = _session;
            if (s == null) return;

            if (s.State == EditorExportState.Finalizing)
            {
                // disable / unload / 离开编辑器期间的取消同样必须 fail-closed：请求取消，
                // 绝不发布 MP4。
                CancelFinalizing(string.IsNullOrEmpty(reason) ? "cancelled" : reason);
                return;
            }

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
                ConvergeFinalizingSlot(terminalCleanReason);
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
            FrameLocalDeliveryProbe.TickSettlement();
            if (FrameLocalDeliveryProbe.ControlStopRequested && DeterministicFrameScheduler.IsRunning)
                DeterministicFrameScheduler.RequestStop("cancelled", "f1-control-stop");
            DeterministicFrameScheduler.TickResidualOwnership();
            // L3-A 启动阶段残留 + L3-C finalizing 槽：非阻塞收敛入口（与 scheduler 的同一个 TryStopAndDrain）。
            TickStartupContextConvergence();
            TickFinalizingConvergence();
            RevokeAbandonedFinalizingHandoff();
            if (_terminalRearmPending)
            {
                TickTerminalControllerRearm();
                return;
            }

            EditorExportSession s = _session;
            if (s == null) return;

            // L3-C：Finalizing 只等 FFmpeg Finish / 核验 / 发布回到主线程。
            // 刻意不做环境校验、不推进 scheduler、不设 wall-clock deadline：
            // 合法的封装与核验可以持续任意长时间。
            if (s.State == EditorExportState.Finalizing)
            {
                TickFinalizing();
                return;
            }

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
            if (FrameLocalDeliveryProbe.IsActive &&
                (status == DeterministicFrameScheduler.SchedulerStatus.Completed ||
                 status == DeterministicFrameScheduler.SchedulerStatus.Cancelled ||
                 status == DeterministicFrameScheduler.SchedulerStatus.Failed))
            {
                FrameLocalDeliveryProbe.Shutdown();
                if (status == DeterministicFrameScheduler.SchedulerStatus.Completed)
                {
                    Fail(new InvalidOperationException("f1-forbids-finalizing"), "F1 diagnostic completed unexpectedly");
                    return;
                }
            }

            // ---- L3-C：MP4 的正常完成路径必须先过 Finalizing 关口 ----
            // scheduler 只在 canonical completion + End Tail 全部提交、且 Unity cleanup 全部
            // 成功之后才把 context 一次性交接出来；这里把它接过来并开始既有 L2 FinishAsync
            // （关闭 stdin → 等待退出与双流 EOF → 独立核验 → 同卷原子发布）。
            // MP4 session **绝不**在此时直接记为 Completed。
            if (status == DeterministicFrameScheduler.SchedulerStatus.Completed &&
                !Mp4TerminalBoundaryPolicy.CanMapToCompleted(_frozenOutputMode))
            {
                CopySchedulerResultIntoSession(s);
                BeginFinalizing(s);
                return;
            }

            switch (status)
            {
                case DeterministicFrameScheduler.SchedulerStatus.Completed:
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
            CopySchedulerResultIntoSession(s);
            TryWriteMetadataBestEffort(s);
            Log.Info(UiText.Format(UiText.LogEditorExportFinishedFormat,
                s.State.ToString(), s.StopReason));
        }

        /// <summary>
        /// 把 scheduler 在本 session 实际冻结并产出的结果投影进 session
        /// （计数 / 几何 / 环境 inventory / completion 证据 / 终止分类）。
        /// 本方法只做投影，不改写任何 authority。
        /// </summary>
        private static void CopySchedulerResultIntoSession(EditorExportSession s)
        {
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
        }

        /// <summary>
        /// L3-C：从 scheduler 取走一次性交接的 context，并开始 FFmpeg 完成关口。
        ///
        /// 取不到 context 或 L2 拒绝受理时**绝不**把 session 记为成功：直接 fail-closed，
        /// 且仍保留已取到的 context 交给既有收敛路径回收（不丢 ownership）。
        /// </summary>
        private static void BeginFinalizing(EditorExportSession s)
        {
            Rgb24DeliveryContext context;
            string takeError;
            if (!DeterministicFrameScheduler.TryTakeCompletedRgb24Delivery(out context, out takeError))
            {
                MarkFinalizingUnavailable(s, "mp4-finalize-handoff-missing:" + (takeError ?? "unknown"));
                return;
            }

            // 计数一致性关口（在 L2 FinishAsync **之前**）：session 的逻辑提交帧数由 scheduler
            // 冻结并投影，L2 的 delivered 计数由管线自己维护 —— 两者必须精确相等，否则
            // fail-closed。这里只做交叉核对，不新增第二套计数 authority。
            Rgb24FinalizeStart start = context.TryBeginFinalizing(s.LogicalFrameCount);
            _finalizingContext = context;
            if (!start.Started)
            {
                MarkFinalizingUnavailable(s, "mp4-finalize-start-failed:" + (start.ErrorCode ?? "unknown") +
                    (string.IsNullOrEmpty(start.ErrorDetail) ? string.Empty : (":" + start.ErrorDetail)));
                return;
            }

            _finalizingTask = start.Completion;
            _finalizingVerdictApplied = false;

            s.State = EditorExportState.Finalizing;
            s.StopReason = null;
            s.StateDetail = "FFmpeg 正在完成封装、独立核验与原子发布。";
            s.FinalizingNotImplemented = false;
            TryWriteMetadataBestEffort(s);

            Log.Info("EditorExportController: L3-C finalizing started (Finish → verification → atomic publish); " +
                     "deliveredFrames=" + s.LogicalFrameCount.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 完成关口无法开始：session fail-closed。context（若已取得）保留在 finalizing 槽，
        /// 由既有 frame + CleanupTask 收敛路径回收并继续阻止重开。
        ///
        /// `mp4FinalizingResidual` 必须反映**当下真实**的收敛状态：只要 context 仍在 controller
        /// 手里且 frame Completion / pipeline CleanupTask 尚未确认收敛，就绝不能记成 false。
        /// </summary>
        private static void MarkFinalizingUnavailable(EditorExportSession s, string errorCode)
        {
            _finalizingTask = null;
            _finalizingVerdictApplied = true;

            s.Mp4FinalizingState = Mp4FinalizationPolicy.FailedLabel;
            s.Mp4FinalizingErrorCode = errorCode;
            s.Mp4FinalizingResidual = CurrentFinalizingResidual(null);
            s.State = EditorExportState.Failed;
            s.StopReason = "mp4-finalize-failed:" + errorCode;
            s.StateDetail = "MP4 完成关口无法开始：本 session 没有可用成品。";
            s.TerminationKind = "finalize-failure";
            s.EndedAtUtc = DateTime.UtcNow;
            TryWriteMetadataBestEffort(s);

            Log.Warn("EditorExportController: L3-C finalizing unavailable: " + errorCode +
                     "；本 session 记为 Failed，且没有可用的最终 MP4 成品。" +
                     " residual=" + (s.Mp4FinalizingResidual ?? false));
        }

        /// <summary>
        /// 判决时刻的 residual 投影：**同时**考虑 L2 outcome 与 controller 仍持有的 context。
        ///
        /// 只报 L2 outcome 是不够的：判决应用时 context 可能仍持有 frame ownership
        /// （例如完成关口被拒绝、或判决与资源收敛并不同时结束）。绝不在这种情况下记成 clean。
        /// </summary>
        private static bool CurrentFinalizingResidual(Mp4FinalizationOutcome outcome)
        {
            if (outcome != null && outcome.ResidualOwnership)
                return true;

            Rgb24DeliveryContext context = _finalizingContext;
            return context != null && context.HasResidualOwnership;
        }

        /// <summary>
        /// L3-C：在主线程观察完成关口（非阻塞、无 wall-clock deadline）。
        /// 完成通知一旦结束即按 <see cref="Mp4FinalizationPolicy"/> 应用终态判决，绝不推断。
        /// </summary>
        private static void TickFinalizing()
        {
            EditorExportSession s = _session;
            if (s == null) return;

            Task<FfmpegVideoOutcome> task = _finalizingTask;
            if (_finalizingContext == null || task == null)
            {
                MarkFinalizingUnavailable(s, "mp4-finalize-slot-inconsistent");
                return;
            }

            // 合法：FFmpeg 封装与独立核验可以持续任意长时间（与 L3-B 的 IO 背压同一原则）。
            if (!task.IsCompleted) return;

            Mp4FinalizationOutcome outcome;
            if (task.IsFaulted)
            {
                Exception inner = task.Exception == null ? null : task.Exception.GetBaseException();
                outcome = Mp4FinalizationPolicy.Faulted(inner == null ? "unknown" : inner.Message);
            }
            else if (task.IsCanceled)
            {
                outcome = Mp4FinalizationPolicy.CancelledTask("finalize-task-canceled");
            }
            else
            {
                outcome = Mp4FinalizationPolicy.Map(task.Result, s.FinalVideoPath);
            }

            ApplyFinalizingVerdict(s, outcome);
        }

        /// <summary>
        /// 应用 Finalizing 判决：只有 Published（= L2 Completed + 核验通过 + 最终路径一致）
        /// 才允许把 session 记为 Completed。其余一律 Failed / Cancelled，且都明确声明没有成品。
        /// </summary>
        private static void ApplyFinalizingVerdict(EditorExportSession s, Mp4FinalizationOutcome outcome)
        {
            _finalizingVerdictApplied = true;
            _finalizingTask = null;

            s.Mp4FinalizingState = Mp4FinalizationPolicy.Label(outcome.Verdict);
            s.Mp4FinalizingErrorCode = outcome.ErrorCode;
            s.Mp4FinalizingErrorDetail = outcome.ErrorDetail;
            s.Mp4EncoderExitCode = outcome.EncoderExitCode;
            s.Mp4DeliveredFrameCount = outcome.DeliveredFrameCount;
            s.Mp4VerificationStatus = outcome.VerificationStatus;
            s.Mp4VerifiedFrameCount = outcome.DecodedFrameCount;
            s.Mp4VerifiedPacketCount = outcome.PacketCount;
            s.Mp4VerifiedCodec = outcome.CodecName;
            s.Mp4VerifiedPixelFormat = outcome.PixelFormat;
            s.Mp4FinalizingResidual = CurrentFinalizingResidual(outcome);
            s.Mp4TempFileRemoved = outcome.TempFileRemoved;

            switch (outcome.Verdict)
            {
                case Mp4FinalizationVerdict.Published:
                    s.State = EditorExportState.Completed;
                    s.StopReason = Mp4FinalizationPolicy.PublishedStopReason;
                    s.StateDetail = "已通过 FFmpeg Finish、独立核验与原子发布，最终 MP4 可用。";
                    s.TerminationKind = "canonical-completion";
                    if (!string.IsNullOrEmpty(outcome.FinalPath))
                        s.FinalVideoPath = outcome.FinalPath;
                    Log.Info("EditorExportController: L3-C MP4 published: " +
                             Mp4FinalizationPolicy.Describe(outcome));
                    break;

                case Mp4FinalizationVerdict.Cancelled:
                    s.State = EditorExportState.Cancelled;
                    s.StopReason = outcome.ErrorCode ?? "cancelled";
                    s.StateDetail = "MP4 完成关口已取消：没有发布任何成品。";
                    s.TerminationKind = ClassifyFinalizeCancellation(outcome.ErrorCode);
                    Log.Warn("EditorExportController: L3-C MP4 finalizing cancelled: " +
                             Mp4FinalizationPolicy.Describe(outcome));
                    break;

                default:
                    s.State = EditorExportState.Failed;
                    s.StopReason = "mp4-finalize-failed:" + (outcome.ErrorCode ?? "unknown");
                    s.StateDetail = "MP4 完成关口失败：没有可用成品。" +
                                    (string.IsNullOrEmpty(outcome.ErrorDetail)
                                        ? string.Empty
                                        : ("(" + outcome.ErrorDetail + ")"));
                    s.TerminationKind = "finalize-failure";
                    Log.Warn("EditorExportController: L3-C MP4 finalizing failed: " +
                             Mp4FinalizationPolicy.Describe(outcome));
                    break;
            }

            s.EndedAtUtc = DateTime.UtcNow;
            TryWriteMetadataBestEffort(s);
            Log.Info(UiText.Format(UiText.LogEditorExportFinishedFormat, s.State.ToString(), s.StopReason));
        }

        private static string ClassifyFinalizeCancellation(string reason)
        {
            if (reason == "user-stop" || reason == "cancelled" ||
                reason == "mod-disabled" || reason == "mod-unload" || reason == "left-editor")
                return "user-cancel";
            return "lifecycle-failure";
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
            if (FrameLocalDeliveryProbe.IsActive) FrameLocalDeliveryProbe.Shutdown();
            // 任何 controller 层未处理异常都必须保证 scheduler 回到恢复态。
            if (DeterministicFrameScheduler.IsRunning)
            {
                try { DeterministicFrameScheduler.StopNow("failed", "controller-fail"); } catch { }
            }

            // 即使 scheduler 已经终态，也可能留下 L3-C 交接 context（例如终态映射在取走之前抛异常）：
            // 统一收敛入口会把它放回取消路径，不会留下无人管理的编码进程或永久占用。
            try { DeterministicFrameScheduler.EnsureCleanedUp("failed", "controller-fail"); }
            catch (Exception cleanupEx)
            {
                Log.Exception("EditorExportController: controller-fail scheduler 收敛异常", cleanupEx);
            }

            // L3-C：controller 层异常时 finalizing 槽同样不得悬空 —— 仍有在途完成关口时
            // 只能请求取消（绝不发布），已有判决时走既有收敛路径。
            try { ConvergeFinalizingSlot("controller-fail"); }
            catch (Exception convergeEx)
            {
                Log.Exception("EditorExportController: finalizing 槽收敛异常", convergeEx);
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
