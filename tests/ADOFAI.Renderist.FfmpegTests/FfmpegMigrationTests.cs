using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.Renderist.Export;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist.FfmpegTests
{
    /// <summary>
    /// 旧托管安装根（LocalLow）→ 当前托管根（LocalApplicationData）一次性迁移回归。
    ///
    /// 断言的仍是**行为契约**：
    ///   * 新托管根优先，旧根只读；
    ///   * 只有通过 ownership + manifest 哈希核验的旧安装才允许迁移；
    ///   * 目标已存在时绝不覆盖（有效 = 无需迁移，无效 = fail-closed）；
    ///   * 任何失败 / 取消都不得留下最终目标半成品，也不得删除 ownership 无法确认的文件；
    ///   * 迁移不引入长期双根 discovery，也不新增一次性迁移标记（有效安装本身即完成判据）。
    /// </summary>
    internal static class FfmpegMigrationTests
    {
        /// <summary>测试资产钉死的归档哈希（迁移的额外 eligibility 条件就是它）。</summary>
        private const string TestArchiveSha256 =
            "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";

        private const string OtherArchiveSha256 =
            "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec";

        private static string _workRoot;

        private sealed class Scenario
        {
            public string Work;
            public FfmpegInstallLayout Target;
            public FfmpegInstallLayout Legacy;
            public FfmpegAsset Asset;

            public string LegacyRoot { get { return Legacy.InstallRoot; } }
            public string TargetRoot { get { return Target.InstallRoot; } }
            public string TargetDirectory { get { return Target.VersionDirectory(Asset.Id, Asset.Version); } }
            public string LegacyDirectory { get { return Legacy.VersionDirectory(Asset.Id, Asset.Version); } }
        }

        public static void Run(string workRoot)
        {
            _workRoot = workRoot;

            byte[] fakeExe = File.ReadAllBytes(FakeFfmpeg.ExecutablePath);
            byte[] deterministic = Fixtures.DeterministicBytes(4096, 21);

            FreshWithoutLegacyCreatesNothing(deterministic);
            FreshWithNewInstallIsAuthoritative(fakeExe);
            UpgradeMigratesTheLegacyInstall(fakeExe);
            BothValidPrefersTheNewInstall(fakeExe);
            NewValidLegacyInvalidDoesNotMigrate(fakeExe);
            NewInvalidLegacyValidIsBlockedFailClosed(fakeExe);
            TargetWithMarkerButBrokenContentIsNotMigrated(deterministic);
            LegacyMarkerMissingIsNotMigrated(deterministic);
            LegacyMarkerMalformedIsNotMigrated(deterministic);
            LegacyMarkerAssetMismatchIsNotMigrated(deterministic);
            LegacyMarkerArchiveMismatchIsNotMigrated(deterministic);
            LegacyFfmpegHashMismatchIsNotMigrated(deterministic);
            LegacyFfprobeHashMismatchIsNotMigrated(deterministic);
            ForeignLegacyDirectoryIsLeftUntouched(deterministic);
            OnlyManifestDeclaredFilesAreCopied(fakeExe);
            StagingFailureLeavesNoTarget(deterministic);
            CapabilityProbeFailureIsNotPublished(Encoding.UTF8.GetBytes("not a PE file"));
            RepeatedMigrationIsIdempotent(fakeExe);
            SameRootIsNeverMigrated(deterministic);
            NullInputsAreHandled(deterministic);
            CancellationDuringCopyCleansOnlyItsOwnStaging();
            TargetAppearingBeforePublicationIsNeverOverwritten();
            InitialStartupReadinessIsReadyAfterMigration(fakeExe);
            RealPinnedFixtureUpgradeIsReady();
            SkippedMigrationDoesNotRegressDiscovery(fakeExe);
            OutputModesAreNotBlockedByMigrationFailure(fakeExe);
        }

        // ==================== 新托管根优先 ====================

        private static void FreshWithoutLegacyCreatesNothing(byte[] content)
        {
            TestKit.Run("migration: no legacy install performs no migration and creates no directory", () =>
            {
                Scenario s = NewScenario("migration-fresh", content, content);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyAbsent);
                TestKit.Check(!Directory.Exists(s.TargetRoot),
                    "the target root must not be created when there is nothing to migrate");
                TestKit.Check(!Directory.Exists(s.LegacyRoot),
                    "the legacy root must not be created");
            });
        }

        private static void FreshWithNewInstallIsAuthoritative(byte[] content)
        {
            TestKit.Run("migration: a fresh new-root install is authoritative and untouched", () =>
            {
                Scenario s = NewScenario("migration-fresh-new", content, content);
                Fixtures.CreateManagedInstall(s.Target, s.Asset, new[] { content, content });
                SortedDictionary<string, string> before = SnapshotTree(s.TargetDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.NotNeeded,
                    FfmpegLegacyMigrationReason.TargetPresent);
                TestKit.Check(!Directory.Exists(s.LegacyRoot), "no legacy directory may be created");
                CheckTreeUnchanged("new install", s.TargetDirectory, before);
                CheckNoStaging(s.Target);
            });
        }

        private static void BothValidPrefersTheNewInstall(byte[] content)
        {
            TestKit.Run("migration: a valid new install wins over a valid legacy install", () =>
            {
                Scenario s = NewScenario("migration-both-valid", content, content);
                DateTime newInstalledUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                DateTime legacyInstalledUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                Fixtures.CreateManagedInstall(s.Target, s.Asset, new[] { content, content },
                    TestArchiveSha256, newInstalledUtc);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content },
                    TestArchiveSha256, legacyInstalledUtc);
                // 旧根中的未知文件必须始终被忽略。
                Fixtures.WriteFile(Path.Combine(s.LegacyDirectory, "unknown.bin"), new byte[] { 1, 2, 3 });

                SortedDictionary<string, string> targetBefore = SnapshotTree(s.TargetDirectory);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.NotNeeded,
                    FfmpegLegacyMigrationReason.TargetPresent);
                CheckTreeUnchanged("new install", s.TargetDirectory, targetBefore);
                CheckTreeUnchanged("legacy install", s.LegacyDirectory, legacyBefore);
                TestKit.Check(MarkerText(s.Target).Contains(newInstalledUtc.ToString("o")),
                    "the new marker must not be rewritten");
                TestKit.Check(MarkerText(s.Legacy).Contains(legacyInstalledUtc.ToString("o")),
                    "the legacy marker must not be rewritten");
                CheckNoStaging(s.Target);
            });
        }

        private static void NewValidLegacyInvalidDoesNotMigrate(byte[] content)
        {
            TestKit.Run("migration: a valid new install plus an invalid legacy install does not migrate", () =>
            {
                Scenario s = NewScenario("migration-new-valid-legacy-invalid", content, content);
                Fixtures.CreateManagedInstall(s.Target, s.Asset, new[] { content, content }, TestArchiveSha256);
                // 旧根存在但没有 ownership 标记。
                Fixtures.CreateForeignInstallDirectory(s.Legacy, s.Asset);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.NotNeeded,
                    FfmpegLegacyMigrationReason.TargetPresent);
                CheckTreeUnchanged("legacy install", s.LegacyDirectory, legacyBefore);
            });
        }

        private static void NewInvalidLegacyValidIsBlockedFailClosed(byte[] content)
        {
            TestKit.Run("migration: an invalid new target blocks migration (fail-closed)", () =>
            {
                Scenario s = NewScenario("migration-blocked", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);

                // 新目标目录存在但不属于 Renderist：绝不覆盖、绝不回退。
                string targetDirectory = s.TargetDirectory;
                Directory.CreateDirectory(targetDirectory);
                string foreignFile = Path.Combine(targetDirectory, "someone-elses.txt");
                File.WriteAllText(foreignFile, "not ours");
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Blocked,
                    FfmpegLegacyMigrationReason.TargetUnowned);
                TestKit.Check(File.Exists(foreignFile), "the foreign target content must be preserved");
                TestKit.Check(!Directory.Exists(Path.Combine(targetDirectory, "bin")),
                    "nothing may be written into the invalid target");
                CheckTreeUnchanged("legacy install", s.LegacyDirectory, legacyBefore);
                CheckNoStaging(s.Target);

                // 迁移失败后 inspector 仍只认识新托管根：绝不 fallback 到旧 LocalLow 二进制。
                FfmpegComponentReport report = InspectNewRootOnly(s, false);
                TestKit.CheckEqual(FfmpegComponentState.NotFound, report.State,
                    "the inspector must report NotFound from the new root");
                TestKit.Check(!report.Found, "no candidate may be discovered");
                TestKit.CheckEqual(FfmpegCandidateSource.None, report.Source,
                    "the legacy root must never be used as a discovery source");
                TestKit.Check(report.ManagedInstalls.Count == 1 && !report.ManagedInstalls[0].IsValid,
                    "the unowned target must be reported as an invalid managed install");
            });
        }

        private static void TargetWithMarkerButBrokenContentIsNotMigrated(byte[] content)
        {
            TestKit.Run("migration: a damaged target that still carries our marker is never overwritten", () =>
            {
                Scenario s = NewScenario("migration-target-damaged", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);

                // 目标目录带我们的 marker，但内容被外部破坏：仍然不迁移、不覆盖、不 fallback。
                Directory.CreateDirectory(s.TargetDirectory);
                string markerError;
                TestKit.Check(FfmpegOwnershipMarker.TryWrite(
                        s.Target.OwnershipMarkerPath(s.TargetDirectory), s.Asset.Id, TestArchiveSha256,
                        DateTime.UtcNow, out markerError), "marker write: " + markerError);
                Fixtures.WriteFile(Path.Combine(s.TargetDirectory, "bin", "ffmpeg.exe"),
                    Fixtures.DeterministicBytes(1024, 55));

                SortedDictionary<string, string> targetBefore = SnapshotTree(s.TargetDirectory);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.NotNeeded,
                    FfmpegLegacyMigrationReason.TargetPresent);
                CheckTreeUnchanged("damaged target", s.TargetDirectory, targetBefore);
                CheckTreeUnchanged("legacy install", s.LegacyDirectory, legacyBefore);
                CheckNoStaging(s.Target);

                FfmpegComponentReport report = InspectNewRootOnly(s, false);
                TestKit.Check(!report.Found,
                    "the damaged target must not be adopted");
                TestKit.Check(report.ManagedInstalls.Count == 1 && !report.ManagedInstalls[0].IsValid,
                    "the damaged target must be reported as invalid with its own reason");
                TestKit.CheckNotEmpty(report.ManagedInstalls[0].InvalidReason, "invalid reason");
            });
        }

        // ==================== 旧根 ownership / eligibility ====================

        private static void LegacyMarkerMissingIsNotMigrated(byte[] content)
        {
            TestKit.Run("migration: a legacy install without an ownership marker is never migrated or deleted", () =>
            {
                Scenario s = NewScenario("migration-no-marker", content, content);
                string legacyDirectory = s.LegacyDirectory;
                Directory.CreateDirectory(Path.Combine(legacyDirectory, "bin"));
                Fixtures.WriteFile(Path.Combine(legacyDirectory, "bin", "ffmpeg.exe"), content);
                Fixtures.WriteFile(Path.Combine(legacyDirectory, "bin", "ffprobe.exe"), content);
                SortedDictionary<string, string> before = SnapshotTree(legacyDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyUnowned);
                CheckTreeUnchanged("unowned legacy directory", legacyDirectory, before);
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
                CheckNoStaging(s.Target);
            });
        }

        private static void LegacyMarkerMalformedIsNotMigrated(byte[] content)
        {
            TestKit.Run("migration: a malformed legacy marker is not migrated", () =>
            {
                Scenario s = NewScenario("migration-marker-malformed", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);
                Fixtures.WriteRawMarker(s.Legacy, s.Asset, "schemaVersion=not-a-number\nassetId=x\n");
                SortedDictionary<string, string> before = SnapshotTree(s.LegacyDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyMarkerUnreadable);
                CheckTreeUnchanged("legacy install", s.LegacyDirectory, before);
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
            });

            TestKit.Run("migration: a legacy marker with an unsupported schema version is not migrated", () =>
            {
                Scenario s = NewScenario("migration-marker-schema", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);
                Fixtures.WriteRawMarker(s.Legacy, s.Asset,
                    "schemaVersion=99\nassetId=" + Fixtures.TestAssetId + "\narchiveSha256=" + TestArchiveSha256 + "\n");

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyMarkerUnreadable);
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
            });
        }

        private static void LegacyMarkerAssetMismatchIsNotMigrated(byte[] content)
        {
            TestKit.Run("migration: a legacy marker with a different assetId is not migrated", () =>
            {
                Scenario s = NewScenario("migration-marker-asset", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);
                Fixtures.WriteRawMarker(s.Legacy, s.Asset,
                    "schemaVersion=1\nassetId=some-other-asset\narchiveSha256=" + TestArchiveSha256 + "\n");

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyAssetMismatch);
                TestKit.Check(Directory.Exists(s.LegacyDirectory), "the legacy directory must be preserved");
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
            });
        }

        private static void LegacyMarkerArchiveMismatchIsNotMigrated(byte[] content)
        {
            TestKit.Run("migration: a legacy marker with a different archiveSha256 is not migrated", () =>
            {
                Scenario s = NewScenario("migration-marker-archive", content, content);
                // 文件哈希与 assetId 都正确，只有归档哈希与本轮 manifest 不一致。
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, OtherArchiveSha256);

                FfmpegManagedInstall legacy = FfmpegManagedInstallLocator.Locate(s.Legacy, s.Asset);
                TestKit.Check(legacy.IsValid,
                    "the legacy install itself must still be structurally valid: " + legacy.InvalidReason);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyArchiveMismatch);
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
                CheckNoStaging(s.Target);
            });
        }

        private static void LegacyFfmpegHashMismatchIsNotMigrated(byte[] content)
        {
            TestKit.Run("migration: a legacy ffmpeg.exe hash mismatch is not migrated", () =>
            {
                Scenario s = NewScenario("migration-ffmpeg-hash", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);
                // 复制完成后篡改 ffmpeg.exe：内容哈希不再匹配 manifest。
                Fixtures.WriteFile(Path.Combine(s.LegacyDirectory, "bin", "ffmpeg.exe"),
                    Fixtures.DeterministicBytes(4096, 99));
                SortedDictionary<string, string> before = SnapshotTree(s.LegacyDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyInstallInvalid);
                TestKit.Check(result.ReasonDetail != null &&
                              result.ReasonDetail.Contains("bin/ffmpeg.exe"),
                    "the reason detail must name ffmpeg.exe, actual: " + result.ReasonDetail);
                CheckTreeUnchanged("legacy install", s.LegacyDirectory, before);
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
            });
        }

        private static void LegacyFfprobeHashMismatchIsNotMigrated(byte[] content)
        {
            TestKit.Run("migration: a legacy ffprobe.exe hash mismatch is not migrated", () =>
            {
                Scenario s = NewScenario("migration-ffprobe-hash", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);
                Fixtures.WriteFile(Path.Combine(s.LegacyDirectory, "bin", "ffprobe.exe"),
                    Fixtures.DeterministicBytes(2048, 98));

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyInstallInvalid);
                TestKit.Check(result.ReasonDetail != null &&
                              result.ReasonDetail.Contains("bin/ffprobe.exe"),
                    "the reason detail must name ffprobe.exe, actual: " + result.ReasonDetail);
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
            });
        }

        private static void ForeignLegacyDirectoryIsLeftUntouched(byte[] content)
        {
            TestKit.Run("migration: a non-Renderist legacy directory is never migrated or deleted", () =>
            {
                Scenario s = NewScenario("migration-foreign-legacy", content, content);
                string legacyDirectory = Fixtures.CreateForeignInstallDirectory(s.Legacy, s.Asset);
                Fixtures.WriteFile(Path.Combine(legacyDirectory, "bin", "ffmpeg.exe"), content);
                SortedDictionary<string, string> before = SnapshotTree(s.LegacyRoot);

                FfmpegLegacyMigrationResult result = Migrate(s);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyUnowned);
                CheckTreeUnchanged("foreign legacy root", s.LegacyRoot, before);
                TestKit.Check(!Directory.Exists(s.TargetRoot), "the target root must not be created");
            });
        }

        private static void OnlyManifestDeclaredFilesAreCopied(byte[] fakeExe)
        {
            TestKit.Run("migration: only manifest-declared files are copied into the new install", () =>
            {
                Scenario s = NewScenario("migration-whitelist", fakeExe, fakeExe);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { fakeExe, fakeExe }, TestArchiveSha256);
                Fixtures.WriteFile(Path.Combine(s.LegacyDirectory, "extras", "notes.txt"),
                    Encoding.UTF8.GetBytes("not part of the managed install"));
                Fixtures.WriteFile(Path.Combine(s.LegacyDirectory, "unrelated.exe"), new byte[] { 7, 7, 7 });

                FfmpegLegacyMigrationResult result = Migrate(s, true);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Migrated,
                    FfmpegLegacyMigrationReason.Migrated);
                TestKit.Check(!File.Exists(Path.Combine(s.TargetDirectory, "extras", "notes.txt")),
                    "unknown files from the legacy root must not be copied");
                TestKit.Check(!File.Exists(Path.Combine(s.TargetDirectory, "unrelated.exe")),
                    "unknown files from the legacy root must not be copied");
                TestKit.Check(File.Exists(Path.Combine(s.TargetDirectory, ".renderist-ffmpeg-owner")),
                    "the new ownership marker must exist");
                TestKit.Check(File.Exists(Path.Combine(s.TargetDirectory, "bin", "ffmpeg.exe")),
                    "ffmpeg.exe must be copied");
                TestKit.Check(File.Exists(Path.Combine(s.TargetDirectory, "bin", "ffprobe.exe")),
                    "ffprobe.exe must be copied");
            });
        }

        // ==================== 迁移执行 ====================

        private static void UpgradeMigratesTheLegacyInstall(byte[] fakeExe)
        {
            TestKit.Run("migration: upgrade copies a valid legacy install into the new root", () =>
            {
                Scenario s = NewScenario("migration-upgrade", fakeExe, fakeExe);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { fakeExe, fakeExe }, TestArchiveSha256);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyRoot);

                FfmpegLegacyMigrationResult result = Migrate(s, true);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Migrated,
                    FfmpegLegacyMigrationReason.Migrated);
                TestKit.CheckEqual(s.TargetDirectory, result.TargetDirectory, "published target directory");

                // destination 必须重新被既有 Locator 判为 valid。
                FfmpegManagedInstall published = FfmpegManagedInstallLocator.Locate(s.Target, s.Asset);
                TestKit.Check(published.IsValid,
                    "the migrated install must be valid: " + published.InvalidReason);
                TestKit.Check(published.Marker != null &&
                              string.Equals(published.Marker.AssetId, s.Asset.Id, StringComparison.Ordinal),
                    "the new marker must carry the asset id");
                TestKit.Check(published.Marker != null &&
                              string.Equals(published.Marker.ArchiveSha256, TestArchiveSha256,
                                  StringComparison.OrdinalIgnoreCase),
                    "the new marker must carry the manifest archive hash");
                TestKit.CheckEqual(FfmpegOwnershipMarker.CurrentSchemaVersion, published.Marker.SchemaVersion,
                    "the new marker schema version");

                CheckTreeUnchanged("legacy root", s.LegacyRoot, legacyBefore);
                TestKit.Check(!Directory.Exists(s.LegacyDirectory) == false,
                    "the legacy install must be preserved");
                CheckNoStaging(s.Target);
            });
        }

        private static void StagingFailureLeavesNoTarget(byte[] content)
        {
            TestKit.Run("migration: a failing staging step leaves no target and deletes nothing foreign", () =>
            {
                Scenario s = NewScenario("migration-staging-failure", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyDirectory);

                // 在 staging 根位置放一个**外来文件**：创建 staging 必然失败。
                Directory.CreateDirectory(s.TargetRoot);
                string blockingFile = Path.Combine(s.TargetRoot, FfmpegInstallLayout.StagingDirectoryName);
                File.WriteAllText(blockingFile, "not ours");

                FfmpegLegacyMigrationResult result = Migrate(s, false);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Failed,
                    FfmpegLegacyMigrationReason.MigrationFailed);
                TestKit.Check(File.Exists(blockingFile),
                    "a foreign file at the staging path must never be deleted");
                TestKit.CheckEqual("not ours", File.ReadAllText(blockingFile),
                    "the foreign file content must be untouched");
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
                CheckTreeUnchanged("legacy install", s.LegacyDirectory, legacyBefore);
            });
        }

        private static void CapabilityProbeFailureIsNotPublished(byte[] notExecutable)
        {
            TestKit.Run("migration: a capability probe failure is not published", () =>
            {
                Scenario s = NewScenario("migration-probe-failure", notExecutable, notExecutable);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { notExecutable, notExecutable },
                    TestArchiveSha256);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyDirectory);

                FfmpegLegacyMigrationResult result = Migrate(s, true);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Failed,
                    FfmpegLegacyMigrationReason.CapabilityProbeFailed);
                TestKit.Check(!Directory.Exists(s.TargetDirectory),
                    "an unproven copy must not be published");
                CheckNoStaging(s.Target);
                CheckTreeUnchanged("legacy install", s.LegacyDirectory, legacyBefore);
            });
        }

        private static void RepeatedMigrationIsIdempotent(byte[] fakeExe)
        {
            TestKit.Run("migration: repeated migration is idempotent and does not re-copy", () =>
            {
                Scenario s = NewScenario("migration-idempotent", fakeExe, fakeExe);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { fakeExe, fakeExe }, TestArchiveSha256);

                FfmpegLegacyMigrationResult first = Migrate(s, true);
                CheckOutcome(first, FfmpegLegacyMigrationOutcome.Migrated,
                    FfmpegLegacyMigrationReason.Migrated);

                string markerAfterFirst = MarkerText(s.Target);
                DateTime publishedUtc = Directory.GetLastWriteTimeUtc(s.TargetDirectory);
                SortedDictionary<string, string> targetAfterFirst = SnapshotTree(s.TargetDirectory);

                FfmpegLegacyMigrationResult second = Migrate(s, true);
                CheckOutcome(second, FfmpegLegacyMigrationOutcome.NotNeeded,
                    FfmpegLegacyMigrationReason.TargetPresent);

                TestKit.CheckEqual(markerAfterFirst, MarkerText(s.Target),
                    "the marker must not be rewritten by a second migration");
                TestKit.CheckEqual(publishedUtc, Directory.GetLastWriteTimeUtc(s.TargetDirectory),
                    "the published install must not be touched by a second migration");
                CheckTreeUnchanged("published install", s.TargetDirectory, targetAfterFirst);
                CheckNoStaging(s.Target);
            });
        }

        private static void SameRootIsNeverMigrated(byte[] content)
        {
            TestKit.Run("migration: an identical legacy and target root is never migrated", () =>
            {
                Scenario s = NewScenario("migration-same-root", content, content);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { content, content }, TestArchiveSha256);
                SortedDictionary<string, string> before = SnapshotTree(s.LegacyRoot);

                FfmpegLegacyMigrationResult result = FfmpegLegacyRootMigration.Migrate(
                    new FfmpegLegacyMigrationRequest
                    {
                        TargetLayout = s.Legacy,
                        LegacyLayout = s.Legacy,
                        Asset = s.Asset,
                    }, CancellationToken.None);

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyRootEqualsTarget);
                CheckTreeUnchanged("legacy root", s.LegacyRoot, before);
            });
        }

        private static void NullInputsAreHandled(byte[] content)
        {
            TestKit.Run("migration: missing layouts and a null request are handled without side effects", () =>
            {
                Scenario s = NewScenario("migration-null-inputs", content, content);

                FfmpegLegacyMigrationResult nullRequest =
                    FfmpegLegacyRootMigration.Migrate(null, CancellationToken.None);
                CheckOutcome(nullRequest, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.RequestNull);

                FfmpegLegacyMigrationResult noTarget = FfmpegLegacyRootMigration.Migrate(
                    new FfmpegLegacyMigrationRequest { LegacyLayout = s.Legacy, Asset = s.Asset },
                    CancellationToken.None);
                CheckOutcome(noTarget, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.InstallRootUnavailable);

                FfmpegLegacyMigrationResult noLegacy = FfmpegLegacyRootMigration.Migrate(
                    new FfmpegLegacyMigrationRequest { TargetLayout = s.Target, Asset = s.Asset },
                    CancellationToken.None);
                CheckOutcome(noLegacy, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyRootUnavailable);

                TestKit.Check(!Directory.Exists(s.TargetRoot) && !Directory.Exists(s.LegacyRoot),
                    "no directory may be created for invalid requests");
            });
        }

        private static void CancellationDuringCopyCleansOnlyItsOwnStaging()
        {
            TestKit.Run("migration: cancellation during the copy removes only its own staging", () =>
            {
                byte[] big = Fixtures.DeterministicBytes(48 * 1024 * 1024, 31);
                byte[] small = Fixtures.DeterministicBytes(4096, 32);
                Scenario s = NewScenario("migration-cancel", big, small);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { big, small }, TestArchiveSha256);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyRoot);

                var cancellation = new CancellationTokenSource();
                FfmpegLegacyMigrationResult result = null;
                Task task = Task.Run(() => { result = Migrate(s, false, cancellation.Token); });

                TestKit.Check(WaitForStaging(s.Target, 60000),
                    "the staging directory must appear before cancellation");
                cancellation.Cancel();
                TestKit.Check(task.Wait(180000), "the migration task must settle");

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Cancelled,
                    FfmpegLegacyMigrationReason.Cancelled);
                TestKit.Check(!Directory.Exists(s.TargetDirectory), "no target may be created");
                CheckNoStaging(s.Target);
                CheckTreeUnchanged("legacy root", s.LegacyRoot, legacyBefore);
            });
        }

        private static void TargetAppearingBeforePublicationIsNeverOverwritten()
        {
            TestKit.Run("migration: a target that appears before publication is never overwritten", () =>
            {
                byte[] big = Fixtures.DeterministicBytes(48 * 1024 * 1024, 41);
                byte[] small = Fixtures.DeterministicBytes(4096, 42);
                Scenario s = NewScenario("migration-target-appeared", big, small);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { big, small }, TestArchiveSha256);
                SortedDictionary<string, string> legacyBefore = SnapshotTree(s.LegacyRoot);

                FfmpegLegacyMigrationResult result = null;
                Task task = Task.Run(() => { result = Migrate(s, false); });

                TestKit.Check(WaitForStaging(s.Target, 60000),
                    "the staging directory must appear before we publish a competing target");
                Directory.CreateDirectory(s.TargetDirectory);
                string sentinel = Path.Combine(s.TargetDirectory, "someone-else.txt");
                File.WriteAllText(sentinel, "not ours");

                TestKit.Check(task.Wait(180000), "the migration task must settle");

                CheckOutcome(result, FfmpegLegacyMigrationOutcome.Failed,
                    FfmpegLegacyMigrationReason.TargetAppeared);
                TestKit.Check(File.Exists(sentinel), "the competing target must be preserved");
                TestKit.Check(!Directory.Exists(Path.Combine(s.TargetDirectory, "bin")),
                    "nothing may be written into the competing target");
                CheckNoStaging(s.Target);
                CheckTreeUnchanged("legacy root", s.LegacyRoot, legacyBefore);
            });
        }

        // ==================== 接线与不回归 ====================

        private static void InitialStartupReadinessIsReadyAfterMigration(byte[] fakeExe)
        {
            TestKit.Run("migration: initial startup readiness is Ready after an upgrade migration", () =>
            {
                Scenario s = NewScenario("migration-readiness", fakeExe, fakeExe);
                Fixtures.CreateManagedInstall(s.Legacy, s.Asset, new[] { fakeExe, fakeExe }, TestArchiveSha256);

                // 与 ModEntry 相同的顺序：主线程冻结输入与 generation → 后台 migration → inspect → 同代发布。
                var tracker = new FfmpegReadinessTracker();
                int generation;
                TestKit.Check(tracker.TryBeginScan(out generation), "the initial scan must start");
                TestKit.CheckEqual(0, generation, "the initial generation");

                string targetRoot = s.TargetRoot;
                FfmpegLegacyMigrationResult migration = null;
                Task<FfmpegComponentReport> inspection = Task.Run(() =>
                {
                    migration = FfmpegLegacyRootMigration.Migrate(
                        new FfmpegLegacyMigrationRequest
                        {
                            TargetLayout = s.Target,
                            LegacyLayout = s.Legacy,
                            Asset = s.Asset,
                            ProbeCapabilities = true,
                        }, CancellationToken.None);

                    return FfmpegComponentInspector.Inspect(new FfmpegComponentInspectionRequest
                    {
                        InstallRoot = targetRoot,
                        ProbeCapabilities = true,
                        PathEnvironment = string.Empty,
                        // 合成资产注入：生产调用为 null（= 真实 manifest），语义完全相同。
                        Assets = new[] { s.Asset },
                    });
                });

                TestKit.Check(inspection.Wait(180000), "the inspection task must settle");
                CheckOutcome(migration, FfmpegLegacyMigrationOutcome.Migrated,
                    FfmpegLegacyMigrationReason.Migrated);

                FfmpegComponentReport report = inspection.Result;
                TestKit.CheckEqual(FfmpegComponentState.Ready, report.State,
                    "the migrated install must be Ready (" + report.DiscoveryErrorCode + ")");
                TestKit.CheckEqual(FfmpegCandidateSource.ManagedInstall, report.Source,
                    "the candidate must come from the new managed root");

                TestKit.Check(tracker.TryPublishScan(generation, report),
                    "the same generation must accept the result");
                FfmpegReadinessSnapshot snapshot = tracker.Snapshot();
                TestKit.CheckEqual(FfmpegReadinessState.Ready, snapshot.State,
                    "readiness must be Ready (" + snapshot.ReasonCode + " " + snapshot.ReasonDetail + ")");
                TestKit.CheckEqual(FfmpegReadinessReason.None, snapshot.ReasonCode, "readiness reason");
                TestKit.CheckEqual(generation, snapshot.Generation,
                    "the result must stay bound to the frozen generation");
                TestKit.Check(!snapshot.ScanInFlight, "no scan may remain in flight");
            });
        }

        private static void SkippedMigrationDoesNotRegressDiscovery(byte[] fakeExe)
        {
            TestKit.Run("migration: a skipped migration does not regress explicit-path or PATH discovery", () =>
            {
                Scenario s = NewScenario("migration-discovery", fakeExe, fakeExe);
                Fixtures.CreateForeignInstallDirectory(s.Legacy, s.Asset);

                FfmpegLegacyMigrationResult migration = Migrate(s);
                CheckOutcome(migration, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyUnowned);

                string externalDirectory = Path.Combine(s.Work, "external");
                Directory.CreateDirectory(externalDirectory);
                string externalExecutable = Path.Combine(externalDirectory, "ffmpeg.exe");
                File.Copy(FakeFfmpeg.ExecutablePath, externalExecutable);

                FfmpegComponentReport explicitReport = FfmpegComponentInspector.Inspect(
                    new FfmpegComponentInspectionRequest
                    {
                        ExplicitPath = externalExecutable,
                        InstallRoot = s.TargetRoot,
                        ProbeCapabilities = true,
                        PathEnvironment = string.Empty,
                    });
                TestKit.CheckEqual(FfmpegCandidateSource.ExplicitPath, explicitReport.Source,
                    "an explicit path must still win");
                TestKit.CheckEqual(FfmpegComponentState.Ready, explicitReport.State,
                    "an explicit path must still reach Ready");

                FfmpegComponentReport invalidExplicit = FfmpegComponentInspector.Inspect(
                    new FfmpegComponentInspectionRequest
                    {
                        ExplicitPath = Path.Combine(externalDirectory, "missing.exe"),
                        InstallRoot = s.TargetRoot,
                        PathEnvironment = string.Empty,
                    });
                TestKit.CheckEqual(FfmpegComponentState.Invalid, invalidExplicit.State,
                    "an invalid explicit path must stay fail-closed (no silent fallback)");

                FfmpegComponentReport pathReport = FfmpegComponentInspector.Inspect(
                    new FfmpegComponentInspectionRequest
                    {
                        InstallRoot = s.TargetRoot,
                        PathEnvironment = externalDirectory,
                    });
                TestKit.CheckEqual(FfmpegCandidateSource.SystemPath, pathReport.Source,
                    "PATH discovery must still work");
                TestKit.CheckEqual(FfmpegComponentState.Discovered, pathReport.State,
                    "PATH discovery without probing must stay Discovered");

                TestKit.Check(!Directory.Exists(s.TargetDirectory),
                    "the skipped migration must not create a managed install");
            });
        }

        private static void OutputModesAreNotBlockedByMigrationFailure(byte[] fakeExe)
        {
            TestKit.Run("migration: PNG and Log-only output modes are unaffected by a failed migration", () =>
            {
                Scenario s = NewScenario("migration-output-modes", fakeExe, fakeExe);
                Fixtures.CreateForeignInstallDirectory(s.Legacy, s.Asset);

                FfmpegLegacyMigrationResult migration = Migrate(s);
                CheckOutcome(migration, FfmpegLegacyMigrationOutcome.Skipped,
                    FfmpegLegacyMigrationReason.LegacyUnowned);

                // 迁移被跳过、组件因此不可用：readiness 只能是信息性的失败态……
                FfmpegComponentReport report = InspectNewRootOnly(s, false);
                TestKit.CheckEqual(FfmpegComponentState.NotFound, report.State,
                    "the component stays unavailable after a skipped migration");

                // ……而 PNG / Log-only 的输出模式判定完全不参考 FFmpeg readiness。
                CaptureOutputMode pngMode;
                bool pngMigrated;
                string pngError;
                TestKit.Check(OutputModePolicy.TryResolve(
                        (int)CaptureOutputMode.PngSequence, true, out pngMode, out pngMigrated, out pngError),
                    "PNG output mode must still resolve: " + pngError);
                TestKit.CheckEqual(CaptureOutputMode.PngSequence, pngMode, "PNG output mode");

                CaptureOutputMode logMode;
                bool logMigrated;
                string logError;
                TestKit.Check(OutputModePolicy.TryResolve(
                        (int)CaptureOutputMode.LogOnly, false, out logMode, out logMigrated, out logError),
                    "Log-only output mode must still resolve: " + logError);
                TestKit.CheckEqual(CaptureOutputMode.LogOnly, logMode, "Log-only output mode");
            });
        }

        // ==================== helpers ====================

        /// <summary>
        /// 真实 fixture 的完整升级路径：真实 manifest 资产 + 与钉死哈希一致的真实二进制。
        ///
        /// 只有本机确实存在该 fixture 时才运行，否则 SKIP（二进制刻意不入库）。它给出的是
        /// **离线**环境里最强的升级证据（旧根安装 → 迁移 → Locator 复核 → inspector Ready →
        /// 同 generation readiness Ready），但仍然**不能**代替游戏内的真实升级实机验收。
        /// </summary>
        private static void RealPinnedFixtureUpgradeIsReady()
        {
            TestKit.Run("migration: a real pinned FFmpeg fixture upgrades into a Ready install", () =>
            {
                string fixtureFfmpeg = FindPinnedFixture();
                if (fixtureFfmpeg == null)
                {
                    throw new SkipTestException(
                        "no local FFmpeg fixture matching the pinned manifest; set RENDERIST_TEST_FFMPEG_DIR to a " +
                        "directory containing the pinned ffmpeg.exe and ffprobe.exe (binaries are intentionally " +
                        "not committed)");
                }

                FfmpegAsset asset = FfmpegAssetManifest.Primary;
                Scenario s = NewScenarioWithAsset("migration-real-fixture", asset);

                string legacyDirectory = s.LegacyDirectory;
                Directory.CreateDirectory(Path.Combine(legacyDirectory, "bin"));
                File.Copy(fixtureFfmpeg, Path.Combine(legacyDirectory, "bin", "ffmpeg.exe"));
                File.Copy(Path.Combine(Path.GetDirectoryName(fixtureFfmpeg), "ffprobe.exe"),
                    Path.Combine(legacyDirectory, "bin", "ffprobe.exe"));

                string markerError;
                if (!FfmpegOwnershipMarker.TryWrite(s.Legacy.OwnershipMarkerPath(legacyDirectory), asset.Id,
                        asset.ArchiveSha256, DateTime.UtcNow, out markerError))
                {
                    throw new Exception("failed to write the legacy fixture marker: " + markerError);
                }

                FfmpegManagedInstall legacyInstall = FfmpegManagedInstallLocator.Locate(s.Legacy, asset);
                if (!legacyInstall.IsValid)
                {
                    throw new SkipTestException(
                        "the configured FFmpeg fixture does not match the pinned manifest hashes: " +
                        legacyInstall.InvalidReason);
                }

                // 与 ModEntry 相同的顺序：冻结 generation → 迁移 → 新根 inspection → 同代发布。
                var tracker = new FfmpegReadinessTracker();
                int generation;
                TestKit.Check(tracker.TryBeginScan(out generation), "the initial scan must start");

                FfmpegLegacyMigrationResult migration = Migrate(s, true);
                CheckOutcome(migration, FfmpegLegacyMigrationOutcome.Migrated,
                    FfmpegLegacyMigrationReason.Migrated);

                FfmpegComponentReport report = FfmpegComponentInspector.Inspect(
                    new FfmpegComponentInspectionRequest
                    {
                        InstallRoot = s.TargetRoot,
                        ProbeCapabilities = true,
                        PathEnvironment = string.Empty,
                    });

                TestKit.CheckEqual(FfmpegComponentState.Ready, report.State,
                    "the migrated real install must be Ready (" + report.DiscoveryErrorCode + ")");
                TestKit.CheckEqual(FfmpegCandidateSource.ManagedInstall, report.Source,
                    "the candidate must come from the new managed root");
                TestKit.Check(report.ExecutablePath != null &&
                              report.ExecutablePath.StartsWith(s.TargetRoot, StringComparison.OrdinalIgnoreCase),
                    "the executable must be the migrated copy, actual: " + report.ExecutablePath);

                TestKit.Check(tracker.TryPublishScan(generation, report), "the result must be published");
                TestKit.CheckEqual(FfmpegReadinessState.Ready, tracker.Snapshot().State,
                    "readiness after the real upgrade migration");

                TestKit.Check(Directory.Exists(s.LegacyDirectory),
                    "the legacy install must be preserved");
                CheckNoStaging(s.Target);
            });
        }

        private static string FindPinnedFixture()
        {
            FfmpegAsset asset = FfmpegAssetManifest.Primary;
            string expectedFfmpeg = null;
            string expectedFfprobe = null;
            for (int i = 0; i < asset.Files.Count; i++)
            {
                if (string.Equals(asset.Files[i].RelativePath, "bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    expectedFfmpeg = asset.Files[i].Sha256;
                else if (string.Equals(asset.Files[i].RelativePath, "bin/ffprobe.exe",
                             StringComparison.OrdinalIgnoreCase))
                    expectedFfprobe = asset.Files[i].Sha256;
            }

            IReadOnlyList<string> candidates = Program.FindLocalFfmpegBinaries();
            for (int i = 0; i < candidates.Count; i++)
            {
                string directory = Path.GetDirectoryName(candidates[i]);
                if (string.IsNullOrEmpty(directory))
                    continue;

                string ffprobe = Path.Combine(directory, "ffprobe.exe");
                if (!File.Exists(ffprobe))
                    continue;

                if (HashMatches(candidates[i], expectedFfmpeg) && HashMatches(ffprobe, expectedFfprobe))
                    return candidates[i];
            }

            return null;
        }

        private static bool HashMatches(string path, string expectedSha256)
        {
            if (string.IsNullOrEmpty(expectedSha256))
                return false;

            string actual;
            string error;
            return FfmpegFileHash.TryCompute(path, out actual, out error) &&
                   FfmpegFileHash.Matches(expectedSha256, actual);
        }

        private static Scenario NewScenario(string label, byte[] ffmpegBytes, byte[] ffprobeBytes)
        {
            return NewScenarioWithAsset(label, BuildAsset(ffmpegBytes, ffprobeBytes));
        }

        private static Scenario NewScenarioWithAsset(string label, FfmpegAsset asset)
        {
            string work = TestKit.NewWorkDirectory(_workRoot, label);

            FfmpegInstallLayout target;
            string targetError;
            if (!FfmpegInstallLayout.TryCreate(Path.Combine(work, "target"), out target, out targetError))
                throw new Exception("target layout: " + targetError);

            FfmpegInstallLayout legacy;
            string legacyError;
            if (!FfmpegInstallLayout.TryCreate(Path.Combine(work, "legacy"), out legacy, out legacyError))
                throw new Exception("legacy layout: " + legacyError);

            return new Scenario
            {
                Work = work,
                Target = target,
                Legacy = legacy,
                Asset = asset,
            };
        }

        private static FfmpegAsset BuildAsset(byte[] ffmpegBytes, byte[] ffprobeBytes)
        {
            return new FfmpegAsset(
                Fixtures.TestAssetId,
                Fixtures.TestAssetVersion,
                "migration test asset",
                "https://example.invalid/migration.zip",
                TestArchiveSha256,
                4096,
                "GPLv3",
                "https://example.invalid/license",
                "https://example.invalid/source",
                "synthetic fixture",
                "bin/ffmpeg.exe",
                new[]
                {
                    new FfmpegAssetFile("ffmpeg.exe", "bin/ffmpeg.exe", Fixtures.Sha256Of(ffmpegBytes), true),
                    new FfmpegAssetFile("ffprobe.exe", "bin/ffprobe.exe", Fixtures.Sha256Of(ffprobeBytes), true),
                });
        }

        private static FfmpegLegacyMigrationResult Migrate(
            Scenario scenario, bool probe = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            return FfmpegLegacyRootMigration.Migrate(
                new FfmpegLegacyMigrationRequest
                {
                    TargetLayout = scenario.Target,
                    LegacyLayout = scenario.Legacy,
                    Asset = scenario.Asset,
                    ProbeCapabilities = probe,
                }, cancellationToken);
        }

        private static FfmpegComponentReport InspectNewRootOnly(Scenario scenario, bool probe)
        {
            // 注入合成资产，使断言真的针对被测安装，而不是"真实 manifest 里没有这个测试资产"这种平凡结论。
            return FfmpegComponentInspector.Inspect(new FfmpegComponentInspectionRequest
            {
                InstallRoot = scenario.TargetRoot,
                ProbeCapabilities = probe,
                PathEnvironment = string.Empty,
                Assets = new[] { scenario.Asset },
            });
        }

        private static void CheckOutcome(
            FfmpegLegacyMigrationResult result, FfmpegLegacyMigrationOutcome outcome, string reasonCode)
        {
            TestKit.Check(result != null, "the migration must produce a result");
            if (result.Outcome != outcome || !string.Equals(result.ReasonCode, reasonCode, StringComparison.Ordinal))
            {
                throw new Exception("outcome mismatch: expected <" + outcome + "/" + reasonCode + "> actual <" +
                                    result.Outcome + "/" + result.ReasonCode + "> detail=" + result.ReasonDetail);
            }
        }

        private static void CheckNoStaging(FfmpegInstallLayout layout)
        {
            TestKit.CheckEqual(0, Fixtures.CountStagingDirectories(layout),
                "no staging directory may be left behind");
        }

        private static string MarkerText(FfmpegInstallLayout layout)
        {
            string directory = layout.VersionDirectory(Fixtures.TestAssetId, Fixtures.TestAssetVersion);
            return File.ReadAllText(layout.OwnershipMarkerPath(directory), Encoding.UTF8);
        }

        private static SortedDictionary<string, string> SnapshotTree(string root)
        {
            var snapshot = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(root))
                return snapshot;

            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string relative = files[i].Substring(root.Length).TrimStart('\\', '/');
                string sha256;
                string error;
                if (!FfmpegFileHash.TryCompute(files[i], out sha256, out error))
                    sha256 = "unreadable:" + error;

                snapshot[relative] = sha256;
            }

            return snapshot;
        }

        private static void CheckTreeUnchanged(
            string what, string root, SortedDictionary<string, string> before)
        {
            SortedDictionary<string, string> after = SnapshotTree(root);
            TestKit.CheckEqual(before.Count, after.Count, what + ": file count must not change");
            foreach (KeyValuePair<string, string> entry in before)
            {
                TestKit.Check(after.ContainsKey(entry.Key), what + ": file must still exist: " + entry.Key);
                TestKit.CheckEqual(entry.Value, after[entry.Key],
                    what + ": content must not change: " + entry.Key);
            }
        }

        private static bool WaitForStaging(FfmpegInstallLayout layout, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (Directory.Exists(layout.StagingRoot) &&
                        Directory.GetDirectories(layout.StagingRoot).Length > 0)
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    // 目录正在被创建：继续轮询。
                }

                Thread.Sleep(1);
            }

            return false;
        }
    }
}
