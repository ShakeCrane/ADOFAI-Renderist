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
    /// Phase 3.7.0 Custom Resolution &amp; Supersampling.
    /// Renderist remains passive towards replay / autoplay.
    /// </summary>
    public static class ModEntry
    {
        internal const string HarmonyId = "com.adofai.renderist";

        /// <summary>
        /// 当前 mod 版本。与 Info.json / csproj / 启动日志保持同步，
        /// 由 scripts/set-version.ps1 自动同步。
        /// </summary>
        internal const string ModVersion = "0.3.7.1";

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

        // Output FPS 编辑缓冲。Settings.EditorTargetFrameRate 仍是唯一配置来源；
        // 这里只保存正在输入的文本，非法输入不会写回 Settings。
        private static string _outputFpsText;
        private static bool _outputFpsInputValid = true;

        // 自定义分辨率宽高编辑缓冲。Settings.EditorCustomResolution* 仍是唯一配置来源；
        // 非法 / 越界输入不会写回 Settings，因此非法 persisted 值不会被 sanitize。
        private static string _customResolutionWidthText;
        private static string _customResolutionHeightText;
        // 超采样倍率编辑缓冲（Phase 3.7.0 第二闭环）。Settings.EditorSupersamplingScale
        // 仍是唯一配置来源；非法输入不会写回 Settings。
        private static string _supersamplingScaleText;
        private static bool _geometryInputValid = true;
        private static bool _supersamplingInputValid = true;

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
                ResetOutputFpsGuiState();
                ResetGeometryGuiState();
                ResetSupersamplingGuiState();

                modEntry.OnToggle = OnToggle;
                modEntry.OnGUI = OnGUI;
                modEntry.OnSaveGUI = OnSaveGUI;
                modEntry.OnUpdate = OnUpdate;

                Harmony = new Harmony(HarmonyId);

                Log.Info("Loaded ADOFAI Renderist 0.3.7.1 (Phase 3.7.0 Custom Resolution & Supersampling).");
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

                // 三台原生谱面 Camera 的当前 aspect（只读诊断）。实机验收需要记录
                // “导出前”与“导出 / 取消并调整窗口后”的实际值，因此直接显示在 GUI。
                GUILayout.Label(UiText.GuiEnvChartCameraAspectPrefix +
                    BuildChartCameraAspectText(report), GUI.skin.label);

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

        /// <summary>
        /// 三台原生谱面 Camera 的当前 aspect 文本（Bgcamstatic / BGcam / camobj）。
        /// 只读诊断：读取失败显示为不可用，绝不阻断导出。
        /// </summary>
        private static string BuildChartCameraAspectText(EditorExportReadinessReport report)
        {
            if (!string.IsNullOrEmpty(report.ChartCameraAspectError))
                return UiText.GuiEnvNotAvailable + "（" + report.ChartCameraAspectError + "）";

            return FormatAspect(report.ChartCameraBgcamstaticAspect) + " / " +
                   FormatAspect(report.ChartCameraBgcamAspect) + " / " +
                   FormatAspect(report.ChartCameraCamobjAspect);
        }

        private static string FormatAspect(float? aspect)
        {
            return aspect.HasValue
                ? aspect.Value.ToString("0.######", CultureInfo.InvariantCulture)
                : UiText.GuiEnvNotAvailable;
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
                    case EditorExportReadinessReason.InvalidOutputGeometry:
                        return UiText.GuiReadinessInvalidOutputGeometry;
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

            DrawOutputFpsGui();
            DrawEndTailGui();
            DrawImageOutputGui();
            DrawGeometryGui();

            EditorExportSession session = EditorExportController.CurrentSession;
            GUILayout.Label(UiText.GuiMasterTimelineHandoffStatusPrefix +
                EditorExportController.CurrentState.ToString(), GUI.skin.label);
            if (session != null)
            {
                // 已提交帧 = 逻辑 commit 数（log-only 模式下 PNG 写盘数为 0，不能用它表示进度）。
                GUILayout.Label(UiText.GuiMasterTimelineHandoffFramesPrefix +
                    session.LogicalFrameCount.ToString(CultureInfo.InvariantCulture), GUI.skin.label);
                GUILayout.Label(session.SafetyFrameLimit.HasValue
                    ? UiText.Format(UiText.GuiMasterTimelineHandoffSafetyFormat,
                        session.SafetyFrameLimit.Value.ToString(CultureInfo.InvariantCulture),
                        (session.SafetyDurationSeconds ?? 0.0).ToString("0.###", CultureInfo.InvariantCulture))
                    : UiText.GuiMasterTimelineHandoffSafetyUnbounded,
                    GUI.skin.label);
                // 视觉尾帧同样按逻辑提交数显示（PNG 与 log-only 的 completion 判定一致）。
                GUILayout.Label(UiText.GuiMasterTimelineHandoffTailPrefix +
                    session.TailFramesCommitted.ToString(CultureInfo.InvariantCulture) + "/" +
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

        /// <summary>
        /// Output FPS 输入框。唯一配置来源仍是 Settings.EditorTargetFrameRate
        /// （不新增第二套 FPS 配置）；本方法只提供编辑入口。
        /// 只接受正整数：空值 / 非法 / &lt;=0 一律不写入 Settings，实际值保持原样。
        /// session 进行中（EditorExportController.IsBusy）禁用编辑；Start 时仍由
        /// 既有 session / preflight 冻结与校验。
        /// </summary>
        private static void DrawOutputFpsGui()
        {
            EnsureOutputFpsGuiState();

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !EditorExportController.IsBusy;

            GUILayout.BeginHorizontal();
            GUILayout.Label(UiText.GuiOutputFpsLabel, GUI.skin.label, GUILayout.Width(82f));
            string changedText = GUILayout.TextField(_outputFpsText ?? string.Empty, GUILayout.Width(110f));
            if (!string.Equals(changedText, _outputFpsText, StringComparison.Ordinal))
            {
                _outputFpsText = changedText;
                ApplyOutputFpsTextInput();
            }

            // 始终显示当前真正生效的值：输入非法时它不会与输入框内容一致。
            GUILayout.Label(UiText.GuiOutputFpsEffectivePrefix +
                Settings.EditorTargetFrameRate.ToString(CultureInfo.InvariantCulture), GUI.skin.label);
            GUILayout.EndHorizontal();

            GUI.enabled = previousEnabled;

            if (!_outputFpsInputValid)
                GUILayout.Label(UiText.GuiOutputFpsInvalid, GUI.skin.label);
        }

        private static void ResetOutputFpsGuiState()
        {
            _outputFpsText = Settings.EditorTargetFrameRate.ToString(CultureInfo.InvariantCulture);
            _outputFpsInputValid = true;
        }

        private static void EnsureOutputFpsGuiState()
        {
            if (_outputFpsText == null)
                ResetOutputFpsGuiState();
        }

        private static void ApplyOutputFpsTextInput()
        {
            // 与 preflight / scheduler gate 共用同一范围规则（OutputFpsPolicy）。
            if (!OutputFpsPolicy.TryParse(_outputFpsText, out int parsed, out _))
            {
                _outputFpsInputValid = false;
                return; // 非法或越界输入：不写 Settings。
            }

            _outputFpsInputValid = true;

            if (parsed == Settings.EditorTargetFrameRate)
                return;

            Settings.EditorTargetFrameRate = parsed;

            // 与其它影响 preflight 的 GUI 配置保持一致：使 readiness 缓存失效。
            _lastReadinessCacheRealtime = float.NegativeInfinity;

            // End Tail 的 Beats / Seconds 换算依赖 outputFps，强制重新换算，
            // 避免沿用旧的 canonical seconds。
            _endTailCanonicalSecondsValid = false;
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

        /// <summary>
        /// 「输出 PNG 图像」开关（默认开）。关闭 = log-only：帧事务与时间推进不变，
        /// 只是不写 PNG 文件（仍写 metadata.json）。
        ///
        /// 模式在 session 开始时由 scheduler 一次性冻结；本开关只写 persisted Settings，
        /// 因此运行中修改它不会影响当前 session。与 Output FPS / End Tail 一致，
        /// session 进行中禁止编辑，避免用户误以为当前 session 已被改变。
        /// 不绑定 VerboseLogging。
        /// </summary>
        private static void DrawImageOutputGui()
        {
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !EditorExportController.IsBusy;

            bool newValue = GUILayout.Toggle(
                Settings.EditorImageOutputEnabled,
                UiText.GuiImageOutputToggle);
            if (newValue != Settings.EditorImageOutputEnabled)
            {
                Settings.EditorImageOutputEnabled = newValue;
            }

            GUI.enabled = previousEnabled;

            if (!Settings.EditorImageOutputEnabled)
            {
                GUILayout.Label(UiText.GuiImageOutputDisabledHint, GUI.skin.label);
            }
        }

        /// <summary>
        /// 「自定义输出分辨率」开关 + 宽高输入（Phase 3.7.0）。
        ///
        /// 唯一配置来源是 Settings.EditorCustomResolution*；本方法只提供编辑入口。
        /// 只接受正整数且不超过真实硬件上限：空值 / 非法 / 越界一律不写入 Settings，
        /// 因此**非法 persisted 值保持非法并 fail-closed**，不会被 sanitize 成默认值。
        ///
        /// 与 Output FPS / End Tail / 图像输出一致：session 进行中禁止编辑；真正的冻结与
        /// 校验由 preflight 与 scheduler 在 Start 时完成。
        /// </summary>
        private static void DrawGeometryGui()
        {
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !EditorExportController.IsBusy;

            bool newValue = GUILayout.Toggle(
                Settings.EditorCustomResolutionEnabled,
                UiText.GuiCustomResolutionToggle);
            if (newValue != Settings.EditorCustomResolutionEnabled)
            {
                Settings.EditorCustomResolutionEnabled = newValue;
                // 影响 preflight：使 readiness 缓存失效。
                _lastReadinessCacheRealtime = float.NegativeInfinity;
            }

            GUI.enabled = previousEnabled;

            EnsureGeometryGuiState();

            // 超采样倍率与自定义分辨率开关**互相独立**（legacy-window 模式下同样生效），
            // 因此该输入行在自定义分辨率关闭时也必须可见、可编辑。
            bool scaleEnabled = GUI.enabled;
            GUI.enabled = scaleEnabled && !EditorExportController.IsBusy;

            GUILayout.BeginHorizontal();
            GUILayout.Label(UiText.GuiSupersamplingScaleLabel, GUI.skin.label, GUILayout.Width(82f));
            string changedScale = GUILayout.TextField(
                _supersamplingScaleText ?? string.Empty, GUILayout.Width(110f));
            if (!string.Equals(changedScale, _supersamplingScaleText, StringComparison.Ordinal))
            {
                _supersamplingScaleText = changedScale;
                ApplySupersamplingTextInput();
            }
            GUILayout.EndHorizontal();

            // 始终显示当前真正生效的 persisted 倍率：输入非法时它与输入框内容不一致。
            GUILayout.Label(UiText.GuiSupersamplingEffectivePrefix +
                Settings.EditorSupersamplingScale.ToString(CultureInfo.InvariantCulture),
                GUI.skin.label);
            GUILayout.Label(
                _supersamplingInputValid ? UiText.GuiSupersamplingHint : UiText.GuiSupersamplingInvalid,
                GUI.skin.label);

            GUI.enabled = scaleEnabled;

            if (!Settings.EditorCustomResolutionEnabled)
            {
                GUILayout.Label(UiText.GuiCustomResolutionDisabledHint, GUI.skin.label);
                return;
            }

            bool inputEnabled = GUI.enabled;
            GUI.enabled = inputEnabled && !EditorExportController.IsBusy;

            GUILayout.BeginHorizontal();
            GUILayout.Label(UiText.GuiCustomResolutionWidthLabel, GUI.skin.label, GUILayout.Width(82f));
            string changedWidth = GUILayout.TextField(
                _customResolutionWidthText ?? string.Empty, GUILayout.Width(110f));
            if (!string.Equals(changedWidth, _customResolutionWidthText, StringComparison.Ordinal))
            {
                _customResolutionWidthText = changedWidth;
                ApplyGeometryTextInput();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(UiText.GuiCustomResolutionHeightLabel, GUI.skin.label, GUILayout.Width(82f));
            string changedHeight = GUILayout.TextField(
                _customResolutionHeightText ?? string.Empty, GUILayout.Width(110f));
            if (!string.Equals(changedHeight, _customResolutionHeightText, StringComparison.Ordinal))
            {
                _customResolutionHeightText = changedHeight;
                ApplyGeometryTextInput();
            }
            GUILayout.EndHorizontal();

            // 始终显示当前真正生效的 persisted 值：输入非法时它不会与输入框内容一致。
            GUILayout.Label(UiText.GuiCustomResolutionEffectivePrefix +
                Settings.EditorCustomResolutionWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                Settings.EditorCustomResolutionHeight.ToString(CultureInfo.InvariantCulture),
                GUI.skin.label);

            GUI.enabled = inputEnabled;

            GUILayout.Label(
                _geometryInputValid ? UiText.GuiCustomResolutionHint : UiText.GuiCustomResolutionInvalid,
                GUI.skin.label);
        }

        /// <summary>
        /// 从 persisted Settings 重建几何 GUI 视图。**不 sanitize、不写回 Settings**：
        /// 非法 persisted 值如实显示（含 0 / 负数），并由 preflight fail-closed 阻断导出，
        /// 直到用户显式输入合法值。
        /// </summary>
        private static void ResetGeometryGuiState()
        {
            _customResolutionWidthText =
                Settings.EditorCustomResolutionWidth.ToString(CultureInfo.InvariantCulture);
            _customResolutionHeightText =
                Settings.EditorCustomResolutionHeight.ToString(CultureInfo.InvariantCulture);
            _geometryInputValid = ValidateGeometryTexts(out _, out _);
        }

        private static void EnsureGeometryGuiState()
        {
            if (_customResolutionWidthText == null || _customResolutionHeightText == null)
                ResetGeometryGuiState();
            if (_supersamplingScaleText == null)
                ResetSupersamplingGuiState();
        }

        /// <summary>
        /// 从 persisted Settings 重建超采样倍率 GUI 视图。**不 sanitize、不写回**：
        /// 非法 persisted 值（含 0 / 负数）如实显示，并由 preflight fail-closed 阻断导出。
        /// </summary>
        private static void ResetSupersamplingGuiState()
        {
            _supersamplingScaleText =
                Settings.EditorSupersamplingScale.ToString(CultureInfo.InvariantCulture);
            _supersamplingInputValid = ValidateSupersamplingText(out _);
        }

        private static void ApplySupersamplingTextInput()
        {
            // 与 preflight / scheduler 共用同一范围规则（OutputGeometryPolicy）。
            if (!ValidateSupersamplingText(out int scale))
            {
                _supersamplingInputValid = false;
                return; // 非法输入：不写 Settings。
            }

            _supersamplingInputValid = true;

            if (scale == Settings.EditorSupersamplingScale)
                return;

            Settings.EditorSupersamplingScale = scale;
            _lastReadinessCacheRealtime = float.NegativeInfinity;
        }

        /// <summary>
        /// 超采样倍率输入必须通过 OutputGeometryPolicy 的同一条规则（≥ 1 的正整数）。
        /// 渲染尺寸的硬件上限由 preflight / scheduler 在 session 开始时统一判定。
        /// </summary>
        private static bool ValidateSupersamplingText(out int scale)
        {
            scale = Settings.EditorSupersamplingScale;
            if (!OutputGeometryPolicy.TryParseSupersamplingScale(
                    _supersamplingScaleText, out int parsedScale, out _))
            {
                return false;
            }

            scale = parsedScale;
            return true;
        }

        private static void ApplyGeometryTextInput()
        {
            // 与 preflight / scheduler 共用同一范围规则（OutputGeometryPolicy）。
            if (!ValidateGeometryTexts(out int width, out int height))
            {
                _geometryInputValid = false;
                return; // 非法或越界输入：不写 Settings。
            }

            _geometryInputValid = true;

            if (width == Settings.EditorCustomResolutionWidth &&
                height == Settings.EditorCustomResolutionHeight)
            {
                return;
            }

            // 宽高作为一组写回：绝不留下"只更新了一半"的 persisted 组合。
            Settings.EditorCustomResolutionWidth = width;
            Settings.EditorCustomResolutionHeight = height;
            _lastReadinessCacheRealtime = float.NegativeInfinity;
        }

        /// <summary>
        /// 两个输入框都必须通过 OutputGeometryPolicy 的同一条规则（正整数 + 真实硬件上限）。
        /// 只有两者都合法时才返回 true，并由调用方一次性写回 Settings。
        /// </summary>
        private static bool ValidateGeometryTexts(out int width, out int height)
        {
            width = Settings.EditorCustomResolutionWidth;
            height = Settings.EditorCustomResolutionHeight;

            if (!OutputGeometryPolicy.TryParseDimension(
                    _customResolutionWidthText, "width", out int parsedWidth, out _))
            {
                return false;
            }
            if (!OutputGeometryPolicy.TryParseDimension(
                    _customResolutionHeightText, "height", out int parsedHeight, out _))
            {
                return false;
            }

            width = parsedWidth;
            height = parsedHeight;
            return true;
        }

        /// <summary>
        /// Rebuild the End Tail GUI view from persisted Settings only.
        ///
        /// Persisted state contract: this method must NEVER write Settings.EditorEndTailValue
        /// or Settings.EditorEndTailUnit, and must never sanitize (no default substitution, no
        /// clamp to 0, no ceil of fractional Frames). An invalid persisted value therefore stays
        /// invalid and fail-closed until the user explicitly edits it through
        /// <see cref="ApplyEndTailTextInput"/>.
        /// </summary>
        private static void ResetEndTailGuiState()
        {
            _endTailDisplayedUnit = Settings.EditorEndTailUnit;
            _endTailValueText = FormatPersistedEndTailValue(Settings.EditorEndTailValue);
            _endTailCanonicalSeconds = 0.0;
            _endTailCanonicalSecondsValid = false;
            _endTailInputValid = false;
            _endTailInputError = null;
            _endTailUnitMenuOpen = false;

            // Cheap business validation only (no BPM/pitch dependency); the conversion /
            // dependency-dependent validation is re-run by EnsureEndTailGuiState once
            // readiness is cached, so a valid persisted Beats value is not falsely blocked.
            if (!TryParseEndTailValue(_endTailValueText, _endTailDisplayedUnit, out double value))
            {
                _endTailInputError = "end-tail-persisted-invalid";
                return;
            }

            var persistedInput = new EndTailInput(value, _endTailDisplayedUnit);
            if (!EndTailPolicy.TryValidateInput(persistedInput, out string persistedError))
            {
                _endTailInputError = string.IsNullOrEmpty(persistedError)
                    ? "end-tail-persisted-invalid"
                    : persistedError;
                return;
            }

            _endTailInputValid = true;
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
                // Distinguish "not a valid End Tail value" from "valid value whose Beats /
                // Frames conversion needs BPM/pitch that is not available yet".
                if (!TryParseEndTailValue(_endTailValueText, _endTailDisplayedUnit, out double currentValue) ||
                    !EndTailPolicy.TryValidateInput(
                        new EndTailInput(currentValue, _endTailDisplayedUnit), out _))
                {
                    // Persisted/GUI input is invalid: switching the displayed unit must not
                    // launder it into a default, and must not write Settings. Keep the invalid
                    // state fail-closed until the user explicitly types a valid value.
                    _endTailInputValid = false;
                    _endTailInputError = "end-tail-persisted-invalid";
                    _lastReadinessCacheRealtime = float.NegativeInfinity;
                    return;
                }

                // Valid value that simply cannot be converted yet (deps unavailable): allow
                // escaping the unit for display/edit only. Re-validation and any Settings
                // update happen on the next draw or through an explicit user edit.
                _endTailDisplayedUnit = targetUnit;
                _endTailCanonicalSecondsValid = false;
                _lastReadinessCacheRealtime = float.NegativeInfinity;
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
            if (targetUnit == EndTailUnit.Frames && OutputFpsPolicy.IsValid(Settings.EditorTargetFrameRate))
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
            // safety 默认未配置（unbounded）；只有显式正整数才是真实上限。
            // 与 preflight / scheduler 共用同一解析，0 = 无上限。
            long safetyFrameLimit = SafetyFrameLimitPolicy.Resolve(
                Settings.EditorExportSafetyFrameLimit).FrameLimit;
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

        /// <summary>
        /// Lossless display of a persisted End Tail value for the GUI view: never rounds,
        /// ceils, clamps or substitutes a default, so an invalid persisted value stays
        /// visible exactly as stored (including NaN / Infinity / negative / fractional).
        /// </summary>
        private static string FormatPersistedEndTailValue(double value)
        {
            if (double.IsNaN(value)) return "NaN";
            if (double.IsPositiveInfinity(value)) return "Infinity";
            if (double.IsNegativeInfinity(value)) return "-Infinity";
            return value.ToString("R", CultureInfo.InvariantCulture);
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
