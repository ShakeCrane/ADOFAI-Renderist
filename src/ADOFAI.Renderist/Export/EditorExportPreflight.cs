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

            // 1. 功能开关
            if (!settings.EditorExportEnabled)
            {
                return CreateReport(EditorExportReadiness.Disabled, EditorExportReadinessReason.FeatureDisabled,
                    env, dirResult, targetFrameRate);
            }

            // 2. 环境信息不可用
            if (env.EnvironmentReadFailed || string.IsNullOrWhiteSpace(env.SceneName))
            {
                return CreateReport(EditorExportReadiness.UnknownEnvironment, EditorExportReadinessReason.EnvironmentUnavailable,
                    env, dirResult, targetFrameRate);
            }

            // 3. 非编辑器场景
            if (env.Detection != EditorEnvDetection.ProbablyEditor)
            {
                return CreateReport(EditorExportReadiness.NotInEditor, EditorExportReadinessReason.EditorSceneNotDetected,
                    env, dirResult, targetFrameRate);
            }

            // 4. 目标帧率非法
            if (targetFrameRate <= 0)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidTargetFrameRate,
                    env, dirResult, targetFrameRate);
            }

            // 5. 输出目录非法
            if (dirResult.Outcome == DirectoryValidationOutcome.Reject)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidOutputDirectory,
                    env, dirResult, targetFrameRate);
            }

            // 6. 全部通过
            return CreateReport(EditorExportReadiness.Ready, EditorExportReadinessReason.None,
                env, dirResult, targetFrameRate);
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
