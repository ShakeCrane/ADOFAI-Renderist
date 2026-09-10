using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器环境快照（Phase 3.4.0）。
    ///
    /// 只采集只读诊断信息，不修改任何 Unity 状态。
    /// 不引入 Assembly-CSharp.dll。
    ///
    /// 读取失败处理：
    ///   * SceneName 为 null 表示场景名读取失败；空字符串表示场景名为空。
    ///   * CameraCount、TimeScale、CaptureFramerate、ScreenWidth、ScreenHeight、IsFocused
    ///     使用可空类型，null 表示读取失败。
    ///   * EnvironmentReadFailed 表示核心环境读取失败，Preflight 据此返回 UnknownEnvironment。
    ///   * Detection 是当前正式 Preflight 的编辑器场景 gate；未识别场景不会进入导出。
    /// </summary>
    internal struct EditorEnvSnapshot
    {
        /// <summary>
        /// 当前场景名（SceneManager.GetActiveScene().name）。
        /// null 表示获取失败；空字符串表示场景名为空。
        /// </summary>
        public string SceneName;

        /// <summary>
        /// 当前相机数量（Camera.allCamerasCount）。
        /// null 表示获取失败。
        /// </summary>
        public int? CameraCount;

        /// <summary>当前时间缩放（Time.timeScale）。null 表示获取失败。</summary>
        public float? TimeScale;

        /// <summary>当前固定帧率（Time.captureFramerate）。null 表示获取失败。</summary>
        public int? CaptureFramerate;

        /// <summary>游戏窗口是否处于焦点（Application.isFocused）。null 表示获取失败。</summary>
        public bool? IsFocused;

        /// <summary>屏幕宽度（Screen.width）。null 表示获取失败。</summary>
        public int? ScreenWidth;

        /// <summary>屏幕高度（Screen.height）。null 表示获取失败。</summary>
        public int? ScreenHeight;

        /// <summary>检测结果：场景名匹配白名单则 ProbablyEditor，否则 Unknown。</summary>
        public EditorEnvDetection Detection;

        /// <summary>
        /// 核心环境读取是否失败（如 SceneManager 抛异常）。
        /// 为 true 时 Detection 不应作为判断依据。
        /// </summary>
        public bool EnvironmentReadFailed;

        /// <summary>
        /// 采集当前环境诊断快照。无副作用：只读不写。
        /// 所有 Unity API 调用都包裹 try/catch，避免在异常环境下阻断 GUI。
        /// </summary>
        public static EditorEnvSnapshot Capture()
        {
            var snapshot = new EditorEnvSnapshot
            {
                Detection = EditorEnvDetection.Unknown,
                EnvironmentReadFailed = false,
            };

            try
            {
                snapshot.SceneName = SceneManager.GetActiveScene().name;
            }
            catch (Exception ex)
            {
                Log.Exception("EditorEnvSnapshot: failed to read active scene name", ex);
                snapshot.SceneName = null;
                snapshot.EnvironmentReadFailed = true;
            }

            // 当前已验证正式版：ADOFAI Steam buildid 24397494 / Unity 6000.3.10f1。
            // scnEditor 是当前版本的编辑器场景名；Preflight 只允许该已验证场景。
            if (snapshot.SceneName != null &&
                Array.IndexOf(EditorSceneNames, snapshot.SceneName) >= 0)
            {
                snapshot.Detection = EditorEnvDetection.ProbablyEditor;
            }

            snapshot.CameraCount = TryRead(() => Camera.allCamerasCount);
            snapshot.TimeScale = TryRead(() => Time.timeScale);
            snapshot.CaptureFramerate = TryRead(() => Time.captureFramerate);
            snapshot.IsFocused = TryRead(() => Application.isFocused);
            snapshot.ScreenWidth = TryRead(() => Screen.width);
            snapshot.ScreenHeight = TryRead(() => Screen.height);

            return snapshot;
        }

        private static T? TryRead<T>(Func<T> read) where T : struct
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 当前正式版实机验证得到的编辑器场景名白名单。
        /// ADOFAI 更新后必须重新基于当前游戏版本验证；未识别场景由 Preflight 阻断。
        /// </summary>
        private static readonly string[] EditorSceneNames =
        {
            "scnEditor",
        };
    }

    /// <summary>
    /// 编辑器检测结果。
    /// - Unknown：未识别或非当前已验证编辑器环境
    /// - ProbablyEditor：场景名匹配当前已验证白名单
    /// </summary>
    internal enum EditorEnvDetection
    {
        Unknown,
        ProbablyEditor,
    }
}
