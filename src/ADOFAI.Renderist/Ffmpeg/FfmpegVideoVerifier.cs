using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ADOFAI.Renderist.Ffmpeg
{
    internal enum FfmpegVideoVerificationStatus
    {
        Verified = 0,
        Failed = 1,
        Cancelled = 2,
    }

    /// <summary>
    /// 一次视频产物核验的请求。期望值全部来自会话开始时冻结的配置与"已完整写入的帧数"，
    /// 不使用容器自报的 nb_frames，也不把 progress=end 当作证据。
    /// </summary>
    internal sealed class FfmpegVideoVerificationRequest
    {
        /// <summary>用于核验的 FFmpeg 可执行文件（与编码使用同一份冻结身份）。</summary>
        public string ExecutablePath { get; set; }

        /// <summary>待核验的视频文件（会话独占的临时产物）。</summary>
        public string VideoPath { get; set; }

        public int ExpectedWidth { get; set; }
        public int ExpectedHeight { get; set; }
        public int ExpectedFps { get; set; }

        /// <summary>期望的压缩流编解码器名（libx264 → "h264"）。null = 不检查。</summary>
        public string ExpectedCodecName { get; set; }

        /// <summary>期望的像素格式 token（yuv420p / yuv444p）。null = 不检查。</summary>
        public string ExpectedPixelFormat { get; set; }

        /// <summary>已完整写入 FFmpeg stdin 的帧数：解码帧数与容器包数都必须精确等于它。</summary>
        public long ExpectedFrameCount { get; set; }

        public CancellationToken CancellationToken { get; set; }
    }

    /// <summary>
    /// 视频产物核验结果：可机读的实测值 + 有界文本证据。
    /// </summary>
    internal sealed class FfmpegVideoVerificationResult
    {
        public FfmpegVideoVerificationStatus Status { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }

        // ---- 解码 pass（真实解码语义）----
        public string StreamLine { get; set; }
        public string CodecName { get; set; }
        public string PixelFormat { get; set; }
        public int VideoStreamCount { get; set; }
        public bool HasStreamLine { get; set; }
        public int? DecodedWidth { get; set; }
        public int? DecodedHeight { get; set; }
        public bool HasTimeBase { get; set; }
        public int TimeBaseNumerator { get; set; }
        public int TimeBaseDenominator { get; set; }
        public long DecodedFrameCount { get; set; }
        public long FirstDecodedPts { get; set; }
        public long LastDecodedPts { get; set; }
        public long FirstDecodedTimingMismatchIndex { get; set; }
        public string FirstDecodedTimingMismatchDetail { get; set; }
        public int DecodeExitCode { get; set; }
        public string DecodeStdErrTail { get; set; }
        public long DecodeStdErrBytes { get; set; }

        // ---- 容器 / 包级 pass（容器时间语义）----
        public bool HasContainerTimeBase { get; set; }
        public int ContainerTimeBaseNumerator { get; set; }
        public int ContainerTimeBaseDenominator { get; set; }
        public long ContainerPacketCount { get; set; }
        public long FirstPacketPts { get; set; }
        public long LastPacketPts { get; set; }
        public long FirstPacketTimingMismatchIndex { get; set; }
        public string FirstPacketTimingMismatchDetail { get; set; }
        public int PacketExitCode { get; set; }
        public string PacketStdErrTail { get; set; }
        public long PacketStdErrBytes { get; set; }

        public long ElapsedMilliseconds { get; set; }

        public bool IsVerified
        {
            get { return Status == FfmpegVideoVerificationStatus.Verified; }
        }
    }

    internal interface IFfmpegVideoVerifier
    {
        Task<FfmpegVideoVerificationResult> VerifyAsync(FfmpegVideoVerificationRequest request);
    }

    /// <summary>
    /// 独立视频验证：用**同一个冻结 FFmpeg 可执行文件**再起两个短进程，真实解码并核对产物。
    ///
    /// 为什么必须这样验证（2026 年在真实 Gyan 9.0.2 / `3256173F…` 上的定向实测结论）：
    ///
    ///   1. "所有 stdin 写入完成 + 退出码 0 + 文件非空 + nb_frames" 都不是 MP4 正确的证据：
    ///      3 个完整帧 + 第 4 个半帧在不加 <c>-xerror</c> 时静默成功，容器里只有 3 帧。
    ///   2. 容器自报与真实解码可以不一致：保留 B 帧时容器报 100 个包，实际只能解出 96 帧。
    ///      因此**解码帧数**与**容器包数**必须分别验证（两种时间语义不可互相代替）。
    ///   3. 截断 / 无视频流 / 非 MP4 文件都会让核验进程非零退出且零帧可解
    ///      （实测 100 字节 MP4、60% 截断 MP4、纯文本文件均为 `-1094995529`；纯音频文件为 `-22`）。
    ///   4. 时间基与逐帧 PTS 必须精确：framemd5 头的 `#tb 0: n/d` 给出有理时间基，
    ///      数据行给出逐帧 dts/pts/duration，足以在**有界内存**下验证 `pts == dts == 行号` 且 `duration == 1`。
    ///
    /// 不做的事：不猜测、不修复、不降低期望值；失败只报告机读原因与有界证据。
    /// 核验进程可被取消：取消会终止并回收两个进程，绝不留下孤儿。
    /// </summary>
    internal sealed class FfmpegVideoVerifier : IFfmpegVideoVerifier
    {
        /// <summary>stderr 证据保留上限（字符）。长时间会话下不会无界增长。</summary>
        public const int StderrTailChars = 8192;

        /// <summary>可保留的结构化行数上限（输入流行 / 错误行各自）。</summary>
        public const int MaxMatchedLines = 8;

        private static readonly Regex StreamVideoRegex = new Regex(
            @"Stream #(?<index>\d+):(?<sub>\d+)[^:]*:\s*Video:\s*(?<codec>[A-Za-z0-9_]+)(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex DimensionRegex = new Regex(
            @"(?<w>\d+)x(?<h>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ParenthesisedRegex = new Regex(
            @"\([^)]*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly FfmpegVideoProcessStart _processStart;

        public FfmpegVideoVerifier(FfmpegVideoProcessStart processStart = null)
        {
            _processStart = processStart;
        }

        public Task<FfmpegVideoVerificationResult> VerifyAsync(FfmpegVideoVerificationRequest request)
        {
            return Task.Run(() => Verify(request));
        }

        private FfmpegVideoVerificationResult Verify(FfmpegVideoVerificationRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            FfmpegVideoVerificationResult result = VerifyCore(request);
            stopwatch.Stop();
            result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            return result;
        }

        private FfmpegVideoVerificationResult VerifyCore(FfmpegVideoVerificationRequest request)
        {
            var result = new FfmpegVideoVerificationResult();

            if (request == null)
                return Fail(result, "verify-request-null", null);

            if (string.IsNullOrWhiteSpace(request.ExecutablePath) || string.IsNullOrWhiteSpace(request.VideoPath))
                return Fail(result, "verify-request-incomplete", null);

            if (request.ExpectedFps <= 0 || request.ExpectedWidth <= 0 || request.ExpectedHeight <= 0)
                return Fail(result, "verify-expectation-invalid", null);

            if (request.ExpectedFrameCount < 0)
                return Fail(result, "verify-expectation-invalid", "expectedFrameCount<0");

            if (request.CancellationToken.IsCancellationRequested)
                return Cancel(result);

            string workingDirectory = SafeDirectoryOf(request.ExecutablePath);

            // ---- pass 1: 真实解码 + 输入流信息 ----
            var decodeRows = new Framemd5Accumulator();
            var decodeStreams = new StreamLineCollector();
            var decodeStderr = new BoundedTextCollector(StderrTailChars, MaxMatchedLines);

            PassOutcome decode = RunPass(
                request,
                FfmpegVideoCommand.BuildVerifyDecodeArguments(request.VideoPath),
                workingDirectory,
                decodeRows,
                decodeStreams,
                decodeStderr);

            result.DecodeExitCode = decode.ExitCode;
            result.DecodeStdErrBytes = decodeStderr.TotalChars;
            result.DecodeStdErrTail = decodeStderr.Tail;

            if (decode.Cancelled)
                return Cancel(result);

            if (decode.StartError != null)
                return Fail(result, "verify-start-failed", decode.StartError);

            if (decode.ReadError != null)
                return Fail(result, "verify-decode-read-failed", decode.ReadError);

            FillDecodeEvidence(result, decodeRows, decodeStreams);

            if (decode.ExitCode != 0)
            {
                return Fail(result, "verify-decode-exit",
                    "exit=" + decode.ExitCode.ToString(CultureInfo.InvariantCulture) +
                    " stderr=" + Flatten(decodeStderr.Tail, 400));
            }

            if (decodeStderr.ErrorLines.Count > 0)
                return Fail(result, "verify-decode-error-output", Flatten(Join(decodeStderr.ErrorLines), 400));

            if (decodeRows.MalformedLineDetail.Length > 0)
                return Fail(result, "verify-framemd5-malformed", decodeRows.MalformedLineDetail);

            string failureCode;
            string failureDetail;
            if (!ValidateDecodePass(request, result, decodeRows, out failureCode, out failureDetail))
                return Fail(result, failureCode, failureDetail);

            // ---- pass 2: 容器 / 包级时间语义 ----
            var packetRows = new Framemd5Accumulator();
            var packetStreams = new StreamLineCollector();
            var packetStderr = new BoundedTextCollector(StderrCharsForPackets(), MaxMatchedLines);

            PassOutcome packets = RunPass(
                request,
                FfmpegVideoCommand.BuildVerifyPacketArguments(request.VideoPath),
                workingDirectory,
                packetRows,
                packetStreams,
                packetStderr);

            result.PacketExitCode = packets.ExitCode;
            result.PacketStdErrBytes = packetStderr.TotalChars;
            result.PacketStdErrTail = packetStderr.Tail;

            if (packets.Cancelled)
                return Cancel(result);

            if (packets.StartError != null)
                return Fail(result, "verify-packet-start-failed", packets.StartError);

            if (packets.ReadError != null)
                return Fail(result, "verify-packet-read-failed", packets.ReadError);

            result.HasContainerTimeBase = packetRows.HasTimeBase;
            result.ContainerTimeBaseNumerator = packetRows.TimeBaseNumerator;
            result.ContainerTimeBaseDenominator = packetRows.TimeBaseDenominator;
            result.ContainerPacketCount = packetRows.RowCount;
            result.FirstPacketPts = packetRows.FirstPts;
            result.LastPacketPts = packetRows.LastPts;
            result.FirstPacketTimingMismatchIndex = packetRows.FirstMismatchIndex;
            result.FirstPacketTimingMismatchDetail = packetRows.FirstMismatchDetail;

            if (packets.ExitCode != 0)
            {
                return Fail(result, "verify-packet-exit",
                    "exit=" + packets.ExitCode.ToString(CultureInfo.InvariantCulture) +
                    " stderr=" + Flatten(packetStderr.Tail, 400));
            }

            if (packetRows.MalformedLineDetail.Length > 0)
                return Fail(result, "verify-packet-framemd5-malformed", packetRows.MalformedLineDetail);

            if (!packetRows.HasTimeBase ||
                packetRows.TimeBaseNumerator != 1 ||
                packetRows.TimeBaseDenominator != request.ExpectedFps)
            {
                return Fail(result, "verify-packet-timebase-mismatch",
                    "expected=1/" + request.ExpectedFps.ToString(CultureInfo.InvariantCulture) +
                    " actual=" + packetRows.DescribeTimeBase());
            }

            if (packetRows.RowCount != request.ExpectedFrameCount)
            {
                return Fail(result, "verify-packet-count-mismatch",
                    "expected=" + request.ExpectedFrameCount.ToString(CultureInfo.InvariantCulture) +
                    " actual=" + packetRows.RowCount.ToString(CultureInfo.InvariantCulture));
            }

            if (packetRows.FirstMismatchIndex >= 0)
                return Fail(result, "verify-packet-timing-mismatch", packetRows.FirstMismatchDetail);

            result.Status = FfmpegVideoVerificationStatus.Verified;
            return result;
        }

        private static int StderrCharsForPackets()
        {
            return StderrTailChars;
        }

        private static FfmpegVideoVerificationResult Fail(
            FfmpegVideoVerificationResult result, string errorCode, string errorDetail)
        {
            result.Status = FfmpegVideoVerificationStatus.Failed;
            result.ErrorCode = errorCode;
            result.ErrorDetail = errorDetail;
            return result;
        }

        private static FfmpegVideoVerificationResult Cancel(FfmpegVideoVerificationResult result)
        {
            result.Status = FfmpegVideoVerificationStatus.Cancelled;
            result.ErrorCode = "verify-cancelled";
            return result;
        }

        private static void FillDecodeEvidence(
            FfmpegVideoVerificationResult result, Framemd5Accumulator rows, StreamLineCollector streams)
        {
            result.VideoStreamCount = streams.Count;
            result.StreamLine = streams.First;
            result.HasStreamLine = result.StreamLine != null;

            string codec;
            string pixelFormat;
            int width;
            int height;
            if (result.HasStreamLine &&
                TryParseStreamLine(result.StreamLine, out codec, out pixelFormat, out width, out height))
            {
                result.CodecName = codec;
                result.PixelFormat = pixelFormat;
                result.DecodedWidth = width;
                result.DecodedHeight = height;
            }
            else if (rows.HasDimensions)
            {
                // 流行解析失败时仍可用解码尺寸作尺寸证据（但不构成"存在视频流"的证据）。
                result.DecodedWidth = rows.DimensionsWidth;
                result.DecodedHeight = rows.DimensionsHeight;
            }

            result.HasTimeBase = rows.HasTimeBase;
            result.TimeBaseNumerator = rows.TimeBaseNumerator;
            result.TimeBaseDenominator = rows.TimeBaseDenominator;
            result.DecodedFrameCount = rows.RowCount;
            result.FirstDecodedPts = rows.FirstPts;
            result.LastDecodedPts = rows.LastPts;
            result.FirstDecodedTimingMismatchIndex = rows.FirstMismatchIndex;
            result.FirstDecodedTimingMismatchDetail = rows.FirstMismatchDetail;
        }

        private static bool ValidateDecodePass(
            FfmpegVideoVerificationRequest request,
            FfmpegVideoVerificationResult result,
            Framemd5Accumulator rows,
            out string errorCode,
            out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;

            if (!result.HasStreamLine)
            {
                errorCode = "verify-no-video-stream";
                errorDetail = "stderr=" + Flatten(result.DecodeStdErrTail, 400);
                return false;
            }

            if (result.VideoStreamCount != 1)
            {
                errorCode = "verify-video-stream-count";
                errorDetail = "expected=1 actual=" + result.VideoStreamCount.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (!string.IsNullOrEmpty(request.ExpectedCodecName) &&
                !string.Equals(result.CodecName, request.ExpectedCodecName, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "verify-codec-mismatch";
                errorDetail = "expected=" + request.ExpectedCodecName + " actual=" + (result.CodecName ?? "null");
                return false;
            }

            if (result.DecodedWidth != request.ExpectedWidth || result.DecodedHeight != request.ExpectedHeight)
            {
                errorCode = "verify-dimension-mismatch";
                errorDetail = "expected=" + request.ExpectedWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                              request.ExpectedHeight.ToString(CultureInfo.InvariantCulture) +
                              " actual=" + Describe(result.DecodedWidth) + "x" + Describe(result.DecodedHeight);
                return false;
            }

            if (rows.HasDimensions &&
                (rows.DimensionsWidth != request.ExpectedWidth || rows.DimensionsHeight != request.ExpectedHeight))
            {
                errorCode = "verify-decoded-dimension-mismatch";
                errorDetail = "expected=" + request.ExpectedWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                              request.ExpectedHeight.ToString(CultureInfo.InvariantCulture) +
                              " actual=" + rows.DimensionsWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                              rows.DimensionsHeight.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (!string.IsNullOrEmpty(request.ExpectedPixelFormat) &&
                !string.Equals(result.PixelFormat, request.ExpectedPixelFormat, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "verify-pixel-format-mismatch";
                errorDetail = "expected=" + request.ExpectedPixelFormat + " actual=" + (result.PixelFormat ?? "null");
                return false;
            }

            if (!rows.HasTimeBase || rows.TimeBaseNumerator != 1 || rows.TimeBaseDenominator != request.ExpectedFps)
            {
                errorCode = "verify-timebase-mismatch";
                errorDetail = "expected=1/" + request.ExpectedFps.ToString(CultureInfo.InvariantCulture) +
                              " actual=" + rows.DescribeTimeBase();
                return false;
            }

            if (rows.RowCount != request.ExpectedFrameCount)
            {
                errorCode = "verify-frame-count-mismatch";
                errorDetail = "expected=" + request.ExpectedFrameCount.ToString(CultureInfo.InvariantCulture) +
                              " actual=" + rows.RowCount.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (rows.FirstMismatchIndex >= 0)
            {
                errorCode = "verify-frame-timing-mismatch";
                errorDetail = rows.FirstMismatchDetail;
                return false;
            }

            return true;
        }

        private static string Describe(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null";
        }

        // ==================================================================== process plumbing

        /// <summary>
        /// 启动并完整跑完一次核验进程：stdout 逐行喂给累加器（有界内存），
        /// stderr 只保留有界尾部与少量结构化行，退出后等待两个流都到 EOF。
        /// 取消会终止进程；两个进程都会被回收，不留下孤儿。
        /// </summary>
        private PassOutcome RunPass(
            FfmpegVideoVerificationRequest request,
            string arguments,
            string workingDirectory,
            Framemd5Accumulator rows,
            StreamLineCollector streams,
            BoundedTextCollector stderr)
        {
            var outcome = new PassOutcome();
            Process process = null;

            try
            {
                process = StartProcess(request.ExecutablePath, arguments, workingDirectory);
                if (process == null)
                {
                    outcome.StartError = "process-start-returned-null";
                    return outcome;
                }

                Task exitTask = WaitForExitAsync(process);

                Task stdoutTask = PumpAsync(
                    process.StandardOutput.BaseStream,
                    rows.FeedLine,
                    ex => outcome.ReadError = outcome.ReadError ?? ("stdout: " + ex.Message));

                Task stderrTask = PumpAsync(
                    process.StandardError.BaseStream,
                    line =>
                    {
                        stderr.FeedLine(line);
                        streams.FeedLine(line);
                        if (IsErrorLine(line))
                            stderr.FeedError(line);
                    },
                    ex => outcome.ReadError = outcome.ReadError ?? ("stderr: " + ex.Message));

                using (request.CancellationToken.Register(() => TryKill(process)))
                {
                    exitTask.Wait();
                    Task.WaitAll(new[] { stdoutTask, stderrTask });
                }

                if (request.CancellationToken.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    return outcome;
                }

                outcome.ExitCode = SafeExitCode(process);
                return outcome;
            }
            catch (Exception ex)
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    return outcome;
                }

                if (outcome.StartError == null)
                    outcome.StartError = "verify-process-failed: " + ex.Message;
                return outcome;
            }
            finally
            {
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                            TryKill(process);
                    }
                    catch
                    {
                    }

                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private Process StartProcess(string executablePath, string arguments, string workingDirectory)
        {
            if (_processStart != null)
                return _processStart(executablePath, arguments, workingDirectory);

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

            // 核验进程不需要 stdin；立刻关闭，避免个别构建等待输入。
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            return process;
        }

        private static async Task PumpAsync(Stream stream, Action<string> onLine, Action<Exception> onError)
        {
            try
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, false))
                {
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        onLine(line);
                }
            }
            catch (Exception ex)
            {
                if (onError != null)
                    onError(ex);
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

        private static int SafeExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch (Exception)
            {
                return int.MinValue;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch
            {
            }
        }

        private static string SafeDirectoryOf(string path)
        {
            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // ==================================================================== text parsing

        internal static bool IsErrorLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return false;

            return line.IndexOf("[error]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("[fatal]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("Error ", StringComparison.Ordinal) >= 0 ||
                   line.IndexOf("Invalid data", StringComparison.Ordinal) >= 0 ||
                   line.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("Conversion failed", StringComparison.Ordinal) >= 0 ||
                   line.IndexOf("Permission denied", StringComparison.Ordinal) >= 0 ||
                   line.IndexOf("No such file or directory", StringComparison.Ordinal) >= 0 ||
                   line.IndexOf("matches no streams", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 解析 <c>Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 64x48, …</c>。
        ///
        /// 实现要点：括号组必须先剥离。否则 <c>(avc1 / 0x31637661)</c> 里的
        /// <c>0x31637661</c> 会被 <c>\d+x\d+</c> 误当成 "0x31637661" 尺寸，
        /// 而 <c>yuv420p(tv, progressive)</c> 内层的逗号也会破坏字段切分。
        /// </summary>
        internal static bool TryParseStreamLine(
            string line, out string codec, out string pixelFormat, out int width, out int height)
        {
            codec = null;
            pixelFormat = null;
            width = 0;
            height = 0;

            if (string.IsNullOrEmpty(line))
                return false;

            Match match = StreamVideoRegex.Match(line);
            if (!match.Success)
                return false;

            codec = match.Groups["codec"].Value;
            string stripped = ParenthesisedRegex.Replace(match.Groups["rest"].Value, " ");

            Match dimensions = DimensionRegex.Match(stripped);
            if (!dimensions.Success)
                return false;

            width = int.Parse(dimensions.Groups["w"].Value, CultureInfo.InvariantCulture);
            height = int.Parse(dimensions.Groups["h"].Value, CultureInfo.InvariantCulture);

            string head = stripped.Substring(0, dimensions.Index);
            string[] fields = head.Split(',');
            for (int i = fields.Length - 1; i >= 0; i--)
            {
                string field = fields[i].Trim();
                if (field.Length == 0)
                    continue;

                pixelFormat = field;
                break;
            }

            return true;
        }

        private static string Flatten(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }

        private static string Join(IReadOnlyList<string> lines)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                    sb.Append(" | ");
                sb.Append(lines[i].Trim());
            }
            return sb.ToString();
        }

        private sealed class PassOutcome
        {
            public int ExitCode { get; set; }
            public bool Cancelled { get; set; }
            public string StartError { get; set; }
            public string ReadError { get; set; }
        }
    }

    /// <summary>
    /// 只收集 **Input 段** 的视频流行。
    ///
    /// 核验进程的 stderr 里同时存在输入流与输出流两行 `Stream #0:0 …: Video: …`
    /// （输出段是 framemd5 的 rawvideo）。只把 Input 段的流行当作"产物里的视频流"证据，
    /// 否则会把一个流数成两个，并把 rawvideo 的像素格式当成产物格式。
    /// </summary>
    internal sealed class StreamLineCollector
    {
        private readonly List<string> _lines = new List<string>();
        private readonly int _maxLines;
        private bool _inputSectionActive;

        public StreamLineCollector(int maxLines = 8)
        {
            _maxLines = maxLines > 0 ? maxLines : 8;
        }

        public int Count
        {
            get { return _lines.Count; }
        }

        public string First
        {
            get { return _lines.Count > 0 ? _lines[0] : null; }
        }

        public void FeedLine(string line)
        {
            if (line == null)
                return;

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("Input #", StringComparison.Ordinal))
            {
                _inputSectionActive = true;
                return;
            }

            if (trimmed.StartsWith("Output #", StringComparison.Ordinal) ||
                trimmed.StartsWith("Stream mapping:", StringComparison.Ordinal))
            {
                _inputSectionActive = false;
                return;
            }

            if (!_inputSectionActive)
                return;

            if (line.IndexOf("Stream #", StringComparison.Ordinal) < 0)
                return;

            if (line.IndexOf("Video:", StringComparison.Ordinal) < 0)
                return;

            if (_lines.Count < _maxLines)
                _lines.Add(line.Trim());
        }
    }

    /// <summary>
    /// 有界文本收集器：保留总字符数、尾部若干字符，以及上限内的结构化行（错误行）。
    /// 超大或超长 stderr 都不会导致无界内存。
    /// </summary>
    internal sealed class BoundedTextCollector
    {
        private readonly int _maxTailChars;
        private readonly int _maxMatchedLines;
        private readonly StringBuilder _tail = new StringBuilder();
        private readonly List<string> _errorLines = new List<string>();

        public BoundedTextCollector(int maxTailChars, int maxMatchedLines)
        {
            _maxTailChars = maxTailChars > 0 ? maxTailChars : 8192;
            _maxMatchedLines = maxMatchedLines > 0 ? maxMatchedLines : 8;
        }

        public long TotalChars { get; private set; }
        public long TotalLines { get; private set; }

        public IReadOnlyList<string> ErrorLines
        {
            get { return _errorLines; }
        }

        public string Tail
        {
            get { return _tail.ToString(); }
        }

        public void FeedLine(string line)
        {
            if (line == null)
                return;

            TotalLines++;
            TotalChars += line.Length + 1;

            _tail.Append(line).Append('\n');
            if (_tail.Length > _maxTailChars)
                _tail.Remove(0, _tail.Length - _maxTailChars);
        }

        public void FeedError(string line)
        {
            if (line != null && _errorLines.Count < _maxMatchedLines)
                _errorLines.Add(line);
        }
    }

    /// <summary>
    /// framemd5 输出累加器（有界内存）：解析时间基与尺寸头，逐行验证
    /// <c>dts == pts == 行号</c> 且 <c>duration == 1</c>，只保留聚合结果与首个不匹配细节。
    /// 即使上千万帧也不会把逐帧数据留在内存里。
    /// </summary>
    internal sealed class Framemd5Accumulator
    {
        private static readonly Regex HeaderDimensionRegex =
            new Regex(@"(?<w>\d+)x(?<h>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public bool HasTimeBase { get; private set; }
        public int TimeBaseNumerator { get; private set; }
        public int TimeBaseDenominator { get; private set; }

        public bool HasDimensions { get; private set; }
        public int DimensionsWidth { get; private set; }
        public int DimensionsHeight { get; private set; }

        public string CodecId { get; private set; }

        public long RowCount { get; private set; }
        public long FirstPts { get; private set; }
        public long LastPts { get; private set; }
        public long FirstMismatchIndex { get; private set; } = -1;
        public string FirstMismatchDetail { get; private set; }
        public string MalformedLineDetail { get; private set; } = string.Empty;

        public string DescribeTimeBase()
        {
            return HasTimeBase
                ? TimeBaseNumerator.ToString(CultureInfo.InvariantCulture) + "/" +
                  TimeBaseDenominator.ToString(CultureInfo.InvariantCulture)
                : "none";
        }

        public void FeedLine(string line)
        {
            if (line == null)
                return;

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                return;

            if (trimmed[0] == '#')
            {
                FeedHeader(trimmed);
                return;
            }

            FeedRow(trimmed);
        }

        private void FeedHeader(string line)
        {
            if (line.StartsWith("#tb ", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string[] parts = line.Substring(colon + 1).Trim().Split('/');
                    int numerator;
                    int denominator;
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator) &&
                        int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator))
                    {
                        TimeBaseNumerator = numerator;
                        TimeBaseDenominator = denominator;
                        HasTimeBase = true;
                    }
                }
                return;
            }

            if (line.StartsWith("#dimensions ", StringComparison.Ordinal))
            {
                Match match = HeaderDimensionRegex.Match(line);
                if (match.Success)
                {
                    DimensionsWidth = int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture);
                    DimensionsHeight = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
                    HasDimensions = true;
                }
                return;
            }

            if (line.StartsWith("#codec_id ", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    CodecId = line.Substring(colon + 1).Trim();
            }
        }

        private void FeedRow(string line)
        {
            string[] parts = line.Split(',');
            if (parts.Length < 6)
            {
                NoteMalformed("fields=" + parts.Length.ToString(CultureInfo.InvariantCulture) + " line=" + line);
                return;
            }

            long dts;
            long pts;
            long duration;
            if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dts) ||
                !long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pts) ||
                !long.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out duration))
            {
                NoteMalformed("non-numeric row: " + line);
                return;
            }

            long index = RowCount;
            RowCount++;

            if (index == 0)
                FirstPts = pts;
            LastPts = pts;

            if (FirstMismatchIndex >= 0)
                return;

            if (dts != pts)
            {
                NoteMismatch(index, "dts=" + dts.ToString(CultureInfo.InvariantCulture) +
                                    " pts=" + pts.ToString(CultureInfo.InvariantCulture) + " expected equal");
                return;
            }

            if (pts != index)
            {
                NoteMismatch(index, "pts=" + pts.ToString(CultureInfo.InvariantCulture) +
                                    " expected=" + index.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (duration != 1)
                NoteMismatch(index, "duration=" + duration.ToString(CultureInfo.InvariantCulture) + " expected=1");
        }

        private void NoteMismatch(long index, string detail)
        {
            FirstMismatchIndex = index;
            FirstMismatchDetail = "frame=" + index.ToString(CultureInfo.InvariantCulture) + " " + detail;
        }

        private void NoteMalformed(string detail)
        {
            if (MalformedLineDetail.Length == 0)
                MalformedLineDetail = detail.Length > 300 ? detail.Substring(0, 300) : detail;
        }
    }
}
