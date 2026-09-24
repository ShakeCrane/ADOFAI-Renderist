using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 文件 SHA-256 计算（小写十六进制），带按 (路径, 长度, 最后写入时间) 的记忆化缓存。
    ///
    /// 缓存的存在理由：FFmpeg 主可执行文件约 100 MB，而组件就绪状态会在 GUI 中反复刷新。
    /// 缓存键包含长度与最后写入时间，因此文件被替换后不会返回过期哈希。
    /// 不接触 Unity / UMM / Harmony。
    /// </summary>
    internal static class FfmpegFileHash
    {
        private const int MaxCacheEntries = 64;

        private sealed class CacheEntry
        {
            public long Length;
            public long LastWriteUtcTicks;
            public string Sha256;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

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

            string cacheKey = info.FullName;
            lock (Gate)
            {
                CacheEntry cached;
                if (Cache.TryGetValue(cacheKey, out cached) &&
                    cached.Length == info.Length &&
                    cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    sha256Hex = cached.Sha256;
                    return true;
                }
            }

            string computed;
            try
            {
                using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                           FileShare.Read, 1 << 20))
                using (var sha = SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(stream);
                    computed = ToHex(digest);
                }
            }
            catch (Exception ex)
            {
                error = "file-read-failed: " + ex.Message;
                return false;
            }

            lock (Gate)
            {
                if (Cache.Count >= MaxCacheEntries)
                    Cache.Clear();

                Cache[cacheKey] = new CacheEntry
                {
                    Length = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Sha256 = computed,
                };
            }

            sha256Hex = computed;
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
