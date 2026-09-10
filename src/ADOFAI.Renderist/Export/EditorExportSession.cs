using System;
using System.Globalization;
using System.IO;
using System.Text;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 确定性编辑器导出会话数据模型（Phase 3.4.0）。
    ///
    /// 保存本阶段真实存在的信息：会话 ID、开始/结束时间、输出目录、当前状态、
    /// TickCount（Unity OnUpdate 推进次数，不是导出帧号）、OutputFps、tail / safety policy、
    /// CaptureRequestCount / CapturedFrameCount（PNG 写盘成功后递增的真实计数）、canonical
    /// completion 观测结果、终止分类、场景名。
    /// </summary>
    internal sealed class EditorExportSession
    {
        public string SessionId;
        public string OutputDirectory;
        public EditorExportState State;
        public string StateDetail;
        public string StopReason;
        public string TerminationKind;  // canonical-completion | user-cancel | safety-limit | watchdog | capture-failure | lifecycle-failure
        public string CompletionSignal;
        public int CompletionFrameIndex;
        public bool CanonicalCompletionCallbackSeen;
        public bool CanonicalCompletionStateSeen;
        public string SceneName;
        public DateTime? StartedAtUtc;
        public DateTime? EndedAtUtc;
        public long TickCount;
        public int OutputFps;
        public long SafetyFrameLimit;
        public double EndTailInputValue;
        public string EndTailInputUnit;
        public int? ResolvedTailFrames;
        public double? ResolvedTailSeconds;
        public double? ResolvedTailBeats;
        public double? CompletionBpm;
        public double? Pitch;
        public long TailFramesCaptured;
        public long CaptureRequestCount;
        public long CapturedFrameCount;

        private const string PhaseLabel = "Phase 3.4.0 deterministic editor export";
        private const string ModeLabel = "editor-export-png-sequence";
        private const string MetadataFileName = "metadata.json";

        public EditorExportSession(string sessionId, string outputDirectory, string sceneName)
        {
            SessionId = sessionId;
            OutputDirectory = outputDirectory;
            SceneName = sceneName;
            State = EditorExportState.Preparing;
            StateDetail = "正在准备会话。";
            StartedAtUtc = DateTime.UtcNow;
            EndedAtUtc = null;
            TickCount = 0;
            OutputFps = 0;
            SafetyFrameLimit = 0;
            EndTailInputValue = EndTailPolicy.DefaultValue;
            EndTailInputUnit = EndTailPolicy.DefaultUnit.ToString();
            ResolvedTailFrames = null;
            ResolvedTailSeconds = null;
            ResolvedTailBeats = null;
            CompletionBpm = null;
            Pitch = null;
            TailFramesCaptured = 0;
            CaptureRequestCount = 0;
            CapturedFrameCount = 0;
            StopReason = null;
            TerminationKind = null;
            CompletionSignal = null;
            CompletionFrameIndex = -1;
            CanonicalCompletionCallbackSeen = false;
            CanonicalCompletionStateSeen = false;
        }

        /// <summary>写入当前会话 metadata。幂等：重复调用覆盖同一文件。</summary>
        public void WriteMetadata()
        {
            if (string.IsNullOrEmpty(OutputDirectory))
            {
                return;
            }
            try
            {
                string path = Path.Combine(OutputDirectory, MetadataFileName);
                File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Exception("EditorExportSession: 写入 metadata 失败", ex);
                throw;
            }
        }

        public string ToJson()
        {
            var sb = new StringBuilder(512);
            sb.Append("{\n");
            AppendString(sb, "version", ModEntry.ModVersion, true);
            AppendString(sb, "phase", PhaseLabel, true);
            AppendString(sb, "mode", ModeLabel, true);
            AppendString(sb, "sessionId", SessionId ?? string.Empty, true);
            AppendStringNullable(sb, "createdAt", IsoUtc(StartedAtUtc), true);
            AppendStringNullable(sb, "endedAt", IsoUtc(EndedAtUtc), true);
            AppendString(sb, "state", State.ToString(), true);
            AppendStringNullable(sb, "stateDetail", StateDetail, true);
            AppendStringNullable(sb, "stopReason", StopReason, true);
            AppendStringNullable(sb, "terminationKind", TerminationKind, true);
            AppendStringNullable(sb, "completionSignal", CompletionSignal, true);
            AppendLong(sb, "completionFrameIndex", CompletionFrameIndex, true);
            AppendBool(sb, "canonicalCompletionCallbackSeen", CanonicalCompletionCallbackSeen, true);
            AppendBool(sb, "canonicalCompletionStateSeen", CanonicalCompletionStateSeen, true);
            AppendStringNullable(sb, "sceneName", SceneName, true);
            AppendStringNullable(sb, "outputDirectory", OutputDirectory, true);
            AppendLong(sb, "tickCount", TickCount, true);
            AppendLong(sb, "outputFps", OutputFps, true);
            AppendLong(sb, "safetyFrameLimit", SafetyFrameLimit, true);
            AppendDouble(sb, "endTailInputValue", EndTailInputValue, true);
            AppendString(sb, "endTailInputUnit", EndTailInputUnit ?? string.Empty, true);
            AppendLongNullable(sb, "resolvedTailFrames", ResolvedTailFrames, true);
            AppendDoubleNullable(sb, "resolvedTailSeconds", ResolvedTailSeconds, true);
            AppendDoubleNullable(sb, "resolvedTailBeats", ResolvedTailBeats, true);
            AppendDoubleNullable(sb, "completionBpm", CompletionBpm, true);
            AppendDoubleNullable(sb, "pitch", Pitch, true);
            AppendLong(sb, "tailFramesCaptured", TailFramesCaptured, true);
            AppendLong(sb, "captureRequestCount", CaptureRequestCount, true);
            AppendLong(sb, "capturedFrameCount", CapturedFrameCount, true);
            sb.Length -= 2; // remove trailing ",\n"
            sb.Append("\n}\n");
            return sb.ToString();
        }

        private static string IsoUtc(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                : null;
        }

        private static void AppendString(StringBuilder sb, string name, string value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": \"").Append(Escape(value ?? string.Empty)).Append('"');
            sb.Append(comma ? ",\n" : "\n");
        }

        private static void AppendStringNullable(StringBuilder sb, string name, string value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": ");
            if (value == null) sb.Append("null");
            else sb.Append('"').Append(Escape(value)).Append('"');
            sb.Append(comma ? ",\n" : "\n");
        }

        private static void AppendLong(StringBuilder sb, string name, long value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
            sb.Append(comma ? ",\n" : "\n");
        }

        private static void AppendLongNullable(StringBuilder sb, string name, long? value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": ");
            if (value.HasValue) sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
            else sb.Append("null");
            sb.Append(comma ? ",\n" : "\n");
        }

        private static void AppendDouble(StringBuilder sb, string name, double value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": ")
              .Append(value.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(comma ? ",\n" : "\n");
        }

        private static void AppendDoubleNullable(StringBuilder sb, string name, double? value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": ");
            if (value.HasValue) sb.Append(value.Value.ToString("R", CultureInfo.InvariantCulture));
            else sb.Append("null");
            sb.Append(comma ? ",\n" : "\n");
        }

        private static void AppendBool(StringBuilder sb, string name, bool value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": ").Append(value ? "true" : "false");
            sb.Append(comma ? ",\n" : "\n");
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
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
