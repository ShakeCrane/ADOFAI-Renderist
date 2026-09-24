using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 文件 SHA-256 计算（小写十六进制）。安装与身份校验必须读取当前文件内容；
    /// 路径、长度和修改时间可以被保留，因此不能用元数据缓存代替完整性校验。
    /// 不接触 Unity / UMM / Harmony。
    /// </summary>
    internal static class FfmpegFileHash
    {
        public static bool TryCompute(string path, out string sha256Hex, out string error)
        {
            sha256Hex = null;
            error = null;

            if (string.IsNullOrEmpty(path))
            {
                error = "path-empty";
                return false;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    error = "file-not-found";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "file-stat-failed: " + ex.Message;
                return false;
            }

            try
            {
                using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                           FileShare.Read, 1 << 20))
                using (var sha = SHA256.Create())
                {
                    sha256Hex = ToHex(sha.ComputeHash(stream));
                }
            }
            catch (Exception ex)
            {
                error = "file-read-failed: " + ex.Message;
                return false;
            }

            return true;
        }

        /// <summary>比较两个 SHA-256 十六进制串，大小写不敏感。</summary>
        public static bool Matches(string expected, string actual)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
                return false;

            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
