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

                Log.Info("Loaded ADOFAI Renderist 0.2.0 (Phase 2.0 screenshot sequence MVP).");
                Log.Warn("Realtime PNG writes may reduce frame rate and consume disk space. Keep sequences short.");
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
                    Log.Info("Enabled.");
                }
                else
                {
                    if (CaptureService.IsRecording)
                    {
                        CaptureService.StopSequence("disabled");
                    }
                    // Always safe to call even when no patches are registered.
                    Harmony?.UnpatchAll(HarmonyId);
                    Log.Info("Disabled. Harmony patches (if any) removed.");
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
                GUILayout.Label("ADOFAI Renderist — Phase 2.0 screenshot sequence MVP", GUI.skin.label);
                GUILayout.Space(6f);

                Settings.VerboseLogging = GUILayout.Toggle(
                    Settings.VerboseLogging,
                    " Verbose logging");

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
            string status = CaptureService.IsRecording ? "RECORDING" : "idle";
            int requested = CaptureService.FramesRequestedInSession;
            string dir = string.IsNullOrEmpty(CaptureService.CurrentSessionDirectory)
                ? "(none yet)"
                : CaptureService.CurrentSessionDirectory;

            GUILayout.Label("Capture status: " + status, GUI.skin.label);
            GUILayout.Label("Frames requested (this session): " +
                requested.ToString(CultureInfo.InvariantCulture), GUI.skin.label);
            GUILayout.Label("Session directory: " + dir, GUI.skin.label);
            GUILayout.Label("Throttle: everyN=" +
                Settings.CaptureEveryNFrames.ToString(CultureInfo.InvariantCulture) +
                ", targetFps=" + Settings.TargetCaptureFps.ToString("0.###", CultureInfo.InvariantCulture) +
                ", maxFrames=" + Settings.MaxFramesPerSession.ToString(CultureInfo.InvariantCulture) +
                ", superSize=" + Settings.CaptureSuperSize.ToString(CultureInfo.InvariantCulture),
                GUI.skin.label);

            GUILayout.Space(4f);
            GUILayout.Label("Note: GUI capture may include the UMM panel. For clean shots, fold UMM and use hotkeys.", GUI.skin.label);

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Capture single frame (next tick)"))
            {
                CaptureService.RequestSingleCaptureNextTick();
            }
            if (!CaptureService.IsRecording)
            {
                if (GUILayout.Button("Start sequence"))
                {
                    CaptureService.StartSequence("gui");
                }
            }
            else
            {
                if (GUILayout.Button("Stop sequence"))
                {
                    CaptureService.StopSequence("user");
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            Settings.HotkeysEnabled = GUILayout.Toggle(
                Settings.HotkeysEnabled,
                " Hotkeys enabled (single=" + Settings.SingleCaptureHotkey +
                ", sequence=" + Settings.SequenceHotkey + ")");
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
