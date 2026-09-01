using System;
using System.Globalization;
using System.IO;
using System.Text;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器导出会话（Phase 2.4）。
    ///
    /// 仅保存本阶段真实存在的信息：
    ///   * 会话 ID、开始/结束时间、输出目录
    ///   * 当前状态与状态说明
    ///   * TickCount（Unity OnUpdate 推进次数，不是导出帧数 / 渲染帧数 / 截图帧数）
    ///   * CaptureImplemented 固定为 false，CaptureRequestCount / CapturedFrameCount 固定为 0
    ///   * 结束原因、场景名
    ///
    /// 不保存、不恢复本阶段没有修改的 Unity 时间 / 相机 / 游戏内部状态。
    /// </summary>
    internal sealed class EditorExportSession
    {
        /// <summary>Phase 2.4 不实现截图后端，固定为 false。</summary>
        public const bool CaptureImplemented = false;

        public string SessionId;
        public string OutputDirectory;
        public EditorExportState State;
        public string StateDetail;
        public string StopReason;       // "user" | "cancelled" | "failed" | null
        public string SceneName;
        public DateTime? StartedAtUtc;
        public DateTime? EndedAtUtc;
        public long TickCount;
        public long CaptureRequestCount; // 固定 0
        public long CapturedFrameCount;  // 固定 0

        private const string PhaseLabel = "Phase 2.4 editor export skeleton";
        private const string ModeLabel = "editor-export-skeleton";
        private const string MetadataFileName = "metadata.json";
        private const string NoteText =
            "Phase 2.4 editor export skeleton: no real capture, no non-realtime rendering, no time/camera/UI control.";

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
            CaptureRequestCount = 0;
            CapturedFrameCount = 0;
            StopReason = null;
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
            var sb = new StringBuilder(640);
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
            AppendStringNullable(sb, "sceneName", SceneName, true);
            AppendStringNullable(sb, "outputDirectory", OutputDirectory, true);
            AppendLong(sb, "tickCount", TickCount, true);
            AppendLong(sb, "captureRequestCount", CaptureRequestCount, true);
            AppendLong(sb, "capturedFrameCount", CapturedFrameCount, true);
            AppendBool(sb, "captureImplemented", CaptureImplemented, true);
            AppendString(sb, "note", NoteText, false);
            sb.Append("}\n");
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
