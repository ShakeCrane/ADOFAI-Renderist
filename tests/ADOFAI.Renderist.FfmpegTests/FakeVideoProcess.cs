using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// 可控制的假视频进程：测试可执行文件本身充当"编码进程"与"核验进程"。
    ///
    /// 存在理由：真实 FFmpeg 二进制刻意不入库，而 L2 需要稳定复现的**进程级**故障形态 ——
    /// 长时间背压（读取端停止消费）、写入中途断管、编码器非零退出、输出无法打开、
    /// 核验进程卡住、以及核验输出里的各种不一致。这些都无法用真实 FFmpeg 稳定构造。
    ///
    /// 与 <see cref="FakeFfmpeg"/> 的区别：假 FFmpeg 由环境变量驱动（只覆盖能力探测），
    /// 本类型由命令行 directive 驱动，因为编码/核验命令需要在同一次测试内切换形态。
    ///
    /// 关键点：假进程**只**从真实命令行的 <c>-y</c> 参数取输出路径。
    /// 因此"Windows 参数引用是否正确"是被真实断言的 —— 引号写错时，假进程会写到错误的路径，
    /// 发布阶段就会因为找不到临时文件而失败。
    /// </summary>
    internal static class FakeVideoProcess
    {
        public const string InvocationMarker = "--renderist-fake-video";

        public static string ExecutablePath
        {
            get { return FakeFfmpeg.ExecutablePath; }
        }

        public static bool IsFakeInvocation(string[] args)
        {
            if (args == null)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], InvocationMarker, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>构造命令行引用：真实参数 + 调用标记 + directive。</summary>
        public static string BuildArguments(string originalArguments, string directives)
        {
            var sb = new StringBuilder();
            sb.Append(originalArguments);
            sb.Append(' ');
            sb.Append(InvocationMarker);

            if (!string.IsNullOrEmpty(directives))
            {
                sb.Append(' ');
                sb.Append(directives);
            }

            return sb.ToString();
        }

        public static int Run(string[] args)
        {
            Dictionary<string, string> directives = ParseDirectives(args);

            if (IsFramemd5(args))
                return RunVerifier(args, directives);

            return RunEncoder(args, directives);
        }

        // ================================================================== encoder

        private static int RunEncoder(string[] args, Dictionary<string, string> directives)
        {
            if (Flag(directives, "hang"))
            {
                SleepForever();
                return 0;
            }

            int sleepBeforeRead = Int(directives, "sleep-before-read-ms", 0);
            if (sleepBeforeRead > 0)
                Thread.Sleep(sleepBeforeRead);

            long stallAfter = Long(directives, "stall-after-bytes", -1);
            long exitAfter = Long(directives, "exit-after-bytes", -1);
            string outputPath = FindOptionValue(args, "-y");

            // 真实 FFmpeg 在启动时就会创建输出文件；early-output 用来复现这一时机。
            if (Flag(directives, "early-output"))
                WriteOutputFile(outputPath, directives);

            var buffer = new byte[65536];
            long totalRead = 0;
            Stream input = Console.OpenStandardInput();

            while (true)
            {
                if (stallAfter >= 0 && totalRead >= stallAfter)
                {
                    // 读取端停止消费：父进程的写入会自然阻塞（真实 IO 背压）。
                    SleepForever();
                    return 0;
                }

                if (exitAfter >= 0 && totalRead >= exitAfter)
                {
                    // 中途退出：对端管道关闭，父进程下一次写入会失败。
                    EmitNoise(directives);
                    return Int(directives, "exit", 0);
                }

                int read;
                try
                {
                    read = input.Read(buffer, 0, buffer.Length);
                }
                catch (Exception)
                {
                    return 1;
                }

                if (read <= 0)
                    break;

                totalRead += read;
            }

            int delayExit = Int(directives, "delay-exit-ms", 0);
            if (delayExit > 0)
                Thread.Sleep(delayExit);

            if (Flag(directives, "fail-output-open"))
            {
                Console.Error.WriteLine("[out#0/mp4 @ 0000000000000000] Error opening output " + (outputPath ?? "?") +
                                        ": Permission denied");
                Console.Error.WriteLine("Error opening output file " + (outputPath ?? "?") + ".");
                Console.Error.Flush();
                return Int(directives, "exit", -13);
            }

            EmitNoise(directives);

            // 默认行为与真实 FFmpeg 一致：除非显式要求，否则一定会创建输出文件。
            if (!Flag(directives, "no-output"))
                WriteOutputFile(outputPath, directives);

            if (Flag(directives, "receipt") && !string.IsNullOrEmpty(outputPath))
            {
                var receipt = new StringBuilder();
                receipt.Append("out=").Append(outputPath).Append('\n');
                receipt.Append("size=").Append(FindOptionValue(args, "-s")).Append('\n');
                receipt.Append("framerate=").Append(FindOptionValue(args, "-framerate")).Append('\n');
                receipt.Append("pixfmt=").Append(FindOptionValue(args, "-pix_fmt")).Append('\n');
                receipt.Append("timescale=").Append(FindOptionValue(args, "-video_track_timescale")).Append('\n');
                receipt.Append("vf=").Append(FindOptionValue(args, "-vf")).Append('\n');
                receipt.Append("stdinBytes=").Append(totalRead.ToString(CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(outputPath + ".fake-receipt", receipt.ToString());
            }

            return Int(directives, "exit", 0);
        }

        private static void WriteOutputFile(string outputPath, Dictionary<string, string> directives)
        {
            if (string.IsNullOrEmpty(outputPath))
                return;

            long bytes = Long(directives, "output-bytes", 64);
            var payload = new byte[bytes];
            for (long i = 0; i < bytes; i++)
                payload[i] = (byte)(i & 0xFF);

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // 与真实 FFmpeg 一致的打开语义：deny-none 共享（_SH_DENYNO）。
            // 这一点很重要：管线的临时文件 ownership 句柄是打开的，任何只共享 Read 的
            // 打开方式都会与之冲突，从而无法复现真实编码器的行为。
            using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
        }

        private static void EmitNoise(Dictionary<string, string> directives)
        {
            int stderrBytes = Int(directives, "stderr-bytes", -1);
            if (stderrBytes < 0 && directives.ContainsKey("stderr-text"))
            {
                Console.Error.WriteLine(directives["stderr-text"]);
                stderrBytes = 0;
            }

            if (stderrBytes > 0)
            {
                var sb = new StringBuilder(stderrBytes + 64);
                for (int i = 0; i < stderrBytes; i++)
                    sb.Append((char)('a' + (i % 26)));
                Console.Error.Write(sb.ToString());
                Console.Error.Flush();
            }

            int stdoutBytes = Int(directives, "stdout-bytes", 0);
            if (stdoutBytes > 0)
            {
                var sb = new StringBuilder(stdoutBytes + 64);
                for (int i = 0; i < stdoutBytes; i++)
                    sb.Append((char)('A' + (i % 26)));
                Console.Out.Write(sb.ToString());
                Console.Out.Flush();
            }
        }

        // ================================================================== verifier

        private static int RunVerifier(string[] args, Dictionary<string, string> directives)
        {
            if (Flag(directives, "hang"))
            {
                SleepForever();
                return 0;
            }

            bool packetPass = HasOption(args, "-c", "copy");
            bool wantsStreamInfo = HasOption(args, "-loglevel", "info");

            int fps = Int(directives, "fps", 30);
            int width = Int(directives, "width", 64);
            int height = Int(directives, "height", 48);
            string pixelFormat = String(directives, "pixfmt", "yuv420p");
            string codec = String(directives, "codec", "h264");

            long frames = packetPass
                ? Long(directives, "packet-frames", Long(directives, "frames", 0))
                : Long(directives, "frames", 0);

            int tbNumerator = Int(directives, "tb-num", 1);
            int tbDenominator = Int(directives, "tb-den", fps);
            long ptsOffset = Long(directives, "pts-offset", 0);
            long duration = Long(directives, "duration", 1);

            if (wantsStreamInfo && !Flag(directives, "without-stream-line"))
            {
                var stderr = new StringBuilder();
                stderr.Append("Input #0, mov,mp4,m4a,3gp,3g2,mj2, from '");
                stderr.Append(FindOptionValue(args, "-i"));
                stderr.Append("':\n");
                stderr.Append("  Duration: 00:00:00.00, start: 0.000000, bitrate: 3417200 kb/s\n");
                stderr.Append("  Stream #0:0[0x1](und): Video: ").Append(codec)
                    .Append(" (High) (avc1 / 0x31637661), ").Append(pixelFormat)
                    .Append("(progressive), ").Append(width).Append('x').Append(height)
                    .Append(", 152 kb/s, ").Append(fps).Append(".00 fps, ").Append(fps)
                    .Append(".00 tbr, ").Append(fps).Append(".00 tbn (default)\n");

                for (int i = 0; i < Int(directives, "extra-stream-lines", 0); i++)
                {
                    stderr.Append("  Stream #0:").Append(i + 1).Append("(und): Video: ").Append(codec)
                        .Append(" (High) (avc1 / 0x31637661), ").Append(pixelFormat)
                        .Append("(progressive), ").Append(width).Append('x').Append(height)
                        .Append(", 152 kb/s, ").Append(fps).Append(".00 fps, ").Append(fps)
                        .Append(".00 tbr, ").Append(fps).Append(".00 tbn (default)\n");
                }

                stderr.Append("Stream mapping:\n");
                stderr.Append("  Stream #0:0 -> #0:0 (").Append(codec).Append(" (native) -> rawvideo (native))\n");
                stderr.Append("Output #0, framemd5, to 'pipe:':\n");
                stderr.Append("  Stream #0:0(und): Video: rawvideo (I420 / 0x30323449), ")
                    .Append(pixelFormat).Append("(progressive), ").Append(width).Append('x').Append(height)
                    .Append(", q=2-31, ").Append(fps).Append(".00 tbn (default)\n");

                if (directives.ContainsKey("stderr-error"))
                    stderr.Append(directives["stderr-error"]).Append('\n');

                Console.Error.Write(stderr.ToString());
                Console.Error.Flush();
            }
            else if (directives.ContainsKey("stderr-error"))
            {
                Console.Error.WriteLine(directives["stderr-error"]);
                Console.Error.Flush();
            }

            var stdout = new StringBuilder();
            stdout.Append("#format: frame checksums\n");
            stdout.Append("#version: 2\n");
            stdout.Append("#hash: MD5\n");
            stdout.Append("#software: Lavf63.1.102\n");
            if (!Flag(directives, "without-tb-header"))
                stdout.Append("#tb 0: ").Append(tbNumerator).Append('/').Append(tbDenominator).Append('\n');
            stdout.Append("#media_type 0: video\n");
            stdout.Append("#codec_id 0: rawvideo\n");
            if (!Flag(directives, "without-dimensions-header"))
                stdout.Append("#dimensions 0: ").Append(width).Append('x').Append(height).Append('\n');
            stdout.Append("#sar 0: 1/1\n");

            for (long i = 0; i < frames; i++)
            {
                long pts = i + ptsOffset;
                stdout.Append("0, ").Append(pts).Append(", ").Append(pts).Append(", ").Append(duration)
                    .Append(", 9216, 00000000000000000000000000000000\n");
            }

            Console.Out.Write(stdout.ToString());
            Console.Out.Flush();

            if (packetPass)
                return Int(directives, "packet-exit", Int(directives, "exit", 0));

            return Int(directives, "exit", 0);
        }

        // ================================================================== helpers

        private static void SleepForever()
        {
            while (true)
                Thread.Sleep(1000);
        }

        private static bool IsFramemd5(string[] args)
        {
            return HasOption(args, "-f", "framemd5");
        }

        private static bool HasOption(string[] args, string option, string value)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], option, StringComparison.Ordinal) &&
                    string.Equals(args[i + 1], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string FindOptionValue(string[] args, string option)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], option, StringComparison.Ordinal))
                    return args[i + 1];
            }
            return null;
        }

        private static Dictionary<string, string> ParseDirectives(string[] args)
        {
            var directives = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], InvocationMarker, StringComparison.Ordinal))
                    continue;

                for (int j = i + 1; j < args.Length; j++)
                {
                    string token = args[j];
                    int separator = token.IndexOf('=');
                    if (separator <= 0)
                    {
                        directives[token] = "1";
                        continue;
                    }

                    directives[token.Substring(0, separator)] = token.Substring(separator + 1);
                }
                break;
            }

            return directives;
        }

        private static bool Flag(Dictionary<string, string> directives, string name)
        {
            string value;
            if (!directives.TryGetValue(name, out value))
                return false;

            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int Int(Dictionary<string, string> directives, string name, int fallback)
        {
            string value;
            int parsed;
            if (directives.TryGetValue(name, out value) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private static long Long(Dictionary<string, string> directives, string name, long fallback)
        {
            string value;
            long parsed;
            if (directives.TryGetValue(name, out value) &&
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private static string String(Dictionary<string, string> directives, string name, string fallback)
        {
            string value;
            return directives.TryGetValue(name, out value) ? value : fallback;
        }
    }

    /// <summary>假编码进程的脚本（directive 集合）。</summary>
    internal sealed class FakeVideoEncoderScript
    {
        public int ExitCode { get; set; }
        public bool CreateOutput { get; set; } = true;
        public long OutputBytes { get; set; } = 64;

        /// <summary>读取到该字节数后停止消费 stdin（真实 IO 背压），进程保持存活。</summary>
        public long StallAfterBytes { get; set; } = -1;

        public int SleepBeforeReadMs { get; set; }

        /// <summary>读取到该字节数后立即退出（中途断管的编码器）。</summary>
        public long ExitAfterBytes { get; set; } = -1;

        public int SleepBeforeExitMs { get; set; }
        public bool Hang { get; set; }
        public bool FailOutputOpen { get; set; }

        /// <summary>像真实 FFmpeg 一样在启动时就创建输出文件（而不是等 stdin EOF）。</summary>
        public bool EarlyOutput { get; set; }

        public int StdErrBytes { get; set; }
        public int StdOutBytes { get; set; }
        public bool WriteReceipt { get; set; }

        public string ToDirectives()
        {
            var sb = new StringBuilder();
            sb.Append("exit=").Append(ExitCode.ToString(CultureInfo.InvariantCulture));
            if (!CreateOutput) sb.Append(" no-output=1");
            sb.Append(" output-bytes=").Append(OutputBytes.ToString(CultureInfo.InvariantCulture));
            if (StallAfterBytes >= 0) sb.Append(" stall-after-bytes=").Append(StallAfterBytes.ToString(CultureInfo.InvariantCulture));
            if (SleepBeforeReadMs > 0) sb.Append(" sleep-before-read-ms=").Append(SleepBeforeReadMs.ToString(CultureInfo.InvariantCulture));
            if (ExitAfterBytes >= 0) sb.Append(" exit-after-bytes=").Append(ExitAfterBytes.ToString(CultureInfo.InvariantCulture));
            if (SleepBeforeExitMs > 0) sb.Append(" delay-exit-ms=").Append(SleepBeforeExitMs.ToString(CultureInfo.InvariantCulture));
            if (Hang) sb.Append(" hang=1");
            if (FailOutputOpen) sb.Append(" fail-output-open=1");
            if (EarlyOutput) sb.Append(" early-output=1");
            if (StdErrBytes > 0) sb.Append(" stderr-bytes=").Append(StdErrBytes.ToString(CultureInfo.InvariantCulture));
            if (StdOutBytes > 0) sb.Append(" stdout-bytes=").Append(StdOutBytes.ToString(CultureInfo.InvariantCulture));
            if (WriteReceipt) sb.Append(" receipt=1");
            return sb.ToString();
        }
    }

    /// <summary>假核验进程的脚本（directive 集合）。</summary>
    internal sealed class FakeVideoVerifierScript
    {
        public int ExitCode { get; set; }
        public int PacketExitCode { get; set; }
        public long Frames { get; set; }
        public long PacketFrames { get; set; } = -1;
        public int Width { get; set; } = 64;
        public int Height { get; set; } = 48;
        public int Fps { get; set; } = 30;
        public string PixelFormat { get; set; } = "yuv420p";
        public string Codec { get; set; } = "h264";
        public int TimeBaseNumerator { get; set; } = 1;
        public int TimeBaseDenominator { get; set; }
        public long PtsOffset { get; set; }
        public long Duration { get; set; } = 1;
        public bool WithoutTimeBaseHeader { get; set; }
        public bool WithoutDimensionsHeader { get; set; }
        public bool WithoutStreamLine { get; set; }
        public int ExtraStreamLines { get; set; }
        public string StderrError { get; set; }
        public bool Hang { get; set; }

        public string ToDirectives()
        {
            var sb = new StringBuilder();
            sb.Append("exit=").Append(ExitCode.ToString(CultureInfo.InvariantCulture));
            sb.Append(" packet-exit=").Append(PacketExitCode.ToString(CultureInfo.InvariantCulture));
            sb.Append(" frames=").Append(Frames.ToString(CultureInfo.InvariantCulture));
            if (PacketFrames >= 0) sb.Append(" packet-frames=").Append(PacketFrames.ToString(CultureInfo.InvariantCulture));
            sb.Append(" width=").Append(Width.ToString(CultureInfo.InvariantCulture));
            sb.Append(" height=").Append(Height.ToString(CultureInfo.InvariantCulture));
            sb.Append(" fps=").Append(Fps.ToString(CultureInfo.InvariantCulture));
            sb.Append(" pixfmt=").Append(PixelFormat);
            sb.Append(" codec=").Append(Codec);
            sb.Append(" tb-num=").Append(TimeBaseNumerator.ToString(CultureInfo.InvariantCulture));
            if (TimeBaseDenominator > 0)
                sb.Append(" tb-den=").Append(TimeBaseDenominator.ToString(CultureInfo.InvariantCulture));
            if (PtsOffset != 0) sb.Append(" pts-offset=").Append(PtsOffset.ToString(CultureInfo.InvariantCulture));
            if (Duration != 1) sb.Append(" duration=").Append(Duration.ToString(CultureInfo.InvariantCulture));
            if (WithoutTimeBaseHeader) sb.Append(" without-tb-header=1");
            if (WithoutDimensionsHeader) sb.Append(" without-dimensions-header=1");
            if (WithoutStreamLine) sb.Append(" without-stream-line=1");
            if (ExtraStreamLines > 0) sb.Append(" extra-stream-lines=").Append(ExtraStreamLines.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(StderrError)) sb.Append(" stderr-error=\"").Append(StderrError).Append('"');
            if (Hang) sb.Append(" hang=1");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 把编码与核验都交给假进程的启动器（替换生产 <see cref="Process.Start(ProcessStartInfo)"/>）。
    /// 命令行本身保持不变，只在末尾追加调用标记与 directive。
    /// </summary>
    internal sealed class FakeVideoProcessLauncher
    {
        public FakeVideoEncoderScript Encoder { get; set; } = new FakeVideoEncoderScript();
        public FakeVideoVerifierScript Verifier { get; set; } = new FakeVideoVerifierScript();

        /// <summary>记录被启动的次数（编码 / 核验）。</summary>
        public int EncoderStarts { get; private set; }
        public int VerifierStarts { get; private set; }

        /// <summary>最后一次编码启动的可执行文件路径与命令行（用于断言冻结配置真的进入了命令）。</summary>
        public string LastEncoderExecutablePath { get; private set; }
        public string LastEncoderArguments { get; private set; }

        public FfmpegVideoProcessStart Create()
        {
            return StartProcess;
        }

        private Process StartProcess(string executablePath, string arguments, string workingDirectory)
        {
            bool verifier = arguments.IndexOf("-f framemd5", StringComparison.Ordinal) >= 0;

            if (!verifier)
            {
                LastEncoderExecutablePath = executablePath;
                LastEncoderArguments = arguments;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = FakeVideoProcess.ExecutablePath,
                Arguments = FakeVideoProcess.BuildArguments(
                    arguments, verifier ? Verifier.ToDirectives() : Encoder.ToDirectives()),
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

            if (verifier)
                VerifierStarts++;
            else
                EncoderStarts++;

            return process;
        }
    }
}
