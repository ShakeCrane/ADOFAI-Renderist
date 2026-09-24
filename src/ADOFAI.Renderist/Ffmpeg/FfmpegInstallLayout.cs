using System;
using System.IO;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 托管安装目录的路径与命名规则（唯一单点）。
    ///
    /// 目录布局：
    /// <code>
    /// &lt;InstallRoot&gt;/
    ///   .install.lock                    进程内/跨进程安装锁文件
    ///   .staging/                        安装暂存区（同卷，便于原子发布）
    ///     &lt;assetId&gt;-&lt;version&gt;-&lt;token&gt;/
    ///   &lt;assetId&gt;/
    ///     &lt;version&gt;/                     已发布的托管安装
    ///       .renderist-ffmpeg-owner      ownership 标记（证明该目录由 Renderist 创建）
    ///       bin/ffmpeg.exe
    ///       bin/ffprobe.exe
    /// </code>
    ///
    /// 本类型只做路径运算，不接触磁盘、不依赖 Unity / UMM / Harmony。
    /// </summary>
    internal sealed class FfmpegInstallLayout
    {
        public const string LockFileName = ".install.lock";
        public const string StagingDirectoryName = ".staging";

        /// <summary>ownership 标记文件名。删除任何目录之前都必须先确认它存在。</summary>
        public const string OwnershipMarkerFileName = ".renderist-ffmpeg-owner";

        private FfmpegInstallLayout(string installRoot)
        {
            InstallRoot = installRoot;
            StagingRoot = Path.Combine(installRoot, StagingDirectoryName);
            LockFilePath = Path.Combine(installRoot, LockFileName);
        }

        /// <summary>托管安装根目录（由 GUI 层根据用户可写目录决定后注入）。</summary>
        public string InstallRoot { get; private set; }

        /// <summary>暂存区根目录。与 InstallRoot 同卷，因此发布可用目录 Move 完成。</summary>
        public string StagingRoot { get; private set; }

        public string LockFilePath { get; private set; }

        /// <summary>
        /// 创建布局。非法（空 / 非绝对路径）时返回 false，绝不猜测目录。
        /// </summary>
        public static bool TryCreate(string installRoot, out FfmpegInstallLayout layout, out string error)
        {
            layout = null;
            error = null;

            if (string.IsNullOrWhiteSpace(installRoot))
            {
                error = "install-root-empty";
                return false;
            }

            string trimmed = installRoot.Trim();

            // 必须先判绝对路径：Path.GetFullPath 会把相对路径按当前工作目录解析，
            // 从而把"依赖进程 CWD"的目录静默变成一个看似合法的绝对路径。
            // 托管目录不允许猜测来源。
            if (!Path.IsPathRooted(trimmed))
            {
                error = "install-root-not-rooted";
                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(trimmed);
            }
            catch (Exception ex)
            {
                error = "install-root-invalid: " + ex.Message;
                return false;
            }

            if (!Path.IsPathRooted(full))
            {
                error = "install-root-not-rooted";
                return false;
            }

            layout = new FfmpegInstallLayout(full);
            return true;
        }

        /// <summary>某个资产某个版本的最终安装目录。</summary>
        public string VersionDirectory(string assetId, string version)
        {
            return Path.Combine(Path.Combine(InstallRoot, RequireSegment(assetId, "assetId")),
                RequireSegment(version, "version"));
        }

        /// <summary>某次安装尝试独占的暂存目录。</summary>
        public string StagingDirectory(string assetId, string version, string token)
        {
            string name = RequireSegment(assetId, "assetId") + "-" +
                          RequireSegment(version, "version") + "-" +
                          RequireSegment(token, "token");
            return Path.Combine(StagingRoot, name);
        }

        /// <summary>目录的 ownership 标记文件路径。</summary>
        public string OwnershipMarkerPath(string directory)
        {
            return Path.Combine(directory, OwnershipMarkerFileName);
        }

        /// <summary>把资产的相对路径解析为托管安装目录内的绝对路径（已做安全校验）。</summary>
        public static bool TryResolveRelative(string baseDirectory, string relativePath, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrEmpty(baseDirectory) || !FfmpegAssetManifest.IsSafeRelativePath(relativePath))
                return false;

            try
            {
                string baseFull = Path.GetFullPath(baseDirectory);
                string candidate = Path.GetFullPath(Path.Combine(baseFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));

                if (!IsUnder(baseFull, candidate))
                    return false;

                fullPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>判断 <paramref name="candidate"/> 是否位于 <paramref name="directory"/> 之内（含自身）。</summary>
        public static bool IsUnder(string directory, string candidate)
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(candidate))
                return false;

            string dir = directory;
            if (!dir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                dir += Path.DirectorySeparatorChar;

            return candidate.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase);
        }

        private static string RequireSegment(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("layout segment '" + name + "' must not be empty", name);

            if (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0 ||
                value == "." || value == ".." || (value.Length >= 2 && value[1] == ':'))
            {
                throw new ArgumentException("layout segment '" + name + "' must be a bare name", name);
            }

            return value;
        }
    }
}
