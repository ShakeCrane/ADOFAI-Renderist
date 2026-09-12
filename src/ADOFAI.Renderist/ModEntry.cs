using System;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist
{
    /// <summary>
    /// Unity Mod Manager entry point for ADOFAI Renderist.
    /// Phase 3.5.0 Render Source Isolation.
    /// Renderist remains passive towards replay / autoplay.
    /// </summary>
    public static class ModEntry
    {
        internal const string HarmonyId = "com.adofai.renderist";

        /// <summary>
        /// 当前 mod 版本。与 Info.json / csproj / 启动日志保持同步，
        /// 由 scripts/set-version.ps1 自动同步。
        /// </summary>
        internal const string ModVersion = "0.3.5.1";

        internal static UnityModManager.ModEntry Mod;
        internal static UnityModManager.ModEntry.ModLogger Logger;
        internal static Settings Settings;
        internal static Harmony Harmony;
        internal static bool Enabled;

        // EditorExportReadiness 报告缓存，避免 OnGUI 每帧重算。
        private const float ReadinessCacheRefreshSeconds = 0.5f;
        private static float _lastReadinessCacheRealtime = float.NegativeInfinity;
        private static EditorExportReadinessReport _cachedReadiness;

        // End Tail GUI keeps one unformatted canonical output duration so unit
        // switches do not accumulate display-rounding drift.
        private static string _endTailValueText;
        private static EndTailUnit _endTailDisplayedUnit = EndTailUnit.Frames;
        private static double _endTailCanonicalSeconds;
        private static bool _endTailCanonicalSecondsValid;
        private static bool _endTailInputValid = true;
        private static string _endTailInputError;
        private static bool _endTailUnitMenuOpen;

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
                ResetEndTailGuiState();

                modEntry.OnToggle = OnToggle;
                modEntry.OnGUI = OnGUI;
                modEntry.OnSaveGUI = OnSaveGUI;
                modEntry.OnUpdate = OnUpdate;

                Harmony = new Harmony(HarmonyId);

                Log.Info("Loaded ADOFAI Renderist 0.3.5.1 (Phase 3.5.0 Render Source Isolation).");
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

        /// <summary>绘制「编辑器导出」段：opt-in 开关 + 就绪状态 + 环境诊断 + MasterTimeline handoff 启动/停止。</summary>
        private static void DrawEditorExportGui()
        {
            GUILayout.Label(UiText.GuiPreflightSectionTitle, GUI.skin.label);

            bool newEditorExportEnabled = GUILayout.Toggle(
                Settings.EditorExportEnabled,
                UiText.GuiEditorExportEnableToggle);
            if (newEditorExportEnabled != Settings.EditorExportEnabled)
            {
                Settings.EditorExportEnabled = newEditorExportEnabled;
                _lastReadinessCacheRealtime = float.NegativeInfinity;
            }

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
            if (report.Readiness == EditorExportReadiness.Blocked)
            {
                switch (report.Reason)
                {
                    case EditorExportReadinessReason.InvalidEndTail:
                        return UiText.GuiEndTailInvalid;
                    case EditorExportReadinessReason.EndTailDependenciesUnavailable:
                        return UiText.GuiEndTailDependenciesUnavailable;
                    case EditorExportReadinessReason.EndTailExceedsSafetyLimit:
                        return UiText.GuiEndTailExceedsSafety;
                }
            }

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

            DrawEndTailGui();

            EditorExportSession session = EditorExportController.CurrentSession;
            GUILayout.Label(UiText.GuiMasterTimelineHandoffStatusPrefix +
                EditorExportController.CurrentState.ToString(), GUI.skin.label);
            if (session != null)
            {
                GUILayout.Label(UiText.GuiMasterTimelineHandoffFramesPrefix +
                    session.CapturedFrameCount.ToString(CultureInfo.InvariantCulture), GUI.skin.label);
                GUILayout.Label(UiText.GuiMasterTimelineHandoffSafetyPrefix +
                    session.SafetyFrameLimit.ToString(CultureInfo.InvariantCulture), GUI.skin.label);
                GUILayout.Label(UiText.GuiMasterTimelineHandoffTailPrefix +
                    session.TailFramesCaptured.ToString(CultureInfo.InvariantCulture) + "/" +
                    (session.ResolvedTailFrames?.ToString(CultureInfo.InvariantCulture) ?? "?"), GUI.skin.label);
            }

            bool buttonEnabled = GUI.enabled;
            if (!EditorExportController.IsBusy)
                GUI.enabled = buttonEnabled && _endTailInputValid;
            bool buttonClicked = GUILayout.Button(EditorExportController.IsBusy
                ? UiText.GuiMasterTimelineHandoffBtnStop
                : UiText.GuiMasterTimelineHandoffBtnStart);
            GUI.enabled = buttonEnabled;
            if (buttonClicked)
            {
                if (EditorExportController.IsBusy)
                    EditorExportController.Stop();
                else
                    EditorExportController.Start();
            }
        }

        private static void DrawEndTailGui()
        {
            EnsureEndTailGuiState();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !EditorExportController.IsBusy;

            GUILayout.BeginHorizontal();
            GUILayout.Label(UiText.GuiEndTailLabel, GUI.skin.label, GUILayout.Width(82f));
            string changedText = GUILayout.TextField(_endTailValueText ?? string.Empty, GUILayout.Width(110f));
            if (!string.Equals(changedText, _endTailValueText, StringComparison.Ordinal))
            {
                _endTailValueText = changedText;
                ApplyEndTailTextInput();
            }

            if (GUILayout.Button(UnitText(_endTailDisplayedUnit) + UiText.GuiEndTailUnitMenuSuffix,
                    GUI.skin.button, GUILayout.Width(76f)))
            {
                _endTailUnitMenuOpen = !_endTailUnitMenuOpen;
            }
            GUILayout.EndHorizontal();

            if (_endTailUnitMenuOpen)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(82f);
                DrawEndTailUnitChoice(EndTailUnit.Frames);
                DrawEndTailUnitChoice(EndTailUnit.Seconds);
                DrawEndTailUnitChoice(EndTailUnit.Beats);
                GUILayout.EndHorizontal();
            }

            GUI.enabled = previousEnabled;
            GUILayout.Label(BuildEndTailPreviewText(), GUI.skin.label);
        }

        private static void DrawEndTailUnitChoice(EndTailUnit unit)
        {
            if (GUILayout.Button(UnitText(unit), GUI.skin.button, GUILayout.Width(76f)))
            {
                SwitchEndTailUnit(unit);
                _endTailUnitMenuOpen = false;
            }
        }

        private static void ResetEndTailGuiState()
        {
            _endTailDisplayedUnit = Enum.IsDefined(typeof(EndTailUnit), Settings.EditorEndTailUnit)
                ? Settings.EditorEndTailUnit
                : EndTailPolicy.DefaultUnit;
            double value = Settings.EditorEndTailValue;
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = EndTailPolicy.DefaultValue;
            else if (value < 0.0)
                value = 0.0;
            else if (_endTailDisplayedUnit == EndTailUnit.Frames &&
                     Math.Abs(value - Math.Round(value)) > 1e-10)
                value = Math.Ceiling(value);

            // Persist the sanitized pair as well as displaying it. Otherwise an
            // invalid value loaded from XML could look repaired while preflight
            // still sees the stale setting.
            Settings.EditorEndTailUnit = _endTailDisplayedUnit;
            Settings.EditorEndTailValue = value;
            _endTailValueText = FormatEndTailValue(value, _endTailDisplayedUnit);
            _endTailCanonicalSeconds = 0.0;
            _endTailCanonicalSecondsValid = false;
            _endTailInputValid = true;
            _endTailInputError = null;
            _endTailUnitMenuOpen = false;
        }

        private static void EnsureEndTailGuiState()
        {
            if (_endTailValueText == null)
                ResetEndTailGuiState();
            if (_endTailCanonicalSecondsValid)
                return;

            if (!TryParseEndTailValue(_endTailValueText, _endTailDisplayedUnit, out double value))
            {
                _endTailInputValid = false;
                _endTailInputError = "end-tail-value-invalid";
                return;
            }

            var input = new EndTailInput(value, _endTailDisplayedUnit);
            if (EndTailPolicy.TryToOutputSeconds(
                    input,
                    Settings.EditorTargetFrameRate,
                    _cachedReadiness?.CompletionBpm,
                    _cachedReadiness?.Pitch,
                    out double seconds,
                    out string error))
            {
                _endTailCanonicalSeconds = seconds;
                _endTailCanonicalSecondsValid = true;
                _endTailInputValid = true;
                _endTailInputError = null;
            }
            else
            {
                _endTailInputValid = false;
                _endTailInputError = error;
            }
        }

        private static void ApplyEndTailTextInput()
        {
            if (!TryParseEndTailValue(_endTailValueText, _endTailDisplayedUnit, out double value))
            {
                _endTailInputValid = false;
                _endTailCanonicalSecondsValid = false;
                _endTailInputError = "end-tail-value-invalid";
                return;
            }

            var input = new EndTailInput(value, _endTailDisplayedUnit);
            if (!EndTailPolicy.TryValidateInput(input, out string validationError))
            {
                _endTailInputValid = false;
                _endTailCanonicalSecondsValid = false;
                _endTailInputError = validationError;
                return;
            }

            Settings.EditorEndTailValue = value;
            Settings.EditorEndTailUnit = _endTailDisplayedUnit;
            _lastReadinessCacheRealtime = float.NegativeInfinity;

            if (EndTailPolicy.TryToOutputSeconds(
                    input,
                    Settings.EditorTargetFrameRate,
                    _cachedReadiness?.CompletionBpm,
                    _cachedReadiness?.Pitch,
                    out double seconds,
                    out string conversionError))
            {
                _endTailCanonicalSeconds = seconds;
                _endTailCanonicalSecondsValid = true;
                _endTailInputValid = true;
                _endTailInputError = null;
            }
            else
            {
                _endTailCanonicalSecondsValid = false;
                _endTailInputValid = false;
                _endTailInputError = conversionError;
            }
        }

        private static void SwitchEndTailUnit(EndTailUnit targetUnit)
        {
            if (targetUnit == _endTailDisplayedUnit)
                return;
            EnsureEndTailGuiState();
            if (!_endTailCanonicalSecondsValid)
            {
                // A Beats value cannot be converted equivalently when BPM/pitch
                // is unavailable. Still allow the player to escape that unit;
                // retain the numeric value (ceil once for Frames) and rebuild
                // conversion state under the newly selected unit.
                if (!TryParseEndTailValue(_endTailValueText, _endTailDisplayedUnit, out double fallbackValue))
                    fallbackValue = EndTailPolicy.DefaultValue;
                if (targetUnit == EndTailUnit.Frames)
                    fallbackValue = Math.Ceiling(fallbackValue);

                _endTailDisplayedUnit = targetUnit;
                _endTailValueText = FormatEndTailValue(fallbackValue, targetUnit);
                _endTailCanonicalSecondsValid = false;
                ApplyEndTailTextInput();
                return;
            }

            if (!EndTailPolicy.TryFromOutputSeconds(
                    _endTailCanonicalSeconds,
                    targetUnit,
                    Settings.EditorTargetFrameRate,
                    _cachedReadiness?.CompletionBpm,
                    _cachedReadiness?.Pitch,
                    out double converted,
                    out string error))
            {
                _endTailInputValid = false;
                _endTailInputError = error;
                return;
            }

            _endTailDisplayedUnit = targetUnit;
            Settings.EditorEndTailUnit = targetUnit;
            Settings.EditorEndTailValue = converted;
            _endTailValueText = FormatEndTailValue(converted, targetUnit);
            _endTailInputValid = true;
            _endTailInputError = null;
            _lastReadinessCacheRealtime = float.NegativeInfinity;

            // Frames is quantized once with ceil. Keep that exact duration as
            // the new canonical UI duration; later switches never parse the
            // rounded display text back into the conversion chain.
            if (targetUnit == EndTailUnit.Frames && Settings.EditorTargetFrameRate > 0)
                _endTailCanonicalSeconds = converted / Settings.EditorTargetFrameRate;
        }

        private static string BuildEndTailPreviewText()
        {
            if (!_endTailInputValid ||
                !TryParseEndTailValue(_endTailValueText, _endTailDisplayedUnit, out double value))
            {
                return EndTailErrorText(_endTailInputError);
            }

            var input = new EndTailInput(value, _endTailDisplayedUnit);
            int safetyFrameLimit = DeterministicFrameScheduler.NormalizeSafetyFrameLimit(
                Settings.EditorExportSafetyFrameLimit);
            if (!EndTailPolicy.TryResolve(
                    input,
                    Settings.EditorTargetFrameRate,
                    _cachedReadiness?.CompletionBpm,
                    _cachedReadiness?.Pitch,
                    safetyFrameLimit,
                    out EndTailResolution resolved,
                    out string error))
            {
                return EndTailErrorText(error);
            }

            string preview = resolved.FrameCount.ToString(CultureInfo.InvariantCulture) + " " +
                             UiText.GuiEndTailUnitFrames + " = " +
                             resolved.Seconds.ToString("0.######", CultureInfo.InvariantCulture) + " " +
                             UiText.GuiEndTailUnitSeconds;
            if (resolved.Beats.HasValue)
            {
                preview += " = " + resolved.Beats.Value.ToString("0.######", CultureInfo.InvariantCulture) +
                           " " + UiText.GuiEndTailUnitBeats;
            }
            else
            {
                preview += "；" + UiText.GuiEndTailPreviewUnavailable;
            }
            return preview;
        }

        private static string EndTailErrorText(string error)
        {
            if (string.Equals(error, "end-tail-exceeds-safety-limit", StringComparison.Ordinal))
                return UiText.GuiEndTailExceedsSafety;
            if (!string.IsNullOrEmpty(error) &&
                (error.Contains("bpm-unavailable") || error.Contains("pitch-unavailable")))
                return UiText.GuiEndTailDependenciesUnavailable;
            return UiText.GuiEndTailInvalid;
        }

        private static bool TryParseEndTailValue(string text, EndTailUnit unit, out double value)
        {
            const NumberStyles style = NumberStyles.Float;
            bool parsed = double.TryParse(text, style, CultureInfo.InvariantCulture, out value) ||
                          double.TryParse(text, style, CultureInfo.CurrentCulture, out value);
            if (!parsed || double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                return false;
            if (unit == EndTailUnit.Frames && Math.Abs(value - Math.Round(value)) > 1e-10)
                return false;
            return true;
        }

        private static string FormatEndTailValue(double value, EndTailUnit unit)
        {
            return unit == EndTailUnit.Frames
                ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        private static string UnitText(EndTailUnit unit)
        {
            switch (unit)
            {
                case EndTailUnit.Seconds: return UiText.GuiEndTailUnitSeconds;
                case EndTailUnit.Beats: return UiText.GuiEndTailUnitBeats;
                default: return UiText.GuiEndTailUnitFrames;
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
