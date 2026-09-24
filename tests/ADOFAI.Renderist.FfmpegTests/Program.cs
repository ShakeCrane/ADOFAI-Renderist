using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// FFmpeg 组件管理（L1）回归测试。
    ///
    /// 断言的是**行为契约**，不是某次实验的具体数值：例如"哈希不符必须拒绝并清理暂存"，
    /// 而不是"某次下载得到 155363 字节"。
    /// </summary>
    internal static class Program
    {
        private static string _workRoot;

        private static int Main(string[] args)
        {
            // 被 L2 视频管线当作"假编码 / 假核验进程"启动时，本进程充当可控子进程。
            if (FakeVideoProcess.IsFakeInvocation(args))
                return FakeVideoProcess.Run(args);

            // 被能力探测以 -hide_banner 调用时，本进程充当"假 FFmpeg"子进程。
            if (FakeFfmpeg.IsFakeInvocation(args))
                return FakeFfmpeg.Run(args);

            _workRoot = Path.Combine(Path.GetTempPath(),
                "renderist-ffmpeg-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_workRoot);

            Console.WriteLine("ADOFAI Renderist — FFmpeg component (L1) regression tests");
            Console.WriteLine("work dir: " + _workRoot);
            Console.WriteLine();

            try
            {
                ManifestTests();
                LayoutTests();
                HashTests();
                DiscoveryTests();
                CapabilityTests();
                FakeFfmpegProbeTests();
                SafeZipTests();
                InstallLockTests();
                InstallerTests();
                DownloadTests();
                UnityFfmpegDownloadDriverTests.Run(_workRoot);
                FfmpegVideoPipelineTests.Run(_workRoot);
            }
            catch (Exception ex)
            {
                Console.WriteLine("HARNESS ERROR: " + ex);
                return 1;
            }
            finally
            {
                TestKit.TryDeleteDirectory(_workRoot);
            }

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "RESULT: {0} passed, {1} failed, {2} skipped",
                TestKit.Passed, TestKit.Failed, TestKit.Skipped));

            for (int i = 0; i < TestKit.Failures.Count; i++)
                Console.WriteLine("  FAIL " + TestKit.Failures[i]);

            return TestKit.Failed == 0 ? 0 : 1;
        }

        // ============================ manifest ============================

        private static void ManifestTests()
        {
            TestKit.Run("manifest: validates without problems", () =>
            {
                IReadOnlyList<string> problems = FfmpegAssetManifest.Validate();
                TestKit.Check(problems.Count == 0,
                    "manifest problems: " + string.Join("; ", problems.ToArray()));
            });

            TestKit.Run("manifest: primary asset uses pinned https url without 'latest'", () =>
            {
                FfmpegAsset asset = FfmpegAssetManifest.Primary;
                TestKit.CheckNotEmpty(asset.Id, "id");
                TestKit.CheckNotEmpty(asset.Version, "version");
                TestKit.Check(asset.ArchiveUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                    "archive url must be https");
                TestKit.Check(asset.ArchiveUrl.IndexOf("latest", StringComparison.OrdinalIgnoreCase) < 0,
                    "archive url must not be a mutable 'latest' address");
                TestKit.CheckEqual(64, asset.ArchiveSha256.Length, "archive sha256 length");
                TestKit.Check(asset.ArchiveSizeBytes > 0, "archive size must be positive");
            });

            TestKit.Run("manifest: declared executables carry pinned hashes", () =>
            {
                FfmpegAsset asset = FfmpegAssetManifest.Primary;
                TestKit.Check(asset.Files.Count >= 2, "expected at least ffmpeg.exe and ffprobe.exe");

                bool sawFfmpeg = false;
                bool sawFfprobe = false;
                for (int i = 0; i < asset.Files.Count; i++)
                {
                    FfmpegAssetFile file = asset.Files[i];
                    TestKit.Check(file.Sha256 != null && file.Sha256.Length == 64,
                        file.MatchFileName + " must declare a pinned sha256");
                    if (file.MatchFileName == "ffmpeg.exe") sawFfmpeg = true;
                    if (file.MatchFileName == "ffprobe.exe") sawFfprobe = true;
                }

                TestKit.Check(sawFfmpeg, "ffmpeg.exe must be declared");
                TestKit.Check(sawFfprobe, "ffprobe.exe must be declared");
            });

            TestKit.Run("manifest: asset lookup by id", () =>
            {
                FfmpegAsset asset;
                TestKit.Check(FfmpegAssetManifest.TryGet(FfmpegAssetManifest.Primary.Id, out asset),
                    "primary asset must be retrievable by id");
                FfmpegAsset missing;
                TestKit.Check(!FfmpegAssetManifest.TryGet("does-not-exist", out missing),
                    "unknown id must not resolve");
            });

            TestKit.Run("manifest: rejects unsafe relative paths", () =>
            {
                TestKit.Check(!FfmpegAssetManifest.IsSafeRelativePath("../escape.exe"), "must reject ..");
                TestKit.Check(!FfmpegAssetManifest.IsSafeRelativePath("/abs.exe"), "must reject rooted");
                TestKit.Check(!FfmpegAssetManifest.IsSafeRelativePath("C:/drive.exe"), "must reject drive");
                TestKit.Check(!FfmpegAssetManifest.IsSafeRelativePath("a\\b.exe"), "must reject backslash");
                TestKit.Check(!FfmpegAssetManifest.IsSafeRelativePath("a/./b.exe"), "must reject dot segment");
                TestKit.Check(!FfmpegAssetManifest.IsSafeRelativePath(""), "must reject empty");
                TestKit.Check(FfmpegAssetManifest.IsSafeRelativePath("bin/ffmpeg.exe"), "must accept safe path");
            });
        }

        // ============================ layout ============================

        private static void LayoutTests()
        {
            TestKit.Run("layout: rejects empty and relative roots", () =>
            {
                FfmpegInstallLayout layout;
                string error;
                TestKit.Check(!FfmpegInstallLayout.TryCreate("   ", out layout, out error), "empty root must fail");
                TestKit.Check(!FfmpegInstallLayout.TryCreate("relative/path", out layout, out error),
                    "relative root must fail");

                string rooted = Path.Combine(_workRoot, "layout-root");
                TestKit.Check(FfmpegInstallLayout.TryCreate(rooted, out layout, out error),
                    "rooted path must succeed: " + error);
                TestKit.Check(FfmpegInstallLayout.IsUnder(layout.InstallRoot, layout.StagingRoot),
                    "staging must live under install root");
                TestKit.Check(FfmpegInstallLayout.IsUnder(layout.InstallRoot, layout.LockFilePath),
                    "lock file must live under install root");
            });

            TestKit.Run("layout: resolve relative path rejects escapes", () =>
            {
                string baseDir = Path.Combine(_workRoot, "resolve-base");
                Directory.CreateDirectory(baseDir);

                string resolved;
                TestKit.Check(FfmpegInstallLayout.TryResolveRelative(baseDir, "bin/ffmpeg.exe", out resolved),
                    "safe relative path must resolve");
                TestKit.Check(FfmpegInstallLayout.IsUnder(baseDir, resolved), "resolved path must stay under base");

                string escaped;
                TestKit.Check(!FfmpegInstallLayout.TryResolveRelative(baseDir, "../escape.exe", out escaped),
                    "traversal must not resolve");
                TestKit.Check(!FfmpegInstallLayout.TryResolveRelative(baseDir, "a/../../escape.exe", out escaped),
                    "nested traversal must not resolve");
                TestKit.Check(!FfmpegInstallLayout.TryResolveRelative(baseDir, "C:/escape.exe", out escaped),
                    "drive-qualified path must not resolve");
            });
        }

        // ============================ hashing ============================

        private static void HashTests()
        {
            TestKit.Run("hash: matches known sha256 vector", () =>
            {
                string path = Path.Combine(_workRoot, "hash-vector.bin");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));

                string sha256;
                string error;
                TestKit.Check(FfmpegFileHash.TryCompute(path, out sha256, out error), "hash must succeed: " + error);
                TestKit.CheckEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                    sha256, "sha256(\"abc\")");
            });

            TestKit.Run("hash: missing file reports failure", () =>
            {
                string sha256;
                string error;
                TestKit.Check(!FfmpegFileHash.TryCompute(Path.Combine(_workRoot, "nope.bin"), out sha256, out error),
                    "missing file must fail");
                TestKit.CheckNotEmpty(error, "error code");
            });

            TestKit.Run("hash: detects same-size replacement with preserved timestamp", () =>
            {
                string path = Path.Combine(_workRoot, "hash-replacement.bin");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));
                DateTime stamp = File.GetLastWriteTimeUtc(path);

                string before;
                string error;
                TestKit.Check(FfmpegFileHash.TryCompute(path, out before, out error), "first hash: " + error);

                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("xyz"));
                File.SetLastWriteTimeUtc(path, stamp);

                string after;
                TestKit.Check(FfmpegFileHash.TryCompute(path, out after, out error), "second hash: " + error);
                TestKit.CheckEqual(Fixtures.Sha256Of(Encoding.ASCII.GetBytes("xyz")), after,
                    "replacement must not reuse old hash");
            });
        }

        // ============================ discovery ============================

        private static void DiscoveryTests()
        {
            TestKit.Run("discovery: explicit path invalid reports error and does NOT fall through", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "discovery-explicit");
                string pathDir = Path.Combine(work, "pathdir");
                Directory.CreateDirectory(pathDir);
                Fixtures.WriteFile(Path.Combine(pathDir, "ffmpeg.exe"), Fixtures.DeterministicBytes(512, 7));

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                FfmpegDiscoveryResult result = FfmpegDiscovery.Discover(new FfmpegDiscoveryRequest
                {
                    ExplicitPath = Path.Combine(work, "missing-ffmpeg.exe"),
                    Layout = layout,
                    Assets = new FfmpegAsset[0],
                    PathEnvironment = pathDir,
                });

                TestKit.CheckEqual(FfmpegCandidateSource.ExplicitPath, result.Source, "source");
                TestKit.Check(!result.Found, "invalid explicit path must not produce a candidate");
                TestKit.CheckEqual("explicit-path-invalid", result.ErrorCode, "error code");
            });

            TestKit.Run("discovery: explicit path wins and freezes identity", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "discovery-explicit-ok");
                byte[] content = Fixtures.DeterministicBytes(2048, 11);
                string explicitPath = Fixtures.WriteFile(Path.Combine(work, "my-ffmpeg.exe"), content);

                FfmpegDiscoveryResult result = FfmpegDiscovery.Discover(new FfmpegDiscoveryRequest
                {
                    ExplicitPath = explicitPath,
                    PathEnvironment = string.Empty,
                });

                TestKit.Check(result.Found, "explicit path must be discovered");
                TestKit.CheckEqual(FfmpegCandidateSource.ExplicitPath, result.Source, "source");
                TestKit.CheckEqual(Path.GetFullPath(explicitPath), result.Candidate.Identity.AbsolutePath,
                    "absolute path");
                TestKit.CheckEqual(Fixtures.Sha256Of(content), result.Candidate.Identity.Sha256, "frozen sha256");
                TestKit.CheckEqual((long)content.Length, result.Candidate.Identity.SizeBytes, "size");
            });

            TestKit.Run("discovery: managed install takes priority over PATH", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "discovery-managed");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath,
                    assetId: "managed-asset", version: "2.0.0");

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                FfmpegInstallResult install = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                }, CancellationToken.None);
                TestKit.CheckEqual(FfmpegInstallOutcome.Installed, install.Outcome,
                    "seed install: " + install.ErrorCode + " " + install.ErrorDetail);

                string pathDir = Path.Combine(work, "pathdir");
                Directory.CreateDirectory(pathDir);
                Fixtures.WriteFile(Path.Combine(pathDir, "ffmpeg.exe"), Fixtures.DeterministicBytes(256, 3));

                FfmpegDiscoveryResult result = FfmpegDiscovery.Discover(new FfmpegDiscoveryRequest
                {
                    Layout = layout,
                    Assets = new[] { asset },
                    PathEnvironment = pathDir,
                });

                TestKit.Check(result.Found, "managed install must be discovered");
                TestKit.CheckEqual(FfmpegCandidateSource.ManagedInstall, result.Source, "source");
                TestKit.Check(result.Candidate.ManagedInstall != null && result.Candidate.ManagedInstall.IsValid,
                    "managed install must be reported valid");
            });

            TestKit.Run("discovery: system PATH order decides first hit, recorded by index", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "discovery-path");
                string first = Path.Combine(work, "first");
                string second = Path.Combine(work, "second");
                Directory.CreateDirectory(first);
                Directory.CreateDirectory(second);

                byte[] firstContent = Fixtures.DeterministicBytes(128, 21);
                Fixtures.WriteFile(Path.Combine(first, "ffmpeg.exe"), firstContent);
                Fixtures.WriteFile(Path.Combine(second, "ffmpeg.exe"), Fixtures.DeterministicBytes(128, 22));

                FfmpegDiscoveryResult result = FfmpegDiscovery.Discover(new FfmpegDiscoveryRequest
                {
                    PathEnvironment = first + ";" + second,
                });

                TestKit.Check(result.Found, "PATH candidate must be found");
                TestKit.CheckEqual(FfmpegCandidateSource.SystemPath, result.Source, "source");
                TestKit.CheckEqual(Fixtures.Sha256Of(firstContent), result.Candidate.Identity.Sha256,
                    "first PATH entry must win");
            });

            TestKit.Run("discovery: nothing found reports none", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "discovery-none");
                string emptyDir = Path.Combine(work, "empty");
                Directory.CreateDirectory(emptyDir);

                FfmpegDiscoveryResult result = FfmpegDiscovery.Discover(new FfmpegDiscoveryRequest
                {
                    PathEnvironment = emptyDir,
                });

                TestKit.Check(!result.Found, "must not find anything");
                TestKit.CheckEqual(FfmpegCandidateSource.None, result.Source, "source");
                TestKit.CheckEqual("ffmpeg-not-found", result.ErrorCode, "error code");
            });

            TestKit.Run("discovery: invalid managed install falls through to PATH", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "discovery-managed-invalid");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath,
                    assetId: "broken-asset", version: "3.0.0");

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                // 伪造"外来"目录：命中目标路径但没有 ownership 标记。
                Fixtures.CreateForeignInstallDirectory(layout, asset);

                string pathDir = Path.Combine(work, "pathdir");
                Directory.CreateDirectory(pathDir);
                Fixtures.WriteFile(Path.Combine(pathDir, "ffmpeg.exe"), Fixtures.DeterministicBytes(64, 5));

                FfmpegDiscoveryResult result = FfmpegDiscovery.Discover(new FfmpegDiscoveryRequest
                {
                    Layout = layout,
                    Assets = new[] { asset },
                    PathEnvironment = pathDir,
                });

                TestKit.CheckEqual(FfmpegCandidateSource.SystemPath, result.Source,
                    "foreign managed directory must not be treated as a valid install");
            });
        }

        // ============================ capability ============================

        private static void CapabilityTests()
        {
            TestKit.Run("capability: missing executable reports probe failure", () =>
            {
                FfmpegCapabilityReport report = FfmpegCapabilityProbe.Probe(
                    Path.Combine(_workRoot, "definitely-missing-ffmpeg.exe"), 5, CancellationToken.None);

                TestKit.CheckEqual(FfmpegCapabilityStatus.ProbeFailed, report.Status, "status");
                TestKit.CheckNotEmpty(report.ErrorCode, "error code");
            });

            TestKit.Run("capability: non-executable file reports probe failure", () =>
            {
                string path = Fixtures.WriteFile(Path.Combine(_workRoot, "garbage.exe"),
                    Fixtures.DeterministicBytes(4096, 99));

                FfmpegCapabilityReport report = FfmpegCapabilityProbe.Probe(path, 5, CancellationToken.None);

                TestKit.CheckEqual(FfmpegCapabilityStatus.ProbeFailed, report.Status, "status");
            });

            TestKit.Run("capability: real ffmpeg binaries report version and required capabilities", () =>
            {
                IReadOnlyList<string> candidates = FindLocalFfmpegBinaries();
                if (candidates.Count == 0)
                {
                    throw new SkipTestException(
                        "no local FFmpeg fixture; set RENDERIST_TEST_FFMPEG_DIR to a directory containing " +
                        "ffmpeg.exe (binaries are intentionally not committed)");
                }

                for (int i = 0; i < candidates.Count; i++)
                {
                    FfmpegCapabilityReport report = FfmpegCapabilityProbe.Probe(
                        candidates[i], 30, CancellationToken.None);

                    TestKit.CheckEqual(FfmpegCapabilityStatus.Probed, report.Status,
                        candidates[i] + " probe status (" + report.ErrorCode + ")");
                    TestKit.CheckNotEmpty(report.VersionLine, "version line for " + candidates[i]);
                    TestKit.Check(report.HasLibx264, "libx264 expected for " + candidates[i]);
                    TestKit.Check(report.HasMp4Muxer, "mp4 muxer expected for " + candidates[i]);
                    TestKit.Check(report.HasRawvideoDemuxer, "rawvideo demuxer expected for " + candidates[i]);
                    TestKit.Check(report.IsUsableForMp4, "usable for mp4: " + candidates[i]);
                    Console.WriteLine("        " + candidates[i]);
                    Console.WriteLine("          " + report.VersionLine);
                }
            });
        }

        internal static IReadOnlyList<string> FindLocalFfmpegBinaries()
        {
            var found = new List<string>();

            string configured = Environment.GetEnvironmentVariable("RENDERIST_TEST_FFMPEG_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                foreach (string directory in configured.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(directory))
                        continue;
                    string candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate))
                        found.Add(candidate);
                }
            }

            return found;
        }

        // ==================== capability probe (fake ffmpeg) ====================
        //
        // 这些回归针对一个已复现的缺陷：能力探测原先只判 -version 的退出码，
        // -encoders / -formats 的非零退出（stdout 仍可能含目标 token）会被误判为成功。
        // 另外"文本里出现 libx264 / mp4 / rawvideo"本身不足以证明能力存在，
        // 必须来自结构化的表行。

        private static void FakeFfmpegProbeTests()
        {
            TestKit.Run("probe: fake ffmpeg with full capabilities reports usable", () =>
            {
                WithFakeEnvironment(null, () =>
                {
                    FfmpegCapabilityReport report = ProbeFake();
                    TestKit.CheckEqual(FfmpegCapabilityStatus.Probed, report.Status,
                        "status (" + report.ErrorCode + ")");
                    TestKit.Check(report.HasLibx264, "libx264 must be detected from the encoder table");
                    TestKit.Check(report.HasMp4Muxer, "mp4 muxer must be detected");
                    TestKit.Check(report.HasRawvideoDemuxer, "rawvideo demuxer must be detected");
                    TestKit.Check(report.IsUsableForMp4, "must be usable for mp4");
                    TestKit.CheckNotEmpty(report.VersionLine, "version line");
                    TestKit.CheckEqual(true, report.ConfigurationEnablesGpl, "configuration enables gpl");
                    TestKit.CheckEqual(true, report.ConfigurationEnablesLibx264, "configuration enables libx264");
                });
            });

            TestKit.Run("probe: nonzero exit from -encoders is a failure even when stdout lists libx264", () =>
            {
                WithFakeEnvironment(new Dictionary<string, string>
                {
                    { "RENDERIST_FAKE_ENCODERS_EXIT", "3" },
                }, () =>
                {
                    FfmpegCapabilityReport report = ProbeFake();
                    TestKit.CheckEqual(FfmpegCapabilityStatus.ProbeFailed, report.Status,
                        "nonzero -encoders exit must fail the probe");
                    TestKit.Check(!report.IsUsableForMp4, "must not be usable for mp4");
                    TestKit.CheckEqual("encoders-probe-nonzero-exit", report.ErrorCode, "error code");
                });
            });

            TestKit.Run("probe: nonzero exit from -formats is a failure even when stdout lists mp4 and rawvideo", () =>
            {
                WithFakeEnvironment(new Dictionary<string, string>
                {
                    { "RENDERIST_FAKE_FORMATS_EXIT", "7" },
                }, () =>
                {
                    FfmpegCapabilityReport report = ProbeFake();
                    TestKit.CheckEqual(FfmpegCapabilityStatus.ProbeFailed, report.Status,
                        "nonzero -formats exit must fail the probe");
                    TestKit.Check(!report.IsUsableForMp4, "must not be usable for mp4");
                    TestKit.CheckEqual("formats-probe-nonzero-exit", report.ErrorCode, "error code");
                });
            });

            TestKit.Run("probe: nonzero exit from -version is still a failure", () =>
            {
                WithFakeEnvironment(new Dictionary<string, string>
                {
                    { "RENDERIST_FAKE_VERSION_EXIT", "2" },
                }, () =>
                {
                    FfmpegCapabilityReport report = ProbeFake();
                    TestKit.CheckEqual(FfmpegCapabilityStatus.ProbeFailed, report.Status, "status");
                    TestKit.CheckEqual("version-probe-nonzero-exit", report.ErrorCode, "error code");
                });
            });

            TestKit.Run("probe: missing libx264 is a missing capability, not Ready", () =>
            {
                WithFakeEnvironment(new Dictionary<string, string>
                {
                    { "RENDERIST_FAKE_ENCODERS_OMIT_LIBX264", "1" },
                }, () =>
                {
                    FfmpegCapabilityReport report = ProbeFake();
                    TestKit.CheckEqual(FfmpegCapabilityStatus.Probed, report.Status, "status");
                    TestKit.Check(!report.HasLibx264, "libx264 must not be reported present");
                    TestKit.Check(!report.IsUsableForMp4, "must not be usable for mp4");
                    TestKit.Check(ContainsCapability(report, "encoder:libx264"),
                        "missing capability must list encoder:libx264");
                });
            });

            TestKit.Run("probe: libx264 mentioned outside the encoder table does not count", () =>
            {
                WithFakeEnvironment(new Dictionary<string, string>
                {
                    { "RENDERIST_FAKE_ENCODERS_NOISE", "1" },
                }, () =>
                {
                    FfmpegCapabilityReport report = ProbeFake();
                    TestKit.CheckEqual(FfmpegCapabilityStatus.Probed, report.Status, "status");
                    TestKit.Check(!report.HasLibx264,
                        "a passing mention of libx264 must not be treated as the encoder being present");
                });
            });

            TestKit.Run("probe: mp4/rawvideo mentioned outside the formats table do not count", () =>
            {
                WithFakeEnvironment(new Dictionary<string, string>
                {
                    { "RENDERIST_FAKE_FORMATS_NOISE", "1" },
                }, () =>
                {
                    FfmpegCapabilityReport report = ProbeFake();
                    TestKit.CheckEqual(FfmpegCapabilityStatus.Probed, report.Status, "status");
                    TestKit.Check(!report.HasMp4Muxer, "mp4 must come from a muxing format row");
                    TestKit.Check(!report.HasRawvideoDemuxer, "rawvideo must come from a demuxing format row");
                    TestKit.Check(!report.IsUsableForMp4, "must not be usable for mp4");
                });
            });
        }

        private static FfmpegCapabilityReport ProbeFake()
        {
            return FfmpegCapabilityProbe.Probe(FakeFfmpeg.ExecutablePath, 30, CancellationToken.None);
        }

        private static bool ContainsCapability(FfmpegCapabilityReport report, string name)
        {
            if (report.MissingCapabilities == null)
                return false;

            for (int i = 0; i < report.MissingCapabilities.Count; i++)
            {
                if (string.Equals(report.MissingCapabilities[i], name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>在受控环境变量下运行一段代码，结束后恢复原值。</summary>
        private static void WithFakeEnvironment(Dictionary<string, string> variables, Action body)
        {
            string[] names =
            {
                "RENDERIST_FAKE_VERSION_EXIT",
                "RENDERIST_FAKE_ENCODERS_EXIT",
                "RENDERIST_FAKE_FORMATS_EXIT",
                "RENDERIST_FAKE_ENCODERS_OMIT_LIBX264",
                "RENDERIST_FAKE_ENCODERS_NOISE",
                "RENDERIST_FAKE_FORMATS_NOISE",
            };

            var previous = new Dictionary<string, string>();
            for (int i = 0; i < names.Length; i++)
            {
                previous[names[i]] = Environment.GetEnvironmentVariable(names[i]);
                Environment.SetEnvironmentVariable(names[i], null);
            }

            if (variables != null)
            {
                foreach (KeyValuePair<string, string> pair in variables)
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            try
            {
                body();
            }
            finally
            {
                for (int i = 0; i < names.Length; i++)
                    Environment.SetEnvironmentVariable(names[i], previous[names[i]]);
            }
        }

        // ============================ safe zip ============================

        private static void SafeZipTests()
        {
            TestKit.Run("zip: extracts whitelisted entries and verifies hashes", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-ok");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);
                string destination = Path.Combine(work, "out");

                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, destination, Fixtures.RulesFor(asset), 64L * 1024 * 1024, 64L * 1024 * 1024,
                    CancellationToken.None);

                TestKit.Check(result.Success, "extraction must succeed: " + result.ErrorCode + " " + result.ErrorDetail);
                TestKit.Check(File.Exists(Path.Combine(destination, "bin", "ffmpeg.exe")), "ffmpeg.exe extracted");
                TestKit.Check(File.Exists(Path.Combine(destination, "bin", "ffprobe.exe")), "ffprobe.exe extracted");

                // 默认拒绝：白名单外的条目绝不写出。
                TestKit.Check(!File.Exists(Path.Combine(destination, "LICENSE")), "LICENSE is not whitelisted");
                TestKit.Check(!Directory.Exists(Path.Combine(destination, "doc")), "doc/ must not be extracted");
            });

            TestKit.Run("zip: rejects path traversal entries", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-traversal");
                string archivePath = Path.Combine(work, "evil.zip");
                Fixtures.BuildZip(archivePath, new[]
                {
                    ZipEntrySpec.File("bin/ffmpeg.exe", Fixtures.DeterministicBytes(128, 31)),
                    ZipEntrySpec.Text("../escaped.txt", "escaped"),
                });

                string destination = Path.Combine(work, "out");
                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, destination, SingleRule("ffmpeg.exe", "bin/ffmpeg.exe"), 0, 0,
                    CancellationToken.None);

                TestKit.Check(!result.Success, "traversal archive must be rejected");
                TestKit.CheckEqual("unsafe-entry", result.ErrorCode, "error code");
            });

            TestKit.Run("zip: rejects rooted and drive-qualified entries", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-rooted");
                string destination = Path.Combine(work, "out");

                string rootedZip = Path.Combine(work, "rooted.zip");
                Fixtures.BuildZip(rootedZip, new[]
                {
                    ZipEntrySpec.File("bin/ffmpeg.exe", Fixtures.DeterministicBytes(128, 41)),
                    ZipEntrySpec.Text("/absolute.txt", "abs"),
                });
                SafeZipExtractionResult rooted = SafeZipExtractor.Extract(
                    rootedZip, destination, SingleRule("ffmpeg.exe", "bin/ffmpeg.exe"), 0, 0, CancellationToken.None);
                TestKit.Check(!rooted.Success, "rooted entry must be rejected");

                string driveZip = Path.Combine(work, "drive.zip");
                Fixtures.BuildZip(driveZip, new[]
                {
                    ZipEntrySpec.File("bin/ffmpeg.exe", Fixtures.DeterministicBytes(128, 42)),
                    ZipEntrySpec.Text("C:/drive.txt", "drive"),
                });
                SafeZipExtractionResult drive = SafeZipExtractor.Extract(
                    driveZip, Path.Combine(work, "out2"), SingleRule("ffmpeg.exe", "bin/ffmpeg.exe"),
                    0, 0, CancellationToken.None);
                TestKit.Check(!drive.Success, "drive-qualified entry must be rejected");
            });

            TestKit.Run("zip: rejects case-insensitive duplicate entries", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-dup");
                string archivePath = Path.Combine(work, "dup.zip");
                Fixtures.BuildZip(archivePath, new[]
                {
                    ZipEntrySpec.File("pkg/bin/ffmpeg.exe", Fixtures.DeterministicBytes(128, 51)),
                    ZipEntrySpec.File("pkg/bin/FFMPEG.EXE", Fixtures.DeterministicBytes(128, 52)),
                });

                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, Path.Combine(work, "out"), SingleRule("ffmpeg.exe", "bin/ffmpeg.exe"),
                    0, 0, CancellationToken.None);

                TestKit.Check(!result.Success, "case-duplicate archive must be rejected");
                TestKit.CheckEqual("duplicate-entry", result.ErrorCode, "error code");
            });

            TestKit.Run("zip: rejects ambiguous rule match", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-ambiguous");
                string archivePath = Path.Combine(work, "ambiguous.zip");
                Fixtures.BuildZip(archivePath, new[]
                {
                    ZipEntrySpec.File("a/ffmpeg.exe", Fixtures.DeterministicBytes(128, 61)),
                    ZipEntrySpec.File("b/ffmpeg.exe", Fixtures.DeterministicBytes(128, 62)),
                });

                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, Path.Combine(work, "out"), SingleRule("ffmpeg.exe", "bin/ffmpeg.exe"),
                    0, 0, CancellationToken.None);

                TestKit.Check(!result.Success, "ambiguous match must be rejected");
                TestKit.CheckEqual("ambiguous-rule-match", result.ErrorCode, "error code");
            });

            TestKit.Run("zip: rejects missing required entry", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-missing");
                string archivePath = Path.Combine(work, "missing.zip");
                Fixtures.BuildZip(archivePath, new[]
                {
                    ZipEntrySpec.File("pkg/bin/ffprobe.exe", Fixtures.DeterministicBytes(128, 71)),
                });

                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, Path.Combine(work, "out"), SingleRule("ffmpeg.exe", "bin/ffmpeg.exe"),
                    0, 0, CancellationToken.None);

                TestKit.Check(!result.Success, "missing required entry must be rejected");
                TestKit.CheckEqual("required-entry-missing", result.ErrorCode, "error code");
            });

            TestKit.Run("zip: rejects content hash mismatch", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-hash");
                string archivePath = Path.Combine(work, "hash.zip");
                Fixtures.BuildZip(archivePath, new[]
                {
                    ZipEntrySpec.File("pkg/bin/ffmpeg.exe", Fixtures.DeterministicBytes(256, 81)),
                });

                var rules = new[]
                {
                    new SafeZipEntryRule("ffmpeg.exe", "bin/ffmpeg.exe", new string('0', 64), true),
                };

                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, Path.Combine(work, "out"), rules, 0, 0, CancellationToken.None);

                TestKit.Check(!result.Success, "hash mismatch must be rejected");
                TestKit.CheckEqual("hash-mismatch", result.ErrorCode, "error code");
            });

            TestKit.Run("zip: rejects corrupt archive", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-corrupt");
                string archivePath = Fixtures.WriteFile(Path.Combine(work, "corrupt.zip"),
                    Fixtures.DeterministicBytes(4096, 91));

                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, Path.Combine(work, "out"), SingleRule("ffmpeg.exe", "bin/ffmpeg.exe"),
                    0, 0, CancellationToken.None);

                TestKit.Check(!result.Success, "corrupt archive must be rejected");
            });

            TestKit.Run("zip: rejects symbolic link entry", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-symlink");
                string archivePath = Path.Combine(work, "symlink.zip");

                var entry = ZipEntrySpec.File("pkg/bin/ffmpeg.exe", Fixtures.DeterministicBytes(128, 101));
                // Unix S_IFLNK (0xA000) 位于外部属性的高 16 位。
                entry.ExternalAttributes = unchecked((int)0xA1FF0000);

                try
                {
                    Fixtures.BuildZip(archivePath, new[] { entry });
                }
                catch (Exception ex)
                {
                    throw new SkipTestException("cannot craft symlink entry on this runtime: " + ex.Message);
                }

                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, Path.Combine(work, "out"), SingleRule("ffmpeg.exe", "bin/ffmpeg.exe"),
                    0, 0, CancellationToken.None);

                TestKit.Check(!result.Success, "symlink entry must be rejected");
                TestKit.CheckEqual("unsafe-entry", result.ErrorCode, "error code");
            });

            TestKit.Run("zip: pre-cancelled token reports cancellation", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-cancel");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    SafeZipExtractionResult result = SafeZipExtractor.Extract(
                        archivePath, Path.Combine(work, "out"), Fixtures.RulesFor(asset), 0, 0, cancellation.Token);

                    TestKit.Check(!result.Success, "cancelled extraction must not succeed");
                    TestKit.CheckEqual("cancelled", result.ErrorCode, "error code");
                }
            });

            TestKit.Run("zip: entry size bound is enforced", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-bound");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath,
                    ffmpegSize: 2 * 1024 * 1024);

                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, Path.Combine(work, "out"), Fixtures.RulesFor(asset),
                    1024, 1024, CancellationToken.None);

                TestKit.Check(!result.Success, "oversized entry must be rejected");
                TestKit.CheckEqual("entry-too-large", result.ErrorCode, "error code");
            });

            TestKit.Run("zip: actual total extraction is bounded", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "zip-actual-total");
                string archivePath = Path.Combine(work, "misstated.zip");
                Fixtures.BuildZip(archivePath, new[]
                {
                    ZipEntrySpec.File("pkg/ffmpeg.exe", Fixtures.DeterministicBytes(2048, 131)),
                    ZipEntrySpec.File("pkg/ffprobe.exe", Fixtures.DeterministicBytes(2048, 132)),
                });

                byte[] bytes = File.ReadAllBytes(archivePath);
                int changed = 0;
                for (int i = 0; i + 46 < bytes.Length; i++)
                {
                    if (bytes[i] != 0x50 || bytes[i + 1] != 0x4b ||
                        bytes[i + 2] != 0x01 || bytes[i + 3] != 0x02)
                        continue;
                    byte[] smaller = BitConverter.GetBytes(1024);
                    Buffer.BlockCopy(smaller, 0, bytes, i + 24, 4);
                    changed++;
                    i += 45;
                }
                TestKit.CheckEqual(2, changed, "central directory entries modified");
                File.WriteAllBytes(archivePath, bytes);

                var rules = new[]
                {
                    new SafeZipEntryRule("ffmpeg.exe", "bin/ffmpeg.exe", null, true),
                    new SafeZipEntryRule("ffprobe.exe", "bin/ffprobe.exe", null, true),
                };
                SafeZipExtractionResult result = SafeZipExtractor.Extract(
                    archivePath, Path.Combine(work, "out"), rules, 3000, 3000, CancellationToken.None);
                TestKit.Check(!result.Success, "actual total above limit must be rejected");
            });
        }

        private static IReadOnlyList<SafeZipEntryRule> SingleRule(
            string matchFileName, string targetRelativePath)
        {
            return new[] { new SafeZipEntryRule(matchFileName, targetRelativePath, null, true) };
        }

        // ============================ install lock ============================

        private static void InstallLockTests()
        {
            TestKit.Run("lock: second acquisition fails while held, succeeds after release", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "lock");
                string lockPath = Path.Combine(work, ".install.lock");

                FfmpegInstallLock first;
                string code;
                string detail;
                TestKit.Check(FfmpegInstallLock.TryAcquire(lockPath, out first, out code, out detail),
                    "first acquisition must succeed: " + code);

                using (first)
                {
                    FfmpegInstallLock second;
                    string secondCode;
                    string secondDetail;
                    TestKit.Check(!FfmpegInstallLock.TryAcquire(lockPath, out second, out secondCode, out secondDetail),
                        "second concurrent acquisition must fail");
                    TestKit.CheckEqual("install-lock-held", secondCode, "error code");
                }

                FfmpegInstallLock third;
                string thirdCode;
                string thirdDetail;
                TestKit.Check(FfmpegInstallLock.TryAcquire(lockPath, out third, out thirdCode, out thirdDetail),
                    "acquisition after release must succeed: " + thirdCode);
                third.Dispose();
            });
        }

        // ============================ installer ============================

        private static void InstallerTests()
        {
            TestKit.Run("install: failed capability probe does not publish", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-probe-fail");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);
                FfmpegInstallLayout layout;
                string layoutError;
                TestKit.Check(FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"),
                    out layout, out layoutError), "layout: " + layoutError);

                FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                    ProbeAfterInstall = true,
                    ProbeTimeoutSeconds = 5,
                }, CancellationToken.None);

                TestKit.CheckEqual(FfmpegInstallOutcome.Failed, result.Outcome, "outcome");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "unusable executable must not be published");
                TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout), "no staging leftovers");
            });

            TestKit.Run("install: happy path publishes version directory", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-ok");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                }, CancellationToken.None);

                TestKit.CheckEqual(FfmpegInstallOutcome.Installed, result.Outcome,
                    "outcome (" + result.ErrorCode + " " + result.ErrorDetail + ")");
                TestKit.CheckNotEmpty(result.TargetDirectory, "target directory");
                TestKit.Check(Directory.Exists(result.TargetDirectory), "version directory must exist");

                string primary = Path.Combine(result.TargetDirectory, "bin", "ffmpeg.exe");
                TestKit.Check(File.Exists(primary), "published ffmpeg.exe must exist");
                TestKit.Check(File.Exists(layout.OwnershipMarkerPath(result.TargetDirectory)),
                    "ownership marker must be published");
                TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout), "no staging leftovers");

                FfmpegManagedInstall located = FfmpegManagedInstallLocator.Locate(layout, asset);
                TestKit.Check(located.IsValid, "published install must validate: " + located.InvalidReason);
            });

            TestKit.Run("install: existing valid install is never overwritten", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-existing");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var request = new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                };

                FfmpegInstallResult first = FfmpegInstaller.Install(request, CancellationToken.None);
                TestKit.CheckEqual(FfmpegInstallOutcome.Installed, first.Outcome, "first install");

                string primary = Path.Combine(first.TargetDirectory, "bin", "ffmpeg.exe");
                DateTime stamp = File.GetLastWriteTimeUtc(primary);

                FfmpegInstallResult second = FfmpegInstaller.Install(request, CancellationToken.None);
                TestKit.CheckEqual(FfmpegInstallOutcome.AlreadyInstalled, second.Outcome, "second install outcome");
                TestKit.CheckEqual(stamp, File.GetLastWriteTimeUtc(primary), "existing file must be untouched");
            });

            TestKit.Run("install: foreign directory at target fails and is preserved", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-foreign");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                string foreign = Fixtures.CreateForeignInstallDirectory(layout, asset);
                string foreignFile = Path.Combine(foreign, "unrelated.txt");

                FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                }, CancellationToken.None);

                TestKit.CheckEqual(FfmpegInstallOutcome.Failed, result.Outcome, "outcome");
                TestKit.CheckEqual("managed-install-invalid", result.ErrorCode, "error code");
                TestKit.Check(Directory.Exists(foreign), "foreign directory must be preserved");
                TestKit.Check(File.Exists(foreignFile), "foreign file must be preserved");
            });

            TestKit.Run("install: tampered archive fails hash check and cleans staging", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-tampered");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                // 归档在构建后被替换：清单里的哈希不再匹配。
                File.Delete(archivePath);
                Fixtures.BuildZip(archivePath, new[]
                {
                    ZipEntrySpec.File("pkg/bin/ffmpeg.exe", Fixtures.DeterministicBytes(128, 111)),
                    ZipEntrySpec.File("pkg/bin/ffprobe.exe", Fixtures.DeterministicBytes(128, 112)),
                });

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                }, CancellationToken.None);

                TestKit.CheckEqual(FfmpegInstallOutcome.Failed, result.Outcome, "outcome");
                TestKit.CheckEqual("archive-hash-mismatch", result.ErrorCode, "error code");
                TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout), "staging must be cleaned");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "no version directory must be published");
            });

            TestKit.Run("install: extraction failure cleans staging and publishes nothing", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-badzip");

                // 归档哈希匹配，但内容缺少必需的 ffprobe.exe → 解压阶段失败。
                string missingZip = Path.Combine(work, "missing.zip");
                Fixtures.BuildZip(missingZip, new[]
                {
                    ZipEntrySpec.File("pkg/bin/ffmpeg.exe", Fixtures.DeterministicBytes(256, 123)),
                });

                string missingSha;
                string hashError;
                TestKit.Check(FfmpegFileHash.TryCompute(missingZip, out missingSha, out hashError),
                    "hash missing zip: " + hashError);

                var strictAsset = new FfmpegAsset(
                    "strict-asset", "1.0.0", "strict test asset",
                    "https://example.invalid/strict.zip", missingSha, new FileInfo(missingZip).Length,
                    "GPLv3", "https://example.invalid/license", "https://example.invalid/source", "synthetic",
                    "bin/ffmpeg.exe",
                    new[]
                    {
                        new FfmpegAssetFile("ffmpeg.exe", "bin/ffmpeg.exe", null, true),
                        new FfmpegAssetFile("ffprobe.exe", "bin/ffprobe.exe", null, true),
                    });

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = strictAsset,
                    Layout = layout,
                    ArchivePath = missingZip,
                }, CancellationToken.None);

                TestKit.CheckEqual(FfmpegInstallOutcome.Failed, result.Outcome, "outcome");
                TestKit.CheckEqual("required-entry-missing", result.ErrorCode, "error code");
                TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout), "staging must be cleaned");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(strictAsset.Id, strictAsset.Version)),
                    "no version directory must be published");
            });

            TestKit.Run("install: pre-cancelled request reports cancellation without artifacts", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-cancel");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();

                    FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                    {
                        Asset = asset,
                        Layout = layout,
                        ArchivePath = archivePath,
                    }, cancellation.Token);

                    TestKit.CheckEqual(FfmpegInstallOutcome.Cancelled, result.Outcome, "outcome");
                    TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout), "staging must be cleaned");
                    TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                        "no version directory must be published");
                }
            });

            TestKit.Run("install: cancellation during install leaves no staging behind", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-cancel-mid");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath,
                    ffmpegSize: 8 * 1024 * 1024, ffprobeSize: 4 * 1024 * 1024);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var request = new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                };

                using (var cancellation = new CancellationTokenSource())
                {
                    Task<FfmpegInstallResult> task = Task.Run(
                        () => FfmpegInstaller.Install(request, cancellation.Token));

                    // 等待暂存目录出现后再取消：这样取消一定落在"已开始安装"之后。
                    bool sawStaging = false;
                    for (int i = 0; i < 3000 && !task.IsCompleted; i++)
                    {
                        if (Fixtures.CountStagingDirectories(layout) > 0)
                        {
                            sawStaging = true;
                            cancellation.Cancel();
                            break;
                        }
                        Thread.Sleep(1);
                    }

                    if (!sawStaging && !task.IsCompleted)
                        cancellation.Cancel();

                    TestKit.Check(task.Wait(120000), "install task must complete");

                    FfmpegInstallResult result = task.Result;
                    TestKit.Check(
                        result.Outcome == FfmpegInstallOutcome.Cancelled ||
                        result.Outcome == FfmpegInstallOutcome.Installed,
                        "outcome must be Cancelled or Installed, got " + result.Outcome + " (" + result.ErrorCode + ")");

                    // 核心契约：无论结果如何，都不允许留下暂存目录。
                    TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout),
                        "no staging leftovers after cancellation");
                }
            });

            TestKit.Run("install: cleans own orphan staging", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-orphan");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                string orphan = Fixtures.CreateOwnOrphanStaging(layout);
                TestKit.Check(Directory.Exists(orphan), "orphan fixture must exist");

                FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                }, CancellationToken.None);

                TestKit.CheckEqual(FfmpegInstallOutcome.Installed, result.Outcome, "install outcome");
                TestKit.Check(!Directory.Exists(orphan), "own orphan staging must be removed");
                TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout), "no staging leftovers");
            });

            TestKit.Run("install: preserves foreign staging without ownership marker", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-foreign-staging");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                string foreign = Fixtures.CreateForeignStagingDirectory(layout);

                FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                }, CancellationToken.None);

                TestKit.CheckEqual(FfmpegInstallOutcome.Installed, result.Outcome, "install outcome");
                TestKit.Check(Directory.Exists(foreign), "foreign staging must never be deleted");
                TestKit.Check(File.Exists(Path.Combine(foreign, "someone-elses.bin")),
                    "foreign staging content must be preserved");
            });

            TestKit.Run("install: missing archive fails without artifacts", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "install-missing-archive");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                FfmpegInstallResult result = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = Path.Combine(work, "nope.zip"),
                }, CancellationToken.None);

                TestKit.CheckEqual(FfmpegInstallOutcome.Failed, result.Outcome, "outcome");
                TestKit.CheckEqual("archive-not-found", result.ErrorCode, "error code");
                TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout), "no staging");
            });

            TestKit.Run("component: unowned directory at target is reported invalid and never used", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "component-inspector");

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                // 组件检查只识别 manifest 中的资产；这里在真实资产的版本目录位置伪造
                // 一个"外来"目录（无 ownership 标记）。
                FfmpegAsset primary = FfmpegAssetManifest.Primary;
                Fixtures.CreateForeignInstallDirectory(layout, primary);

                FfmpegComponentReport report = FfmpegComponentInspector.Inspect(
                    new FfmpegComponentInspectionRequest
                    {
                        InstallRoot = layout.InstallRoot,
                        ProbeCapabilities = false,
                        PathEnvironment = string.Empty,
                    });

                TestKit.Check(!report.Found, "unowned directory must never be used as a component");

                bool sawInvalid = false;
                for (int i = 0; i < report.ManagedInstalls.Count; i++)
                {
                    FfmpegManagedInstall install = report.ManagedInstalls[i];
                    if (install.AssetId != primary.Id)
                        continue;

                    sawInvalid = true;
                    TestKit.Check(install.DirectoryPresent, "directory must be reported present");
                    TestKit.Check(!install.IsValid, "unowned directory must not be valid");
                    TestKit.CheckEqual("ownership-marker-missing", install.InvalidReason, "invalid reason");
                }

                TestKit.Check(sawInvalid, "manifest asset must be listed in the component report");
                TestKit.CheckEqual(FfmpegComponentState.NotFound, report.State, "state");
            });

            TestKit.Run("component: valid managed install is discovered for a manifest asset", () =>
            {
                // 组件检查按 manifest 资产定位托管安装。用一个临时布局验证：
                // 合法的托管安装必须被发现并冻结身份。
                string work = TestKit.NewWorkDirectory(_workRoot, "component-inspector-valid");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                FfmpegInstallResult install = FfmpegInstaller.Install(new FfmpegInstallRequest
                {
                    Asset = asset,
                    Layout = layout,
                    ArchivePath = archivePath,
                }, CancellationToken.None);
                TestKit.CheckEqual(FfmpegInstallOutcome.Installed, install.Outcome, "seed install");

                // 直接对合成资产做定位：等价于组件检查对 manifest 资产所做的校验。
                FfmpegManagedInstall located = FfmpegManagedInstallLocator.Locate(layout, asset);
                TestKit.Check(located.IsValid, "install must validate: " + located.InvalidReason);
                TestKit.CheckNotEmpty(located.PrimaryExecutablePath, "primary executable path");

                FfmpegManagedInstall missing = FfmpegManagedInstallLocator.Locate(
                    layout, FfmpegAssetManifest.Primary);
                TestKit.Check(!missing.IsValid, "unrelated asset must not resolve to this install");
            });

            TestKit.Run("install: component inspector reports explicit path failure", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "component-inspector-invalid");

                FfmpegComponentReport report = FfmpegComponentInspector.Inspect(
                    new FfmpegComponentInspectionRequest
                    {
                        ExplicitPath = Path.Combine(work, "missing.exe"),
                        InstallRoot = Path.Combine(work, "managed"),
                        ProbeCapabilities = false,
                        PathEnvironment = string.Empty,
                    });

                TestKit.Check(!report.Found, "must not find a component");
                TestKit.CheckEqual(FfmpegComponentState.Invalid, report.State, "state");
            });
        }

        // ============================ download pipeline ============================
        //
        // 说明：UnityWebRequest 本身无法在 net48 中真实执行，因此这里测试的是
        // Unity-free 的 FfmpegDownloadController：generation 隔离、临时文件 ownership、
        // 长度/哈希复核、后台安装与取消语义。Unity 侧驱动只把结果整理成
        // FfmpegDownloadResponse 交给控制器 —— 这些测试正是模拟该契约。
        // UnityWebRequest 的真实 TLS / 重定向 / 进度 / Abort 行为**必须**留到游戏内验证。

        private static void DownloadTests()
        {
            TestKit.Run("download: success verifies length and hash, installs, and cleans temp", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-ok");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAssetWithFakeFfmpeg(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var aborts = new List<long>();
                var controller = new FfmpegDownloadController(layout, generation => aborts.Add(generation));

                FfmpegDownloadPlan plan = controller.TryStart(asset, true, 30);
                TestKit.Check(plan.Started, "download must start: " + plan.ErrorCode);
                TestKit.CheckEqual(FfmpegDownloadState.Downloading, controller.State, "state after start");

                File.Copy(archivePath, plan.TempFilePath, true);
                TestKit.Check(controller.ReportFinished(plan.Generation, SuccessResponse(plan)), "must accept own generation");
                TestKit.CheckEqual(FfmpegDownloadState.Verifying, controller.State, "state after finish");

                PumpUntilSettled(controller);

                TestKit.CheckEqual(FfmpegDownloadState.Succeeded, controller.State,
                    "must succeed (" + controller.ErrorCode + " " + controller.ErrorDetail + ")");
                TestKit.Check(Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "version directory must be published");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp archive must be deleted by the controller");
                TestKit.Check(controller.Capability != null && controller.Capability.IsUsableForMp4,
                    "capability must be reported usable");
            });

            TestKit.Run("download: duplicate request while busy is rejected", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-busy");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan first = controller.TryStart(asset, false, 5);
                TestKit.Check(first.Started, "first request");

                FfmpegDownloadPlan second = controller.TryStart(asset, false, 5);
                TestKit.Check(!second.Started, "second request must be rejected");
                TestKit.CheckEqual("busy", second.ErrorCode, "error code");

                controller.Cancel("test-cleanup");
            });

            TestKit.Run("download: transport failure fails and cleans temp", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-transport-fail");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);
                File.Copy(archivePath, plan.TempFilePath, true);

                controller.ReportFinished(plan.Generation, new FfmpegDownloadResponse
                {
                    ResponseCode = 0,
                    RequestSucceeded = false,
                    Error = "connection closed before completion",
                });

                TestKit.CheckEqual(FfmpegDownloadState.Failed, controller.State, "state");
                TestKit.CheckEqual("download-request-failed", controller.ErrorCode, "error code");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "nothing may be published");
            });

            TestKit.Run("download: http error response fails and cleans temp", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-http-fail");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);
                File.Copy(archivePath, plan.TempFilePath, true);

                controller.ReportFinished(plan.Generation, new FfmpegDownloadResponse
                {
                    ResponseCode = 404,
                    RequestSucceeded = true,
                    FinalUrl = plan.Url,
                });

                TestKit.CheckEqual(FfmpegDownloadState.Failed, controller.State, "state");
                TestKit.CheckEqual("download-http-error", controller.ErrorCode, "error code");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned");
            });

            TestKit.Run("download: https redirect accepted, non-https redirect rejected", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-redirect");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                // 非 HTTPS 重定向必须被拒绝（防降级）。
                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);
                File.Copy(archivePath, plan.TempFilePath, true);

                controller.ReportFinished(plan.Generation, new FfmpegDownloadResponse
                {
                    ResponseCode = 200,
                    RequestSucceeded = true,
                    FinalUrl = "http://insecure.invalid/asset.zip",
                });

                TestKit.CheckEqual(FfmpegDownloadState.Failed, controller.State, "insecure redirect must fail");
                TestKit.CheckEqual("download-insecure-redirect", controller.ErrorCode, "error code");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned");

                // HTTPS 重定向正常通过响应校验（后续由哈希决定成败）。
                var controller2 = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan2 = controller2.TryStart(asset, false, 5);
                File.Copy(archivePath, plan2.TempFilePath, true);
                TestKit.Check(controller2.ReportFinished(plan2.Generation, new FfmpegDownloadResponse
                {
                    ResponseCode = 200,
                    RequestSucceeded = true,
                    FinalUrl = "https://objects.githubusercontent.com/asset.zip",
                }), "https redirect must be accepted");
                PumpUntilSettled(controller2);
                TestKit.CheckEqual(FfmpegDownloadState.Succeeded, controller2.State,
                    "https redirect must reach install (" + controller2.ErrorCode + ")");
            });

            TestKit.Run("download: byte count mismatch fails and cleans temp", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-size");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);

                // 写入一个长度明确不同的文件。
                File.WriteAllBytes(plan.TempFilePath, Fixtures.DeterministicBytes(128, 777));

                controller.ReportFinished(plan.Generation, SuccessResponse(plan));
                PumpUntilSettled(controller);

                TestKit.CheckEqual(FfmpegDownloadState.Failed, controller.State, "state");
                TestKit.CheckEqual("download-size-mismatch", controller.ErrorCode, "error code");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "nothing may be published");
            });

            TestKit.Run("download: sha256 mismatch fails and cleans temp", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-hash");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);

                // 长度相同但内容被篡改。
                byte[] original = File.ReadAllBytes(archivePath);
                byte[] tampered = (byte[])original.Clone();
                tampered[tampered.Length / 2] ^= 0xFF;
                File.WriteAllBytes(plan.TempFilePath, tampered);

                controller.ReportFinished(plan.Generation, SuccessResponse(plan));
                PumpUntilSettled(controller);

                TestKit.CheckEqual(FfmpegDownloadState.Failed, controller.State, "state");
                TestKit.CheckEqual("download-hash-mismatch", controller.ErrorCode, "error code");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "nothing may be published");
            });

            TestKit.Run("download: cancel aborts, cleans temp, and ignores the late completion", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-cancel");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var aborts = new List<long>();
                var controller = new FfmpegDownloadController(layout, generation => aborts.Add(generation));

                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);
                File.Copy(archivePath, plan.TempFilePath, true);

                controller.Cancel("user-cancel");

                TestKit.CheckEqual(FfmpegDownloadState.Cancelled, controller.State, "state");
                TestKit.Check(aborts.Count == 1, "abort must be requested exactly once");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned synchronously");

                // 取消后迟到的完成通知：必须被忽略，且不得把会话变成成功。
                bool accepted = controller.ReportFinished(plan.Generation, SuccessResponse(plan));
                TestKit.Check(!accepted, "late completion must be rejected");
                TestKit.CheckEqual(FfmpegDownloadState.Cancelled, controller.State,
                    "late completion must not overwrite the cancelled state");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "late completion must not publish anything");
            });

            TestKit.Run("download: old generation notification does not affect a newer task", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-generation");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAssetWithFakeFfmpeg(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });

                // 第一代：启动后立即废弃。
                FfmpegDownloadPlan first = controller.TryStart(asset, false, 5);
                File.Copy(archivePath, first.TempFilePath, true);
                controller.Invalidate("superseded", true);
                TestKit.Check(!File.Exists(first.TempFilePath), "abandoned generation temp must be cleaned");

                // 第二代：正常完成。
                FfmpegDownloadPlan second = controller.TryStart(asset, false, 5);
                TestKit.Check(second.Started, "second generation must start");
                TestKit.Check(second.Generation > first.Generation, "generation must advance");
                File.Copy(archivePath, second.TempFilePath, true);

                // 第一代的迟到通知到达：必须被拒绝，且不得影响第二代。
                bool accepted = controller.ReportFinished(first.Generation, SuccessResponse(first));
                TestKit.Check(!accepted, "old generation completion must be rejected");
                TestKit.CheckEqual(FfmpegDownloadState.Downloading, controller.State,
                    "new task state must be untouched");
                TestKit.Check(File.Exists(second.TempFilePath), "new task temp file must be untouched");

                TestKit.Check(controller.ReportFinished(second.Generation, SuccessResponse(second)),
                    "new generation completion must be accepted");
                PumpUntilSettled(controller);
                TestKit.CheckEqual(FfmpegDownloadState.Succeeded, controller.State,
                    "new generation must still succeed (" + controller.ErrorCode + ")");
            });

            TestKit.Run("download: install failure after successful download leaves no publish", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-install-fail");

                // 归档哈希与清单一致，但缺少必需的 ffprobe.exe → 解压阶段失败。
                string archivePath = Path.Combine(work, "missing.zip");
                Fixtures.BuildZip(archivePath, new[]
                {
                    ZipEntrySpec.File("pkg/bin/ffmpeg.exe", Fixtures.DeterministicBytes(256, 901)),
                });

                string sha;
                string hashError;
                TestKit.Check(FfmpegFileHash.TryCompute(archivePath, out sha, out hashError), "hash: " + hashError);

                var asset = new FfmpegAsset(
                    "missing-entry-asset", "1.0.0", "missing entry asset",
                    "https://example.invalid/missing.zip", sha, new FileInfo(archivePath).Length,
                    "GPLv3", "https://example.invalid/license", "https://example.invalid/source", "synthetic",
                    "bin/ffmpeg.exe",
                    new[]
                    {
                        new FfmpegAssetFile("ffmpeg.exe", "bin/ffmpeg.exe", null, true),
                        new FfmpegAssetFile("ffprobe.exe", "bin/ffprobe.exe", null, true),
                    });

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);
                File.Copy(archivePath, plan.TempFilePath, true);

                controller.ReportFinished(plan.Generation, SuccessResponse(plan));
                PumpUntilSettled(controller);

                TestKit.CheckEqual(FfmpegDownloadState.Failed, controller.State, "state");
                TestKit.CheckEqual("required-entry-missing", controller.ErrorCode, "error code");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned on install failure");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "failed install must not publish");
            });

            TestKit.Run("download: unusable binary fails the capability gate and is not published", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-capability");
                string archivePath;
                // 默认的合成资产里 ffmpeg.exe 是随机字节，不可执行：
                // 安装后的能力探测必须失败，且绝不能发布。
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, true, 5);
                File.Copy(archivePath, plan.TempFilePath, true);

                controller.ReportFinished(plan.Generation, SuccessResponse(plan));
                PumpUntilSettled(controller);

                TestKit.CheckEqual(FfmpegDownloadState.Failed, controller.State, "state");
                TestKit.CheckEqual("capability-probe-failed", controller.ErrorCode, "error code");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "unusable binary must not be published");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned");
            });

            TestKit.Run("download: shutdown while downloading cancels and cleans without publishing", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-shutdown");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var aborts = new List<long>();
                var controller = new FfmpegDownloadController(layout, generation => aborts.Add(generation));

                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);
                File.Copy(archivePath, plan.TempFilePath, true);

                // 模拟 Mod 禁用 / 卸载：必须在调用内同步收敛，不能等 OnUpdate。
                controller.Shutdown();

                TestKit.CheckEqual(FfmpegDownloadState.Cancelled, controller.State, "state after shutdown");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned by shutdown");
                TestKit.Check(aborts.Count == 1, "abort must be requested");

                // 禁用后迟到的完成通知不得标记成功。
                controller.ReportFinished(plan.Generation, SuccessResponse(plan));
                TestKit.CheckEqual(FfmpegDownloadState.Cancelled, controller.State,
                    "late completion after shutdown must not mark success");
                TestKit.Check(!Directory.Exists(layout.VersionDirectory(asset.Id, asset.Version)),
                    "shutdown must not publish");
            });

            TestKit.Run("download: existing valid install is preserved and reported as succeeded", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-existing");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAssetWithFakeFfmpeg(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });

                // 第一次安装。
                FfmpegDownloadPlan first = controller.TryStart(asset, true, 30);
                File.Copy(archivePath, first.TempFilePath, true);
                controller.ReportFinished(first.Generation, SuccessResponse(first));
                PumpUntilSettled(controller);
                TestKit.CheckEqual(FfmpegDownloadState.Succeeded, controller.State,
                    "first install (" + controller.ErrorCode + ")");

                string primary = Path.Combine(layout.VersionDirectory(asset.Id, asset.Version), "bin", "ffmpeg.exe");
                DateTime stamp = File.GetLastWriteTimeUtc(primary);

                // 第二次：既有有效安装不得被覆盖。
                FfmpegDownloadPlan second = controller.TryStart(asset, true, 30);
                File.Copy(archivePath, second.TempFilePath, true);
                controller.ReportFinished(second.Generation, SuccessResponse(second));
                PumpUntilSettled(controller);

                TestKit.CheckEqual(FfmpegDownloadState.Succeeded, controller.State, "second run");
                TestKit.CheckEqual(stamp, File.GetLastWriteTimeUtc(primary),
                    "existing install must not be overwritten");
            });

            TestKit.Run("download: orphan download files are cleaned, current task file is not", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-orphans");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);
                Directory.CreateDirectory(layout.DownloadRoot);

                string orphan = Path.Combine(layout.DownloadRoot,
                    FfmpegInstallLayout.DownloadFilePrefix + "left-over.zip");
                File.WriteAllBytes(orphan, new byte[] { 1, 2, 3 });

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);
                File.Copy(archivePath, plan.TempFilePath, true);

                int removed = controller.CleanupOrphanDownloads();
                TestKit.Check(removed >= 1, "orphan must be removed");
                TestKit.Check(!File.Exists(orphan), "orphan file must be gone");
                TestKit.Check(File.Exists(plan.TempFilePath), "current task temp file must be preserved");

                controller.Cancel("test-cleanup");
            });

            TestKit.Run("download: progress tracking is reported", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-progress");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);

                TestKit.Check(controller.ProgressFraction == 0.0, "initial progress");
                controller.ReportProgress(plan.ExpectedBytes / 2, plan.ExpectedBytes);
                TestKit.Check(controller.ProgressFraction > 0.4 && controller.ProgressFraction < 0.6,
                    "half progress, got " + controller.ProgressFraction);

                controller.ReportProgress(plan.ExpectedBytes, plan.ExpectedBytes);
                TestKit.CheckEqual(1.0, controller.ProgressFraction, "complete progress");

                controller.Cancel("test-cleanup");
            });

            TestKit.Run("download: duplicate completion of the active generation keeps the in-use archive", () =>
            {
                string work = TestKit.NewWorkDirectory(_workRoot, "download-duplicate");
                string archivePath;
                FfmpegAsset asset = Fixtures.BuildSyntheticAssetWithFakeFfmpeg(work, out archivePath);

                FfmpegInstallLayout layout;
                string layoutError;
                FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"), out layout, out layoutError);

                var controller = new FfmpegDownloadController(layout, generation => { });
                FfmpegDownloadPlan plan = controller.TryStart(asset, true, 30);
                File.Copy(archivePath, plan.TempFilePath, true);

                TestKit.Check(controller.ReportFinished(plan.Generation, SuccessResponse(plan)),
                    "first completion must be accepted");
                TestKit.CheckEqual(FfmpegDownloadState.Verifying, controller.State, "entered verification");

                // 同一代的重复完成通知：既不得重复推进校验/安装，也不得删除
                // 校验与安装**仍在读取**的归档（临时文件的 owner 是控制器）。
                TestKit.Check(!controller.ReportFinished(plan.Generation, SuccessResponse(plan)),
                    "duplicate completion must be rejected");
                TestKit.CheckEqual(FfmpegDownloadState.Verifying, controller.State,
                    "duplicate completion must not change the state");
                TestKit.Check(File.Exists(plan.TempFilePath),
                    "in-use archive must NOT be deleted while verification/installation reads it");

                PumpUntilSettled(controller);
                TestKit.CheckEqual(FfmpegDownloadState.Succeeded, controller.State,
                    "the single accepted verification must still complete (" +
                    controller.ErrorCode + " " + controller.ErrorDetail + ")");
                TestKit.Check(!File.Exists(plan.TempFilePath), "temp must be cleaned once terminal");
            });
        }

        private static FfmpegDownloadResponse SuccessResponse(FfmpegDownloadPlan plan)
        {
            return new FfmpegDownloadResponse
            {
                ResponseCode = 200,
                RequestSucceeded = true,
                FinalUrl = plan.Url,
                ReportedDownloadedBytes = plan.ExpectedBytes,
            };
        }

        /// <summary>推进控制器直到后台校验/安装结束（或超时）。</summary>
        private static void PumpUntilSettled(FfmpegDownloadController controller)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(120);
            while (controller.IsBusy && DateTime.UtcNow < deadline)
            {
                controller.Pump();
                Thread.Sleep(5);
            }
            controller.Pump();
        }
    }
}
