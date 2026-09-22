using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器确定性导出就绪检查（Phase 3.4.0；Phase 3.7.0 增加输出几何 gate）。
    /// 完全无副作用：不创建目录、不写文件、不执行 Harmony Patch、不改 Unity 时间属性、
    /// 不改任何 Camera 状态。三台谱面 Camera 的 aspect 只做只读诊断采集。
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

            // 输出几何的 persisted 输入（原始值，不 sanitize）。它是只读配置，
            // 因此在任何早期返回路径上都可安全携带进报告，供 GUI 显示实际配置。
            var geometryInput = new GeometryInput(
                settings.EditorCustomResolutionEnabled,
                settings.EditorCustomResolutionWidth,
                settings.EditorCustomResolutionHeight,
                settings.EditorSupersamplingScale);

            if (!settings.EditorExportEnabled)
            {
                return CreateReport(EditorExportReadiness.Disabled, EditorExportReadinessReason.FeatureDisabled,
                    env, dirResult, targetFrameRate, geometryInput: geometryInput);
            }

            if (env.EnvironmentReadFailed || string.IsNullOrWhiteSpace(env.SceneName))
            {
                return CreateReport(EditorExportReadiness.UnknownEnvironment, EditorExportReadinessReason.EnvironmentUnavailable,
                    env, dirResult, targetFrameRate, geometryInput: geometryInput);
            }

            if (env.Detection != EditorEnvDetection.ProbablyEditor)
            {
                return CreateReport(EditorExportReadiness.NotInEditor, EditorExportReadinessReason.EditorSceneNotDetected,
                    env, dirResult, targetFrameRate, geometryInput: geometryInput);
            }

            // 初始化只读反射缓存；不修改游戏状态。
            EditorGameReflection.EnsureTypes();

            // 当前恢复路径能处理普通单选和连续 multi-select；非连续 multi-select
            // 会在 RestoreSelectedFloorSeqs 中失败，因此必须在 session 创建前 fail-closed。
            // selectedFloors 为空时不在此做推断，避免把 ADOFAI 的正常单选表示误判为不可恢复。
            if (EditorGameReflection.IsLevelLoaded() && !IsEditorSelectionRestorable())
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.UnsupportedEditorSelection,
                    env, dirResult, targetFrameRate, geometryInput: geometryInput);
            }

            if (!OutputFpsPolicy.IsValid(targetFrameRate))
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidTargetFrameRate,
                    env, dirResult, targetFrameRate, geometryInput: geometryInput);
            }

            // 输出几何 gate（Phase 3.7.0）。自定义分辨率关闭时这里只读 Screen，
            // 与 0.3.6.4 行为一致；开启时按 OutputGeometryPolicy 的统一规则校验
            // persisted 宽高（正整数 + 真实硬件上限），非法值保持非法并 fail-closed。
            if (!OutputGeometryPolicy.TryResolve(
                    geometryInput, out GeometryResolution geometry, out string geometryError))
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidOutputGeometry,
                    env, dirResult, targetFrameRate,
                    geometryInput: geometryInput, geometryError: geometryError);
            }

            var endTailInput = new EndTailInput(
                settings.EditorEndTailValue, settings.EditorEndTailUnit);
            double? completionBpm = EditorGameReflection.TryReadFinalEffectiveBpm(
                out double finalBpm, out string bpmError)
                ? finalBpm
                : (double?)null;
            double pitchValue = EditorGameReflection.ReadPitch(out bool pitchUnavailable);
            double? pitch = pitchUnavailable ? (double?)null : pitchValue;
            // safety 默认未配置（unbounded）：0 表示不存在总帧数 / 总时长上限，
            // End Tail 只受自身数值可表达性约束；显式配置时才按配置值判定。
            SafetyLimitResolution safety = SafetyFrameLimitPolicy.Resolve(
                settings.EditorExportSafetyFrameLimit);
            long safetyFrameLimit = safety.FrameLimit;

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
                    string.Equals(endTailError, "end-tail-exceeds-safety-limit", StringComparison.Ordinal)
                        ? EditorExportReadinessReason.EndTailExceedsSafetyLimit
                        : endTailError != null &&
                          (endTailError.Contains("bpm-unavailable") ||
                           endTailError.Contains("pitch-unavailable"))
                            ? EditorExportReadinessReason.EndTailDependenciesUnavailable
                            : EditorExportReadinessReason.InvalidEndTail;
                return CreateReport(EditorExportReadiness.Blocked, tailReason,
                    env, dirResult, targetFrameRate, endTailInput, null,
                    completionBpm, pitch, safetyFrameLimit,
                    endTailError ?? bpmError,
                    geometryInput: geometryInput, geometry: geometry);
            }

            if (dirResult.Outcome == DirectoryValidationOutcome.Reject)
            {
                return CreateReport(EditorExportReadiness.Blocked, EditorExportReadinessReason.InvalidOutputDirectory,
                    env, dirResult, targetFrameRate, endTailInput, endTailResolution,
                    completionBpm, pitch, safetyFrameLimit, null,
                    geometryInput: geometryInput, geometry: geometry);
            }

            return CreateReport(EditorExportReadiness.Ready, EditorExportReadinessReason.None,
                env, dirResult, targetFrameRate, endTailInput, endTailResolution,
                completionBpm, pitch, safetyFrameLimit, null,
                geometryInput: geometryInput, geometry: geometry);
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

        /// <summary>
        /// safetyFrameLimit: 0 = 未配置上限（unbounded），报告中记为 <c>null</c>；
        /// 正数 = 显式配置的 output-frame 上限，原样记录。
        /// </summary>
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
            long safetyFrameLimit = 0,
            string endTailValidationError = null,
            GeometryInput? geometryInput = null,
            GeometryResolution? geometry = null,
            string geometryError = null)
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
                SafetyFrameLimit = safetyFrameLimit > 0 ? safetyFrameLimit : (long?)null,
                EndTailValidationError = endTailValidationError,
                OutputGeometryMode = geometry?.Mode,
                OutputWidth = geometry?.Width,
                OutputHeight = geometry?.Height,
                OutputAspect = geometry?.Aspect,
                ConfiguredCustomWidth = geometryInput?.Width,
                ConfiguredCustomHeight = geometryInput?.Height,
                ConfiguredSupersamplingScale = geometryInput?.SupersamplingScale,
                GeometryValidationError = geometryError,
                SupersamplingScale = geometry?.Scale,
                RenderWidth = geometry?.RenderWidth,
                RenderHeight = geometry?.RenderHeight,
                DownsampleLevelCount = geometry?.DownsampleLevelCount,
            };

            CaptureChartCameraAspectDiagnostics(report, env);

            Log.Debug($"EditorExportPreflight: {readiness} / {reason}");
            return report;
        }

        /// <summary>
        /// 只读采集三台原生谱面 Camera 的当前 aspect，供实机验收记录
        /// “导出前 / 导出后”的实际值。任何失败都只记录到报告，绝不阻断导出，
        /// 也绝不修改 Camera 状态。
        /// </summary>
        private static void CaptureChartCameraAspectDiagnostics(
            EditorExportReadinessReport report, EditorEnvSnapshot env)
        {
            // 非编辑器环境不做无谓反射；GUI 只在编辑器内展示该诊断。
            if (env.Detection != EditorEnvDetection.ProbablyEditor)
                return;

            try
            {
                EditorGameReflection.EnsureTypes();
                if (!EditorGameReflection.TryReadChartCameraChain(
                        out Camera bgStaticCamera, out Camera bgCamera, out Camera mainCamera,
                        out string chainError))
                {
                    report.ChartCameraAspectError = chainError ?? "camera-chain-unavailable";
                    return;
                }

                report.ChartCameraBgcamstaticAspect = TryReadAspect(bgStaticCamera);
                report.ChartCameraBgcamAspect = TryReadAspect(bgCamera);
                report.ChartCameraCamobjAspect = TryReadAspect(mainCamera);
            }
            catch (Exception ex)
            {
                report.ChartCameraAspectError = ex.Message;
            }
        }

        private static float? TryReadAspect(Camera camera)
        {
            if (camera == null)
                return null;

            try
            {
                return camera.aspect;
            }
            catch
            {
                return null;
            }
        }
    }
}
