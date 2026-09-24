using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 托管安装目录的 ownership 标记内容。
    /// </summary>
    internal sealed class FfmpegOwnershipMarker
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; }
        public string AssetId { get; set; }
        public string ArchiveSha256 { get; set; }
        public string InstalledUtc { get; set; }

        /// <summary>
        /// 解析标记文件。任何损坏都视为"不是 Renderist 拥有的目录"，绝不猜测。
        /// </summary>
        public static bool TryRead(string markerPath, out FfmpegOwnershipMarker marker, out string error)
        {
            marker = null;
            error = null;

            try
            {
                if (!File.Exists(markerPath))
                {
                    error = "marker-missing";
                    return false;
                }

                var parsed = new FfmpegOwnershipMarker();
                string[] lines = File.ReadAllLines(markerPath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        error = "marker-malformed-line";
                        return false;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "schemaVersion":
                            int schema;
                            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out schema))
                            {
                                error = "marker-schema-invalid";
                                return false;
                            }
                            parsed.SchemaVersion = schema;
                            break;
                        case "assetId":
                            parsed.AssetId = value;
                            break;
                        case "archiveSha256":
                            parsed.ArchiveSha256 = value;
                            break;
                        case "installedUtc":
                            parsed.InstalledUtc = value;
                            break;
                        default:
                            // 未知键：向前兼容，忽略。
                            break;
                    }
                }

                if (parsed.SchemaVersion <= 0 || parsed.SchemaVersion > CurrentSchemaVersion)
                {
                    error = "marker-schema-unsupported";
                    return false;
                }

                if (string.IsNullOrEmpty(parsed.AssetId))
                {
                    error = "marker-asset-id-missing";
                    return false;
                }

                marker = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = "marker-read-failed: " + ex.Message;
                return false;
            }
        }

        public static bool TryWrite(
            string markerPath, string assetId, string archiveSha256, DateTime installedUtc, out string error)
        {
            error = null;
            try
            {
                var sb = new StringBuilder();
                sb.Append("schemaVersion=").Append(CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("assetId=").Append(assetId ?? string.Empty).Append('\n');
                sb.Append("archiveSha256=").Append(archiveSha256 ?? string.Empty).Append('\n');
                sb.Append("installedUtc=")
                  .Append(installedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))
                  .Append('\n');

                File.WriteAllText(markerPath, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = "marker-write-failed: " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// 磁盘上一份托管安装的状态。
    ///
    /// 关键不变量：<see cref="IsValid"/> 为 false 的目录**既不能被覆盖也不能被删除**。
    /// 它可能属于别的工具，也可能是被外部破坏的安装；两种情况都需要用户显式处理。
    /// </summary>
    internal sealed class FfmpegManagedInstall
    {
        public string InstallDirectory { get; set; }
        public string AssetId { get; set; }
        public string Version { get; set; }

        /// <summary>主可执行文件绝对路径（仅当文件实际存在时非 null）。</summary>
        public string PrimaryExecutablePath { get; set; }

        public bool DirectoryPresent { get; set; }
        public bool MarkerPresent { get; set; }
        public bool MarkerOwnedByRenderist { get; set; }
        public FfmpegOwnershipMarker Marker { get; set; }

        public bool IsValid { get; set; }

        /// <summary><see cref="IsValid"/> 为 false 时的原因码（机读）。</summary>
        public string InvalidReason { get; set; }
    }

    /// <summary>
    /// 定位并校验某个资产在托管目录中的安装。
    ///
    /// 校验顺序：目录存在 → ownership 标记存在且属于 Renderist → 每个必需文件存在 →
    /// 声明了哈希的文件内容匹配。任何一步失败都返回 <c>IsValid = false</c> 并给出原因码，
    /// 调用方据此 fail-closed。
    /// </summary>
    internal static class FfmpegManagedInstallLocator
    {
        public static FfmpegManagedInstall Locate(FfmpegInstallLayout layout, FfmpegAsset asset)
        {
            var install = new FfmpegManagedInstall
            {
                AssetId = asset.Id,
                Version = asset.Version,
                InstallDirectory = layout.VersionDirectory(asset.Id, asset.Version),
            };

            if (!Directory.Exists(install.InstallDirectory))
            {
                install.InvalidReason = "managed-install-absent";
                return install;
            }

            install.DirectoryPresent = true;

            FfmpegOwnershipMarker marker;
            string markerError;
            install.MarkerPresent = File.Exists(layout.OwnershipMarkerPath(install.InstallDirectory));
            if (!install.MarkerPresent)
            {
                install.InvalidReason = "ownership-marker-missing";
                return install;
            }

            if (!FfmpegOwnershipMarker.TryRead(
                    layout.OwnershipMarkerPath(install.InstallDirectory), out marker, out markerError))
            {
                install.InvalidReason = markerError ?? "ownership-marker-unreadable";
                return install;
            }

            install.Marker = marker;

            if (!string.Equals(marker.AssetId, asset.Id, StringComparison.Ordinal))
            {
                install.InvalidReason = "ownership-marker-asset-mismatch";
                return install;
            }

            install.MarkerOwnedByRenderist = true;

            for (int i = 0; i < asset.Files.Count; i++)
            {
                FfmpegAssetFile file = asset.Files[i];
                string fullPath;
                if (!FfmpegInstallLayout.TryResolveRelative(install.InstallDirectory, file.RelativePath, out fullPath))
                {
                    install.InvalidReason = "install-file-path-unsafe: " + file.RelativePath;
                    return install;
                }

                if (!File.Exists(fullPath))
                {
                    if (!file.Required)
                        continue;

                    install.InvalidReason = "install-file-missing: " + file.RelativePath;
                    return install;
                }

                if (file.Sha256 == null)
                    continue;

                string actual;
                string hashError;
                if (!FfmpegFileHash.TryCompute(fullPath, out actual, out hashError))
                {
                    install.InvalidReason = "install-file-hash-failed: " + file.RelativePath + " (" + hashError + ")";
                    return install;
                }

                if (!FfmpegFileHash.Matches(file.Sha256, actual))
                {
                    install.InvalidReason = "install-file-hash-mismatch: " + file.RelativePath;
                    return install;
                }
            }

            string primaryPath;
            if (!FfmpegInstallLayout.TryResolveRelative(
                    install.InstallDirectory, asset.PrimaryExecutableRelativePath, out primaryPath) ||
                !File.Exists(primaryPath))
            {
                install.InvalidReason = "primary-executable-missing";
                return install;
            }

            install.PrimaryExecutablePath = primaryPath;
            install.IsValid = true;
            return install;
        }

        /// <summary>列出托管目录中所有可识别的安装（含无效项），用于 GUI 展示。</summary>
        public static IReadOnlyList<FfmpegManagedInstall> ListAll(
            FfmpegInstallLayout layout, IReadOnlyList<FfmpegAsset> assets)
        {
            var results = new List<FfmpegManagedInstall>();
            for (int i = 0; i < assets.Count; i++)
                results.Add(Locate(layout, assets[i]));
            return results;
        }
    }
}
