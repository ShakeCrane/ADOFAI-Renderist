using UnityModManagerNet;
using ADOFAI.Renderist.Export;

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
        /// Export.OutputPath for the reject-list rules.
        /// </summary>
        public string OutputDirectory = string.Empty;

        // ---------------- Phase 3.4.0: deterministic editor export ----------------

        /// <summary>
        /// Switch for the deterministic editor export session (MasterTimeline).
        /// </summary>
        public bool EditorExportEnabled = false;

        /// <summary>
        /// Target output frame rate for the deterministic editor export session.
        /// Must be greater than 0 to pass readiness checks.
        /// </summary>
        public int EditorTargetFrameRate = 60;

        /// <summary>
        /// Single player-facing End Tail value. Its interpretation is selected by
        /// EditorEndTailUnit and frozen when a session starts.
        /// </summary>
        public double EditorEndTailValue = EndTailPolicy.DefaultValue;

        /// <summary>Unit for EditorEndTailValue: Frames, Seconds, or Beats.</summary>
        public EndTailUnit EditorEndTailUnit = EndTailPolicy.DefaultUnit;

        /// <summary>
        /// Safety-only maximum number of output frames for a session that never
        /// reaches native completion. Zero uses the built-in safe default.
        /// This is not a normal completion condition.
        /// </summary>
        public int EditorExportSafetyFrameLimit = 36000;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
