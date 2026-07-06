using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Export;
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

                Log.Info("Loaded ADOFAI Renderist 0.2.2.1 (Phase 2.2.1 output directory GUI validation bridge).");
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
                GUILayout.Label("ADOFAI Renderist — Phase 2.2.1 output directory GUI validation bridge", GUI.skin.label);
                GUILayout.Space(6f);

                Settings.VerboseLogging = GUILayout.Toggle(
                    Settings.VerboseLogging,
                    UiText.GuiVerboseLoggingToggle);

                GUILayout.Space(8f);
                RefreshPreflightCacheIfNeeded();
                DrawPreflightGui();
                GUILayout.Space(8f);
                DrawCaptureGUI();
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnGUI failed", ex);
            }
        }

        /// <summary>
        /// 刷新 Preflight / EditorEnv 缓存。每 0.5 秒最多一次。
        /// 无副作用：Preflight.Run 内部不创建目录、不改 CaptureService 状态。
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
            _lastPreflightCacheRealtime = now;
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

            // Phase 2.2.1: 输出目录输入框 + 路径检查结果
            GUILayout.Label(UiText.GuiPreflightOutputDirInputPrefix + " " + UiText.GuiPreflightOutputDirInputHint, GUI.skin.label);
            string newDir = GUILayout.TextField(Settings.OutputDirectory ?? string.Empty);
            if (newDir != (Settings.OutputDirectory ?? string.Empty))
            {
                Settings.OutputDirectory = newDir;
            }

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
                    pathCheckText = UiText.GuiPreflightPathCheckReject + "（" + (dirVal.RejectReason ?? "?") + "）";
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

            GUILayout.Space(4f);

            GUILayout.Label(UiText.GuiEnvSectionTitle, GUI.skin.label);
            EditorEnvSnapshot env = _cachedEditorEnvSnapshot;
            string sceneName = string.IsNullOrEmpty(env.SceneName) ? UiText.GuiEnvSceneEmpty : env.SceneName;
            GUILayout.Label(UiText.GuiEnvSceneNamePrefix + sceneName, GUI.skin.label);
            string camCount = env.CameraCount < 0
                ? UiText.GuiEnvNotAvailable
                : env.CameraCount.ToString(CultureInfo.InvariantCulture);
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

            GUILayout.Space(4f);

            GUILayout.Label(UiText.Format(UiText.GuiThrottleFormat,
                Settings.CaptureEveryNFrames.ToString(CultureInfo.InvariantCulture),
                Settings.TargetCaptureFps.ToString("0.###", CultureInfo.InvariantCulture),
                Settings.MaxFramesPerSession.ToString(CultureInfo.InvariantCulture),
                Settings.CaptureSuperSize.ToString(CultureInfo.InvariantCulture)),
                GUI.skin.label);

            GUILayout.Space(4f);
            GUILayout.Label(UiText.GuiUmmPanelNote, GUI.skin.label);

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(UiText.GuiBtnCaptureSingleNextTick))
            {
                CaptureService.RequestSingleCaptureNextTick();
            }
            if (!CaptureService.IsRecording)
            {
                if (GUILayout.Button(UiText.GuiBtnStartSequence))
                {
                    ExportCoordinator.TryStartSequence("gui");
                }
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
                    if (Input.GetKeyDown(Settings.SingleCaptureHotkey))
                    {
                        CaptureService.RequestSingleCaptureNow();
                    }
                    if (Input.GetKeyDown(Settings.SequenceHotkey))
                    {
                        if (CaptureService.IsRecording) CaptureService.StopSequence("user");
                        else ExportCoordinator.TryStartSequence("hotkey");
                    }
                }

                CaptureService.Tick();
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnUpdate failed", ex);
            }
        }
    }
}
