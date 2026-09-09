using System;
using System.Globalization;
using System.IO;
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
    /// Phase 3.3.0 deterministic editor export (MasterTimeline Deterministic Gameplay Handoff).
    /// Renderist remains passive towards replay / autoplay.
    /// </summary>
    public static class ModEntry
    {
        internal const string HarmonyId = "com.adofai.renderist";

        /// <summary>
        /// 当前 mod 版本。与 Info.json / csproj / 启动日志保持同步，
        /// 由 scripts/set-version.ps1 自动同步。
        /// </summary>
        internal const string ModVersion = "0.3.3.1";

        internal static UnityModManager.ModEntry Mod;
        internal static UnityModManager.ModEntry.ModLogger Logger;
        internal static Settings Settings;
        internal static Harmony Harmony;
        internal static bool Enabled;

        // EditorExportReadiness 报告缓存，避免 OnGUI 每帧重算。
        private const float ReadinessCacheRefreshSeconds = 0.5f;
        private static float _lastReadinessCacheRealtime = float.NegativeInfinity;
        private static EditorExportReadinessReport _cachedReadiness;

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

                Harmony = new Harmony(HarmonyId);

                Log.Info("Loaded ADOFAI Renderist 0.3.3.1 (Phase 3.3.0 deterministic hardening).");
                return true;
            }
            catch (Exception ex)
            {
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
                    Log.Info(UiText.LogEnabled);
                }
                else
                {
                    // Mod 禁用时安全取消当前编辑器导出会话。
                    EditorExportController.Cancel("mod-disabled");
                    // 最终 safety net：该 HarmonyId 只属于 Renderist。
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
                RefreshReadinessCacheIfNeeded();
                DrawOutputDirectoryGui();
                GUILayout.Space(8f);
                DrawEditorExportGui();
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnGUI failed", ex);
            }
        }

        /// <summary>
        /// 刷新 EditorExportReadiness 报告缓存。每 0.5 秒最多一次，无副作用。
        /// </summary>
        private static void RefreshReadinessCacheIfNeeded()
        {
            float now = Time.realtimeSinceStartup;
            if (_lastReadinessCacheRealtime != float.NegativeInfinity &&
                (now - _lastReadinessCacheRealtime) < ReadinessCacheRefreshSeconds)
            {
                return;
            }
            _cachedReadiness = EditorExportPreflight.Run();
            _lastReadinessCacheRealtime = now;
        }

        /// <summary>
        /// 在 Windows 资源管理器中打开目录。只打开查看，不作为目录选择器。
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
        /// 解析 Explorer 打开路径：配置目录存在则用它；否则尝试有效父目录；
        /// 最后回退默认输出目录 / persistentDataPath。
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

        /// <summary>绘制「输出目录设置」段：输入框 + 打开目录按钮 + 路径检查结果 + 默认目录。</summary>
        private static void DrawOutputDirectoryGui()
        {
            GUILayout.Label(UiText.GuiPreflightOutputDirInputPrefix, GUI.skin.label);

            EditorExportReadinessReport report = _cachedReadiness;
            if (report == null) return;

            GUILayout.BeginHorizontal();
            string newDir = GUILayout.TextField(Settings.OutputDirectory ?? string.Empty);
            if (newDir != (Settings.OutputDirectory ?? string.Empty))
            {
                Settings.OutputDirectory = newDir;
                _lastReadinessCacheRealtime = float.NegativeInfinity;
            }
            if (GUILayout.Button(UiText.GuiBtnOpenInExplorer, GUI.skin.button, GUILayout.Width(140)))
            {
                OpenInExplorer(Settings.OutputDirectory);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(UiText.GuiPreflightOutputDirInputHint, GUI.skin.label);

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
                string defaultPath = dirVal.NormalizedPath ?? UiText.GuiNonePlaceholder;
                GUILayout.Label(UiText.GuiPreflightDefaultDirPrefix + defaultPath, GUI.skin.label);
            }
        }

        /// <summary>绘制「编辑器导出」段：就绪状态 + 环境诊断 + MasterTimeline handoff 启动/停止。</summary>
        private static void DrawEditorExportGui()
        {
            GUILayout.Label(UiText.GuiPreflightSectionTitle, GUI.skin.label);

            EditorExportReadinessReport report = _cachedReadiness;
            if (report == null)
            {
                GUILayout.Label(UiText.GuiPreflightStatusPrefix + UiText.GuiPreflightNotChecked, GUI.skin.label);
                return;
            }

            GUILayout.Label(UiText.GuiPreflightStatusPrefix + ReadinessText(report), GUI.skin.label);

            if (Settings.VerboseLogging)
            {
                GUILayout.Space(4f);

                GUILayout.Label(UiText.GuiEnvSectionTitle, GUI.skin.label);
                EditorEnvSnapshot env = report.EditorEnv;
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

                if (report.Readiness != EditorExportReadiness.Ready)
                {
                    GUILayout.Label(UiText.GuiSeeLogForDetails, GUI.skin.label);
                }

                GUILayout.Space(6f);
                GUILayout.Label(UiText.GuiDeveloperDiagnosticsSectionTitle, GUI.skin.label);
                GUILayout.Space(4f);
            }

            DrawMasterTimelineHandoffGui();
        }

        private static string ReadinessText(EditorExportReadinessReport report)
        {
            switch (report.Readiness)
            {
                case EditorExportReadiness.Ready: return UiText.GuiReadinessReady;
                case EditorExportReadiness.Disabled: return UiText.GuiReadinessDisabled;
                case EditorExportReadiness.NotInEditor: return UiText.GuiReadinessNotInEditor;
                case EditorExportReadiness.UnknownEnvironment: return UiText.GuiReadinessUnknown;
                default: return UiText.GuiReadinessBlocked + "（" + report.Reason + "）";
            }
        }

        private static void DrawMasterTimelineHandoffGui()
        {
            GUILayout.Label(UiText.GuiMasterTimelineHandoffSectionTitle, GUI.skin.label);

            EditorExportSession session = EditorExportController.CurrentSession;
            GUILayout.Label(UiText.GuiMasterTimelineHandoffStatusPrefix +
                EditorExportController.CurrentState.ToString(), GUI.skin.label);
            if (session != null)
            {
                GUILayout.Label(UiText.GuiMasterTimelineHandoffFramesPrefix +
                    session.CapturedFrameCount.ToString(CultureInfo.InvariantCulture) + "/" +
                    session.TargetFrameCount.ToString(CultureInfo.InvariantCulture), GUI.skin.label);
            }

            if (GUILayout.Button(EditorExportController.IsBusy
                ? UiText.GuiMasterTimelineHandoffBtnStop
                : UiText.GuiMasterTimelineHandoffBtnStart))
            {
                if (EditorExportController.IsBusy)
                    EditorExportController.Stop();
                else
                    EditorExportController.Start();
            }
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

                // 仅推进当前编辑器确定性导出会话。
                EditorExportController.Tick();
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnUpdate failed", ex);
            }
        }
    }
}
