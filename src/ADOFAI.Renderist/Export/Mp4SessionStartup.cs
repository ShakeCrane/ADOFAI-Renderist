using System;
using System.IO;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 创建并 Start **唯一** L2 编码管线的缝。生产实现直接调用
    /// <see cref="FfmpegVideoPipeline"/>；回归用假实现可以确定性覆盖 Start 失败路径。
    /// 本接口**不**创建第二套 process owner：它只返回 L2 自己的实例。
    /// </summary>
    internal interface IMp4PipelineFactory
    {
        /// <summary>只构造 L2 实例，**不**启动进程。失败时返回 null。</summary>
        FfmpegVideoPipeline Create(
            FfmpegVideoPipelineOptions options, out string errorCode, out string errorDetail);

        /// <summary>启动已构造的 L2 实例。失败时返回 false（L2 内部 fail-closed 收敛）。</summary>
        bool Start(FfmpegVideoPipeline pipeline, out string errorCode, out string errorDetail);
    }

    /// <summary>生产实现：L2 管线的直接构造 + Start。</summary>
    internal sealed class FfmpegPipelineFactory : IMp4PipelineFactory
    {
        internal static readonly FfmpegPipelineFactory Instance = new FfmpegPipelineFactory();

        public FfmpegVideoPipeline Create(
            FfmpegVideoPipelineOptions options, out string errorCode, out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;
            try
            {
                return new FfmpegVideoPipeline(options);
            }
            catch (Exception ex)
            {
                errorCode = "pipeline-construct-failed";
                errorDetail = ex.Message;
                return null;
            }
        }

        public bool Start(FfmpegVideoPipeline pipeline, out string errorCode, out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;
            if (pipeline == null)
            {
                errorCode = "pipeline-missing";
                return false;
            }

            FfmpegVideoStartResult start;
            try
            {
                start = pipeline.Start();
            }
            catch (Exception ex)
            {
                errorCode = "pipeline-start-exception";
                errorDetail = ex.Message;
                return false;
            }

            if (start == null || !start.Started)
            {
                errorCode = start == null ? "pipeline-start-null" : (start.ErrorCode ?? "pipeline-start-rejected");
                errorDetail = start == null ? null : start.ErrorDetail;
                return false;
            }

            return true;
        }
    }

    /// <summary>MP4 session 启动所需的**已冻结输入**。全部在 Unity 主线程、Play 之前确定。</summary>
    internal sealed class Mp4StartupInputs
    {
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
        public int OutputFps { get; set; }
        public int Crf { get; set; }
        public string Preset { get; set; }

        /// <summary>本 session 的唯一输出目录（已创建）。</summary>
        public string SessionDirectory { get; set; }

        /// <summary>L1 组件报告；必须为 Ready 才能冻结身份。</summary>
        public FfmpegComponentReport Report { get; set; }
    }

    internal sealed class Mp4StartupResult
    {
        internal Mp4StartupResult()
        {
            ErrorCode = null;
            ErrorDetail = null;
        }

        internal bool Succeeded { get { return Options != null; } }
        internal string ErrorCode { get; set; }
        internal string ErrorDetail { get; set; }
        internal FfmpegVideoSettings Settings { get; set; }
        internal FfmpegVideoIdentity Identity { get; set; }
        internal string FinalVideoPath { get; set; }

        /// <summary>已冻结、可直接交给 <see cref="IMp4PipelineFactory.Create"/> 的选项。</summary>
        internal FfmpegVideoPipelineOptions Options { get; set; }
    }

    /// <summary>
    /// 建立 RGB24 delivery context 的缝。生产实现直接调用
    /// <see cref="Rgb24DeliveryContext.TryCreate"/>（其中包含主线程 SynchronizationContext 捕获）。
    /// </summary>
    internal interface IMp4DeliveryFactory
    {
        bool TryCreate(int width, int height, FfmpegVideoPipeline pipeline,
            out Rgb24DeliveryContext context, out string error);
    }

    internal sealed class DefaultMp4DeliveryFactory : IMp4DeliveryFactory
    {
        internal static readonly DefaultMp4DeliveryFactory Instance = new DefaultMp4DeliveryFactory();

        public bool TryCreate(int width, int height, FfmpegVideoPipeline pipeline,
            out Rgb24DeliveryContext context, out string error)
        {
            return Rgb24DeliveryContext.TryCreate(width, height, pipeline, out context, out error);
        }
    }

    /// <summary>Bind 的结果：要么拿到已启动管线 + 已建立 context，要么失败。</summary>
    internal sealed class Mp4SessionBinding
    {
        internal bool Succeeded { get { return Context != null; } }
        internal FfmpegVideoPipeline Pipeline { get; set; }
        internal Rgb24DeliveryContext Context { get; set; }
        internal string ErrorCode { get; set; }
        internal string ErrorDetail { get; set; }

        /// <summary>
        /// 失败后仍未能收敛的 context（frame Completion / CleanupTask 尚未完成）。
        /// 调用方必须保留它作为 residual ownership，由既有 Tick 继续收敛并阻止重开。
        /// </summary>
        internal Rgb24DeliveryContext PendingConvergence { get; set; }
    }

    /// <summary>
    /// L3-A 最小 MP4 session 启动：冻结参数 → 冻结 L1 身份 → 唯一最终路径 → 创建并
    /// Start 唯一 L2 管线。**刻意 Unity-free**，因此可以在 net48 harness 中确定性覆盖
    /// （PNG / Log-only 不创建管线、Ready gate、Start 失败、路径冻结与不覆盖）。
    ///
    /// 本模块只做"准备"，不 Arm、不 Play、不 Commit，也不拥有 scheduler 生命周期。
    /// </summary>
    internal static class Mp4SessionStartup
    {
        /// <summary>本 session 内唯一最终视频目标文件名。临时产物由 L2 ownership 管理。</summary>
        internal const string FinalVideoFileName = "video.mp4";

        internal static bool TryResolveFinalVideoPath(
            string sessionDirectory, out string finalPath, out string errorCode)
        {
            finalPath = null;
            errorCode = null;

            if (string.IsNullOrWhiteSpace(sessionDirectory))
            {
                errorCode = "session-directory-missing";
                return false;
            }

            try
            {
                finalPath = Path.Combine(sessionDirectory, FinalVideoFileName);
            }
            catch (Exception)
            {
                errorCode = "final-video-path-invalid";
                return false;
            }

            // 最终目标绝不静默覆盖（L2 也会独立拒绝，这里在启动进程之前就 fail-closed）。
            if (File.Exists(finalPath))
            {
                errorCode = "final-video-exists";
                return false;
            }

            return true;
        }

        internal static bool TryFreezeSettings(
            Mp4StartupInputs inputs, out FfmpegVideoSettings settings, out string errorCode)
        {
            settings = null;
            errorCode = null;

            if (inputs == null)
            {
                errorCode = "mp4-inputs-missing";
                return false;
            }

            // 只做结构性检查；CRF / preset / 像素格式的**产品级**合法性由 L2 自己判定，
            // 这里不复制第二套验证逻辑。
            if (inputs.OutputWidth <= 0 || inputs.OutputHeight <= 0)
            {
                errorCode = "mp4-geometry-invalid";
                return false;
            }

            if (inputs.OutputFps <= 0)
            {
                errorCode = "mp4-fps-invalid";
                return false;
            }

            if (inputs.Crf < 0)
            {
                errorCode = "mp4-crf-invalid";
                return false;
            }

            if (string.IsNullOrWhiteSpace(inputs.Preset))
            {
                errorCode = "mp4-preset-invalid";
                return false;
            }

            settings = new FfmpegVideoSettings
            {
                Width = inputs.OutputWidth,
                Height = inputs.OutputHeight,
                Fps = inputs.OutputFps,
                Crf = inputs.Crf,
                Preset = inputs.Preset,
                // null = 由 L2 按真实几何选择 yuv420p / yuv444p，绝不静默改写输出几何。
                PixelFormat = null,
            };
            return true;
        }

        /// <summary>
        /// 按固定顺序冻结 MP4 session 的可执行输入：冻结编码参数 → 冻结 L1 Ready 身份 →
        /// 解析唯一最终路径 → 组装 L2 options。**不**创建进程、**不** Start。
        ///
        /// 顺序刻意是"先冻结、再创建、再 Start"：这样 context 建立失败时不会留下一个
        /// 已经启动、却没有任何 owner 的编码进程。
        /// </summary>
        internal static Mp4StartupResult Freeze(Mp4StartupInputs inputs)
        {
            var result = new Mp4StartupResult();

            FfmpegVideoSettings settings;
            string settingsError;
            if (!TryFreezeSettings(inputs, out settings, out settingsError))
            {
                result.ErrorCode = settingsError;
                return result;
            }
            result.Settings = settings;

            FfmpegVideoIdentity identity;
            string identityError;
            string identityDetail;
            if (!FfmpegVideoIdentity.TryFreeze(inputs.Report, out identity, out identityError, out identityDetail))
            {
                // L1 Ready / identity gate：不重新发现、不下载、不安装、不改 PATH。
                result.ErrorCode = "mp4-identity:" + (identityError ?? "identity-unavailable");
                result.ErrorDetail = identityDetail;
                return result;
            }
            result.Identity = identity;

            string finalPath;
            string pathError;
            if (!TryResolveFinalVideoPath(inputs.SessionDirectory, out finalPath, out pathError))
            {
                result.ErrorCode = pathError;
                return result;
            }
            result.FinalVideoPath = finalPath;

            result.Options = new FfmpegVideoPipelineOptions
            {
                Settings = settings,
                Identity = identity,
                FinalPath = finalPath,
            };
            return result;
        }

        /// <summary>
        /// MP4 启动前的 readiness gate。**GUI 与 EditorExportController 共用这一个判定**，
        /// 因此不会出现两套 Ready 规则。
        ///
        /// 语义：
        ///   * 非 MP4 模式（PNG / Log-only）恒为允许 —— 它们绝不依赖 FFmpeg 状态；
        ///   * MP4 只有在“当前输入 generation 对应的报告明确 Ready”时才允许
        ///     （Preparing / Failed / fault / 过期报告一律拒绝）。
        ///
        /// 返回 false 时给出稳定的机读 <paramref name="errorCode"/>。
        /// </summary>
        internal static bool TryCheckReadiness(
            CaptureOutputMode mode, FfmpegReadinessSnapshot readiness,
            out string errorCode, out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;

            if (!OutputModePolicy.IsMp4(mode))
                return true;

            if (readiness == null)
            {
                errorCode = "mp4-ffmpeg-readiness-unknown";
                return false;
            }

            // MP4 的四个启动条件（缺一不可）：
            //   1. 没有待处理的输入失效（dirty）；
            //   2. 没有有效的检查在途；
            //   3. 报告明确对应当前输入 generation；
            //   4. 报告状态为 Ready。
            // 下面按顺序显式检查 2 / 4，1+3 由 tracker 的投影（State + Report）保证。
            if (readiness.ScanInFlight)
            {
                errorCode = "mp4-ffmpeg-" + FfmpegReadinessReason.Checking;
                return false;
            }

            if (readiness.State != FfmpegReadinessState.Ready)
            {
                errorCode = "mp4-ffmpeg-" + (readiness.ReasonCode ?? "not-ready");
                errorDetail = readiness.ReasonDetail;
                return false;
            }

            // 纵深防御：Ready 必须由“当前 generation 的 Ready 报告”支撑，
            // 不接受一个自称 Ready 却没有报告的投影。
            if (readiness.Report == null || readiness.Report.State != FfmpegComponentState.Ready)
            {
                errorCode = "mp4-ffmpeg-report-not-ready";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 绑定运行时资源：构造唯一 L2 pipeline → 建立 delivery context → Start pipeline。
        ///
        /// 严格顺序的意义：**context 在 Start 之前建立**，因此 Start 之后任何失败都能经该
        /// context 的既有 Cancel / CleanupTask 收敛，绝不会留下一个"已启动但无 owner"的编码进程。
        /// 本方法不 Arm、不 Play；Arm 由调用方在主线程完成。
        /// </summary>
        internal static Mp4SessionBinding Bind(
            Mp4StartupResult frozen, int width, int height,
            IMp4PipelineFactory pipelines, IMp4DeliveryFactory deliveries)
        {
            var binding = new Mp4SessionBinding();

            if (frozen == null || !frozen.Succeeded)
            {
                binding.ErrorCode = frozen == null ? "mp4-frozen-missing" : frozen.ErrorCode;
                binding.ErrorDetail = frozen == null ? null : frozen.ErrorDetail;
                return binding;
            }

            if (pipelines == null || deliveries == null)
            {
                binding.ErrorCode = "mp4-seam-missing";
                return binding;
            }

            string createError;
            string createDetail;
            FfmpegVideoPipeline pipeline = pipelines.Create(frozen.Options, out createError, out createDetail);
            if (pipeline == null)
            {
                // 进程尚未启动：没有需要收敛的运行时资源。
                binding.ErrorCode = "mp4-pipeline-create-failed:" + (createError ?? "unknown");
                binding.ErrorDetail = createDetail;
                return binding;
            }

            Rgb24DeliveryContext context;
            string contextError;
            if (!deliveries.TryCreate(width, height, pipeline, out context, out contextError))
            {
                // context 建立失败、进程也尚未启动：同样没有需要收敛的运行时资源。
                binding.ErrorCode = "mp4-context-create-failed:" + (contextError ?? "unknown");
                return binding;
            }

            string startError;
            string startDetail;
            if (!pipelines.Start(pipeline, out startError, out startDetail))
            {
                binding.ErrorCode = "mp4-pipeline-start-failed:" + (startError ?? "unknown");
                binding.ErrorDetail = startDetail;
                binding.PendingConvergence = RetireOrKeep(context);
                return binding;
            }

            binding.Pipeline = pipeline;
            binding.Context = context;
            return binding;
        }

        /// <summary>
        /// 失败路径统一先尝试收敛；未能收敛时返回该 context，由调用方作为 residual ownership 保留。
        /// </summary>
        internal static Rgb24DeliveryContext RetireOrKeep(Rgb24DeliveryContext context)
        {
            if (context == null) return null;
            string error;
            return context.TryStopAndDrain("mp4-startup-failed", out error) ? null : context;
        }
    }

    /// <summary>
    /// L3-A 终态边界策略。
    ///
    /// 本轮**没有** L3-C Finalizing：不存在正式 FinishAsync / 核验 / 原子发布。因此 MP4 session
    /// 到达既有导出终态边界时**必须 fail-closed**，绝不能把"尚未执行正式完成关口"的视频当作
    /// 成功导出呈现给用户。
    /// </summary>
    internal static class Mp4TerminalBoundaryPolicy
    {
        internal const string ValidationBoundaryStopReason = "l3a-validation-boundary:no-finalizing";

        internal const string ValidationBoundaryDetail =
            "L3-A 验证边界：本 session 到达导出终态时未执行 FFmpeg Finish / 核验 / 发布，" +
            "因此这不是一次成功的 MP4 导出，也没有可用的最终成品。";

        /// <summary>
        /// MP4 session 在 scheduler 报告的终态是否仍然可以记为 Completed。
        /// 无 Finalizing 时**永远**返回 false —— 这是 fail-closed，不是降级。
        /// </summary>
        internal static bool CanMapToCompleted(CaptureOutputMode mode)
        {
            return !OutputModePolicy.IsMp4(mode);
        }
    }
}
