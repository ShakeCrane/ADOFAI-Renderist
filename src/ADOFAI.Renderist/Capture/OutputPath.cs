using System;
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
}
