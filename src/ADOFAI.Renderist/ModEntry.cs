using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using ADOFAI.Renderist.Capture;
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

                Log.Info("Loaded ADOFAI Renderist 0.2.1 (Phase 2.1 Chinese UI baseline).");
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
                GUILayout.Label("ADOFAI Renderist — Phase 2.1 Chinese UI baseline", GUI.skin.label);
                GUILayout.Space(6f);

                Settings.VerboseLogging = GUILayout.Toggle(
                    Settings.VerboseLogging,
                    UiText.GuiVerboseLoggingToggle);

                GUILayout.Space(8f);
                DrawCaptureGUI();
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnGUI failed", ex);
            }
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
                    CaptureService.StartSequence("gui");
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
                        else CaptureService.StartSequence("hotkey");
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
