using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器非实时导出就绪检查（Phase 2.3）。
    ///
    /// 完全无副作用：
    ///   * 不创建目录、不写文件、不创建 single_* / seq_*。
    ///   * 不修改 CaptureService 状态。
    ///   * 不设置 Time.timeScale / Time.captureFramerate。
    ///   * 不执行 Harmony Patch。
    /// 仅读取 Settings、Unity API、CaptureService.IsRecording、
    /// OutputPath.ValidateDirectory 与 EditorEnvSnapshot.Capture。
    /// </summary>
    internal static class EditorExportPreflight
    {
        /// <summary>
        /// 同步执行编辑器导出就绪检查并返回报告。
        /// </summary>
        public static EditorExportReadinessReport Run()
        {
            Settings settings = ModEntry.Settings;
            if (settings == null)
            {
                // Settings 尚未加载——不应发生，但防御性处理。
                return CreateReport(EditorExportReadiness.UnknownEnvironment, EditorExportReadinessReason.EnvironmentUnavailable);
            }

            EditorEnvSnapshot env = EditorEnvSnapshot.Capture();
            bool isRecording = CaptureService.IsRecording;
            DirectoryValidationResult dirResult = OutputPath.ValidateDirectory(settings.OutputDirectory);
            int targetFrameRate = settings.EditorTargetFrameRate;

            // 1. 功能开关
            if (!settings.EditorExportEnabled)
            {
                return CreateReport(EditorExportReadiness.Disabled, EditorExportReadinessReason.FeatureDisabled,
                    env, dirResult, targetFrameRate, isRecording);
            }

            // 2. 环境信息不可用（核心读取失败或场景名无法判断）
            if (env.EnvironmentReadFailed || string.IsNullOrWhiteSpace(env.SceneName))
            {
                return CreateReport(EditorExportReadiness.UnknownEnvironment, EditorExportReadinessReason.EnvironmentUnavailable,
                    env, dirResult, targetFrameRate, isRecording);
            }

            // 3. 非编辑器场景
            if (env.Detection != EditorEnvDetection.ProbablyEditor)
            {
                return CreateReport(EditorExportReadiness.NotInEditor, EditorExportReadinessReason.EditorSceneNotDetected,
                    env, dirResult, targetFrameRate, isRecording);
            }

            // 4. 实时序列占用
            if (isRecording)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.CaptureBusy,
                    env, dirResult, targetFrameRate, isRecording);
            }

            // 5. 目标帧率非法
            if (targetFrameRate <= 0)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidTargetFrameRate,
                    env, dirResult, targetFrameRate, isRecording);
            }

            // 6. 输出目录非法
            if (dirResult.Outcome == DirectoryValidationOutcome.Reject)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidOutputDirectory,
                    env, dirResult, targetFrameRate, isRecording);
            }

            // 7. 全部通过
            return CreateReport(EditorExportReadiness.Ready, EditorExportReadinessReason.None,
                env, dirResult, targetFrameRate, isRecording);
        }

        private static EditorExportReadinessReport CreateReport(
            EditorExportReadiness readiness,
            EditorExportReadinessReason reason,
            EditorEnvSnapshot env = default,
            DirectoryValidationResult dirResult = default,
            int targetFrameRate = 0,
            bool isRecording = false)
        {
            var report = new EditorExportReadinessReport
            {
                Readiness = readiness,
                Reason = reason,
                EditorEnv = env,
                OutputDirectoryValidation = dirResult,
                TargetFrameRate = targetFrameRate,
                IsRecording = isRecording,
            };

            Log.Debug($"EditorExportPreflight: {readiness} / {reason}");
            return report;
        }
    }
}
