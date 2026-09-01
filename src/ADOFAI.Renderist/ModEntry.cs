using System;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Gui;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist
{
    /// <summary>
    /// Unity Mod Manager entry point for ADOFAI Renderist.
    /// Phase 2.0 scope: screenshot sequence MVP. No Harmony patches.
    /// Renderist remains passive towards replay / autoplay.
    /// </summary>
    public static class ModEntry
    {
        internal const string HarmonyId = "com.adofai.renderist";

        /// <summary>
        /// 当前 mod 版本（Phase 2.2.3 新增）。与 Info.json / csproj / 启动日志保持同步。
        /// 由 scripts/set-version.ps1 自动同步。供 CaptureService metadata.json version 字段引用，
        /// 避免 metadata version 与 mod version 脱节。
        /// </summary>
        internal const string ModVersion = "0.2.4.0";

        internal static UnityModManager.ModEntry Mod;
        internal static UnityModManager.ModEntry.ModLogger Logger;
        internal static Settings Settings;
        internal static Harmony Harmony;
        internal static bool Enabled;

        // Phase 2.2: Preflight / EditorEnv 缓存，避免 OnGUI 每帧重算。
        // F10 / GUI 启动序列时仍会执行即时 Preflight，不依赖此缓存。
        private const float PreflightCacheRefreshSeconds = 0.5f;
        private static float _lastPreflightCacheRealtime = float.NegativeInfinity;
        private static PreflightReport _cachedPreflightReport;
        private static EditorEnvSnapshot _cachedEditorEnvSnapshot;
        private static EditorExportReadinessReport _cachedEditorExportReadinessReport;
        private static DirectoryBrowserState _directoryBrowserState;

        /// <summary>
        /// UMM entry method, invoked via Info.json's "EntryMethod".
        /// </summary>
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            try
            {
                Mod = modEntry;
                Logger = modEntry.Logger;

                Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);

                modEntry.OnToggle = OnToggle;
                modEntry.OnGUI = OnGUI;
                modEntry.OnSaveGUI = OnSaveGUI;
                modEntry.OnUpdate = OnUpdate;

                // Instantiate Harmony but do NOT PatchAll in Phase 2.
                Harmony = new Harmony(HarmonyId);

                Log.Info("Loaded ADOFAI Renderist 0.2.4.0 (Phase 2.4 editor export skeleton).");
                Log.Warn(UiText.LogStartupPerfWarn);
                return true;
            }
            catch (Exception ex)
            {
                // Defensive: surface a clear failure in UMM Log.txt rather than silently dying.
                (modEntry?.Logger)?.LogException("ModEntry.Load failed", ex);
                return false;
            }
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            try
            {
                if (value == Enabled) return true;
                Enabled = value;

                if (value)
                {
                    // Phase 2: nothing to patch yet. Reserved for Phase 3+.
                    Log.Info(UiText.LogEnabled);
                }
                else
                {
                    if (CaptureService.IsRecording)
                    {
                        CaptureService.StopSequence("disabled");
                    }
                    // Phase 2.4: Mod 禁用时安全取消编辑器导出会话。
                    EditorExportController.Cancel("mod-disabled");
                    // Always safe to call even when no patches are registered.
                    Harmony?.UnpatchAll(HarmonyId);
                    Log.Info(UiText.LogDisabled);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnToggle failed", ex);
                return false;
            }
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            try
            {
                GUILayout.Label("ADOFAI Renderist", GUI.skin.label);
                GUILayout.Label(ModEntry.ModVersion, GUI.skin.label);
                GUILayout.Space(6f);

                Settings.VerboseLogging = GUILayout.Toggle(
                    Settings.VerboseLogging,
                    UiText.GuiVerboseLoggingToggle);

                GUILayout.Space(8f);
                RefreshPreflightCacheIfNeeded();
                DrawOutputDirectoryGui();
                GUILayout.Space(8f);
                DrawPreflightGui();
                GUILayout.Space(8f);
                DrawCaptureGUI();

                // Phase 2.2.2: 目录浏览面板（如果打开）
                if (_directoryBrowserState.IsOpen)
                {
                    GUILayout.Space(8f);
                    if (DirectoryBrowserGui.Draw(ref _directoryBrowserState))
                    {
                        Settings.OutputDirectory = _directoryBrowserState.CurrentPath ?? string.Empty;
                        // 强制刷新 Preflight 缓存，使路径检查结果尽快更新
                        _lastPreflightCacheRealtime = float.NegativeInfinity;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnGUI failed", ex);
            }
        }

        /// <summary>
        /// 刷新 Preflight / EditorEnv / 编辑器导出就绪缓存。每 0.5 秒最多一次。
        /// 无副作用：所有 Run 内部均不创建目录、不改 CaptureService 状态、不改 Unity 时间属性。
        /// </summary>
        private static void RefreshPreflightCacheIfNeeded()
        {
            float now = Time.realtimeSinceStartup;
            if (_lastPreflightCacheRealtime != float.NegativeInfinity &&
                (now - _lastPreflightCacheRealtime) < PreflightCacheRefreshSeconds)
            {
                return;
            }
            _cachedPreflightReport = Preflight.Run();
            _cachedEditorEnvSnapshot = _cachedPreflightReport.EditorEnv;
            _cachedEditorExportReadinessReport = EditorExportPreflight.Run();
            _lastPreflightCacheRealtime = now;
        }

        /// <summary>
        /// 在 Windows 资源管理器中打开目录。Phase 2.2.3 新增。
        /// 只打开目录查看，不作为目录选择器，不回填路径。
        /// 无副作用：不创建目录、不写文件。
        /// </summary>
        private static void OpenInExplorer(string configuredDir)
        {
            string path = ResolveExplorerPath(configuredDir);
            if (string.IsNullOrEmpty(path))
            {
                Log.Warn(UiText.Format(UiText.LogOpenExplorerFailedFormat, "no valid path"));
                return;
            }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Warn(UiText.Format(UiText.LogOpenExplorerFailedFormat, ex.Message));
            }
        }

        /// <summary>
        /// 解析 Explorer 打开路径。Phase 2.2.3 新增。
        /// 1. 配置目录存在 → 打开它
        /// 2. 配置目录不存在 → 尝试有效父目录
        /// 3. 否则打开默认目录（persistentDataPath 下的 captures）
        /// 4. 否则打开 persistentDataPath
        /// </summary>
        private static string ResolveExplorerPath(string configuredDir)
        {
            if (!string.IsNullOrEmpty(configuredDir) && Directory.Exists(configuredDir))
            {
                return configuredDir;
            }
            if (!string.IsNullOrEmpty(configuredDir))
            {
                string path = configuredDir;
                while (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        if (Directory.Exists(path)) return path;
                        DirectoryInfo parent = Directory.GetParent(path);
                        if (parent == null) break;
                        path = parent.FullName;
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            try
            {
                string defaultRoot = Path.Combine(Application.persistentDataPath, "ADOFAI.Renderist/captures");
                if (Directory.Exists(defaultRoot)) return defaultRoot;
                if (Directory.Exists(Application.persistentDataPath)) return Application.persistentDataPath;
            }
            catch
            {
            }
            return string.Empty;
        }

        /// <summary>
        /// 绘制「输出目录设置」段（Phase 2.2.2 拆出）。
        /// 含输入框 + 浏览按钮 + 路径检查结果 + 默认目录。
        /// </summary>
        private static void DrawOutputDirectoryGui()
        {
            GUILayout.Label(UiText.GuiPreflightOutputDirInputPrefix, GUI.skin.label);

            PreflightReport report = _cachedPreflightReport;
            if (report == null) return;

            // 输入框 + 在资源管理器中打开按钮（Phase 2.2.3：移除自研浏览面板入口）
            GUILayout.BeginHorizontal();
            string newDir = GUILayout.TextField(Settings.OutputDirectory ?? string.Empty);
            if (newDir != (Settings.OutputDirectory ?? string.Empty))
            {
                Settings.OutputDirectory = newDir;
                // 输出目录变更后立即使 Preflight / EditorExport 缓存失效，
                // 使目录验证摘要在下一次 OnGUI 绘制时更新。
                _lastPreflightCacheRealtime = float.NegativeInfinity;
            }
            if (GUILayout.Button(UiText.GuiBtnOpenInExplorer, GUI.skin.button, GUILayout.Width(140)))
            {
                OpenInExplorer(Settings.OutputDirectory);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(UiText.GuiPreflightOutputDirInputHint, GUI.skin.label);

            // 路径检查结果
            DirectoryValidationResult dirVal = report.OutputDirectoryValidation;
            string pathCheckText;
            switch (dirVal.Outcome)
            {
                case DirectoryValidationOutcome.Accept:
                    pathCheckText = UiText.GuiPreflightPathCheckAccept;
                    break;
                case DirectoryValidationOutcome.FallBackToDefault:
                    pathCheckText = UiText.GuiPreflightPathCheckFallBack;
                    break;
                case DirectoryValidationOutcome.Reject:
                    pathCheckText = UiText.GuiPreflightPathCheckReject;
                    if (Settings.VerboseLogging)
                    {
                        pathCheckText += "（" + (dirVal.RejectReason ?? "?") + "）";
                    }
                    break;
                default:
                    pathCheckText = UiText.GuiPreflightNotChecked;
                    break;
            }
            GUILayout.Label(UiText.GuiPreflightPathCheckPrefix + pathCheckText, GUI.skin.label);

            if (dirVal.Outcome == DirectoryValidationOutcome.FallBackToDefault)
            {
                string defaultPath = report.OutputDirectory ?? UiText.GuiNonePlaceholder;
                GUILayout.Label(UiText.GuiPreflightDefaultDirPrefix + defaultPath, GUI.skin.label);
            }
        }

        /// <summary>
        /// 绘制「导出前检查」+「环境诊断」段。Phase 2.2 新增。
        /// 只读展示，不触发任何操作。
        /// </summary>
        private static void DrawPreflightGui()
        {
            GUILayout.Label(UiText.GuiPreflightSectionTitle, GUI.skin.label);

            PreflightReport report = _cachedPreflightReport;
            if (report == null)
            {
                GUILayout.Label(UiText.GuiPreflightStatusPrefix + UiText.GuiPreflightNotChecked, GUI.skin.label);
                return;
            }

            string statusText;
            switch (report.Overall)
            {
                case PreflightResult.Pass: statusText = UiText.GuiPreflightStatusPass; break;
                case PreflightResult.Warn: statusText = UiText.GuiPreflightStatusWarn; break;
                case PreflightResult.Fail: statusText = UiText.GuiPreflightStatusFail; break;
                default: statusText = UiText.GuiPreflightNotChecked; break;
            }
            GUILayout.Label(UiText.GuiPreflightStatusPrefix + statusText, GUI.skin.label);

            ExportState state = ExportStateMachine.CurrentState;
            string stateText;
            switch (state)
            {
                case ExportState.Idle: stateText = UiText.GuiExportStateIdle; break;
                case ExportState.Capturing: stateText = UiText.GuiExportStateCapturing; break;
                case ExportState.Failed: stateText = UiText.GuiExportStateFailed; break;
                default: stateText = UiText.GuiExportStateIdle; break;
            }
            GUILayout.Label(UiText.GuiExportStatePrefix + stateText, GUI.skin.label);

            // Phase 2.2.2: 输出目录段已拆出至 DrawOutputDirectoryGui
            // Phase 2.2.3: 环境诊断段移至 VerboseLogging 门控

            if (Settings.VerboseLogging)
            {
                GUILayout.Space(4f);

                GUILayout.Label(UiText.GuiEnvSectionTitle, GUI.skin.label);
                EditorEnvSnapshot env = _cachedEditorEnvSnapshot;
                string sceneName = env.SceneName == null
                    ? UiText.GuiEnvNotAvailable
                    : (string.IsNullOrEmpty(env.SceneName) ? UiText.GuiEnvSceneEmpty : env.SceneName);
                GUILayout.Label(UiText.GuiEnvSceneNamePrefix + sceneName, GUI.skin.label);
                string camCount = env.CameraCount.HasValue
                    ? env.CameraCount.Value.ToString(CultureInfo.InvariantCulture)
                    : UiText.GuiEnvNotAvailable;
                GUILayout.Label(UiText.GuiEnvCameraCountPrefix + camCount, GUI.skin.label);
                string detectionText;
                switch (env.Detection)
                {
                    case EditorEnvDetection.ProbablyEditor:
                        detectionText = UiText.GuiEnvDetectionProbablyEditor;
                        break;
                    default:
                        detectionText = UiText.GuiEnvDetectionUnknown;
                        break;
                }
                GUILayout.Label(UiText.GuiEnvDetectionPrefix + detectionText, GUI.skin.label);

                GUILayout.Space(6f);
                DrawEditorExportReadinessGui();
            }
        }

        /// <summary>
        /// 绘制「编辑器导出就绪」段（Phase 2.3）。
        /// 仅在 Detailed Log / VerboseLogging 区域显示，不增加默认界面复杂度。
        /// 不实现任何导出执行器，只展示就绪状态、环境与阻断原因。
        /// </summary>
        private static void DrawEditorExportReadinessGui()
        {
            GUILayout.Label(UiText.GuiEditorExportSectionTitle, GUI.skin.label);
            GUILayout.Label(UiText.GuiEditorExportNotImplementedWarn, GUI.skin.label);

            // 实验性开关
            bool newEnabled = GUILayout.Toggle(
                Settings.EditorExportEnabled,
                UiText.GuiEditorExportEnabledToggle);
            if (newEnabled != Settings.EditorExportEnabled)
            {
                Settings.EditorExportEnabled = newEnabled;
                // 强制刷新缓存，使状态立即更新
                _lastPreflightCacheRealtime = float.NegativeInfinity;
            }

            // 目标帧率输入（仅意图参数）
            GUILayout.BeginHorizontal();
            GUILayout.Label(UiText.GuiEditorExportTargetFrameRatePrefix, GUI.skin.label);
            string frameRateText = GUILayout.TextField(
                Settings.EditorTargetFrameRate.ToString(CultureInfo.InvariantCulture),
                GUILayout.Width(80));
            GUILayout.EndHorizontal();
            if (int.TryParse(frameRateText, out int parsedFrameRate) &&
                parsedFrameRate != Settings.EditorTargetFrameRate)
            {
                Settings.EditorTargetFrameRate = parsedFrameRate;
                _lastPreflightCacheRealtime = float.NegativeInfinity;
            }

            EditorExportReadinessReport report = _cachedEditorExportReadinessReport;
            if (report == null)
            {
                GUILayout.Label(UiText.GuiPreflightNotChecked, GUI.skin.label);
                return;
            }

            // 就绪状态
            string readinessText;
            switch (report.Readiness)
            {
                case EditorExportReadiness.Disabled:
                    readinessText = UiText.GuiEditorExportReadinessDisabled;
                    break;
                case EditorExportReadiness.NotInEditor:
                    readinessText = UiText.GuiEditorExportReadinessNotInEditor;
                    break;
                case EditorExportReadiness.UnknownEnvironment:
                    readinessText = UiText.GuiEditorExportReadinessUnknownEnvironment;
                    break;
                case EditorExportReadiness.Blocked:
                    readinessText = UiText.GuiEditorExportReadinessBlocked;
                    break;
                case EditorExportReadiness.Ready:
                    readinessText = UiText.GuiEditorExportReadinessReady;
                    break;
                default:
                    readinessText = UiText.GuiPreflightNotChecked;
                    break;
            }
            GUILayout.Label(UiText.GuiEditorExportReadinessPrefix + readinessText, GUI.skin.label);

            // 原因
            string reasonText;
            switch (report.Reason)
            {
                case EditorExportReadinessReason.None:
                    reasonText = UiText.GuiEditorExportReasonNone;
                    break;
                case EditorExportReadinessReason.FeatureDisabled:
                    reasonText = UiText.GuiEditorExportReasonFeatureDisabled;
                    break;
                case EditorExportReadinessReason.EditorSceneNotDetected:
                    reasonText = UiText.GuiEditorExportReasonEditorSceneNotDetected;
                    break;
                case EditorExportReadinessReason.EnvironmentUnavailable:
                    reasonText = UiText.GuiEditorExportReasonEnvironmentUnavailable;
                    break;
                case EditorExportReadinessReason.CaptureBusy:
                    reasonText = UiText.GuiEditorExportReasonCaptureBusy;
                    break;
                case EditorExportReadinessReason.InvalidTargetFrameRate:
                    reasonText = UiText.GuiEditorExportReasonInvalidTargetFrameRate;
                    break;
                case EditorExportReadinessReason.InvalidOutputDirectory:
                    reasonText = UiText.GuiEditorExportReasonInvalidOutputDirectory;
                    break;
                default:
                    reasonText = UiText.GuiPreflightNotChecked;
                    break;
            }
            GUILayout.Label(UiText.GuiEditorExportReasonPrefix + reasonText, GUI.skin.label);

            // 环境快照
            EditorEnvSnapshot env = report.EditorEnv;
            string sceneName = env.SceneName == null
                ? UiText.GuiEnvNotAvailable
                : (string.IsNullOrEmpty(env.SceneName) ? UiText.GuiEnvSceneEmpty : env.SceneName);
            GUILayout.Label(UiText.GuiEnvSceneNamePrefix + sceneName, GUI.skin.label);

            string detectionText;
            switch (env.Detection)
            {
                case EditorEnvDetection.ProbablyEditor:
                    detectionText = UiText.GuiEnvDetectionProbablyEditor;
                    break;
                default:
                    detectionText = UiText.GuiEnvDetectionUnknown;
                    break;
            }
            GUILayout.Label(UiText.GuiEnvDetectionPrefix + detectionText, GUI.skin.label);

            string timeScaleText = env.TimeScale.HasValue
                ? env.TimeScale.Value.ToString("0.###", CultureInfo.InvariantCulture)
                : UiText.GuiEnvNotAvailable;
            GUILayout.Label(UiText.GuiEditorExportEnvTimeScalePrefix + timeScaleText, GUI.skin.label);

            string captureFramerateText = env.CaptureFramerate.HasValue
                ? env.CaptureFramerate.Value.ToString(CultureInfo.InvariantCulture)
                : UiText.GuiEnvNotAvailable;
            GUILayout.Label(UiText.GuiEditorExportEnvCaptureFrameratePrefix + captureFramerateText, GUI.skin.label);

            string isFocusedText = env.IsFocused.HasValue
                ? (env.IsFocused.Value ? "true" : "false")
                : UiText.GuiEnvNotAvailable;
            GUILayout.Label(UiText.GuiEditorExportEnvIsFocusedPrefix + isFocusedText, GUI.skin.label);

            string screenSizeText = env.ScreenWidth.HasValue && env.ScreenHeight.HasValue
                ? $"{env.ScreenWidth.Value}x{env.ScreenHeight.Value}"
                : UiText.GuiEnvNotAvailable;
            GUILayout.Label(UiText.GuiEditorExportEnvScreenSizePrefix + screenSizeText, GUI.skin.label);

            string camCount = env.CameraCount.HasValue
                ? env.CameraCount.Value.ToString(CultureInfo.InvariantCulture)
                : UiText.GuiEnvNotAvailable;
            GUILayout.Label(UiText.GuiEnvCameraCountPrefix + camCount, GUI.skin.label);

            // 实时序列占用
            GUILayout.Label(UiText.GuiEditorExportIsRecordingPrefix +
                (report.IsRecording ? UiText.GuiStatusRecording : UiText.GuiStatusIdle), GUI.skin.label);

            // 输出目录验证摘要
            DirectoryValidationResult dirVal = report.OutputDirectoryValidation;
            string dirSummary;
            switch (dirVal.Outcome)
            {
                case DirectoryValidationOutcome.Accept:
                    dirSummary = UiText.GuiPreflightPathCheckAccept;
                    break;
                case DirectoryValidationOutcome.FallBackToDefault:
                    dirSummary = UiText.GuiPreflightPathCheckFallBack;
                    break;
                case DirectoryValidationOutcome.Reject:
                    dirSummary = UiText.GuiPreflightPathCheckReject +
                        "（" + (dirVal.RejectReason ?? "?") + "）";
                    break;
                default:
                    dirSummary = UiText.GuiPreflightNotChecked;
                    break;
            }
            GUILayout.Label(UiText.GuiPreflightPathCheckPrefix + dirSummary, GUI.skin.label);

            // Phase 2.4: 编辑器导出骨架控制段（仅 EditorExportEnabled 时显示）
            if (Settings.EditorExportEnabled)
            {
                GUILayout.Space(6f);
                DrawEditorExportControlGui(report);
            }
        }

        /// <summary>
        /// 绘制编辑器导出骨架控制段（Phase 2.4）。
        /// 仅在 Detailed Log + EditorExportEnabled 时显示。
        /// 不新增 FPS / MaxFrames / Camera / UI / Replay 等配置控件。
        /// </summary>
        private static void DrawEditorExportControlGui(EditorExportReadinessReport report)
        {
            GUILayout.Label(UiText.GuiEditorExportSkeletonSectionTitle, GUI.skin.label);
            GUILayout.Label(UiText.GuiEditorExportSkeletonNotImplementedWarn, GUI.skin.label);

            EditorExportState state = EditorExportController.CurrentState;
            string stateText;
            switch (state)
            {
                case EditorExportState.Idle: stateText = UiText.GuiEditorExportStateIdle; break;
                case EditorExportState.Preparing: stateText = UiText.GuiEditorExportStatePreparing; break;
                case EditorExportState.Running: stateText = UiText.GuiEditorExportStateRunning; break;
                case EditorExportState.Cleaning: stateText = UiText.GuiEditorExportStateCleaning; break;
                case EditorExportState.Completed: stateText = UiText.GuiEditorExportStateCompleted; break;
                case EditorExportState.Cancelled: stateText = UiText.GuiEditorExportStateCancelled; break;
                case EditorExportState.Failed: stateText = UiText.GuiEditorExportStateFailed; break;
                default: stateText = UiText.GuiEditorExportStateIdle; break;
            }
            GUILayout.Label(UiText.GuiEditorExportStatePrefix + stateText, GUI.skin.label);

            EditorExportSession session = EditorExportController.CurrentSession;
            string detail = (session != null && !string.IsNullOrEmpty(session.StateDetail))
                ? session.StateDetail
                : UiText.GuiEditorExportNotRunning;
            GUILayout.Label(UiText.GuiEditorExportStateDetailPrefix + detail, GUI.skin.label);

            string dir = (session != null && !string.IsNullOrEmpty(session.OutputDirectory))
                ? session.OutputDirectory
                : UiText.GuiEditorExportNotRunning;
            GUILayout.Label(UiText.GuiEditorExportSessionDirPrefix + dir, GUI.skin.label);

            string startedAt = (session != null && session.StartedAtUtc.HasValue)
                ? session.StartedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : UiText.GuiEditorExportNotRunning;
            GUILayout.Label(UiText.GuiEditorExportStartedAtPrefix + startedAt, GUI.skin.label);

            long tick = session != null ? session.TickCount : 0;
            GUILayout.Label(UiText.GuiEditorExportTickCountPrefix +
                tick.ToString(CultureInfo.InvariantCulture), GUI.skin.label);

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            bool canStart = !EditorExportController.IsBusy &&
                report != null && report.Readiness == EditorExportReadiness.Ready;
            GUI.enabled = canStart;
            if (GUILayout.Button(UiText.GuiEditorExportBtnStart))
            {
                EditorExportController.Start();
            }
            GUI.enabled = EditorExportController.IsBusy;
            if (GUILayout.Button(UiText.GuiEditorExportBtnStop))
            {
                EditorExportController.Stop();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private static void DrawCaptureGUI()
        {
            // Status block — never reuses the phase label string to keep
            // scripts/set-version.ps1 matching exactly one phase label.

            // 序列段（F10）
            string seqStatus = CaptureService.IsRecording ? UiText.GuiStatusRecording : UiText.GuiStatusIdle;
            int seqRequested = CaptureService.FramesRequestedInSession;
            string seqDir = string.IsNullOrEmpty(CaptureService.CurrentSessionDirectory)
                ? UiText.GuiNonePlaceholder
                : CaptureService.CurrentSessionDirectory;

            GUILayout.Label(UiText.GuiSequenceStatusPrefix + seqStatus, GUI.skin.label);
            GUILayout.Label(UiText.GuiSequenceFramesRequestedPrefix +
                seqRequested.ToString(CultureInfo.InvariantCulture), GUI.skin.label);
            GUILayout.Label(UiText.GuiSequenceDirectoryPrefix + seqDir, GUI.skin.label);

            GUILayout.Space(4f);

            // 单帧段（F9）— 与序列状态完全独立
            int singleCount = CaptureService.SingleFrameCaptureCountThisRun;
            string singleTime = CaptureService.LastSingleCaptureTimeLocal.HasValue
                ? CaptureService.LastSingleCaptureTimeLocal.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : UiText.GuiNonePlaceholder;
            string singleDir = string.IsNullOrEmpty(CaptureService.LastSingleCaptureDirectory)
                ? UiText.GuiNonePlaceholder
                : CaptureService.LastSingleCaptureDirectory;
            string singleFile = string.IsNullOrEmpty(CaptureService.LastSingleCaptureFile)
                ? UiText.GuiNonePlaceholder
                : CaptureService.LastSingleCaptureFile;

            GUILayout.Label(UiText.GuiSingleCountPrefix +
                singleCount.ToString(CultureInfo.InvariantCulture), GUI.skin.label);
            GUILayout.Label(UiText.GuiSingleLastTimePrefix + singleTime, GUI.skin.label);
            GUILayout.Label(UiText.GuiSingleLastDirPrefix + singleDir, GUI.skin.label);
            GUILayout.Label(UiText.GuiSingleLastFilePrefix + singleFile, GUI.skin.label);

            // Phase 2.2.2: 单帧拒绝状态（最近一次 F9 / GUI 单帧被 Preflight 阻止）
            // Phase 2.2.3: 拒绝原因详情移至 VerboseLogging 门控
            if (ExportCoordinator.LastSingleCaptureRejectReason != null)
            {
                GUILayout.Label(UiText.GuiSingleRejectStatusPrefix + UiText.GuiSingleRejectStatus, GUI.skin.label);
                if (Settings.VerboseLogging)
                {
                    GUILayout.Label(UiText.GuiSingleRejectReasonPrefix + ExportCoordinator.LastSingleCaptureRejectReason, GUI.skin.label);
                    if (ExportCoordinator.LastSingleCaptureRejectTimeLocal.HasValue)
                    {
                        string rejectTime = ExportCoordinator.LastSingleCaptureRejectTimeLocal.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                        GUILayout.Label(UiText.GuiSingleRejectTimePrefix + rejectTime, GUI.skin.label);
                    }
                }
                else
                {
                    GUILayout.Label(UiText.GuiSeeLogForDetails, GUI.skin.label);
                }
            }

            // Phase 2.2.3: 节流参数 + UMM 提示移至 VerboseLogging 门控
            if (Settings.VerboseLogging)
            {
                GUILayout.Space(4f);

                GUILayout.Label(UiText.Format(UiText.GuiThrottleFormat,
                    Settings.CaptureEveryNFrames.ToString(CultureInfo.InvariantCulture),
                    Settings.TargetCaptureFps.ToString("0.###", CultureInfo.InvariantCulture),
                    Settings.MaxFramesPerSession.ToString(CultureInfo.InvariantCulture),
                    Settings.CaptureSuperSize.ToString(CultureInfo.InvariantCulture)),
                    GUI.skin.label);

                GUILayout.Space(4f);
                GUILayout.Label(UiText.GuiUmmPanelNote, GUI.skin.label);
            }

            GUILayout.Space(6f);
            bool editorExportBusy = EditorExportController.IsBusy;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(UiText.GuiBtnCaptureSingleNextTick))
            {
                if (editorExportBusy)
                {
                    Log.Warn(UiText.LogEditorExportF9F10Blocked);
                }
                else
                {
                    ExportCoordinator.TrySingleCaptureNextTick("gui");
                }
            }
            if (!CaptureService.IsRecording)
            {
                GUI.enabled = !editorExportBusy;
                if (GUILayout.Button(UiText.GuiBtnStartSequence))
                {
                    if (editorExportBusy)
                    {
                        Log.Warn(UiText.LogEditorExportF9F10Blocked);
                    }
                    else
                    {
                        ExportCoordinator.TryStartSequence("gui");
                    }
                }
                GUI.enabled = true;
            }
            else
            {
                if (GUILayout.Button(UiText.GuiBtnStopSequence))
                {
                    CaptureService.StopSequence("user");
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            Settings.HotkeysEnabled = GUILayout.Toggle(
                Settings.HotkeysEnabled,
                UiText.Format(UiText.GuiHotkeysToggleFormat,
                    Settings.SingleCaptureHotkey,
                    Settings.SequenceHotkey));
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            try
            {
                Settings.Save(modEntry);
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnSaveGUI failed", ex);
            }
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            try
            {
                if (!Enabled) return;

                if (Settings != null && Settings.HotkeysEnabled)
                {
                    // Phase 2.4: 编辑器导出会话进行中时阻止 F9/F10，避免状态耦合。
                    if (Input.GetKeyDown(Settings.SingleCaptureHotkey))
                    {
                        if (EditorExportController.IsBusy)
                        {
                            Log.Warn(UiText.LogEditorExportF9F10Blocked);
                        }
                        else
                        {
                            ExportCoordinator.TrySingleCapture("hotkey");
                        }
                    }
                    if (Input.GetKeyDown(Settings.SequenceHotkey))
                    {
                        if (EditorExportController.IsBusy)
                        {
                            Log.Warn(UiText.LogEditorExportF9F10Blocked);
                        }
                        else if (CaptureService.IsRecording)
                        {
                            CaptureService.StopSequence("user");
                        }
                        else
                        {
                            ExportCoordinator.TryStartSequence("hotkey");
                        }
                    }
                }

                CaptureService.Tick();
                // Phase 2.4: 编辑器导出会话生命周期推进（不截图、不推进时间）。
                EditorExportController.Tick();
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnUpdate failed", ex);
            }
        }
    }
}
