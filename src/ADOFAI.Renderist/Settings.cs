using UnityModManagerNet;
using ADOFAI.Renderist.Export;

namespace ADOFAI.Renderist
{
    /// <summary>
    /// Persistent settings for ADOFAI Renderist.
    /// Stored by UMM as a sibling XML file inside the mod folder.
    /// </summary>
    public class Settings : UnityModManager.ModSettings
    {
        /// <summary>
        /// When true, the Log helper also emits debug-level messages.
        /// </summary>
        public bool VerboseLogging = false;

        /// <summary>
        /// Optional absolute output directory. Empty = use the default under
        /// Application.persistentDataPath. Validated and may be rejected; see
        /// Export.OutputPath for the reject-list rules.
        /// </summary>
        public string OutputDirectory = string.Empty;

        // ---------------- Phase 3.4.0: deterministic editor export ----------------

        /// <summary>
        /// Switch for the deterministic editor export session (MasterTimeline).
        /// </summary>
        public bool EditorExportEnabled = false;

        /// <summary>
        /// Target output frame rate for the deterministic editor export session.
        /// 取值范围与默认值由 OutputFpsPolicy 单点定义。
        /// </summary>
        public int EditorTargetFrameRate = OutputFpsPolicy.Default;

        /// <summary>
        /// Single player-facing End Tail value. Its interpretation is selected by
        /// EditorEndTailUnit and frozen when a session starts.
        /// </summary>
        public double EditorEndTailValue = EndTailPolicy.DefaultValue;

        /// <summary>Unit for EditorEndTailValue: Frames, Seconds, or Beats.</summary>
        public EndTailUnit EditorEndTailUnit = EndTailPolicy.DefaultUnit;

        /// <summary>
        /// 是否把每个 output frame 写成 PNG 文件。默认 true，保持既有 PNG 序列行为。
        ///
        /// false = log-only（image output disabled）：帧事务本身完全不变
        /// （native Update/render → WaitForEndOfFrame → source / generation / pending-index 校验
        /// → 成功的帧末回调 → 唯一 CommitFrame），只是跳过图像读回、编码与写盘，
        /// 也不构造 PNG 文件路径。session 仍写入 metadata.json。
        ///
        /// 该值在 session 开始时冻结（由 scheduler 持有），运行中修改 GUI 不影响当前 session。
        /// 与 VerboseLogging 无关。
        /// </summary>
        public bool EditorImageOutputEnabled = true;

        // ---------------- Phase 3.7.0: custom output resolution ----------------

        /// <summary>
        /// 是否使用自定义输出分辨率。默认 false = 沿用当前游戏窗口渲染分辨率
        /// （Screen.width / Screen.height），保持 0.3.6.4 行为。
        ///
        /// true 时使用 EditorCustomResolutionWidth / EditorCustomResolutionHeight
        /// 作为输出（以及 capture RenderTexture、三台原生 Camera aspect）的唯一来源。
        ///
        /// 该值在 session 开始时一次性冻结；运行中修改 GUI 不影响当前 session。
        /// </summary>
        public bool EditorCustomResolutionEnabled = false;

        /// <summary>
        /// 自定义输出宽度（像素）。只在 EditorCustomResolutionEnabled 为 true 时参与导出。
        ///
        /// 必须是正整数，且不得超过当前 GPU 的 SystemInfo.maxTextureSize（真实硬件能力）。
        /// **非法 persisted 值不自动修复**：它保持非法并 fail-closed，直到用户显式改成
        /// 合法值（与 persisted End Tail 的方案 A 语义一致）。
        ///
        /// OutputGeometryPolicy 是唯一判定点；这里不定义任何产品级性能上限。
        /// 该值在 session 开始时一次性冻结。
        /// </summary>
        public int EditorCustomResolutionWidth = OutputGeometryPolicy.DefaultCustomWidth;

        /// <summary>自定义输出高度（像素）。语义同 <see cref="EditorCustomResolutionWidth"/>。</summary>
        public int EditorCustomResolutionHeight = OutputGeometryPolicy.DefaultCustomHeight;

        // ---------------- Phase 3.7.0: supersampling ----------------

        /// <summary>
        /// 超采样倍率（Phase 3.7.0 第二闭环）。默认 1 = 不启用，与第一闭环行为完全一致。
        ///
        /// render = output × 本值：Source RenderTexture 以 render 尺寸渲染，
        /// 再经多级 bilinear 降采样到 output 尺寸后写出 PNG。
        /// 与自定义分辨率开关**互相独立**：legacy-window 模式下同样生效
        /// （此时 output = 冻结窗口尺寸）。
        ///
        /// 必须是 ≥ 1 的正整数。合法性只有两类真实约束：int 表达能力（render 尺寸的
        /// checked 乘法不得溢出）与 SystemInfo.maxTextureSize（对实际 render 尺寸生效）。
        /// **没有产品级上限**：像素总数 / 显存估算 / 编码耗时 / 文件体积都不是判定条件。
        ///
        /// **非法 persisted 值不自动修复**：保持非法并 fail-closed，直到用户显式改成
        /// 合法值（与 persisted End Tail / persisted 宽高 的方案 A 语义一致）。
        /// OutputGeometryPolicy 是唯一判定点。
        ///
        /// 该值在 session 开始时一次性冻结；运行中修改 GUI 不影响当前 session。
        /// </summary>
        public int EditorSupersamplingScale = OutputGeometryPolicy.DefaultSupersamplingScale;

        /// <summary>
        /// Optional safety-only output-frame limit for a session that never reaches
        /// native completion. 0 (default) and the historical default 36000 both mean
        /// "not configured": no total frame-count and no total-duration limit exists.
        /// Any other positive value is used verbatim as an output-frame limit and is
        /// never capped by the product layer (int.MaxValue is only the data-type
        /// boundary of this field, not a recommended or supported performance target).
        ///
        /// Known ambiguity, deliberately accepted: safety never had a GUI, so a 36000
        /// written by UMM is the historical default, but a hand-edited 36000 is
        /// indistinguishable from it. Both are intentionally migrated to "not
        /// configured" (unbounded); use any other positive value to opt in explicitly.
        ///
        /// This is not a normal completion condition.
        /// </summary>
        public int EditorExportSafetyFrameLimit = 0;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
