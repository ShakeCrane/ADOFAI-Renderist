using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// Resolves and validates deterministic editor-export output directories.
    ///
    /// Rules:
    ///   - Prefer Settings.OutputDirectory when it is a non-empty absolute path
    ///     that passes the reject list. Otherwise fall back to the default under
    ///     Application.persistentDataPath.
    ///   - Reject paths that resolve into an ADOFAI install directory, Managed,
    ///     UnityModManager core directory, filesystem root, or detected repository.
    ///   - User custom-level directories are not guessed at runtime.
    /// </summary>
    internal static class OutputPath
    {
        private const string DefaultSubdirectory = "ADOFAI.Renderist/captures";
        private const int MaxUniqueNameAttempts = 1000;

        /// <summary>
        /// 为编辑器导出创建一个保证此前不存在的全新会话目录。
        /// 名称冲突时依次尝试 "&lt;baseName&gt;_001" / "_002" ...；
        /// 已存在目录绝不复用，因此快速 Stop → Start 不会覆盖上一会话 metadata。
        /// </summary>
        public static string ResolveUniqueSessionDirectory(string configured, string baseName, out string finalName)
        {
            finalName = null;

            if (!string.IsNullOrEmpty(configured))
            {
                string trimmed = configured.Trim();
                if (TryAcceptConfigured(trimmed, out string root))
                {
                    string dir = TryCreateUnique(root, baseName, out string name);
                    if (dir != null)
                    {
                        finalName = name;
                        return dir;
                    }
                    Log.Warn(UiText.Format(UiText.LogOutDirConfiguredCreateFailedFormat, Path.Combine(root, baseName)));
                    // 配置根创建失败时回退默认根，不直接失败。
                }
            }

            string defaultRoot;
            try
            {
                defaultRoot = Path.Combine(Application.persistentDataPath, DefaultSubdirectory);
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.LogExPersistentDataPathFailed, ex);
                finalName = null;
                return null;
            }

            string defaultDir = TryCreateUnique(defaultRoot, baseName, out string defaultName);
            if (defaultDir != null)
            {
                finalName = defaultName;
                return defaultDir;
            }

            finalName = null;
            Log.Error(UiText.LogOutDirPrepareFailed);
            return null;
        }

        private static string TryCreateUnique(string root, string baseName, out string finalName)
        {
            finalName = null;
            for (int i = 0; i < MaxUniqueNameAttempts; i++)
            {
                string name = i == 0
                    ? baseName
                    : baseName + "_" + i.ToString("000", CultureInfo.InvariantCulture);
                string dir = Path.Combine(root, name);

                if (Directory.Exists(dir))
                    continue;

                if (!TryCreate(dir))
                {
                    finalName = null;
                    return null;
                }

                finalName = name;
                return dir;
            }

            Log.Warn(UiText.Format(UiText.LogOutDirUniqueNameExhaustedFormat, MaxUniqueNameAttempts, baseName));
            return null;
        }

        /// <summary>
        /// 只读验证输出目录合法性。
        /// 无副作用：不创建目录、不写文件、不产生日志；reject reason 通过返回值传递。
        /// 与实际创建路径共享同一 reject-list 规则。
        /// </summary>
        public static DirectoryValidationResult ValidateDirectory(string configured)
        {
            if (string.IsNullOrEmpty(configured))
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.FallBackToDefault,
                    NormalizedPath = TryGetDefaultRoot(),
                    RejectReason = null,
                };
            }

            string trimmed = configured.Trim();
            if (!Path.IsPathRooted(trimmed))
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.Reject,
                    NormalizedPath = trimmed,
                    RejectReason = "Must be absolute",
                };
            }

            string full;
            try
            {
                full = Path.GetFullPath(trimmed);
            }
            catch
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.Reject,
                    NormalizedPath = trimmed,
                    RejectReason = "Invalid path",
                };
            }

            if (IsFilesystemRoot(full))
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.Reject,
                    NormalizedPath = full,
                    RejectReason = "Filesystem root",
                };
            }

            // 仅 Windows 盘符路径检查盘符是否存在；UNC 路径跳过。
            string pathRoot = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(pathRoot) && pathRoot.Length >= 2 && pathRoot[1] == ':')
            {
                bool driveExists = false;
                try
                {
                    string[] logicalDrives = Directory.GetLogicalDrives();
                    string rootNormalized = pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    foreach (string drive in logicalDrives)
                    {
                        string driveNormalized = drive.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.Equals(driveNormalized, rootNormalized, StringComparison.OrdinalIgnoreCase))
                        {
                            driveExists = true;
                            break;
                        }
                    }
                }
                catch
                {
                    driveExists = true;
                }
                if (!driveExists)
                {
                    return new DirectoryValidationResult
                    {
                        Outcome = DirectoryValidationOutcome.Reject,
                        NormalizedPath = full,
                        RejectReason = "Drive does not exist",
                    };
                }
            }

            if (PathContains(full, GetAdofaiInstallRoot()))
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.Reject,
                    NormalizedPath = full,
                    RejectReason = "ADOFAI install directory",
                };
            }

            if (PathContains(full, GetManagedDir()))
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.Reject,
                    NormalizedPath = full,
                    RejectReason = "Managed directory",
                };
            }

            if (PathContains(full, GetUmmCoreDir()))
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.Reject,
                    NormalizedPath = full,
                    RejectReason = "UnityModManager directory",
                };
            }

            string repoRoot = TryDetectRepositoryRoot();
            if (repoRoot != null && PathContains(full, repoRoot))
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.Reject,
                    NormalizedPath = full,
                    RejectReason = "Repository directory",
                };
            }

            return new DirectoryValidationResult
            {
                Outcome = DirectoryValidationOutcome.Accept,
                NormalizedPath = full,
                RejectReason = null,
            };
        }

        private static string TryGetDefaultRoot()
        {
            try
            {
                return Path.Combine(Application.persistentDataPath, DefaultSubdirectory);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryAcceptConfigured(string configured, out string accepted)
        {
            accepted = null;
            string trimmed = (configured ?? string.Empty).Trim();
            if (!Path.IsPathRooted(trimmed))
            {
                Log.Warn(UiText.Format(UiText.LogOutDirMustBeAbsoluteFormat, trimmed));
                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(trimmed);
            }
            catch (Exception ex)
            {
                Log.Warn(UiText.Format(UiText.LogOutDirInvalidPathFormat, trimmed, ex.Message));
                return false;
            }

            if (IsFilesystemRoot(full))
            {
                Log.Error(UiText.Format(UiText.LogOutDirRejectRootFormat, full));
                return false;
            }

            if (PathContains(full, GetAdofaiInstallRoot()))
            {
                Log.Error(UiText.Format(UiText.LogOutDirRejectInstallFormat, full));
                return false;
            }

            if (PathContains(full, GetManagedDir()))
            {
                Log.Error(UiText.Format(UiText.LogOutDirRejectManagedFormat, full));
                return false;
            }

            if (PathContains(full, GetUmmCoreDir()))
            {
                Log.Error(UiText.Format(UiText.LogOutDirRejectUmmFormat, full));
                return false;
            }

            string repoRoot = TryDetectRepositoryRoot();
            if (repoRoot != null && PathContains(full, repoRoot))
            {
                Log.Error(UiText.Format(UiText.LogOutDirRejectRepoFormat, full));
                return false;
            }

            accepted = full;
            return true;
        }

        private static bool TryCreate(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                return Directory.Exists(dir);
            }
            catch (Exception ex)
            {
                Log.Exception(UiText.Format(UiText.LogExCreateDirectoryFailedFormat, dir), ex);
                return false;
            }
        }

        private static bool IsFilesystemRoot(string full)
        {
            try
            {
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                return string.Equals(
                    full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool PathContains(string candidate, string ancestor)
        {
            if (string.IsNullOrEmpty(ancestor)) return false;
            try
            {
                string a = Path.GetFullPath(ancestor)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string c = Path.GetFullPath(candidate)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(a, c, StringComparison.OrdinalIgnoreCase)) return true;
                string aSep = a + Path.DirectorySeparatorChar;
                return c.StartsWith(aSep, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string GetAdofaiInstallRoot()
        {
            try
            {
                string dataPath = Application.dataPath;
                if (string.IsNullOrEmpty(dataPath)) return null;
                DirectoryInfo parent = Directory.GetParent(dataPath);
                return parent != null ? parent.FullName : null;
            }
            catch
            {
                return null;
            }
        }

        private static string GetManagedDir()
        {
            try
            {
                string dataPath = Application.dataPath;
                if (string.IsNullOrEmpty(dataPath)) return null;
                return Path.Combine(dataPath, "Managed");
            }
            catch
            {
                return null;
            }
        }

        private static string GetUmmCoreDir()
        {
            string managed = GetManagedDir();
            if (string.IsNullOrEmpty(managed)) return null;
            return Path.Combine(managed, "UnityModManager");
        }

        private static string TryDetectRepositoryRoot()
        {
            try
            {
                var asm = typeof(OutputPath).Assembly;
                string asmPath = asm.Location;
                if (string.IsNullOrEmpty(asmPath)) return null;

                DirectoryInfo dir = new DirectoryInfo(Path.GetDirectoryName(asmPath));
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch
            {
                // Best-effort only.
            }
            return null;
        }
    }

    internal struct DirectoryValidationResult
    {
        public DirectoryValidationOutcome Outcome;
        public string NormalizedPath;
        public string RejectReason;
    }

    internal enum DirectoryValidationOutcome
    {
        Accept,
        FallBackToDefault,
        Reject,
    }
}
