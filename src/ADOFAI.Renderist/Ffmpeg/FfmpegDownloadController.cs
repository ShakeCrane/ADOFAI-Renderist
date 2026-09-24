using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ADOFAI.Renderist.Ffmpeg
{
    /// <summary>
    /// 下载 + 校验 + 安装的管线阶段。每个阶段在 GUI 中都必须可区分。
    /// </summary>
    internal enum FfmpegDownloadState
    {
        Idle = 0,

        /// <summary>网络传输中（UnityWebRequest 在主线程驱动）。</summary>
        Downloading = 1,

        /// <summary>传输结束，正在核对长度与内容 SHA-256。</summary>
        Verifying = 2,

        /// <summary>校验通过，正在安全解压、能力探测并原子发布。</summary>
        Installing = 3,

        /// <summary>安装与能力探测全部成功。注意：这不等于 MP4 导出已经可用。</summary>
        Succeeded = 4,

        Cancelled = 5,

        Failed = 6,
    }

    /// <summary>
    /// Unity 侧对一次下载请求的结果描述（纯数据，不含 Unity 类型）。
    ///
    /// 由 <c>UnityFfmpegDownloadDriver</c> 在主线程构造并交给控制器。
    /// </summary>
    internal sealed class FfmpegDownloadResponse
    {
        /// <summary>HTTP 响应码；没有拿到响应时为 0。</summary>
        public int ResponseCode { get; set; }

        /// <summary>请求本身是否成功（传输完成且无网络/协议错误）。</summary>
        public bool RequestSucceeded { get; set; }

        /// <summary>重定向后的最终 URL（若无法获得则为空）。</summary>
        public string FinalUrl { get; set; }

        /// <summary>Unity 报告已接收的字节数（诊断用；权威长度以磁盘文件为准）。</summary>
        public long ReportedDownloadedBytes { get; set; }

        /// <summary>传输层错误文本（可能为空）。</summary>
        public string Error { get; set; }
    }

    /// <summary>一次下载请求的启动计划，交给 Unity 侧发起请求。</summary>
    internal sealed class FfmpegDownloadPlan
    {
        public bool Started { get; set; }
        public long Generation { get; set; }
        public string Url { get; set; }
        public string TempFilePath { get; set; }
        public long ExpectedBytes { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorDetail { get; set; }
    }

    /// <summary>
    /// FFmpeg 组件「下载 → 校验 → 安装」的单一下载 owner（Unity-free）。
    ///
    /// 设计要点：
    ///   * **单一 owner**：任何时刻只允许一个活动请求；重复点击被拒绝（<c>busy</c>）。
    ///   * **generation 隔离**：每次启动递增 generation。禁用 / 取消 / 卸载会先让
    ///     generation 失效，因此迟到的完成通知只能清理自己那一代，绝不能启动安装、
    ///     发布旧版本、覆盖新任务状态或把已取消会话标记为成功。
    ///   * **临时文件 ownership 明确**：下载临时文件由本控制器创建并在**整个生命周期**
    ///     （下载 → 校验 → 安装）独占拥有与删除；<see cref="FfmpegInstaller"/> 只读取它，
    ///     绝不删除。Unity 侧的 <c>removeFileOnAbort</c> 只是 teardown 安全网，
    ///     不承担清理责任。
    ///   * **不在主线程做重活**：长度 / SHA-256 复核与安装都在后台 <see cref="Task"/> 上执行；
    ///     后台线程不接触任何 Unity 对象。
    ///
    /// 本类型不依赖 Unity / UMM / Harmony，因此可以在 net48 回归工程中完整测试。
    /// </summary>
    internal sealed class FfmpegDownloadController
    {
        private const int MaxTrackedTempFiles = 8;

        private readonly FfmpegInstallLayout _layout;

        /// <summary>请求 Unity 侧中止当前请求（主线程调用；参数为 generation）。</summary>
        private readonly Action<long> _abortRequest;

        private readonly object _gate = new object();

        private long _generationCounter;
        private long _generation;
        private long _ownedTempFilesPrunedBelow;

        /// <summary>generation → 由本控制器拥有、尚未删除的临时归档路径。</summary>
        private readonly Dictionary<long, string> _ownedTempFiles = new Dictionary<long, string>();

        private CancellationTokenSource _cancellation;
        private Task<FfmpegInstallResult> _workTask;
        private long _workGeneration = -1;

        public FfmpegDownloadController(FfmpegInstallLayout layout, Action<long> abortRequest)
        {
            if (layout == null)
                throw new ArgumentNullException("layout");

            _layout = layout;
            _abortRequest = abortRequest;
        }

        // ------------------------------ 只读状态（GUI 读取） ------------------------------

        public FfmpegDownloadState State { get; private set; }

        public long Generation
        {
            get { return _generation; }
        }

        /// <summary>当前（或最后一次）请求的资产。</summary>
        public FfmpegAsset Asset { get; private set; }

        public string TempFilePath { get; private set; }

        public long DownloadedBytes { get; private set; }

        public long ExpectedBytes { get; private set; }

        public string ErrorCode { get; private set; }

        public string ErrorDetail { get; private set; }

        public FfmpegInstallResult InstallResult { get; private set; }

        /// <summary>安装成功后的组件能力报告（仅在 <see cref="State"/> 为 Succeeded 时有意义）。</summary>
        public FfmpegCapabilityReport Capability
        {
            get { return InstallResult == null ? null : InstallResult.Capability; }
        }

        /// <summary>是否处于活动阶段（下载 / 校验 / 安装）。</summary>
        public bool IsBusy
        {
            get
            {
                return State == FfmpegDownloadState.Downloading ||
                       State == FfmpegDownloadState.Verifying ||
                       State == FfmpegDownloadState.Installing;
            }
        }

        /// <summary>下载进度比例（0..1）；总长度未知时为 0。</summary>
        public double ProgressFraction
        {
            get
            {
                if (ExpectedBytes <= 0)
                    return 0.0;
                if (DownloadedBytes <= 0)
                    return 0.0;
                if (DownloadedBytes >= ExpectedBytes)
                    return 1.0;
                return (double)DownloadedBytes / ExpectedBytes;
            }
        }

        // ------------------------------ 启动 ------------------------------

        /// <summary>
        /// 开始一次下载。已处于活动阶段时拒绝（重复点击保护）。
        /// 返回的计划由调用方交给 Unity 侧发起请求。
        /// </summary>
        public FfmpegDownloadPlan TryStart(
            FfmpegAsset asset, bool probeAfterInstall, int probeTimeoutSeconds)
        {
            var plan = new FfmpegDownloadPlan();

            if (asset == null)
            {
                plan.ErrorCode = "asset-missing";
                return plan;
            }

            if (IsBusy)
            {
                plan.ErrorCode = "busy";
                return plan;
            }

            lock (_gate)
            {
                _generationCounter++;
                _generation = _generationCounter;

                PruneOwnedTempFiles();

                string token = Guid.NewGuid().ToString("N").Substring(0, 8);
                string tempPath = _layout.DownloadFilePath(asset.Id, _generation, token);

                try
                {
                    Directory.CreateDirectory(_layout.DownloadRoot);
                }
                catch (Exception ex)
                {
                    plan.ErrorCode = "download-directory-failed";
                    plan.ErrorDetail = ex.Message;
                    return plan;
                }

                _ownedTempFiles[_generation] = tempPath;

                if (_cancellation != null)
                {
                    try
                    {
                        _cancellation.Dispose();
                    }
                    catch
                    {
                    }
                }
                _cancellation = new CancellationTokenSource();

                Asset = asset;
                TempFilePath = tempPath;
                ExpectedBytes = asset.ArchiveSizeBytes;
                DownloadedBytes = 0;
                ErrorCode = null;
                ErrorDetail = null;
                InstallResult = null;
                State = FfmpegDownloadState.Downloading;

                plan.Started = true;
                plan.Generation = _generation;
                plan.Url = asset.ArchiveUrl;
                plan.TempFilePath = tempPath;
                plan.ExpectedBytes = asset.ArchiveSizeBytes;

                PendingProbeAfterInstall = probeAfterInstall;
                PendingProbeTimeoutSeconds = probeTimeoutSeconds;
                return plan;
            }
        }

        private bool PendingProbeAfterInstall { get; set; }

        private int PendingProbeTimeoutSeconds { get; set; }

        // ------------------------------ 进度与完成（主线程） ------------------------------

        public void ReportProgress(long downloadedBytes, long totalBytes)
        {
            if (State != FfmpegDownloadState.Downloading)
                return;

            DownloadedBytes = downloadedBytes;
            if (totalBytes > 0)
                ExpectedBytes = totalBytes;
        }

        /// <summary>
        /// 接收 Unity 侧的完成通知。
        ///
        /// 返回 true 表示该通知属于当前 generation 并已被接受；
        /// 返回 false 表示它不是本次活动下载的第一次完成通知，因此**不做任何推进**。
        ///
        /// 临时文件 ownership 规则：
        ///   * 只有**非当前** generation（已被取消 / 失效的旧代）的迟到通知才删除那个旧文件；
        ///   * 当前 generation 的重复通知**绝不删除**归档 —— 此时校验或安装可能正在读取它，
        ///     删掉会把一次合法安装变成失败。终态清理由 <see cref="Pump"/> 统一负责。
        /// </summary>
        public bool ReportFinished(long generation, FfmpegDownloadResponse response)
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    // 旧代的迟到通知：只负责删除它自己那一代的临时文件。
                    DiscardOwnedTempFile(generation);
                    return false;
                }

                if (State != FfmpegDownloadState.Downloading)
                {
                    // 同一代的重复 / 重复投递通知：不得重复推进校验或安装，
                    // 也不得删除当前活动流程仍在使用的归档。
                    return false;
                }

                string validationError;
                string validationDetail;
                if (!ValidateResponse(response, out validationError, out validationDetail))
                {
                    FailLocked(validationError, validationDetail, generation);
                    return true;
                }

                State = FfmpegDownloadState.Verifying;
                StartWorkLocked(generation);
                return true;
            }
        }

        // ------------------------------ 取消与失效 ------------------------------

        /// <summary>
        /// 取消当前请求（用户主动取消）。先失效 generation，再中止请求并清理 owned 资源。
        /// </summary>
        public void Cancel(string reason)
        {
            Invalidate(reason, true);
        }

        /// <summary>
        /// 使一切失效：Mod 禁用 / 卸载 / 退出时调用。
        ///
        /// 与 <see cref="Cancel"/> 的区别只在语义标签：这里必须保证**不依赖**
        /// OnUpdate 或 GUI 再次被调用也能收敛（清理在调用内同步完成）。
        /// </summary>
        public void Invalidate(string reason, bool markCancelled)
        {
            long generationToAbort;

            lock (_gate)
            {
                if (!IsBusy && State == FfmpegDownloadState.Idle)
                {
                    // 无活动任务：仍要让所有在途通知失效。
                    _generationCounter++;
                    _generation = _generationCounter;
                    return;
                }

                // 先失效 generation：此后任何到达的通知都会被判为迟到。
                generationToAbort = _generation;
                _generationCounter++;
                _generation = _generationCounter;

                if (_cancellation != null)
                {
                    try
                    {
                        _cancellation.Cancel();
                    }
                    catch
                    {
                    }
                }

                if (markCancelled)
                {
                    State = FfmpegDownloadState.Cancelled;
                    ErrorCode = "cancelled";
                    ErrorDetail = reason;
                }

                // 取消发生在主线程：同步删除本次拥有的临时文件。
                DeleteAllOwnedTempFiles();
            }

            // 在锁外请求 Unity 侧中止（避免在持锁时回调外部代码）。
            RequestAbort(generationToAbort);
        }

        // ------------------------------ 主线程泵 ------------------------------

        /// <summary>
        /// 收取后台校验/安装任务的结果。必须由主线程按帧调用。
        /// 只读取后台线程产出的纯数据对象。
        /// </summary>
        public void Pump()
        {
            Task<FfmpegInstallResult> task;
            long taskGeneration;

            lock (_gate)
            {
                task = _workTask;
                taskGeneration = _workGeneration;
            }

            if (task == null || !task.IsCompleted)
                return;

            FfmpegInstallResult installResult;
            if (task.IsFaulted)
            {
                installResult = new FfmpegInstallResult
                {
                    Outcome = FfmpegInstallOutcome.Failed,
                    ErrorCode = "install-task-faulted",
                    ErrorDetail = DescribeFailure(task),
                };
            }
            else if (task.IsCanceled)
            {
                installResult = new FfmpegInstallResult
                {
                    Outcome = FfmpegInstallOutcome.Cancelled,
                    ErrorCode = "cancelled",
                };
            }
            else
            {
                installResult = task.Result;
            }

            lock (_gate)
            {
                _workTask = null;

                // 任务属于上一代（已被取消 / 失效）：只清理，不改状态。
                if (taskGeneration != _generation)
                {
                    DiscardOwnedTempFile(taskGeneration);
                    return;
                }

                InstallResult = installResult;

                switch (installResult.Outcome)
                {
                    case FfmpegInstallOutcome.Installed:
                        State = FfmpegDownloadState.Succeeded;
                        ErrorCode = null;
                        ErrorDetail = null;
                        break;
                    case FfmpegInstallOutcome.AlreadyInstalled:
                        // 既有安装未被覆盖，同样视为组件可用。
                        State = FfmpegDownloadState.Succeeded;
                        ErrorCode = null;
                        ErrorDetail = null;
                        break;
                    case FfmpegInstallOutcome.Cancelled:
                        State = FfmpegDownloadState.Cancelled;
                        ErrorCode = "cancelled";
                        ErrorDetail = installResult.ErrorDetail;
                        break;
                    default:
                        State = FfmpegDownloadState.Failed;
                        ErrorCode = installResult.ErrorCode ?? "install-failed";
                        ErrorDetail = installResult.ErrorDetail;
                        break;
                }

                // 安装阶段结束：临时归档的使命完成，由本控制器删除。
                DiscardOwnedTempFile(taskGeneration);
                DisposeCancellationLocked();
            }
        }

        /// <summary>清理本控制器拥有的所有临时文件与后台任务（禁用 / 卸载收尾）。</summary>
        public void Shutdown()
        {
            Invalidate("shutdown", true);
        }

        // ------------------------------ 内部实现 ------------------------------

        private bool ValidateResponse(FfmpegDownloadResponse response, out string errorCode, out string detail)
        {
            errorCode = null;
            detail = null;

            if (response == null)
            {
                errorCode = "download-response-missing";
                return false;
            }

            if (!response.RequestSucceeded)
            {
                errorCode = "download-request-failed";
                detail = string.IsNullOrEmpty(response.Error)
                    ? ("responseCode=" + response.ResponseCode.ToString(CultureInfo.InvariantCulture))
                    : response.Error;
                return false;
            }

            if (response.ResponseCode != 200)
            {
                errorCode = "download-http-error";
                detail = "responseCode=" + response.ResponseCode.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            // 防御性 HTTPS 检查：完整性仍由固定 SHA-256 保证，这里只是纵深防御。
            // 不使用可变地址，也不允许静默降级到明文。
            if (!string.IsNullOrEmpty(response.FinalUrl) &&
                response.FinalUrl.IndexOf("https://", StringComparison.OrdinalIgnoreCase) != 0)
            {
                errorCode = "download-insecure-redirect";
                detail = response.FinalUrl;
                return false;
            }

            return true;
        }

        private void StartWorkLocked(long generation)
        {
            CancellationToken token = _cancellation != null ? _cancellation.Token : CancellationToken.None;
            FfmpegAsset asset = Asset;
            string tempPath = TempFilePath;
            bool probe = PendingProbeAfterInstall;
            int probeTimeout = PendingProbeTimeoutSeconds;

            _workGeneration = generation;
            _workTask = Task.Run(() => RunWork(asset, tempPath, probe, probeTimeout, token), token);
        }

        /// <summary>
        /// 后台工作：复核长度与内容哈希（每次都重新读内容，绝不用元数据缓存），
        /// 然后交给现有 <see cref="FfmpegInstaller"/>。不接触任何 Unity 对象。
        /// </summary>
        private FfmpegInstallResult RunWork(
            FfmpegAsset asset, string tempPath, bool probeAfterInstall, int probeTimeoutSeconds,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            long actualLength;
            try
            {
                actualLength = new FileInfo(tempPath).Length;
            }
            catch (Exception ex)
            {
                return Failure("download-file-unreadable", ex.Message);
            }

            if (asset.ArchiveSizeBytes > 0 && actualLength != asset.ArchiveSizeBytes)
            {
                return Failure("download-size-mismatch",
                    "expected=" + asset.ArchiveSizeBytes.ToString(CultureInfo.InvariantCulture) +
                    " actual=" + actualLength.ToString(CultureInfo.InvariantCulture));
            }

            token.ThrowIfCancellationRequested();

            string actualSha256;
            string hashError;
            if (!FfmpegFileHash.TryCompute(tempPath, out actualSha256, out hashError))
                return Failure("download-hash-failed", hashError);

            if (!FfmpegFileHash.Matches(asset.ArchiveSha256, actualSha256))
            {
                return Failure("download-hash-mismatch",
                    "expected=" + asset.ArchiveSha256 + " actual=" + actualSha256);
            }

            token.ThrowIfCancellationRequested();

            return FfmpegInstaller.Install(new FfmpegInstallRequest
            {
                Asset = asset,
                Layout = _layout,
                ArchivePath = tempPath,
                ProbeAfterInstall = probeAfterInstall,
                ProbeTimeoutSeconds = probeTimeoutSeconds,
            }, token);
        }

        private static FfmpegInstallResult Failure(string code, string detail)
        {
            return new FfmpegInstallResult
            {
                Outcome = FfmpegInstallOutcome.Failed,
                ErrorCode = code,
                ErrorDetail = detail,
            };
        }

        /// <summary>必须持锁调用。失败即丢弃临时文件（本控制器是唯一 owner）。</summary>
        private void FailLocked(string errorCode, string detail, long generation)
        {
            State = FfmpegDownloadState.Failed;
            ErrorCode = errorCode;
            ErrorDetail = detail;

            DiscardOwnedTempFile(generation);
            DisposeCancellationLocked();
        }

        private void DisposeCancellationLocked()
        {
            if (_cancellation == null)
                return;

            try
            {
                _cancellation.Dispose();
            }
            catch
            {
            }
            _cancellation = null;
        }

        private void RequestAbort(long generation)
        {
            if (_abortRequest == null)
                return;

            try
            {
                _abortRequest(generation);
            }
            catch
            {
                // 中止只是尽力而为；即使失败，generation 已失效，
                // 迟到通知也绝不会被接受。
            }
        }

        private void DeleteAllOwnedTempFiles()
        {
            var generations = new List<long>(_ownedTempFiles.Keys);
            for (int i = 0; i < generations.Count; i++)
                DiscardOwnedTempFile(generations[i]);
        }

        /// <summary>删除某一代拥有的临时文件（幂等；文件已不存在也视为成功）。</summary>
        private void DiscardOwnedTempFile(long generation)
        {
            string path;
            if (!_ownedTempFiles.TryGetValue(generation, out path))
                return;

            _ownedTempFiles.Remove(generation);

            // 纵深防御：只删除位于本布局下载目录内的文件。
            if (!FfmpegInstallLayout.IsUnder(_layout.DownloadRoot, path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 删除失败（例如仍被写入句柄占用）不改变状态：
                // Unity 侧的 removeFileOnAbort 与下一次启动的孤儿清理都会兜底。
            }
        }

        private void PruneOwnedTempFiles()
        {
            if (_ownedTempFiles.Count < MaxTrackedTempFiles)
                return;

            long threshold = _generation - MaxTrackedTempFiles;
            if (threshold <= _ownedTempFilesPrunedBelow)
                threshold = _ownedTempFilesPrunedBelow + 1;

            var stale = new List<long>();
            foreach (KeyValuePair<long, string> pair in _ownedTempFiles)
            {
                if (pair.Key <= threshold)
                    stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                DiscardOwnedTempFile(stale[i]);

            _ownedTempFilesPrunedBelow = threshold;
        }

        /// <summary>清理上次运行遗留的孤儿下载文件（只匹配本模块自己的命名前缀）。</summary>
        public int CleanupOrphanDownloads()
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(_layout.DownloadRoot))
                    return 0;

                string[] files = Directory.GetFiles(
                    _layout.DownloadRoot, FfmpegInstallLayout.DownloadFilePrefix + "*");
                for (int i = 0; i < files.Length; i++)
                {
                    string path = files[i];
                    if (!FfmpegInstallLayout.IsUnder(_layout.DownloadRoot, path))
                        continue;

                    lock (_gate)
                    {
                        // 绝不删除当前活动请求正在写入的文件。
                        bool active = false;
                        foreach (KeyValuePair<long, string> pair in _ownedTempFiles)
                        {
                            if (string.Equals(pair.Value, path, StringComparison.OrdinalIgnoreCase))
                            {
                                active = true;
                                break;
                            }
                        }
                        if (active)
                            continue;
                    }

                    try
                    {
                        File.Delete(path);
                        removed++;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return removed;
        }

        private static string DescribeFailure(Task task)
        {
            AggregateException aggregate = task.Exception;
            if (aggregate == null)
                return "unknown";

            AggregateException flat = aggregate.Flatten();
            if (flat.InnerExceptions.Count == 0)
                return aggregate.Message;

            return flat.InnerExceptions[0].Message;
        }
    }
}
