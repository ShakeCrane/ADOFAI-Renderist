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
        // {0}=safetyFrameLimit, {1}=safetyDurationSeconds
        public const string GuiMasterTimelineHandoffSafetyFormat = "安全上限：{0} 帧 / {1} 秒";
        public const string GuiMasterTimelineHandoffSafetyUnbounded = "安全上限：未配置（无总帧数 / 总时长上限）";
        public const string GuiEndTailLabel = "结束延长：";
        public const string GuiOutputFpsLabel = "输出帧率：";
        public const string GuiOutputFpsEffectivePrefix = "实际：";
        public const string GuiOutputFpsInvalid = "输出帧率输入无效：必须是正整数；设置未被修改。";
        public const string GuiEndTailUnitFrames = "帧";
        public const string GuiEndTailUnitSeconds = "秒";
        public const string GuiEndTailUnitBeats = "拍";
        public const string GuiEndTailUnitMenuSuffix = " ▼";
        public const string GuiEndTailPreviewUnavailable = "无法换算；拍按结束时有效 BPM 解释。";
        public const string GuiEndTailInvalid = "结束延长输入无效。";
        public const string GuiEndTailDependenciesUnavailable = "无法读取结束 BPM 或 pitch，不能安全换算拍。";
        public const string GuiEndTailExceedsSafety = "结束延长超过安全帧上限。";
        public const string GuiImageOutputToggle = " 输出 PNG 图像";
        public const string GuiImageOutputDisabledHint =
            "已关闭图像输出（log-only）：帧事务与时间推进照常执行，但不写 PNG 文件，仅写 metadata.json。";
        // ---- Phase 3.7.0: 自定义输出分辨率 ----
        public const string GuiCustomResolutionToggle = " 自定义输出分辨率";
        public const string GuiCustomResolutionWidthLabel = "输出宽度：";
        public const string GuiCustomResolutionHeightLabel = "输出高度：";
        public const string GuiCustomResolutionEffectivePrefix = "实际输出：";
        public const string GuiCustomResolutionInvalid =
            "自定义分辨率输入无效：必须是正整数，且不得超过硬件上限；设置未被修改，导出保持阻断。";
        public const string GuiCustomResolutionDisabledHint =
            "已关闭自定义分辨率：沿用当前游戏窗口渲染分辨率（与既有行为一致）。";
        public const string GuiCustomResolutionHint =
            "导出期间三台谱面 Camera 统一使用该宽高比；异常时以 fail-closed 拒绝启动。";
        // ---- Phase 3.7.0 第二闭环: 超采样 ----
        public const string GuiSupersamplingScaleLabel = "超采样倍率：";
        public const string GuiSupersamplingEffectivePrefix = "实际渲染：";
        public const string GuiSupersamplingInvalid =
            "超采样倍率输入无效：必须是大于等于 1 的正整数，且渲染尺寸不得超过硬件上限；设置未被修改，导出保持阻断。";
        public const string GuiSupersamplingHint =
            "渲染尺寸 = 输出尺寸 × 倍率，再经多级 bilinear 降采样。倍率 1 为关闭。";
        public const string GuiSupersamplingDisabledHint =
            "倍率 1：不启用超采样（与既有行为一致）。";
        public const string GuiReadinessInvalidOutputGeometry = "输出分辨率非法";
        public const string GuiEnvChartCameraAspectPrefix = "谱面相机 aspect：";
        public const string GuiMasterTimelineHandoffBtnStart = "启动 MasterTimeline Deterministic Gameplay Handoff";
        public const string GuiMasterTimelineHandoffBtnStop = "停止 MasterTimeline Deterministic Gameplay Handoff";

        // ---- Phase 3.8.0: FFmpeg 组件管理 ----

        public const string GuiFfmpegSectionTitle = "FFmpeg 组件";
        public const string GuiFfmpegStatusPrefix = "组件状态：";
        public const string GuiFfmpegStateInstallRootUnavailable = "托管目录不可用";
        public const string GuiFfmpegStateNotFound = "未发现 FFmpeg";
        public const string GuiFfmpegStateDiscovered = "已发现（未做能力检查）";
        public const string GuiFfmpegStateReady = "就绪";
        public const string GuiFfmpegStateUnsupported = "已发现但能力不足";
        public const string GuiFfmpegStateInvalid = "显式路径无效";
        public const string GuiFfmpegNotBlockingHint =
            "FFmpeg 仅用于未来的视频输出；组件缺失或能力不足不影响 PNG / Log-only 导出。";
        public const string GuiFfmpegSourcePrefix = "发现来源：";
        public const string GuiFfmpegSourceExplicit = "用户指定路径";
        public const string GuiFfmpegSourceManaged = "Renderist 托管安装";
        public const string GuiFfmpegSourcePath = "系统 PATH";
        public const string GuiFfmpegSourceNone = "（无）";
        public const string GuiFfmpegExecutablePrefix = "可执行文件：";
        public const string GuiFfmpegIdentityPrefix = "二进制身份（SHA-256 前 16 位）：";
        public const string GuiFfmpegInstallRootPrefix = "托管目录：";
        public const string GuiFfmpegManagedInstallsPrefix = "托管安装：";
        public const string GuiFfmpegManagedNone = "（无）";
        public const string GuiFfmpegCapabilityPrefix = "能力检查：";
        public const string GuiFfmpegCapabilityNotProbed = "（未检查）";
        public const string GuiFfmpegCapabilityOk = "含 libx264 / mp4 / rawvideo";
        public const string GuiFfmpegCapabilityMissingPrefix = "缺少 ";
        public const string GuiFfmpegCapabilityFailedPrefix = "检查失败：";
        public const string GuiFfmpegAssetPrefix = "可安装资产：";
        public const string GuiFfmpegAssetSourcePrefix = "来源：";
        public const string GuiFfmpegLicensePrefix = "许可证：";
        public const string GuiFfmpegExplicitPathLabel = "指定 FFmpeg 路径：";
        public const string GuiFfmpegExplicitPathHint = "（留空 = 依次查找托管安装、系统 PATH）";
        public const string GuiFfmpegArchivePathLabel = "本地安装包 ZIP：";
        public const string GuiFfmpegArchivePathHint = "（选择已下载好的固定版本 ZIP；不会自动下载）";
        public const string GuiFfmpegButtonRefresh = "刷新组件状态";
        public const string GuiFfmpegButtonInstall = "从本地 ZIP 安装";
        public const string GuiFfmpegButtonCancelInstall = "取消安装";
        public const string GuiFfmpegBusyInspecting = "正在检查组件…";
        public const string GuiFfmpegBusyInstalling = "正在安装…";
        public const string GuiFfmpegInstallResultPrefix = "上次安装：";
        public const string GuiFfmpegInstallOutcomeInstalled = "已安装";
        public const string GuiFfmpegInstallOutcomeAlready = "已存在，未覆盖";
        public const string GuiFfmpegInstallOutcomeCancelled = "已取消（已清理暂存）";
        public const string GuiFfmpegInstallOutcomeFailed = "失败";
        public const string GuiFfmpegUnavailable = "（不可用）";

        // ---- Phase 3.8.0: FFmpeg 下载（UnityWebRequest + DownloadHandlerFile）----

        // 每个阶段都必须可区分，不能被混成一个"进行中"。
        public const string GuiFfmpegDownloadStatePrefix = "下载状态：";
        public const string GuiFfmpegDownloadStateIdle = "未开始";
        public const string GuiFfmpegDownloadStateDownloading = "下载中";
        public const string GuiFfmpegDownloadStateVerifying = "校验中（长度与 SHA-256）";
        public const string GuiFfmpegDownloadStateInstalling = "安装中（解压 / 能力探测 / 发布）";
        public const string GuiFfmpegDownloadStateSucceeded = "成功（组件可用；不代表 MP4 导出已可用）";
        public const string GuiFfmpegDownloadStateCancelled = "已取消";
        public const string GuiFfmpegDownloadStateFailed = "失败";
        public const string GuiFfmpegDownloadProgressPrefix = "已接收：";
        public const string GuiFfmpegButtonDownloadAndInstall = "下载并安装固定版本";
        public const string GuiFfmpegButtonCancelDownload = "取消下载";
        public const string GuiFfmpegDownloadBusyHint =
            "下载由 Unity 主线程发起；校验与安装在其后的后台任务中完成。";
        public const string GuiFfmpegDownloadNoTimeoutHint =
            "不设置请求超时：慢速网络属于真实条件，卡住时请使用取消。";
        public const string GuiFfmpegDownloadErrorPrefix = "失败原因：";
        public const string GuiFfmpegSourceCodePrefix = "对应源码：";
        public const string GuiFfmpegDownloadErrorBusy = "已有下载或安装在进行中。";
        public const string GuiFfmpegDownloadErrorRequestFailed = "网络请求失败（可能是 TLS、证书或断线）。";
        public const string GuiFfmpegDownloadErrorHttp = "服务器返回了非 200 响应。";
        public const string GuiFfmpegDownloadErrorInsecure = "下载被重定向到非 HTTPS 地址，已拒绝。";
        public const string GuiFfmpegDownloadErrorSize = "下载长度与固定清单不一致，已拒绝。";
        public const string GuiFfmpegDownloadErrorHash = "下载内容 SHA-256 与固定清单不一致，已拒绝。";
        public const string GuiFfmpegDownloadErrorCapability = "该二进制缺少必需能力，未发布安装。";

        // ---------------- Log: FFmpeg 下载 ----------------

        // {0}=generation, {1}=url
        public const string LogFfmpegDownloadStartedFormat = "开始下载 FFmpeg 组件（generation {0}）：{1}";
        // {0}=generation, {1}=errorCode, {2}=detail
        public const string LogFfmpegDownloadFailedFormat = "FFmpeg 下载失败（generation {0}）：{1} {2}";
        public const string LogFfmpegDownloadCancelled = "FFmpeg 下载已取消，临时文件已清理。";
        // {0}=generation
        public const string LogFfmpegDownloadStaleIgnoredFormat =
            "FFmpeg 下载：忽略迟到的完成通知（generation {0}）。";
        // {0}=directory
        public const string LogFfmpegDownloadSucceededFormat = "FFmpeg 组件安装成功：{0}";
        // {0}=count
        public const string LogFfmpegOrphanDownloadsRemovedFormat = "已清理 {0} 个遗留的 FFmpeg 下载临时文件。";

        // ---------------- Log: FFmpeg 组件 ----------------

        // {0}=assetId, {1}=errorCode, {2}=detail
        public const string LogFfmpegInstallFailedFormat = "FFmpeg 安装失败（{0}）：{1} {2}";
        public const string LogFfmpegInstallCancelled = "FFmpeg 安装已取消，暂存目录已清理。";
        // {0}=directory
        public const string LogFfmpegInstallSucceededFormat = "FFmpeg 安装完成：{0}";
        // {0}=directory
        public const string LogFfmpegInstallAlreadyPresentFormat = "FFmpeg 已存在，未覆盖：{0}";
        // {0}=error
        public const string LogFfmpegInspectionFailedFormat = "FFmpeg 组件状态检查失败：{0}";

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

        // {0}=outputFps, {1}=safetyPolicy, {2}=safetyFrameLimit|unbounded, {3}=outputDirectory
        public const string LogSchedulerStartedFormat =
            "确定性帧调度器已启动：outputFps={0}, safetyPolicy={1}, safetyFrameLimit={2}, 输出目录={3}";
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
        // {0}=reason, {1}=controllerState, {2}=handoffStatus, {3}=runtimeSnapshot
        public const string LogSchedulerPlaybackPausedFormat =
            "播放生命周期已到达 PlayerControl，但 paused 状态异常（{0}）：state={1} handoff={2} {3}。Renderist 不写 paused、不调用 TogglePauseGame，会话 fail-closed。";
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

        // ---------------- Log: RenderistAutoPlay ----------------

        // {0}=error
        public const string LogAutoPlayHitStateFailedFormat =
            "命中前 deterministic autoplay invariant 建立失败，已跳过官方 Hit(true)：{0}";

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
