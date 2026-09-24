using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 跨进程安装锁。
    ///
    /// 实现方式：以 <see cref="FileShare.None"/> 独占打开锁文件并**在整个安装期间持有句柄**。
    /// 因此：
    ///   * 第二个并发安装会立刻收到共享冲突并被拒绝（fail-closed）；
    ///   * 进程被强制终止时操作系统会释放句柄，锁不会永久卡死（不会留下"死锁文件"）。
    ///
    /// 锁文件本身刻意不删除：真正起作用的独占句柄，而不是文件是否存在。
    /// 不接触 Unity / UMM / Harmony。
    /// </summary>
    internal sealed class FfmpegInstallLock : IDisposable
    {
        private FileStream _handle;
        private string _lockFilePath;
        private bool _disposed;

        private FfmpegInstallLock(FileStream handle, string lockFilePath)
        {
            _handle = handle;
            _lockFilePath = lockFilePath;
        }

        public string LockFilePath
        {
            get { return _lockFilePath; }
        }

        /// <summary>
        /// 尝试取得安装锁。失败时返回 false 并给出机读原因码，调用方必须放弃安装。
        /// </summary>
        public static bool TryAcquire(
            string lockFilePath, out FfmpegInstallLock installLock, out string errorCode, out string errorDetail)
        {
            installLock = null;
            errorCode = null;
            errorDetail = null;

            if (string.IsNullOrWhiteSpace(lockFilePath))
            {
                errorCode = "install-lock-path-empty";
                return false;
            }

            try
            {
                string directory = Path.GetDirectoryName(lockFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                errorCode = "install-lock-directory-failed";
                errorDetail = ex.Message;
                return false;
            }

            FileStream handle = null;
            try
            {
                handle = new FileStream(
                    lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096,
                    FileOptions.WriteThrough);

                // 写入诊断信息；失败不影响锁本身的有效性。
                try
                {
                    string info = "pid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                                  "\nacquiredUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n";
                    byte[] bytes = new UTF8Encoding(false).GetBytes(info);
                    handle.SetLength(0);
                    handle.Write(bytes, 0, bytes.Length);
                    handle.Flush();
                }
                catch
                {
                }

                installLock = new FfmpegInstallLock(handle, lockFilePath);
                return true;
            }
            catch (IOException ex)
            {
                // 共享冲突 = 另一个安装正在进行。
                errorCode = "install-lock-held";
                errorDetail = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                errorCode = "install-lock-denied";
                errorDetail = ex.Message;
            }
            catch (Exception ex)
            {
                errorCode = "install-lock-failed";
                errorDetail = ex.Message;
            }

            if (handle != null)
            {
                try
                {
                    handle.Dispose();
                }
                catch
                {
                }
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_handle != null)
            {
                try
                {
                    _handle.Dispose();
                }
                catch
                {
                }
                _handle = null;
            }
        }
    }
}
