namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器确定性导出的就绪状态（Phase 3.4.0）。
    /// 状态由 <see cref="EditorExportPreflight.Run"/> 从 Unity 环境、
    /// Settings、编辑器选择状态与输出目录验证结果派生。
    /// </summary>
    internal enum EditorExportReadiness
    {
        /// <summary>功能未启用。</summary>
        Disabled,

        /// <summary>当前场景明确不是编辑器场景。</summary>
        NotInEditor,

        /// <summary>场景信息不可用或识别结果无法判断，不能安全结论。</summary>
        UnknownEnvironment,

        /// <summary>存在阻断条件。</summary>
        Blocked,

        /// <summary>已就绪。</summary>
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

        /// <summary>功能未启用。</summary>
        FeatureDisabled,

        /// <summary>未检测到编辑器场景。</summary>
        EditorSceneNotDetected,

        /// <summary>环境信息不可用或无法判断。</summary>
        EnvironmentUnavailable,

        /// <summary>当前编辑器 floor 选择无法由现有安全恢复路径精确恢复。</summary>
        UnsupportedEditorSelection,

        /// <summary>目标帧率不合法。</summary>
        InvalidTargetFrameRate,

        /// <summary>输出目录非法。</summary>
        InvalidOutputDirectory,

        /// <summary>End Tail 数值或单位非法。</summary>
        InvalidEndTail,

        /// <summary>Beats 换算缺少结束 BPM 或 pitch。</summary>
        EndTailDependenciesUnavailable,

        /// <summary>End Tail 自身已不可能容纳于 safety frame limit。</summary>
        EndTailExceedsSafetyLimit,
    }

    /// <summary>
    /// 编辑器导出就绪报告（只读快照，Phase 3.4.0）。
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

        /// <summary>当前目标帧率。</summary>
        public int TargetFrameRate { get; set; }

        public double? EndTailInputValue { get; set; }
        public EndTailUnit? EndTailInputUnit { get; set; }
        public int? ResolvedTailFrames { get; set; }
        public double? ResolvedTailSeconds { get; set; }
        public double? ResolvedTailBeats { get; set; }
        public double? CompletionBpm { get; set; }
        public double? Pitch { get; set; }
        public int SafetyFrameLimit { get; set; }
        public string EndTailValidationError { get; set; }
    }
}
