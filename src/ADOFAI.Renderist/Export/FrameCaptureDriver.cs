using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 确定性编辑器导出的同步 PNG 捕获后端（Phase 3.3.0）。
    ///
    /// 只支持当前游戏实际渲染分辨率（Screen.width × Screen.height）。
    /// 捕获点：WaitForEndOfFrame。完成语义：ReadPixels → EncodeToPNG → File.WriteAllBytes 成功，才算一帧已捕获。
    /// 不调用 ScreenCapture.CaptureScreenshot；不依赖异步完成回调。
    ///
    /// generation 机制：每次成功 Start 分配一个唯一 generation，旧 session 的 EndOfFrame callback
    /// 不会污染新 session。
    /// </summary>
    internal static class FrameCaptureDriver
    {
        /// <summary>捕获结果回调（在 Unity 主线程 WaitForEndOfFrame 之后调用）。</summary>
        public delegate void CaptureResultCallback(long generation, int frameIndex, bool success, string filePath, string error);

        private static GameObject _host;
        private static CaptureHostBehaviour _behaviour;

        private static long _generationCounter;
        private static long _activeGeneration;

        public static bool IsRunning => _host != null && _behaviour != null;

        public static bool Start(
            string outputDirectory,
            string prefix,
            int zeroPadWidth,
            CaptureResultCallback onResult,
            out long generation,
            out string error)
        {
            generation = 0;
            error = null;

            if (IsRunning)
            {
                error = "already-running";
                return false;
            }

            if (string.IsNullOrEmpty(outputDirectory))
            {
                error = "output-directory-empty";
                return false;
            }

            try
            {
                generation = ++_generationCounter;
                _activeGeneration = generation;

                GameObject host = new GameObject("ADOFAI.Renderist.FrameCaptureDriver");
                host.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(host);

                CaptureHostBehaviour behaviour = host.AddComponent<CaptureHostBehaviour>();
                behaviour.Configure(outputDirectory, string.IsNullOrEmpty(prefix) ? "frame_" : prefix,
                    zeroPadWidth < 1 ? 1 : zeroPadWidth, onResult, generation);

                _host = host;
                _behaviour = behaviour;
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 启动失败", ex);
                Stop();
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 请求在下一个 WaitForEndOfFrame 捕获指定输出帧。同帧内会被去重。
        /// generation 与当前 active generation 不一致时返回 false（旧 session 请求被拒绝）。
        /// </summary>
        public static bool RequestCapture(long generation, int frameIndex)
        {
            if (generation != _activeGeneration) return false;
            CaptureHostBehaviour b = _behaviour;
            if (b == null) return false;
            b.RequestCapture(frameIndex);
            return true;
        }

        /// <summary>
        /// 停止并释放 host / coroutine / 复用纹理。幂等。
        /// generation 先失效；只有同步 Shutdown 成功后才丢弃静态 host 引用。
        /// </summary>
        public static bool Stop()
        {
            GameObject host = _host;
            CaptureHostBehaviour behaviour = _behaviour;

            // 先使旧 generation 失效，阻止新的 capture callback / commit。
            _activeGeneration = 0;

            // 先同步让 behaviour 失效（停止 coroutine、清 callback / pending），再销毁 host。
            if (behaviour != null)
            {
                try
                {
                    behaviour.Shutdown();
                }
                catch (Exception ex)
                {
                    Log.Exception("FrameCaptureDriver: 停止失败，保留 host 供重试", ex);
                    return false;
                }
            }

            _host = null;
            _behaviour = null;

            if (host != null)
            {
                try
                {
                    UnityEngine.Object.Destroy(host);
                }
                catch (Exception ex)
                {
                    // host / behaviour 已经失效且 callback 不再可达；保留 false
                    // 让 scheduler 本轮报告异常，下一次 Stop 可幂等收敛。
                    Log.Exception("FrameCaptureDriver: 销毁 host 失败", ex);
                    return false;
                }
            }

            return !IsRunning && _activeGeneration == 0;
        }

        // ================================================================
        // MonoBehaviour 宿主
        // ================================================================

        private sealed class CaptureHostBehaviour : MonoBehaviour
        {
            private Coroutine _routine;
            private string _outputDirectory;
            private string _prefix;
            private int _zeroPadWidth;
            private CaptureResultCallback _onResult;
            private long _generation;

            private bool _pending;
            private int _pendingIndex;
            private Texture2D _texture;
            private bool _stopped;

            public void Configure(string outputDirectory, string prefix, int zeroPadWidth,
                CaptureResultCallback onResult, long generation)
            {
                _outputDirectory = outputDirectory;
                _prefix = prefix;
                _zeroPadWidth = zeroPadWidth;
                _onResult = onResult;
                _generation = generation;
                _pending = false;
                _pendingIndex = -1;
                _stopped = false;

                if (_routine == null)
                {
                    _routine = StartCoroutine(Observe());
                }
            }

            public void RequestCapture(int frameIndex)
            {
                if (_stopped || _generation != _activeGeneration) return;
                if (!_pending)
                {
                    _pending = true;
                    _pendingIndex = frameIndex;
                }
                // 同帧重复请求只保留首次，避免重复捕获。
            }

            /// <summary>同步失效：停止 coroutine、清 pending / callback。调用后再 Destroy host。</summary>
            public void Shutdown()
            {
                _stopped = true;
                _pending = false;
                _pendingIndex = -1;
                _onResult = null;

                if (_routine != null)
                {
                    try { StopCoroutine(_routine); } catch { }
                    _routine = null;
                }
            }

            private IEnumerator Observe()
            {
                while (true)
                {
                    yield return new WaitForEndOfFrame();
                    if (_stopped || _generation != _activeGeneration) yield break;
                    if (!_pending) continue;

                    int index = _pendingIndex;
                    _pending = false;
                    if (index == 0) Log.Info("MasterTimeline Stage=Frame0 AFTER_EOF frameIndex=0");
                    _pendingIndex = -1;
                    CaptureNow(index);
                }
            }

            private void CaptureNow(int frameIndex)
            {
                if (_stopped || _generation != _activeGeneration) return;

                string filePath = BuildFilePath(frameIndex);

                try
                {
                    if (Screen.width <= 0 || Screen.height <= 0)
                    {
                        throw new InvalidOperationException("screen-size-invalid");
                    }

                    EnsureTexture();

                    // 明确从屏幕后端读取，而不是任何 RenderTexture。
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = null;
                    try
                    {
                        _texture.ReadPixels(
                            new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                    }

                    if (_stopped || _generation != _activeGeneration) return;

                    _texture.Apply(false);

                    byte[] png = _texture.EncodeToPNG();
                    if (png == null || png.Length == 0)
                    {
                        throw new InvalidOperationException("encode-to-png-empty");
                    }

                    if (_stopped || _generation != _activeGeneration) return;

                    File.WriteAllBytes(filePath, png);

                    if (_stopped || _generation != _activeGeneration) return;

                    Log.Debug("FrameCaptureDriver: wrote " + filePath + " (" + png.Length + " bytes)");
                    _onResult?.Invoke(_generation, frameIndex, true, filePath, null);
                }
                catch (Exception ex)
                {
                    Log.Exception("FrameCaptureDriver: 捕获输出帧 " + frameIndex + " 失败", ex);
                    _onResult?.Invoke(_generation, frameIndex, false, filePath, ex.Message);
                }
            }

            private void EnsureTexture()
            {
                if (_texture != null && _texture.width == Screen.width && _texture.height == Screen.height)
                {
                    return;
                }

                if (_texture != null)
                {
                    try { UnityEngine.Object.Destroy(_texture); } catch { }
                }

                // RGB24：只读 CPU 纹理，EncodeToPNG 前不触发 GPU 上传依赖。
                _texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            }

            private string BuildFilePath(int frameIndex)
            {
                string indexText = frameIndex
                    .ToString(CultureInfo.InvariantCulture)
                    .PadLeft(_zeroPadWidth, '0');
                string filename = _prefix + indexText + ".png";
                return Path.Combine(_outputDirectory, filename);
            }

            private void OnDestroy()
            {
                if (_routine != null)
                {
                    try { StopCoroutine(_routine); } catch { }
                    _routine = null;
                }

                if (_texture != null)
                {
                    try { UnityEngine.Object.Destroy(_texture); } catch { }
                    _texture = null;
                }
            }
        }
    }
}
