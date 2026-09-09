using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 保留的旧当前分辨率同步捕获后端；当前尚未接入 production RenderSession。
    ///
    /// 只支持：当前游戏实际渲染分辨率（Screen.width × Screen.height）。
    /// 捕获点：WaitForEndOfFrame。
    /// 完成语义：ReadPixels → EncodeToPNG → File.WriteAllBytes 成功，才算一帧已捕获。
    ///
    /// 不调用 ScreenCapture.CaptureScreenshot；不依赖异步完成回调。
    /// 目标文件名：<c>frame_000000.png</c> 等，由调用方传入前缀与补零宽度。
    /// </summary>
    internal static class FrameCaptureDriver
    {
        /// <summary>捕获结果回调（在 Unity 主线程 WaitForEndOfFrame 之后调用）。</summary>
        public delegate void CaptureResultCallback(int frameIndex, bool success, string filePath, string error);

        private static GameObject _host;
        private static CaptureHostBehaviour _behaviour;

        public static bool IsRunning => _host != null && _behaviour != null;

        public static bool Start(
            string outputDirectory,
            string prefix,
            int zeroPadWidth,
            CaptureResultCallback onResult,
            out string error)
        {
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
                GameObject host = new GameObject("ADOFAI.Renderist.FrameCaptureDriver");
                host.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(host);

                CaptureHostBehaviour behaviour = host.AddComponent<CaptureHostBehaviour>();
                behaviour.Configure(outputDirectory, string.IsNullOrEmpty(prefix) ? "frame_" : prefix,
                    zeroPadWidth < 1 ? 1 : zeroPadWidth, onResult);

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

        /// <summary>请求在下一个 WaitForEndOfFrame 捕获指定输出帧。同帧内会被去重。</summary>
        public static void RequestCapture(int frameIndex)
        {
            CaptureHostBehaviour b = _behaviour;
            b?.RequestCapture(frameIndex);
        }

        /// <summary>停止并释放 host / coroutine / 复用纹理。幂等。</summary>
        public static void Stop()
        {
            GameObject host = _host;
            _host = null;
            _behaviour = null;
            if (host != null)
            {
                try { UnityEngine.Object.Destroy(host); } catch { }
            }
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

            private bool _pending;
            private int _pendingIndex;
            private Texture2D _texture;

            public void Configure(string outputDirectory, string prefix, int zeroPadWidth,
                CaptureResultCallback onResult)
            {
                _outputDirectory = outputDirectory;
                _prefix = prefix;
                _zeroPadWidth = zeroPadWidth;
                _onResult = onResult;
                _pending = false;
                _pendingIndex = -1;

                if (_routine == null)
                {
                    _routine = StartCoroutine(Observe());
                }
            }

            public void RequestCapture(int frameIndex)
            {
                if (!_pending)
                {
                    _pending = true;
                    _pendingIndex = frameIndex;
                }
                // 同帧重复请求只保留首次，避免重复捕获。
            }

            private IEnumerator Observe()
            {
                while (true)
                {
                    yield return new WaitForEndOfFrame();
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

                    _texture.Apply(false);

                    byte[] png = _texture.EncodeToPNG();
                    if (png == null || png.Length == 0)
                    {
                        throw new InvalidOperationException("encode-to-png-empty");
                    }

                    File.WriteAllBytes(filePath, png);

                    Log.Debug("FrameCaptureDriver: wrote " + filePath + " (" + png.Length + " bytes)");
                    _onResult?.Invoke(frameIndex, true, filePath, null);
                }
                catch (Exception ex)
                {
                    Log.Exception("FrameCaptureDriver: 捕获输出帧 " + frameIndex + " 失败", ex);
                    _onResult?.Invoke(frameIndex, false, filePath, ex.Message);
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
