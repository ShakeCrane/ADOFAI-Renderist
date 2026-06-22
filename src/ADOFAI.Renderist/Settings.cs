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

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
