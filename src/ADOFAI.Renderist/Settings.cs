using UnityModManagerNet;

namespace ADOFAI.Renderist
{
    /// <summary>
    /// Persistent settings for ADOFAI Renderist.
    /// Stored by UMM as a sibling XML file inside the mod folder.
    /// </summary>
    public class Settings : UnityModManager.ModSettings
    {
        /// <summary>
        /// When true, the Log helper also emits debug-level messages.
        /// </summary>
        public bool VerboseLogging = false;

        /// <summary>
        /// Optional absolute output directory. Empty = use the default under
        /// Application.persistentDataPath. Validated and may be rejected; see
        /// Capture.OutputPath for the reject-list rules.
        /// </summary>
        public string OutputDirectory = string.Empty;

        // ---------------- Phase 3.3.0: deterministic editor export ----------------

        /// <summary>
        /// Switch for the deterministic editor export session (MasterTimeline).
        /// </summary>
        public bool EditorExportEnabled = false;

        /// <summary>
        /// Target output frame rate for the deterministic editor export session.
        /// Must be greater than 0 to pass readiness checks.
        /// </summary>
        public int EditorTargetFrameRate = 60;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
