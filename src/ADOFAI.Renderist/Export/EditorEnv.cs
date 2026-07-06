using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 编辑器环境诊断快照（Phase 2.2）。
    ///
    /// 第一版仅采集只读诊断信息，不做白名单匹配。
    /// <see cref="Detection"/> 始终返回 <see cref="EditorEnvDetection.Unknown"/>，
    /// 不作为 Preflight Fail 条件。
    ///
    /// 不引入 Assembly-CSharp.dll。
    /// CanvasCount 暂不实现（需要 UnityEngine.UIModule.dll，本轮不新增引用）。
    /// </summary>
    internal struct EditorEnvSnapshot
    {
        /// <summary>
        /// 当前场景名（SceneManager.GetActiveScene().name）。
        /// 空字符串表示获取失败或场景名为空。
        /// </summary>
        public string SceneName;

        /// <summary>
        /// 当前相机数量（Camera.allCamerasCount）。
        /// -1 表示获取失败。
        /// </summary>
        public int CameraCount;

        /// <summary>检测结果：第一版始终 Unknown。</summary>
        public EditorEnvDetection Detection;

        /// <summary>
        /// 采集当前环境诊断快照。无副作用：只读不写。
        /// 所有 Unity API 调用都包裹 try/catch，避免在异常环境下阻断 GUI。
        /// </summary>
        public static EditorEnvSnapshot Capture()
        {
            var snapshot = new EditorEnvSnapshot
            {
                Detection = EditorEnvDetection.Unknown,
            };

            try
            {
                snapshot.SceneName = SceneManager.GetActiveScene().name ?? string.Empty;
            }
            catch
            {
                snapshot.SceneName = string.Empty;
            }

            // Phase 2.2.1: 基于 Phase 2.2 实机验证（ADOFAI buildid 23935606，
            // Unity 6000.3.10f1），scnEditor 是 ADOFAI 编辑器场景名。
            // 仅诊断展示，不影响 Preflight Pass / Warn / Fail，不阻断任何截图路径。
            if (!string.IsNullOrEmpty(snapshot.SceneName) &&
                Array.IndexOf(EditorSceneNames, snapshot.SceneName) >= 0)
            {
                snapshot.Detection = EditorEnvDetection.ProbablyEditor;
            }

            try
            {
                snapshot.CameraCount = Camera.allCamerasCount;
            }
            catch
            {
                snapshot.CameraCount = -1;
            }

            return snapshot;
        }

        /// <summary>
        /// 实机验证得到的编辑器场景名白名单（基于 Phase 2.2 实机验证，
        /// ADOFAI buildid 23935606，Unity 6000.3.10f1）。
        /// 仅用于 Detection 诊断展示，不作为 Preflight 通过 / 失败条件。
        /// ADOFAI 更新后场景名可能变化，届时需重新实机验证。
        /// </summary>
        private static readonly string[] EditorSceneNames =
        {
            "scnEditor",
        };
    }

    /// <summary>
    /// 编辑器检测结果。
    /// - Unknown：未识别或非编辑器环境
    /// - ProbablyEditor：场景名匹配白名单，疑似编辑器
    ///
    /// 仅为「疑似」，不写「确认」——场景名可能因 ADOFAI 版本更新而变化。
    /// Detection 不影响 Preflight Pass / Warn / Fail，不阻断任何截图路径。
    /// </summary>
    internal enum EditorEnvDetection
    {
        Unknown,
        ProbablyEditor,
    }
}
