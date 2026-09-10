using System.Collections.Generic;
using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器确定性导出就绪检查（Phase 3.3.0）。
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

            if (dirResult.Outcome == DirectoryValidationOutcome.Reject)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidOutputDirectory,
                    env, dirResult, targetFrameRate);
            }

            return CreateReport(EditorExportReadiness.Ready, EditorExportReadinessReason.None,
                env, dirResult, targetFrameRate);
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
            int targetFrameRate = 0)
        {
            var report = new EditorExportReadinessReport
            {
                Readiness = readiness,
                Reason = reason,
                EditorEnv = env,
                OutputDirectoryValidation = dirResult,
                TargetFrameRate = targetFrameRate,
            };

            Log.Debug($"EditorExportPreflight: {readiness} / {reason}");
            return report;
        }
    }
}
