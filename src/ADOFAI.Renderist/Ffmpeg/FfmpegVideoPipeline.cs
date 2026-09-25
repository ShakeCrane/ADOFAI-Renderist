using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 从 L1 的组件报告**冻结**出来的 FFmpeg 二进制身份。
    ///
    /// L1 的 <c>FfmpegComponentReport</c> 只是发现时的快照，**不是**永久运行授权：
    /// 启动编码前必须重新确认所选绝对路径当前的内容身份，并且绝不静默改用其他 FFmpeg。
    ///
    /// 已知且**未解决**的竞态（不虚构保证）：哈希校验与 <c>Process.Start</c> 之间存在
    /// TOCTOU 窗口 —— 校验通过之后、进程启动之前，磁盘上的可执行文件仍可能被替换。
    /// 本模块只保证"校验的是哪个二进制"，不声称已消除该窗口。
    /// </summary>
    internal sealed class FfmpegVideoIdentity
    {
        /// <summary>绝对路径（已确认 rooted）。</summary>
        public string ExecutablePath { get; private set; }

        /// <summary>冻结的内容 SHA-256（小写十六进制）。</summary>
        public string ExecutableSha256 { get; private set; }

        /// <summary>冻结的字节数。</summary>
        public long ExecutableSizeBytes { get; private set; }

        /// <summary>发现来源（诊断用）。</summary>
        public FfmpegCandidateSource Source { get; private set; }

        /// <summary>发现时使用的托管安装（可能为 null）。</summary>
        public FfmpegManagedInstall ManagedInstall { get; private set; }

        // ---- 冻结的必要能力 ----
        public string VersionLine { get; private set; }
        public bool HasLibx264 { get; private set; }
        public bool HasMp4Muxer { get; private set; }
        public bool HasRawvideoDemuxer { get; private set; }

        /// <summary>能力探测当时记录的内容哈希（用于与冻结身份比对）。</summary>
        public string CapabilityExecutableSha256 { get; private set; }

        /// <summary>
        /// 从 L1 报告冻结身份。任何不满足即 fail-closed —— 不重新发现、不下载、不安装、不改 PATH，
        /// 也不会退回其他候选。
        /// </summary>
        public static bool TryFreeze(
            FfmpegComponentReport report, out FfmpegVideoIdentity identity, out string errorCode, out string errorDetail)
        {
            identity = null;
            errorCode = null;
            errorDetail = null;

            if (report == null)
            {
                errorCode = "component-report-null";
                return false;
            }

            if (report.State != FfmpegComponentState.Ready)
            {
                errorCode = "component-not-ready";
                errorDetail = "state=" + report.State;
                return false;
            }

            FfmpegCandidate candidate = report.Candidate;
            if (candidate == null || candidate.Identity == null)
            {
                errorCode = "component-identity-missing";
                return false;
            }

            string absolutePath = candidate.Identity.AbsolutePath;
            if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath))
            {
                errorCode = "identity-path-not-absolute";
                errorDetail = absolutePath;
                return false;
            }

            if (string.IsNullOrWhiteSpace(candidate.Identity.Sha256) || candidate.Identity.Sha256.Length != 64)
            {
                errorCode = "identity-hash-missing";
                return false;
            }

            if (candidate.Identity.SizeBytes <= 0)
            {
                errorCode = "identity-size-missing";
                return false;
            }

            FfmpegCapabilityReport capability = report.Capability;
            if (capability == null)
            {
                errorCode = "capability-missing";
                return false;
            }

            if (capability.Status != FfmpegCapabilityStatus.Probed)
            {
                errorCode = "capability-not-probed";
                errorDetail = "status=" + capability.Status;
                return false;
            }

            if (string.IsNullOrWhiteSpace(capability.ExecutableSha256))
            {
                errorCode = "capability-hash-missing";
                return false;
            }

            if (!FfmpegFileHash.Matches(candidate.Identity.Sha256, capability.ExecutableSha256))
            {
                errorCode = "capability-identity-mismatch";
                errorDetail = "identity=" + candidate.Identity.Sha256 + " capability=" + capability.ExecutableSha256;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(capability.ExecutablePath) &&
                !string.Equals(Path.GetFullPath(capability.ExecutablePath), Path.GetFullPath(absolutePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "capability-path-mismatch";
                errorDetail = "identity=" + absolutePath + " capability=" + capability.ExecutablePath;
                return false;
            }

            if (!capability.IsUsableForMp4)
            {
                errorCode = "capability-incomplete";
                IReadOnlyList<string> missing = capability.MissingCapabilities;
                errorDetail = missing == null || missing.Count == 0
                    ? "unspecified"
                    : string.Join(",", ToArray(missing));
                return false;
            }

            identity = new FfmpegVideoIdentity
            {
                ExecutablePath = Path.GetFullPath(absolutePath),
                ExecutableSha256 = candidate.Identity.Sha256.ToLowerInvariant(),
                ExecutableSizeBytes = candidate.Identity.SizeBytes,
                Source = candidate.Source,
                ManagedInstall = candidate.ManagedInstall,
                VersionLine = capability.VersionLine,
                HasLibx264 = capability.HasLibx264,
                HasMp4Muxer = capability.HasMp4Muxer,
                HasRawvideoDemuxer = capability.HasRawvideoDemuxer,
                CapabilityExecutableSha256 = capability.ExecutableSha256.ToLowerInvariant(),
            };
            return true;
        }

        /// <summary>
        /// 在启动编码进程之前重新确认磁盘上的内容身份（重新读取内容计算 SHA-256，不用元数据缓存）。
        /// </summary>
        public bool TryReverify(out string errorCode, out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;

            if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathRooted(ExecutablePath))
            {
                errorCode = "identity-path-not-absolute";
                return false;
            }

            long size;
            try
            {
                var info = new FileInfo(ExecutablePath);
                if (!info.Exists)
                {
                    errorCode = "identity-executable-missing";
                    errorDetail = ExecutablePath;
                    return false;
                }
                size = info.Length;
            }
            catch (Exception ex)
            {
                errorCode = "identity-size-unavailable";
                errorDetail = ex.Message;
                return false;
            }

            if (size != ExecutableSizeBytes)
            {
                errorCode = "identity-size-changed";
                errorDetail = "expected=" + ExecutableSizeBytes.ToString(CultureInfo.InvariantCulture) +
                              " actual=" + size.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            string sha256;
            string hashError;
            if (!FfmpegFileHash.TryCompute(ExecutablePath, out sha256, out hashError))
            {
                errorCode = "identity-hash-unavailable";
                errorDetail = hashError;
                return false;
            }

            if (!FfmpegFileHash.Matches(ExecutableSha256, sha256))
            {
                errorCode = "identity-hash-changed";
                errorDetail = "expected=" + ExecutableSha256 + " actual=" + sha256;
                return false;
            }

            return true;
        }

        private static string[] ToArray(IReadOnlyList<string> values)
        {
            var array = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
                array[i] = values[i];
            return array;
        }
    }

    /// <summary>
    /// 一帧原始 RGB24 数据的一段。缓冲区由调用方独占，管线**不复制**它，只在本帧写入期间读取。
    /// </summary>
    internal sealed class FfmpegFrameSegment
    {
        public FfmpegFrameSegment(byte[] buffer, int offset, int count)
        {
            Buffer = buffer;
            Offset = offset;
            Count = count;
        }

        public byte[] Buffer { get; private set; }
        public int Offset { get; private set; }
        public int Count { get; private set; }
    }

    /// <summary>
    /// 一帧完整的原始帧（可分段）。允许分段的原因：单个 <c>byte[]</c> 的 CLR 数组表达上限
    /// 不是产品参数上限 —— 超大分辨率可以用多段表达，而长度校验在**接收前**用 long 完成。
    /// </summary>
    internal sealed class FfmpegVideoFrame
    {
        private readonly FfmpegFrameSegment[] _segments;

        public FfmpegVideoFrame(IReadOnlyList<FfmpegFrameSegment> segments)
        {
            if (segments == null)
                throw new ArgumentNullException("segments");

            _segments = new FfmpegFrameSegment[segments.Count];
            long total = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                FfmpegFrameSegment segment = segments[i];
                if (segment == null)
                    throw new ArgumentException("frame segment " + i + " is null", "segments");
                if (segment.Buffer == null)
                    throw new ArgumentException("frame segment " + i + " buffer is null", "segments");
                if (segment.Offset < 0 || segment.Count < 0 ||
                    (long)segment.Offset + segment.Count > segment.Buffer.Length)
                {
                    throw new ArgumentException("frame segment " + i + " is out of range", "segments");
                }

                _segments[i] = segment;
                total += segment.Count;
            }

            Length = total;
        }

        public static FfmpegVideoFrame FromBuffer(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");

            return new FfmpegVideoFrame(new[] { new FfmpegFrameSegment(buffer, 0, buffer.Length) });
        }

        public IReadOnlyList<FfmpegFrameSegment> Segments
        {
            get { return _segments; }
        }

        /// <summary>累计字节数（long，不受单数组大小限制）。</summary>
        public long Length { get; private set; }
    }

    internal enum FfmpegVideoPipelineState
    {
        NotStarted = 0,
        Running = 1,
        Finalizing = 2,
        Completed = 3,
        Cancelled = 4,
        Failed = 5,
    }

    internal sealed class FfmpegVideoPipelineOptions
    {
        /// <summary>会话开始时冻结的编码配置。</summary>
        public FfmpegVideoSettings Settings { get; set; }

        /// <summary>从 L1 报告冻结的 FFmpeg 身份。</summary>
        public FfmpegVideoIdentity Identity { get; set; }

        /// <summary>最终 MP4 的绝对路径。目录必须已存在；已存在的目标文件绝不删除或覆盖。</summary>
        public string FinalPath { get; set; }

        /// <summary>临时产物路径覆盖（测试用）。null = 由最终路径 + 唯一 token 派生。</summary>
        public string TempPath { get; set; }

        /// <summary>进程启动缝。null = 生产 <see cref="Process.Start(ProcessStartInfo)"/>。</summary>
        public FfmpegVideoProcessStart ProcessStart { get; set; }

        /// <summary>视频核验器。null = 生产 <see cref="FfmpegVideoVerifier"/>。</summary>
        public IFfmpegVideoVerifier Verifier { get; set; }

        /// <summary>核验所使用的期望编解码器名。默认 "h264"（libx264）。</summary>
        public string ExpectedCodecName { get; set; }

        /// <summary>
        /// **回归测试专用故障注入缝**：包装 stdin / stdout / stderr 流（参数为流与它的角色名）。
        /// null = 直接使用进程提供的流。生产恒为 null。
        /// 用途：让"读取流抛 IO 异常"这一故障形态可以确定性复现，而不是靠随机时序。
        /// </summary>
        public Func<Stream, string, Stream> StreamWrapper { get; set; }

        /// <summary>
        /// **回归测试专用故障注入缝**：在"字节已完整写出"与"写入结果提交到会话状态"之间的
        /// 可控屏障。null = 无屏障。生产恒为 null。
        ///
        /// 该缝标记的正是 Write/Finish 交接的临界点：修复后，在途标记由提交临界区自己持有并释放，
        /// 因此屏障期间 FinishAsync 必须拒绝，绝不可能观察到未提交的写入结果。
        /// </summary>
        public Action WriteCommitBarrier { get; set; }
    }

    internal sealed class FfmpegVideoStartResult
    {
        public bool Started { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }
        public string TempPath { get; set; }
        public string Arguments { get; set; }
    }

    internal sealed class FfmpegFrameWriteResult
    {
        public bool Success { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }
        public long DeliveredFrameCount { get; set; }
    }

    /// <summary>
    /// 一次帧提交尝试的结果。被拒绝时**不排队**：第二帧并发提交立即返回 busy。
    /// </summary>
    internal sealed class FfmpegFrameWriteAttempt
    {
        private FfmpegFrameWriteAttempt()
        {
        }

        public bool Accepted { get; private set; }
        public string ErrorCode { get; private set; }
        public string ErrorDetail { get; private set; }

        /// <summary>被接受时：本帧完整写入（或失败）的完成通知。调用方可 await，绝不阻塞主线程。</summary>
        public Task<FfmpegFrameWriteResult> Completion { get; private set; }

        public static FfmpegFrameWriteAttempt Rejected(string errorCode, string errorDetail)
        {
            return new FfmpegFrameWriteAttempt
            {
                Accepted = false,
                ErrorCode = errorCode,
                ErrorDetail = errorDetail,
            };
        }

        public static FfmpegFrameWriteAttempt Accept(Task<FfmpegFrameWriteResult> completion)
        {
            return new FfmpegFrameWriteAttempt
            {
                Accepted = true,
                Completion = completion,
            };
        }
    }

    /// <summary>
    /// 管线终态结果（Finish 与 cleanup 共用同一形状）。
    /// </summary>
    internal sealed class FfmpegVideoOutcome
    {
        public FfmpegVideoPipelineState State { get; set; }

        /// <summary>只有 Completed 时非 null。</summary>
        public string FinalPath { get; set; }

        public string TempPath { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }

        /// <summary>取消原因（仅 Cancelled）。</summary>
        public string Reason { get; set; }

        /// <summary>**完整写入成功**的帧数。</summary>
        public long DeliveredFrameCount { get; set; }

        public int? EncoderExitCode { get; set; }
        public bool ProcessStarted { get; set; }
        public bool ProcessReaped { get; set; }

        /// <summary>核验证据（仅在跑到核验步骤时非 null）。</summary>
        public FfmpegVideoVerificationResult Verification { get; set; }

        /// <summary>true = 本会话拥有的临时文件在终态后已不存在。</summary>
        public bool TempFileRemoved { get; set; }

        /// <summary>true = 仍有本会话拥有的资源未收敛（进程未回收 / 临时文件未删除）。绝不伪装成 clean。</summary>
        public bool ResidualOwnership { get; set; }

        public string ResidualDetail { get; set; }

        /// <summary>true = 进程对象已经 Dispose（正常完成路径同样必须释放，而不是等 GC）。</summary>
        public bool ProcessDisposed { get; set; }

        /// <summary>true = 本会话为临时产物建立的 ownership 句柄已经释放。</summary>
        public bool TempOwnershipReleased { get; set; }

        public bool IsCompleted
        {
            get { return State == FfmpegVideoPipelineState.Completed; }
        }
    }

    /// <summary>
    /// L2 —— 独立于 Unity 帧调度的 FFmpeg 视频进程管线（Unity-free / UMM-free / Harmony-free）。
    ///
    /// 职责：会话生命周期、进程与管道 ownership、单帧完整写入、异步背压、Finish / Finalizing、
    /// Cancel、资源回收、终态仲裁、临时文件 ownership、最终文件发布。
    ///
    /// 关键不变量：
    ///
    ///   * **一次最多一个写入中的帧**。第二帧并发提交立即返回 <c>busy</c>，不建立无界队列。
    ///   * **完整写入成功后才增加交付帧计数**；部分写入失败会污染管道（<c>pipe-poisoned</c>），
    ///     绝不在同一个 stdin 上重试该帧。
    ///   * **写入成功 ≠ MP4 完成**。正常完成需要：全部预期帧完整写入 → stdin 正常关闭 →
    ///     编码进程退出 → stdout/stderr 都到 EOF → 退出码 0 → 独立核验通过 → 同目录原子发布。
    ///   * **终态仲裁唯一**：Finish / Cancel / Failure / Publish 竞争时只有一个赢家；
    ///     发布一旦提交即 Completed，之后的 Cancel 不能再取消或删除已发布文件。
    ///   * **取消和失败只清理本次会话拥有的临时文件**；已存在的正式目标文件绝不删除或覆盖。
    ///   * **Dispose 不同步等待进程退出**：它只请求取消，收敛在可观察、可等待的
    ///     <see cref="CleanupTask"/> 上继续；清理失败会如实报告 residual ownership。
    ///   * 合法 IO 背压**没有固定写入超时**；取消也不只依赖 WriteAsync 的 token ——
    ///     取消会终止进程，从而主动解除被阻塞的写入。
    /// </summary>
    internal sealed class FfmpegVideoPipeline : IDisposable
    {
        private readonly object _gate = new object();

        // ---- 冻结配置：构造时复制全部标量与路径值 ----
        // 之后调用方修改 FfmpegVideoPipelineOptions / FfmpegVideoSettings 不再影响本会话：
        // 命令构造、帧长度、核验期望与输出路径全部只读这些冻结值。
        private readonly FfmpegVideoSettings _settings;
        private readonly FfmpegVideoIdentity _identity;
        private readonly string _configuredFinalPath;
        private readonly string _configuredTempPath;
        private readonly string _expectedCodecName;
        private readonly FfmpegVideoProcessStart _processStart;
        private readonly IFfmpegVideoVerifier _verifier;
        private readonly Func<Stream, string, Stream> _streamWrapper;
        private readonly Action _writeCommitBarrier;
        private readonly long _frameLengthBytes;

        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly BoundedTextCollector _stdoutText = new BoundedTextCollector(4096, 4);
        private readonly BoundedTextCollector _stderrText = new BoundedTextCollector(8192, 8);

        private FfmpegVideoPipelineState _state = FfmpegVideoPipelineState.NotStarted;
        private bool _starting;
        private bool _disposed;

        private Process _process;
        private Stream _stdin;
        private Task<Exception> _stdoutPump;
        private Task<Exception> _stderrPump;
        private Task _exitTask;

        private int _writeInFlight;
        private long _deliveredFrames;
        private bool _poisoned;

        private string _finalPath;
        private string _tempPath;

        /// <summary>本会话对临时产物的 ownership 句柄：原子创建 + 直到发布/清理前一直持有。</summary>
        private FileStream _tempOwnership;
        private bool _tempOwned;
        private bool _tempOwnershipReleased;

        private int? _encoderExitCode;
        private bool _processStarted;
        private bool _processReaped;
        private bool _processDisposed;
        private bool _processKillFailed;
        private string _cancelReason;
        private string _failureCode;
        private string _failureDetail;

        /// <summary>资源释放阶段的可观察失败细节（不进入终态判定，但绝不隐藏）。</summary>
        private string _releaseDetail;

        private Task<FfmpegVideoOutcome> _convergence;

        public FfmpegVideoPipeline(FfmpegVideoPipelineOptions options)
        {
            if (options == null)
                throw new ArgumentNullException("options");

            // 深复制：只保留标量副本，不保留调用方的可变对象引用。
            FfmpegVideoSettings source = options.Settings ?? new FfmpegVideoSettings();
            _settings = new FfmpegVideoSettings
            {
                Width = source.Width,
                Height = source.Height,
                Fps = source.Fps,
                Crf = source.Crf,
                Preset = source.Preset,
                PixelFormat = source.PixelFormat,
            };

            _identity = options.Identity;
            _configuredFinalPath = options.FinalPath;
            _configuredTempPath = options.TempPath;
            _expectedCodecName = string.IsNullOrEmpty(options.ExpectedCodecName) ? "h264" : options.ExpectedCodecName;
            _processStart = options.ProcessStart;
            _streamWrapper = options.StreamWrapper;
            _writeCommitBarrier = options.WriteCommitBarrier;

            string settingsError;
            string settingsDetail;
            if (!_settings.TryValidate(out settingsError, out settingsDetail))
                throw new ArgumentException("invalid video settings: " + settingsError + " " + (settingsDetail ?? string.Empty));

            long frameLength;
            string frameError;
            if (!_settings.TryGetFrameLengthBytes(out frameLength, out frameError))
                throw new ArgumentException("invalid video settings: " + frameError);

            _frameLengthBytes = frameLength;
            _verifier = options.Verifier ?? new FfmpegVideoVerifier(options.ProcessStart);
        }

        public FfmpegVideoPipelineState State
        {
            get
            {
                lock (_gate)
                    return _state;
            }
        }

        /// <summary>本会话冻结的单帧完整字节数。</summary>
        public long FrameLengthBytes
        {
            get { return _frameLengthBytes; }
        }

        /// <summary>已完整写入的帧数（WriteAsync 成功不代表 MP4 完成）。</summary>
        public long DeliveredFrameCount
        {
            get { lock (_gate) return _deliveredFrames; }
        }

        public string FinalPath
        {
            get { return _finalPath; }
        }

        public string TempPath
        {
            get { return _tempPath; }
        }

        /// <summary>唯一启动的终态收敛任务（未发生终态时为 null）。可观察、可等待。</summary>
        public Task<FfmpegVideoOutcome> CleanupTask
        {
            get { lock (_gate) return _convergence; }
        }

        // ==================================================================== start

        public FfmpegVideoStartResult Start()
        {
            var result = new FfmpegVideoStartResult();

            lock (_gate)
            {
                if (_disposed)
                {
                    result.ErrorCode = "disposed";
                    return result;
                }

                if (_state != FfmpegVideoPipelineState.NotStarted || _starting)
                {
                    result.ErrorCode = "already-started";
                    result.ErrorDetail = "state=" + _state;
                    return result;
                }

                _starting = true;
            }

            try
            {
                return StartCore(result);
            }
            finally
            {
                lock (_gate)
                {
                    _starting = false;
                    Monitor.PulseAll(_gate);

                    // 启动失败且尚未发生任何需要回收的资源：仍然给出可观察的 CleanupTask。
                    if (_convergence == null &&
                        _state != FfmpegVideoPipelineState.NotStarted &&
                        _state != FfmpegVideoPipelineState.Running)
                    {
                        _convergence = Task.FromResult(BuildOutcome());
                    }
                }
            }
        }

        private FfmpegVideoStartResult StartCore(FfmpegVideoStartResult result)
        {
            // 1) 输出路径与临时文件 ownership（在启动任何进程之前确定）。
            string pathError;
            string pathDetail;
            if (!TryPreparePaths(out pathError, out pathDetail))
            {
                ClaimFailure(pathError, pathDetail);
                result.ErrorCode = pathError;
                result.ErrorDetail = pathDetail;
                return result;
            }

            result.TempPath = _tempPath;

            // 2) 冻结身份重新确认（fail-closed；绝不改用其他 FFmpeg）。
            FfmpegVideoIdentity identity = _identity;
            if (identity == null)
                return FailStart(result, "identity-missing", null);

            string identityError;
            string identityDetail;
            if (!identity.TryReverify(out identityError, out identityDetail))
                return FailStart(result, identityError, identityDetail);

            // 3) 命令构造（冻结配置 → 命令行）。
            string arguments;
            string commandError;
            string commandDetail;
            if (!FfmpegVideoCommand.TryBuildEncodeArguments(_settings, _tempPath, out arguments, out commandError, out commandDetail))
                return FailStart(result, commandError, commandDetail);

            result.Arguments = arguments;

            // 4) 启动编码进程。
            Process process = null;
            try
            {
                string workingDirectory;
                try
                {
                    workingDirectory = Path.GetDirectoryName(identity.ExecutablePath) ?? string.Empty;
                }
                catch (Exception)
                {
                    workingDirectory = string.Empty;
                }

                process = _processStart != null
                    ? _processStart(identity.ExecutablePath, arguments, workingDirectory)
                    : StartDefaultProcess(identity.ExecutablePath, arguments, workingDirectory);
            }
            catch (Exception ex)
            {
                return FailStart(result, "process-start-failed", ex.Message);
            }

            if (process == null)
                return FailStart(result, "process-start-failed", "process-start-returned-null");

            // 进程 ownership 在 Process.Start 成功之后**立即**登记，
            // 先于任何可能抛异常的初始化步骤（stdin/stdout/stderr/exit 等待任务）。
            lock (_gate)
            {
                _process = process;
                _processStarted = true;
            }

            // 5) 管道与退出等待的初始化。
            //
            // Ownership 原则：**任何资源或任务一旦成功创建/启动，就立即登记到 pipeline ownership，
            // 然后才执行下一步可能失败的初始化。** 绝不先启动多个异步任务再批量登记 ——
            // 否则"已经启动但尚未登记"的窗口会让 CleanupTask 无法等待它，
            // 进而可能在后台读取任务仍在使用管道时提前 Dispose 进程。
            try
            {
                Stream stdin = process.StandardInput.BaseStream;
                lock (_gate)
                    _stdin = stdin;

                Task exitTask = WaitForExitAsync(process);
                lock (_gate)
                    _exitTask = exitTask;

                Stream stdoutStream = WrapStream(process.StandardOutput.BaseStream, "stdout");
                Task<Exception> stdoutPump = FfmpegStreamPump.Start(stdoutStream, _stdoutText);
                lock (_gate)
                    _stdoutPump = stdoutPump;

                Stream stderrStream = WrapStream(process.StandardError.BaseStream, "stderr");
                Task<Exception> stderrPump = FfmpegStreamPump.Start(stderrStream, _stderrText);
                lock (_gate)
                    _stderrPump = stderrPump;

                lock (_gate)
                {
                    if (_state == FfmpegVideoPipelineState.NotStarted)
                        _state = FfmpegVideoPipelineState.Running;
                }
            }
            catch (Exception ex)
            {
                // 到这里为止已经登记的资源（进程、stdin、退出任务、可能已启动的 stdout pump）
                // 全部仍归本会话所有，由收敛任务负责等待与回收。
                lock (_gate)
                {
                    // 绝不覆盖已经赢得的终态（例如启动期间取消）。
                    if (_state == FfmpegVideoPipelineState.NotStarted)
                    {
                        _state = FfmpegVideoPipelineState.Failed;
                        _failureCode = "process-init-failed";
                        _failureDetail = ex.Message;
                    }
                }

                // 已经启动的进程/任务必须有真正的回收路径，而不是只把异常抛给调用方。
                StartConvergence(() => ConvergeFailure());

                result.ErrorCode = "process-init-failed";
                result.ErrorDetail = ex.Message;
                return result;
            }

            if (State == FfmpegVideoPipelineState.Failed)
            {
                // 进程已启动但管道不可用：必须有真正的回收，而不是"完成"的假象。
                StartConvergence(() => ConvergeFailure());
                result.ErrorCode = _failureCode;
                result.ErrorDetail = _failureDetail;
                return result;
            }

            if (State != FfmpegVideoPipelineState.Running)
            {
                // 启动期间已被取消：由取消方负责回收，这里不触碰任何资源。
                result.ErrorCode = State == FfmpegVideoPipelineState.Cancelled ? "cancelled-during-start" : _failureCode;
                result.ErrorDetail = _failureDetail;
                return result;
            }

            result.Started = true;
            return result;
        }

        /// <summary>故障注入缝：只在配置了 wrapper 时包装（生产恒为 null）。</summary>
        private Stream WrapStream(Stream stream, string role)
        {
            return _streamWrapper != null ? _streamWrapper(stream, role) : stream;
        }

        /// <summary>
        /// 启动阶段失败：锁定失败结论，**并让收敛任务回收已经建立的资源**
        /// （进程 ownership、临时文件 ownership 句柄与已创建的临时文件）。
        /// 绝不把"已经创建过资源"的失败伪装成"什么都没发生"。
        /// </summary>
        private FfmpegVideoStartResult FailStart(FfmpegVideoStartResult result, string errorCode, string errorDetail)
        {
            ClaimFailure(errorCode, errorDetail);
            StartConvergence(() => ConvergeFailure());

            result.ErrorCode = errorCode;
            result.ErrorDetail = errorDetail;
            return result;
        }

        private bool TryPreparePaths(out string errorCode, out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;

            string finalPath = _configuredFinalPath;
            if (string.IsNullOrWhiteSpace(finalPath) || !Path.IsPathRooted(finalPath))
            {
                errorCode = "final-path-not-absolute";
                errorDetail = finalPath;
                return false;
            }

            string directory;
            try
            {
                finalPath = Path.GetFullPath(finalPath);
                directory = Path.GetDirectoryName(finalPath);
            }
            catch (Exception ex)
            {
                errorCode = "final-path-invalid";
                errorDetail = ex.Message;
                return false;
            }

            if (string.IsNullOrEmpty(directory))
            {
                errorCode = "final-path-invalid";
                return false;
            }

            if (!Directory.Exists(directory))
            {
                errorCode = "output-directory-not-found";
                errorDetail = directory;
                return false;
            }

            // 已有正式目标文件绝不删除或覆盖。
            if (File.Exists(finalPath))
            {
                errorCode = "final-file-exists";
                errorDetail = finalPath;
                return false;
            }

            string tempPath = _configuredTempPath;
            if (string.IsNullOrWhiteSpace(tempPath))
            {
                string token = Guid.NewGuid().ToString("N").Substring(0, 16);
                string tokenError;
                if (!FfmpegVideoCommand.TryBuildTempPath(finalPath, token, out tempPath, out tokenError))
                {
                    errorCode = tokenError;
                    return false;
                }
            }
            else
            {
                try
                {
                    tempPath = Path.GetFullPath(tempPath);
                }
                catch (Exception ex)
                {
                    errorCode = "temp-path-invalid";
                    errorDetail = ex.Message;
                    return false;
                }

                // 临时文件必须与最终文件同目录（同卷），发布才能用一次 Move 完成。
                string tempDirectory = Path.GetDirectoryName(tempPath);
                if (!string.Equals(tempDirectory, directory, StringComparison.OrdinalIgnoreCase))
                {
                    errorCode = "temp-path-not-sibling";
                    errorDetail = tempPath;
                    return false;
                }
            }

            // 原子取得临时路径 ownership：CreateNew 要么创建成功，要么因为路径已被占用而失败。
            // 不存在"先检查、后由 FFmpeg 创建"的窗口，因此绝不可能覆盖无法证明属于本会话的文件。
            //
            // 句柄以 FileShare.ReadWrite（**不含** FileShare.Delete）保持打开：FFmpeg 仍可正常
            // 以 -y 打开并覆写本会话自己的这个文件，但其他任何一方都无法删除或改名覆盖它，
            // 因此从创建到 FFmpeg 打开、直到发布/清理之前，路径始终绑定到本会话创建的这个文件。
            FileStream ownership = null;
            try
            {
                ownership = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (Exception ex)
            {
                errorCode = "temp-file-exists";
                errorDetail = tempPath + " (" + ex.GetType().Name + ": " + ex.Message + ")";
                if (ownership != null)
                {
                    try
                    {
                        ownership.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
                return false;
            }

            _finalPath = finalPath;
            _tempPath = tempPath;
            _tempOwnership = ownership;
            _tempOwned = true;
            return true;
        }

        /// <summary>
        /// 释放临时产物的 ownership 句柄（幂等）。发布（Move）与清理（Delete）都需要它先释放：
        /// 二者都要求对该文件的 DELETE 权限，而本会话的句柄刻意不共享 DELETE。
        /// 释放之后到 Move/Delete 完成之间存在一个极短窗口，剩余风险见 §2.8 的说明。
        /// </summary>
        private void ReleaseTempOwnership()
        {
            FileStream ownership;
            lock (_gate)
            {
                ownership = _tempOwnership;
                _tempOwnership = null;
                _tempOwnershipReleased = true;
            }

            if (ownership != null)
            {
                try
                {
                    ownership.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private static Process StartDefaultProcess(string executablePath, string arguments, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory ?? string.Empty,
            };

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            return process;
        }

        // ==================================================================== frame protocol

        /// <summary>
        /// 提交一帧。**接收前**校验累计长度；长度不符 / 已有在途帧 / 非 Running 一律立即拒绝且不排队。
        /// </summary>
        public FfmpegFrameWriteAttempt TryWriteFrame(FfmpegVideoFrame frame)
        {
            lock (_gate)
            {
                if (_state != FfmpegVideoPipelineState.Running)
                    return FfmpegFrameWriteAttempt.Rejected("not-running", "state=" + _state);

                if (_poisoned)
                    return FfmpegFrameWriteAttempt.Rejected("pipe-poisoned", _failureDetail);

                if (frame == null)
                    return FfmpegFrameWriteAttempt.Rejected("frame-null", null);

                if (frame.Length != _frameLengthBytes)
                {
                    return FfmpegFrameWriteAttempt.Rejected("frame-length-mismatch",
                        "expected=" + _frameLengthBytes.ToString(CultureInfo.InvariantCulture) +
                        " actual=" + frame.Length.ToString(CultureInfo.InvariantCulture));
                }

                if (Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0)
                    return FfmpegFrameWriteAttempt.Rejected("busy", "another frame write is in flight");
            }

            Stream stdin = _stdin;
            if (stdin == null)
            {
                Interlocked.Exchange(ref _writeInFlight, 0);
                return FfmpegFrameWriteAttempt.Rejected("not-running", "stdin-unavailable");
            }

            Task<FfmpegFrameWriteResult> completion = Task.Run(() => WriteFrameCore(frame, stdin));
            return FfmpegFrameWriteAttempt.Accept(completion);
        }

        private FfmpegFrameWriteResult WriteFrameCore(FfmpegVideoFrame frame, Stream stdin)
        {
            long written = 0;
            Exception failure = null;

            try
            {
                IReadOnlyList<FfmpegFrameSegment> segments = frame.Segments;
                for (int i = 0; i < segments.Count; i++)
                {
                    FfmpegFrameSegment segment = segments[i];
                    if (segment.Count == 0)
                        continue;

                    // 直接写调用方的数组：已独占的数据不额外复制。
                    stdin.WriteAsync(segment.Buffer, segment.Offset, segment.Count, _cancellation.Token)
                        .GetAwaiter().GetResult();
                    written += segment.Count;
                }

                stdin.FlushAsync(_cancellation.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // 故障注入缝：字节已全部写出、结果尚未提交到这个位置。
            // 修复后，在途标记由下面的同一个临界区持有并释放，因此这里必须仍然"在途"。
            if (_writeCommitBarrier != null)
                _writeCommitBarrier();

            // **写入结果提交与在途标记释放在同一个临界区内完成**：
            // FinishAsync 拿到的只能是完整的写入终态 —— 要么在途（拒绝 Finish），
            // 要么已计数 / 已锁定失败。绝不存在"已写出但未计数"或"已失败但未锁定"的窗口。
            bool startFailureConvergence = false;
            FfmpegFrameWriteResult result;

            lock (_gate)
            {
                try
                {
                    if (failure != null)
                    {
                        result = CommitWriteFailureLocked(frame, written, failure, out startFailureConvergence);
                    }
                    else if (_state != FfmpegVideoPipelineState.Running)
                    {
                        result = new FfmpegFrameWriteResult
                        {
                            Success = false,
                            ErrorCode = _state == FfmpegVideoPipelineState.Cancelled ? "cancelled" : "not-running",
                            ErrorDetail = "state=" + _state,
                            DeliveredFrameCount = _deliveredFrames,
                        };
                    }
                    else
                    {
                        // 完整写入成功之后才增加交付帧计数。
                        _deliveredFrames++;
                        result = new FfmpegFrameWriteResult
                        {
                            Success = true,
                            DeliveredFrameCount = _deliveredFrames,
                        };
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _writeInFlight, 0);
                    Monitor.PulseAll(_gate);
                }
            }

            if (startFailureConvergence)
                StartConvergence(() => ConvergeFailure());

            return result;
        }

        /// <summary>
        /// 在写入提交临界区内锁定失败结果（调用方必须已持有 <see cref="_gate"/>）。
        /// 部分写入不得在同一个 stdin 上重试，因此这里同时污染管道。
        /// </summary>
        private FfmpegFrameWriteResult CommitWriteFailureLocked(
            FfmpegVideoFrame frame, long written, Exception ex, out bool startConvergence)
        {
            startConvergence = false;

            // 部分写入不得在同一个 stdin 上重试。
            _poisoned = true;

            bool cancelled = _state == FfmpegVideoPipelineState.Cancelled;
            if (!cancelled && _state == FfmpegVideoPipelineState.Running)
            {
                _state = FfmpegVideoPipelineState.Failed;
                _failureCode = "write-failed";
                _failureDetail = "written=" + written.ToString(CultureInfo.InvariantCulture) + "/" +
                                 frame.Length.ToString(CultureInfo.InvariantCulture) + " ex=" + ex.Message;
                startConvergence = true;
            }

            return new FfmpegFrameWriteResult
            {
                Success = false,
                ErrorCode = cancelled ? "cancelled" : "write-failed",
                ErrorDetail = ex.Message,
                DeliveredFrameCount = _deliveredFrames,
            };
        }

        // ==================================================================== finish / cancel

        /// <summary>
        /// 结束输入并等待产物就绪：关闭 stdin → 等待进程退出与两个流 EOF → 核验 → 发布。
        /// 返回的任务在后台线程完成，调用方 await 即可（绝不阻塞 Unity 主线程）。
        /// </summary>
        public Task<FfmpegVideoOutcome> FinishAsync()
        {
            lock (_gate)
            {
                if (_state == FfmpegVideoPipelineState.Completed)
                    return Task.FromResult(BuildOutcome());

                if (_state == FfmpegVideoPipelineState.Cancelled || _state == FfmpegVideoPipelineState.Failed)
                    return _convergence ?? Task.FromResult(BuildOutcome());

                if (_state == FfmpegVideoPipelineState.Finalizing)
                    return _convergence;

                if (_state != FfmpegVideoPipelineState.Running)
                    return Task.FromResult(BuildFailure("not-running", "state=" + _state));

                // 防御性检查：管道一旦被污染（部分写入失败），绝不允许进入 Finalizing。
                if (_poisoned)
                    return Task.FromResult(BuildFailure("pipe-poisoned", _failureDetail));

                if (Volatile.Read(ref _writeInFlight) != 0)
                    return Task.FromResult(BuildFailure("write-in-flight", "await the pending frame write first"));

                _state = FfmpegVideoPipelineState.Finalizing;
                _convergence = Task.Run(() => FinalizeCore());
                return _convergence;
            }
        }

        /// <summary>
        /// 请求取消。**不同步等待进程退出**：立即锁定终态并解除阻塞写入（终止进程），
        /// 收敛在 <see cref="CleanupTask"/> 上继续。
        /// </summary>
        public bool Cancel(string reason)
        {
            Process process;

            lock (_gate)
            {
                if (_state == FfmpegVideoPipelineState.Completed ||
                    _state == FfmpegVideoPipelineState.Cancelled ||
                    _state == FfmpegVideoPipelineState.Failed)
                {
                    return false;
                }

                _state = FfmpegVideoPipelineState.Cancelled;
                _cancelReason = reason ?? "cancel";
                process = _process;

                try
                {
                    _cancellation.Cancel();
                }
                catch (Exception)
                {
                }

                if (_convergence == null)
                    _convergence = Task.Run(() => ConvergeCancel());
            }

            // 主动解除可能被阻塞的写入：终止进程会让对端管道关闭。
            // stdin 的关闭由收敛任务负责（可能被阻塞的写入正在使用它）。
            if (process != null)
                TryKill(process, out _);

            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            Cancel("disposed");
        }

        // ==================================================================== convergence

        private void StartConvergence(Func<FfmpegVideoOutcome> body)
        {
            lock (_gate)
            {
                if (_convergence == null)
                    _convergence = Task.Run(body);
            }

            // 失败收敛同样需要主动解除阻塞写入。
            Process process;
            lock (_gate)
                process = _process;

            if (process != null)
                TryKill(process, out _);
        }

        private FfmpegVideoPipelineState ClaimFailure(string errorCode, string errorDetail)
        {
            lock (_gate)
            {
                _failureCode = errorCode;
                _failureDetail = errorDetail;

                if (_state == FfmpegVideoPipelineState.NotStarted)
                    _state = FfmpegVideoPipelineState.Failed;
            }

            return State;
        }

        private FfmpegVideoOutcome FinalizeCore()
        {
            // 1) 正常关闭 stdin（EOF），让编码器把 trailer 写完。
            try
            {
                Stream stdin;
                lock (_gate)
                    stdin = _stdin;

                if (stdin != null)
                {
                    try
                    {
                        stdin.Flush();
                    }
                    catch (Exception)
                    {
                    }

                    stdin.Dispose();
                }

                lock (_gate)
                    _stdin = null;
            }
            catch (Exception ex)
            {
                return Fail("stdin-close-failed", ex.Message);
            }

            // 2) 进程退出 + stdout/stderr 都到 EOF（Exited ≠ 日志已排空；读取异常 ≠ EOF）。
            int exitCode;
            string waitError;
            if (!TryAwaitProcessAndDrains(out exitCode, out waitError))
                return Fail("drain-failed", waitError);

            lock (_gate)
                _encoderExitCode = exitCode;

            if (exitCode != 0)
            {
                return Fail("encoder-exit-nonzero",
                    "exit=" + exitCode.ToString(CultureInfo.InvariantCulture) +
                    " stderr=" + Flatten(_stderrText.Tail, 400) +
                    (string.IsNullOrEmpty(_stdoutText.Tail) ? string.Empty : " stdout=" + Flatten(_stdoutText.Tail, 200)));
            }

            // 进程已退出、两个流都已到真实 EOF：正常完成路径同样必须释放 Process 及其流，
            // 而不是把句柄留给 GC。释放失败只作为可观察的 residual 记录，不影响 Completed 语义。
            string releaseError = ReleaseProcessResources();
            if (releaseError != null)
                lock (_gate)
                    _releaseDetail = Append(_releaseDetail, releaseError);

            // 3) 独立核验：只有实际可解码帧数 / 尺寸 / 有理时间基 / PTS 全部匹配才算产物有效。
            var request = new FfmpegVideoVerificationRequest
            {
                ExecutablePath = _identity != null ? _identity.ExecutablePath : null,
                VideoPath = _tempPath,
                ExpectedWidth = _settings.Width,
                ExpectedHeight = _settings.Height,
                ExpectedFps = _settings.Fps,
                ExpectedCodecName = _expectedCodecName,
                ExpectedPixelFormat = FfmpegVideoPixelFormatPolicy.ToToken(_settings.ResolvePixelFormat()),
                ExpectedFrameCount = DeliveredFrameCount,
                CancellationToken = _cancellation.Token,
            };

            FfmpegVideoVerificationResult verification;
            try
            {
                verification = _verifier.VerifyAsync(request).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return Fail("verification-threw", ex.Message);
            }

            lock (_gate)
            {
                if (_state != FfmpegVideoPipelineState.Finalizing)
                {
                    // 取消（或失败）在核验期间赢得终态。本任务就是唯一的收敛 owner，
                    // 因此必须由这里完成回收 —— 绝不留给一个从未被创建的收敛任务。
                    var raced = ConvergeByState();
                    raced.Verification = verification;
                    return raced;
                }
            }

            if (verification.Status == FfmpegVideoVerificationStatus.Cancelled)
            {
                // 核验自身报告取消（例如 token 被取消）：本任务仍是唯一收敛 owner，
                // 必须由这里完成回收。
                Cancel("cancelled-during-finalize");
                return ConvergeByState();
            }

            if (!verification.IsVerified)
            {
                var failed = Fail("verification-failed",
                    verification.ErrorCode + " " + (verification.ErrorDetail ?? string.Empty));
                failed.Verification = verification;
                return failed;
            }

            // 4) 安全发布：同卷 Move，且绝不覆盖已存在的正式目标文件。
            FfmpegVideoOutcome outcome;
            lock (_gate)
            {
                if (_state != FfmpegVideoPipelineState.Finalizing)
                {
                    var raced = ConvergeByState();
                    raced.Verification = verification;
                    return raced;
                }

                if (File.Exists(_finalPath))
                {
                    _failureCode = "final-file-exists";
                    _failureDetail = _finalPath;
                    _state = FfmpegVideoPipelineState.Failed;
                    outcome = null;
                }
                else if (!File.Exists(_tempPath))
                {
                    // 临时路径上不再是一个普通文件（例如被换成了目录）：
                    // 绝不用 Move 把非产物发布成正式输出。
                    _failureCode = "temp-file-missing";
                    _failureDetail = _tempPath;
                    _state = FfmpegVideoPipelineState.Failed;
                    outcome = null;
                }
                else
                {
                    try
                    {
                        // Move 需要源文件的 DELETE 权限，而 ownership 句柄刻意不共享 DELETE：
                        // 必须先释放句柄再发布（剩余窗口见 §2.8）。
                        ReleaseTempOwnership();
                        File.Move(_tempPath, _finalPath);
                        _tempOwned = false;
                        _state = FfmpegVideoPipelineState.Completed;
                        outcome = BuildOutcome();
                    }
                    catch (Exception ex)
                    {
                        _failureCode = "publish-failed";
                        _failureDetail = ex.Message;
                        _state = FfmpegVideoPipelineState.Failed;
                        outcome = null;
                    }
                }
            }

            if (outcome != null)
            {
                outcome.Verification = verification;
                return outcome;
            }

            var publishFailure = ConvergeFailure();
            publishFailure.Verification = verification;
            return publishFailure;
        }

        private FfmpegVideoOutcome Fail(string errorCode, string errorDetail)
        {
            lock (_gate)
            {
                if (_state == FfmpegVideoPipelineState.Finalizing || _state == FfmpegVideoPipelineState.Running)
                {
                    _state = FfmpegVideoPipelineState.Failed;
                    _failureCode = errorCode;
                    _failureDetail = errorDetail;
                }
            }

            return ConvergeByState();
        }

        /// <summary>
        /// 由**当前收敛任务 owner**按已赢得的终态完成回收。
        /// 只有 owner 可以调用：终态已经确定，但资源可能还没被回收。
        /// </summary>
        private FfmpegVideoOutcome ConvergeByState()
        {
            FfmpegVideoPipelineState state = State;

            if (state == FfmpegVideoPipelineState.Cancelled)
                return ConvergeCancel();

            if (state == FfmpegVideoPipelineState.Failed)
                return ConvergeFailure();

            // Completed（已发布）或仍在进行中：没有需要回收的东西。
            return BuildOutcome();
        }

        private FfmpegVideoOutcome ConvergeCancel()
        {
            lock (_gate)
            {
                while (_starting)
                    Monitor.Wait(_gate);
            }

            TeardownOutcome teardown = Teardown();

            lock (_gate)
            {
                var outcome = BuildOutcome();
                outcome.State = FfmpegVideoPipelineState.Cancelled;
                outcome.Reason = _cancelReason;
                ApplyTeardown(outcome, teardown);

                if (teardown.Error != null)
                {
                    outcome.ResidualOwnership = true;
                }

                if (teardown.TempStillPresent)
                {
                    outcome.ResidualOwnership = true;
                    outcome.ResidualDetail = Append(outcome.ResidualDetail, "temp-file-not-removed:" + _tempPath);
                }

                return outcome;
            }
        }

        private FfmpegVideoOutcome ConvergeFailure()
        {
            lock (_gate)
            {
                while (_starting)
                    Monitor.Wait(_gate);
            }

            TeardownOutcome teardown = Teardown();

            lock (_gate)
            {
                var outcome = BuildOutcome();
                outcome.State = FfmpegVideoPipelineState.Failed;
                outcome.ErrorCode = _failureCode ?? "failed";
                outcome.ErrorDetail = _failureDetail;
                ApplyTeardown(outcome, teardown);

                if (teardown.Error != null)
                {
                    outcome.ResidualOwnership = true;
                }

                if (teardown.TempStillPresent)
                {
                    outcome.ResidualOwnership = true;
                    outcome.ResidualDetail = Append(outcome.ResidualDetail, "temp-file-not-removed:" + _tempPath);
                }

                return outcome;
            }
        }

        private sealed class TeardownOutcome
        {
            public bool TempStillPresent;
            public string Error;
        }

        /// <summary>
        /// 唯一的资源回收路径：终止并回收进程 → 关闭 stdin → 等待退出与两个流 EOF →
        /// 删除**本会话拥有的**临时文件。绝不动正式目标文件。
        /// </summary>
        private TeardownOutcome Teardown()
        {
            var teardown = new TeardownOutcome();

            Process process;
            Stream stdin;
            Task exitTask;
            Task<Exception> stdoutPump;
            Task<Exception> stderrPump;

            lock (_gate)
            {
                process = _process;
                stdin = _stdin;
                exitTask = _exitTask;
                stdoutPump = _stdoutPump;
                stderrPump = _stderrPump;
            }

            if (process != null)
            {
                bool killError;
                TryKill(process, out killError);
                if (killError)
                {
                    _processKillFailed = true;
                    teardown.Error = Append(teardown.Error, "process-kill-failed");
                }
            }

            if (stdin != null)
            {
                try
                {
                    stdin.Dispose();
                }
                catch (Exception)
                {
                }

                lock (_gate)
                    _stdin = null;
            }

            // 初始化阶段可能还没建立退出等待任务：这里补建，确保**真正等待**进程退出，
            // 而不是依赖一次 HasExited 读数。
            if (process != null && exitTask == null)
            {
                try
                {
                    exitTask = WaitForExitAsync(process);
                }
                catch (Exception ex)
                {
                    teardown.Error = Append(teardown.Error, "exit-wait-create-failed:" + ex.Message);
                }
            }

            if (exitTask != null)
            {
                try
                {
                    exitTask.Wait();
                }
                catch (Exception ex)
                {
                    teardown.Error = Append(teardown.Error, "exit-wait-failed:" + ex.Message);
                }
            }

            if (stdoutPump != null || stderrPump != null)
            {
                try
                {
                    var pumps = new List<Task>(2);
                    if (stdoutPump != null)
                        pumps.Add(stdoutPump);
                    if (stderrPump != null)
                        pumps.Add(stderrPump);
                    Task.WaitAll(pumps.ToArray());
                }
                catch (Exception ex)
                {
                    teardown.Error = Append(teardown.Error, "drain-failed:" + ex.Message);
                }

                teardown.Error = Append(teardown.Error, DescribePumpFailure("stdout", stdoutPump));
                teardown.Error = Append(teardown.Error, DescribePumpFailure("stderr", stderrPump));
            }

            // 两个流都已结束（或已失败）之后才允许 Dispose 进程对象。
            string releaseError = ReleaseProcessResources();
            if (releaseError != null)
            {
                lock (_gate)
                    _releaseDetail = Append(_releaseDetail, releaseError);
                teardown.Error = Append(teardown.Error, releaseError);
            }

            bool ownsTemp;
            string tempPath;
            lock (_gate)
            {
                ownsTemp = _tempOwned;
                tempPath = _tempPath;
            }

            if (ownsTemp && !string.IsNullOrEmpty(tempPath))
            {
                // 删除前必须释放 ownership 句柄：它刻意不共享 DELETE。
                ReleaseTempOwnership();

                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception ex)
                    {
                        teardown.Error = Append(teardown.Error, "temp-delete-failed:" + ex.Message);
                    }
                }

                if (File.Exists(tempPath))
                {
                    teardown.TempStillPresent = true;
                    teardown.Error = Append(teardown.Error, "temp-file-not-removed");
                }
                else if (Directory.Exists(tempPath))
                {
                    // 临时路径上出现的是我们没有创建、也不该删除的东西：如实报告占用，不伪装成 clean。
                    teardown.TempStillPresent = true;
                    teardown.Error = Append(teardown.Error, "temp-path-not-a-file");
                }
                else
                {
                    lock (_gate)
                        _tempOwned = false;
                }
            }
            else
            {
                ReleaseTempOwnership();
            }

            return teardown;
        }

        /// <summary>把排空泵的结果翻译成可观察的细节；正常 EOF（null）不产生任何内容。</summary>
        private static string DescribePumpFailure(string role, Task<Exception> pump)
        {
            if (pump == null)
                return null;

            try
            {
                if (pump.IsFaulted)
                {
                    Exception inner = pump.Exception != null ? pump.Exception.GetBaseException() : null;
                    return role + "-drain-failed:" + (inner != null ? inner.Message : "unknown");
                }

                Exception result = pump.Result;
                return result != null ? role + "-drain-failed:" + result.Message : null;
            }
            catch (Exception ex)
            {
                return role + "-drain-failed:" + ex.Message;
            }
        }

        private static void ApplyTeardown(FfmpegVideoOutcome outcome, TeardownOutcome teardown)
        {
            outcome.ResidualDetail = Append(outcome.ResidualDetail, teardown.Error);
        }

        private bool TryAwaitProcessAndDrains(out int exitCode, out string error)
        {
            exitCode = int.MinValue;
            error = null;

            Task exitTask;
            Task<Exception> stdoutPump;
            Task<Exception> stderrPump;
            Process process;

            lock (_gate)
            {
                exitTask = _exitTask;
                stdoutPump = _stdoutPump;
                stderrPump = _stderrPump;
                process = _process;
            }

            try
            {
                if (exitTask != null)
                    exitTask.Wait();
            }
            catch (Exception ex)
            {
                error = "exit-wait-failed: " + ex.Message;
                return false;
            }

            try
            {
                var pumps = new List<Task>(2);
                if (stdoutPump != null)
                    pumps.Add(stdoutPump);
                if (stderrPump != null)
                    pumps.Add(stderrPump);
                if (pumps.Count > 0)
                    Task.WaitAll(pumps.ToArray());
            }
            catch (Exception ex)
            {
                error = "drain-failed: " + ex.Message;
                return false;
            }

            // **真实 EOF 与读取异常必须区分**：读取异常绝不能被当成"日志已排空"，
            // 也绝不允许在排空失败之后发布正式产物。
            string stdoutError = DescribePumpFailure("stdout", stdoutPump);
            if (stdoutError != null)
            {
                error = stdoutError;
                return false;
            }

            string stderrError = DescribePumpFailure("stderr", stderrPump);
            if (stderrError != null)
            {
                error = stderrError;
                return false;
            }

            try
            {
                exitCode = process != null ? process.ExitCode : int.MinValue;
            }
            catch (Exception ex)
            {
                error = "exit-code-unavailable: " + ex.Message;
                return false;
            }

            return true;
        }

        private FfmpegVideoOutcome BuildOutcome()
        {
            var outcome = new FfmpegVideoOutcome
            {
                State = _state,
                TempPath = _tempPath,
                DeliveredFrameCount = _deliveredFrames,
                EncoderExitCode = _encoderExitCode,
                ProcessStarted = _processStarted,
                ProcessReaped = _processReaped,
                ProcessDisposed = _processDisposed,
                TempOwnershipReleased = _tempOwnershipReleased,
                ErrorCode = _failureCode,
                ErrorDetail = _failureDetail,
                Reason = _cancelReason,
            };

            if (_state == FfmpegVideoPipelineState.Completed)
                outcome.FinalPath = _finalPath;

            if (_tempPath == null || !_tempOwned || !File.Exists(_tempPath))
                outcome.TempFileRemoved = true;

            if (_process != null && !_processReaped)
            {
                outcome.ResidualOwnership = true;
                outcome.ResidualDetail = Append(outcome.ResidualDetail, "process-not-reaped");
            }

            if (_processKillFailed)
            {
                outcome.ResidualOwnership = true;
                outcome.ResidualDetail = Append(outcome.ResidualDetail, "process-kill-failed");
            }

            // 释放阶段的可观察失败（例如 Process.Dispose 抛异常、ownership 句柄未释放）如实上报。
            if (!string.IsNullOrEmpty(_releaseDetail))
            {
                outcome.ResidualOwnership = true;
                outcome.ResidualDetail = Append(outcome.ResidualDetail, _releaseDetail);
            }

            return outcome;
        }

        private FfmpegVideoOutcome BuildFailure(string errorCode, string errorDetail)
        {
            var outcome = BuildOutcome();
            outcome.State = FfmpegVideoPipelineState.Failed;
            outcome.ErrorCode = errorCode;
            outcome.ErrorDetail = errorDetail;
            return outcome;
        }

        private static string Append(string existing, string value)
        {
            if (string.IsNullOrEmpty(value))
                return existing;

            return string.IsNullOrEmpty(existing) ? value : existing + "; " + value;
        }

        private static string Flatten(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }

        // ==================================================================== process plumbing

        /// <summary>
        /// 终止（如仍存活）→ 等待真实退出 → 释放 Process 对象及其流。幂等。
        ///
        /// 不变量：
        ///   * 只要还持有进程对象，就必须等到它真正退出；绝不"Kill 之后读一次 HasExited 就丢弃对象"。
        ///   * 只在调用方已经等待过 stdout/stderr 之后才调用（流仍被后台读取时不得 Dispose）。
        ///   * 返回 null 表示干净释放；否则返回可观察的失败细节（不伪装成 clean）。
        /// </summary>
        private string ReleaseProcessResources()
        {
            Process process;
            lock (_gate)
            {
                process = _process;
                _process = null;
            }

            if (process == null)
                return null;

            string error = null;

            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch (Exception)
            {
                exited = false;
            }

            if (!exited)
            {
                bool killFailed;
                TryKill(process, out killFailed);
                if (killFailed)
                {
                    _processKillFailed = true;
                    error = Append(error, "process-kill-failed");
                }

                try
                {
                    process.WaitForExit();
                }
                catch (Exception ex)
                {
                    error = Append(error, "exit-wait-failed:" + ex.Message);
                }
            }

            try
            {
                int code = process.ExitCode;
                lock (_gate)
                    _encoderExitCode = _encoderExitCode ?? code;
                _processReaped = true;
            }
            catch (Exception ex)
            {
                _processReaped = false;
                error = Append(error, "exit-code-unavailable:" + ex.Message);
            }

            try
            {
                process.Dispose();
                _processDisposed = true;
            }
            catch (Exception ex)
            {
                error = Append(error, "process-dispose-failed:" + ex.Message);
            }

            return error;
        }

        private static bool TryKill(Process process, out bool failed)        {
            failed = false;
            if (process == null)
                return false;

            try
            {
                if (!process.HasExited)
                    process.Kill();
                return true;
            }
            catch (Exception)
            {
                // 进程可能刚好自己退出：只有在确认仍未退出时才把它当作真实的 kill 失败。
                try
                {
                    if (process.HasExited)
                        return true;
                }
                catch (Exception)
                {
                }

                failed = true;
                return false;
            }
        }

        private static Task WaitForExitAsync(Process process)
        {
            var completion = new TaskCompletionSource<int>();
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) =>
            {
                try
                {
                    completion.TrySetResult(process.ExitCode);
                }
                catch (Exception)
                {
                    completion.TrySetResult(int.MinValue);
                }
            };

            try
            {
                if (process.HasExited)
                    completion.TrySetResult(process.ExitCode);
            }
            catch (Exception)
            {
                // 进程可能刚好退出；Exited 事件仍会完成该任务。
            }

            return completion.Task;
        }
    }

    /// <summary>
    /// stdout / stderr 的排空泵（L2 内部）：**区分真实 EOF 与读取异常**。
    ///
    /// 返回的任务在正常 EOF 时结果为 <c>null</c>；读取失败时结果为该异常。
    /// 调用方只有确认两个泵都以 <c>null</c> 结束时，才能认为日志已经排空到 EOF ——
    /// 排空失败绝不允许进入 Completed，也绝不允许发布产物。
    ///
    /// 诊断缓冲只是**有界证据**，不是读取上限：缓冲饱和后循环仍继续排空实际流。
    /// </summary>
    internal static class FfmpegStreamPump
    {
        public static Task<Exception> Start(Stream stream, BoundedTextCollector collector)
        {
            if (stream == null)
                return Task.FromResult<Exception>(null);

            return Task.Run(async () =>
            {
                try
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, false))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            collector.FeedLine(line);
                    }

                    return (Exception)null;
                }
                catch (Exception ex)
                {
                    // 真实 EOF 与读取失败必须区分：异常向上传递，由调用方决定终态。
                    return ex;
                }
            });
        }
    }
}