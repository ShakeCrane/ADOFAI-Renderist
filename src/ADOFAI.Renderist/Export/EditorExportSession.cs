using System;
using System.Globalization;
using System.IO;
using System.Text;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 确定性编辑器导出会话数据模型（Phase 3.7.0）。
    ///
    /// 保存本阶段真实存在的信息：会话 ID、开始/结束时间、输出目录、当前状态、
    /// TickCount（Unity OnUpdate 推进次数，不是导出帧号）、OutputFps、tail / safety policy、
    /// 输出模式（image output enabled / log-only）、输出几何（legacy-window /
    /// custom-resolution 及其冻结宽高与统一 aspect）、只读运行时渲染环境 inventory
    /// （色彩空间 / GPU / capture RenderTexture 形态）、逻辑帧事务计数与 PNG 写盘计数、
    /// canonical completion 观测结果、终止分类、场景名。
    ///
    /// 计数语义（两个模式共用同一条帧事务与唯一 CommitFrame）：
    ///   * FrameTransactionRequestCount：请求过的逻辑帧事务数（PNG 与 log-only 都计入）。
    ///   * CaptureRequestCount：PNG 图像请求数（log-only 恒为 0）。
    ///   * LogicalFrameCount：成功提交的逻辑输出帧数。
    ///   * WrittenPngFrameCount / CapturedFrameCount：成功写盘 PNG 的帧数（log-only 恒为 0）。
    ///   * TailFramesCommitted：成功提交的逻辑尾帧数（completion 判定 authority）。
    ///   * TailFramesCaptured：成功写盘的 PNG 尾帧数（log-only 恒为 0）。
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
        /// <summary>canonical completion 时的 output frame 编号（long；-1 = 未观测）。</summary>
        public long CompletionFrameIndex;
        public bool CanonicalCompletionCallbackSeen;
        public bool CanonicalCompletionStateSeen;
        public string SceneName;
        public DateTime? StartedAtUtc;
        public DateTime? EndedAtUtc;
        public long TickCount;
        public int OutputFps;
        /// <summary>本 session 实际采用的 safety policy 标签：unbounded | explicit-frames。</summary>
        public string SafetyPolicy;
        /// <summary>
        /// 实际生效的 output-frame safety 上限；null = 未配置（无总帧数 / 总时长上限），
        /// 不是 0 帧，也不是 sentinel。
        /// </summary>
        public long? SafetyFrameLimit;
        /// <summary>与 SafetyFrameLimit 对应的逻辑 output duration（秒）；unbounded 时为 null。</summary>
        public double? SafetyDurationSeconds;
        public double EndTailInputValue;
        public string EndTailInputUnit;
        /// <summary>解析后的 End Tail output-frame 数（long：与 canonical frame 计数同一域）。</summary>
        public long? ResolvedTailFrames;
        public double? ResolvedTailSeconds;
        public double? ResolvedTailBeats;
        public double? CompletionBpm;
        public double? Pitch;
        /// <summary>
        /// 本 session 冻结的输出模式：true = 写 PNG 序列，false = log-only
        /// （image output disabled；帧事务照常，但不写图像，仍写 metadata）。
        /// </summary>
        public bool ImageOutputEnabled;
        public long TailFramesCommitted;
        public long TailFramesCaptured;
        public long FrameTransactionRequestCount;
        public long LogicalFrameCount;
        public long WrittenPngFrameCount;
        public long CaptureRequestCount;
        public long CapturedFrameCount;
        public string CaptureSource;
        public long CaptureWidth;
        public long CaptureHeight;

        // ---- Phase 3.7.0: 输出几何 ----

        /// <summary>本 session 的输出几何来源方式：legacy-window | custom-resolution。</summary>
        public string OutputGeometryMode;
        /// <summary>本 session 开始时冻结的“是否使用自定义分辨率”。</summary>
        public bool GeometryCustomResolutionEnabled;
        /// <summary>Settings 中 persisted 的自定义宽度（诊断用，未经解析、未 sanitize）。</summary>
        public long GeometryConfiguredWidth;
        /// <summary>Settings 中 persisted 的自定义高度（诊断用，未经解析、未 sanitize）。</summary>
        public long GeometryConfiguredHeight;
        /// <summary>本 session 冻结的输出宽度（= capture RenderTexture 宽度）。</summary>
        public long OutputWidth;
        /// <summary>本 session 冻结的输出高度（= capture RenderTexture 高度）。</summary>
        public long OutputHeight;
        /// <summary>本 session 冻结的统一输出 aspect（三台原生 Camera 与 capture RT 共用）。</summary>
        public double OutputAspect;

        // ---- Phase 3.7.0: 只读运行时渲染环境 inventory ----

        /// <summary>QualitySettings.activeColorSpace（Gamma / Linear）；读取失败为 null。</summary>
        public string ColorSpace;
        public string GraphicsDeviceType;
        public string GraphicsDeviceName;
        public string GraphicsDeviceVersion;
        public int? GraphicsShaderLevel;
        /// <summary>SystemInfo.maxTextureSize：自定义分辨率的真实硬件能力上限来源。</summary>
        public int? MaxTextureSize;
        public bool? SupportsComputeShaders;
        public int? SystemMemorySizeMb;
        /// <summary>capture RenderTexture 的实际 format / graphicsFormat / MSAA / mipmap。</summary>
        public string RenderTextureFormat;
        public string RenderTextureGraphicsFormat;
        public int? RenderTextureAntiAliasing;
        public bool? RenderTextureUseMipMap;

        private const string PhaseLabel = "Phase 3.7.0 Custom Resolution & Supersampling";
        /// <summary>PNG 序列模式的既有 mode 值（保持不变，避免破坏既有 metadata 语义）。</summary>
        private const string PngModeLabel = "editor-export-png-sequence";
        /// <summary>log-only（image output disabled）模式的 mode 值。</summary>
        private const string LogOnlyModeLabel = "editor-export-log-only";
        private const string MetadataFileName = "metadata.json";

        /// <summary>
        /// metadata 的 mode 字段：由冻结的 imageOutputEnabled 派生，不额外维护第二套状态。
        /// </summary>
        public string Mode => ImageOutputEnabled ? PngModeLabel : LogOnlyModeLabel;

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
            SafetyPolicy = null;
            SafetyFrameLimit = null;
            SafetyDurationSeconds = null;
            EndTailInputValue = EndTailPolicy.DefaultValue;
            EndTailInputUnit = EndTailPolicy.DefaultUnit.ToString();
            ResolvedTailFrames = null;
            ResolvedTailSeconds = null;
            ResolvedTailBeats = null;
            CompletionBpm = null;
            Pitch = null;
            // 与 Settings.EditorImageOutputEnabled 的默认值一致：未被显式设置前不得
            // 静默声称 log-only。
            ImageOutputEnabled = true;
            TailFramesCommitted = 0;
            TailFramesCaptured = 0;
            FrameTransactionRequestCount = 0;
            LogicalFrameCount = 0;
            WrittenPngFrameCount = 0;
            CaptureRequestCount = 0;
            CapturedFrameCount = 0;
            CaptureSource = null;
            CaptureWidth = 0;
            CaptureHeight = 0;
            // 输出几何：未被 scheduler 冻结前不得声称任何已解析结果；
            // legacy-window 是 Settings 默认值对应的模式，因此作为初始标签。
            OutputGeometryMode = OutputGeometryPolicy.LegacyWindowLabel;
            GeometryCustomResolutionEnabled = false;
            GeometryConfiguredWidth = 0;
            GeometryConfiguredHeight = 0;
            OutputWidth = 0;
            OutputHeight = 0;
            OutputAspect = 0.0;
            // 运行时环境 inventory：未采集到就保持 null，绝不用占位值冒充真实环境。
            ColorSpace = null;
            GraphicsDeviceType = null;
            GraphicsDeviceName = null;
            GraphicsDeviceVersion = null;
            GraphicsShaderLevel = null;
            MaxTextureSize = null;
            SupportsComputeShaders = null;
            SystemMemorySizeMb = null;
            RenderTextureFormat = null;
            RenderTextureGraphicsFormat = null;
            RenderTextureAntiAliasing = null;
            RenderTextureUseMipMap = null;
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
            AppendString(sb, "mode", Mode, true);
            AppendBool(sb, "imageOutputEnabled", ImageOutputEnabled, true);
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
            AppendStringNullable(sb, "safetyPolicy", SafetyPolicy, true);
            AppendLongNullable(sb, "safetyFrameLimit", SafetyFrameLimit, true);
            AppendDoubleNullable(sb, "safetyDurationSeconds", SafetyDurationSeconds, true);
            AppendDouble(sb, "endTailInputValue", EndTailInputValue, true);
            AppendString(sb, "endTailInputUnit", EndTailInputUnit ?? string.Empty, true);
            AppendLongNullable(sb, "resolvedTailFrames", ResolvedTailFrames, true);
            AppendDoubleNullable(sb, "resolvedTailSeconds", ResolvedTailSeconds, true);
            AppendDoubleNullable(sb, "resolvedTailBeats", ResolvedTailBeats, true);
            AppendDoubleNullable(sb, "completionBpm", CompletionBpm, true);
            AppendDoubleNullable(sb, "pitch", Pitch, true);
            AppendLong(sb, "tailFramesCommitted", TailFramesCommitted, true);
            AppendLong(sb, "tailFramesCaptured", TailFramesCaptured, true);
            AppendStringNullable(sb, "captureSource", CaptureSource, true);
            AppendLong(sb, "captureWidth", CaptureWidth, true);
            AppendLong(sb, "captureHeight", CaptureHeight, true);
            AppendString(sb, "outputGeometryMode", OutputGeometryMode ?? string.Empty, true);
            AppendBool(sb, "geometryCustomResolutionEnabled", GeometryCustomResolutionEnabled, true);
            AppendLong(sb, "geometryConfiguredWidth", GeometryConfiguredWidth, true);
            AppendLong(sb, "geometryConfiguredHeight", GeometryConfiguredHeight, true);
            AppendLong(sb, "outputWidth", OutputWidth, true);
            AppendLong(sb, "outputHeight", OutputHeight, true);
            AppendDouble(sb, "outputAspect", OutputAspect, true);
            AppendStringNullable(sb, "colorSpace", ColorSpace, true);
            AppendStringNullable(sb, "graphicsDeviceType", GraphicsDeviceType, true);
            AppendStringNullable(sb, "graphicsDeviceName", GraphicsDeviceName, true);
            AppendStringNullable(sb, "graphicsDeviceVersion", GraphicsDeviceVersion, true);
            AppendIntNullable(sb, "graphicsShaderLevel", GraphicsShaderLevel, true);
            AppendIntNullable(sb, "maxTextureSize", MaxTextureSize, true);
            AppendBoolNullable(sb, "supportsComputeShaders", SupportsComputeShaders, true);
            AppendIntNullable(sb, "systemMemorySizeMb", SystemMemorySizeMb, true);
            AppendStringNullable(sb, "renderTextureFormat", RenderTextureFormat, true);
            AppendStringNullable(sb, "renderTextureGraphicsFormat", RenderTextureGraphicsFormat, true);
            AppendIntNullable(sb, "renderTextureAntiAliasing", RenderTextureAntiAliasing, true);
            AppendBoolNullable(sb, "renderTextureUseMipMap", RenderTextureUseMipMap, true);
            AppendLong(sb, "frameTransactionRequestCount", FrameTransactionRequestCount, true);
            AppendLong(sb, "logicalFrameCount", LogicalFrameCount, true);
            AppendLong(sb, "captureRequestCount", CaptureRequestCount, true);
            AppendLong(sb, "writtenPngFrameCount", WrittenPngFrameCount, true);
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

        private static void AppendIntNullable(StringBuilder sb, string name, int? value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": ");
            if (value.HasValue) sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
            else sb.Append("null");
            sb.Append(comma ? ",\n" : "\n");
        }

        private static void AppendBoolNullable(StringBuilder sb, string name, bool? value, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": ");
            if (value.HasValue) sb.Append(value.Value ? "true" : "false");
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
