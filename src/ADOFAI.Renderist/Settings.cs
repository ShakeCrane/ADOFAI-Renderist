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
        /// 取值范围与默认值由 OutputFpsPolicy 单点定义。
        /// </summary>
        public int EditorTargetFrameRate = OutputFpsPolicy.Default;

        /// <summary>
        /// Single player-facing End Tail value. Its interpretation is selected by
        /// EditorEndTailUnit and frozen when a session starts.
        /// </summary>
        public double EditorEndTailValue = EndTailPolicy.DefaultValue;

        /// <summary>Unit for EditorEndTailValue: Frames, Seconds, or Beats.</summary>
        public EndTailUnit EditorEndTailUnit = EndTailPolicy.DefaultUnit;

        /// <summary>
        /// Optional safety-only output-frame limit for a session that never reaches
        /// native completion. 0 (default) and the historical default 36000 both mean
        /// "not configured": no total frame-count and no total-duration limit exists.
        /// Any other positive value is used verbatim as an output-frame limit and is
        /// never capped by the product layer (int.MaxValue is only the data-type
        /// boundary of this field, not a recommended or supported performance target).
        ///
        /// Known ambiguity, deliberately accepted: safety never had a GUI, so a 36000
        /// written by UMM is the historical default, but a hand-edited 36000 is
        /// indistinguishable from it. Both are intentionally migrated to "not
        /// configured" (unbounded); use any other positive value to opt in explicitly.
        ///
        /// This is not a normal completion condition.
        /// </summary>
        public int EditorExportSafetyFrameLimit = 0;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
