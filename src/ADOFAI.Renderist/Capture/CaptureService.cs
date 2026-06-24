using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Capture
{
    /// <summary>
    /// Phase 2.0 capture core.
    ///
    /// Design constraints (locked):
    ///   * Renderist is passive: it never starts, pauses, resumes, or inspects
    ///     replay / autoplay. The user drives sequences manually.
    ///   * ScreenCapture.CaptureScreenshot has no completion callback. We
    ///     never record "framesWritten" — only "framesRequested" — and on stop
    ///     we do a best-effort directory scan to populate "filesDetected".
    ///   * FPS throttling uses Time.realtimeSinceStartup, NOT game time and
    ///     NOT Time.timeScale.
    ///   * GUI-triggered single captures delay one frame so the button release
    ///     can repaint, but we never touch UMM panel visibility.
    /// </summary>
    internal static class CaptureService
    {
        private const string SingleFilePrefix = "single_";
        private const string SequenceMetadataName = "metadata.json";

        public static bool IsRecording { get; private set; }
        public static int FramesRequestedInSession { get; private set; }
        public static string CurrentSessionDirectory { get; private set; }

        // 单帧域：本次运行内只读状态（不持久化、不写入 metadata）。
        // 与 FramesRequestedInSession 完全分离：F9 / 单帧按钮路径仅更新这一组字段。
        public static int SingleFrameCaptureCountThisRun { get; private set; }
        public static string LastSingleCaptureDirectory { get; private set; }
        public static string LastSingleCaptureFile { get; private set; }
        public static DateTime? LastSingleCaptureTimeLocal { get; private set; }

        private static int _everyNCounter;
        private static float _lastCaptureRealtime;
        private static int _nextSequenceIndex;
        private static Metadata _sessionMetadata;
        private static int _guiSingleDelayTicks;

        // ---------------- single ----------------

        /// <summary>
        /// Request a single screenshot immediately. Safe to call from a hotkey path.
        /// </summary>
        public static void RequestSingleCaptureNow()
        {
            Settings settings = ModEntry.Settings;
            if (settings == null) return;

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string dir = OutputPath.ResolveSessionDirectory(settings.OutputDirectory, "single_" + stamp);
            if (string.IsNullOrEmpty(dir))
            {
                Log.Error(UiText.LogSingleAbortedNoDir);
                return;
            }

            string fileStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string filename = SingleFilePrefix + fileStamp + ".png";
            string filePath = Path.Combine(dir, filename);

            int superSize = NormalizeSuperSize(settings.CaptureSuperSize);

            try
            {
                ScreenCapture.CaptureScreenshot(filePath, superSize);
                Log.Info(UiText.Format(UiText.LogSingleRequestedFormat, filePath, superSize));
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.Format(UiText.LogExScreenCaptureThrewFormat, filePath), ex);
                return;
            }

            // GPU 请求已发出（未抛异常）才更新单帧状态，与序列 EmitSequenceFrame 的 honesty 一致。
            // 仅记录"已请求"，不代表 PNG 已落盘——Unity 无完成回调。
            SingleFrameCaptureCountThisRun++;
            LastSingleCaptureDirectory = dir;
            LastSingleCaptureFile = filename;
            LastSingleCaptureTimeLocal = DateTime.Now;

            WriteSingleMetadata(dir, filename, superSize);
        }

        /// <summary>
        /// Request a single capture but delay one OnUpdate tick so the GUI
        /// button release can repaint. We still cannot guarantee that any UMM
        /// panel is gone — the user must fold it themselves.
        /// </summary>
        public static void RequestSingleCaptureNextTick()
        {
            Log.Info(UiText.LogSingleQueuedNextTick);
            _guiSingleDelayTicks = 1;
        }

        // ---------------- sequence ----------------

        public static void StartSequence(string trigger)
        {
            if (IsRecording)
            {
                Log.Warn(UiText.LogSequenceStartIgnoredAlreadyRunning);
                return;
            }

            Settings settings = ModEntry.Settings;
            if (settings == null) return;

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string dir = OutputPath.ResolveSessionDirectory(settings.OutputDirectory, "seq_" + stamp);
            if (string.IsNullOrEmpty(dir))
            {
                Log.Error(UiText.LogSequenceAbortedNoDir);
                return;
            }

            int superSize = NormalizeSuperSize(settings.CaptureSuperSize);
            int everyN = settings.CaptureEveryNFrames < 1 ? 1 : settings.CaptureEveryNFrames;
            float fps = settings.TargetCaptureFps < 0f ? 0f : settings.TargetCaptureFps;
            int maxFrames = settings.MaxFramesPerSession < 0 ? 0 : settings.MaxFramesPerSession;
            int padWidth = settings.ZeroPadWidth < 1 ? 1 : settings.ZeroPadWidth;
            int startIndex = settings.SequenceStartIndex < 0 ? 0 : settings.SequenceStartIndex;
            string prefix = string.IsNullOrEmpty(settings.FilenamePrefix) ? "frame_" : settings.FilenamePrefix;

            CurrentSessionDirectory = dir;
            FramesRequestedInSession = 0;
            _nextSequenceIndex = startIndex;
            _everyNCounter = 0;
            _lastCaptureRealtime = float.NegativeInfinity;
            IsRecording = true;

            _sessionMetadata = new Metadata
            {
                Version             = "0.2.1",
                Phase               = "Phase 2.0 screenshot sequence MVP",
                CreatedAtUtc        = Metadata.IsoUtcNow(),
                EndedAtUtc          = null,
                Mode                = "sequence",
                Prefix              = prefix,
                ZeroPadWidth        = padWidth,
                StartIndex          = startIndex,
                SuperSize           = superSize,
                CaptureEveryNFrames = everyN,
                TargetCaptureFps    = fps,
                MaxFramesPerSession = maxFrames,
                FramesRequested     = 0,
                FilesDetected       = null,
                StopReason          = null,
            };
            _sessionMetadata.Write(Path.Combine(dir, SequenceMetadataName));

            Log.Info(UiText.Format(UiText.LogSequenceStartFormat, trigger ?? "?", dir));
            Log.Info(UiText.Format(UiText.LogSequenceStartParamsFormat,
                superSize.ToString(CultureInfo.InvariantCulture),
                everyN.ToString(CultureInfo.InvariantCulture),
                fps.ToString("0.###", CultureInfo.InvariantCulture),
                maxFrames.ToString(CultureInfo.InvariantCulture)));
            Log.Warn(UiText.LogSequencePerfWarn);
        }

        public static void StopSequence(string reason)
        {
            if (!IsRecording) return;
            IsRecording = false;

            if (_sessionMetadata != null)
            {
                _sessionMetadata.EndedAtUtc = Metadata.IsoUtcNow();
                _sessionMetadata.FramesRequested = FramesRequestedInSession;
                _sessionMetadata.StopReason = reason ?? "user";
                _sessionMetadata.FilesDetected = TryCountPngs(CurrentSessionDirectory, _sessionMetadata.Prefix);
                if (!string.IsNullOrEmpty(CurrentSessionDirectory))
                {
                    _sessionMetadata.Write(Path.Combine(CurrentSessionDirectory, SequenceMetadataName));
                }
            }

            int detected = _sessionMetadata != null && _sessionMetadata.FilesDetected.HasValue
                ? _sessionMetadata.FilesDetected.Value
                : -1;
            Log.Info(UiText.Format(UiText.LogSequenceStopFormat,
                reason ?? "user",
                FramesRequestedInSession.ToString(CultureInfo.InvariantCulture),
                detected >= 0 ? detected.ToString(CultureInfo.InvariantCulture) : "n/a",
                CurrentSessionDirectory));
        }

        // ---------------- tick driver ----------------

        /// <summary>
        /// Called from ModEntry.OnUpdate every game tick. Drives throttling,
        /// sequence emission, and the one-tick GUI single delay.
        /// </summary>
        public static void Tick()
        {
            // Drain the GUI single-capture one-tick delay first.
            if (_guiSingleDelayTicks > 0)
            {
                _guiSingleDelayTicks--;
                if (_guiSingleDelayTicks == 0)
                {
                    RequestSingleCaptureNow();
                }
            }

            if (!IsRecording) return;

            Settings settings = ModEntry.Settings;
            if (settings == null) return;

            // Throttle 1: every-N-frames.
            int everyN = settings.CaptureEveryNFrames < 1 ? 1 : settings.CaptureEveryNFrames;
            _everyNCounter++;
            if (_everyNCounter < everyN) return;

            // Throttle 2: realtime target fps.
            float fps = settings.TargetCaptureFps;
            if (fps > 0f)
            {
                float minInterval = 1f / fps;
                float now = Time.realtimeSinceStartup;
                if (_lastCaptureRealtime != float.NegativeInfinity &&
                    (now - _lastCaptureRealtime) < minInterval)
                {
                    return;
                }
            }

            // Throttle 3: hard session cap.
            int maxFrames = settings.MaxFramesPerSession;
            if (maxFrames > 0 && FramesRequestedInSession >= maxFrames)
            {
                StopSequence("max-frames");
                return;
            }

            EmitSequenceFrame(settings);

            _everyNCounter = 0;
            _lastCaptureRealtime = Time.realtimeSinceStartup;
        }

        private static void EmitSequenceFrame(Settings settings)
        {
            if (string.IsNullOrEmpty(CurrentSessionDirectory)) return;

            int padWidth = settings.ZeroPadWidth < 1 ? 1 : settings.ZeroPadWidth;
            string prefix = string.IsNullOrEmpty(settings.FilenamePrefix) ? "frame_" : settings.FilenamePrefix;
            int superSize = NormalizeSuperSize(settings.CaptureSuperSize);

            string indexText = _nextSequenceIndex
                .ToString(CultureInfo.InvariantCulture)
                .PadLeft(padWidth, '0');
            string filename = prefix + indexText + ".png";
            string filePath = Path.Combine(CurrentSessionDirectory, filename);

            try
            {
                ScreenCapture.CaptureScreenshot(filePath, superSize);
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.Format(UiText.LogExScreenCaptureThrewFormat, filePath), ex);
                return;
            }

            FramesRequestedInSession++;
            _nextSequenceIndex++;
            Log.Debug("Frame requested #" + FramesRequestedInSession.ToString(CultureInfo.InvariantCulture) +
                      " -> " + filename);
        }

        // ---------------- helpers ----------------

        private static int NormalizeSuperSize(int superSize)
        {
            if (superSize < 1) return 1;
            if (superSize > 8) return 8;
            return superSize;
        }

        private static int? TryCountPngs(string dir, string prefix)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                string pattern = (string.IsNullOrEmpty(prefix) ? "" : prefix) + "*.png";
                string[] files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
                return files.Length;
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.Format(UiText.LogExDirectoryScanFailedFormat, dir), ex);
                return null;
            }
        }

        private static void WriteSingleMetadata(string dir, string filename, int superSize)
        {
            try
            {
                Settings settings = ModEntry.Settings;
                if (settings == null) return;

                var m = new Metadata
                {
                    Version             = "0.2.1",
                    Phase               = "Phase 2.0 screenshot sequence MVP",
                    CreatedAtUtc        = Metadata.IsoUtcNow(),
                    EndedAtUtc          = null,
                    Mode                = "single",
                    Prefix              = SingleFilePrefix,
                    ZeroPadWidth        = 0,
                    StartIndex          = 0,
                    SuperSize           = superSize,
                    CaptureEveryNFrames = 0,
                    TargetCaptureFps    = 0f,
                    MaxFramesPerSession = 0,
                    FramesRequested     = 1,
                    FilesDetected       = null,
                    StopReason          = null,
                };
                string metaPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(filename) + ".meta.json");
                m.Write(metaPath);
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.LogExSingleMetadataFailed, ex);
            }
        }
    }
}
