using ADOFAI.Renderist.Capture;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 导出前置检查（Phase 2.2）。
    ///
    /// 无副作用契约：
    ///   - 不创建目录（绝不调用 OutputPath.ResolveSessionDirectory 或 Directory.CreateDirectory）。
    ///   - 不调用 CaptureService.StartSequence / StopSequence / RequestSingleCaptureNow。
    ///   - 不修改 CaptureService 任何静态状态。
    ///   - 只读：Settings、CaptureService.IsRecording、ModEntry.Enabled、
    ///           OutputPath.ValidateDirectory、EditorEnvSnapshot.Capture。
    /// </summary>
    internal static class Preflight
    {
        /// <summary>
        /// 同步执行全部 Preflight 检查并返回报告。无副作用。
        /// </summary>
        public static PreflightReport Run()
        {
            var report = new PreflightReport();
            bool hasFail = false;
            bool hasWarn = false;

            Settings settings = ModEntry.Settings;
            if (settings == null)
            {
                // Settings 尚未加载——不应发生，但防御性处理。
                report.Overall = PreflightResult.Fail;
                return report;
            }

            // 1. 输出目录验证（只验证不创建）
            DirectoryValidationResult dirResult = OutputPath.ValidateDirectory(settings.OutputDirectory);
            report.OutputDirectoryValidation = dirResult;
            report.OutputDirectory = dirResult.NormalizedPath ?? string.Empty;
            if (dirResult.Outcome == DirectoryValidationOutcome.Reject)
            {
                hasFail = true;
                Log.Debug("Preflight: output directory rejected: " + dirResult.RejectReason);
            }

            // 2. CaptureService 当前未在录制
            report.IsRecording = CaptureService.IsRecording;
            if (CaptureService.IsRecording)
            {
                hasFail = true;
                Log.Debug("Preflight: CaptureService is already recording");
            }

            // 3. Mod 已启用
            report.ModEnabled = ModEntry.Enabled;
            if (!ModEntry.Enabled)
            {
                hasFail = true;
                Log.Debug("Preflight: mod is not enabled");
            }

            // 4. SuperSize 范围 [1, 8]
            int superSize = settings.CaptureSuperSize;
            report.SuperSize = NormalizeSuperSize(superSize);
            if (superSize < 1 || superSize > 8)
            {
                hasWarn = true;
                Log.Debug("Preflight: SuperSize out of range, will be clamped: " + superSize);
            }

            // 5. EveryN >= 1
            int everyN = settings.CaptureEveryNFrames;
            report.EveryN = everyN < 1 ? 1 : everyN;
            if (everyN < 1)
            {
                hasWarn = true;
                Log.Debug("Preflight: EveryN < 1, will be clamped to 1");
            }

            // 6. TargetFps >= 0
            float fps = settings.TargetCaptureFps;
            report.TargetFps = fps < 0f ? 0f : fps;
            if (fps < 0f)
            {
                hasWarn = true;
                Log.Debug("Preflight: TargetFps < 0, will be clamped to 0");
            }

            // 7. MaxFrames >= 0
            int maxFrames = settings.MaxFramesPerSession;
            report.MaxFrames = maxFrames < 0 ? 0 : maxFrames;
            if (maxFrames < 0)
            {
                hasWarn = true;
                Log.Debug("Preflight: MaxFrames < 0, will be clamped to 0");
            }

            // 8. ZeroPadWidth >= 1
            int padWidth = settings.ZeroPadWidth;
            report.ZeroPadWidth = padWidth < 1 ? 1 : padWidth;
            if (padWidth < 1)
            {
                hasWarn = true;
                Log.Debug("Preflight: ZeroPadWidth < 1, will be clamped to 1");
            }

            // 9. EditorEnv 诊断（始终 Unknown，不作为 Fail）
            report.EditorEnv = EditorEnvSnapshot.Capture();

            // ---- 汇总 ----
            if (hasFail)
            {
                report.Overall = PreflightResult.Fail;
            }
            else if (hasWarn)
            {
                report.Overall = PreflightResult.Warn;
            }
            else
            {
                report.Overall = PreflightResult.Pass;
            }

            return report;
        }

        private static int NormalizeSuperSize(int superSize)
        {
            if (superSize < 1) return 1;
            if (superSize > 8) return 8;
            return superSize;
        }
    }
}
