namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器确定性导出会话状态（Phase 3.3.0）。
    /// 终止状态（Completed / Cancelled / Failed）保留供 GUI 查看，下一次 Start 可重新进入 Preparing。
    /// </summary>
    internal enum EditorExportState
    {
        /// <summary>空闲：从未启动或已被显式重置。</summary>
        Idle,

        /// <summary>准备中：Start 已调用，正在校验环境与创建会话目录。</summary>
        Preparing,

        /// <summary>运行中：DeterministicFrameScheduler 正在推进并捕获。</summary>
        Running,

        /// <summary>已完成：达到 target-frame-count 并成功收尾。</summary>
        Completed,

        /// <summary>已取消：用户停止、Mod 禁用或会话被外部取消。</summary>
        Cancelled,

        /// <summary>已失败：内部错误或异常。</summary>
        Failed,
    }
}
