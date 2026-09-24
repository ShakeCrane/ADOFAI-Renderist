using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>归档条目定义（用于构造测试用 ZIP）。</summary>
    internal sealed class ZipEntrySpec
    {
        public string Name;
        public byte[] Content = new byte[0];

        /// <summary>可选：设置外部属性（用于伪造符号链接 / 重解析点条目）。</summary>
        public int? ExternalAttributes;

        public static ZipEntrySpec File(string name, byte[] content)
        {
            return new ZipEntrySpec { Name = name, Content = content };
        }

        public static ZipEntrySpec Text(string name, string text)
        {
            return new ZipEntrySpec { Name = name, Content = Encoding.UTF8.GetBytes(text) };
        }
    }

    /// <summary>
    /// 测试夹具构造：合成资产与 ZIP。
    ///
    /// 刻意使用**合成字节**而不是真实 FFmpeg 二进制：回归测试不得携带任何下载产物，
    /// 而 L1 的契约（哈希校验、路径安全、所有权、回滚）与文件内容无关。
    /// </summary>
    internal static class Fixtures
    {
        public const string TestAssetId = "test-asset";
        public const string TestAssetVersion = "1.0.0";

        public static byte[] DeterministicBytes(int length, int seed)
        {
            var bytes = new byte[length];
            var random = new Random(seed);
            random.NextBytes(bytes);
            return bytes;
        }

        public static string WriteFile(string path, byte[] content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(path, content);
            return path;
        }

        public static string BuildZip(string zipPath, IReadOnlyList<ZipEntrySpec> entries)
        {
            string directory = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(entries[i].Name);
                    if (entries[i].ExternalAttributes.HasValue)
                        entry.ExternalAttributes = entries[i].ExternalAttributes.Value;

                    using (Stream stream = entry.Open())
                        stream.Write(entries[i].Content, 0, entries[i].Content.Length);
                }
            }

            return zipPath;
        }

        /// <summary>
        /// 合成一份结构完整的"资产 + 归档"：归档内 <c>&lt;prefix&gt;bin/ffmpeg.exe</c> 与
        /// <c>&lt;prefix&gt;bin/ffprobe.exe</c>，哈希与清单声明一致。
        /// </summary>
        public static FfmpegAsset BuildSyntheticAsset(
            string workDirectory,
            out string archivePath,
            string assetId = TestAssetId,
            string version = TestAssetVersion,
            int ffmpegSize = 64 * 1024,
            int ffprobeSize = 8 * 1024,
            string entryPrefix = "pkg/")
        {
            byte[] ffmpegBytes = DeterministicBytes(ffmpegSize, 1);
            byte[] ffprobeBytes = DeterministicBytes(ffprobeSize, 2);

            string assetDirectory = Path.Combine(workDirectory, "asset-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(assetDirectory);

            archivePath = Path.Combine(assetDirectory, "asset.zip");

            BuildZip(archivePath, new[]
            {
                ZipEntrySpec.File(entryPrefix + "bin/ffmpeg.exe", ffmpegBytes),
                ZipEntrySpec.File(entryPrefix + "bin/ffprobe.exe", ffprobeBytes),
                ZipEntrySpec.Text(entryPrefix + "LICENSE", "test license"),
                ZipEntrySpec.Text(entryPrefix + "doc/notes.txt", "not whitelisted"),
            });

            string archiveSha256;
            string error;
            if (!FfmpegFileHash.TryCompute(archivePath, out archiveSha256, out error))
                throw new Exception("failed to hash synthetic archive: " + error);

            return new FfmpegAsset(
                assetId,
                version,
                "synthetic test asset",
                "https://example.invalid/asset.zip",
                archiveSha256,
                new FileInfo(archivePath).Length,
                "GPLv3",
                "https://example.invalid/license",
                "synthetic fixture",
                "bin/ffmpeg.exe",
                new[]
                {
                    new FfmpegAssetFile("ffmpeg.exe", "bin/ffmpeg.exe", Sha256Of(ffmpegBytes), true),
                    new FfmpegAssetFile("ffprobe.exe", "bin/ffprobe.exe", Sha256Of(ffprobeBytes), true),
                });
        }

        public static string Sha256Of(byte[] content)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return FfmpegFileHash.ToHex(sha.ComputeHash(content));
            }
        }

        public static IReadOnlyList<SafeZipEntryRule> RulesFor(FfmpegAsset asset)
        {
            var rules = new List<SafeZipEntryRule>();
            for (int i = 0; i < asset.Files.Count; i++)
            {
                FfmpegAssetFile file = asset.Files[i];
                rules.Add(new SafeZipEntryRule(file.MatchFileName, file.RelativePath, file.Sha256, file.Required));
            }
            return rules;
        }

        /// <summary>在托管目录中伪造一份"外来"（无 ownership 标记）安装目录。</summary>
        public static string CreateForeignInstallDirectory(FfmpegInstallLayout layout, FfmpegAsset asset)
        {
            string directory = layout.VersionDirectory(asset.Id, asset.Version);
            Directory.CreateDirectory(directory);
            WriteFile(Path.Combine(directory, "unrelated.txt"), Encoding.UTF8.GetBytes("foreign content"));
            return directory;
        }

        /// <summary>在托管目录中伪造一个"自己的孤儿 staging"（带合法 ownership 标记）。</summary>
        public static string CreateOwnOrphanStaging(FfmpegInstallLayout layout)
        {
            string directory = layout.StagingDirectory(TestAssetId, TestAssetVersion,
                "orphan-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);

            string error;
            if (!FfmpegOwnershipMarker.TryWrite(
                    layout.OwnershipMarkerPath(directory), TestAssetId, "deadbeef", DateTime.UtcNow, out error))
            {
                throw new Exception("failed to write orphan marker: " + error);
            }

            WriteFile(Path.Combine(directory, "partial.bin"), new byte[] { 1, 2, 3 });
            return directory;
        }

        /// <summary>在托管目录中伪造一个"外来的" staging（无标记），必须被保留。</summary>
        public static string CreateForeignStagingDirectory(FfmpegInstallLayout layout)
        {
            string directory = layout.StagingDirectory(TestAssetId, TestAssetVersion,
                "foreign-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);
            WriteFile(Path.Combine(directory, "someone-elses.bin"), new byte[] { 9, 9, 9 });
            return directory;
        }

        public static int CountStagingDirectories(FfmpegInstallLayout layout)
        {
            if (!Directory.Exists(layout.StagingRoot))
                return 0;
            return Directory.GetDirectories(layout.StagingRoot).Length;
        }
    }
}
