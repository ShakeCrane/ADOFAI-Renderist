using System.Globalization;

namespace ADOFAI.Renderist
{
    /// <summary>
    /// 集中存放面向用户的 UI / 日志中文文案。
    ///
    /// 约定（Phase 2.1 中文 UI 基线）：
    ///   * 仅集中 <b>面向用户</b> 的 GUI 字符串与 <c>Log.Info / Warn / Error / Exception</c> 文案。
    ///   * <c>Log.Debug</c> 保留英文（仅 VerboseLogging=true 时输出，开发者向）。
    ///   * <c>Log.Exception</c> 上下文使用"中文短句 + 英文 API/技术名"形式，保留可 grep 的英文锚。
    ///   * 不翻译机读契约：
    ///       - <c>metadata.json</c> 字段名（version / phase / createdAt / endedAt / mode / prefix / stopReason 等）。
    ///       - <c>Mode</c> 取值 "single" / "sequence"。
    ///       - <c>StopReason</c> 取值 "user" / "max-frames" / "disabled" / "error"。
    ///       - <c>Metadata.Phase</c> 锚字符串 "Phase 2.0 screenshot sequence MVP"。
    ///       - 文件名前缀 "single_" / "frame_"，文件扩展名 .png，元数据文件名 metadata.json / *.meta.json。
    ///       - 日志前缀 "[Renderist] "（位于 Logging/Log.cs）。
    ///       - 技术关键词：Application.persistentDataPath / ScreenCapture.CaptureScreenshot / Managed / UnityModManager / UMM / Harmony / PNG / F9 / F10 / superSize / everyN / targetFps / maxFrames 等。
    ///   * 新增日志/GUI 文案时禁止再写硬编码英文中文，统一在本类增补常量或 Format 模板。
    /// </summary>
    internal static class UiText
    {
        // ---------------- GUI: ModEntry ----------------

        public const string GuiTitle = "ADOFAI Renderist — Phase 2.1 中文 UI 基线";
        public const string GuiVerboseLoggingToggle = " 详细日志";

        // 序列段（F10 连续帧序列状态）
        public const string GuiSequenceStatusPrefix = "序列状态：";
        public const string GuiStatusRecording = "录制中";
        public const string GuiStatusIdle = "空闲";

        public const string GuiSequenceFramesRequestedPrefix = "本次序列已请求帧数：";
        public const string GuiSequenceDirectoryPrefix = "当前序列目录：";

        // 单帧段（F9 单张截图，本次运行内只读状态）
        // GUI 文案统一用"请求"而非"成功"，与序列同理：ScreenCapture.CaptureScreenshot 无完成回调，
        // 仅能确认 GPU 请求已发出，不能确认 PNG 已落盘。
        public const string GuiSingleCountPrefix = "单帧请求次数（本次运行）：";
        public const string GuiSingleLastTimePrefix = "最近单帧请求时间：";
        public const string GuiSingleLastDirPrefix = "最近单帧目录：";
        public const string GuiSingleLastFilePrefix = "最近单帧文件：";

        // 通用占位（无值时显示）
        public const string GuiNonePlaceholder = "（尚无）";

        // 节流参数行使用占位模板，避免 GUI 重新拼接：{0}=everyN {1}=targetFps {2}=maxFrames {3}=superSize
        public const string GuiThrottleFormat =
            "节流参数：everyN={0}, targetFps={1}, maxFrames={2}, superSize={3}";

        public const string GuiUmmPanelNote =
            "提示：截图可能包含 UMM 面板。如需干净画面，请先折叠 UMM 面板再使用快捷键。";

        public const string GuiBtnCaptureSingleNextTick = "下一帧截取单张";
        public const string GuiBtnStartSequence = "开始序列截图";
        public const string GuiBtnStopSequence = "停止序列截图";

        // Hotkeys 行模板：{0}=single key, {1}=sequence key
        public const string GuiHotkeysToggleFormat = " 启用快捷键（单张={0}，序列={1}）";

        // ---------------- Log: ModEntry ----------------

        // 注：ModEntry.cs:46 启动日志（"Loaded ADOFAI Renderist X.Y.Z (...)" ）
        // 与 ModEntry.cs:94 GUI 标题（"ADOFAI Renderist — ..."）是 scripts/set-version.ps1
        // 的正则锚点，必须保留英文字面量原样，不在 UiText 中集中——否则下次版本升级会失败。

        public const string LogStartupPerfWarn =
            "实时写入 PNG 会降低帧率并占用磁盘空间，请保持序列短小。";

        public const string LogEnabled = "已启用。";
        public const string LogDisabled = "已禁用。已撤销 Harmony 补丁（若有）。";

        // ---------------- Log: CaptureService ----------------

        public const string LogSingleAbortedNoDir = "单帧截图已中止：没有可用的输出目录。";

        // {0}=filePath, {1}=superSize
        public const string LogSingleRequestedFormat =
            "已请求单帧截图 -> {0} （superSize={1}）。实际写入为异步过程，无法确认。";

        public const string LogSingleQueuedNextTick =
            "单帧截图已排队到下一帧。UMM 面板可能仍可见——如不希望被截入，请先折叠面板。";

        public const string LogSequenceStartIgnoredAlreadyRunning =
            "已忽略 StartSequence：当前已有正在进行的序列。";

        public const string LogSequenceAbortedNoDir = "序列截图已中止：没有可用的输出目录。";

        // {0}=trigger, {1}=dir
        public const string LogSequenceStartFormat = "序列截图 START（{0}）-> {1}";
        // {0}=superSize, {1}=everyN, {2}=targetFps, {3}=maxFrames
        public const string LogSequenceStartParamsFormat =
            "  superSize={0}, everyN={1}, targetFps={2}, maxFrames={3}";
        public const string LogSequencePerfWarn =
            "实时写入 PNG 会降低帧率并占用磁盘空间，请使用短会话。";

        // {0}=reason, {1}=framesRequested, {2}=filesDetected 或 n/a, {3}=dir
        public const string LogSequenceStopFormat =
            "序列截图 STOP（{0}） 已请求帧数={1}，检测到的文件数={2}，目录={3}";

        // ---------------- Log: CaptureService 异常上下文（中文短句 + 英文 API 名） ----------------

        // {0}=filePath
        public const string LogExScreenCaptureThrewFormat =
            "截图调用失败 (ScreenCapture.CaptureScreenshot): {0}";

        // {0}=dir
        public const string LogExDirectoryScanFailedFormat = "目录扫描失败 (Directory.GetFiles): {0}";

        public const string LogExSingleMetadataFailed = "写入单帧元数据失败 (metadata)";

        // ---------------- Log: OutputPath ----------------

        // {0}=dir
        public const string LogOutDirConfiguredCreateFailedFormat =
            "无法创建配置的输出目录，回退到默认目录：{0}";

        public const string LogOutDirPrepareFailed = "无法准备任何输出目录；截图已中止。";

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
