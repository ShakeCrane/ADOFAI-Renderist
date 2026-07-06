using ADOFAI.Renderist.Capture;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// Preflight 检查报告（不可变快照，Phase 2.2）。
    ///
    /// 由 <see cref="Preflight.Run"/> 同步生成，无副作用。
    /// 生成后与 Settings 解耦——即使用户随后改了 Settings，
    /// 旧报告仍代表生成时刻的状态。
    /// </summary>
    internal sealed class PreflightReport
    {
        /// <summary>整体检查结果。</summary>
        public PreflightResult Overall { get; set; }

        // ---- 输出目录验证 ----

        /// <summary>输出目录验证结果（包含 Outcome / NormalizedPath / RejectReason）。</summary>
        public DirectoryValidationResult OutputDirectoryValidation { get; set; }

        /// <summary>展示用目录字符串（已 normalize，或默认目录提示）。</summary>
        public string OutputDirectory { get; set; }

        // ---- CaptureService / Mod 状态 ----

        /// <summary>当前是否正在录制（读自 CaptureService.IsRecording）。</summary>
        public bool IsRecording { get; set; }

        /// <summary>Mod 是否已启用（读自 ModEntry.Enabled）。</summary>
        public bool ModEnabled { get; set; }

        // ---- 参数合法性（已 normalize 后的值）----

        public int SuperSize { get; set; }
        public int EveryN { get; set; }
        public float TargetFps { get; set; }
        public int MaxFrames { get; set; }
        public int ZeroPadWidth { get; set; }

        // ---- 环境诊断 ----

        public EditorEnvSnapshot EditorEnv { get; set; }
    }

    internal enum PreflightResult
    {
        Pass,
        Warn,
        Fail,
    }
}
