using UnityEngine;
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

        // ---------------- Phase 2.0: screenshot sequence MVP ----------------

        /// <summary>
        /// Optional absolute output directory. Empty = use the default under
        /// Application.persistentDataPath. Validated and may be rejected; see
        /// Capture.OutputPath for the reject-list rules.
        /// </summary>
        public string OutputDirectory = string.Empty;

        /// <summary>Filename prefix for sequence frames, e.g. "frame_".</summary>
        public string FilenamePrefix = "frame_";

        /// <summary>Zero-pad width for the sequence index (e.g. 6 -> 000000).</summary>
        public int ZeroPadWidth = 6;

        /// <summary>Starting index for each new sequence session.</summary>
        public int SequenceStartIndex = 0;

        /// <summary>ScreenCapture superSize multiplier (1 = native resolution).</summary>
        public int CaptureSuperSize = 1;

        /// <summary>
        /// Capture once every N OnUpdate ticks. Values &lt;= 1 mean "every tick".
        /// Combined with TargetCaptureFps: the stricter limiter wins.
        /// </summary>
        public int CaptureEveryNFrames = 1;

        /// <summary>
        /// Real-time target capture FPS (uses Time.realtimeSinceStartup, NOT
        /// game time, NOT Time.timeScale, NOT any replay/autoplay clock).
        /// 0 = disabled.
        /// </summary>
        public float TargetCaptureFps = 0f;

        /// <summary>
        /// Hard cap on requested frames per session. 0 = unlimited.
        /// When reached, the sequence stops automatically with reason "max-frames".
        /// </summary>
        public int MaxFramesPerSession = 0;

        /// <summary>Enable F9/F10 hotkeys. GUI buttons remain available regardless.</summary>
        public bool HotkeysEnabled = true;

        /// <summary>Hotkey for a single-frame capture.</summary>
        public KeyCode SingleCaptureHotkey = KeyCode.F9;

        /// <summary>Hotkey to toggle the sequence on/off.</summary>
        public KeyCode SequenceHotkey = KeyCode.F10;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
