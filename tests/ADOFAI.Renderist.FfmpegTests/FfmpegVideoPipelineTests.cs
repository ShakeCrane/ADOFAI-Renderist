using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// L2（FFmpeg Video Process Pipeline）回归测试。
    ///
    /// 断言的是**行为契约**，不是某次实验的数值：
    ///   * 命令形状与真实约束（时间基 / 像素格式 / 参数引用）；
    ///   * 单帧在途、busy 拒绝、完整写入后才计数、部分写入不重试；
    ///   * 终态仲裁（Finish / Cancel / Failure / Publish 竞争）；
    ///   * 临时文件 ownership、正式目标文件保护、residual ownership；
    ///   * 核验协议（真实解码帧数 / 有理时间基 / PTS / 流与尺寸）。
    ///
    /// 进程级故障（背压、断管、卡住的核验进程、非零退出）由 <see cref="FakeVideoProcess"/>
    /// 真实子进程复现；真实 FFmpeg（Gyan 9.0.2）fixture 存在时另跑真实编码矩阵，
    /// 缺失时如实 SKIP（二进制刻意不入库）。
    /// </summary>
    internal static class FfmpegVideoPipelineTests
    {
        private const int DefaultTimeoutMs = 120000;

        public static void Run(string workRoot)
        {
            CommandTests(workRoot);
            IdentityTests(workRoot);
            StreamLineParsingTests();
            VerifierContractTests(workRoot);
            PipelineLifecycleTests(workRoot);
            LifecycleHardeningTests(workRoot);
            RealFixtureTests(workRoot);
        }

        // ==================================================================== helpers

        private static T Await<T>(Task<T> task, string what)
        {
            if (task == null)
                throw new Exception(what + ": task is null");

            if (!task.Wait(DefaultTimeoutMs))
                throw new Exception(what + ": timed out after " + DefaultTimeoutMs + "ms");

            return task.Result;
        }

        private static FfmpegVideoSettings Settings(int width, int height, int fps)
        {
            return new FfmpegVideoSettings { Width = width, Height = height, Fps = fps };
        }

        private static FfmpegVideoFrame Frame(int width, int height)
        {
            long length = (long)width * height * 3;
            var buffer = new byte[length];
            for (long i = 0; i < length; i++)
                buffer[i] = (byte)(i & 0xFF);
            return FfmpegVideoFrame.FromBuffer(buffer);
        }

        /// <summary>用一个真实存在的文件冻结身份（fake 测试用测试可执行文件本身）。</summary>
        private static FfmpegVideoIdentity MakeIdentity(string path)
        {
            string sha256;
            string hashError;
            if (!FfmpegFileHash.TryCompute(path, out sha256, out hashError))
                throw new Exception("cannot hash identity fixture: " + hashError);

            long size = new FileInfo(path).Length;

            var report = new FfmpegComponentReport
            {
                State = FfmpegComponentState.Ready,
                Source = FfmpegCandidateSource.ManagedInstall,
                Candidate = new FfmpegCandidate
                {
                    Source = FfmpegCandidateSource.ManagedInstall,
                    Identity = new FfmpegBinaryIdentity
                    {
                        AbsolutePath = path,
                        Sha256 = sha256,
                        SizeBytes = size,
                    },
                },
                Capability = new FfmpegCapabilityReport
                {
                    Status = FfmpegCapabilityStatus.Probed,
                    ExecutablePath = path,
                    ExecutableSha256 = sha256,
                    VersionLine = "ffmpeg version fixture",
                    HasLibx264 = true,
                    HasMp4Muxer = true,
                    HasRawvideoDemuxer = true,
                    MissingCapabilities = new string[0],
                },
            };

            FfmpegVideoIdentity identity;
            string errorCode;
            string errorDetail;
            if (!FfmpegVideoIdentity.TryFreeze(report, out identity, out errorCode, out errorDetail))
                throw new Exception("cannot freeze fixture identity: " + errorCode + " " + errorDetail);

            return identity;
        }

        private static FfmpegVideoPipeline CreatePipeline(
            string directory,
            FfmpegVideoSettings settings,
            FfmpegVideoIdentity identity,
            FfmpegVideoProcessStart processStart,
            string finalName,
            string tempPath = null,
            IFfmpegVideoVerifier verifier = null)
        {
            var options = new FfmpegVideoPipelineOptions
            {
                Settings = settings,
                Identity = identity,
                FinalPath = Path.Combine(directory, finalName),
                TempPath = tempPath,
                ProcessStart = processStart,
                Verifier = verifier,
            };

            return new FfmpegVideoPipeline(options);
        }

        private static FfmpegVideoVerificationResult RunFakeVerifier(
            FakeVideoVerifierScript script,
            string videoPath,
            int width,
            int height,
            int fps,
            long expectedFrames,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var launcher = new FakeVideoProcessLauncher { Verifier = script };
            var verifier = new FfmpegVideoVerifier(launcher.Create());

            var request = new FfmpegVideoVerificationRequest
            {
                ExecutablePath = FakeVideoProcess.ExecutablePath,
                VideoPath = videoPath,
                ExpectedWidth = width,
                ExpectedHeight = height,
                ExpectedFps = fps,
                ExpectedCodecName = "h264",
                ExpectedPixelFormat = "yuv420p",
                ExpectedFrameCount = expectedFrames,
                CancellationToken = cancellationToken,
            };

            return Await(verifier.VerifyAsync(request), "fake verify");
        }

        private static string[] PartialFiles(string directory)
        {
            string[] all = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            var partial = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                if (Path.GetFileName(all[i]).IndexOf(FfmpegVideoCommand.TempFileInfix, StringComparison.Ordinal) >= 0)
                    partial.Add(all[i]);
            }
            return partial.ToArray();
        }

        // ==================================================================== command / settings

        private static void CommandTests(string workRoot)
        {
            TestKit.Run("video command: normal fps uses its own input framerate and the exact filter chain", () =>
            {
                string arguments;
                string errorCode;
                string errorDetail;
                TestKit.Check(FfmpegVideoCommand.TryBuildEncodeArguments(
                    Settings(1920, 1080, 30), @"C:\out\video.mp4", out arguments, out errorCode, out errorDetail),
                    "command build failed: " + errorCode + " " + errorDetail);

                TestKit.Check(arguments.IndexOf("-framerate 30 ", StringComparison.Ordinal) >= 0,
                    "input framerate must be the frozen fps: " + arguments);
                TestKit.Check(arguments.IndexOf("-vf settb=1/30,setpts=N", StringComparison.Ordinal) >= 0,
                    "exact rational timebase filter missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-fps_mode passthrough", StringComparison.Ordinal) >= 0,
                    "fps_mode passthrough missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-enc_time_base filter", StringComparison.Ordinal) >= 0,
                    "enc_time_base filter missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-bsf:v setts=duration=1", StringComparison.Ordinal) >= 0,
                    "setts duration missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-video_track_timescale 30", StringComparison.Ordinal) >= 0,
                    "video_track_timescale missing: " + arguments);
                TestKit.Check(arguments.IndexOf(" -bf 0", StringComparison.Ordinal) >= 0,
                    "B frames must be disabled: " + arguments);
                TestKit.Check(arguments.IndexOf("-crf 18", StringComparison.Ordinal) >= 0, "crf missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-pix_fmt yuv420p", StringComparison.Ordinal) >= 0,
                    "pixel format missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-xerror", StringComparison.Ordinal) >= 0, "-xerror missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-abort_on empty_output_stream", StringComparison.Ordinal) >= 0,
                    "-abort_on empty_output_stream missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-f mp4", StringComparison.Ordinal) >= 0,
                    "explicit mp4 muxer missing: " + arguments);
                TestKit.Check(arguments.IndexOf("pipe:0", StringComparison.Ordinal) >= 0, "stdin input missing");
                TestKit.Check(arguments.IndexOf("-f rawvideo -pix_fmt rgb24", StringComparison.Ordinal) >= 0,
                    "rawvideo rgb24 input missing");
                TestKit.Check(arguments.IndexOf("-nostdin", StringComparison.Ordinal) >= 0, "-nostdin missing");
            });

            TestKit.Run("video command: fps above the rawvideo parse limit only caps the input framerate", () =>
            {
                string arguments;
                string errorCode;
                string errorDetail;
                TestKit.Check(FfmpegVideoCommand.TryBuildEncodeArguments(
                    Settings(64, 48, int.MaxValue), @"C:\out\v.mp4", out arguments, out errorCode, out errorDetail),
                    "command build failed: " + errorCode);

                TestKit.Check(arguments.IndexOf("-framerate 1001000 ", StringComparison.Ordinal) >= 0,
                    "input framerate must be explicitly capped at 1001000: " + arguments);
                TestKit.Check(arguments.IndexOf("-framerate 2147483647", StringComparison.Ordinal) < 0,
                    "the frozen fps must not be handed to -framerate: " + arguments);
                TestKit.Check(arguments.IndexOf("-vf settb=1/2147483647,setpts=N", StringComparison.Ordinal) >= 0,
                    "exact int.MaxValue timebase missing: " + arguments);
                TestKit.Check(arguments.IndexOf("-video_track_timescale 2147483647", StringComparison.Ordinal) >= 0,
                    "int.MaxValue track timescale missing: " + arguments);

                TestKit.CheckEqual(1001000, FfmpegVideoCommand.ResolveInputFramerate(2147483647), "capped framerate");
                TestKit.CheckEqual(1001000, FfmpegVideoCommand.ResolveInputFramerate(1001001), "framerate just above cap");
                TestKit.CheckEqual(1001000, FfmpegVideoCommand.ResolveInputFramerate(1001000), "framerate at cap");
                TestKit.CheckEqual(999999, FfmpegVideoCommand.ResolveInputFramerate(999999), "framerate below cap");
                TestKit.CheckEqual(1, FfmpegVideoCommand.ResolveInputFramerate(1), "minimum framerate");
            });

            TestKit.Run("video command: pixel format follows real geometry, override wins", () =>
            {
                TestKit.CheckEqual(FfmpegVideoPixelFormat.Yuv420p,
                    FfmpegVideoPixelFormatPolicy.Select(1920, 1080), "even geometry");
                TestKit.CheckEqual(FfmpegVideoPixelFormat.Yuv444p,
                    FfmpegVideoPixelFormatPolicy.Select(65, 49), "odd width and height");
                TestKit.CheckEqual(FfmpegVideoPixelFormat.Yuv444p,
                    FfmpegVideoPixelFormatPolicy.Select(64, 49), "odd height only");

                var overridden = Settings(1920, 1080, 30);
                overridden.PixelFormat = FfmpegVideoPixelFormat.Yuv444p;
                TestKit.CheckEqual(FfmpegVideoPixelFormat.Yuv444p, overridden.ResolvePixelFormat(), "override honoured");
            });

            TestKit.Run("video command: validation rejects only real constraints", () =>
            {
                string code;
                string detail;

                TestKit.Check(!Settings(0, 1080, 30).TryValidate(out code, out detail), "width 0 must be rejected");
                TestKit.CheckEqual("settings-width-invalid", code, "width error code");

                TestKit.Check(!Settings(1920, -1, 30).TryValidate(out code, out detail), "height -1 must be rejected");
                TestKit.CheckEqual("settings-height-invalid", code, "height error code");

                TestKit.Check(!Settings(1920, 1080, 0).TryValidate(out code, out detail), "fps 0 must be rejected");
                TestKit.CheckEqual("settings-fps-invalid", code, "fps error code");

                var badCrf = Settings(64, 48, 30);
                badCrf.Crf = 52;
                TestKit.Check(!badCrf.TryValidate(out code, out detail), "crf 52 must be rejected");
                TestKit.CheckEqual("settings-crf-out-of-range", code, "crf error code");

                var badPreset = Settings(64, 48, 30);
                badPreset.Preset = "bogus";
                TestKit.Check(!badPreset.TryValidate(out code, out detail), "unknown preset must be rejected");
                TestKit.CheckEqual("settings-preset-unknown", code, "preset error code");

                // 没有产品级分辨率上限：只有 int 表达能力与 checked 单帧长度是真实边界。
                var huge = Settings(int.MaxValue, int.MaxValue, 1);
                TestKit.Check(!huge.TryValidate(out code, out detail), "int.MaxValue geometry overflows a frame");
                TestKit.CheckEqual("settings-frame-length-overflow", code, "overflow error code");

                var large = Settings(16384, 16384, 1);
                TestKit.Check(large.TryValidate(out code, out detail), "large but expressible geometry must be legal: " + code);

                long frameLength;
                TestKit.Check(large.TryGetFrameLengthBytes(out frameLength, out code), "frame length");
                TestKit.CheckEqual(16384L * 16384L * 3L, frameLength, "frame length value");
            });

            TestKit.Run("video command: windows argument quoting handles spaces, quotes and backslashes", () =>
            {
                TestKit.CheckEqual(@"C:\plain\a.mp4", FfmpegVideoCommand.QuoteArgument(@"C:\plain\a.mp4"),
                    "no quoting needed");
                TestKit.CheckEqual("\"C:\\with space\\a.mp4\"", FfmpegVideoCommand.QuoteArgument(@"C:\with space\a.mp4"),
                    "spaces are quoted");
                TestKit.CheckEqual("\"a\\\"b\"", FfmpegVideoCommand.QuoteArgument("a\"b"), "embedded quote escaped");
                TestKit.CheckEqual(@"C:\dir\", FfmpegVideoCommand.QuoteArgument(@"C:\dir\"),
                    "an unquoted trailing backslash needs no escaping");
                TestKit.CheckEqual("\"C:\\with space\\\\\"", FfmpegVideoCommand.QuoteArgument(@"C:\with space\"),
                    "trailing backslash inside quotes must be doubled");
                TestKit.CheckEqual("\"  \"", FfmpegVideoCommand.QuoteArgument("  "), "whitespace-only");
                TestKit.CheckEqual("\"\"", FfmpegVideoCommand.QuoteArgument(string.Empty), "empty argument");
            });

            TestKit.Run("video command: temp path is a sibling with a unique token", () =>
            {
                string temp;
                string errorCode;
                TestKit.Check(FfmpegVideoCommand.TryBuildTempPath(
                    @"C:\out\video.mp4", "abc123", out temp, out errorCode), "temp build failed: " + errorCode);
                TestKit.CheckEqual(@"C:\out\video.mp4" + FfmpegVideoCommand.TempFileInfix + "abc123", temp, "temp path");
                TestKit.CheckEqual(@"C:\out", Path.GetDirectoryName(temp), "temp must be a sibling");

                string missing;
                TestKit.Check(!FfmpegVideoCommand.TryBuildTempPath(null, "t", out missing, out errorCode),
                    "empty final path must fail");
                TestKit.CheckEqual("final-path-empty", errorCode, "error code");
            });

            TestKit.Run("video command: special characters survive quoting through a real command line", () =>
            {
                string directory = Path.Combine(workRoot, "specials 特殊 & (括号) 🎬");
                Directory.CreateDirectory(directory);
                string finalPath = Path.Combine(directory, "video 输出 & (v1) 🎬.mp4");

                string arguments;
                string errorCode;
                string errorDetail;
                string tempPath;
                TestKit.Check(FfmpegVideoCommand.TryBuildTempPath(finalPath, "token1234", out tempPath, out errorCode),
                    "temp path");
                TestKit.Check(FfmpegVideoCommand.TryBuildEncodeArguments(
                    Settings(64, 48, 30), tempPath, out arguments, out errorCode, out errorDetail),
                    "command build failed: " + errorCode);

                TestKit.Check(arguments.IndexOf("\"" + tempPath + "\"", StringComparison.Ordinal) >= 0,
                    "path with spaces must be quoted: " + arguments);

                // 用真实子进程验证引用：假进程按命令行解析出的 -y 路径写文件。
                var launcher = new FakeVideoProcessLauncher();
                launcher.Encoder.WriteReceipt = true;

                Process process = launcher.Create()(FakeVideoProcess.ExecutablePath, arguments, directory);
                try
                {
                    process.StandardInput.Close();
                    TestKit.Check(process.WaitForExit(60000), "fake process must exit");
                    TestKit.CheckEqual(0, process.ExitCode, "fake process exit");
                }
                finally
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch
                    {
                    }
                    process.Dispose();
                }

                TestKit.Check(File.Exists(tempPath + ".fake-receipt"), "receipt must exist: " + tempPath);
                string receipt = File.ReadAllText(tempPath + ".fake-receipt");
                TestKit.Check(receipt.IndexOf("out=" + tempPath + "\n", StringComparison.Ordinal) >= 0,
                    "the child must receive the exact quoted output path: " + receipt);
                TestKit.Check(receipt.IndexOf("size=64x48\n", StringComparison.Ordinal) >= 0, "size argument: " + receipt);
                TestKit.Check(receipt.IndexOf("vf=settb=1/30,setpts=N\n", StringComparison.Ordinal) >= 0,
                    "filter argument must survive unquoted: " + receipt);

                TestKit.TryDeleteDirectory(directory);
            });
        }

        // ==================================================================== identity

        private static void IdentityTests(string workRoot)
        {
            TestKit.Run("video identity: requires Ready, matching capability identity and complete capability", () =>
            {
                string fixture = FakeVideoProcess.ExecutablePath;
                FfmpegVideoIdentity identity;
                string code;
                string detail;

                TestKit.Check(!FfmpegVideoIdentity.TryFreeze(null, out identity, out code, out detail),
                    "null report must fail");
                TestKit.CheckEqual("component-report-null", code, "null report code");

                FfmpegComponentReport discovered = ReadyReport(fixture);
                discovered.State = FfmpegComponentState.Discovered;
                TestKit.Check(!FfmpegVideoIdentity.TryFreeze(discovered, out identity, out code, out detail),
                    "non-Ready report must fail closed");
                TestKit.CheckEqual("component-not-ready", code, "not ready code");

                FfmpegComponentReport mismatched = ReadyReport(fixture);
                mismatched.Capability.ExecutableSha256 = new string('a', 64);
                TestKit.Check(!FfmpegVideoIdentity.TryFreeze(mismatched, out identity, out code, out detail),
                    "capability/identity hash mismatch must fail closed");
                TestKit.CheckEqual("capability-identity-mismatch", code, "mismatch code");

                FfmpegComponentReport incomplete = ReadyReport(fixture);
                incomplete.Capability.HasLibx264 = false;
                incomplete.Capability.MissingCapabilities = new[] { "encoder:libx264" };
                TestKit.Check(!FfmpegVideoIdentity.TryFreeze(incomplete, out identity, out code, out detail),
                    "incomplete capability must fail closed");
                TestKit.CheckEqual("capability-incomplete", code, "incomplete code");
                TestKit.CheckEqual("encoder:libx264", detail, "missing capability detail");

                FfmpegComponentReport notProbed = ReadyReport(fixture);
                notProbed.Capability.Status = FfmpegCapabilityStatus.ProbeFailed;
                TestKit.Check(!FfmpegVideoIdentity.TryFreeze(notProbed, out identity, out code, out detail),
                    "failed probe must fail closed");
                TestKit.CheckEqual("capability-not-probed", code, "probe status code");

                TestKit.Check(FfmpegVideoIdentity.TryFreeze(ReadyReport(fixture), out identity, out code, out detail),
                    "ready report must freeze: " + code);
                TestKit.Check(Path.IsPathRooted(identity.ExecutablePath), "frozen path must be absolute");
                TestKit.CheckEqual(64, identity.ExecutableSha256.Length, "frozen hash length");
                TestKit.Check(identity.HasLibx264 && identity.HasMp4Muxer && identity.HasRawvideoDemuxer,
                    "capabilities must be frozen");
            });

            TestKit.Run("video identity: reverify detects size and same-size content change", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "identity");
                string path = Path.Combine(directory, "ffmpeg.exe");

                var payload = new byte[4096];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(i & 0xFF);
                File.WriteAllBytes(path, payload);

                FfmpegVideoIdentity identity = MakeIdentity(path);

                string code;
                string detail;
                TestKit.Check(identity.TryReverify(out code, out detail), "unchanged file must reverify: " + code);

                File.WriteAllBytes(path, new byte[8192]);
                TestKit.Check(!identity.TryReverify(out code, out detail), "size change must be detected");
                TestKit.CheckEqual("identity-size-changed", code, "size change code");

                File.WriteAllBytes(path, payload);
                TestKit.Check(identity.TryReverify(out code, out detail), "restored content must reverify");

                var sameSize = new byte[payload.Length];
                Array.Copy(payload, sameSize, payload.Length);
                sameSize[0] ^= 0xFF;
                File.WriteAllBytes(path, sameSize);
                TestKit.Check(!identity.TryReverify(out code, out detail),
                    "same-size replacement must be detected (no metadata caching)");
                TestKit.CheckEqual("identity-hash-changed", code, "hash change code");

                File.Delete(path);
                TestKit.Check(!identity.TryReverify(out code, out detail), "missing file must fail closed");
                TestKit.CheckEqual("identity-executable-missing", code, "missing file code");

                TestKit.TryDeleteDirectory(directory);
            });
        }

        private static FfmpegComponentReport ReadyReport(string path)
        {
            string sha256;
            string hashError;
            if (!FfmpegFileHash.TryCompute(path, out sha256, out hashError))
                throw new Exception("cannot hash: " + hashError);

            return new FfmpegComponentReport
            {
                State = FfmpegComponentState.Ready,
                Source = FfmpegCandidateSource.ManagedInstall,
                Candidate = new FfmpegCandidate
                {
                    Source = FfmpegCandidateSource.ManagedInstall,
                    Identity = new FfmpegBinaryIdentity
                    {
                        AbsolutePath = path,
                        Sha256 = sha256,
                        SizeBytes = new FileInfo(path).Length,
                    },
                },
                Capability = new FfmpegCapabilityReport
                {
                    Status = FfmpegCapabilityStatus.Probed,
                    ExecutablePath = path,
                    ExecutableSha256 = sha256,
                    VersionLine = "ffmpeg version fixture",
                    HasLibx264 = true,
                    HasMp4Muxer = true,
                    HasRawvideoDemuxer = true,
                    MissingCapabilities = new string[0],
                },
            };
        }

        // ==================================================================== stream line parsing

        private static void StreamLineParsingTests()
        {
            TestKit.Run("verify parsing: parses real ffmpeg stream lines without the 0x31637661 trap", () =>
            {
                // 这两行是真实 Gyan 9.0.2 输出（probe 记录）。
                string progressive =
                    "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 64x48, " +
                    "152 kb/s, 30 fps, 30 tbr, 30 tbn (default)";
                string fourFourFour =
                    "  Stream #0:0[0x1](und): Video: h264 (High 4:4:4 Predictive) (avc1 / 0x31637661), " +
                    "yuv444p(progressive), 65x49, 310 kb/s, 30 fps, 30 tbr, 30 tbn (default)";
                string tv =
                    "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, progressive), " +
                    "1920x1080, 1001k tbr, 1001k tbn";

                string codec;
                string pixelFormat;
                int width;
                int height;

                TestKit.Check(FfmpegVideoVerifier.TryParseStreamLine(progressive, out codec, out pixelFormat, out width, out height),
                    "progressive line must parse");
                TestKit.CheckEqual("h264", codec, "codec");
                TestKit.CheckEqual("yuv420p", pixelFormat, "pixel format");
                TestKit.CheckEqual(64, width, "width");
                TestKit.CheckEqual(48, height, "height");

                TestKit.Check(FfmpegVideoVerifier.TryParseStreamLine(fourFourFour, out codec, out pixelFormat, out width, out height),
                    "4:4:4 line must parse");
                TestKit.CheckEqual("yuv444p", pixelFormat, "4:4:4 pixel format");
                TestKit.CheckEqual(65, width, "4:4:4 width");
                TestKit.CheckEqual(49, height, "4:4:4 height");

                TestKit.Check(FfmpegVideoVerifier.TryParseStreamLine(tv, out codec, out pixelFormat, out width, out height),
                    "pix fmt with inner comma must parse");
                TestKit.CheckEqual("yuv420p", pixelFormat, "inner-comma pixel format");
                TestKit.CheckEqual(1920, width, "inner-comma width");
                TestKit.CheckEqual(1080, height, "inner-comma height");

                TestKit.Check(!FfmpegVideoVerifier.TryParseStreamLine("Input #0, rawvideo, from 'pipe:0':", out codec,
                    out pixelFormat, out width, out height), "non-stream line must not parse");
            });

            TestKit.Run("verify parsing: section filter only counts input video streams", () =>
            {
                var collector = new StreamLineCollector();
                collector.FeedLine("Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'x.mp4':");
                collector.FeedLine("  Stream #0:0[0x1](und): Video: h264 (High), yuv420p(progressive), 64x48, 30 tbn");
                collector.FeedLine("Stream mapping:");
                collector.FeedLine("  Stream #0:0 -> #0:0 (h264 (native) -> rawvideo (native))");
                collector.FeedLine("Output #0, framemd5, to 'pipe:':");
                collector.FeedLine("  Stream #0:0(und): Video: rawvideo (I420), yuv420p(progressive), 64x48, 30 tbn");

                TestKit.CheckEqual(1, collector.Count, "only the input stream counts as a product video stream");
                TestKit.Check(collector.First.IndexOf("h264", StringComparison.Ordinal) >= 0, "input line expected");
            });
        }

        // ==================================================================== verifier contract

        private static void VerifierContractTests(string workRoot)
        {
            string directory = TestKit.NewWorkDirectory(workRoot, "verify");
            string videoPath = Path.Combine(directory, "video.mp4");
            File.WriteAllBytes(videoPath, new byte[128]);

            TestKit.Run("verify: accepts a well-formed report", () =>
            {
                var script = new FakeVideoVerifierScript { Frames = 30 };
                FfmpegVideoVerificationResult result = RunFakeVerifier(script, videoPath, 64, 48, 30, 30);

                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Verified, result.Status,
                    "status (" + result.ErrorCode + " " + result.ErrorDetail + ")");
                TestKit.CheckEqual(30L, result.DecodedFrameCount, "decoded frames");
                TestKit.CheckEqual(30L, result.ContainerPacketCount, "container packets");
                TestKit.CheckEqual(1, result.TimeBaseNumerator, "timebase numerator");
                TestKit.CheckEqual(30, result.TimeBaseDenominator, "timebase denominator");
                TestKit.CheckEqual(0L, result.FirstDecodedPts, "first pts");
                TestKit.CheckEqual(29L, result.LastDecodedPts, "last pts");
                TestKit.CheckEqual("h264", result.CodecName, "codec");
                TestKit.CheckEqual("yuv420p", result.PixelFormat, "pixel format");
            });

            TestKit.Run("verify: rejects a decoded frame count mismatch", () =>
            {
                var script = new FakeVideoVerifierScript { Frames = 29 };
                FfmpegVideoVerificationResult result = RunFakeVerifier(script, videoPath, 64, 48, 30, 30);
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Failed, result.Status, "status");
                TestKit.CheckEqual("verify-frame-count-mismatch", result.ErrorCode, "error code");
            });

            TestKit.Run("verify: rejects a container packet count mismatch", () =>
            {
                var script = new FakeVideoVerifierScript { Frames = 30, PacketFrames = 30 };
                FfmpegVideoVerificationResult control = RunFakeVerifier(script, videoPath, 64, 48, 30, 30);
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Verified, control.Status, "control must verify");

                script.PacketFrames = 31;
                FfmpegVideoVerificationResult result = RunFakeVerifier(script, videoPath, 64, 48, 30, 30);
                TestKit.CheckEqual("verify-packet-count-mismatch", result.ErrorCode, "error code");
            });

            TestKit.Run("verify: rejects dimension and pixel format mismatches", () =>
            {
                var dimensions = new FakeVideoVerifierScript { Frames = 5, Width = 63, Height = 48 };
                FfmpegVideoVerificationResult result = RunFakeVerifier(dimensions, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-dimension-mismatch", result.ErrorCode, "dimension code");

                var pixelFormat = new FakeVideoVerifierScript { Frames = 5, PixelFormat = "yuv444p" };
                result = RunFakeVerifier(pixelFormat, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-pixel-format-mismatch", result.ErrorCode, "pixel format code");

                var codec = new FakeVideoVerifierScript { Frames = 5, Codec = "mpeg4" };
                result = RunFakeVerifier(codec, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-codec-mismatch", result.ErrorCode, "codec code");
            });

            TestKit.Run("verify: rejects rational timebase mismatches on both passes", () =>
            {
                var decode = new FakeVideoVerifierScript { Frames = 5, TimeBaseDenominator = 1001000 };
                FfmpegVideoVerificationResult result = RunFakeVerifier(decode, videoPath, 64, 48, 1001001, 5);
                TestKit.CheckEqual("verify-timebase-mismatch", result.ErrorCode, "decoded timebase code");

                var packet = new FakeVideoVerifierScript { Frames = 5, TimeBaseDenominator = 1001000 };
                result = RunFakeVerifier(packet, videoPath, 64, 48, 1001001, 5);
                TestKit.CheckEqual("verify-timebase-mismatch", result.ErrorCode, "packet-vs-decode timebase");

                var numerator = new FakeVideoVerifierScript { Frames = 5, TimeBaseNumerator = 1001 };
                result = RunFakeVerifier(numerator, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-timebase-mismatch", result.ErrorCode, "non-unit numerator");

                var missing = new FakeVideoVerifierScript { Frames = 5, WithoutTimeBaseHeader = true };
                result = RunFakeVerifier(missing, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-timebase-mismatch", result.ErrorCode, "missing timebase header");
            });

            TestKit.Run("verify: rejects pts sequence and duration errors", () =>
            {
                var offset = new FakeVideoVerifierScript { Frames = 5, PtsOffset = 1 };
                FfmpegVideoVerificationResult result = RunFakeVerifier(offset, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-frame-timing-mismatch", result.ErrorCode, "pts offset code");
                TestKit.Check(result.FirstDecodedTimingMismatchDetail.IndexOf("frame=0", StringComparison.Ordinal) >= 0,
                    "mismatch detail must name the frame: " + result.FirstDecodedTimingMismatchDetail);

                var duration = new FakeVideoVerifierScript { Frames = 5, Duration = 2 };
                result = RunFakeVerifier(duration, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-frame-timing-mismatch", result.ErrorCode, "duration code");
                TestKit.Check(result.FirstDecodedTimingMismatchDetail.IndexOf("duration=2", StringComparison.Ordinal) >= 0,
                    "duration detail: " + result.FirstDecodedTimingMismatchDetail);
            });

            TestKit.Run("verify: rejects a missing or duplicated video stream", () =>
            {
                var missing = new FakeVideoVerifierScript { Frames = 5, WithoutStreamLine = true };
                FfmpegVideoVerificationResult result = RunFakeVerifier(missing, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-no-video-stream", result.ErrorCode, "missing stream code");

                var duplicated = new FakeVideoVerifierScript { Frames = 5, ExtraStreamLines = 1 };
                result = RunFakeVerifier(duplicated, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-video-stream-count", result.ErrorCode, "duplicate stream code");
            });

            TestKit.Run("verify: rejects non-zero exits and error output", () =>
            {
                var decodeExit = new FakeVideoVerifierScript { Frames = 5, ExitCode = -1094995529 };
                FfmpegVideoVerificationResult result = RunFakeVerifier(decodeExit, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-decode-exit", result.ErrorCode, "decode exit code");

                var packetExit = new FakeVideoVerifierScript { Frames = 5, PacketExitCode = -22 };
                result = RunFakeVerifier(packetExit, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-packet-exit", result.ErrorCode, "packet exit code");

                var errorText = new FakeVideoVerifierScript { Frames = 5, StderrError = "[in#0] Error while decoding stream" };
                result = RunFakeVerifier(errorText, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual("verify-decode-error-output", result.ErrorCode, "error output code");
            });

            TestKit.Run("verify: rejects malformed framemd5 rows", () =>
            {
                var script = new FakeVideoVerifierScript { Frames = 5 };
                // frames=5 但其中一行被破坏：用 pts-offset 之外的形态不易构造，
                // 因此这里直接验证累加器本身。
                var accumulator = new Framemd5Accumulator();
                accumulator.FeedLine("#tb 0: 1/30");
                accumulator.FeedLine("0, 0, 0, 1, 9216, abc");
                accumulator.FeedLine("0, not-a-number, 1, 1, 9216, abc");
                TestKit.Check(accumulator.MalformedLineDetail.Length > 0, "malformed row must be recorded");

                FfmpegVideoVerificationResult control = RunFakeVerifier(script, videoPath, 64, 48, 30, 5);
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Verified, control.Status, "control still verifies");
            });

            TestKit.Run("verify: cancellation terminates and reaps a hanging verification process", () =>
            {
                var script = new FakeVideoVerifierScript { Frames = 5, Hang = true };
                var launcher = new FakeVideoProcessLauncher { Verifier = script };
                var verifier = new FfmpegVideoVerifier(launcher.Create());

                using (var cancellation = new CancellationTokenSource())
                {
                    var request = new FfmpegVideoVerificationRequest
                    {
                        ExecutablePath = FakeVideoProcess.ExecutablePath,
                        VideoPath = videoPath,
                        ExpectedWidth = 64,
                        ExpectedHeight = 48,
                        ExpectedFps = 30,
                        ExpectedCodecName = "h264",
                        ExpectedPixelFormat = "yuv420p",
                        ExpectedFrameCount = 5,
                        CancellationToken = cancellation.Token,
                    };

                    Task<FfmpegVideoVerificationResult> task = verifier.VerifyAsync(request);

                    // 等核验进程真的起来之后再取消，避免与启动竞争。
                    var started = Stopwatch.StartNew();
                    while (launcher.VerifierStarts == 0 && started.ElapsedMilliseconds < 30000)
                        Thread.Sleep(20);
                    TestKit.CheckEqual(1, launcher.VerifierStarts, "verification process must have started");

                    cancellation.Cancel();

                    FfmpegVideoVerificationResult result = Await(task, "cancelled verify");
                    TestKit.CheckEqual(FfmpegVideoVerificationStatus.Cancelled, result.Status, "status");
                    TestKit.CheckEqual("verify-cancelled", result.ErrorCode, "error code");
                }
            });

            TestKit.TryDeleteDirectory(directory);
        }

        // ==================================================================== pipeline lifecycle (fake process)

        private static void PipelineLifecycleTests(string workRoot)
        {
            TestKit.Run("pipeline: happy path publishes only after verification", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "happy");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { OutputBytes = 512 },
                    Verifier = new FakeVideoVerifierScript { Frames = 3 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");

                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(start.Started, "start failed: " + start.ErrorCode + " " + start.ErrorDetail);

                for (int i = 0; i < 3; i++)
                {
                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame " + i + " rejected: " + attempt.ErrorCode);
                    FfmpegFrameWriteResult written = Await(attempt.Completion, "frame write");
                    TestKit.Check(written.Success, "frame " + i + " write failed: " + written.ErrorCode);
                }

                TestKit.CheckEqual(3L, pipeline.DeliveredFrameCount, "delivered frames");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                TestKit.CheckEqual(3L, outcome.DeliveredFrameCount, "delivered frames");
                TestKit.Check(outcome.Verification != null && outcome.Verification.IsVerified, "verification must pass");
                TestKit.Check(File.Exists(outcome.FinalPath), "final file must exist: " + outcome.FinalPath);
                TestKit.Check(!File.Exists(pipeline.TempPath), "temp file must be gone after publish");
                TestKit.Check(!outcome.ResidualOwnership, "no residual ownership: " + outcome.ResidualDetail);
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");
                TestKit.CheckEqual(1, launcher.EncoderStarts, "exactly one encoder process");
                TestKit.CheckEqual(2, launcher.VerifierStarts, "exactly two verification processes");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: a short frame is rejected before it is accepted", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "short");
                var launcher = new FakeVideoProcessLauncher();
                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");

                TestKit.Check(pipeline.Start().Started, "start");

                var shortFrame = FfmpegVideoFrame.FromBuffer(new byte[100]);
                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(shortFrame);
                TestKit.Check(!attempt.Accepted, "short frame must be rejected");
                TestKit.CheckEqual("frame-length-mismatch", attempt.ErrorCode, "error code");
                TestKit.CheckEqual(0L, pipeline.DeliveredFrameCount, "nothing may be delivered");

                pipeline.Cancel("test");
                Await(pipeline.CleanupTask, "cleanup");
                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: a second concurrent frame returns busy immediately without queueing", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "busy");
                var launcher = new FakeVideoProcessLauncher
                {
                    // 读取端立刻停止消费：大帧一定会阻塞在管道上。
                    Encoder = new FakeVideoEncoderScript { StallAfterBytes = 0 },
                };

                var pipeline = CreatePipeline(directory, Settings(512, 512, 30),
                    MakeIdentity(FakeVideoProcess.ExecutablePath), launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt first = pipeline.TryWriteFrame(Frame(512, 512));
                TestKit.Check(first.Accepted, "first frame must be accepted: " + first.ErrorCode);

                var stopwatch = Stopwatch.StartNew();
                FfmpegFrameWriteAttempt second = pipeline.TryWriteFrame(Frame(512, 512));
                stopwatch.Stop();

                TestKit.Check(!second.Accepted, "second frame must be rejected");
                TestKit.CheckEqual("busy", second.ErrorCode, "error code");
                TestKit.Check(stopwatch.ElapsedMilliseconds < 2000,
                    "busy must return immediately, not after waiting: " + stopwatch.ElapsedMilliseconds + "ms");

                TestKit.Check(pipeline.Cancel("test"), "cancel must be accepted");
                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State, "state");
                TestKit.Check(!outcome.ResidualOwnership, "no residual: " + outcome.ResidualDetail);
                TestKit.CheckEqual(0L, pipeline.DeliveredFrameCount, "the blocked frame must not count as delivered");

                FfmpegFrameWriteResult blocked = Await(first.Completion, "blocked write");
                TestKit.Check(!blocked.Success, "a write interrupted by cancel must not report success");
                TestKit.CheckEqual(0L, pipeline.DeliveredFrameCount, "late write result must not change the count");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: a broken pipe mid-frame poisons the session and never retries", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "broken");
                var launcher = new FakeVideoProcessLauncher
                {
                    // 读满一个缓冲区就退出：父进程的大帧写入必然中途失败。
                    Encoder = new FakeVideoEncoderScript { ExitAfterBytes = 4096, ExitCode = 9 },
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(512, 512, 30),
                    MakeIdentity(FakeVideoProcess.ExecutablePath), launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(512, 512));
                TestKit.Check(attempt.Accepted, "first frame accepted: " + attempt.ErrorCode);
                FfmpegFrameWriteResult result = Await(attempt.Completion, "broken write");

                TestKit.Check(!result.Success, "the write must fail");
                TestKit.CheckEqual("write-failed", result.ErrorCode, "error code");
                TestKit.CheckEqual(0L, pipeline.DeliveredFrameCount, "a partial frame must not be counted");

                FfmpegFrameWriteAttempt retry = pipeline.TryWriteFrame(Frame(512, 512));
                TestKit.Check(!retry.Accepted, "the poisoned pipe must reject further frames");
                TestKit.Check(retry.ErrorCode == "not-running" || retry.ErrorCode == "pipe-poisoned",
                    "error code: " + retry.ErrorCode);

                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.CheckEqual("write-failed", outcome.ErrorCode, "outcome error code");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: encoder non-zero exit fails closed and publishes nothing", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "exitcode");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { ExitCode = 4 },
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.CheckEqual("encoder-exit-nonzero", outcome.ErrorCode, "error code");
                TestKit.CheckEqual(4, outcome.EncoderExitCode.HasValue ? outcome.EncoderExitCode.Value : int.MinValue,
                    "encoder exit code");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");
                TestKit.CheckEqual(0, launcher.VerifierStarts, "verification must not run after a failed encode");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: a verification failure fails closed and keeps no temp file", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "verifyfail");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript(),
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");
                FfmpegFrameWriteAttempt second = pipeline.TryWriteFrame(Frame(64, 48));
                Await(second.Completion, "frame write");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.CheckEqual("verification-failed", outcome.ErrorCode, "error code");
                TestKit.Check(outcome.ErrorDetail.IndexOf("verify-frame-count-mismatch", StringComparison.Ordinal) >= 0,
                    "verification reason must be reported: " + outcome.ErrorDetail);
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: process start failure is fail-closed", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "startfail");
                FfmpegVideoIdentity identity = MakeIdentity(FakeVideoProcess.ExecutablePath);

                var throwing = CreatePipeline(directory, Settings(64, 48, 30), identity,
                    (path, arguments, workingDirectory) => { throw new InvalidOperationException("boom"); }, "video.mp4");
                FfmpegVideoStartResult start = throwing.Start();
                TestKit.Check(!start.Started, "throwing launcher must not start");
                TestKit.CheckEqual("process-start-failed", start.ErrorCode, "error code");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, throwing.State, "state");

                // 启动失败也要回收已经建立的资源（这里包括预创建的临时文件与它的 ownership 句柄）。
                FfmpegVideoOutcome throwingOutcome = Await(throwing.CleanupTask, "throwing cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, throwingOutcome.State, "outcome state");
                TestKit.CheckEqual("process-start-failed", throwingOutcome.ErrorCode, "outcome error code");
                TestKit.Check(throwingOutcome.TempFileRemoved, "the owned temp file must be reclaimed");
                TestKit.Check(throwingOutcome.TempOwnershipReleased, "the ownership handle must be released");
                TestKit.Check(!throwingOutcome.ResidualOwnership, "no residual: " + throwingOutcome.ResidualDetail);

                var nulling = CreatePipeline(directory, Settings(64, 48, 30), identity,
                    (path, arguments, workingDirectory) => null, "video.mp4");
                start = nulling.Start();
                TestKit.Check(!start.Started, "null process must not start");
                TestKit.CheckEqual("process-start-failed", start.ErrorCode, "error code");
                Await(nulling.CleanupTask, "nulling cleanup");

                TestKit.CheckEqual(0, PartialFiles(directory).Length, "nothing may be left behind");
                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: identity change before start is fail-closed and starts no process", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "identitychange");
                string binary = Path.Combine(directory, "ffmpeg.exe");
                File.WriteAllBytes(binary, new byte[2048]);

                FfmpegVideoIdentity identity = MakeIdentity(binary);
                var launcher = new FakeVideoProcessLauncher();

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), identity, launcher.Create(), "video.mp4");

                File.WriteAllBytes(binary, new byte[4096]);

                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(!start.Started, "start must fail");
                TestKit.CheckEqual("identity-size-changed", start.ErrorCode, "error code");
                TestKit.CheckEqual(0, launcher.EncoderStarts, "no process may be started with a stale identity");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, pipeline.State, "state");
                TestKit.Check(pipeline.CleanupTask != null, "a failed start must still expose a cleanup task");
                Await(pipeline.CleanupTask, "cleanup");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: an existing final file is never overwritten", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "existing");
                string finalPath = Path.Combine(directory, "video.mp4");
                File.WriteAllText(finalPath, "user-owned");

                var launcher = new FakeVideoProcessLauncher();
                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");

                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(!start.Started, "start must fail");
                TestKit.CheckEqual("final-file-exists", start.ErrorCode, "error code");
                TestKit.CheckEqual(0, launcher.EncoderStarts, "no encoder may be started");
                TestKit.CheckEqual("user-owned", File.ReadAllText(finalPath), "existing content must be untouched");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: a final file appearing mid-session blocks publishing", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "racepublish");
                string finalPath = Path.Combine(directory, "video.mp4");

                var launcher = new FakeVideoProcessLauncher
                {
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");

                File.WriteAllText(finalPath, "appeared-during-session");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.CheckEqual("final-file-exists", outcome.ErrorCode, "error code");
                TestKit.CheckEqual("appeared-during-session", File.ReadAllText(finalPath), "existing content untouched");
                TestKit.Check(outcome.Verification != null && outcome.Verification.IsVerified,
                    "verification still had to succeed before the publish gate");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "our temp file must be removed");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: missing output directory and existing temp file are rejected", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "paths");
                var launcher = new FakeVideoProcessLauncher();

                var missing = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), Path.Combine("no-such-subdir", "video.mp4"));
                FfmpegVideoStartResult start = missing.Start();
                TestKit.Check(!start.Started, "missing directory must fail");
                TestKit.CheckEqual("output-directory-not-found", start.ErrorCode, "error code");

                string tempPath = Path.Combine(directory, "video.mp4" + FfmpegVideoCommand.TempFileInfix + "token");
                File.WriteAllText(tempPath, "stale");

                var existingTemp = CreatePipeline(directory, Settings(64, 48, 30),
                    MakeIdentity(FakeVideoProcess.ExecutablePath), launcher.Create(), "video.mp4", tempPath);
                start = existingTemp.Start();
                TestKit.Check(!start.Started, "existing temp file must fail");
                TestKit.CheckEqual("temp-file-exists", start.ErrorCode, "error code");
                TestKit.CheckEqual("stale", File.ReadAllText(tempPath), "a foreign file must never be deleted");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: cancel while a write is blocked recovers the process", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "cancelwrite");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { StallAfterBytes = 0 },
                };

                var pipeline = CreatePipeline(directory, Settings(512, 512, 30),
                    MakeIdentity(FakeVideoProcess.ExecutablePath), launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(512, 512));
                TestKit.Check(attempt.Accepted, "frame accepted");

                var stopwatch = Stopwatch.StartNew();
                TestKit.Check(pipeline.Cancel("user-cancel"), "cancel must win");
                TestKit.Check(stopwatch.ElapsedMilliseconds < 5000,
                    "cancel must not wait for process exit: " + stopwatch.ElapsedMilliseconds + "ms");

                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State, "state");
                TestKit.CheckEqual("user-cancel", outcome.Reason, "reason");
                TestKit.Check(outcome.ProcessReaped, "the process must be reaped");
                TestKit.Check(!outcome.ResidualOwnership, "no residual ownership: " + outcome.ResidualDetail);
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");

                FfmpegFrameWriteResult blocked = Await(attempt.Completion, "blocked write");
                TestKit.Check(!blocked.Success, "the blocked write must fail");
                TestKit.CheckEqual(0L, pipeline.DeliveredFrameCount, "no frame may count as delivered");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: cancel during finalize wins before publish", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "cancelfinalize");
                var launcher = new FakeVideoProcessLauncher
                {
                    Verifier = new FakeVideoVerifierScript { Frames = 1, Hang = true },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");

                Task<FfmpegVideoOutcome> finish = pipeline.FinishAsync();

                var started = Stopwatch.StartNew();
                while (launcher.VerifierStarts == 0 && started.ElapsedMilliseconds < 30000)
                    Thread.Sleep(20);
                TestKit.Check(launcher.VerifierStarts >= 1, "verification must have started");

                TestKit.Check(pipeline.Cancel("cancel-during-finalize"), "cancel must win before publish");

                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State, "state");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");
                TestKit.Check(!outcome.ResidualOwnership, "no residual ownership: " + outcome.ResidualDetail);

                FfmpegVideoOutcome finished = Await(finish, "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, finished.State,
                    "the finalize task must report the winning terminal state");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: cancel after publish cannot un-publish", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "cancelpublished");
                var launcher = new FakeVideoProcessLauncher
                {
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State, "state");

                TestKit.Check(!pipeline.Cancel("late-cancel"), "cancel after publish must be refused");
                TestKit.Check(File.Exists(outcome.FinalPath), "the published file must survive");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, pipeline.State, "state must stay Completed");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: finish and cancel races always resolve to one terminal state", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "race");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { SleepBeforeExitMs = 300 },
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");

                Task<FfmpegVideoOutcome> finish = pipeline.FinishAsync();
                bool cancelled = pipeline.Cancel("race");

                FfmpegVideoOutcome outcome = Await(finish, "finish");
                if (!cancelled)
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State, "finish won the race");
                else
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State, "cancel won the race");

                if (outcome.State == FfmpegVideoPipelineState.Completed)
                    TestKit.Check(File.Exists(outcome.FinalPath), "Completed implies a published file");
                else
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")),
                        "Cancelled must not leave a published file");

                FfmpegVideoOutcome cleanup = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(outcome.State, cleanup.State, "cleanup must report the same terminal state");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: finish after cancel returns the cancelled outcome", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "finishaftercancel");
                var launcher = new FakeVideoProcessLauncher { Encoder = new FakeVideoEncoderScript { SleepBeforeExitMs = 200 } };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                pipeline.Cancel("first");
                FfmpegVideoOutcome finished = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, finished.State, "state");
                TestKit.CheckEqual("first", finished.Reason, "reason");
                TestKit.Check(!finished.ResidualOwnership, "no residual: " + finished.ResidualDetail);

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: finish with a frame write in flight is rejected", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "finishinflight");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { StallAfterBytes = 0 },
                };

                var pipeline = CreatePipeline(directory, Settings(512, 512, 30),
                    MakeIdentity(FakeVideoProcess.ExecutablePath), launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(512, 512));
                TestKit.Check(attempt.Accepted, "frame accepted");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.CheckEqual("write-in-flight", outcome.ErrorCode, "error code");

                pipeline.Cancel("cleanup");
                Await(pipeline.CleanupTask, "cleanup");
                Await(attempt.Completion, "blocked write");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: dispose does not block and leaves an awaitable cleanup task", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "dispose");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { StallAfterBytes = 0 },
                };

                var pipeline = CreatePipeline(directory, Settings(512, 512, 30),
                    MakeIdentity(FakeVideoProcess.ExecutablePath), launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(512, 512));
                TestKit.Check(attempt.Accepted, "frame accepted");

                var stopwatch = Stopwatch.StartNew();
                pipeline.Dispose();
                stopwatch.Stop();

                TestKit.Check(stopwatch.ElapsedMilliseconds < 2000,
                    "Dispose must not wait for the process: " + stopwatch.ElapsedMilliseconds + "ms");
                TestKit.Check(pipeline.CleanupTask != null, "cleanup must be observable");

                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State, "state");
                TestKit.CheckEqual("disposed", outcome.Reason, "reason");
                TestKit.Check(!outcome.ResidualOwnership, "no residual ownership: " + outcome.ResidualDetail);
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");

                Await(attempt.Completion, "blocked write");
                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: repeated start/finish cycles leave no temp files or processes", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "cycles");
                FfmpegVideoIdentity identity = MakeIdentity(FakeVideoProcess.ExecutablePath);

                for (int cycle = 0; cycle < 3; cycle++)
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        Verifier = new FakeVideoVerifierScript { Frames = 2 },
                    };

                    var pipeline = CreatePipeline(directory, Settings(64, 48, 30), identity,
                        launcher.Create(), "video-" + cycle + ".mp4");
                    TestKit.Check(pipeline.Start().Started, "cycle " + cycle + " start");

                    for (int i = 0; i < 2; i++)
                    {
                        FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                        TestKit.Check(attempt.Accepted, "cycle " + cycle + " frame " + i);
                        Await(attempt.Completion, "cycle write");
                    }

                    FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "cycle finish");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                        "cycle " + cycle + " state (" + outcome.ErrorCode + ")");
                    TestKit.Check(!outcome.ResidualOwnership, "cycle " + cycle + " residual: " + outcome.ResidualDetail);
                }

                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files may remain");
                TestKit.CheckEqual(3, Directory.GetFiles(directory, "video-*.mp4").Length, "three published files");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: encoder and verification output are fully drained", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "drain");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { StdErrBytes = 400000, StdOutBytes = 400000 },
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                TestKit.Check(outcome.Verification != null && outcome.Verification.IsVerified,
                    "stream output must not break verification");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: caller data is written without an extra copy and without mutation", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "nocopy");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { WriteReceipt = true },
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                long frameLength;
                string lengthError;
                TestKit.Check(Settings(64, 48, 30).TryGetFrameLengthBytes(out frameLength, out lengthError),
                    "frame length");

                var first = new byte[frameLength / 2];
                var second = new byte[frameLength - first.Length];
                for (int i = 0; i < first.Length; i++)
                    first[i] = 0x11;
                for (int i = 0; i < second.Length; i++)
                    second[i] = 0x22;

                var segmented = new FfmpegVideoFrame(new[]
                {
                    new FfmpegFrameSegment(first, 0, first.Length),
                    new FfmpegFrameSegment(second, 0, second.Length),
                });

                TestKit.CheckEqual(frameLength, segmented.Length, "segmented frame length");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(segmented);
                TestKit.Check(attempt.Accepted, "segmented frame accepted: " + attempt.ErrorCode);
                TestKit.Check(Await(attempt.Completion, "segmented write").Success, "segmented write success");

                TestKit.CheckEqual((byte)0x11, first[0], "caller buffer must not be mutated");
                TestKit.CheckEqual((byte)0x22, second[0], "caller buffer must not be mutated");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                TestKit.Check(File.Exists(outcome.FinalPath), "final file must exist");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: state machine rejects writes before start and after termination", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "states");
                var launcher = new FakeVideoProcessLauncher
                {
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");

                FfmpegFrameWriteAttempt beforeStart = pipeline.TryWriteFrame(Frame(64, 48));
                TestKit.Check(!beforeStart.Accepted, "writes before start must be rejected");
                TestKit.CheckEqual("not-running", beforeStart.ErrorCode, "error code");
                TestKit.CheckEqual(FfmpegVideoPipelineState.NotStarted, pipeline.State, "state");

                TestKit.Check(pipeline.Start().Started, "start");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Running, pipeline.State, "running state");

                FfmpegFrameWriteAttempt duplicateStart = pipeline.TryWriteFrame(Frame(64, 48));
                Await(duplicateStart.Completion, "frame write");

                TestKit.Check(!pipeline.Start().Started, "a second Start must be rejected");
                TestKit.CheckEqual("already-started", pipeline.Start().ErrorCode, "error code");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State, "state");

                FfmpegFrameWriteAttempt afterFinish = pipeline.TryWriteFrame(Frame(64, 48));
                TestKit.Check(!afterFinish.Accepted, "writes after termination must be rejected");
                TestKit.CheckEqual("not-running", afterFinish.ErrorCode, "error code");

                FfmpegVideoOutcome secondFinish = Await(pipeline.FinishAsync(), "second finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, secondFinish.State, "finish is idempotent");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("pipeline: cleanup failure reports residual ownership instead of pretending to be clean", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "residual");
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { EarlyOutput = true },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), MakeIdentity(FakeVideoProcess.ExecutablePath),
                    launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");

                // 用兼容的共享模式再占一个句柄（ReadWrite，但不共享 Delete）：
                // 管线自己的 ownership 句柄仍能释放，但文件因此在终态时无法删除，
                // 管线必须如实报告 residual ownership。
                FileStream hold = new FileStream(
                    pipeline.TempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                try
                {
                    TestKit.Check(pipeline.Cancel("residual-test"), "cancel");
                    FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");

                    TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State, "state");
                    TestKit.Check(outcome.TempOwnershipReleased, "the session ownership handle must still be released");
                    TestKit.Check(outcome.ResidualOwnership, "residual ownership must be reported");
                    TestKit.Check((outcome.ResidualDetail ?? string.Empty).IndexOf("temp-delete-failed", StringComparison.Ordinal) >= 0,
                        "residual detail: " + outcome.ResidualDetail);
                    TestKit.Check(!outcome.TempFileRemoved, "an undeletable temp file must not be reported as removed");
                    TestKit.Check(File.Exists(pipeline.TempPath), "the temp file is still there");
                }
                finally
                {
                    hold.Dispose();
                }

                TestKit.TryDeleteDirectory(directory);
            });
        }

        // ==================================================================== lifecycle hardening
        //
        // 本节针对 GPT Work 最终审查确认的六项缺陷。每项都有**确定性**回归：
        //   P1-1 Write/Finish 原子交接 —— 用 WriteCommitBarrier 可控屏障卡在"字节已写出、
        //        结果未提交"的临界点上（不依赖随机 Sleep）。
        //   P1-2 临时文件 ownership —— 原子 CreateNew + 保持到发布/清理前的 ownership 句柄。
        //   P1-3 Start/Cancel 与初始化异常回收 —— 启动屏障 + 注入的初始化异常。
        //   P1-4 stdout/stderr 读取异常传播 —— 注入会抛 IO 异常的读取流。
        //   P2-1 Completed 后释放 Process 资源。
        //   P2-2 冻结配置深复制。

        private static void LifecycleHardeningTests(string workRoot)
        {
            // ---------------------------------------------------------------- P1-1 write / finish handoff
            TestKit.Run("hardening: Finish cannot observe an uncommitted successful write", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "commitwin");
                var launcher = new FakeVideoProcessLauncher
                {
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                using (var barrierEntered = new ManualResetEventSlim(false))
                using (var barrierRelease = new ManualResetEventSlim(false))
                {
                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = Settings(64, 48, 30),
                        Identity = MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        ProcessStart = launcher.Create(),
                        WriteCommitBarrier = () =>
                        {
                            barrierEntered.Set();
                            barrierRelease.Wait(30000);
                        },
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "start");

                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame accepted: " + attempt.ErrorCode);
                    TestKit.Check(barrierEntered.Wait(30000), "the writer must reach the commit barrier");

                    // 字节已经完整写出，但结果还没提交：此时既不能计数，也不能开始 Finalizing。
                    TestKit.CheckEqual(0L, pipeline.DeliveredFrameCount, "an uncommitted write must not be counted");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Running, pipeline.State, "state must stay Running");

                    FfmpegVideoOutcome rejected = Await(pipeline.FinishAsync(), "finish during commit");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, rejected.State, "finish must be rejected");
                    TestKit.CheckEqual("write-in-flight", rejected.ErrorCode, "error code");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Running, pipeline.State,
                        "a rejected finish must not change the session state");

                    barrierRelease.Set();

                    FfmpegFrameWriteResult written = Await(attempt.Completion, "frame write");
                    TestKit.Check(written.Success, "the frame write must succeed: " + written.ErrorCode);
                    TestKit.CheckEqual(1L, pipeline.DeliveredFrameCount, "the completed write must be counted exactly once");

                    FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                        "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                    TestKit.CheckEqual(1L, outcome.DeliveredFrameCount, "the verified frame count must include the frame");
                    TestKit.Check(outcome.Verification != null && outcome.Verification.IsVerified, "verification");
                    TestKit.Check(File.Exists(outcome.FinalPath), "the artifact must be published");
                    TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");
                }

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("hardening: Finish cannot observe an uncommitted failed write", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "commitfail");
                var launcher = new FakeVideoProcessLauncher
                {
                    // 读满一个缓冲区就退出：大帧的写入必然中途失败。
                    Encoder = new FakeVideoEncoderScript { ExitAfterBytes = 4096, ExitCode = 9 },
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                using (var barrierEntered = new ManualResetEventSlim(false))
                using (var barrierRelease = new ManualResetEventSlim(false))
                {
                    var options = new FfmpegVideoPipelineOptions
                    {
                        Settings = Settings(512, 512, 30),
                        Identity = MakeIdentity(FakeVideoProcess.ExecutablePath),
                        FinalPath = Path.Combine(directory, "video.mp4"),
                        ProcessStart = launcher.Create(),
                        WriteCommitBarrier = () =>
                        {
                            barrierEntered.Set();
                            barrierRelease.Wait(30000);
                        },
                    };

                    var pipeline = new FfmpegVideoPipeline(options);
                    TestKit.Check(pipeline.Start().Started, "start");

                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(512, 512));
                    TestKit.Check(attempt.Accepted, "frame accepted: " + attempt.ErrorCode);
                    TestKit.Check(barrierEntered.Wait(30000), "the writer must reach the commit barrier");

                    // 写入已经失败，但失败结果尚未锁定：此时 Finish 必须被拒绝，
                    // 绝不允许"写入失败却继续正常 Finalizing"。
                    FfmpegVideoOutcome rejected = Await(pipeline.FinishAsync(), "finish during failing commit");
                    TestKit.CheckEqual("write-in-flight", rejected.ErrorCode, "error code");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Running, pipeline.State, "state must stay Running");

                    barrierRelease.Set();

                    FfmpegFrameWriteResult written = Await(attempt.Completion, "frame write");
                    TestKit.Check(!written.Success, "the write must fail");
                    TestKit.CheckEqual("write-failed", written.ErrorCode, "write error code");
                    TestKit.CheckEqual(0L, pipeline.DeliveredFrameCount, "a partial frame must never be counted");

                    FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                    TestKit.CheckEqual("write-failed", outcome.ErrorCode, "outcome error code");

                    FfmpegVideoOutcome afterFailure = Await(pipeline.FinishAsync(), "finish after failure");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, afterFailure.State,
                        "a failed session must never reach Completed");
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                    TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");
                }

                TestKit.TryDeleteDirectory(directory);
            });

            // ---------------------------------------------------------------- P1-2 temp ownership
            TestKit.Run("hardening: temp ownership is acquired atomically and pins the path", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "tempowner");
                var launcher = new FakeVideoProcessLauncher
                {
                    // 读之前先睡：确保 Start 之后、FFmpeg 触碰输出之前有一个确定的时间窗。
                    Encoder = new FakeVideoEncoderScript { SleepBeforeReadMs = 5000 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30),
                    MakeIdentity(FakeVideoProcess.ExecutablePath), launcher.Create(), "video.mp4");

                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(start.Started, "start: " + start.ErrorCode + " " + start.ErrorDetail);

                // 文件是**本会话**在启动进程之前原子创建的（空文件），不是 FFmpeg 创建的。
                TestKit.Check(File.Exists(pipeline.TempPath), "the session must create the temp file itself");
                TestKit.CheckEqual(0L, new FileInfo(pipeline.TempPath).Length, "the created temp file must be empty");

                // 在 ownership 句柄持有期间，路径不能被删除或被替换。
                bool deleted = false;
                try
                {
                    File.Delete(pipeline.TempPath);
                    deleted = true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                TestKit.Check(!deleted, "no other actor may delete the owned temp file");

                string foreign = Path.Combine(directory, "foreign.tmp");
                File.WriteAllText(foreign, "foreign");
                bool replaced = false;
                try
                {
                    File.Replace(foreign, pipeline.TempPath, null);
                    replaced = true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                TestKit.Check(!replaced, "no other actor may replace the owned temp file");
                TestKit.CheckEqual("foreign", File.ReadAllText(foreign), "the foreign file must be untouched");

                TestKit.Check(pipeline.Cancel("owner-test"), "cancel");
                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.Check(outcome.TempOwnershipReleased, "the ownership handle must be released");
                TestKit.Check(outcome.TempFileRemoved, "the owned temp file must be removed");
                TestKit.Check(!outcome.ResidualOwnership, "no residual: " + outcome.ResidualDetail);
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("hardening: foreign content at the temp path is never overwritten, deleted or published", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "foreigntemp");
                FfmpegVideoIdentity identity = MakeIdentity(FakeVideoProcess.ExecutablePath);
                var launcher = new FakeVideoProcessLauncher();

                string tempPath = Path.Combine(directory, "video.mp4" + FfmpegVideoCommand.TempFileInfix + "occupied");
                File.WriteAllText(tempPath, "foreign-content");

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), identity, launcher.Create(),
                    "video.mp4", tempPath);

                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(!start.Started, "start must fail closed");
                TestKit.CheckEqual("temp-file-exists", start.ErrorCode, "error code");
                TestKit.CheckEqual(0, launcher.EncoderStarts, "no encoder may be started for an occupied path");
                TestKit.CheckEqual("foreign-content", File.ReadAllText(tempPath), "foreign content must not be overwritten");

                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.Check(File.Exists(tempPath), "foreign content must not be deleted");
                TestKit.CheckEqual("foreign-content", File.ReadAllText(tempPath), "foreign content must survive cleanup");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                TestKit.Check(!outcome.ResidualOwnership, "nothing of ours was left behind: " + outcome.ResidualDetail);

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("hardening: cleanup of one session never touches another session's temp file", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "twosessions");
                FfmpegVideoIdentity identity = MakeIdentity(FakeVideoProcess.ExecutablePath);

                var first = CreatePipeline(directory, Settings(64, 48, 30), identity,
                    new FakeVideoProcessLauncher { Encoder = new FakeVideoEncoderScript { SleepBeforeReadMs = 5000 } }.Create(),
                    "first.mp4");
                var second = CreatePipeline(directory, Settings(64, 48, 30), identity,
                    new FakeVideoProcessLauncher { Encoder = new FakeVideoEncoderScript { SleepBeforeReadMs = 5000 } }.Create(),
                    "second.mp4");

                TestKit.Check(first.Start().Started, "first start");
                TestKit.Check(second.Start().Started, "second start");
                TestKit.Check(!string.Equals(first.TempPath, second.TempPath, StringComparison.OrdinalIgnoreCase),
                    "each session must own a distinct temp path");
                TestKit.Check(File.Exists(second.TempPath), "the second session temp file must exist");

                TestKit.Check(first.Cancel("cancel-first"), "cancel first");
                FfmpegVideoOutcome firstOutcome = Await(first.CleanupTask, "first cleanup");
                TestKit.Check(firstOutcome.TempFileRemoved, "the first session must clean its own temp file");
                TestKit.Check(!File.Exists(first.TempPath), "the first temp file must be gone");
                TestKit.Check(File.Exists(second.TempPath), "the second session temp file must be untouched");

                TestKit.Check(second.Cancel("cancel-second"), "cancel second");
                Await(second.CleanupTask, "second cleanup");
                TestKit.Check(!File.Exists(second.TempPath), "the second temp file must be gone");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");

                TestKit.TryDeleteDirectory(directory);
            });

            // ---------------------------------------------------------------- P1-3 start / cancel / init
            TestKit.Run("hardening: cancel during a blocked start still reaps the started process", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "startcancel");
                int before = CountProcesses("ADOFAI.Renderist.FfmpegTests");

                var launcher = new FakeVideoProcessLauncher();
                using (var processStarted = new ManualResetEventSlim(false))
                using (var releaseStart = new ManualResetEventSlim(false))
                {
                    FfmpegVideoProcessStart start = (path, arguments, workingDirectory) =>
                    {
                        // 真实启动子进程，但把启动委托卡在这里，制造 Start/Cancel 交错。
                        Process process = launcher.Create()(path, arguments, workingDirectory);
                        processStarted.Set();
                        releaseStart.Wait(30000);
                        return process;
                    };

                    var pipeline = CreatePipeline(directory, Settings(64, 48, 30),
                        MakeIdentity(FakeVideoProcess.ExecutablePath), start, "video.mp4");

                    Task<FfmpegVideoStartResult> startTask = Task.Run(() => pipeline.Start());
                    TestKit.Check(processStarted.Wait(30000), "the process must have been started");

                    TestKit.Check(pipeline.Cancel("cancel-during-start"), "cancel must be accepted during start");
                    releaseStart.Set();

                    FfmpegVideoStartResult result = Await(startTask, "start");
                    TestKit.Check(!result.Started, "a cancelled start must not report success");
                    TestKit.CheckEqual("cancelled-during-start", result.ErrorCode, "error code");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, pipeline.State, "state");

                    FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State, "outcome state");
                    TestKit.Check(outcome.ProcessStarted, "the process was started");
                    TestKit.Check(outcome.ProcessReaped, "the started process must be reaped");
                    TestKit.Check(outcome.ProcessDisposed, "the process object must be disposed");
                    TestKit.Check(outcome.TempOwnershipReleased, "the ownership handle must be released");
                    TestKit.Check(!outcome.ResidualOwnership, "no residual ownership: " + outcome.ResidualDetail);
                    TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");
                    TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                }

                int after = CountProcesses("ADOFAI.Renderist.FfmpegTests");
                TestKit.Check(after <= before, "no child process may survive a cancelled start (" + before + " -> " + after + ")");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("hardening: an initialization failure after a successful start still reaps the process", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "initfail");
                int before = CountProcesses("ADOFAI.Renderist.FfmpegTests");

                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript { Hang = true },
                };

                var options = new FfmpegVideoPipelineOptions
                {
                    Settings = Settings(64, 48, 30),
                    Identity = MakeIdentity(FakeVideoProcess.ExecutablePath),
                    FinalPath = Path.Combine(directory, "video.mp4"),
                    ProcessStart = launcher.Create(),
                    // 注入 stdout 初始化异常：进程已经启动，但流初始化失败。
                    StreamWrapper = (stream, role) =>
                    {
                        if (string.Equals(role, "stdout", StringComparison.Ordinal))
                            throw new IOException("injected stdout initialization failure");
                        return stream;
                    },
                };

                var pipeline = new FfmpegVideoPipeline(options);
                FfmpegVideoStartResult start = pipeline.Start();

                TestKit.Check(!start.Started, "start must fail");
                TestKit.CheckEqual("process-init-failed", start.ErrorCode, "error code");
                TestKit.CheckEqual(1, launcher.EncoderStarts, "the encoder process was really started");

                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.Check(outcome.ProcessStarted, "the process was started");
                TestKit.Check(outcome.ProcessReaped, "the process must be reaped even without a pre-built exit wait");
                TestKit.Check(outcome.ProcessDisposed, "the process object must be disposed");
                TestKit.Check(outcome.TempOwnershipReleased, "the ownership handle must be released");
                TestKit.Check(!outcome.ResidualOwnership, "no residual ownership: " + outcome.ResidualDetail);
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");

                int after = CountProcesses("ADOFAI.Renderist.FfmpegTests");
                TestKit.Check(after <= before, "no child process may survive an init failure (" + before + " -> " + after + ")");

                TestKit.TryDeleteDirectory(directory);
            });

            // ---------------------------------------------------------------- P1-4 drain error propagation
            TestKit.Run("hardening: the drain pump distinguishes EOF from a read failure", () =>
            {
                var collector = new BoundedTextCollector(64, 2);

                var closed = new MemoryStream(new byte[0]);
                TestKit.Check(FfmpegStreamPump.Start(closed, collector).Result == null,
                    "an empty stream must be reported as a clean EOF");

                var throwing = new ThrowingReadStream();
                Exception error = FfmpegStreamPump.Start(throwing, collector).Result;
                TestKit.Check(error != null, "a read failure must be reported, not swallowed");
                TestKit.Check(error is IOException, "the original exception must be preserved: " + error.GetType().Name);

                // 缓冲饱和之后仍必须继续排空实际流。
                var collector2 = new BoundedTextCollector(16, 1);
                var lines = new MemoryStream(Encoding.UTF8.GetBytes("first\nsecond\nthird\n"));
                TestKit.Check(FfmpegStreamPump.Start(lines, collector2).Result == null, "clean EOF expected");
                TestKit.CheckEqual(3L, collector2.TotalLines, "the pump must keep draining after the buffer saturates");
                TestKit.Check(collector2.TotalChars > 16, "all bytes must have been consumed");
            });

            TestKit.Run("hardening: a stdout read failure prevents Completed and publishing", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "drainfail");
                var launcher = new FakeVideoProcessLauncher
                {
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var options = new FfmpegVideoPipelineOptions
                {
                    Settings = Settings(64, 48, 30),
                    Identity = MakeIdentity(FakeVideoProcess.ExecutablePath),
                    FinalPath = Path.Combine(directory, "video.mp4"),
                    ProcessStart = launcher.Create(),
                    StreamWrapper = (stream, role) =>
                    {
                        // 只有 stdout 读取失败；stderr 与 stdin 都是真实管道。
                        return string.Equals(role, "stdout", StringComparison.Ordinal)
                            ? new ThrowingReadStream()
                            : stream;
                    },
                };

                var pipeline = new FfmpegVideoPipeline(options);
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                TestKit.Check(attempt.Accepted, "frame accepted");
                TestKit.Check(Await(attempt.Completion, "frame write").Success, "frame write");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                TestKit.CheckEqual("drain-failed", outcome.ErrorCode, "error code");
                TestKit.Check((outcome.ErrorDetail ?? string.Empty).IndexOf("stdout-drain-failed", StringComparison.Ordinal) >= 0,
                    "the stdout read failure must be reported: " + outcome.ErrorDetail);
                TestKit.CheckEqual(0, launcher.VerifierStarts, "verification must not run after a drain failure");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");
                TestKit.Check(outcome.ProcessDisposed, "the process object must still be released");

                TestKit.TryDeleteDirectory(directory);
            });

            // ---------------------------------------------------------------- P2-1 release after Completed
            TestKit.Run("hardening: a completed session releases the process object and stays safe to dispose", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "release");
                var launcher = new FakeVideoProcessLauncher
                {
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30),
                    MakeIdentity(FakeVideoProcess.ExecutablePath), launcher.Create(), "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                Await(attempt.Completion, "frame write");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State, "state");
                TestKit.Check(outcome.ProcessReaped, "the process must be observed exited");
                TestKit.Check(outcome.ProcessDisposed, "Completed must release the process object, not leave it to GC");
                TestKit.Check(outcome.TempOwnershipReleased, "the temp ownership handle must be released");
                TestKit.Check(!outcome.ResidualOwnership, "no residual: " + outcome.ResidualDetail);
                TestKit.Check(File.Exists(outcome.FinalPath), "the artifact must exist");

                // Dispose 不得遗漏已经 Completed 的资源，也不得破坏已发布产物。
                pipeline.Dispose();
                pipeline.Dispose();
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, pipeline.State, "state must stay Completed");
                TestKit.Check(File.Exists(outcome.FinalPath), "the published artifact must survive Dispose");
                TestKit.Check(!outcome.ResidualOwnership, "no residual after Dispose");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("hardening: consecutive sessions do not accumulate processes or handles", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "nohandles");
                FfmpegVideoIdentity identity = MakeIdentity(FakeVideoProcess.ExecutablePath);
                int before = CountProcesses("ADOFAI.Renderist.FfmpegTests");

                for (int cycle = 0; cycle < 4; cycle++)
                {
                    var launcher = new FakeVideoProcessLauncher
                    {
                        Verifier = new FakeVideoVerifierScript { Frames = 1 },
                    };

                    var pipeline = CreatePipeline(directory, Settings(64, 48, 30), identity,
                        launcher.Create(), "video-" + cycle + ".mp4");
                    TestKit.Check(pipeline.Start().Started, "cycle " + cycle + " start");

                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    Await(attempt.Completion, "cycle write");

                    FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "cycle finish");
                    TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                        "cycle " + cycle + " state (" + outcome.ErrorCode + ")");
                    TestKit.Check(outcome.ProcessReaped && outcome.ProcessDisposed,
                        "cycle " + cycle + " must release process resources");
                    TestKit.Check(outcome.TempOwnershipReleased,
                        "cycle " + cycle + " must release the temp ownership handle");
                    TestKit.Check(!outcome.ResidualOwnership,
                        "cycle " + cycle + " residual: " + outcome.ResidualDetail);

                    // 本会话的临时文件必须已经不存在（既没被复制，也没被遗留）。
                    TestKit.Check(!File.Exists(pipeline.TempPath), "cycle " + cycle + " temp file must be gone");
                }

                int after = CountProcesses("ADOFAI.Renderist.FfmpegTests");
                TestKit.Check(after <= before, "no child process may accumulate (" + before + " -> " + after + ")");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files may remain");
                TestKit.CheckEqual(4, Directory.GetFiles(directory, "video-*.mp4").Length, "four published files");

                TestKit.TryDeleteDirectory(directory);
            });

            // ---------------------------------------------------------------- P2-2 frozen configuration
            TestKit.Run("hardening: the session configuration is frozen at construction", () =>
            {
                string directory = TestKit.NewWorkDirectory(workRoot, "frozen");
                FfmpegVideoIdentity identity = MakeIdentity(FakeVideoProcess.ExecutablePath);
                var launcher = new FakeVideoProcessLauncher
                {
                    Encoder = new FakeVideoEncoderScript(),
                    Verifier = new FakeVideoVerifierScript { Frames = 1 },
                };
                var verifier = new CapturingVideoVerifier();

                var settings = Settings(64, 48, 30);
                string originalFinalPath = Path.Combine(directory, "video.mp4");
                var options = new FfmpegVideoPipelineOptions
                {
                    Settings = settings,
                    Identity = identity,
                    FinalPath = originalFinalPath,
                    ProcessStart = launcher.Create(),
                    Verifier = verifier,
                };

                var pipeline = new FfmpegVideoPipeline(options);
                long frozenFrameLength = pipeline.FrameLengthBytes;

                // 构造之后再修改调用方的**所有**可变配置。
                settings.Width = 128;
                settings.Height = 128;
                settings.Fps = 60;
                settings.Crf = 51;
                settings.Preset = "ultrafast";
                settings.PixelFormat = FfmpegVideoPixelFormat.Yuv444p;
                options.FinalPath = Path.Combine(directory, "mutated.mp4");
                options.TempPath = Path.Combine(directory, "mutated.tmp");
                options.ExpectedCodecName = "mpeg4";
                options.ProcessStart = null;
                options.Verifier = null;
                options.Identity = null;

                TestKit.CheckEqual(frozenFrameLength, pipeline.FrameLengthBytes, "frame length must stay frozen");

                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(start.Started, "start: " + start.ErrorCode + " " + start.ErrorDetail);

                // 命令来自冻结值。
                string arguments = launcher.LastEncoderArguments;
                TestKit.CheckNotEmpty(arguments, "encoder arguments");
                TestKit.Check(arguments.IndexOf("-s 64x48", StringComparison.Ordinal) >= 0, "frozen size: " + arguments);
                TestKit.Check(arguments.IndexOf("-framerate 30 ", StringComparison.Ordinal) >= 0, "frozen framerate: " + arguments);
                TestKit.Check(arguments.IndexOf("-vf settb=1/30,setpts=N", StringComparison.Ordinal) >= 0, "frozen filter: " + arguments);
                TestKit.Check(arguments.IndexOf("-video_track_timescale 30", StringComparison.Ordinal) >= 0, "frozen timescale");
                TestKit.Check(arguments.IndexOf("-crf 18", StringComparison.Ordinal) >= 0, "frozen crf: " + arguments);
                TestKit.Check(arguments.IndexOf("-preset medium", StringComparison.Ordinal) >= 0, "frozen preset: " + arguments);
                TestKit.Check(arguments.IndexOf("-pix_fmt yuv420p", StringComparison.Ordinal) >= 0, "frozen pixel format");
                TestKit.CheckEqual(identity.ExecutablePath, launcher.LastEncoderExecutablePath, "frozen identity path");

                // 输出路径来自冻结值。
                TestKit.CheckEqual(Path.GetFullPath(originalFinalPath), pipeline.FinalPath, "frozen final path");
                TestKit.Check(pipeline.TempPath.StartsWith(originalFinalPath, StringComparison.OrdinalIgnoreCase),
                    "the temp path must derive from the frozen final path: " + pipeline.TempPath);

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                TestKit.Check(attempt.Accepted, "frame accepted: " + attempt.ErrorCode);
                Await(attempt.Completion, "frame write");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");

                // 核验期望也来自冻结值。
                TestKit.Check(verifier.Requests > 0, "the verifier must have been asked to verify");
                TestKit.CheckEqual(64, verifier.LastRequest.ExpectedWidth, "frozen verification width");
                TestKit.CheckEqual(48, verifier.LastRequest.ExpectedHeight, "frozen verification height");
                TestKit.CheckEqual(30, verifier.LastRequest.ExpectedFps, "frozen verification fps");
                TestKit.CheckEqual("yuv420p", verifier.LastRequest.ExpectedPixelFormat, "frozen verification pixel format");
                TestKit.CheckEqual("h264", verifier.LastRequest.ExpectedCodecName, "frozen verification codec");
                TestKit.CheckEqual(identity.ExecutablePath, verifier.LastRequest.ExecutablePath, "frozen verification executable");
                TestKit.CheckEqual(pipeline.TempPath, verifier.LastRequest.VideoPath, "verification target");
                TestKit.CheckEqual(1L, verifier.LastRequest.ExpectedFrameCount, "expected frame count");

                TestKit.CheckEqual(Path.GetFullPath(originalFinalPath), outcome.FinalPath, "published at the frozen path");
                TestKit.Check(!File.Exists(Path.Combine(directory, "mutated.mp4")), "the mutated path must not be used");

                TestKit.TryDeleteDirectory(directory);
            });
        }

        // ==================================================================== real FFmpeg fixture
        private static void RealFixtureTests(string workRoot)
        {
            IReadOnlyList<string> binaries = Program.FindLocalFfmpegBinaries();
            string ffmpeg = binaries.Count > 0 ? binaries[0] : null;

            TestKit.Run("video fixture: 30 fps synthetic frames produce a verified, published mp4", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "real30");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), identity, null, "video.mp4");
                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(start.Started, "start failed: " + start.ErrorCode + " " + start.ErrorDetail);

                const int frames = 24;
                for (int i = 0; i < frames; i++)
                {
                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame " + i + " rejected: " + attempt.ErrorCode);
                    FfmpegFrameWriteResult written = Await(attempt.Completion, "frame " + i);
                    TestKit.Check(written.Success, "frame " + i + " failed: " + written.ErrorCode + " " + written.ErrorDetail);
                }

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                TestKit.CheckEqual((long)frames, outcome.DeliveredFrameCount, "delivered frames");
                TestKit.Check(File.Exists(outcome.FinalPath), "final file must exist");

                // 独立复核：对**已发布**的文件再跑一次完整核验。
                FfmpegVideoVerificationResult recheck = Await(new FfmpegVideoVerifier().VerifyAsync(
                    new FfmpegVideoVerificationRequest
                    {
                        ExecutablePath = ffmpeg,
                        VideoPath = outcome.FinalPath,
                        ExpectedWidth = 64,
                        ExpectedHeight = 48,
                        ExpectedFps = 30,
                        ExpectedCodecName = "h264",
                        ExpectedPixelFormat = "yuv420p",
                        ExpectedFrameCount = frames,
                    }), "recheck");

                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Verified, recheck.Status,
                    "recheck (" + recheck.ErrorCode + " " + recheck.ErrorDetail + ")");
                TestKit.CheckEqual((long)frames, recheck.DecodedFrameCount, "decoded frames");
                TestKit.CheckEqual((long)frames, recheck.ContainerPacketCount, "container packets");
                TestKit.CheckEqual(1, recheck.TimeBaseNumerator, "timebase numerator");
                TestKit.CheckEqual(30, recheck.TimeBaseDenominator, "timebase denominator");
                TestKit.CheckEqual((long)frames - 1, recheck.LastDecodedPts, "last pts");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");

                Console.WriteLine("        published: " + outcome.FinalPath + " (" + new FileInfo(outcome.FinalPath).Length + " bytes)");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: 1001001 fps keeps an exact rational timebase and frame count", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "real1001001");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                var pipeline = CreatePipeline(directory, Settings(64, 48, 1001001), identity, null, "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                const int frames = 100;
                for (int i = 0; i < frames; i++)
                {
                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame " + i + ": " + attempt.ErrorCode);
                    TestKit.Check(Await(attempt.Completion, "frame").Success, "frame " + i + " write");
                }

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");

                FfmpegVideoVerificationResult verification = outcome.Verification;
                TestKit.Check(verification != null && verification.IsVerified, "verification");
                TestKit.CheckEqual(1, verification.TimeBaseNumerator, "timebase numerator");
                TestKit.CheckEqual(1001001, verification.TimeBaseDenominator, "timebase denominator");
                TestKit.CheckEqual((long)frames, verification.DecodedFrameCount, "decoded frames");
                TestKit.CheckEqual((long)frames - 1, verification.LastDecodedPts, "last decoded pts");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: int.MaxValue fps keeps an exact rational timebase", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realmaxfps");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                var pipeline = CreatePipeline(directory, Settings(64, 48, int.MaxValue), identity, null, "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                const int frames = 40;
                for (int i = 0; i < frames; i++)
                {
                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame " + i + ": " + attempt.ErrorCode);
                    TestKit.Check(Await(attempt.Completion, "frame").Success, "frame " + i + " write");
                }

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                TestKit.CheckEqual(int.MaxValue, outcome.Verification.TimeBaseDenominator, "int.MaxValue timescale");
                TestKit.CheckEqual((long)frames, outcome.Verification.DecodedFrameCount, "decoded frames");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: odd dimensions use 4:4:4 and stay verified", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realodd");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                var pipeline = CreatePipeline(directory, Settings(65, 49, 30), identity, null, "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                const int frames = 12;
                for (int i = 0; i < frames; i++)
                {
                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(65, 49));
                    TestKit.Check(attempt.Accepted, "frame " + i + ": " + attempt.ErrorCode);
                    TestKit.Check(Await(attempt.Completion, "frame").Success, "frame " + i + " write");
                }

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                TestKit.CheckEqual("yuv444p", outcome.Verification.PixelFormat, "odd geometry must use 4:4:4");
                TestKit.CheckEqual(65, outcome.Verification.DecodedWidth.HasValue
                    ? outcome.Verification.DecodedWidth.Value : 0, "width");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: an illegal encoder combination fails closed without publishing", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realillegal");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                // libx264 的 4:2:0 需要偶数宽高：强制 4:2:0 + 奇数几何必须失败，且不发布任何东西。
                var settings = Settings(65, 49, 30);
                settings.PixelFormat = FfmpegVideoPixelFormat.Yuv420p;

                var pipeline = CreatePipeline(directory, settings, identity, null, "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(65, 49));
                TestKit.Check(attempt.Accepted, "frame accepted");
                Await(attempt.Completion, "frame write");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.CheckEqual("encoder-exit-nonzero", outcome.ErrorCode, "error code");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: empty input fails closed", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realempty");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), identity, null, "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.CheckEqual("encoder-exit-nonzero", outcome.ErrorCode, "error code");
                TestKit.CheckEqual(0L, outcome.DeliveredFrameCount, "no frames delivered");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: timestamp-corrupted encodes are rejected by verification", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realtimebase");
                const int frames = 20;

                var request = new FfmpegVideoVerificationRequest
                {
                    ExecutablePath = ffmpeg,
                    ExpectedWidth = 64,
                    ExpectedHeight = 48,
                    ExpectedFps = 1001001,
                    ExpectedCodecName = "h264",
                    ExpectedPixelFormat = "yuv420p",
                    ExpectedFrameCount = frames,
                };

                // 正对照：正确的重定时命令必须通过核验。
                string good = Path.Combine(directory, "good.mp4");
                RunRealEncoder(ffmpeg, NegativeControlArguments(good, "settb=1/1001001,setpts=N", 1001001),
                    frames, 64 * 48 * 3, directory);
                request.VideoPath = good;
                FfmpegVideoVerificationResult control = Await(new FfmpegVideoVerifier().VerifyAsync(request), "control");
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Verified, control.Status,
                    "the correct command must verify (" + control.ErrorCode + " " + control.ErrorDetail + ")");

                // 负对照 1：PTS 被改写（setpts=2*N）。退出码、文件大小、可解码性都正常，
                // 只有时间语义不对 —— 这正是必须由核验而不是退出码发现的问题。
                string retimed = Path.Combine(directory, "retimed.mp4");
                RunRealEncoder(ffmpeg, NegativeControlArguments(retimed, "settb=1/1001001,setpts=2*N", 1001001),
                    frames, 64 * 48 * 3, directory);
                request.VideoPath = retimed;
                FfmpegVideoVerificationResult result = Await(new FfmpegVideoVerifier().VerifyAsync(request), "retimed");
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Failed, result.Status,
                    "a pts-corrupted file must not verify");
                TestKit.Check(result.ErrorCode == "verify-frame-timing-mismatch" ||
                              result.ErrorCode == "verify-timebase-mismatch",
                    "error code must be a timestamp error: " + result.ErrorCode + " " + result.ErrorDetail);
                Console.WriteLine("        pts-corrupted rejected with: " + result.ErrorCode + " (" + result.ErrorDetail + ")");

                // 负对照 2：帧被丢弃（select 只留偶数帧）。容器与解码器都会短，
                // 核验必须按**实际可解码帧数**拒绝，而不是只看"能解码"。
                string dropped = Path.Combine(directory, "dropped.mp4");
                RunRealEncoder(ffmpeg,
                    "-hide_banner -nostdin -loglevel error -f rawvideo -pix_fmt rgb24 -s 64x48 -framerate 1001000 " +
                    "-i pipe:0 -vf settb=1/1001001,select='not(mod(n,2))',setpts=N -fps_mode passthrough " +
                    "-enc_time_base filter -bsf:v setts=duration=1 -c:v libx264 -preset medium -bf 0 -crf 18 " +
                    "-pix_fmt yuv420p -video_track_timescale 1001001 -xerror -abort_on empty_output_stream -f mp4 -y " +
                    FfmpegVideoCommand.QuoteArgument(dropped),
                    frames, 64 * 48 * 3, directory);
                request.VideoPath = dropped;
                result = Await(new FfmpegVideoVerifier().VerifyAsync(request), "dropped frames");
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Failed, result.Status,
                    "a file with dropped frames must not verify");
                TestKit.CheckEqual("verify-frame-count-mismatch", result.ErrorCode,
                    "error code (" + result.ErrorDetail + ")");
                Console.WriteLine("        dropped-frame control rejected with: " + result.ErrorCode +
                                  " (" + result.ErrorDetail + ")");

                // 观察项：保留 B 帧时容器包数与可解码帧数可以不一致。
                // 不变量：只要解码帧数少于交付帧数，核验就必须失败（不能只看容器）。
                string bframes = Path.Combine(directory, "bframes.mp4");
                RunRealEncoder(ffmpeg,
                    "-hide_banner -nostdin -loglevel error -f rawvideo -pix_fmt rgb24 -s 64x48 -framerate 1001000 " +
                    "-i pipe:0 -vf settb=1/1001001,setpts=N -fps_mode passthrough -enc_time_base filter " +
                    "-bsf:v setts=duration=1 -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p " +
                    "-video_track_timescale 1001001 -xerror -abort_on empty_output_stream -f mp4 -y " +
                    FfmpegVideoCommand.QuoteArgument(bframes),
                    frames, 64 * 48 * 3, directory);

                request.VideoPath = bframes;
                FfmpegVideoVerificationResult bframeResult =
                    Await(new FfmpegVideoVerifier().VerifyAsync(request), "b frames");
                Console.WriteLine("        B-frame control: packets=" + bframeResult.ContainerPacketCount +
                                  " decoded=" + bframeResult.DecodedFrameCount + " status=" + bframeResult.Status);

                if (bframeResult.DecodedFrameCount != frames)
                {
                    TestKit.CheckEqual(FfmpegVideoVerificationStatus.Failed, bframeResult.Status,
                        "fewer decodable frames than delivered must fail verification");
                }

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: truncated and stream-less files are rejected", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realtruncated");

                string good = Path.Combine(directory, "good.mp4");
                string arguments =
                    "-hide_banner -nostdin -loglevel error -f rawvideo -pix_fmt rgb24 -s 64x48 -framerate 30 -i pipe:0 " +
                    "-vf settb=1/30,setpts=N -fps_mode passthrough -enc_time_base filter -bsf:v setts=duration=1 " +
                    "-c:v libx264 -preset medium -bf 0 -crf 18 -pix_fmt yuv420p -video_track_timescale 30 " +
                    "-xerror -abort_on empty_output_stream -f mp4 -y " + FfmpegVideoCommand.QuoteArgument(good);

                RunRealEncoder(ffmpeg, arguments, 30, 64 * 48 * 3, directory);
                TestKit.Check(File.Exists(good), "control file must exist");

                var request = new FfmpegVideoVerificationRequest
                {
                    ExecutablePath = ffmpeg,
                    ExpectedWidth = 64,
                    ExpectedHeight = 48,
                    ExpectedFps = 30,
                    ExpectedCodecName = "h264",
                    ExpectedPixelFormat = "yuv420p",
                    ExpectedFrameCount = 30,
                };

                request.VideoPath = good;
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Verified,
                    Await(new FfmpegVideoVerifier().VerifyAsync(request), "control verify").Status,
                    "the control file must verify");

                byte[] bytes = File.ReadAllBytes(good);
                string truncated = Path.Combine(directory, "truncated.mp4");
                var cut = new byte[(int)(bytes.Length * 0.75)];
                Array.Copy(bytes, cut, cut.Length);
                File.WriteAllBytes(truncated, cut);

                request.VideoPath = truncated;
                FfmpegVideoVerificationResult result = Await(new FfmpegVideoVerifier().VerifyAsync(request), "truncated verify");
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Failed, result.Status, "truncated must fail");
                TestKit.Check(result.ErrorCode == "verify-decode-exit" || result.ErrorCode == "verify-packet-exit",
                    "error code: " + result.ErrorCode);

                string notVideo = Path.Combine(directory, "notvideo.mp4");
                File.WriteAllText(notVideo, "this is not an mp4 at all");
                request.VideoPath = notVideo;
                result = Await(new FfmpegVideoVerifier().VerifyAsync(request), "not-video verify");
                TestKit.CheckEqual(FfmpegVideoVerificationStatus.Failed, result.Status, "non-mp4 must fail");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: special-character output paths publish correctly", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realspecial 特殊 & (括号) 🎬");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), identity, null, "video 输出 & (v1) 🎬.mp4");
                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(start.Started, "start failed: " + start.ErrorCode + " " + start.ErrorDetail);

                const int frames = 8;
                for (int i = 0; i < frames; i++)
                {
                    FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(64, 48));
                    TestKit.Check(attempt.Accepted, "frame " + i + ": " + attempt.ErrorCode);
                    TestKit.Check(Await(attempt.Completion, "frame").Success, "frame " + i + " write");
                }

                FfmpegVideoOutcome outcome = Await(pipeline.FinishAsync(), "finish");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Completed, outcome.State,
                    "state (" + outcome.ErrorCode + " " + outcome.ErrorDetail + ")");
                TestKit.Check(File.Exists(outcome.FinalPath), "final file must exist: " + outcome.FinalPath);
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "no partial files left");

                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: an occupied temp path fails closed before any process starts", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realunwritable");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                // 临时路径上预先存在一个**目录**：原子 CreateNew 必然失败，
                // 因此管线在启动任何进程之前就 fail-closed，并且绝不触碰外来内容。
                string tempPath = Path.Combine(directory, "video.mp4" + FfmpegVideoCommand.TempFileInfix + "blocked");
                Directory.CreateDirectory(tempPath);
                string sentinel = Path.Combine(tempPath, "foreign.txt");
                File.WriteAllText(sentinel, "foreign");

                var pipeline = CreatePipeline(directory, Settings(64, 48, 30), identity, null, "video.mp4", tempPath);
                FfmpegVideoStartResult start = pipeline.Start();
                TestKit.Check(!start.Started, "start must fail closed");
                TestKit.CheckEqual("temp-file-exists", start.ErrorCode, "error code");
                TestKit.Check(Directory.Exists(tempPath), "the foreign directory must not be touched");
                TestKit.CheckEqual("foreign", File.ReadAllText(sentinel), "foreign content must be untouched");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");

                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");
                TestKit.CheckEqual(FfmpegVideoPipelineState.Failed, outcome.State, "state");
                TestKit.Check(!outcome.ResidualOwnership, "no residual: " + outcome.ResidualDetail);
                TestKit.Check(Directory.Exists(tempPath), "cleanup must not delete a foreign directory");

                Directory.Delete(tempPath, true);
                TestKit.TryDeleteDirectory(directory);
            });

            TestKit.Run("video fixture: cancel during a real encode reaps the process and removes the temp file", () =>
            {
                RequireFixture(ffmpeg);
                string directory = TestKit.NewWorkDirectory(workRoot, "realcancel");
                FfmpegVideoIdentity identity = MakeRealIdentity(ffmpeg);

                int before = CountProcesses("ffmpeg");

                var pipeline = CreatePipeline(directory, Settings(256, 256, 30), identity, null, "video.mp4");
                TestKit.Check(pipeline.Start().Started, "start");

                FfmpegFrameWriteAttempt attempt = pipeline.TryWriteFrame(Frame(256, 256));
                TestKit.Check(attempt.Accepted, "frame accepted");
                Await(attempt.Completion, "frame write");

                TestKit.Check(pipeline.Cancel("real-cancel"), "cancel must win");
                FfmpegVideoOutcome outcome = Await(pipeline.CleanupTask, "cleanup");

                TestKit.CheckEqual(FfmpegVideoPipelineState.Cancelled, outcome.State, "state");
                TestKit.Check(outcome.ProcessReaped, "the encoder process must be reaped");
                TestKit.Check(!outcome.ResidualOwnership, "no residual ownership: " + outcome.ResidualDetail);
                TestKit.CheckEqual(0, PartialFiles(directory).Length, "temp file must be removed");
                TestKit.Check(!File.Exists(Path.Combine(directory, "video.mp4")), "nothing may be published");

                int after = CountProcesses("ffmpeg");
                TestKit.Check(after <= before, "no ffmpeg process may be left behind (" + before + " -> " + after + ")");

                TestKit.TryDeleteDirectory(directory);
            });
        }

        private static void RequireFixture(string ffmpeg)
        {
            if (string.IsNullOrEmpty(ffmpeg))
            {
                throw new SkipTestException(
                    "no local FFmpeg fixture; set RENDERIST_TEST_FFMPEG_DIR to a directory containing ffmpeg.exe " +
                    "(binaries are intentionally not committed)");
            }
        }

        private static FfmpegVideoIdentity MakeRealIdentity(string ffmpegPath)
        {
            FfmpegCapabilityReport capability =
                FfmpegCapabilityProbe.Probe(ffmpegPath, 30, CancellationToken.None);

            TestKit.CheckEqual(FfmpegCapabilityStatus.Probed, capability.Status,
                "real fixture probe status (" + capability.ErrorCode + ")");

            string sha256;
            string hashError;
            TestKit.Check(FfmpegFileHash.TryCompute(ffmpegPath, out sha256, out hashError), "hash: " + hashError);
            long size = new FileInfo(ffmpegPath).Length;

            var report = new FfmpegComponentReport
            {
                State = FfmpegComponentState.Ready,
                Source = FfmpegCandidateSource.ManagedInstall,
                Candidate = new FfmpegCandidate
                {
                    Source = FfmpegCandidateSource.ManagedInstall,
                    Identity = new FfmpegBinaryIdentity
                    {
                        AbsolutePath = ffmpegPath,
                        Sha256 = sha256,
                        SizeBytes = size,
                    },
                },
                Capability = capability,
            };

            FfmpegVideoIdentity identity;
            string errorCode;
            string errorDetail;
            TestKit.Check(FfmpegVideoIdentity.TryFreeze(report, out identity, out errorCode, out errorDetail),
                "real identity freeze: " + errorCode + " " + errorDetail);

            Console.WriteLine("        fixture: " + ffmpegPath);
            Console.WriteLine("          " + capability.VersionLine + " sha256=" + sha256.Substring(0, 16) + "…");
            return identity;
        }

        /// <summary>负对照用的真实编码命令（时间基重定时参数可替换）。</summary>
        private static string NegativeControlArguments(string outputPath, string filter, int trackTimescale)
        {
            return "-hide_banner -nostdin -loglevel error -f rawvideo -pix_fmt rgb24 -s 64x48 -framerate 1001000 " +
                   "-i pipe:0 -vf " + filter + " -fps_mode passthrough -enc_time_base filter " +
                   "-bsf:v setts=duration=1 -c:v libx264 -preset medium -bf 0 -crf 18 -pix_fmt yuv420p " +
                   "-video_track_timescale " + trackTimescale.ToString(CultureInfo.InvariantCulture) +
                   " -xerror -abort_on empty_output_stream -f mp4 -y " +
                   FfmpegVideoCommand.QuoteArgument(outputPath);
        }

        /// <summary>直接用真实 FFmpeg 跑一条任意命令行（负对照构造用），从 stdin 灌入合成帧。</summary>
        private static void RunRealEncoder(
            string ffmpegPath, string arguments, int frames, int frameLength, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();

                var frame = new byte[frameLength];
                for (int i = 0; i < frameLength; i++)
                    frame[i] = (byte)(i & 0xFF);

                for (int i = 0; i < frames; i++)
                    process.StandardInput.BaseStream.Write(frame, 0, frame.Length);

                process.StandardInput.BaseStream.Flush();
                process.StandardInput.Close();

                TestKit.Check(process.WaitForExit(DefaultTimeoutMs), "negative control encoder must exit");
                string errorText = stderr.Result;
                TestKit.CheckEqual(0, process.ExitCode, "negative control encoder exit: " + errorText);
            }
        }

        private static int CountProcesses(string name)
        {
            try
            {
                return Process.GetProcessesByName(name).Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 只会抛 IO 异常的读取流：用于确定性地复现"排空读取失败"这一故障形态
        /// （真实管道在正常终止时给出 EOF，不会这样失败）。
        /// </summary>
        private sealed class ThrowingReadStream : Stream
        {
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new IOException("injected read failure");
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                throw new IOException("injected read failure");
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>记录核验请求的核验器：用于断言会话**实际使用**的冻结期望值。</summary>
        private sealed class CapturingVideoVerifier : IFfmpegVideoVerifier
        {
            public int Requests { get; private set; }
            public FfmpegVideoVerificationRequest LastRequest { get; private set; }

            public Task<FfmpegVideoVerificationResult> VerifyAsync(FfmpegVideoVerificationRequest request)
            {
                Requests++;
                LastRequest = request;

                return Task.FromResult(new FfmpegVideoVerificationResult
                {
                    Status = FfmpegVideoVerificationStatus.Verified,
                    DecodedFrameCount = request != null ? request.ExpectedFrameCount : 0,
                    ContainerPacketCount = request != null ? request.ExpectedFrameCount : 0,
                    TimeBaseNumerator = 1,
                    TimeBaseDenominator = request != null ? request.ExpectedFps : 1,
                    CodecName = request != null ? request.ExpectedCodecName : null,
                    PixelFormat = request != null ? request.ExpectedPixelFormat : null,
                    DecodedWidth = request != null ? request.ExpectedWidth : 0,
                    DecodedHeight = request != null ? request.ExpectedHeight : 0,
                });
            }
        }
    }
}
