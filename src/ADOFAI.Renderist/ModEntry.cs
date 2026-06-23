using System;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist
{
    /// <summary>
    /// Unity Mod Manager entry point for ADOFAI Renderist.
    /// Phase 1 scope: load / unload / log / settings / Harmony instance only.
    /// No game-method patches are registered in this phase.
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

                // Instantiate Harmony but do NOT PatchAll in Phase 1.
                Harmony = new Harmony(HarmonyId);

                Log.Info("Loaded ADOFAI Renderist 0.1.3 (Phase 1.3 local reference baseline).");
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
                    // Phase 1: nothing to patch yet. Reserved for Phase 3+.
                    Log.Info("Enabled.");
                }
                else
                {
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
                GUILayout.Label("ADOFAI Renderist — Phase 1.3 local reference baseline", GUI.skin.label);
                GUILayout.Space(6f);

                Settings.VerboseLogging = GUILayout.Toggle(
                    Settings.VerboseLogging,
                    " Verbose logging");
            }
            catch (Exception ex)
            {
                Logger?.LogException("OnGUI failed", ex);
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
    }
}
