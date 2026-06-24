using System;
using System.Globalization;
using System.IO;
using System.Text;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Capture
{
    /// <summary>
    /// Lightweight metadata.json writer for a capture session.
    ///
    /// Honesty contract (Phase 2.0):
    ///   - ScreenCapture.CaptureScreenshot has no completion/failure callback,
    ///     so we never write a "framesWritten" number.
    ///   - "framesRequested" is incremented synchronously on every request and
    ///     is the source of truth for what we asked Unity to write.
    ///   - "filesDetected" is a weak post-hoc disk scan and is null until the
    ///     session ends. It may legitimately differ from framesRequested when
    ///     Unity drops frames or writes are still flushing.
    /// </summary>
    internal sealed class Metadata
    {
        public string Version;
        public string Phase;
        public string CreatedAtUtc;
        public string EndedAtUtc;
        public string Mode;                  // "single" | "sequence"
        public string Prefix;
        public int ZeroPadWidth;
        public int StartIndex;
        public int SuperSize;
        public int CaptureEveryNFrames;
        public float TargetCaptureFps;
        public int MaxFramesPerSession;
        public int FramesRequested;
        public int? FilesDetected;           // null until end-of-session scan
        public string StopReason;            // "user" | "max-frames" | "disabled" | "error" | null

        public string ToJson()
        {
            var sb = new StringBuilder(512);
            sb.Append("{\n");
            AppendStringField(sb, "version",             Version,             true);
            AppendStringField(sb, "phase",               Phase,               true);
            AppendStringField(sb, "createdAt",           CreatedAtUtc,        true);
            AppendStringFieldNullable(sb, "endedAt",     EndedAtUtc,          true);
            AppendStringField(sb, "mode",                Mode,                true);
            AppendStringField(sb, "prefix",              Prefix,              true);
            AppendIntField   (sb, "zeroPadWidth",        ZeroPadWidth,        true);
            AppendIntField   (sb, "startIndex",          StartIndex,          true);
            AppendIntField   (sb, "superSize",           SuperSize,           true);
            AppendIntField   (sb, "captureEveryNFrames", CaptureEveryNFrames, true);
            AppendFloatField (sb, "targetCaptureFps",    TargetCaptureFps,    true);
            AppendIntField   (sb, "maxFramesPerSession", MaxFramesPerSession, true);
            AppendIntField   (sb, "framesRequested",     FramesRequested,     true);
            AppendNullableIntField(sb, "filesDetected",  FilesDetected,       true);
            AppendStringFieldNullable(sb, "stopReason",  StopReason,          false);
            sb.Append("}\n");
            return sb.ToString();
        }

        public void Write(string path)
        {
            try
            {
                string json = ToJson();
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // metadata write failures must never break capture.
                Log.Exception("Failed to write metadata.json at " + path, ex);
            }
        }

        public static string IsoUtcNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        private static void AppendStringField(StringBuilder sb, string name, string value, bool trailingComma)
        {
            sb.Append("  \"").Append(name).Append("\": \"").Append(EscapeJson(value ?? string.Empty)).Append('"');
            sb.Append(trailingComma ? ",\n" : "\n");
        }

        private static void AppendStringFieldNullable(StringBuilder sb, string name, string value, bool trailingComma)
        {
            sb.Append("  \"").Append(name).Append("\": ");
            if (value == null) sb.Append("null");
            else sb.Append('"').Append(EscapeJson(value)).Append('"');
            sb.Append(trailingComma ? ",\n" : "\n");
        }

        private static void AppendIntField(StringBuilder sb, string name, int value, bool trailingComma)
        {
            sb.Append("  \"").Append(name).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
            sb.Append(trailingComma ? ",\n" : "\n");
        }

        private static void AppendNullableIntField(StringBuilder sb, string name, int? value, bool trailingComma)
        {
            sb.Append("  \"").Append(name).Append("\": ");
            if (value.HasValue) sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
            else sb.Append("null");
            sb.Append(trailingComma ? ",\n" : "\n");
        }

        private static void AppendFloatField(StringBuilder sb, string name, float value, bool trailingComma)
        {
            sb.Append("  \"").Append(name).Append("\": ")
              .Append(value.ToString("0.######", CultureInfo.InvariantCulture));
            sb.Append(trailingComma ? ",\n" : "\n");
        }

        private static string EscapeJson(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
