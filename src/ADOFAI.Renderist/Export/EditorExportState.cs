namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器导出会话状态枚举（Phase 2.4）。
    ///
    /// Phase 2.4 不执行真实截图或非实时渲染，因此不使用 <c>Capturing</c> 这一术语，
    /// 避免误导用户认为有图片落盘。<c>Running</c> 仅表示会话存活、Tick 在推进计数。
    ///
    /// 终止状态（Completed / Cancelled / Failed）保留供 GUI 查看，下一次 Start 可重新进入 Preparing。
    /// </summary>
    internal enum EditorExportState
    {
        /// <summary>空闲：从未启动或已被显式重置。</summary>
        Idle,

        /// <summary>准备中：Start 已调用，正在校验环境与创建会话目录。</summary>
        Preparing,

        /// <summary>运行中：会话存活，Tick 推进计数；不产生截图。</summary>
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
