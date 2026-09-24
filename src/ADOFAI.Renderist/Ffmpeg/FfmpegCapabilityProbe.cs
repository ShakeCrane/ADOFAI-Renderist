using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ADOFAI.Renderist.Ffmpeg
{
    internal enum FfmpegCapabilityStatus
    {
        NotProbed = 0,
        Probed = 1,
        ProbeFailed = 2,
    }

    /// <summary>
    /// FFmpeg 能力检查结果。
    ///
    /// 重要边界：本报告**只**描述该二进制是否具备未来 MP4 / H.264 输出所需的能力。
    /// 它**不是** PNG / Log-only 导出的前置条件 —— FFmpeg 缺失或能力不足绝不能阻断既有导出。
    /// </summary>
    internal sealed class FfmpegCapabilityReport
    {
        public FfmpegCapabilityStatus Status { get; set; }

        /// <summary>被探测的可执行文件绝对路径。</summary>
        public string ExecutablePath { get; set; }

        /// <summary>探测时该文件的内容哈希，用于确认报告对应哪个二进制。</summary>
        public string ExecutableSha256 { get; set; }

        public string VersionLine { get; set; }

        /// <summary>从 <c>-version</c> 的 configuration 行解析出的构建配置（可能为空）。</summary>
        public string Configuration { get; set; }

        /// <summary>构建配置中出现 <c>--enable-gpl</c>。</summary>
        public bool? ConfigurationEnablesGpl { get; set; }

        /// <summary>构建配置中出现 <c>--enable-libx264</c>。</summary>
        public bool? ConfigurationEnablesLibx264 { get; set; }

        public bool HasLibx264 { get; set; }
        public bool HasMp4Muxer { get; set; }
        public bool HasRawvideoDemuxer { get; set; }

        /// <summary>缺失的能力项（机读名称）。空 = 具备全部要求。</summary>
        public IReadOnlyList<string> MissingCapabilities { get; set; }

        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }

        public bool IsUsableForMp4
        {
            get { return Status == FfmpegCapabilityStatus.Probed && MissingCapabilities != null && MissingCapabilities.Count == 0; }
        }
    }

    /// <summary>
    /// FFmpeg 版本与必要编码能力的**一次性探测**（唯一单点）。
    ///
    /// 这是短进程探测，不是帧流会话：每个探测进程读少量文本后立即退出。
    /// 生产级帧流进程（L2）不在本阶段范围内。
    ///
    /// 不接触 Unity / UMM / Harmony。
    /// </summary>
    internal static class FfmpegCapabilityProbe
    {
        public const int DefaultTimeoutSeconds = 20;

        /// <summary>未来 MP4 / H.264 输出所需的最小能力集合。</summary>
        public static readonly string[] RequiredEncoders = { "libx264" };
        public static readonly string[] RequiredMuxers = { "mp4" };
        public static readonly string[] RequiredDemuxers = { "rawvideo" };

        private static readonly Regex FormatLineRegex =
            new Regex(@"^\s*([D ])([E ])\s+(\S+)\s", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static FfmpegCapabilityReport Probe(string executablePath, CancellationToken cancellationToken)
        {
            return Probe(executablePath, DefaultTimeoutSeconds, cancellationToken);
        }

        public static FfmpegCapabilityReport Probe(
            string executablePath, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var report = new FfmpegCapabilityReport
            {
                ExecutablePath = executablePath,
                Status = FfmpegCapabilityStatus.NotProbed,
                MissingCapabilities = new string[0],
            };

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                report.Status = FfmpegCapabilityStatus.ProbeFailed;
                report.ErrorCode = "executable-path-empty";
                return report;
            }

            string sha256;
            string hashError;
            if (FfmpegFileHash.TryCompute(executablePath, out sha256, out hashError))
                report.ExecutableSha256 = sha256;

            string versionOut;
            string versionErr;
            int versionExit;
            string versionError;
            if (!TryRun(executablePath, "-hide_banner -version", timeoutSeconds, cancellationToken,
                    out versionOut, out versionErr, out versionExit, out versionError))
            {
                report.Status = FfmpegCapabilityStatus.ProbeFailed;
                report.ErrorCode = versionError;
                return report;
            }

            if (versionExit != 0)
            {
                report.Status = FfmpegCapabilityStatus.ProbeFailed;
                report.ErrorCode = "version-probe-nonzero-exit";
                report.ErrorDetail = "exit=" + versionExit;
                return report;
            }

            report.VersionLine = FirstNonEmptyLine(versionOut);
            report.Configuration = ExtractConfiguration(versionOut);
            if (report.Configuration != null)
            {
                report.ConfigurationEnablesGpl =
                    report.Configuration.IndexOf("--enable-gpl", StringComparison.OrdinalIgnoreCase) >= 0;
                report.ConfigurationEnablesLibx264 =
                    report.Configuration.IndexOf("--enable-libx264", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            string encodersOut;
            string encodersErr;
            int encodersExit;
            string encodersError;
            if (!TryRun(executablePath, "-hide_banner -encoders", timeoutSeconds, cancellationToken,
                    out encodersOut, out encodersErr, out encodersExit, out encodersError))
            {
                report.Status = FfmpegCapabilityStatus.ProbeFailed;
                report.ErrorCode = encodersError;
                return report;
            }

            string formatsOut;
            string formatsErr;
            int formatsExit;
            string formatsError;
            if (!TryRun(executablePath, "-hide_banner -formats", timeoutSeconds, cancellationToken,
                    out formatsOut, out formatsErr, out formatsExit, out formatsError))
            {
                report.Status = FfmpegCapabilityStatus.ProbeFailed;
                report.ErrorCode = formatsError;
                return report;
            }

            report.HasLibx264 = ContainsLineToken(encodersOut, "libx264");
            report.HasMp4Muxer = HasFormat(formatsOut, "mp4", true);
            report.HasRawvideoDemuxer = HasFormat(formatsOut, "rawvideo", false);

            var missing = new List<string>();
            if (!report.HasLibx264)
                missing.Add("encoder:libx264");
            if (!report.HasMp4Muxer)
                missing.Add("muxer:mp4");
            if (!report.HasRawvideoDemuxer)
                missing.Add("demuxer:rawvideo");

            report.MissingCapabilities = missing;
            report.Status = FfmpegCapabilityStatus.Probed;
            return report;
        }

        private static string FirstNonEmptyLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0)
                    return line;
            }
            return null;
        }

        private static string ExtractConfiguration(string versionOutput)
        {
            if (string.IsNullOrEmpty(versionOutput))
                return null;

            string[] lines = versionOutput.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("configuration:".Length).Trim();
            }
            return null;
        }

        private static bool ContainsLineToken(string output, string token)
        {
            if (string.IsNullOrEmpty(output))
                return false;

            string[] lines = output.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                for (int p = 0; p < parts.Length; p++)
                {
                    if (string.Equals(parts[p], token, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 在 <c>-formats</c> 输出中查找同时包含指定名称且具有 D/E 标志的格式行。
        /// 名称列可能是逗号分隔的别名组（例如 <c>mov,mp4,m4a,3gp,3g2,mj2</c>）。
        /// </summary>
        private static bool HasFormat(string formatsOutput, string name, bool requireMuxing)
        {
            if (string.IsNullOrEmpty(formatsOutput))
                return false;

            string[] lines = formatsOutput.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = FormatLineRegex.Match(lines[i]);
                if (!match.Success)
                    continue;

                bool demuxing = match.Groups[1].Value[0] == 'D';
                bool muxing = match.Groups[2].Value[0] == 'E';

                if (requireMuxing && !muxing)
                    continue;
                if (!requireMuxing && !demuxing)
                    continue;

                string[] aliases = match.Groups[3].Value.Split(',');
                for (int a = 0; a < aliases.Length; a++)
                {
                    if (string.Equals(aliases[a], name, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 运行一个短探测进程并完整捕获 stdout / stderr。
        /// 同时读取两个流，因此任一管道写满都不会造成死锁；超时或取消都会终止进程。
        /// </summary>
        private static bool TryRun(
            string executablePath,
            string arguments,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            out string standardOutput,
            out string standardError,
            out int exitCode,
            out string error)
        {
            standardOutput = null;
            standardError = null;
            exitCode = int.MinValue;
            error = null;

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            Process process = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(executablePath) ?? string.Empty,
                };

                process = new Process { StartInfo = startInfo };

                using (var stdoutDone = new ManualResetEventSlim(false))
                using (var stderrDone = new ManualResetEventSlim(false))
                {
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data == null) stdoutDone.Set();
                        else stdout.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data == null) stderrDone.Set();
                        else stderr.AppendLine(e.Data);
                    };

                    if (!process.Start())
                    {
                        error = "process-start-returned-false";
                        return false;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // 探测进程不需要 stdin；立即关闭以免个别构建等待输入。
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch
                    {
                    }

                    int timeoutMs = timeoutSeconds > 0 ? timeoutSeconds * 1000 : Timeout.Infinite;

                    using (cancellationToken.Register(() => TryKill(process)))
                    {
                        if (!process.WaitForExit(timeoutMs))
                        {
                            TryKill(process);
                            process.WaitForExit(5000);
                            error = cancellationToken.IsCancellationRequested
                                ? "probe-cancelled"
                                : "probe-timeout";
                            return false;
                        }
                    }

                    // 无参 WaitForExit 会等待异步读取处理器排空，避免截断输出。
                    process.WaitForExit();

                    stdoutDone.Wait(2000);
                    stderrDone.Wait(2000);

                    standardOutput = stdout.ToString();
                    standardError = stderr.ToString();
                    exitCode = process.ExitCode;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "probe-failed: " + ex.Message;
                return false;
            }
            finally
            {
                if (process != null)
                {
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
    }
}
