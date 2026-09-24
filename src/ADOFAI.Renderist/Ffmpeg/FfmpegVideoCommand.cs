using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 启动一个 FFmpeg 子进程。生产实现是 <see cref="Process.Start(ProcessStartInfo)"/>；
    /// 回归测试注入自己的实现，以便在不需要真实 FFmpeg 的情况下覆盖进程生命周期契约。
    /// 这不是通用进程框架：整个 L2 只有这一处进程启动缝。
    /// </summary>
    internal delegate Process FfmpegVideoProcessStart(string executablePath, string arguments, string workingDirectory);

    /// <summary>
    /// 输出像素格式。
    ///
    /// 这是**编码器条件**，不是产品分辨率上限：libx264 的 4:2:0 需要宽高都能被 2 整除，
    /// 奇数尺寸只能用 4:4:4。两者都已在真实 Gyan 9.0.2 上实测（见
    /// PROJECT_UNDERSTANDING.md §2.8）。绝不为了满足 4:2:0 去静默改写输出宽高。
    /// </summary>
    internal enum FfmpegVideoPixelFormat
    {
        Yuv420p = 0,
        Yuv444p = 1,
    }

    internal static class FfmpegVideoPixelFormatPolicy
    {
        public const string Yuv420pToken = "yuv420p";
        public const string Yuv444pToken = "yuv444p";

        /// <summary>
        /// 宽高都能被 2 整除 → yuv420p（播放器兼容面最广）；
        /// 否则 → yuv444p（libx264 在奇数尺寸下唯一可用的像素格式）。
        /// </summary>
        public static FfmpegVideoPixelFormat Select(int width, int height)
        {
            bool even = (width % 2) == 0 && (height % 2) == 0;
            return even ? FfmpegVideoPixelFormat.Yuv420p : FfmpegVideoPixelFormat.Yuv444p;
        }

        public static string ToToken(FfmpegVideoPixelFormat format)
        {
            return format == FfmpegVideoPixelFormat.Yuv444p ? Yuv444pToken : Yuv420pToken;
        }

        public static bool TryParseToken(string token, out FfmpegVideoPixelFormat format)
        {
            format = FfmpegVideoPixelFormat.Yuv420p;
            if (string.IsNullOrEmpty(token))
                return false;

            if (string.Equals(token, Yuv420pToken, StringComparison.Ordinal))
                return true;

            if (string.Equals(token, Yuv444pToken, StringComparison.Ordinal))
            {
                format = FfmpegVideoPixelFormat.Yuv444p;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 一次会话的**冻结**编码配置。
    ///
    /// 冻结语义：会话开始时一次性解析（分辨率 / FPS / 像素格式 / 编码参数），
    /// 之后不再读取 Settings 或任何运行期状态。运行中修改 GUI 不影响当前会话。
    ///
    /// 合法性只有真实约束：int 正整数的宽高与 FPS、x264 真实存在的 preset 与 CRF 取值域，
    /// 以及 <c>checked</c> 表达的单帧字节数。没有产品级 FPS / 分辨率 / 帧数 / 体积上限。
    /// </summary>
    internal sealed class FfmpegVideoSettings
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }

        /// <summary>x264 CRF。默认 18。</summary>
        public int Crf { get; set; } = FfmpegVideoCommand.DefaultCrf;

        /// <summary>x264 preset。默认 medium。</summary>
        public string Preset { get; set; } = FfmpegVideoCommand.DefaultPreset;

        /// <summary>
        /// 像素格式覆盖。null = 由 <see cref="FfmpegVideoPixelFormatPolicy"/> 按真实几何选择。
        /// </summary>
        public FfmpegVideoPixelFormat? PixelFormat { get; set; }

        public FfmpegVideoPixelFormat ResolvePixelFormat()
        {
            return PixelFormat ?? FfmpegVideoPixelFormatPolicy.Select(Width, Height);
        }

        /// <summary>单帧完整 RGB24 字节数：<c>checked((long)width * height * 3)</c>。</summary>
        public bool TryGetFrameLengthBytes(out long lengthBytes, out string errorCode)
        {
            lengthBytes = 0;
            errorCode = null;

            if (Width <= 0)
            {
                errorCode = "settings-width-invalid";
                return false;
            }

            if (Height <= 0)
            {
                errorCode = "settings-height-invalid";
                return false;
            }

            try
            {
                lengthBytes = checked((long)Width * Height * 3);
            }
            catch (OverflowException)
            {
                errorCode = "settings-frame-length-overflow";
                return false;
            }

            return true;
        }

        public bool TryValidate(out string errorCode, out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;

            long frameLength;
            string lengthError;
            if (!TryGetFrameLengthBytes(out frameLength, out lengthError))
            {
                errorCode = lengthError;
                return false;
            }

            if (Fps <= 0)
            {
                errorCode = "settings-fps-invalid";
                return false;
            }

            if (Crf < FfmpegVideoCommand.MinCrf || Crf > FfmpegVideoCommand.MaxCrf)
            {
                errorCode = "settings-crf-out-of-range";
                errorDetail = "crf=" + Crf.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (!FfmpegVideoCommand.IsKnownPreset(Preset))
            {
                errorCode = "settings-preset-unknown";
                errorDetail = "preset=" + (Preset ?? "null");
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// FFmpeg 视频编码命令与 Windows 参数引用的唯一单点（Unity-free）。
    ///
    /// 命令形状由 2026 年在真实 Gyan 9.0.2（`3256173F…`）上的定向实验确定，实测结论：
    ///
    ///   * 直接把目标 FPS 交给 rawvideo <c>-framerate</c> 时，FFmpeg 9.0.2 的
    ///     <c>av_parse_video_rate</c> 路径会把大于 <c>1001000</c> 的值**静默降为 1001000/1**。
    ///     因此大 FPS 必须用 <c>settb=1/&lt;fps&gt;,setpts=N</c> 重新建立时间基，
    ///     输入侧 framerate 只取 <c>min(fps, 1001000)</c>（并且**显式**取，不依赖静默降级）。
    ///   * <c>-fps_mode passthrough -enc_time_base filter</c> 让编码器使用 filter 输出的时间基，
    ///     避免按输入 framerate 做 CFR 复制/丢帧。
    ///   * <c>-bsf:v setts=duration=1 -video_track_timescale &lt;fps&gt;</c> 让容器逐包时长为 1 tick、
    ///     有理时间基为 1/fps；实测 1001001 与 2147483647 都能逐帧精确往返。
    ///   * <c>-bf 0</c> 是必须项：保留 B 帧时容器仍报 100 个包，但只能解出 96 帧
    ///     （Gyan 9.0.2 + <c>-preset medium</c> 实测），因此必须由解码帧数核验而不是只看容器。
    ///   * <c>-xerror</c> + <c>-abort_on empty_output_stream</c> 把"最后一帧被截断"和"空输入"
    ///     从静默成功变成非零退出。
    ///
    /// 命令**不**包含任何写盘之外的副作用：不使用 `-y` 之外的文件选项，不修改输出几何。
    /// </summary>
    internal static class FfmpegVideoCommand
    {
        /// <summary>
        /// rawvideo 输入 framerate 的可表达上限。实测：直接传入更大的值会被
        /// FFmpeg 9.0.2 静默降为 1001000/1，所以这里显式取 min(fps, 1001000)，
        /// 真正的输出帧率始终由 settb/setpts 与 video_track_timescale 表达。
        /// </summary>
        public const int MaxInputFramerate = 1001000;

        public const int DefaultCrf = 18;
        public const string DefaultPreset = "medium";
        public const int MinCrf = 0;
        public const int MaxCrf = 51;

        /// <summary>libx264 真实存在的 preset 名称（x264 侧条件，不是产品偏好）。</summary>
        private static readonly string[] KnownPresets =
        {
            "ultrafast", "superfast", "veryfast", "faster", "fast",
            "medium", "slow", "slower", "veryslow", "placebo",
        };

        public static bool IsKnownPreset(string preset)
        {
            if (string.IsNullOrEmpty(preset))
                return false;

            for (int i = 0; i < KnownPresets.Length; i++)
            {
                if (string.Equals(KnownPresets[i], preset, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>rawvideo 输入侧使用的 framerate（显式 min，不依赖静默降级）。</summary>
        public static int ResolveInputFramerate(int fps)
        {
            return fps <= MaxInputFramerate ? fps : MaxInputFramerate;
        }

        /// <summary>
        /// 构造完整编码命令行（不含可执行文件本身）。
        /// 失败时返回 false 并给出机读错误码，绝不返回"部分正确"的命令。
        /// </summary>
        public static bool TryBuildEncodeArguments(
            FfmpegVideoSettings settings, string outputPath, out string arguments, out string errorCode, out string errorDetail)
        {
            arguments = null;
            errorCode = null;
            errorDetail = null;

            if (settings == null)
            {
                errorCode = "settings-null";
                return false;
            }

            string validationCode;
            string validationDetail;
            if (!settings.TryValidate(out validationCode, out validationDetail))
            {
                errorCode = validationCode;
                errorDetail = validationDetail;
                return false;
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                errorCode = "output-path-empty";
                return false;
            }

            int fps = settings.Fps;
            int inputFramerate = ResolveInputFramerate(fps);
            string pixelFormat = FfmpegVideoPixelFormatPolicy.ToToken(settings.ResolvePixelFormat());

            var sb = new StringBuilder(256);
            sb.Append("-hide_banner -nostdin -loglevel error");
            sb.Append(" -f rawvideo -pix_fmt rgb24 -s ");
            sb.Append(settings.Width.ToString(CultureInfo.InvariantCulture));
            sb.Append('x');
            sb.Append(settings.Height.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -framerate ");
            sb.Append(inputFramerate.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -i pipe:0");
            sb.Append(" -vf settb=1/");
            sb.Append(fps.ToString(CultureInfo.InvariantCulture));
            sb.Append(",setpts=N");
            sb.Append(" -fps_mode passthrough -enc_time_base filter");
            sb.Append(" -bsf:v setts=duration=1");
            sb.Append(" -c:v libx264 -preset ");
            sb.Append(settings.Preset);
            sb.Append(" -crf ");
            sb.Append(settings.Crf.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -bf 0");
            sb.Append(" -pix_fmt ");
            sb.Append(pixelFormat);
            sb.Append(" -video_track_timescale ");
            sb.Append(fps.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -xerror -abort_on empty_output_stream");
            sb.Append(" -f mp4 -y ");
            sb.Append(QuoteArgument(outputPath));

            arguments = sb.ToString();
            return true;
        }

        /// <summary>
        /// 解码核验命令行：真正解码每一帧并输出 framemd5（逐帧 pts/duration/尺寸 + 时间基头），
        /// 同时以 <c>-loglevel info</c> 让 stderr 给出流信息（编码器 / 像素格式 / 尺寸）。
        ///
        /// <c>-nostats</c> 抑制周期性进度行，使 stderr 有界。
        /// </summary>
        public static string BuildVerifyDecodeArguments(string videoPath)
        {
            return "-hide_banner -nostdin -nostats -loglevel info -xerror -i " + QuoteArgument(videoPath) +
                   " -map 0:v:0 -an -fps_mode passthrough -f framemd5 -";
        }

        /// <summary>
        /// 容器/包级核验命令行：不解码，按流拷贝输出逐包 dts/pts/duration。
        ///
        /// 与解码核验是**两种不同语义**，必须分开验证：容器可以声明 N 个包而解码器
        /// 只产出更少的帧（B 帧负对照实测 100 包 / 96 帧）。
        /// </summary>
        public static string BuildVerifyPacketArguments(string videoPath)
        {
            return "-hide_banner -nostdin -nostats -loglevel error -xerror -i " + QuoteArgument(videoPath) +
                   " -map 0:v:0 -an -c copy -f framemd5 -";
        }

        /// <summary>
        /// Windows 命令行参数引用（CommandLineToArgvW 规则）：包含空白或引号时整体加引号，
        /// 引号前的反斜杠加倍，结尾反斜杠加倍。路径可能出现空格、中文、&amp;、括号与 emoji。
        /// </summary>
        public static string QuoteArgument(string argument)
        {
            if (argument == null)
                return "\"\"";

            bool needsQuotes = false;
            for (int i = 0; i < argument.Length; i++)
            {
                char c = argument[i];
                if (c == ' ' || c == '\t' || c == '"')
                {
                    needsQuotes = true;
                    break;
                }
            }

            if (!needsQuotes)
                return argument.Length == 0 ? "\"\"" : argument;

            var sb = new StringBuilder(argument.Length + 8);
            sb.Append('"');
            int backslashes = 0;

            for (int i = 0; i < argument.Length; i++)
            {
                char c = argument[i];

                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }

                sb.Append(c);
            }

            if (backslashes > 0)
                sb.Append('\\', backslashes * 2);

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>临时产物路径：同目录 + 唯一 token，绝不与正式目标文件同名。</summary>
        public const string TempFileInfix = ".renderist-partial-";

        public static bool TryBuildTempPath(string finalPath, string token, out string tempPath, out string errorCode)
        {
            tempPath = null;
            errorCode = null;

            if (string.IsNullOrWhiteSpace(finalPath))
            {
                errorCode = "final-path-empty";
                return false;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                errorCode = "temp-token-empty";
                return false;
            }

            string directory;
            string fileName;
            try
            {
                directory = Path.GetDirectoryName(finalPath);
                fileName = Path.GetFileName(finalPath);
            }
            catch (Exception)
            {
                errorCode = "final-path-invalid";
                return false;
            }

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                errorCode = "final-path-invalid";
                return false;
            }

            // 同目录 ⇒ 同卷 ⇒ 发布可用一次 Move 完成（不跨卷复制）。
            tempPath = Path.Combine(directory, fileName + TempFileInfix + token);
            return true;
        }
    }
}
