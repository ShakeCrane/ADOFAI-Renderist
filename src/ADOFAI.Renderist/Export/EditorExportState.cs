namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 保留的旧编辑器导出会话状态枚举。
    ///
    /// 旧实现曾由 DeterministicFrameScheduler 驱动 PNG 捕获；
    /// <c>Running</c> 表示会话存活且 scheduler 正在推进 / 捕获。
    ///
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

        /// <summary>清理中：正在写入最终 metadata 并收尾。</summary>
        Cleaning,

        /// <summary>已完成：用户主动停止并成功收尾。</summary>
        Completed,

        /// <summary>已取消：环境失效、Mod 禁用或会话被外部取消。</summary>
        Cancelled,

        /// <summary>已失败：metadata 写入失败或未处理异常。</summary>
        Failed,
    }
}
