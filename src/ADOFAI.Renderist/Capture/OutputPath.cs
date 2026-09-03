using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Capture
{
    /// <summary>
    /// Resolves and validates the capture output directory.
    ///
    /// Phase 2.0 rules:
    ///   - Prefer Settings.OutputDirectory when it is a non-empty absolute path
    ///     that passes the reject list. Otherwise fall back to the default under
    ///     Application.persistentDataPath.
    ///   - Reject (and fall back to default) when the path resolves into:
    ///       * an ADOFAI install directory (detected by neighbouring .exe),
    ///       * a Managed directory,
    ///       * a UnityModManager core directory,
    ///       * a filesystem root.
    ///   - Reject the repository directory only when it can be detected. The
    ///     runtime never requires the repository path to exist.
    ///   - User custom-level directories are not detected at runtime; document
    ///     the recommendation instead of attempting heuristics.
    /// </summary>
    internal static class OutputPath
    {
        private const string DefaultSubdirectory = "ADOFAI.Renderist/captures";

        /// <summary>编辑器导出会话目录名冲突时最多尝试的后缀数量。</summary>
        private const int MaxUniqueNameAttempts = 1000;

        /// <summary>
        /// Resolve the output directory for a new session and ensure it exists.
        /// Returns null when no usable directory could be prepared.
        /// </summary>
        public static string ResolveSessionDirectory(string configured, string sessionStamp)
        {
            string candidate = null;

            if (!string.IsNullOrEmpty(configured))
            {
                string trimmed = configured.Trim();
                if (TryAcceptConfigured(trimmed, out candidate))
                {
                    string finalDir = Path.Combine(candidate, sessionStamp);
                    if (TryCreate(finalDir)) return finalDir;
                    Log.Warn(UiText.Format(UiText.LogOutDirConfiguredCreateFailedFormat, finalDir));
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
                return null;
            }

            string defaultDir = Path.Combine(defaultRoot, sessionStamp);
            if (TryCreate(defaultDir)) return defaultDir;

            Log.Error(UiText.LogOutDirPrepareFailed);
            return null;
        }

        /// <summary>
        /// 为编辑器导出解析一个保证“尚不存在”的全新会话目录（Phase 2.4 缺陷修复）。
        ///
        /// 与 <see cref="ResolveSessionDirectory"/> 不同：已存在的目录绝不复用。
        /// 名称冲突时依次尝试 "&lt;baseName&gt;_001" / "_002" ... 直到找到空闲名称；
        /// 达到 <see cref="MaxUniqueNameAttempts"/> 仍未命中则返回 null。
        ///
        /// 确定性唯一性：不依赖时间戳的毫秒精度，已存在目录不会被静默复用，
        /// 因此快速 Stop → Start 不会复用旧目录，也不会覆盖上一会话 metadata。
        /// F9/F10 的 single_* / seq_* 仍使用 <see cref="ResolveSessionDirectory"/>，行为不变。
        /// </summary>
        /// <param name="configured">Settings.OutputDirectory（未经过滤的原值）。</param>
        /// <param name="baseName">基础目录名（如 editor_20260902_120000）。</param>
        /// <param name="finalName">实际使用的最终目录名（与返回目录的末段一致）。</param>
        /// <returns>创建成功的完整目录路径；失败返回 null（finalName 亦为 null）。</returns>
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
                    // 与 ResolveSessionDirectory 一致：配置根创建失败时回退默认根，不直接失败。
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

        /// <summary>
        /// 在 root 下寻找空闲目录名："&lt;baseName&gt;"、"&lt;baseName&gt;_001"、"_002" ...
        /// 已存在的目录绝不复用（Directory.CreateDirectory 对已存在目录静默成功，
        /// 必须先探测再创建）。首个候选因 IO/权限创建失败即失败返回，避免无意义重试。
        /// </summary>
        private static string TryCreateUnique(string root, string baseName, out string finalName)
        {
            finalName = null;
            for (int i = 0; i < MaxUniqueNameAttempts; i++)
            {
                string name = i == 0
                    ? baseName
                    : baseName + "_" + i.ToString("000", CultureInfo.InvariantCulture);
                string dir = Path.Combine(root, name);

                // 冲突：目录已存在 → 绝不复用，继续尝试下一个后缀。
                if (Directory.Exists(dir))
                {
                    continue;
                }

                if (!TryCreate(dir))
                {
                    // 非冲突型创建失败：立即返回，由调用方决定回退默认根或失败。
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
        /// 只读验证输出目录合法性。Phase 2.2 新增。
        ///
        /// 无副作用契约：
        ///   - 不调用 Directory.CreateDirectory。
        ///   - 不调用 ResolveSessionDirectory。
        ///   - 不写文件，不产生日志（reject reason 通过返回值传递）。
        ///
        /// 与 ResolveSessionDirectory / TryAcceptConfigured 共享 reject list 规则，
        /// 但不会触发任何副作用。供 Preflight 检查使用。
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

            if (!Path.IsPathRooted(full))
            {
                return new DirectoryValidationResult
                {
                    Outcome = DirectoryValidationOutcome.Reject,
                    NormalizedPath = full,
                    RejectReason = "Must be absolute",
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

            // Phase 2.2.1: 检查盘符是否存在（仅 Windows 盘符路径，不误伤 UNC 路径）。
            // 无副作用：Directory.GetLogicalDrives 只读系统盘符列表，不创建目录、不写文件。
            // GetLogicalDrives 抛异常时保守降级（跳过检查），不阻断。
            // UNC 路径（\\server\share）的 pathRoot 第 2 个字符不是 ':'，跳过盘符检查。
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
                    // GetLogicalDrives 失败时保守降级，跳过盘符检查
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

        /// <summary>
        /// 获取默认输出目录路径（基于 Application.persistentDataPath）。
        /// 不创建目录，只返回路径字符串。失败时返回 null。
        /// </summary>
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
            string full;
            try
            {
                full = Path.GetFullPath(configured);
            }
            catch (Exception ex)
            {
                Log.Warn(UiText.Format(UiText.LogOutDirInvalidPathFormat, configured, ex.Message));
                return false;
            }

            if (!Path.IsPathRooted(full))
            {
                Log.Warn(UiText.Format(UiText.LogOutDirMustBeAbsoluteFormat, full));
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

        /// <summary>
        /// Returns true when <paramref name="candidate"/> equals or is contained
        /// under <paramref name="ancestor"/>. Both must already be absolute.
        /// Null or missing ancestors return false (nothing is rejected blindly).
        /// </summary>
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
                // dataPath = <install>/A Dance of Fire and Ice_Data
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

        /// <summary>
        /// Best-effort repository detection. The runtime does not require the
        /// repository to be reachable from the game machine; we only reject when
        /// we can confidently confirm the configured path is inside it.
        /// </summary>
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
                    {
                        return dir.FullName;
                    }
                    dir = dir.Parent;
                }
            }
            catch
            {
                // Detection is best-effort by design.
            }
            return null;
        }
    }

    /// <summary>
    /// 目录验证结果。由 OutputPath.ValidateDirectory 返回。
    /// 无副作用快照——仅包含路径验证结论，不携带任何创建/写入状态。
    /// </summary>
    internal struct DirectoryValidationResult
    {
        /// <summary>验证结论：Accept / FallBackToDefault / Reject。</summary>
        public DirectoryValidationOutcome Outcome;

        /// <summary>已 normalize 的绝对路径。Reject 时为原始输入。</summary>
        public string NormalizedPath;

        /// <summary>拒绝原因（仅 Reject 时非空）。供 Preflight / GUI 展示，不用于控制流。</summary>
        public string RejectReason;
    }

    internal enum DirectoryValidationOutcome
    {
        /// <summary>路径合法，接受用户配置。</summary>
        Accept,

        /// <summary>配置为空，回退到默认目录（基于 Application.persistentDataPath）。</summary>
        FallBackToDefault,

        /// <summary>路径非法或命中 reject list。</summary>
        Reject,
    }
}
