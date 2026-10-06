using System;
using System.Threading;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 一次在途 FFmpeg 组件检查的取消能力（Unity-free / UMM-free）。
    ///
    /// 存在理由：legacy 托管根迁移是**写磁盘**行为（复制、写新 marker、原子发布），
    /// 不能再沿用原只读 inspection 的“无取消”边界。disable / unload / 退出时
    /// <c>ShutdownFfmpegComponent</c> 必须能让在途的 hash / copy / probe / publish 尽快收敛。
    ///
    /// 生命周期契约（调用方必须遵守，本类型刻意只提供最小 API）：
    ///   * <see cref="Token"/> 必须在启动后台任务**之前**、在调用线程上取好
    ///     （释放之后再读会抛 <see cref="ObjectDisposedException"/>）；
    ///   * <see cref="Cancel"/> 只**请求**取消，绝不释放句柄 —— 后台线程可能仍在 register；
    ///   * <see cref="Dispose"/> 只允许在**对应任务已经 settle** 之后调用（例如 Task continuation）；
    ///   * Cancel 与 Dispose 都幂等，顺序颠倒也不抛异常；
    ///   * 同一实例只服务一次检查，绝不跨 generation 复用（每个 generation 一个新实例）。
    /// </summary>
    internal sealed class FfmpegInspectionCancellation : IDisposable
    {
        private readonly CancellationTokenSource _source = new CancellationTokenSource();

        /// <summary>本代检查的取消 token。必须在 <see cref="Dispose"/> 之前读取。</summary>
        public CancellationToken Token
        {
            get { return _source.Token; }
        }

        /// <summary>是否已请求取消（已释放时按已取消处理）。</summary>
        public bool IsCancellationRequested
        {
            get
            {
                try
                {
                    return _source.IsCancellationRequested;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// 请求取消。已取消、已释放或某个注册回调抛异常时都是安全的 no-op：
        /// 收敛路径绝不因为“取消本身出错”而中断。
        /// </summary>
        public void Cancel()
        {
            try
            {
                _source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException)
            {
            }
        }

        public void Dispose()
        {
            try
            {
                _source.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal sealed class FfmpegInspectionSequenceRequest
    {
        /// <summary>当前（新）托管安装根。null / 非法 = 托管目录不可用。</summary>
        public string InstallRoot { get; set; }

        /// <summary>旧（LocalLow）托管安装根。null / 非法 = 无旧安装可迁移。</summary>
        public string LegacyRoot { get; set; }

        /// <summary>用户显式指定的 FFmpeg 路径（来自 Settings）。</summary>
        public string ExplicitPath { get; set; }

        /// <summary>
        /// 本代检查的取消 token。**同一个 token 同时覆盖迁移与 inspection**，
        /// 由 <see cref="FfmpegInspectionCancellation"/> 提供。
        /// </summary>
        public CancellationToken CancellationToken { get; set; }

        /// <summary>
        /// 参与迁移与托管安装发现的资产。null = <see cref="FfmpegAssetManifest.Primary"/>
        /// 与真实 manifest（生产调用恒为 null）。
        ///
        /// 与 <see cref="FfmpegComponentInspectionRequest.Assets"/> 同样的注入点：
        /// 离线回归可以用合成资产覆盖**生产序列本身**（含取消语义），
        /// 而不需要携带真实 FFmpeg 二进制。
        /// </summary>
        public FfmpegAsset Asset { get; set; }
    }

    /// <summary>
    /// 一次后台组件检查的**唯一**序列（Unity-free）：
    /// 旧托管根一次性迁移准备 → 新托管根 inspection → 同一个 generation 的结果。
    ///
    /// 顺序是**结构性**的：inspection 读取磁盘之前，迁移已经结束，因此不会出现
    /// “先发布 NotFound、再 invalidate 后重扫”的中间态。
    ///
    /// 取消语义：
    ///   * token 真正传进 <see cref="FfmpegLegacyRootMigration.Migrate"/>（写盘阶段在每个
    ///     安全检查点收敛）与 <see cref="FfmpegComponentInspector.Inspect"/>（能力探测会 kill 短进程）；
    ///   * 一旦观察到取消，**绝不继续**启动新的 inspector / capability probe 工作，
    ///     而是返回一个带取消标记的报告；
    ///   * 取消的边界是“**不再启动**新的 copy / probe / publish”，不是“立刻中断正在执行的 IO”：
    ///     迁移使用同步 <see cref="System.IO.File.Copy(string,string,bool)"/>，token 不能中断
    ///     已经开始的那一次 copy —— 它可能完成之后才在下一个检查点被观察到，随后只清理本次 staging；
    ///     Locator / hash 同样没有细粒度 token。取消后**绝不 publish**，旧根始终只读；
    ///   * 该报告由调用方（ModEntry 的 pump）先 invalidate 再尝试发布，因此它
    ///     **永远不会成为 readiness 的当前结论**；即使真的被发布也是 fail-closed 的。
    ///
    /// 这里不是“第二套 state machine”：它就是那唯一一条检查序列，只是放在 Unity-free
    /// 命名空间里以便 net48 回归直接覆盖；generation / 发布语义仍由既有
    /// <see cref="FfmpegReadinessTracker"/> 唯一决定。
    /// </summary>
    internal static class FfmpegInspectionSequence
    {
        /// <summary>被取消的检查使用的机读标记。</summary>
        internal const string CancelledDiscoveryErrorCode = "inspection-cancelled";

        /// <summary>判断一份报告是否是“因取消而未完成”的报告。</summary>
        public static bool IsCancelled(FfmpegComponentReport report)
        {
            return report != null &&
                   string.Equals(report.DiscoveryErrorCode, CancelledDiscoveryErrorCode, StringComparison.Ordinal);
        }

        public static FfmpegComponentReport Run(FfmpegInspectionSequenceRequest request)
        {
            if (request == null)
                return BuildCancelledReport(null);

            CancellationToken cancellationToken = request.CancellationToken;
            FfmpegAsset asset = request.Asset ?? FfmpegAssetManifest.Primary;

            FfmpegInstallLayout targetLayout;
            string targetLayoutError;
            bool targetAvailable = FfmpegInstallLayout.TryCreate(
                request.InstallRoot, out targetLayout, out targetLayoutError);

            FfmpegInstallLayout legacyLayout;
            string legacyLayoutError;
            bool legacyAvailable = FfmpegInstallLayout.TryCreate(
                request.LegacyRoot, out legacyLayout, out legacyLayoutError);

            // 1) 旧托管根一次性迁移（写磁盘行为：token 必须真正传进去）。
            FfmpegLegacyMigrationResult migration = FfmpegLegacyRootMigration.Migrate(
                new FfmpegLegacyMigrationRequest
                {
                    TargetLayout = targetAvailable ? targetLayout : null,
                    LegacyLayout = legacyAvailable ? legacyLayout : null,
                    Asset = asset,
                    ProbeCapabilities = true,
                }, cancellationToken);

            // 2) 已取消：不再启动新的 inspector / capability probe 工作。
            if (cancellationToken.IsCancellationRequested)
                return BuildCancelledReport(migration);

            // 3) 迁移已经结束；inspection 读取磁盘，同一个 token 继续生效。
            FfmpegComponentReport report = FfmpegComponentInspector.Inspect(
                new FfmpegComponentInspectionRequest
                {
                    ExplicitPath = request.ExplicitPath,
                    InstallRoot = request.InstallRoot,
                    ProbeCapabilities = true,
                    CancellationToken = cancellationToken,
                    Assets = request.Asset == null ? null : new[] { asset },
                });

            report.Migration = migration;
            return report;
        }

        /// <summary>
        /// 取消标记报告。<see cref="FfmpegComponentState.NotFound"/> 只表示“本次没有得到结论”，
        /// 不是对磁盘的判断；调用方先 invalidate，因此它不会成为当前结论。
        /// </summary>
        private static FfmpegComponentReport BuildCancelledReport(FfmpegLegacyMigrationResult migration)
        {
            return new FfmpegComponentReport
            {
                State = FfmpegComponentState.NotFound,
                ManagedInstalls = new FfmpegManagedInstall[0],
                InstallableAsset = FfmpegAssetManifest.Primary,
                DiscoveryErrorCode = CancelledDiscoveryErrorCode,
                Migration = migration,
            };
        }
    }
}
