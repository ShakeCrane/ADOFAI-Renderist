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
    }

    /// <summary>
    /// 编辑器检测结果。Phase 2.2 第一版只有 Unknown。
    /// 未来版本可能扩展 ProbablyEditor / ProbablyNotEditor，
    /// 但需先确认场景名白名单（由用户在实机中通过诊断段读取真实场景名后填充）。
    /// </summary>
    internal enum EditorEnvDetection
    {
        Unknown,
    }
}
