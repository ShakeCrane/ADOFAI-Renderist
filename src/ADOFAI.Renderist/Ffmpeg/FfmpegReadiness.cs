using System;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 进程内 FFmpeg readiness 的三态投影。
    ///
    /// 重要边界：readiness 是**信息性**的。它只回答“MP4 session 现在能不能启动”，
    /// 绝不是 PNG / Log-only 导出的前置条件 —— FFmpeg 缺失、无效或能力不足
    /// 都绝不能阻断既有导出路径。
    /// </summary>
    internal enum FfmpegReadinessState
    {
        /// <summary>正在检查 / 待检查：当前输入 generation 尚无有效结论。</summary>
        Preparing = 0,

        /// <summary>当前 generation 的报告明确为 <see cref="FfmpegComponentState.Ready"/>。</summary>
        Ready = 1,

        /// <summary>明确的失败 / 不可用：组件状态非 Ready，或检查任务自身 fault。</summary>
        Failed = 2,
    }

    /// <summary>readiness 的机读原因码。Ready 时为 <see cref="None"/>。</summary>
    internal static class FfmpegReadinessReason
    {
        internal const string None = "none";

        /// <summary>已请求检查，但检查尚未开始。</summary>
        internal const string Pending = "pending";

        /// <summary>检查正在进行。</summary>
        internal const string Checking = "checking";

        /// <summary>检查任务自身 fault（与“组件不可用”区分）。</summary>
        internal const string InspectionFaulted = "inspection-faulted";

        /// <summary>报告存在但组件状态不是 Ready。</summary>
        internal static string ComponentNotReady(FfmpegComponentState state)
        {
            return "component-" + state;
        }
    }

    /// <summary>
    /// readiness 的只读快照（纯数据）。
    ///
    /// <see cref="Report"/> 只有在它仍然对应当前输入 generation 时才非 null；
    /// 调用方**不得**把“记得上一次看到的 report”当作当前结论。
    /// </summary>
    internal sealed class FfmpegReadinessSnapshot
    {
        public FfmpegReadinessState State { get; set; }

        /// <summary>产生本快照的输入 generation。</summary>
        public int Generation { get; set; }

        /// <summary>是否有检查在途。MP4 启动条件之一就是它为 false。</summary>
        public bool ScanInFlight { get; set; }

        /// <summary>机读原因码（见 <see cref="FfmpegReadinessReason"/>）。</summary>
        public string ReasonCode { get; set; }

        /// <summary>可选的补充细节（fault 异常信息 / 发现阶段错误细节）。</summary>
        public string ReasonDetail { get; set; }

        /// <summary>仍对应当前 generation 的组件报告；否则为 null。</summary>
        public FfmpegComponentReport Report { get; set; }

        /// <summary>最近一次使输入失效 / 请求重新检查的原因（诊断用）。</summary>
        public string PendingReason { get; set; }
    }

    /// <summary>
    /// FFmpeg readiness 的唯一 authority（Process-wide，进程内、内存态）。
    ///
    /// 它只做一件事：把“输入是否变化 / 检查是否在途 / 最近一次结论是什么”投影成
    /// <see cref="FfmpegReadinessState"/>，使 GUI 与 <c>EditorExportController</c>
    /// 不能再各自复制一套 Ready 判定。
    ///
    /// 规则：
    ///   * <see cref="Invalidate"/> 立即让旧报告失效 —— 旧报告不再是当前输入的结论；
    ///   * 检查启动时绑定当前 generation，完成时 generation 不符即**丢弃**；
    ///   * fault 形成明确的失败态并**不**自动重试（需要用户显式重新检查），
    ///     因此不会出现 GUI 每帧 fault → retry 循环；
    ///   * 不持久化任何报告到磁盘。
    ///
    /// 线程模型：只在 Unity 主线程使用（GUI、OnUpdate pump、controller.Start）。
    /// 后台任务只负责产出纯数据的报告，不接触本类型。
    /// </summary>
    internal sealed class FfmpegReadinessTracker
    {
        internal const int NoGeneration = -1;

        private int _generation;
        private FfmpegComponentReport _report;
        private int _reportGeneration = NoGeneration;
        private bool _scanInFlight;
        private int _scanGeneration = NoGeneration;
        private bool _scanRequested = true;
        private string _faultCode;
        private string _faultDetail;
        private string _pendingReason = "initial";

        /// <summary>当前输入 generation。每次输入失效 / 显式重新检查都会 +1。</summary>
        public int Generation
        {
            get { return _generation; }
        }

        /// <summary>是否有检查在途。</summary>
        public bool ScanInFlight
        {
            get { return _scanInFlight; }
        }

        /// <summary>
        /// 是否应当启动一次新的检查。已有在途检查时恒为 false，
        /// 因此 GUI 每帧绘制不会重复启动检查。
        /// </summary>
        public bool NeedsScan
        {
            get { return _scanRequested && !_scanInFlight; }
        }

        /// <summary>
        /// 使当前输入失效并请求一次重新检查。适用于：
        /// 输入配置变化（显式路径）、磁盘状态变化（安装 / 下载结果）、
        /// 显式 Refresh、重新启用 Mod、以及 fault 后的用户显式重试。
        ///
        /// 旧报告立即不再被 readiness 接受（MP4 回到 Preparing）。
        /// </summary>
        public void Invalidate(string reason)
        {
            _generation++;
            _report = null;
            _reportGeneration = NoGeneration;
            _faultCode = null;
            _faultDetail = null;
            _scanRequested = true;
            _pendingReason = reason;
        }

        /// <summary>
        /// 绑定当前 generation 并标记检查在途。已有一个在途检查时返回 false
        /// （同一时刻最多一个检查任务，不做并发扫描）。
        /// </summary>
        public bool TryBeginScan(out int generation)
        {
            generation = NoGeneration;
            if (_scanInFlight || !_scanRequested)
                return false;

            _scanRequested = false;
            _scanInFlight = true;
            _scanGeneration = _generation;
            generation = _scanGeneration;
            return true;
        }

        /// <summary>
        /// 发布一次检查结果。仅当该 generation 仍是当前输入时才会成为当前报告；
        /// 否则结果被丢弃（返回 false），绝不覆盖更新一代的状态。
        /// </summary>
        public bool TryPublishScan(int generation, FfmpegComponentReport report)
        {
            if (!_scanInFlight || generation != _scanGeneration)
                return false;

            // 在途标记必须先释放：无论发布还是丢弃，这一代任务都已结束。
            _scanInFlight = false;
            _scanGeneration = NoGeneration;

            if (generation != _generation)
                return false;

            if (report == null)
            {
                // 不变量：一次检查必须给出结论。空结果按**失败**处理（fail-closed），
                // 既不能发布成“当前结论”，也不能让 readiness 停在 Preparing 且不再排队。
                _report = null;
                _reportGeneration = NoGeneration;
                _faultCode = FfmpegReadinessReason.InspectionFaulted;
                _faultDetail = "inspection-returned-null";
                _scanRequested = false;
                return false;
            }

            _report = report;
            _reportGeneration = generation;
            _faultCode = null;
            _faultDetail = null;
            return true;
        }

        /// <summary>
        /// 记录检查任务 fault。仅当该 generation 仍是当前输入时才形成失败态；
        /// 否则视为过期 fault 丢弃（不覆盖更新一代的状态）。
        ///
        /// 接受的 fault **不**请求重新检查：必须由用户显式重新检查，
        /// 以避免每帧自动 fault → retry 循环。
        /// </summary>
        public bool TryFailScan(int generation, string code, string detail)
        {
            if (!_scanInFlight || generation != _scanGeneration)
                return false;

            _scanInFlight = false;
            _scanGeneration = NoGeneration;

            if (generation != _generation)
                return false;

            _report = null;
            _reportGeneration = NoGeneration;
            _faultCode = string.IsNullOrEmpty(code) ? FfmpegReadinessReason.InspectionFaulted : code;
            _faultDetail = detail;
            _scanRequested = false;
            return true;
        }

        /// <summary>当前 readiness 投影。Ready 当且仅当下面四个条件同时成立。</summary>
        public FfmpegReadinessSnapshot Snapshot()
        {
            var snapshot = new FfmpegReadinessSnapshot
            {
                Generation = _generation,
                ScanInFlight = _scanInFlight,
                PendingReason = _pendingReason,
            };

            bool reportIsCurrent = _report != null && _reportGeneration == _generation;
            if (reportIsCurrent)
            {
                snapshot.Report = _report;

                if (_scanInFlight)
                {
                    // 已有一个仍在途的检查：旧结论不再是当前输入的有效结论。
                    snapshot.State = FfmpegReadinessState.Preparing;
                    snapshot.ReasonCode = FfmpegReadinessReason.Checking;
                    return snapshot;
                }

                if (_report.State == FfmpegComponentState.Ready)
                {
                    snapshot.State = FfmpegReadinessState.Ready;
                    snapshot.ReasonCode = FfmpegReadinessReason.None;
                    return snapshot;
                }

                snapshot.State = FfmpegReadinessState.Failed;
                snapshot.ReasonCode = FfmpegReadinessReason.ComponentNotReady(_report.State);
                snapshot.ReasonDetail = FirstNonEmpty(_report.DiscoveryErrorDetail, _report.DiscoveryErrorCode);
                return snapshot;
            }

            if (_faultCode != null)
            {
                snapshot.State = FfmpegReadinessState.Failed;
                snapshot.ReasonCode = _faultCode;
                snapshot.ReasonDetail = _faultDetail;
                return snapshot;
            }

            snapshot.State = FfmpegReadinessState.Preparing;
            snapshot.ReasonCode = _scanInFlight
                ? FfmpegReadinessReason.Checking
                : FfmpegReadinessReason.Pending;
            return snapshot;
        }

        /// <summary>机器可读的一行摘要（日志用）。</summary>
        public override string ToString()
        {
            FfmpegReadinessSnapshot snapshot = Snapshot();
            return snapshot.State + "/" + snapshot.ReasonCode + "@gen" + snapshot.Generation;
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return string.IsNullOrEmpty(first) ? second : first;
        }
    }
}
