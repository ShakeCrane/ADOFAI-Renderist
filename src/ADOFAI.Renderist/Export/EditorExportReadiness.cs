using ADOFAI.Renderist.Capture;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器非实时导出的就绪状态（Phase 2.3）。
    ///
    /// 本阶段为只读就绪检测，不实现任何真实的编辑器逐帧导出。
    /// 状态由 <see cref="EditorExportPreflight.Run"/> 从 Unity 环境、
    /// Settings、CaptureService 状态与输出目录验证结果派生。
    /// </summary>
    internal enum EditorExportReadiness
    {
        /// <summary>实验性功能未启用。</summary>
        Disabled,

        /// <summary>当前场景明确不是编辑器场景。</summary>
        NotInEditor,

        /// <summary>场景信息不可用或识别结果无法判断，不能安全结论。</summary>
        UnknownEnvironment,

        /// <summary>编辑器场景已识别，但存在阻断条件（目录非法、帧率非法或实时序列占用）。</summary>
        Blocked,

        /// <summary>已就绪。仅表示满足当前前置条件，不代表导出已开始或已实现。</summary>
        Ready,
    }

    /// <summary>
    /// 编辑器导出就绪判定原因（机器可读）。
    /// 供 GUI 与日志映射，不作为业务逻辑字符串直接使用。
    /// </summary>
    internal enum EditorExportReadinessReason
    {
        /// <summary>无阻断原因。</summary>
        None,

        /// <summary>实验性功能未启用。</summary>
        FeatureDisabled,

        /// <summary>未检测到编辑器场景。</summary>
        EditorSceneNotDetected,

        /// <summary>环境信息不可用或无法判断。</summary>
        EnvironmentUnavailable,

        /// <summary>实时序列截图（F10）正在运行。</summary>
        CaptureBusy,

        /// <summary>目标帧率不合法。</summary>
        InvalidTargetFrameRate,

        /// <summary>输出目录非法。</summary>
        InvalidOutputDirectory,
    }

    /// <summary>
    /// 编辑器导出就绪报告（只读快照，Phase 2.3）。
    /// 与 Settings 解耦：报告生成后即使 Settings 变化，仍代表生成时刻状态。
    /// </summary>
    internal sealed class EditorExportReadinessReport
    {
        /// <summary>就绪状态。</summary>
        public EditorExportReadiness Readiness { get; set; }

        /// <summary>稳定的原因标识。</summary>
        public EditorExportReadinessReason Reason { get; set; }

        /// <summary>编辑器环境快照。</summary>
        public EditorEnvSnapshot EditorEnv { get; set; }

        /// <summary>输出目录验证结果。</summary>
        public DirectoryValidationResult OutputDirectoryValidation { get; set; }

        /// <summary>当前目标帧率（仅作为意图参数）。</summary>
        public int TargetFrameRate { get; set; }

        /// <summary>实时序列是否正在运行。</summary>
        public bool IsRecording { get; set; }
    }
}
