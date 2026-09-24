using System;
using UnityEngine.Networking;
using ADOFAI.Renderist.Ffmpeg;

namespace ADOFAI.Renderist
{
    /// <summary>
    /// Unity 侧的 FFmpeg 下载驱动：唯一持有 <see cref="UnityWebRequest"/> 的地方。
    ///
    /// 职责边界：
    ///   * <b>只在 Unity 主线程</b>创建请求、调用 <c>SendWebRequest()</c>、读取进度、
    ///     收取 <c>completed</c>、<c>Abort()</c> 与 <c>Dispose()</c>。
    ///   * 只把结果整理成纯数据 <see cref="FfmpegDownloadResponse"/> 交给
    ///     <see cref="FfmpegDownloadController"/>；所有状态机、哈希校验与安装都在
    ///     控制器与后台 <c>Task</c> 中完成，后台线程不接触 Unity 对象。
    ///
    /// 本文件位于 <c>Ffmpeg/</c> 之外，正是为了让 <c>Ffmpeg/</c> 保持 Unity-free，
    /// 从而能被 net48 回归工程直接编译测试。
    ///
    /// 安全相关：
    ///   * 使用固定 manifest URL，不做地址替换，不使用可变 latest。
    ///   * <c>certificateHandler = null</c> → 使用平台默认证书校验；
    ///     **绝不**安装自定义或放行的证书处理器。
    ///   * 不修改全局 TLS 配置、不改系统 PATH、不需要管理员权限。
    ///   * <c>removeFileOnAbort = true</c> 只作为 teardown 安全网：临时文件的
    ///     清理责任仍然完全属于 <see cref="FfmpegDownloadController"/>。
    /// </summary>
    internal sealed class UnityFfmpegDownloadDriver
    {
        /// <summary>重定向次数上限；超过即失败，不做无界跟随。</summary>
        private const int RedirectLimit = 10;

        private UnityWebRequest _request;
        private UnityWebRequestAsyncOperation _operation;
        private long _generation = -1;
        private bool _completionDelivered;
        private Action<long, FfmpegDownloadResponse> _onCompleted;
        private Action<long, long, long> _onProgress;

        /// <summary>当前是否有活动请求。</summary>
        public bool IsActive
        {
            get { return _operation != null; }
        }

        public long ActiveGeneration
        {
            get { return _generation; }
        }

        /// <summary>
        /// 发起一次下载。必须在 Unity 主线程调用。
        /// 失败时返回 false 并给出原因码，且不留下任何已分配资源。
        /// </summary>
        public bool TryStart(
            FfmpegDownloadPlan plan,
            Action<long, long, long> onProgress,
            Action<long, FfmpegDownloadResponse> onCompleted,
            out string errorCode,
            out string errorDetail)
        {
            errorCode = null;
            errorDetail = null;

            if (plan == null || !plan.Started)
            {
                errorCode = "plan-not-started";
                return false;
            }

            if (_operation != null)
            {
                errorCode = "driver-busy";
                return false;
            }

            try
            {
                var handler = new DownloadHandlerFile(plan.TempFilePath);
                // teardown 安全网；临时文件的正常清理仍由控制器负责。
                handler.removeFileOnAbort = true;

                var request = new UnityWebRequest(plan.Url, UnityWebRequest.kHttpVerbGET)
                {
                    downloadHandler = handler,
                    redirectLimit = RedirectLimit,
                    // 不设请求超时：慢链路属于真实网络条件，不能变成人为的性能上限。
                    // 卡住时由用户取消（Abort）收敛。
                    timeout = 0,
                    // 平台默认证书校验；绝不自定义或放行。
                    certificateHandler = null,
                };

                _request = request;
                _generation = plan.Generation;
                _completionDelivered = false;
                _onProgress = onProgress;
                _onCompleted = onCompleted;

                _operation = request.SendWebRequest();
                return true;
            }
            catch (Exception ex)
            {
                errorCode = "download-start-failed";
                errorDetail = ex.Message;
                DisposeRequest();
                return false;
            }
        }

        /// <summary>
        /// 每帧调用（Unity 主线程）。请求完成后收集一次结果并释放请求资源。
        /// </summary>
        public void Pump()
        {
            UnityWebRequestAsyncOperation operation = _operation;
            if (operation == null)
                return;

            try
            {
                if (!operation.isDone)
                {
                    ReportProgress();
                    return;
                }

                if (!_completionDelivered)
                {
                    _completionDelivered = true;
                    FfmpegDownloadResponse response = BuildResponse();
                    long generation = _generation;

                    // 先释放请求与文件句柄，再交付结果：
                    // 控制器可能在收到结果后立刻删除临时文件，句柄必须先关闭。
                    DisposeRequest();

                    if (_onCompleted != null)
                        _onCompleted(generation, response);
                    return;
                }
            }
            catch (Exception)
            {
                // 任何驱动层异常都不能让主线程崩溃：释放资源并当作失败交付。
                long generation = _generation;
                DisposeRequest();

                if (!_completionDelivered && _onCompleted != null)
                {
                    _completionDelivered = true;
                    _onCompleted(generation, new FfmpegDownloadResponse
                    {
                        ResponseCode = 0,
                        RequestSucceeded = false,
                        Error = "download-driver-exception",
                    });
                }
                return;
            }

            DisposeRequest();
        }

        /// <summary>
        /// 中止并释放当前请求（Unity 主线程）。
        /// 仅当 <paramref name="generation"/> 与当前请求匹配时才动作，
        /// 因此新任务不会被旧的中止请求影响。
        /// </summary>
        public void AbortAndDispose(long generation)
        {
            if (_operation == null)
                return;

            if (generation >= 0 && generation != _generation)
                return;

            DisposeRequest();
        }

        /// <summary>无条件释放（Mod 卸载 / 禁用收尾）。</summary>
        public void DisposeRequest()
        {
            UnityWebRequest request = _request;
            _request = null;
            _operation = null;
            _onProgress = null;
            _onCompleted = null;

            if (request == null)
                return;

            try
            {
                if (!request.isDone)
                    request.Abort();
            }
            catch
            {
            }

            try
            {
                request.Dispose();
            }
            catch
            {
            }
        }

        private void ReportProgress()
        {
            if (_onProgress == null)
                return;

            try
            {
                UnityWebRequest request = _request;
                if (request == null)
                    return;

                long downloaded = (long)request.downloadedBytes;
                long total = 0;

                string contentLength = request.GetResponseHeader("Content-Length");
                if (!string.IsNullOrEmpty(contentLength))
                {
                    long parsed;
                    if (long.TryParse(contentLength, out parsed))
                        total = parsed;
                }

                _onProgress(_generation, downloaded, total);
            }
            catch
            {
            }
        }

        private FfmpegDownloadResponse BuildResponse()
        {
            var response = new FfmpegDownloadResponse();
            UnityWebRequest request = _request;

            if (request == null)
            {
                response.RequestSucceeded = false;
                response.Error = "request-missing";
                return response;
            }

            response.ResponseCode = (int)request.responseCode;
            response.ReportedDownloadedBytes = (long)request.downloadedBytes;
            response.Error = request.error;

            // Unity 6 使用 UnityWebRequest.result 表达传输结果。
            response.RequestSucceeded = request.result == UnityWebRequest.Result.Success;

            try
            {
                response.FinalUrl = request.url;
            }
            catch
            {
            }

            return response;
        }
    }
}
