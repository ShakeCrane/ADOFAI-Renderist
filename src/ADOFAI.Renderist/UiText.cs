using System.Globalization;

namespace ADOFAI.Renderist
{
    /// <summary>
    /// 集中存放面向用户的 GUI / 日志中文文案（Phase 3.4.0）。
    ///
    /// 约定：
    ///   * 仅集中面向用户的 GUI 字符串与 <c>Log.Info / Warn / Error / Exception</c> 文案。
    ///   * <c>Log.Debug</c> 保留英文（仅 VerboseLogging=true 时输出，开发者向）。
    ///   * 不翻译机读契约：日志前缀 "[Renderist] "、metadata.json 字段名、技术关键词等保持原样。
    /// </summary>
    internal static class UiText
    {
        // ---------------- GUI: 通用 ----------------

        public const string GuiVerboseLoggingToggle = " 详细日志";
        public const string GuiNonePlaceholder = "（尚无）";
        public const string GuiBtnOpenInExplorer = "在资源管理器中打开";
        public const string GuiSeeLogForDetails = "详情请查看日志。";

        // ---------------- GUI: 输出目录 ----------------

        public const string GuiPreflightOutputDirInputPrefix = "输出目录设置：";
        public const string GuiPreflightOutputDirInputHint = "（留空使用默认目录）";
        public const string GuiPreflightDefaultDirPrefix = "当前默认目录：";
        public const string GuiPreflightPathCheckPrefix = "路径检查结果：";
        public const string GuiPreflightPathCheckAccept = "合法";
        public const string GuiPreflightPathCheckFallBack = "回退默认目录";
        public const string GuiPreflightPathCheckReject = "拒绝";
        public const string GuiPreflightNotChecked = "（尚未检查）";

        // ---------------- GUI: 编辑器导出就绪 ----------------

        public const string GuiPreflightSectionTitle = "编辑器导出";
        public const string GuiEditorExportEnableToggle = " 启用编辑器导出（实验性）";
        public const string GuiPreflightStatusPrefix = "检查状态：";
        public const string GuiReadinessReady = "就绪";
        public const string GuiReadinessDisabled = "未启用";
        public const string GuiReadinessNotInEditor = "非编辑器场景";
        public const string GuiReadinessUnknown = "环境未知";
        public const string GuiReadinessBlocked = "已阻断";

        // ---------------- GUI: 环境诊断 ----------------

        public const string GuiEnvSectionTitle = "环境诊断";
        public const string GuiEnvSceneNamePrefix = "当前场景：";
        public const string GuiEnvCameraCountPrefix = "相机数量：";
        public const string GuiEnvDetectionPrefix = "检测结果：";
        public const string GuiEnvDetectionUnknown = "未知（仅诊断，不阻断）";
        public const string GuiEnvDetectionProbablyEditor = "疑似编辑器（仅诊断，不阻断）";
        public const string GuiEnvSceneEmpty = "（空）";
        public const string GuiEnvNotAvailable = "（不可用）";

        // ---------------- GUI: MasterTimeline Deterministic Gameplay Handoff ----------------

        public const string GuiDeveloperDiagnosticsSectionTitle = "开发者 Diagnostics";
        public const string GuiMasterTimelineHandoffSectionTitle = "MasterTimeline Deterministic Gameplay Handoff";
        public const string GuiMasterTimelineHandoffStatusPrefix = "Handoff 状态：";
        public const string GuiMasterTimelineHandoffFramesPrefix = "已提交帧：";
        public const string GuiMasterTimelineHandoffTailPrefix = "视觉尾帧：";
        public const string GuiMasterTimelineHandoffSafetyPrefix = "安全上限：";
        public const string GuiEndTailLabel = "结束延长：";
        public const string GuiEndTailUnitFrames = "帧";
        public const string GuiEndTailUnitSeconds = "秒";
        public const string GuiEndTailUnitBeats = "拍";
        public const string GuiEndTailUnitMenuSuffix = " ▼";
        public const string GuiEndTailPreviewUnavailable = "无法换算；拍按结束时有效 BPM 解释。";
        public const string GuiEndTailInvalid = "结束延长输入无效。";
        public const string GuiEndTailDependenciesUnavailable = "无法读取结束 BPM 或 pitch，不能安全换算拍。";
        public const string GuiEndTailExceedsSafety = "结束延长超过安全帧上限。";
        public const string GuiMasterTimelineHandoffBtnStart = "启动 MasterTimeline Deterministic Gameplay Handoff";
        public const string GuiMasterTimelineHandoffBtnStop = "停止 MasterTimeline Deterministic Gameplay Handoff";

        // ---------------- Log: ModEntry ----------------

        public const string LogEnabled = "已启用。";
        public const string LogDisabled = "已禁用。已撤销 Harmony 补丁（若有）。";

        // ---------------- Log: Explorer 打开目录 ----------------

        public const string LogOpenExplorerFailedFormat = "打开目录失败：{0}";

        // ---------------- Log: 编辑器导出会话 ----------------

        // {0}=reason
        public const string LogEditorExportStartRejectedFormat = "编辑器导出启动被拒绝：{0}";
        // {0}=dir
        public const string LogEditorExportStartedFormat = "编辑器导出会话已开始 -> {0}";
        // {0}=reason
        public const string LogEditorExportCancelledFormat = "编辑器导出会话已取消：{0}";
        // {0}=reason
        public const string LogEditorExportFailedFormat = "编辑器导出会话失败：{0}";
        // {0}=terminalState, {1}=stopReason
        public const string LogEditorExportFinishedFormat =
            "编辑器导出会话结束：state={0}, stopReason={1}。";

        // ---------------- Log: DeterministicFrameScheduler ----------------

        // {0}=outputFps, {1}=safetyFrameLimit, {2}=outputDirectory
        public const string LogSchedulerStartedFormat =
            "确定性帧调度器已启动：outputFps={0}, safetyFrameLimit={1}, 输出目录={2}";
        // {0}=reason
        public const string LogSchedulerStartRejectedFormat =
            "确定性帧调度器启动被拒绝：{0}";
        public const string LogSchedulerPlaybackRequested =
            "已请求 Renderist-owned editor.Play()，等待本次 lifecycle handoff。";
        // {0}=floor0EntryTime
        public const string LogSchedulerCanonicalAnchorFormat =
            "floor0 chart 基准已读取：{0}（来自 floors[0].entryTime，等待 lifecycle-ready 推导 gameplay anchor）。";
        public const string LogSchedulerForcedClockInstalled =
            "Forced Visual Clock 已安装（lifecycle-ready 前不激活，避免冻结 Countdown_Update）。";
        public const string LogSchedulerInitHoldStarted =
            "Initialization Hold 已开始：不推进输出帧、不捕获。";
        public const string LogSchedulerEditorPlayCalled =
            "已调用 editor.Play()。";
        // {0}=canonicalStartTime, {1}=pitch
        public const string LogSchedulerInitHoldReleasedFormat =
            "回放就绪，Initialization Hold 已释放：CanonicalStartTime={0}, pitch={1}。";
        // {0}=forcedSongPosition
        public const string LogSchedulerFrame0ForcedFormat =
            "frame 0 ForcedSongPosition={0}。";
        // {0}=reason, {1}=terminalStatus
        public const string LogSchedulerStoppedFormat =
            "确定性帧调度器已停止（{0}），终态={1}。";
        // {0}=frameIndex, {1}=error
        public const string LogSchedulerCaptureFailedFormat =
            "确定性帧调度器：输出帧 {0} 捕获失败：{1}";

        // ---------------- Log: OutputPath ----------------

        // {0}=dir
        public const string LogOutDirConfiguredCreateFailedFormat =
            "无法创建配置的输出目录，回退到默认目录：{0}";
        public const string LogOutDirPrepareFailed = "无法准备任何输出目录；编辑器导出已中止。";
        // {0}=尝试次数, {1}=基础名
        public const string LogOutDirUniqueNameExhaustedFormat =
            "会话目录唯一名已耗尽（尝试 {0} 次）：{1}";
        // {0}=configured path, {1}=exception message
        public const string LogOutDirInvalidPathFormat = "OutputDirectory 不是合法路径：{0}（{1}）";
        // {0}=path
        public const string LogOutDirMustBeAbsoluteFormat = "OutputDirectory 必须是绝对路径，实际为：{0}";
        public const string LogOutDirRejectRootFormat = "OutputDirectory 拒绝写入文件系统根目录：{0}";
        public const string LogOutDirRejectInstallFormat = "OutputDirectory 拒绝写入 ADOFAI 安装目录：{0}";
        public const string LogOutDirRejectManagedFormat = "OutputDirectory 拒绝写入 Managed/ 目录：{0}";
        public const string LogOutDirRejectUmmFormat = "OutputDirectory 拒绝写入 UnityModManager/ 目录：{0}";
        public const string LogOutDirRejectRepoFormat = "OutputDirectory 拒绝写入项目仓库目录：{0}";
        // {0}=Application.persistentDataPath 调用上下文
        public const string LogExPersistentDataPathFailed =
            "读取 Application.persistentDataPath 失败";
        // {0}=dir
        public const string LogExCreateDirectoryFailedFormat = "创建目录失败 (Directory.CreateDirectory): {0}";

        // ---------------- helpers ----------------

        /// <summary>
        /// 以不变文化（InvariantCulture）格式化 UiText 模板，保持数字/路径输出稳定。
        /// </summary>
        public static string Format(string template, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
    }
}
