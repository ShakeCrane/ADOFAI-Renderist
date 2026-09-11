using System.Collections.Generic;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器确定性导出就绪检查（Phase 3.4.0）。
    /// 完全无副作用：不创建目录、不写文件、不执行 Harmony Patch、不改 Unity 时间属性。
    /// </summary>
    internal static class EditorExportPreflight
    {
        public static EditorExportReadinessReport Run()
        {
            Settings settings = ModEntry.Settings;
            if (settings == null)
            {
                return CreateReport(EditorExportReadiness.UnknownEnvironment,
                    EditorExportReadinessReason.EnvironmentUnavailable);
            }

            EditorEnvSnapshot env = EditorEnvSnapshot.Capture();
            DirectoryValidationResult dirResult = OutputPath.ValidateDirectory(settings.OutputDirectory);
            int targetFrameRate = settings.EditorTargetFrameRate;

            if (!settings.EditorExportEnabled)
            {
                return CreateReport(EditorExportReadiness.Disabled, EditorExportReadinessReason.FeatureDisabled,
                    env, dirResult, targetFrameRate);
            }

            if (env.EnvironmentReadFailed || string.IsNullOrWhiteSpace(env.SceneName))
            {
                return CreateReport(EditorExportReadiness.UnknownEnvironment, EditorExportReadinessReason.EnvironmentUnavailable,
                    env, dirResult, targetFrameRate);
            }

            if (env.Detection != EditorEnvDetection.ProbablyEditor)
            {
                return CreateReport(EditorExportReadiness.NotInEditor, EditorExportReadinessReason.EditorSceneNotDetected,
                    env, dirResult, targetFrameRate);
            }

            // 初始化只读反射缓存；不修改游戏状态。
            EditorGameReflection.EnsureTypes();

            // 当前恢复路径能处理普通单选和连续 multi-select；非连续 multi-select
            // 会在 RestoreSelectedFloorSeqs 中失败，因此必须在 session 创建前 fail-closed。
            // selectedFloors 为空时不在此做推断，避免把 ADOFAI 的正常单选表示误判为不可恢复。
            if (EditorGameReflection.IsLevelLoaded() && !IsEditorSelectionRestorable())
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.UnsupportedEditorSelection,
                    env, dirResult, targetFrameRate);
            }

            if (targetFrameRate <= 0)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidTargetFrameRate,
                    env, dirResult, targetFrameRate);
            }

            var endTailInput = new EndTailInput(
                settings.EditorEndTailValue, settings.EditorEndTailUnit);
            double? completionBpm = EditorGameReflection.TryReadFinalEffectiveBpm(
                out double finalBpm, out string bpmError)
                ? finalBpm
                : (double?)null;
            double pitchValue = EditorGameReflection.ReadPitch(out bool pitchUnavailable);
            double? pitch = pitchUnavailable ? (double?)null : pitchValue;
            int safetyFrameLimit = DeterministicFrameScheduler.NormalizeSafetyFrameLimit(
                settings.EditorExportSafetyFrameLimit);

            if (!EndTailPolicy.TryResolve(
                    endTailInput,
                    targetFrameRate,
                    completionBpm,
                    pitch,
                    safetyFrameLimit,
                    out EndTailResolution endTailResolution,
                    out string endTailError))
            {
                EditorExportReadinessReason tailReason =
                    string.Equals(endTailError, "end-tail-exceeds-safety-limit", System.StringComparison.Ordinal)
                        ? EditorExportReadinessReason.EndTailExceedsSafetyLimit
                        : endTailError != null &&
                          (endTailError.Contains("bpm-unavailable") ||
                           endTailError.Contains("pitch-unavailable"))
                            ? EditorExportReadinessReason.EndTailDependenciesUnavailable
                            : EditorExportReadinessReason.InvalidEndTail;
                return CreateReport(EditorExportReadiness.Blocked, tailReason,
                    env, dirResult, targetFrameRate, endTailInput, null,
                    completionBpm, pitch, safetyFrameLimit,
                    endTailError ?? bpmError);
            }

            if (dirResult.Outcome == DirectoryValidationOutcome.Reject)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidOutputDirectory,
                    env, dirResult, targetFrameRate, endTailInput, endTailResolution,
                    completionBpm, pitch, safetyFrameLimit, null);
            }

            return CreateReport(EditorExportReadiness.Ready, EditorExportReadinessReason.None,
                env, dirResult, targetFrameRate, endTailInput, endTailResolution,
                completionBpm, pitch, safetyFrameLimit, null);
        }

        private static bool IsEditorSelectionRestorable()
        {
            var selected = new List<int>();
            EditorGameReflection.ReadSelectedFloorSeqs(selected);

            // 0/1 不足以证明存在 multi-select 恢复风险；普通路径保持兼容。
            if (selected.Count <= 1)
                return true;

            selected.Sort();
            for (int i = 1; i < selected.Count; i++)
            {
                if (selected[i] != selected[i - 1] + 1)
                    return false;
            }

            return EditorGameReflection.EditorMultiSelectFloorsMethod != null;
        }

        private static EditorExportReadinessReport CreateReport(
            EditorExportReadiness readiness,
            EditorExportReadinessReason reason,
            EditorEnvSnapshot env = default,
            DirectoryValidationResult dirResult = default,
            int targetFrameRate = 0,
            EndTailInput? endTailInput = null,
            EndTailResolution? endTailResolution = null,
            double? completionBpm = null,
            double? pitch = null,
            int safetyFrameLimit = 0,
            string endTailValidationError = null)
        {
            var report = new EditorExportReadinessReport
            {
                Readiness = readiness,
                Reason = reason,
                EditorEnv = env,
                OutputDirectoryValidation = dirResult,
                TargetFrameRate = targetFrameRate,
                EndTailInputValue = endTailInput?.Value,
                EndTailInputUnit = endTailInput?.Unit,
                ResolvedTailFrames = endTailResolution?.FrameCount,
                ResolvedTailSeconds = endTailResolution?.Seconds,
                ResolvedTailBeats = endTailResolution?.Beats,
                CompletionBpm = completionBpm,
                Pitch = pitch,
                SafetyFrameLimit = safetyFrameLimit,
                EndTailValidationError = endTailValidationError,
            };

            Log.Debug($"EditorExportPreflight: {readiness} / {reason}");
            return report;
        }
    }
}
