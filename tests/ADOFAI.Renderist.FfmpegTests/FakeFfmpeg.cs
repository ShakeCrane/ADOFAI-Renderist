using System;
using System.Diagnostics;
using System.Text;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// 可控制的假 FFmpeg：测试可执行文件在被以 <c>-hide_banner</c> 调用时，
    /// 按环境变量输出结构与退出码，用来精确复现能力探测的失败形态。
    ///
    /// 存在理由：真实 FFmpeg 二进制刻意不入库，而"非零退出但 stdout 仍含
    /// libx264 / mp4 / rawvideo"这类缺陷只能在**可控子进程**上稳定复现。
    /// 因此这里让测试程序自身充当子进程，不依赖任何外部工具。
    ///
    /// 环境变量：
    ///   RENDERIST_FAKE_VERSION_EXIT         -version 的退出码（默认 0）
    ///   RENDERIST_FAKE_ENCODERS_EXIT        -encoders 的退出码（默认 0）
    ///   RENDERIST_FAKE_FORMATS_EXIT         -formats 的退出码（默认 0）
    ///   RENDERIST_FAKE_ENCODERS_OMIT_LIBX264  1 = 编码器表里不出现 libx264
    ///   RENDERIST_FAKE_ENCODERS_NOISE          1 = 只有普通文本提到 libx264，没有编码器表行
    ///   RENDERIST_FAKE_FORMATS_NOISE           1 = 只有普通文本提到 mp4/rawvideo，没有格式表行
    ///   RENDERIST_FAKE_FFMPEG_TOUCH            非空 = 每次被调用都向该文件追加一行（探测可观测性）
    /// </summary>
    internal static class FakeFfmpeg
    {
        private const string SentinelArgument = "-hide_banner";

        /// <summary>当前测试可执行文件的绝对路径（作为"假 FFmpeg"被探测）。</summary>
        public static string ExecutablePath
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().MainModule.FileName;
                }
                catch
                {
                    return System.Reflection.Assembly.GetEntryAssembly().Location;
                }
            }
        }

        /// <summary>只有我们的能力探测会传 <c>-hide_banner</c>，测试运行器自身不带参数。</summary>
        public static bool IsFakeInvocation(string[] args)
        {
            if (args == null)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], SentinelArgument, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static int Run(string[] args)
        {
            RecordInvocation(args);

            if (HasFlag(args, "-version"))
            {
                Console.Out.Write(VersionText());
                Console.Out.Flush();
                return ReadExitCode("RENDERIST_FAKE_VERSION_EXIT");
            }

            if (HasFlag(args, "-encoders"))
            {
                Console.Out.Write(IsSet("RENDERIST_FAKE_ENCODERS_NOISE") ? EncodersNoiseText() : EncodersText());
                Console.Out.Flush();
                return ReadExitCode("RENDERIST_FAKE_ENCODERS_EXIT");
            }

            if (HasFlag(args, "-formats"))
            {
                Console.Out.Write(IsSet("RENDERIST_FAKE_FORMATS_NOISE") ? FormatsNoiseText() : FormatsText());
                Console.Out.Flush();
                return ReadExitCode("RENDERIST_FAKE_FORMATS_EXIT");
            }

            return 0;
        }

        /// <summary>
        /// 每次假 FFmpeg 被调用时向 <c>RENDERIST_FAKE_FFMPEG_TOUCH</c> 指向的文件追加一行。
        ///
        /// 用途：让"能力探测确实发生 / 确实**没有**发生"成为可观测事实 ——
        /// 取消后的检查绝不允许再启动新的短进程探测。
        /// </summary>
        private static void RecordInvocation(string[] args)
        {
            string path = Environment.GetEnvironmentVariable("RENDERIST_FAKE_FFMPEG_TOUCH");
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                System.IO.File.AppendAllText(path, string.Join(" ", args) + Environment.NewLine);
            }
            catch
            {
                // 记录失败不能影响假 FFmpeg 自身的行为。
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool IsSet(string name)
        {
            return string.Equals(
                Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
        }

        private static int ReadExitCode(string name)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            int value;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out value))
                return value;
            return 0;
        }

        private static string VersionText()
        {
            var sb = new StringBuilder();
            sb.Append("ffmpeg version 9.0.2-fake Copyright (c) 2000-2026 the FFmpeg developers\n");
            sb.Append("built with gcc 13.2.0 (GCC)\n");
            sb.Append("configuration: --enable-gpl --enable-libx264 --enable-static\n");
            sb.Append("libavutil      59. 39.100 / 59. 39.100\n");
            return sb.ToString();
        }

        private static string EncodersText()
        {
            var sb = new StringBuilder();
            sb.Append("Encoders:\n");
            sb.Append(" V..... = Video\n");
            sb.Append(" ------\n");
            if (!IsSet("RENDERIST_FAKE_ENCODERS_OMIT_LIBX264"))
                sb.Append(" V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC (codec h264)\n");
            sb.Append(" V....D mpeg4                MPEG-4 part 2\n");
            return sb.ToString();
        }

        /// <summary>没有任何编码器表行，但普通文本提到了 libx264。</summary>
        private static string EncodersNoiseText()
        {
            var sb = new StringBuilder();
            sb.Append("Encoders:\n");
            sb.Append("Note: this build does not list libx264 in the table below.\n");
            sb.Append("See https://example.invalid/libx264 for details.\n");
            return sb.ToString();
        }

        private static string FormatsText()
        {
            var sb = new StringBuilder();
            sb.Append("File formats:\n");
            sb.Append(" D. = Demuxing supported\n");
            sb.Append(" .E = Muxing supported\n");
            sb.Append(" --\n");
            sb.Append(" D  rawvideo         raw video\n");
            sb.Append(" DE mov,mp4,m4a,3gp,3g2,mj2 QuickTime / MOV\n");
            return sb.ToString();
        }

        /// <summary>没有任何格式表行，但普通文本提到了 mp4 与 rawvideo。</summary>
        private static string FormatsNoiseText()
        {
            var sb = new StringBuilder();
            sb.Append("File formats:\n");
            sb.Append(" D. = Demuxing supported\n");
            sb.Append(" .E = Muxing supported\n");
            sb.Append(" --\n");
            sb.Append("The mp4 muxer and the rawvideo demuxer are not listed here.\n");
            return sb.ToString();
        }
    }
}
