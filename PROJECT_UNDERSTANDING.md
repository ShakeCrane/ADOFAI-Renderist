# ADOFAI Renderist 项目理解

> 本文件只保留当前项目状态、后续 Agent 持续需要的已验证事实、已确定技术路线、失败模式与未解决风险。
> 规则冲突：当前用户要求 > 网页版 GPT 项目指令 > `AGENTS.md` > 本文件。
> 事实冲突：实际仓库、diff、构建、测试、日志和实机结果 > 本文件。

---

## 1. 当前目标与硬边界

ADOFAI Renderist 是基于 **Unity Mod Manager（UMM）** 的 ADOFAI 编辑器内非实时渲染导出 Mod。

当前**已实现**路线：编辑器内原生 Camera → Renderist-owned RenderTexture → PNG 序列 / Log-only；`MasterTimeline` 控制逻辑帧。`0.3.8.0` 阶段在保留两条现有路径的同时增加独立 MP4 输出（技术决策与待验证项见 §2.1）：**L1（FFmpeg 组件管理 + HTTPS 下载）已完成并通过实机验收**；**L2（独立 FFmpeg 视频进程管线）已实现为 Unity-free 模块并通过独立 net48 回归，但尚未接入 Unity**（§2.8）；**L3（Unity MP4 帧事务）尚未实施**。

硬边界：

- 仅支持最新已验证 ADOFAI 正式版；当前基线：Steam public buildid `24397494`，`Assembly-CSharp.dll` FileVersion `0.4.3.0`。
- Unity `6000.3.10f1` / Mono；UMM `0.33.0`；Harmony `2.3.6.0`；`net48` / C# `9.0`。
- UMM-only；禁止 BepInEx / MelonLoader / Doorstop / 多 Loader 抽象层。
- editor-first；当前不实现 replay / TUFReplay / Creplay；Official Autoplay 只用于辅助/参考验证。
- 不提交游戏、Unity、UMM、Harmony、第三方 Mod DLL 或反编译源码。
- README 由用户维护，默认不修改。
- 发布包固定为 `Info.json` + `ADOFAI.Renderist.dll` + `LICENSE`。
- **非实时导出原则**：不得因 wall-clock 性能、PNG 编码耗时、文件数量、磁盘写入速度或预计导出时长人为限制合法导出参数。性能问题只能用 warning / estimate / benchmark / recommendation 表达，不能用 legality gate 禁止参数。
- **没有内建的最大总帧数或最大导出时长**。正常终止 authority 是 canonical completion + End Tail；另保留用户 cancel、异常 fail-closed 与无进展 watchdog。
- **输出分辨率原则（0.3.7.0 起）**：自定义分辨率只受**真实表达能力**（Settings 与 Unity API 都是 int 的正整数）与**真实硬件能力**（`SystemInfo.maxTextureSize`）约束。像素总数、显存估算、预计编码耗时与预计文件体积**不是**合法性条件，不得据此禁止参数。非法 persisted 宽高**不自动修复**，保持 fail-closed 直到用户显式改正。

---

## 2. 当前版本、Git 与工具基线

| 项目 | 当前状态 |
| --- | --- |
| 产品版本 | `0.3.8.0`（L2 实现轮按用户要求**未递增第四位**，Phase 文案也未修改） |
| Phase | `Phase 3.8.0 FFmpeg Video Export Pipeline — L1 Component Management`（代码内文案**保持 L1**，见 §2.8 结尾） |
| 版本定位 | **当前 `0.3.8.0` = `Phase 3.8.0 FFmpeg Video Export Pipeline`**：**L1（组件管理 + HTTPS 下载）已完成并通过实机验收**，**L2（FFmpeg 视频进程管线）已在本轮实现并通过独立 net48 回归**（§2.8），**L3（Unity MP4 帧事务）未实施** —— 因此 `0.3.8.0` **仍然不代表** MP4 导出可用。其下 `0.3.7.1` = **Custom Resolution + Supersampling 双闭环稳定性收敛版**（**上一稳定基线**；`0.3.7.0` 功能不变、仅第四位递增）。第一闭环 Custom Resolution 与第二闭环 Supersampling **均已在当前 Gamma / Direct3D11 环境通过实机验收**；更早 `0.3.6.4` = **Log-only Frame Transactions（image output disabled）** |
| 稳定实机基线 | **`0.3.7.1`**（= `0.3.7.0` 双闭环 + `ce34ad4` metadata 语义修正；构建身份见 §12）。`0.3.7.0` 第一闭环（Custom Resolution）与第二闭环（Supersampling，含 **scale=4 完整导出**）均已实机通过：第一闭环见 §9.1；第二闭环见 §9.7.1 + §9.7.3。更早的稳定基线 `0.3.6.4`（PNG 与 Log-only 同谱面跑通）仍见 §9.1。**未覆盖边界**（Linear 色彩空间、极端资源失败）见 §9.7.3。 |
| L1 实机状态 | **`0.3.8.0`（DLL `AE2326D3…` / `+4d35801862d0828ea248d9934e8ebe74af11f2bb`）已通过用户最小实机验收**：UMM 显示 `0.3.8.0`、FFmpeg `Ready`、`libx264` / `mp4` / `rawvideo`、禁用并重新启用、无新可见异常。**L1 主路径已具备阶段性基线证据**；未覆盖边界（活动下载生命周期、异常网络、真实回调竞态）见 §2.7。 |
| L2 实现状态 | **已实现（源码级 + 独立 net48 回归）**：`FfmpegVideoCommand` / `FfmpegVideoVerifier` / `FfmpegVideoPipeline` 三个 Unity-free 模块 + 独立测试。真实 Gyan 9.0.2 fixture 回归 **128 passed / 0 failed / 0 skipped**（无 fixture 时 116 / 0 / 12）。**未接入 Unity**，`CommitFrame` / `MasterTimeline` / `FrameCaptureDriver` / `DeterministicFrameScheduler` 一行未改；**没有任何 Mono 实机证据**。详见 §2.8。 |
| 当前开发方向 | **`0.3.8.0 — FFmpeg Video Export Pipeline`：L1 完成并实机验收，L2 已实现（§2.8），L3（Unity MP4 帧事务 / Finalizing 接入）未开始**。第二闭环仍然**未覆盖** Unity **Linear** 色彩空间与极端资源失败路径（详见 §9.7.3），不得由 Gamma 结果外推。 |
| 下一阶段 | **`L3 — Unity MP4 Frame Transactions` 的技术规划**（事件驱动交付、主板程回投、Finalizing 关口、watchdog 分离）。前提仍是 §2.2 的视觉确定性补测结论；`0.3.8.0` 的 DLL **不具备** MP4 导出能力。 |

`0.3.6.2` 相对 `0.3.6.1` 的四个 hardening 点（功能语义不变，只收敛异常路径与输入判定）：

1. **cleanup host ownership**：`Destroy(host)` 成功前不丢 `_host` / `_behaviour`；failure 保留 ownership，下一次 `Stop()` 可重试。
2. **capture target ownership**：`Release` / `Destroy` 成功前不丢 `_captureTarget`；live Camera 仍引用 target 时绝不销毁；failure 保留 residual ownership。
3. **activation ownership**：RenderTexture 创建成功后不存在 untracked ownership window；Camera assignment 之前先 publish ownership；partial Camera assignment failure 走同一个 `RestoreCameraSource`；rollback failure 保留 Camera refs / saved targets / capture target，下一次 `Stop()` 继续收敛。
4. **End Tail numerical hardening**：直接 Frames 输入使用**固定绝对容差 `1e-10`**（不再使用 magnitude-relative tolerance）；Seconds / Beats computed frame count 只用 `max(1e-10, half-ULP)` 吸收浮点表示误差；不引入任何 FPS / frame / duration 人为产品上限。

对应实现提交：`8bceeef`（cleanup ownership + End Tail validation）与 `1af1205`（activation ownership）。

`0.3.6.3` 的两个闭环（提交 `c9dc977` native pre-entry 正式化、`c34558b` persisted End Tail 语义修正，`f9dd15a` 发布）：

1. **native deterministic pre-entry / count-in capture 正式化**：删除 TEMP 总开关与全部调查 instrumentation，pre-entry 成为正式产品路径（PreEntryClock / grid bracket / scoped `Countdown_Update` lifecycle bridge / hidden zero-scaled-time phase / G partial timestep / 共享 capture·progress watchdog）；`FrameCaptureDriver` 恢复纯捕获职责。
2. **persisted End Tail 语义修正（方案 A）**：GUI 初始化不再 sanitize 或写回 persisted 值；非法值保持非法并 fail-closed，直到用户显式输入合法值（见 §5.2）。

`0.3.6.4` 的闭环 —— **Log-only Frame Transactions**：

1. **输出模式**：`Settings.EditorImageOutputEnabled`（默认 `true` = 既有 PNG 序列行为）在 **session 开始时一次性冻结**；GUI 提供「输出 PNG 图像」开关与关闭说明；与 `VerboseLogging` 无关（见 §8.1）。
2. **单一帧事务**：两种模式共用同一 scheduler、同一 `WaitForEndOfFrame` coroutine、同一结果回调与**唯一** `CommitFrame`；log-only 只跳过图像读回 / 编码 / 写盘，并以 `imageWritten=false` 返回成功帧末结果。帧事务本身（Camera source / RenderTexture 接管、source·generation·pending-index 校验、cleanup）**两个模式完全相同**。
3. **计数拆分**：逻辑 authority（`logicalFrameCount` / `frameTransactionRequestCount` / `tailFramesCommitted`）与 PNG 计数（`captureRequestCount` / `writtenPngFrameCount` / `capturedFrameCount` / `tailFramesCaptured`）分离；completion / End Tail / safety limit / progress watchdog 只使用逻辑 authority（见 §8.1）。
4. **未新增 Harmony Patch、未引入新的 ADOFAI 内部 API、未新建第二套 scheduler / driver**。

`0.3.7.0` 第一闭环 —— **Custom Resolution（自定义分辨率）**：

1. **输出几何单一 authority**：新增 `OutputGeometryPolicy`，是分辨率模式、合法性判定与解析的唯一单点。自定义分辨率关闭（默认）→ 沿用 `Screen.width/height`，保持 `0.3.6.4` 行为；开启 → 使用用户指定的正整数宽高。合法性只有两类真实约束：int 正整数表达能力 + `SystemInfo.maxTextureSize` 硬件能力。
2. **session 开始时冻结**：几何在 `DeterministicFrameScheduler.TryStart` 内解析一次并冻结；`FrameCaptureDriver` 只消费冻结值，**不再读取 Screen**（因此 session 中途改变窗口尺寸不会造成 RT 尺寸与 aspect 不一致）。运行中修改 GUI 不影响当前 session。
3. **三台原生 Camera 统一 aspect ownership**：激活前先只读检查三台 Camera 的实际 aspect（可读 / 有限 / 为正 / 三台互相兼容），不通过即 fail-closed 且**尚未写入任何 Camera**；通过后在同一 ownership transaction 内先登记 `targetTexture` + aspect 的全部可恢复状态，再写入统一的输出 aspect。cleanup 只在当前值仍等于 Renderist 写入值时才 `ResetAspect()` 恢复 Unity 自动行为；已被外部流程改写的值不覆盖。
4. **单 RenderTexture 基线与 EOF 帧事务不变**：仍只有一个 capture RT（ARGB32 / depth 24 / MSAA 1 / 无 mipmap），仍走同一个 `WaitForEndOfFrame` 与唯一 `CommitFrame`；PNG 与 log-only 共用同一尺寸、同一 RT、同一 aspect。**未引入 Blit、downsample RT 链或第二套 scheduler / driver，也未新增任何 ADOFAI Hook。**
5. **metadata 与运行时 inventory**：session metadata 新增输出几何（模式 / persisted 配置宽高 / 冻结宽高 / 统一 aspect）与只读运行时渲染环境 inventory（`colorSpace`、GPU 型号与版本、`maxTextureSize`、capture RT 的 format / graphicsFormat / MSAA / mipmap）。`phase` 文案同步为 `Phase 3.7.0 Custom Resolution & Supersampling`。

`0.3.7.0` 第二闭环 —— **Supersampling & Downsampling**（提交 `2585664` + `7efcacd`，metadata 语义修正 `ce34ad4`；**已实机验收**，见 §9.7.1 / §9.7.3）：

1. **整数倍率 + 多级 bilinear 降采样**：`OutputGeometryPolicy` 扩展出 `SupersamplingScale`（默认 `1` = 关闭）。`render = output × scale`，用 `checked` 乘法计算，溢出即 fail-closed（不 clamp、不降级）。`SystemInfo.maxTextureSize` 对**实际 render 尺寸**生效；scale=1 时 render == output，因此硬件判定与第一闭环完全等价（错误码也保持 `geometry-width-exceeds-hardware-max`）。**没有产品级倍率 / 像素数 / 显存 / 耗时 / 体积上限**。
2. **降采样链规划器（`multi-stage-bilinear`）**：级别按**相对 output 的整数倍率**递减：`nextFactor = factor / 2 + factor % 2`（= `ceil(factor/2)`），每级尺寸 = `(outputWidth × nextFactor, outputHeight × nextFactor)`。因此 **1 → 空链；2 → [1]；3 → [2,1]；4 → [2,1]；5 → [3,2,1]**，**每相邻两级比例 ≤ 2:1**，**每级精确保持宽高比**（不使用宽高分别 ceil-halving），末级精确等于 output。规划器是纯函数，可被 harness 全覆盖。（该比例关系**不等于**「普遍等价于 2×2 box 平均」——等价性边界见第 11 项。）
3. **Source RT 与降采样链的 ownership**：Source RT 仍是 ARGB32 / depth 24 / MSAA 1 / 无 mipmap；降采样各级由**已创建的 Source descriptor 派生**，只改尺寸 / depth（0）/ MSAA / mipmap / dynamic scale / bindMS / random-write，**不改 graphicsFormat 与 sRGB 语义**（构造性一致）。Source 与每一级都在**构造成功后立即登记** ownership，之后才做属性设置 / `Create` / `IsCreated` 校验；**全部 RT 准备成功后**才接管 Camera。不使用 `RenderTexture.GetTemporary`（池化生命周期无法与 residual / Stop 重试语义共存）。
4. **降采样只在 PNG 且 scale>1 时创建**：链在 activation 时一次性创建、逐帧复用；每级 `filterMode=Bilinear` / `wrapMode=Clamp`；Blit 前断言 source ≠ destination。**最终 `ReadPixels` 与 `Texture2D` 恒为 output 尺寸**（Source 是 render 尺寸，两者分离）。
5. **Log-only 与 scale>1**：仍使用相同 `renderWidth/renderHeight` 创建高分辨率 Source RT、维持相同 Camera aspect ownership 与同一个 EOF 事务，但**不创建降采样 RT、不 Blit、不 ReadPixels、不建 Texture2D、不写文件**（并在 log-only 分支断言链为空）。
6. **scale=1 完全绕过降采样路径**：不规划链、不建额外 RT、不调用 `Graphics.Blit`、不触碰 `GL.sRGBWrite`、直接从 Source RT ReadPixels ⇒ 第一闭环行为不变。
7. **GPU 状态 ownership（`RenderTexture.active` / `GL.sRGBWrite`）**：两者各自登记独立 restore token 并**独立恢复**。**Gamma 下不触碰 `GL.sRGBWrite`**；**Linear 下每次 Blit 按 destination 的实际 sRGB 语义（`GraphicsFormatUtility.IsSRGBFormat(destination.graphicsFormat)`）设置它**。保存失败且尚未修改任何状态时**不产生虚假 residual**。任一 token 未恢复 ⇒ 当前帧失败（不 `Apply` / 不 `EncodeToPNG` / 不写盘 / 不 commit），并保留 residual 由下一次 `Stop()` 分别重试；**GPU 状态未全部恢复前绝不 Release / Destroy 任何可能仍被其引用的 RT**。CPU 侧的 `Apply` / 编码 / 写盘严格发生在 GPU 状态全部恢复之后。
8. **residual gate 扩展**：`FrameCaptureDriver.HasOwnedDownsampleChain` 与 `HasResidualGpuState` 并入 `DeterministicFrameScheduler.HasResidualOwnership()`，因此未收敛时下一个 session 仍会在创建目录前被 residual gate 拒绝。
9. **metadata**：新增 8 键 —— `geometryConfiguredSupersamplingScale`、`supersamplingScale`、`renderWidth`、`renderHeight`、`downsampleLevelCount`、`downsampleAlgorithm`、`downsampleRenderTextureFormat`、`downsampleRenderTextureGraphicsFormat`（实测 `0.3.6.x` 39 键 → 第一闭环 58 键 → 本闭环 **66 键**，无键被删除）。语义边界：`outputWidth/Height` = **最终 PNG 尺寸**；`renderWidth/Height` = Source RT 尺寸；`captureWidth/Height` = 三台 Camera 实际渲染进入的 Source RT 尺寸。scale=1 时三者相等；scale>1 时 capture/render 大于 output。
   - **`downsampleLevelCount` 语义（`ce34ad4` 起确定）**：= **实际创建**的 Downsample RT 级数（不含 source），只在 Source 成功激活之后由 scheduler 从 `FrameCaptureDriver` 快照，并在终态 metadata 回填。因此：scale=1 PNG → `0`；scale=2 PNG → `1`；scale=3 PNG → `2`；scale=4 PNG → `2`；**scale=4 log-only → `0`**（高分辨率 Source 仍照常报告，`renderWidth/Height` 仍为 4320）；未激活 / activation 失败 → `0`。**计划级数不另设字段**，可由 `supersamplingScale` 与 `OutputGeometryPolicy.TryBuildDownsampleSteps` 推算。
   - 早期构建（`7efcacd` 及更早）该字段写的是**计划级数**，因此 log-only + scale>1 会显示 `2` 而实际链为 0 级 —— 这是**历史语义**，`ce34ad4` 已修正。
   - `EditorExportReadiness.DownsampleLevelCount`（GUI 就绪报告）仍是**计划**值，属 session 开始前的展示；它与 session metadata 的字段同名但语义不同，不要互相引用。
10. **未新增 Harmony Patch、未新增 ADOFAI 内部 API、未新增程序集引用**（`Graphics.Blit` / `GL.sRGBWrite` / `GraphicsFormatUtility` 都在已引用的 `UnityEngine.CoreModule`），单一 scheduler / 单一 driver / 单一 `WaitForEndOfFrame` / 唯一 `CommitFrame` 全部保持。
11. **算法命名与等价性**：实现名称为 **`multi-stage-bilinear`**（逐级 bilinear 采样）。**不要**把它普遍等价为 "box filtering"：只有相邻两级恰为 2:1 时，bilinear 采样才近似 2×2 box 平均；`3W→2W`（1.5:1）一类比例并不等价于 box。色彩统计中已实测到**轻微整体压暗**（scale=4 vs scale=1：Δ ≈ (−0.171, −0.131, −0.124)/255，三通道同向等量，无偏色）；该现象的**具体因果归因尚未验证**，不得把 Gamma 空间平均写成唯一或已确定的原因（证据边界见 §9.7.3）。以上均**不是**对 box filter 的等价声明。

版本同步事实：

- `scripts/set-version.ps1` 实际自动修改：`mod/Info.json Version`、csproj `<Version>`、`ModEntry.ModVersion`、`ModEntry` 启动日志里的 version + phase。
- `-Phase` **不是全仓库 phase 同步器**：`EditorExportSession.PhaseLabel`、`ModEntry` / `FrameCaptureDriver` 类注释等 phase 文案仍需人工核对。脚本 DESCRIPTION 对“只修改三类文件”的描述是准确的，但 SYNOPSIS“Synchronizes ... version and phase text”容易被理解得过宽；当前属于非阻塞 tooling debt。
- `package-release.ps1` 在打包前交叉校验 Info.json / csproj / `ModEntry.ModVersion`。
- 本地编译引用由 `scripts/prepare-references.ps1` 从真实 ADOFAI / UMM 安装生成 ignored `build/local.props`；仓库不跟踪 `references/`。
- `Assembly-CSharp.dll` 只作为当前游戏内部行为调查基线，不是 compile-time reference。

---

### 2.1 Event-Driven A+ 视频导出设计（L3 仍待实施；L2 已实现，见 §2.8）

**调查依据**：本次在干净工作区核对 HEAD `01555e2300d38c938801d69d6d4387691ee96b58`、版本点、`MasterTimeline`、`DeterministicFrameScheduler`、`FrameCaptureDriver`、`OutputGeometryPolicy`、`EditorExportController`、`EditorExportSession`、Settings / GUI / preflight / metadata。以下“已确认”均指当前 `0.3.7.1` 源码静态事实，不是 MP4 实机验收。

- **已确认基线**：唯一 `MasterTimeline` 用已提交的 `_outputFrameIndex` 映射输出/谱面时间；PNG / Log-only 共用一个 EOF coroutine 和 `OnCaptureResult` → 唯一 `CommitFrame`。当前捕获回调在 Unity 主线程同步执行；PNG 在 ReadPixels、GPU 状态恢复、`Texture2D.Apply`、PNG 编码及写盘成功后提交，Log-only 在帧末校验后提交。Source RT 与下采样链由冻结几何驱动；当前链只在 PNG 且 scale>1 时创建。metadata 的 `imageOutputEnabled` 是旧两模式布尔语义，不足以表示独立 MP4 类型。
- **已确认的改造点**：`PrepareFrame` 与 `PreparePreEntryFrame` 把跨 Unity 帧 `_pendingCapture` 视为失败；视频等待 IO 时不能沿用该判断。现有捕获/进展 watchdog 均为 30 秒 wall-clock deadline；合法的编码背压须从 EOF 捕获超时中分离。`CommitFrame` 内包含 B−1 / G timeScale 转换、End Tail 完成判断，不能另设视频提交点。`EditorExportController.FinalizeFromScheduler` 当前把 scheduler Completed 直接记为 session Completed；视频须增加 FFmpeg 封装确认关口。
- **已确定路线（用户批准，尚未实现）**：保留 PNG 与 Log-only，新增互斥的 MP4 输出类型；PNG 与 MP4 在 GUI 中平级，Log-only 可继续作为独立验证选项。一个 session 冻结输出类型、几何、FPS、编码参数及 FFmpeg 可执行文件身份。MP4 沿用现有 Camera / EOF / scale>1 降采样链，从最终 output 尺寸读回，复制到由视频管线独占的完整 CPU 帧缓冲；不编码/写入中间 PNG，不建立 Renderist 主动管理的多帧队列。主线程只做 Unity / GPU 操作和短暂交付，后台异步写 FFmpeg stdin；保持至多一帧在途，完整帧写入成功的完成事件回到 Unity 主线程并通过 session generation + pending index 校验后才调用原 `CommitFrame`。下一帧的准备和逻辑时间推进必须等待该提交；IO 慢则自然背压，不能用固定单帧时限或性能估算拒绝合法工作。等待 FFmpeg 不使用 Update / Coroutine / 固定间隔轮询；Unity 既有 Tick 仍负责既有环境与生命周期观察，不充当视频 IO 探测器。
- **终态与隔离（待实现）**：FFmpeg stdin 完整写入只说明帧已交付，**不是 MP4 成功**。canonical completion / End Tail 排空后关闭 stdin、异步等待进程退出及 stderr 完成，确认退出码 0 和临时 MP4 有效，再将同卷临时文件发布为最终文件，随后标记 session Completed。取消/失败时废弃当前 generation、关闭管道、终止并回收进程、清理或隔离临时文件；迟到回调不能提交新 session 帧，cleanup residual 未收敛不得重开。后台线程不接触 Unity 对象。Unity 主线程完成通知采用事件驱动调度（当前环境的 SynchronizationContext.Post 已由临时探针验证，正式 IO 仍待验证），不得用定期轮询 IO 代替。
- **安装路线（L1 下载代码已实现，目标游戏收尾仍未验收，见 §2.6）**：优先级为用户明确指定路径 → Renderist 私有安装 → 系统 PATH → 用户主动选择一键安装；明确指定的路径失效时报错，不能静默换用其他候选（该语义已由回归测试固定）。Gyan 9.0.2 Release Essentials ZIP 已固化为 manifest 中**唯一可安装资产**（固定 tag 地址 + 归档 SHA-256 + 归档字节数 + 内部 `ffmpeg.exe` / `ffprobe.exe` 固定 SHA-256）；BtbN 的日构建只作能力交叉验证，不把可变 `latest` 或短期保留资产当长期回退源。安装须在用户点击后经 HTTPS 获取固定资产、校验哈希、只提取白名单内容到同卷 staging、验证可执行能力并原子发布版本目录；不修改 PATH、不要求管理员权限、不覆盖现有安装、不进入三文件发布 ZIP。MP4 + H.264 / `libx264` 软件编码仍是初期建议，Gyan 构建为 GPLv3，分发与告知义务须在发布前审查。FFmpeg 缺失不阻断 PNG / Log-only。
- **未解决 / 待验证**：Unity RGB24 CPU 像素缓冲的长度、行顺序与生命周期；等待编码器时 native Update、pre-entry lifecycle 与 visual clock 是否严格停在同一已渲染帧；正式 IO 的主线程回投与**活跃新 session**取消竞态；FFmpeg 故障处理与文件发布虽有 §2.3 的独立进程证据，仍未接入 Renderist / Unity Mono；Gamma / Linear 色彩差异与奇数尺寸 H.264 4:4:4 的目标播放器兼容性也未验证。实施验证拆为静态不变量、独立进程故障注入、Unity 实机三层；**已有独立进程 MP4 证据，仍无 Renderist MP4 实机验证**。

### 2.2 Event-Driven A+ 最小延迟交付探针（2026-09-23；探针已清理）

调查基线为 HEAD `731b1866249c9ea6e2ecec31a6b9ffe63bcc9c0b`，产品仍为 `0.3.7.1`。临时探针构建及实机部署 DLL SHA-256 均为 `97731D386002CCF58C387F0F794D3A17EB0A612D9D7C1FD19548FED2322C1F71`；测试后已从正式源码移除，并以当前 HEAD 源码重新构建、部署（清理后 DLL SHA-256 `75A026E54075296E919AA890991A0A6951AAD61B7FDA6E95F7E4AD5BAB710E21`）；**清理后未重新执行 Unity 实机测试**。探针只模拟 PNG EOF 写盘后的 9 秒异步交付，不含 FFmpeg、MP4 或新 UI。实机为 Unity 6000.3.10f1 / Mono、Gamma / Direct3D11，测试配置 30 FPS、3072×1920、scale=1。

- **单帧事务实机通过的范围**：无延迟 PNG session `editor_20260923_145159` 与延迟 PNG session `editor_20260923_145241` 均为 Completed，输出各 251 个连续编号 PNG，metadata 中逻辑/请求/已提交 PNG 计数均为 251；completion frame 均为 238，End Tail 均提交 12 帧。两组 Hit frame 均为 `75, 93, 111, 129, 147, 165, 183, 201, 219, 237`。延迟组 debug 日志的 Commit 0–250 每帧恰好一次，没有捕获或进展 watchdog 超时。临时实现区分 AwaitingEOF / AwaitingDelivery；等待时不再 Prepare、autoplay 或记录未提交帧的 canonical completion，沿用唯一 `MasterTimeline` / `CommitFrame`。这些是**此配置下的逻辑与日志结论**，不能推出视觉确定性。
- **主线程投递与 Unity 更新**：四个目标帧为 pre-entry 0、B−1=56、G=57、gameplay G+10=67。`SynchronizationContext.Current` 实际为 `UnityEngine.UnitySynchronizationContext`；四次 Post 均在主线程 ID 1 执行，随后的一次性 EOF continuation 在同一 Unity frame、已观察到 Conductor Prefix/Postfix 后提交。等待期间 Unity frame 分别增加 `2758 / 3681 / 3655 / 2296`，合计 12,390；metadata 的 `tickCount` 从对照组 263 增至延迟组 12,653。强制 songposition、`outputFrameIndex`、所记录的 controller state、chosenPlanetAngle、Hit 与 completion 标志在四次等待首尾保持不变；B−1 等待时 `timeScale=1`、G 等待时 `timeScale=0.64`，恢复后仍按既有 B−1/G 提交规则变化。**Unity 原生 Update 并未停止**，这些快照不足以证明所有原生视觉状态不漂移。
- **实际 PNG 像素未达到一致**：对同逻辑帧 251 对 PNG 逐像素比较 RGB，完全一致为 0 对；每帧不同像素占比最小 `0.949%`、最大 `3.874%`、平均 `1.601%`。第 0 帧在首次等待发生前就已有 `0.949%` 差异；两次 session 启动时 `cachedAngle` 分别为 `0` 与 `7.853981`，初始原生状态未严格一致。第 1 帧差异扩大至 `2.856%`，蓝色 Planet/拖尾位置可见不同；G 及相邻帧的蓝色拖尾形态也不同。探针统计的 8 个 `TrailRenderer` 在采样点 `positionCount` 全为 0，不能代表 PNG 中可见的全部拖尾或特效。当前证据证明画面不一致，**不能单凭这两组断言差异由等待导致**；需同配置、匹配初始状态的无延迟对照或等价针对性验证。只有确认等待因果后，才调查当前游戏 DLL 的具体原生更新路径、方法和签名，并提出最小修复；不得先扩大 Harmony Patch。
- **取消与隔离范围**：`editor_20260923_145419` 在 generation 3 的 frame 0 已写 PNG、尚未 Commit 时取消，metadata 为 Cancelled、`logicalFrameCount=0`、`writtenPngFrameCount=0`，目录中实有 `frame_000000.png`。这是**探针引入的跨帧交付状态**下产生的未提交物理产物；正式 `0.3.7.1` PNG 路径在同一同步帧末事务内完成写盘与提交，不存在该跨帧窗口。正式视频管线必须用临时输出和取消清理语义处理。随后 generation 4 的 Log-only session `editor_20260923_145426` 正常完成 251 个逻辑帧、completion 238、无 PNG；旧 generation 3 的 Post 到达时被忽略（日志 `currentGeneration=0`）。未见 cleanup-failed / residual gate 拒绝，恢复日志包含 captureFramerate / targetFrameRate / vSync / RDC.auto，下一 session 的 Camera source 可正常激活。旧通知到达时新 session **已完成**，因此“新 session 活跃期间旧通知迟到”仍只有静态 generation/pending 检查，尚无实机碰撞证据；Camera 与 timeScale 的逐项精确恢复也没有独立实机快照。
- **阶段判断**：Event-Driven A+ 的事件投递、单帧逻辑背压及取消后的闲置态旧通知隔离在本环境具备可行性证据；**尚不具备批准正式 FFmpeg IO 接入的视觉确定性证据**。下一轮最小补测是同参数且控制启动状态的无延迟 PNG 对照，重点比对 frame 0/1、56–59、67–69 及可见 Trail/特效；若差异确与等待相关，再定位原生 Update 路径。正式 IO 仍须另外验证 CPU 缓冲 ownership、完整写入、FFmpeg 进程终态、封装发布与活跃新 session 的迟到回调。此次临时探针已清理，不把其等待门控当作稳定产品功能，也不更改 `0.3.7.1` 历史基线。Event-Driven A+ 仍保留为**已确定路线**（§2.1），但**正式 MP4 帧事务接入尚未获准**；下一步**不再默认要求用户重复本轮实机探针**，仅在有明确因果问题需要判定时才安排针对性实机对照。

### 2.3 FFmpeg 构建、安装与独立进程取证（2026-09-23；未接入产品）

**证据层级**：在当前 Windows x64（中完整性级别）、ADOFAI `Assembly-CSharp.dll` FileVersion `0.4.3.0` / Unity `6000.3.10f1` / Renderist `net48` 环境下，使用仓库外临时目录、Windows .NET Framework 4.8 编译的独立 C# `Process` harness 及 FFmpeg 命令行测试。只读核对当前游戏自带 Mono `System.dll` / `mscorlib.dll` / `System.IO.Compression*.dll`，确认 `Process.Start/Kill/WaitForExit/BeginErrorReadLine/BeginOutputReadLine/Exited`、`Stream.WriteAsync`、`SynchronizationContext.Post` 与 `ZipFile.OpenRead` 的 API **存在**，并非运行语义验证。以下是**资产校验、目标程序集静态 API 与独立进程结果**，不是 Unity Mono 中的正式 IO 或 MP4 实机验收。本轮未改产品代码、游戏、系统 PATH 或发布 ZIP。

| 实测 Windows x64 资产 | 归档 SHA-256 | 关键区别 |
| --- | --- | --- |
| Gyan `ffmpeg-9.0.2-essentials_build.zip`，2026-09-19 构建，2026-09-20 GitHub 发布 | `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba` | **拟定一键安装首选**；ZIP、静态、GPLv3、含 `libx264` / `ffprobe`，无同包 FFmpeg DLL 启动依赖 |
| Gyan `ffmpeg-8.1.2-essentials_build.zip`，2026-06-27 发布 | `db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec` | 前一发行版交叉验证；相同基本管线可用 |
| BtbN `ffmpeg-n9.0.2-3-ga5923073bf-win64-gpl-9.0.zip`，2026-09-22 日构建 | `649f40e14a3fadb377de32d88fa1106d33cc0b85d53ba8aa0c1b8d87e1bfbc35` | 静态、GPLv3、含 `libx264`；日构建保留期限短，不适合作为长期自动安装回退 |
| 同一 BtbN 发布的 `...win64-gpl-shared-9.0.zip` | `a95126789722aa67fdd18de03dbf03f099ce822c61b534bbb54d97c63944098d` | `ffmpeg.exe` 需同目录 8 个 `av*` / `sw*` DLL；缺 DLL 时 `Process.Start` 可成功但退出 `0xC0000135`、stderr 为空 |
| 同一 BtbN 发布的 `...win64-lgpl-shared-9.0.zip` | `5dbe1d1aca0270df4ebf5d13258aa6b181040139d356bf5d124014633ab0a412` | LGPLv3，**无 `libx264`**；当前机器 `h264_mf` 可编码，不据此承诺别的 Windows 环境可用 |

Gyan [构建页](https://www.gyan.dev/ffmpeg/builds/)同时列出原站包、SHA 与 [GitHub 镜像](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2)；两处公布的 9.0.2 SHA 与实际下载一致。拟定固定下载地址为 `https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip`，**不得改成可变 `latest`**；原站同版本包可作同哈希备用来源。Gyan Essentials 已有 rawvideo / pipe、`libx264`、MP4 muxer；Full 多出本阶段不需的库且发行包主要为 7z。BtbN [构建说明](https://github.com/BtbN/FFmpeg-Builds)明确日构建保留策略、GPL / LGPL 与 static / shared 差异。以上 EXE 实测均为 PE x64；三份静态构建的导入表只有 Windows / UCRT DLL，当前系统可以直接运行，不能推广为其他 Windows 版本或 ARM64 的证明。Gyan 两个 Essentials 构建与 BtbN GPL static 已按实际归档哈希核对并编码；此处资产哈希不是产品已内置的安装清单。

- **首选资产身份与信任边界**：Gyan 构建页把 9.0.2 对应到 FFmpeg [源码提交 `946fcce07b`](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b)。受测 ZIP 中 `ffmpeg.exe` SHA-256 为 `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec`，`ffprobe.exe` 为 `f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52`；两者当前均无 Authenticode 签名。固定 ZIP 哈希可发现下载截断或资产变化，但校验值与包同由构建方发布，不能等同独立代码签名/第三方审计；安装界面须准确说明来源，运行前验证固定身份和实际编码能力。
- **Unity 像素 ownership 的静态边界**：当前游戏 `UnityEngine.CoreModule.dll` 确有 `Texture2D.GetRawTextureData` 两种重载；[Unity 6 文档](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Texture2D.GetRawTextureData.html)规定无参 `byte[]` 返回 CPU 数据副本、单数组最大约 2 GB，而泛型 `NativeArray<T>` 是纹理内存视图，纹理更新后可能失效。因此不能把视图交给跨帧后台 writer；必须在主线程形成独占且长度核实的完整帧数据，超过单数组表达能力时另行采用有明确 ownership 的分段表示或按真实资源/接口限制报错，不人为缩减几何参数。[`ReadPixels`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Texture2D.ReadPixels.html) 以左下为坐标原点；与 FFmpeg 行序的实际对应仍须用 Unity 图案/PNG 对照验证，不能只依文档推断成片方向。
- **帧、格式与时间**：三份静态构建均从 RGB24 stdin 生成 30 FPS、64×48 和 3072×1920 的 H.264 MP4（3/3 帧）；1×1、3×3、65×49 用 `yuv444p` 可编码，65×49 用 `yuv420p` 时 `libx264` 因宽高不能被 2 整除而失败。4:4:4 的目标播放器兼容性待测，不得静默改输出宽高或把该 codec 条件当作 PNG 参数上限。合成 RGB24 图案经 `libx264rgb -crf 0` 解码后行/列与输入一致；Unity 当前 `Texture2D` 原始内存行序**仍待实机确认**。3 帧写入间隔各 1 秒，墙钟约 3.3 秒而成片仍为 30 FPS / 0.1 秒，证实外部进程可按指定帧率离线工作。
- **帧率精度是必须预检的实际边界**：三份静态构建直接对 rawvideo 使用 `-framerate 1001001` 至 `2147483647` 时均**成功但静默降为 `1001000/1`**；FFmpeg 9.0.2 的 [`av_parse_video_rate`](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavutil/parseutils.c) 使这一命令路径受限。进一步实测三份构建均可用 rawvideo `-framerate 1001000` 加 `settb=1/<目标FPS>,setpts=N`、`-fps_mode passthrough -enc_time_base filter -bsf:v setts=duration=1 -video_track_timescale <目标FPS>` 生成精确 `1001001` 和 `2147483647` 的逐包时间基/时长；Gyan 9.0.2 的 stdin 测试在 100 帧和 5000 帧时保持逐包 PTS 连续；三份构建使用 `libx264 -preset medium -bf 0` 时，100 帧的 `1001001` / `2147483647` 样本均可完整解码，Gyan 9.0.2 的 `2147483647` / 5000 帧样本亦完整解码。相同重定时命令若保留 `medium` 默认 B 帧，Gyan 9.0.2 虽退出 0 且容器报 100 包，却在两种帧率分别只解出 98 / 0 帧；所以必须用实际解码帧数及时间戳核验选定编码参数，不能只核对容器 FPS / `nb_frames`。因此 `1001000` **不是 MP4 产品 FPS 上限**。很短的极高 FPS 视频在 `ffprobe` 秒数显示中可能四舍五入为 `0` / `N/A`，应核对 rational time_base、duration_ts 与逐包 PTS；正式命令选择及验证仍须按所选二进制和参数做精确性预检，不默默改变 Renderist 逻辑时间线。
- **交付与封装不可混淆**：错误编码器、不可写输出目录、文件锁、缺少共享 DLL 时可出现**所有 stdin WriteAsync 均完成而最终退出非零 / 无视频**；输入首帧仅半帧时退出 `69` 并留下非空无视频文件。更危险的是**3 帧完整 + 第 4 帧半帧**：三份静态构建默认均退出 `0`，但 MP4 只有 3 帧；`-xerror` 可将该错误变成非零，空输入需 `-abort_on empty_output_stream` 才会非零。故每帧先校验 `checked((long)width × height × 3)` 的完整数据并持有独立 CPU 所有权；终态同时校验进程退出码、实际视频流/尺寸/FPS/帧数，不能只看写入、退出码、文件非空或 `ffprobe` 的退出码。实际截断样本中 `ffprobe` 对 100 字节 MP4 **退出 0 但没有视频流**，对另一截断样本 `nb_frames=3` 而读到 2 帧并报错。仅用已选 FFmpeg 本身执行 `-xerror -i 临时MP4 -map 0:v:0 -an -f null -progress pipe:1 NUL` 的独立完成验证，实测可区分正常、无流、截断与帧数不符；这是可选的无 `ffprobe` 依赖实现方案，完整解码可能耗时，须异步、可取消且不受 30 秒帧 watchdog 误杀。
- **命令与进程约束**：临时输出名若为 `.mp4.part`，未指定 `-f mp4` 会直接失败；显式 muxer 后三份静态构建均成功封装并经核验后同卷发布。建议首批命令包含 `-nostdin -xerror -abort_on empty_output_stream -fps_mode passthrough -f mp4`，具体编码参数与输出像素格式随 session 冻结。独立 .NET Framework harness 的 stderr/debug 数百行异步排空未死锁；20 次连续启动/退出无残留 FFmpeg；`-readrate 0.01` 故障注入时后台 writer 在第 25 次完成后阻塞、主线程保持可运行，Kill 后 WriteAsync 以断管异常收敛。另一独立测试中取消 `WriteAsync` 的 token 后 writer 收到 `TaskCanceledException`，FFmpeg **仍以 0 退出并留下只含已完整接收帧的有效 MP4**：取消必须先锁定 session 终态并废弃 generation，不可把后续 0 退出视为 Completed；取消中的部分写入量未知，不在原管道重试该帧。缺 EXE / 非法 EXE / 暂时拒绝执行的 ACL 分别导致启动异常，缺共享 DLL 则可能是启动成功后的异常退出。输出路径和 EXE 路径含空格、中文、`&`、括号及 emoji 可运行，且不依赖当前工作目录。以上不是 Unity Mono 异步 Stream / `Exited` 事件的实机证明。当前 30 秒 capture/progress watchdog 若直接跨越合法 IO 等待会误判；正式流程须按 AwaitingEOF / AwaitingDelivery / Finalizing 阶段区分，不以等待墙钟推进逻辑时间。
- **退出事件不等于日志排空**：独立 net48 进程 harness 对 FFmpeg 自校验进程观察到 `Exited` 先于最后的 `progress=end`、stdout EOF 与 stderr EOF；20 次正常样本均如此。损坏 MP4 也可能发出 `progress=end`，所以必须把进程退出码、stdout/stderr 完整排空、最终帧数与无错误作为同一异步终态屏障，不能在 `Exited` 回调中立即发布文件；等待不得阻塞 Unity 主线程。该顺序仍待 Unity Mono 实测。
- **安装与回滚独立实验**：临时 net48 安装 harness 用固定 SHA-256、ZIP 全条目安全扫描、白名单提取、同卷版本化 staging → 目录 `Move`，失败不覆盖旧目标。哈希不符、损坏 ZIP、`../` 路径穿越、大小写重复、提取后取消均失败并清理自身 staging；同一安装锁的并发第二进程被拒绝，第一进程成功；无写权限安装目录直接拒绝。故意在原站下载 3 秒后中断，只取得 155363/114768076 字节、退出 28，固定哈希验证拒绝其作为安装包。强制终止第一进程会留下孤儿 staging，但安装锁句柄释放，后续安装可成功；正式实现需持锁后安全清理**自身**孤儿 staging，不能递归误删外部文件。下载站失效或资产改名都应保持旧安装可用并显示失败，不静默退到未验证资产。系统 PATH 顺序实测会改变被发现版本，必须记录/展示并冻结**绝对路径与二进制身份**；用户显式路径失效不得自动改用 PATH。所有试验均在仓库外，不代表安装功能已实现。
- **许可证与剩余门槛**：Gyan README 与 `ffmpeg -L` 表明首选二进制 GPLv3；FFmpeg [法律说明](https://ffmpeg.org/legal.html)及[外部库文档](https://ffmpeg.org/general.html)确认 `libx264` 使 FFmpeg 构建进入 GPL，不能把不放入发布 ZIP 解释为自动豁免。正式一键下载前应清楚显示来源、版本、许可证和安装位置，保留许可证/构建信息与源码链接；若改变分发方式须另行审查。**组件管理及 Windows 独立进程层已有进入实施的证据**；Unity Mono 回投、RGB24 实际拷贝/翻行、活跃新 session 迟到回调、长时间 native Update 的视觉连续性及正式 End Tail/封装关口仍待产品实现与定向实机验收（§2.2），不可据此宣布完整 MP4 管线可用。

### 2.4 FFmpeg 组件管理闭环 L1（已实现；未接入导出路径）

**实现基线**：L1 实现提交 `62d10de`（其后文档提交 `caecfa9`）；产品版本仍为 **`0.3.7.1`**，Phase 未同步（本闭环形成稳定产物前不机械递增版本）。源码位于 `src/ADOFAI.Renderist/Ffmpeg/`，**刻意不依赖 Unity / UMM / Harmony** —— 因此同一份源码可直接编入 net48 回归工程（`tests/ADOFAI.Renderist.FfmpegTests`），**不需要任何 Unity stub**；GUI 接线由既有 UMM 入口负责。后续 L1 审查修正与最新验证结果见 §2.5。

- **已实现（L1）**：① 固定资产 manifest（唯一可安装资产 = Gyan 9.0.2 essentials；固定 HTTPS tag 地址、归档 SHA-256、归档字节数、两个可执行文件固定 SHA-256；**禁止可变 `latest`**；manifest 自身结构校验，非法即 fail-closed）。② 候选发现：**用户显式路径 → Renderist 托管安装 → 系统 PATH → 无**，并冻结**绝对路径 + 内容哈希 + 字节数**；显式路径失效**报错且绝不静默回退**（PATH 顺序会改变被发现的版本）。③ 版本与必要能力检查（`-version` / `-encoders` / `-formats`，检查 `libx264` / `mp4` muxer / `rawvideo` demuxer，并解析 configuration 行的 `--enable-gpl` / `--enable-libx264`）；短进程探测同时读 stdout / stderr，带超时与取消，不是帧流会话。④ 安全 ZIP 解压（**默认拒绝**：绝对路径 / 盘符 / `..` 与 `.` 段 / 大小写不敏感重复条目 / 符号链接与重解析点 / 非法字符 / 单条目与归档总量上限；只写白名单匹配项；写出前再次确认目标仍在目标目录内；逐文件校验 SHA-256）。⑤ 安装锁（`FileShare.None` 独占句柄：第二个并发安装立即被拒绝；进程被强杀时由 OS 释放句柄，不会留下死锁）。⑥ 托管安装与 ownership 标记（无标记的目录**既不采用也不覆盖**）。⑦ 取消 / 回滚 / 孤儿 staging 清理（只删除**本次调用自己创建**的 staging，以及位于自身 StagingRoot 下且带本模块 ownership 标记的目录；不含标记的外来目录一律保留）。⑧ 组件就绪状态与机读错误码（`NotFound` / `Discovered` / `Ready` / `Unsupported` / `Invalid` / `InstallRootUnavailable`）。
- **HTTPS 下载（2026-09-24 实施；Unity 实机未验证）**：已按既定路线用 `UnityWebRequest` + `DownloadHandlerFile` 实现固定资产下载，见 §2.6。`FfmpegInstaller` 仍然只接受"已在本地"的归档路径（它**只读取、绝不删除**该文件；临时文件由下载控制器独占拥有）。**L2（FFmpeg 帧流进程）在该时点未实现（现已实现，见 §2.8）；L3（MP4 帧事务）仍未实现**；`MasterTimeline` / `DeterministicFrameScheduler` / `FrameCaptureDriver` / `EditorExportController` / `EditorExportSession` **未被修改**，未新建第二套 EOF 或第二个 `CommitFrame` authority。
- **API 静态存在性 ≠ Mono 运行行为（本轮只读核对游戏自带程序集）**：`..._Data\Managed` 中存在 `System.dll`（`WebClient` / `HttpWebRequest` / `ServicePointManager` / `SecurityProtocolType` / `Tls12`）、`System.Net.Http.dll`（`HttpClient` / `HttpClientHandler`）、`System.Core.dll`（`SHA256` / `SHA256Managed`）、`System.IO.Compression.dll`（`ZipArchive` / `ZipArchiveEntry` / `DeflateStream`）、`System.IO.Compression.FileSystem.dll`（`ZipFile` / `ZipFileExtensions`）、`UnityEngine.UnityWebRequestModule.dll`；`System.Net.dll` **不存在**（不影响：`WebClient` / `HttpWebRequest` 位于 `System.dll`）。**这些只证明类型存在，不证明 Mono 下的 TLS、取消与异步语义可用。** 构建侧事实：net48 SDK 的**隐式**引用集合为 `System` / `System.Data` / `System.Drawing` / `System.Xml` / `System.Core` / `System.Runtime.Serialization` / `System.Xml.Linq` / `System.Numerics` / **`System.IO.Compression.FileSystem`**，而 `System.Net.Http` **不在其中**；因此安全解压无需新增引用，而 `HttpClient` 路线需要显式 `<Reference>`。所需参考程序集均已离线缓存，加引用**不需要**新 NuGet 下载。
- **该提交的回归验证**：`tests/run-ffmpeg-tests.ps1` → **42 passed / 0 failed / 1 skipped**（exit 0）；§2.5 已增加回归，此数字只代表 `62d10de` 当时的测试。覆盖 manifest 校验、候选发现（含"显式路径失效绝不静默回退"、PATH 顺序优先、无效托管目录回退）、SHA-256、ZIP 安全（穿越 / 盘符 / 大小写重复 / 歧义匹配 / 缺必需条目 / 哈希不符 / 损坏归档 / 符号链接 / 尺寸上限 / 取消）、安装锁竞争、安装 happy path、既有安装不被覆盖、外来目录被保留、归档被篡改、解压失败清理、取消无残留、孤儿 staging 清理与外来 staging 保留、组件状态报告。唯一 SKIP 是**真实 FFmpeg 多版本能力回归**（本机无 FFmpeg fixture；二进制刻意不入库，需 `-FfmpegDirs` 指向本地目录）。测试使用**合成**归档与合成资产，不携带任何下载产物。
- **该回归测试本轮发现并已修复的两个实现缺陷**：① `FfmpegInstallLayout.TryCreate` 原先经 `Path.GetFullPath` 把**相对路径**按进程 CWD 静默解析成合法绝对路径（违反"不猜测托管目录"）；现改为解析前即要求绝对路径。② 解压层把取消包装成失败结果，安装器原先会把**用户取消记成 `Failed`**；现按语义如实返回 `Cancelled`。
- **构建与发布**：Release Rebuild **0 error / 0 warning**；`package-release.ps1`（内置 verify）与独立 `verify-release-package.ps1` 均 **PASS 11 checks / 0 failures**；发布 ZIP 仍严格只有 `Info.json` / `ADOFAI.Renderist.dll` / `LICENSE`（已独立解包核对，0 个 FFmpeg / 测试资产）。`git diff --check` clean。
- **边界与待办**：FFmpeg 组件状态是**信息性**的，**不参与 preflight，绝不阻断 PNG / Log-only**。仍需 GPT Work 在目标 Unity Mono 环境收敛下载客户端方案；许可证展示与 **GPLv3 分发/告知义务**须在正式发布前审查。**本闭环未做任何 Unity 实机测试**（不下载、不安装、不接入导出），因此不得记为实机通过。

### 2.5 L1 定向审查与下载路线（2026-09-24）

**阶段状态**：`0.3.8.0 — FFmpeg Video Export Pipeline` 为已批准目标阶段；当前产品版本仍为 `0.3.7.1`。L1 的发现、固定 manifest、能力探测、本地归档安全安装和信息性 GUI 已实现；审查基线为 `caecfa9`，本地 `main` 当时领先实时核对的 `origin/main=731b186` 四个提交。本节保留 GPT Work 的审查结论；其中"HTTPS 下载未实现"与"probe 非零退出未修复"两条已在 2026-09-24 由 dsh 处理，现状见 §2.6。**截至该时点 L2/L3 均未实施（L2 已于 §2.8 实现，L3 仍未实施）。**

- **已复现并在 L1 修正**：原 `FfmpegFileHash` 以路径、长度和修改时间缓存内容哈希。回归测试将同长度文件替换后恢复原时间戳，原代码返回旧哈希，故该缓存不能用于归档校验、托管安装校验或二进制身份。现改为每次读取当前内容计算 SHA-256；测试先失败后通过。`SafeZipExtractor` 原先只限制各条目的真实写出量，跨条目总量只累计 ZIP 声明长度；伪造中央目录长度的双文件 ZIP 可突破真实总量上限，现复制时也限制累计真实字节数，回归先失败后通过。GUI 检查异步进行，重复哈希的性能代价待目标游戏观察，不以不安全缓存换取速度。
- **已复现并在 L1 修正**：GUI 本地安装请求 `ProbeAfterInstall=true`，但原安装器即使能力探测失败仍原子发布并报告 `Installed`。合成不可执行文件的回归测试复现此行为；现探测失败或缺失必需能力时保留失败结果、清理自身 staging、不发布；发布前再次检查取消。此结论不等于真实 FFmpeg 已在当前机器重新完成能力检查。
- **已验证层级（2026-09-24 当时的结论；当前计数与真实 fixture 结果以 §2.6 / §2.7 为准，本节不重复）**：当时独立 net48 回归为 **67 passed / 0 failed / 1 skipped**；跳过项是**真实 FFmpeg 多版本能力回归** —— 当时本机没有 fixture，且独立 shell 出站 TLS 被阻断（DNS 与 TCP 443 可连，TLS 握手失败），无法按 manifest 取得固定资产。**该 SKIP 此后已由目标游戏内实际安装的固定资产补齐（§2.6 / §2.7），故本节不再声称"没有取得任何真实 FFmpeg 能力检查结果"。**Release 主工程构建 0 error / 0 warning；独立发布包校验 **11 checks / 0 failures**，包仍只有三文件。以上不是 Unity Mono HTTPS、GUI 生命周期或真实二进制的实机验收。目标游戏文件当前显示 UnityPlayer `6000.3.10`、`Assembly-CSharp.dll` FileVersion `0.4.3.0`；实际 `UnityEngine.UnityWebRequestModule.dll` 通过只读反射确认 `UnityWebRequest.SendWebRequest/Abort/Dispose`、`result/responseCode/downloadedBytes/redirectLimit/timeout`、`DownloadHandlerFile.removeFileOnAbort` 成员存在，`System.Net.Http.dll` / `System.dll` 也存在。这仍只证明本地程序集条件，不能证明 TLS、回调相位与取消运行语义。
- **已确定的下载实施路线，运行行为部分实机验证（见 §2.6）**：UnityWebRequest + DownloadHandlerFile 向独占临时文件异步写入；Unity 侧主线程创建请求、SendWebRequest，当前生产实现由 OnUpdate 读取进度及 operation.isDone，取消时 Abort，完成后 Dispose。后台 Task 只处理已关闭文件的哈希与现有安装器，不访问 Unity 对象。certificateHandler=null，维持平台默认证书校验；不更改全局 TLS、系统 PATH 或管理员权限。目标游戏已验证固定资产传输、HTTP 200 / HTTPS 重定向、请求完成、哈希校验、安装和能力探测，以及先前手动取消清理；证书/断线故障及活动时禁用/退出收敛仍待验证。临时探针观察到 completed 事件在主线程触发，但正式代码仍使用 isDone 泵；HttpClient / HttpWebRequest / WebClient 不是实施路线。
- **已由 dsh 实施（2026-09-24）；主路径已在目标游戏实证，边界项仍未验收（现状见 §2.6 / §2.7）**：上述要求已落实为 `FfmpegDownloadController`（Unity-free 状态机 / generation 隔离 / 临时文件唯一 owner / 后台校验与安装）与 `UnityFfmpegDownloadDriver`（唯一持有 `UnityWebRequest`，只在主线程 `SendWebRequest` / 读取进度 / `Dispose` / `Abort`）。用户主动点击后才下载 manifest 钉住的 Gyan 9.0.2 ZIP；沿用固定长度 `114768076` 与 SHA-256 `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`，响应成功仍须校验**磁盘实际长度**与**重新计算**的内容哈希（`FfmpegFileHash` 已取消元数据缓存）；非 HTTPS 最终地址与 HTTP 非 200 一律 fail-closed，不使用 `latest`。取消/禁用/卸载会**先失效 generation**，再请求 Abort 并同步删除自己那一代的临时文件；迟到通知只能清理自己那一代，绝不启动安装、发布或把已取消会话标记为成功。`OnUpdate` 的 FFmpeg 生命周期泵现在**先于 `Enabled` 判断**执行，且 `OnToggle(false)` 与 `OnUnload` 各自独立收敛，不依赖 GUI 再次打开；OnUnload 仅在签名已核实的范围内接线。仍**未**在实机验证的部分见 §2.6。固定 SHA 与能力报告只是发现时快照；未来 L2 在 `Process.Start` 前必须重新确认所选绝对路径的当前二进制身份并处理校验与启动间的替换竞态，不能把 L1 报告当成永久运行授权。
- **其他审查发现**：① `FfmpegCapabilityProbe` 原先只判 `-version` 的非零退出，`-encoders` / `-formats` 的非零退出未作为失败，异常输出中若仍含目标 token 可能误报 Ready —— **已在 2026-09-24 修复**（非零退出即 `ProbeFailed`；能力必须来自结构化表行；回归先失败后通过），见 §2.6。② GUI 原先用 `OnUpdate` 收取后台 Task 且在禁用后直接跳过 —— **已修复**：生命周期泵前移到 `Enabled` 判断之前，`OnToggle(false)` / `OnUnload` 各自独立收敛，安装 Task 与下载均有取消。③ 当前引用的 UMM `UnityModManager.dll` 通过反射确认 `ModEntry.OnUnload` 字段类型为 `Func<ModEntry,bool>`，已按此接线；这仍只是签名核实，**不是**实际游戏退出回调验证。
- **许可证边界**：当前固定构建含 `libx264`，manifest 标为 GPLv3。正式主动下载界面和安装记录应列出第三方构建方、固定版本、归档来源、安装位置、许可证文本/链接、对应源码和构建说明入口；安装物只提取两个 EXE，尚未保留随包许可证文件。FFmpeg 不进入三文件发布 ZIP，并不自动消除因应用提供下载而产生的告知或分发审查事项；实际义务须按发布方式确认，不作法律定论。

### 2.6 FFmpeg L1 HTTPS 下载实施与验证状态（2026-09-24）

**范围**：在 GPT Work 三处 L1 修正（§2.5，原样保留并回归验证）之上，补齐 `UnityWebRequest` + `DownloadHandlerFile` 下载，但目标游戏的请求完成与安装闭环仍待验收。**未**接入 L2/L3，**未**修改 `MasterTimeline` / `DeterministicFrameScheduler` / `FrameCaptureDriver` / `EditorExportController` / `EditorExportSession`，产品版本仍为 `0.3.7.1`。

**实现**（源码见 `src/ADOFAI.Renderist/Ffmpeg/FfmpegDownloadController.cs` 与 `src/ADOFAI.Renderist/UnityFfmpegDownloadDriver.cs`）：

- **单一下载 owner**：`FfmpegDownloadController` 是 Unity-free 的状态机（`Idle` / `Downloading` / `Verifying` / `Installing` / `Succeeded` / `Cancelled` / `Failed`）；活动期间重复请求返回 `busy`，不与本地 ZIP 安装并行。
- **临时文件 ownership 唯一且显式**：路径由布局给出（`<InstallRoot>/.downloads/renderist-download-<assetId>-<generation>-<token>.zip`），**创建与删除都只由控制器负责**，覆盖下载 → 校验 → 安装全程；`FfmpegInstaller` 只读取。Unity 侧 `removeFileOnAbort = true` 仅作 teardown 安全网，不承担清理责任。启动时另清理只匹配本模块前缀的孤儿下载文件。
- **Unity 侧只在主线程**：`UnityFfmpegDownloadDriver` 唯一持有 `UnityWebRequest`，只在主线程创建、`SendWebRequest()`、读 `downloadedBytes` / `Content-Length` 进度、`Dispose()`、`Abort()`；结果整理成纯数据 `FfmpegDownloadResponse` 交给控制器。后台 `Task` 只做本地长度/SHA-256 复核与 `FfmpegInstaller`，不接触 Unity 对象。
- **完整性以内容为准**：校验**磁盘实际长度** == 固定 `114768076`，再**重新读取内容**计算 SHA-256 == `60f467…`；随后安装器再校验归档与两个可执行文件哈希、安全解压、能力探测，只有全部通过才原子发布。
- **fail-closed 的响应校验**：传输失败、HTTP 非 200、重定向到非 HTTPS 最终地址、长度不符、哈希不符、能力探测失败（含 probe 非零退出）都拒绝发布，且都清理临时文件。
- **generation 隔离**：每次启动递增 generation，并保留 generation → 临时文件映射。取消 / 禁用 / 卸载**先失效 generation**，再请求 Abort 并同步删除该代临时文件；迟到通知走"只清理自己那一代"的分支，绝不启动安装、发布旧版本、覆盖新任务状态或把已取消会话标记为成功。
- **生命周期不依赖 GUI**：`OnUpdate` 的 FFmpeg 生命周期泵前移到 `Enabled` 判断**之前**；`OnToggle(false)` 与 `OnUnload`（UMM 签名已核实）各自独立调用 `ShutdownFfmpegComponent`，在调用内同步收敛，不需要用户再次打开 GUI。
- **GUI 与告知**：复用既有 FFmpeg 组件区域，区分显示发现状态、实际路径与二进制身份、固定版本与构建方、来源链接、**GPLv3 许可证与对应源码入口**（manifest 新增 `SourceCodeUrl`）、托管安装位置、下载阶段、已接收字节数（IMGUI 进度条）、取消入口与可读失败原因。**不新增通用 UI 框架。**
- **不使用**：可变 `latest`、`HttpClient` / `HttpWebRequest` / `WebClient` 替代路线、自定义或放行的证书处理器、全局 TLS 配置修改、系统 PATH 修改、管理员权限。请求**不设超时**（慢链路属真实条件，不转化为人为上限），卡住由用户取消收敛。

**离线回归（该实现步骤；计数随后续 driver 回归与回调 ownership 回归上升，最新见下文复测段与 §2.7）**：`tests/run-ffmpeg-tests.ps1` → 当时 **67 passed / 0 failed / 1 skipped**（exit 0）。新增覆盖：下载成功（长度+哈希+安装+发布+能力探测，临时文件被清理）、busy 拒绝、传输失败、HTTP 错误、HTTPS 重定向接受 / 非 HTTPS 重定向拒绝、字节数不符、SHA-256 不符、取消 + 迟到通知被忽略、旧 generation 不影响新任务、下载成功后安装失败不发布、能力不可用不发布、禁用/卸载期间取消且迟到通知不能标记成功、既有有效安装不被覆盖、孤儿下载清理且不误删当前任务文件、进度比例。**关键**：`-encoders` / `-formats` 非零退出与"文本命中但非表行"的定向回归**先失败（3 failed）后通过**；这是真实的缺陷复现证据，不是事后补写的断言。测试程序自身充当可控假 FFmpeg（`FakeFfmpeg`），因此**不携带任何下载产物**。

**构建与发布（已执行）**：Release Rebuild **0 error / 0 warning**；`package-release.ps1` 与独立 `verify-release-package.ps1` 均 **PASS 11 checks / 0 failures**；发布 ZIP 仍严格只有 `Info.json` / `ADOFAI.Renderist.dll` / `LICENSE`。已部署到游戏 `Mods\ADOFAI.Renderist\`，部署 DLL 与 `bin\Release` SHA-256 一致（`64C9DCEB08CF…`）。

**目标游戏实机验收（2026-09-24，部署 DLL SHA-256 为 64C9DCEB08CFDF4994C4DEBB76BC00CE1065BD35A86ADA7320AA4FB1619EC019，与本地 Release DLL 一致）**：

1. **PASS — UMM/GUI 与缺失状态**：UMM 0.33.0.0 在游戏中正常打开，Renderist 0.3.7.1 为启用状态。组件显示「未发现 FFmpeg」、托管目录、固定 Gyan 9.0.2 Essentials 来源、GPLv3 与对应源码入口；下载、进度、取消控件可见。取消后的 OnToggle(false) → 重新启用在 GUI 中呈灰→绿，未见残留安装；**活动下载期间禁用与 OnUnload 未测**（“重启后重新发现”已在下文清洁 DLL 复测中通过）。旧的「自动下载尚未启用」提示已在本轮源码中删除，诊断构建的实机 GUI 已确认不再显示。
2. **前次卡住的根因已由源码定位并修正**：前次用户主动下载的 ZIP 达到 manifest 的 **114768076 字节**，独立只读 SHA-256 为 **60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba**，但 GUI 保持「下载中」。旧 `UnityFfmpegDownloadDriver.Pump` 在 `operation.isDone` 后先 `DisposeRequest()`（清空 `_onCompleted`），再读取 `_onCompleted`，所以即使请求结束也无法向控制器交付；异常路径有同样缺陷。旧正式代码根本没有订阅 `completed` 事件，因此前次运行的 isDone 与事件状态**无法追溯**，不能据此归因 Unity 网络。现先保存回调到局部变量，释放文件句柄后只交付一次；状态读取异常转成失败响应。`Invalidate` 另修正为中止旧 generation，进度回调也校验 generation。离线新增 driver 回归覆盖成功、失败、异常、取消后新请求。前次启动阶段另一条 UnityTls 错误早于请求，仍不归因于本下载。
3. **PASS — 手动取消的当前 generation 清理**：对上述停滞请求点击「取消下载」，GUI 进入「已取消」，该代 ZIP 消失，私有 .downloads 当时仅余空目录，未发布安装；收尾时已移除空目录及本轮创建的空托管根。**旧回调迟到并与新下载交错、断线/证书失败、活动下载时禁用或卸载、游戏退出收敛仍未实机覆盖**。**在该次观察时点**尚无已安装的真实 FFmpeg，故固定 EXE 身份、实际 ffmpeg/ffprobe 能力、Ready、重启重新发现当时均 **NOT TESTED**（离线假二进制不能代替）；这些项随后由下文复测与 §2.7 补齐。
4. **PASS — 缺失 FFmpeg 时的最小现有导出回归**：同一张未保存且未修改的编辑器测试画面，30 FPS、12 帧尾段，Log-only session editor_20260924_100209 为 Completed、254 逻辑帧、0 PNG；PNG session editor_20260924_100401 为 Completed、254 逻辑帧、254 个连续编号 PNG（frame_000000 至 frame_000253，首末均 3072×1920、无空文件）。两者 completionFrameIndex=241、tailFramesCommitted=12、stopReason=canonical-completion-tail-drained。这只是最小实机回归，不覆盖复杂谱面。两组本轮独立输出目录在记录核实后已清理；原设置 Settings.xml SHA-256 前后同为 2ADEEC63CE65C22E2495F67D31BE9B1A7130C4DC223A55EADBC61F44CBFFA293，运行时 PNG 开关已恢复关闭，UMM 已关闭。
5. **观察到的非 L1 日志问题**：原设置 VerboseLogging=true 时，只要 UMM 展示编辑器预检，EditorExportPreflight 会每帧反复写入 Player.log（菜单阶段一次观测已达 355 行）；这是既有详细日志路径，不等同 FFmpeg 下载自身刷日志，应另行按需要抑制重复状态日志。

**本轮实机复测（诊断 DLL SHA-256 `85D2324B1A03…`）**：固定 Gyan 9.0.2 请求 generation 1 在目标 Unity Mono 中得到 `operation.isDone=True`、`request.isDone=True`、`downloadHandler.isDone=True`、`result=Success`、HTTP 200、HTTPS GitHub → `release-assets.githubusercontent.com` 重定向、`Content-Length=downloadedBytes=fileLength=114768076`。临时订阅的 `completed` 事件确实触发，回调线程 ID=1，与主线程诊断 ID=1 相同；修复后的完整结果被同代控制器接受，进入 `Verifying`，经归档长度/内容 SHA、白名单解压、两个 EXE 固定 SHA 和实际能力探测后报告安装成功，GUI `Ready` 显示 `libx264 / mp4 / rawvideo`。托管 `ffmpeg.exe` SHA-256 为 `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec`，`ffprobe.exe` 为 `f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52`，均与 manifest 相同；.downloads 无剩余文件。离线回归（本次复测当时）**71 passed / 0 failed / 1 skipped**，以已安装真实 FFmpeg 为 fixture 在沙箱外运行后 **72 passed / 0 failed / 0 skipped**（§2.7 又增加一条回调 ownership 回归，当前源码计数以 §2.7 为准）；同一真实 fixture 在受限命令沙箱内以 Windows 进程退出码 `0xC0000142` 失败，沙箱外通过，不能把沙箱限制误报为目标游戏能力缺陷。一次性诊断源码已删除；清洁 Release Rebuild 0 warning / 0 error，三文件发布包 11 checks / 0 failures；清洁 DLL SHA-256 `F8B3553F4622934F5FB46D5BE3AAC547A627031DA207656090CFDE85B9ABB919` 已在游戏退出后部署，重启后 GUI 再次显示托管安装 `Ready` 与 `libx264 / mp4 / rawvideo`，UMM 已关闭。该重启验证的是重新发现，不是再次下载。

**当前结论**：完整字节到达后的终态阻塞根因在 L1 回调清空顺序，已修复并由目标游戏完整下载、安装与 Ready，以及清洁 DLL 重启重新发现实证。完整文件本身仍不是请求成功判据：必须等待 Unity 最终成功状态和控制器校验。尚需补证活动下载时禁用/卸载、旧通知与新请求真实交错、证书/断线失败；该轮 L2/L3 均未实施（L2 已于 §2.8 实现，L3 仍未实施），不得把 L1 Ready 视为 MP4 可用。

### 2.7 L1 下载回调 ownership 收敛与当前验证状态（dsh，2026-09-24）

**审查基线**：`bea143d` 加 GPT Work 未提交修正（驱动回调交付顺序、`Invalidate` 中止旧 generation、进度回调校验 generation、删除过时 GUI 文案、新增 driver 回归）。这些修正**原样保留**，复核后确认正确：`UnityFfmpegDownloadDriver` 原先在 `operation.isDone` 后**先** `DisposeRequest()`（清空 `_onCompleted`）**再**读取该字段，导致请求结束也无法向控制器交付；异常路径有同样缺陷。现先把回调与 generation 存入局部变量、释放文件句柄后**只交付一次**，状态读取异常转成失败响应。

- **本轮发现并修复一处明确缺陷（临时文件 ownership）**：`FfmpegDownloadController.ReportFinished` 原先对**当前 generation 的重复完成通知**也走"迟到通知"分支，删除该代临时归档。但此时校验或安装**正在读取**该文件：删除可能失败（映射条目已被移除 → 归档泄漏且不再被任何路径清理），也可能成功（把一次合法安装变成失败）。现改为：只有**非当前** generation 的迟到通知才删除其临时文件；当前 generation 的重复通知一律**不推进校验/安装、不删除在用归档**，终态清理由 `Pump` 统一负责。回归 `download: duplicate completion of the active generation keeps the in-use archive` **先失败后通过**（修复前 71 passed / 1 failed）。
- **当前验证结果（本轮实际执行）**：离线回归 **72 passed / 0 failed / 1 skipped**（连跑三次稳定）；以目标游戏内**已安装的真实** Gyan 9.0.2 essentials 为 fixture 运行适用矩阵 **73 passed / 0 failed / 0 skipped**。真实 fixture 的两份 EXE 哈希均与 manifest 一致（`ffmpeg.exe` `3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC`、`ffprobe.exe` `F0D36ECBBDD3BCFAC3EFA078C96C7271C2E68B3810595552AC3B7F17E9A65C52`），实测 `ffmpeg version 9.0.2-essentials_build-www.gyan.dev`，配置含 `--enable-gpl` / `--enable-libx264`。**该 fixture 来自游戏内实际安装，不是本轮 shell 下载 —— 本会话 shell 的出站 TLS 仍然被阻断。** 这独立佐证了 §2.6 的实机安装结论。Release Rebuild 0 error / 0 warning；`package-release.ps1` 与独立 `verify-release-package.ps1` 均 **11 checks / 0 failures**；发布 ZIP 仍严格只有三文件；`git diff --check` clean。
- **二进制身份（2026-09-24 复核；证据来自直接读取文件，不由 UI 结果推断）**：`Mods\ADOFAI.Renderist\ADOFAI.Renderist.dll` 现为 **`1D70BE45DCFA4BD809A909ED248BF22B744E95EC6AB956EF271BF00B6DC5F5B3`**（267776 字节），`ProductVersion = 0.3.7.1+02c7fb8ca6be2009aceb17148acbbe2dbc520bcb`，部署时间 2026-09-24 14:56:50，与 Release 构建**逐字节一致**（部署后重新读取校验），且**包含** `ea09bd7` 的 ownership 修正。**被取代**：`F8B3553F…`（`+bea143d`，13:34:51）—— 用户此前 5 项实机 PASS 绑定的是它，**不含**该修正；`6EA48355…`（`+be29174`）与本次部署功能内容相同、仅嵌入 HEAD 不同（`be29174` → `02c7fb8` 为 docs-only 提交），亦已取代；`63674902…`（HEAD `bea143d` + 未提交工作区）作废。**重要事实**：两个构建在游戏内都显示 `0.3.7.1` / `Phase 3.7.0 Custom Resolution & Supersampling`，**游戏内版本字符串无法区分它们**；唯一判别依据是**磁盘 DLL 哈希或 `ProductVersion`**（辅以 `Player.log` 在部署时间之后的加载记录）。因此 `0.3.8.0` 版本收敛本身也会顺带给出可区分的游戏内身份（`0.3.8.0` ≠ `0.3.7.1`）。
- **L1 正式实机验收（2026-09-24，用户执行）**：**`0.3.8.0` 正式构建**（DLL `AE2326D3D2B13FFA5B300CD1E3E06241F8A6A5522B5AB3EF1260085721DBC6E9` = `0.3.8.0+4d35801862d0828ea248d9934e8ebe74af11f2bb`，2026-09-24 15:07:02 部署到 `Mods`）**全部通过**：UMM 显示 Renderist `0.3.8.0` — **PASS**；FFmpeg `Ready` — **PASS**；`libx264` / `mp4` / `rawvideo` 识别 — **PASS**；禁用并重新启用 — **PASS**；其他可见异常 — **无**。连同此前 `1D70BE45…`（`0.3.7.1+02c7fb8`，覆盖 `ea09bd7` 的 ownership 修正）的同类 PASS，**L1 主路径（组件发现 → 固定 manifest → 能力探测 → 安全安装 → HTTPS 下载 → GUI `Ready`）已具备阶段性基线证据**。
- **仍未覆盖的边界（不得记为通过）**：① **活动下载生命周期** —— 下载进行中禁用 Mod / `OnUnload` / 游戏退出时的 Abort、临时文件与 `.downloads` 收敛（已验收的"禁用并重新启用"是**空闲状态**，不覆盖活动下载）；② **异常网络** —— 证书失败、中途断线、超时的 fail-closed 与清理；③ **真实回调竞态** —— 旧代完成通知与新请求在真实运行中交错（含同代重复通知）；④ 完整主路径的 114 MB 下载按用户指示未重复执行，其证据沿用 §2.6 的 GPT Work 轮次记录。部署未改动 `Settings.xml`（哈希前后一致 `F81452BB…`），未删除已安装 FFmpeg（两份 EXE 哈希仍与 manifest 一致），未放置任何额外的可被 UMM 加载的备份 DLL。
- **用户实机验收（2026-09-24，针对上述 13:34:51 部署构建 `F8B3553F`）**：① FFmpeg 组件显示 `Ready` 并识别 `libx264` / `mp4` / `rawvideo` — **PASS**；② 禁用并重新启用 Renderist — **PASS**；③ Log-only 导出 — **PASS**；④ PNG 导出 — **PASS**；⑤ 其他可见异常 — **无**。**证据边界**：这五项绑定 `F8B3553F`（`+bea143d`），**不是**当前 HEAD 构建 `6EA48355`；`ea09bd7` 之后未重新部署，故当前构建未获得这五项结论。其中 ② 覆盖的是**空闲状态**下的禁用/启用，**不覆盖**活动下载期间的禁用/卸载。
- **L1 分项状态（不得整体宣布完成）**：**已有实机证据** —— 主下载（固定 HTTPS、GitHub → `release-assets.githubusercontent.com` 重定向、HTTP 200、`Content-Length` = `downloadedBytes` = 实际文件长度 = `114768076`）、内容 SHA-256 校验、安全解压与原子安装、**真实能力探测**（`libx264` / `mp4` / `rawvideo`）、**重启后重新发现为 Ready**、**空闲状态下禁用/重新启用**、**存在 Ready 组件时的 Log-only 与 PNG 导出**。**仍未完整验收** —— 活动下载期间禁用/卸载；异常网络（证书失败、断线、超时）；旧回调与新请求的真实交错。
- **剩余最小实机验收项**：① 活动下载进行中禁用 Mod / `OnUnload` / 退出游戏，确认 Abort、临时文件与 `.downloads` 收敛；② 让旧代完成通知与新请求**真实交错**（含同代重复完成通知），确认真实交错下不发布旧版本、不误删在用归档；③ 异常网络（证书失败、中途断线）的 fail-closed 与清理；④ 若要把当前 HEAD 构建 `6EA48355…` 作为交付基线，需在**下一次自然的实机验证**中确认（不要求用户专门重复验收）。

### 2.8 L2 — FFmpeg Video Process Pipeline（已实现；未接入 L3）

**基线**：在 `7a67546a62bf02e7079ebf77cbecfcd282d8b0e2` 之上实现，产品版本**保持 `0.3.8.0`**（用户要求不递增第四位）；本轮**未修改 Phase 文案**（仍为 `Phase 3.8.0 FFmpeg Video Export Pipeline — L1 Component Management`，是否升为 L2 待网页版 GPT 审查）。新增源码（全部 Unity-free / UMM-free / Harmony-free，因此同一份源码直接编入 net48 回归工程）：

| 文件 | 职责 |
| --- | --- |
| `src/ADOFAI.Renderist/Ffmpeg/FfmpegVideoCommand.cs` | 冻结配置、参数合法性、编码命令、精确 FPS、像素格式选择、Windows 参数引用、临时路径规则 |
| `src/ADOFAI.Renderist/Ffmpeg/FfmpegVideoVerifier.cs` | 独立视频验证进程、视频流与尺寸核验、实际可解码帧数、有理时间基与 PTS、失败原因、异步取消与验证进程回收 |
| `src/ADOFAI.Renderist/Ffmpeg/FfmpegVideoPipeline.cs` | 会话生命周期、进程与管道 ownership、单帧完整写入、异步背压、Finish / Finalizing、Cancel、资源回收、终态仲裁、临时文件 ownership、最终文件发布；`FfmpegVideoIdentity` 负责 L1 身份冻结 |
| `tests/ADOFAI.Renderist.FfmpegTests/FfmpegVideoPipelineTests.cs` + `FakeVideoProcess.cs` | 独立自动化回归；假进程由**测试可执行文件本身**充当编码 / 核验子进程 |

**未修改**：`MasterTimeline`、`DeterministicFrameScheduler`、`FrameCaptureDriver`、`EditorExportController`、`EditorExportSession`、`ModEntry`、`UiText`、`Settings`、`EditorExportPreflight`、`Info.json`、csproj `<Version>`、README。**未新增 Harmony Patch、未新增 UI、未改动 PNG / Log-only 行为**（改动仅：3 个新生产文件 + 测试工程 2 个新文件 + `Program.cs` 接线）。

**编码命令（真实 Gyan 9.0.2 `3256173F…` 实测）**：

```text
-hide_banner -nostdin -loglevel error
-f rawvideo -pix_fmt rgb24 -s <W>x<H> -framerate <min(fps,1001000)> -i pipe:0
-vf settb=1/<fps>,setpts=N -fps_mode passthrough -enc_time_base filter
-bsf:v setts=duration=1 -c:v libx264 -preset medium -crf 18 -bf 0
-pix_fmt <yuv420p|yuv444p> -video_track_timescale <fps>
-xerror -abort_on empty_output_stream -f mp4 -y <临时文件>
```

实测结论（64×48 / stdin，输出用 framemd5 逐帧核对）：

1. **30 / 1001001 / 2147483647 三种 FPS 全部精确**：解码帧数 == 输入帧数，逐帧 `pts == dts == N`、`duration == 1`，`#tb 0: 1/<fps>` 与 `-video_track_timescale` 一致。
2. `-framerate` 的**输入侧**上限是 **1001000**：直接传 1001001 会被静默降级，因此命令显式取 `min(fps, 1001000)`（不依赖静默降级），真正的输出帧率只由 `settb/setpts` + `video_track_timescale` 表达。
3. **奇数宽高只能用 4:4:4**：65×49 + `yuv420p` 时 libx264 直接失败（`width not divisible by 2`），`yuv444p` 正常编码并通过核验。因此像素格式是**编码器条件**而不是产品分辨率上限：宽高都是偶数选 `yuv420p`，否则选 `yuv444p`，**绝不静默改写输出几何**。
4. **最后一帧被截断**时 `-xerror` 使退出码非零（实测 `-1094995529`，stderr 出现 `Packet corrupt / corrupt input packet`）；不加 `-xerror` 会静默退出 0 且只封装完整帧。空输入需 `-abort_on empty_output_stream` 才非零（`-22`）。
5. **B 帧必须禁用**：保留 `-preset medium` 的默认 B 帧时，容器报 20 个包却只能解出 17 帧（100 帧 / 1001001 FPS 时同样丢失）—— 这正是"必须核验**实际可解码帧数**"的直接证据。

**验证协议（两个独立进程，使用同一个冻结 FFmpeg）**：

- **解码 pass**：`-loglevel info -xerror -i <file> -map 0:v:0 -an -fps_mode passthrough -f framemd5 -`。stdout 是 framemd5（`#tb` 有理时间基头 + 逐帧 dts/pts/duration），stderr 给出**输入**流信息（编码器 / 像素格式 / 尺寸）；`-nostats` 抑制进度行使 stderr 有界。
- **容器/包 pass**：同一命令加 `-c copy`（不解码），给出**容器**逐包 dts/pts/duration。
- 断言：退出码 0、无错误行、恰好一个**输入**视频流、编码器 == `h264`、尺寸与像素格式精确、`#tb == 1/<fps>`、**解码帧数 == 容器包数 == 已完整写入的帧数**、逐帧 `pts == dts == N` 且 `duration == 1`。
- **两种时间语义必须分开验证**：容器可以声明 N 个包而解码器产出更少的帧（B 帧实测），只验证其中一侧会漏掉该失败模式。
- **负对照（真实文件）**：PTS 被改写（`setpts=2*N`，实际时间基变成 `2/1001001`）→ `verify-timebase-mismatch`；丢帧（`select='not(mod(n,2))'`）→ `verify-frame-count-mismatch`（expected=20 actual=10）；截断 MP4、100 字节残缺 MP4、纯文本、纯音频 MP4 全部被拒绝。
- 解析实现要点：**必须先剥离括号组**再找 `WxH`，否则 `(avc1 / 0x31637661)` 里的 `0x31637661` 会被误当成尺寸；只统计 `Input #N` 段的流行，否则 framemd5 的 rawvideo **输出**流会被算成第二个视频流。
- **内存有界**：帧累加器只保留聚合值与首个不匹配细节，stderr 只保留尾部与少量结构化行，帧数与日志量都不构成内存上限。

**进程生命周期与终态语义**：

- 会话状态：`NotStarted → Running → Finalizing → Completed`，另有 `Cancelled` / `Failed`。`Finish` / `Cancel` / 失败 / 发布竞争由**同一终态仲裁**处理，先到者赢；**发布一旦提交即 `Completed`，之后的 `Cancel` 不能取消、也不能删除已发布文件**。
- 正常完成条件：全部预期帧完整写入 → stdin 正常关闭 → 编码进程退出 → stdout/stderr 均到 EOF → 退出码 0 → 独立核验通过 → 同目录 `Move` 发布。
- **单帧在途**：第二帧并发提交**立即**返回 `busy`（回归断言 < 2 s 内返回），不建立队列。
- **写入结果提交与在途标记释放在同一个临界区内完成**（GPT Work 复审修复，见 §2.8.1）：`FinishAsync` 只能观察到**完整的**写入终态 —— 要么仍在途（拒绝 Finish，`write-in-flight`），要么已计数 / 已锁定失败。不存在"已完整写出但未计数就进入 Finalizing"，也不存在"写入失败却继续正常 Finalizing"。部分写入失败会污染管道（`pipe-poisoned`），同一 stdin 不重试该帧；`WriteAsync` 成功**不代表** MP4 完成。
- **取消不只依赖 WriteAsync 的 token**：取消会终止进程 → 对端管道关闭 → 被阻塞的写入立即失败。`Dispose` 只发起取消，收敛在可观察、可等待的 `CleanupTask` 上，**不在主线程等待进程退出**。
- 合法 IO 背压**没有固定写入超时**；回归用"读取端停止消费"真实复现长时间背压。
- **stdout/stderr 的 EOF 与读取异常必须区分**（GPT Work 复审修复）：排空泵正常 EOF 返回 `null`，读取失败返回该异常；任一侧失败都会阻止 `Completed`、阻止发布（`drain-failed` / `stdout-drain-failed` / `stderr-drain-failed`），且核验进程不会被启动。
- **临时文件 ownership（GPT Work 复审修复）**：临时文件由本会话用 `FileMode.CreateNew` **原子创建**（不存在"先检查、后由 FFmpeg 创建"的窗口），并以 `FileShare.ReadWrite`（**不含** `FileShare.Delete`）的 ownership 句柄保持打开，直到发布或清理之前才释放。因此：外来文件绝不可能被覆盖（创建即失败 `temp-file-exists`），路径在 FFmpeg 打开窗口内不可能被删除或改名覆盖，取消/失败只删除本会话创建的那个文件。**剩余风险**：句柄释放后到 `Move`/`Delete` 完成之间存在一个极短窗口（见 §2.8.1）。
- **已存在的正式目标文件绝不删除或覆盖**（`final-file-exists`）；发布仍是同目录一次 `Move`。删除失败或临时路径被外来目录占用时**如实报告 residual ownership**（`temp-delete-failed` / `temp-path-not-a-file` / `temp-file-not-removed`），不伪装成 clean。
- **进程资源在正常完成路径同样释放**（GPT Work 复审修复）：退出码 0 且两个流都到真实 EOF 之后即 `Dispose` 进程对象（`ProcessDisposed`），不再留给 GC；失败/取消路径走同一套回收（先终止、**等待真实退出**、再 Dispose）。
- **冻结配置**（GPT Work 复审修复）：构造时深复制全部标量（宽高 / FPS / CRF / preset / 像素格式）与路径、编码器身份、期望编解码器名；之后调用方修改 `FfmpegVideoPipelineOptions` / `FfmpegVideoSettings` 不再影响本会话。
- **L1 契约**：只接受 `State == Ready` 的报告，冻结 `Candidate.Identity`（绝对路径 + SHA-256 + 字节数）与必要能力；启动编码前**重新读取内容**计算 SHA-256，并与冻结身份及 `Capability.ExecutableSha256` 比对，失败即 fail-closed（`component-not-ready` / `capability-identity-mismatch` / `capability-incomplete` / `identity-hash-changed` 等），**不重新发现、不下载、不安装、不改 PATH**。**哈希校验与 `Process.Start` 之间的 TOCTOU 窗口仍然存在**，代码注释如实记录，未声称已解决。

**本轮实际验证结果**（L2 实现轮 + GPT Work 复审修复轮，均为独立 net48 进程）：

- 离线（无 fixture）`tests/run-ffmpeg-tests.ps1` → **128 passed / 0 failed / 12 skipped**（12 个真实 fixture 用例如实 SKIP）。
- 以真实 Gyan 9.0.2 essentials（与 manifest 同哈希）为 fixture → **140 passed / 0 failed / 0 skipped**（修复轮内连跑多次一致）。
- 覆盖：命令与冻结配置、Windows 参数引用（含空格 / 中文 / `&` / 括号 / emoji 的真实子进程往返）、身份冻结与失效检测、核验协议正负对照、单帧在途与 busy、部分写入污染、中途断管、空输入、非零退出、核验失败、启动失败、输出目录缺失、临时文件已存在、正式目标文件保护（启动前与 session 中途出现两种情形）、Finalizing 中取消、发布后取消、终态竞争、Dispose 非阻塞、连续启停无残留、迟到写入结果不增加计数、清理失败 residual 报告、真实编码的取消与进程回收；复审修复轮另加 §2.8.1 的 12 项定向回归。
- 真实产物样本：24 帧 64×48 30 FPS → 2531 字节 MP4，**发布后再次独立核验通过**（解码帧数 = 容器包数 = 24、`tb = 1/30`、末帧 pts = 23）。
- Release Rebuild **0 error**（仅 `NU1900` 离线 NuGet 环境噪声）；`package-release.ps1` 与独立 `verify-release-package.ps1` 均 **PASS 11 checks / 0 failures**；发布包严格三文件；`git diff --check` clean。构建身份见 §12.3。

**边界（不得误读）**：

- **未接入 Unity**：L2 只提供"接受外部完整 RGB24 帧"的独立管线，**`0.3.8.0` 仍然不是 MP4 导出可用的版本**；没有 MP4 导出 UI、没有音频、没有 replay、没有新增 Harmony Patch。
- **本模块的测试是独立 net48 进程结果，不是 Unity Mono 实机验收**：Mono 下的 `Process.Start` / 异步 `Stream.WriteAsync` / `Exited` 事件语义、Unity 主线程回投、以及 L3 帧事务与 Finalizing 关口仍未验证。目标 Mono 验证不得由本轮结果代替。
- 本轮**未在游戏内部署**，也**未改动** `Mods` 内已部署的 `0.3.8.0`（`AE2326D3…`）。
- **工具环境事实（不影响产品）**：本机 agent 沙箱下，位于 `LocalLow\...\ADOFAI.Renderist\ffmpeg\<asset>\<version>\bin\ffmpeg.exe` 的二进制**无法创建任何输出文件**（`Permission denied`，即使放宽到 full access 也一样），而把**逐字节相同**的副本放到工作区目录后立即正常。真实 fixture 回归因此指向工作区内的同哈希副本，**不是**替换产品二进制；`-FfmpegDirs` 指向真实安装目录时在本机会因该沙箱限制而失败，这属于环境限制而非产品缺陷。
- **另一个工具环境事实**：用文件复制回滚被修改的生产源码时，副本会带上**旧时间戳**，增量 MSBuild 可能因此跳过重编译并运行旧程序集（本轮实测踩到一次）。回滚 .cs 之后必须刷新时间戳或 `-t:Rebuild`。

#### 2.8.1 L2 生命周期与资源所有权修复（GPT Work 复审轮，已实现）

**范围**：仅 `FfmpegVideoPipeline.cs`（生产）+ 测试工程（`FfmpegVideoPipelineTests.cs`、`FakeVideoProcess.cs`）。`FfmpegVideoCommand.cs` / `FfmpegVideoVerifier.cs` 未改；编码与验证架构（命令形状、两个核验 pass、核验断言）完全保留；版本与 Phase 不变；`Export/` 未触碰；未新增 Harmony Patch、未新增 UI。

**六项缺陷与修复方式**：

| # | 缺陷（GPT Work 结论） | 修复 |
| --- | --- | --- |
| P1-1 | `_writeInFlight` 在计数/失败锁定之前被释放，Finish 可能观察到不完整的写入结果 | 写入结果提交与在途标记释放合并到**同一个 `_gate` 临界区**；成功先计数、失败先锁定，`FinishAsync` 另外防御性拒绝 `pipe-poisoned` |
| P1-2 | 临时路径"检查后、FFmpeg 创建前"存在碰撞窗口，`-y` + `_tempOwned` 可能覆盖或误删外来文件 | 改为 `FileMode.CreateNew` **原子创建** + 持有 `FileShare.ReadWrite`（不共享 Delete）的 ownership 句柄直到发布/清理前；启动失败同样回收已创建的临时文件 |
| P1-3 | 启动期间取消可能早于进程 ownership 登记；部分初始化异常使已启动进程没有回收路径 | `Process.Start` 成功后**立即**登记 `_process`，之后的 stdin/stdout/stderr/exit 等待初始化全部包在 try/catch 中，失败即 `process-init-failed` 并建立收敛任务；`Teardown` 缺少退出等待任务时**补建并真正等待**；`ReleaseProcessResources` 统一"终止 → 等待退出 → 记录退出码 → Dispose" |
| P1-4 | `PumpAsync` 吞掉读取异常，Finalize 误判为 EOF | 新增 `FfmpegStreamPump`：正常 EOF 返回 `null`、读取失败返回异常；两侧都必须干净结束，否则 `drain-failed` 且**不发布**、不启动核验；缓冲饱和后仍继续排空 |
| P2-1 | 正常 Completed 后不释放 `Process` 及其流 | 退出 + 双流 EOF 之后立即 `ReleaseProcessResources()`（`ProcessDisposed`）；`Dispose()` 幂等且不破坏已发布产物 |
| P2-2 | 管线保留 `options` / `options.Settings` 可变引用 | 构造时**深复制**标量与路径（宽高 / FPS / CRF / preset / 像素格式 / 最终路径 / 临时路径覆盖 / 期望编解码器名 / 身份 / 启动缝 / 核验器 / 故障注入缝）；不再持有调用方对象 |

**定向回归（12 项，全部确定性、不依赖随机 Sleep）**：可控屏障 `WriteCommitBarrier`（成功与失败两条路径各一项）、临时路径 ownership 原子性与钉住语义（`File.Delete` / `File.Replace` 必须失败）、外来内容不被覆盖/删除/发布、两个会话之间互不清理、启动委托被阻塞时的 Start/Cancel 交错、启动后流初始化异常、排空泵 EOF 与异常的区分（单元）、stdout 读取失败阻止 Completed 与发布、Completed 后释放进程资源、连续会话不累积进程/句柄、冻结配置（构造后修改原配置不影响命令 / 帧长 / 输出路径 / 核验期望）。

**"复现旧缺陷"验证（本轮实测）**：对六项修复逐一**临时回退**成旧行为并跑完整套件，每次都只有对应的定向测试失败，随后从备份字节级还原并强制重建：

| 临时回退的旧行为 | 结果 |
| --- | --- |
| 提交临界区之前释放在途标记 | 138 passed / **2 failed**（两条屏障测试） |
| 排空泵吞掉读取异常 | 138 / **2**（泵单元 + 管线排空测试） |
| 初始化失败不建立收敛任务 | 139 / **1**（初始化异常回收测试） |
| 不持有临时文件 ownership 句柄 | 139 / **1**（ownership 钉住测试） |
| 正常完成不释放 Process | 136 / **4**（2 项新增 + 2 项既有残留断言） |
| 构造时保留调用方配置对象 | 139 / **1**（冻结配置测试） |

**保留与未变**：单帧在途与 `busy` 语义、终态仲裁与发布语义、`-y` + 显式 `-f mp4`、核验协议与负对照、L1 身份契约与 TOCTOU 说明、无固定写入超时。**仍未验证**：Unity Mono 实机（进程/异步 IO/`Exited` 语义、主线程回投）、L3 帧事务与 Finalizing 关口；本轮**未部署**游戏。

**剩余风险（不编造保证）**：ownership 句柄释放之后到 `Move`/`Delete` 完成之间存在一个极短窗口，同一用户的其他进程理论上可以在该窗口内替换文件；窗口内只有两个相邻 BCL 调用，且路径是会话目录下 16 位随机 token 的临时名，正常流程没有外来参与者。若需要零窗口，需要按文件 ID 校验身份（P/Invoke `GetFileInformationByHandle`）或内核级独占语义 —— 本轮**未**引入该依赖，交由 GPT Work 决定是否需要。

## 3. 正式导出架构

### 3.1 Render Source

```text
scrCamera.instance
  → Bgcamstatic
  → BGcam
  → camobj
  → Renderist-owned CaptureTarget (RenderTexture)
```

- session 内把三台 ADOFAI 原生谱面 Camera 的 `targetTexture` 接管到 Renderist-owned RT。
- 同一 session 内把三台 Camera 的 `aspect` **统一到冻结输出 aspect**（`outputWidth / outputHeight`），并登记 aspect ownership；cleanup 用 `ResetAspect()` 恢复 Unity 自动行为。
- **输出几何（0.3.7.0）**：session 开始时冻结最终 `outputWidth/Height`、实际 Source/Capture RT 的 `renderWidth/Height = output × scale` 与统一 Camera aspect（按 output 宽高比）。自定义分辨率关闭时 output 尺寸 = 冻结当时的 `Screen.width/height`。`FrameCaptureDriver` 只消费冻结值，不读 Screen。
- Unity 仍走正常原生 Camera 渲染；Screen Space Editor / UMM / Renderist UI 不进入该 RT。
- 已实机确认谱面 floor、planets、background、decorations 与测试谱面特效正常，PNG 朝向正常。
- 不使用 `Camera.main` clone、替代 Camera、`Camera.Render()`、ScreenCapture、AsyncGPUReadback、UI SetActive/cullingMask 隐藏方案。

### 3.2 Frame transaction

```text
FrameIndex N (long)
→ MasterTimeline: outputTime = N / OutputFps
→ chartTime = canonicalStart + outputTime × pitch
→ scrConductor.Update Prefix：强制本帧 songposition
→ 原生 scrConductor.Update
→ scrConductor.Update Postfix：RenderistAutoPlay 消费所有 due floor → 官方 scrPlayer.Hit(true)
→ 原生视觉渲染到 CaptureTarget
→ WaitForEndOfFrame
→ 按 session 开始时冻结的输出模式分支（同一捕获后端、同一结果回调、同一 CommitFrame）：
     PNG：ReadPixels → EncodeToPNG → File.WriteAllBytes → imageWritten = true
     Log-only：跳过读回 / 编码 / 写盘，也不构造 PNG 路径 → imageWritten = false, filePath = null
→ 仅成功的帧末事务才 commit N
→ completion 后按成功 commit 的**逻辑** output frame 计 End Tail
```

关键不变量：**Frame N 未成功完成 capture transaction，就不 commit N，也不开始 N+1。**

`MasterTimeline.FrameIndex` 是 export / chart timeline authority；wall clock 与 audible audio 都不推进它。`Time.captureFramerate` 负责 Unity engine timestep，不等于 MasterTimeline 拥有全部 Unity state。

**当前（0.3.6.3 起已实现）pre-entry**：`PreEntryClock` 在 Countdown 可安全识别后锁定 native schedule anchor，以 `pitch / OutputFps` 为步长；pre-entry / gameplay 都只在**逻辑帧成功 Commit** 后推进，PlayerControl 边界切入既有 MasterTimeline。不得把 source activation、输出 IO wall time 或 raw DSP 推进误作 chart-time authority；边界公式与 scoped `timeScale` ownership 见 §10.5。

### 3.3 Completion 与取消

Canonical completion：

```text
scrController.OnLandOnPortal Postfix 观察到完成请求
+ controller.state == Won
+ completion 当帧成功 capture/commit
+ 配置 End Tail 全部成功 commit
= 当前 PNG / Log-only 导出可 Completed
```

MP4 的计划语义另有 `Finalizing`：所有逻辑帧完成后仍需等待 FFmpeg 正常封装与最终文件发布（§2.1）；这**尚未实现**。

最后一个 floor 被 `Hit(true)` 不等于完成；不能用 floor index / hit count 推导完成。

Esc：

```text
scnEditor.Update 的 Esc 分支
→ scnEditor.SwitchToEditMode(false)
→ Renderist exact Postfix observer
→ RequestStop("native-playback-stopped")
→ ownership-aware cleanup
→ Cancelled / user-cancel
```

30 秒 watchdog 只是故障保护，不是正常 Esc 路径或全局 session 上限。

### 3.4 Input isolation（session-scoped）

在官方 `editor.Play()` 之前安装 6 个 exact Prefix，只在 InitializationHold / Capturing 生效：

- `scrPlayerManager.AnyValidInputWasTriggered()` → false
- `scrPlayer.ValidInputWasTriggered()` → false
- `scrPlayer.ValidInputWasReleased()` → false
- `scrPlayer.CountValidKeysPressed()` → 0
- `scnEditor.ZoomCamera(float,bool,bool)` → 跳过
- `scrController.TogglePauseGame()` → 跳过并返回当前 paused

**仅“本组 Input Guard”不 Patch** `scrPlayer.Hit`、`scnEditor.Update`、`scrPlayerManager.SetAllPlayerResponsive`、`scrPlanet.Update_RefreshAngles`。`scrConductor.Update` **不属于 Input Guard**：scheduler 按 §3.2 另行安装自己的 Prefix/Postfix。不要把“Input Guard 未 Patch Conductor”误读成整个 Renderist 没有 Conductor hook。

Esc 不经过 `TogglePauseGame`，因此 guard 不阻断 Esc cancellation。

### 3.5 Cleanup / ownership

统一原则：cleanup 必须 ownership-aware、幂等、partial-safe、retry-safe；session terminal 不等于 scheduler 已无 ownership。

- Harmony hook 保存实际成功注册的 original `MethodInfo`，正常 cleanup 精确 `Unpatch(original, prefix/postfix)`；不以 `UnpatchAll` 作为正常流程。
- `HasResidualOwnership()` 覆盖 playback、Unity timing、lifecycle handoff、`_captureGeneration`、capture host/source/target、input guard、forced clock、Conductor/AsyncInput/completion/native-stop hooks、`RDC.auto` 与 editor selection；以及 pre-entry lifecycle bridge 的 `Countdown_Update` patch 与 transient ownership（scoped beat override、pre-entry timeScale freeze / partial）。
- Camera `targetTexture` 只在当前值仍 `ReferenceEquals(captureTarget)` 时恢复 saved old target；native 已先接管/置空则不覆盖。
- **Camera aspect ownership（0.3.7.0，durable invariant）**：`targetTexture` 与 aspect 使用**各自独立**的释放路径（`RelinquishTargetTexture` / `RelinquishAspect`），但收敛判定合并到同一个 `_sourceActive` transaction。要点：
  - 激活前先只读检查三台 Camera 的实际 aspect（可读 / 有限 / 为正 / 三台互相兼容），**不通过即 fail-closed，且此时尚未写入任何 Camera**；
  - `captureTarget` / Camera refs / saved old targets / **saved baseline aspect** / 本次写入的统一 aspect 全部在**第一次 Camera 写入之前**登记，因此 setter 成功后读回失败、或 aspect 部分写入（partial assignment），都不会漏掉 ownership；
  - 激活后的读回校验**只用于日志**，读回异常不影响 ownership；
  - `RelinquishAspect` 只在当前值仍等于 Renderist 写入值时才 `ResetAspect()`；已被外部流程改写的值不覆盖、只记录（不是 cleanup failure）；
  - aspect 恢复失败与 targetTexture 恢复失败一样返回 false 并保留 residual（`_sourceActive` 保持 true），由下一次 `Stop()` 重试，绝不被提前清空。
- live Camera 仍引用 captureTarget 时绝不 Release/Destroy RT。
- **capture host ownership**：`CaptureHostBehaviour.Shutdown()` 先同步失效 coroutine/callback；`_host/_behaviour` 只有在 `Destroy(host)` 返回成功后才清空。Destroy 抛异常时保留引用，下一次 `Stop()` 可重试。
- **capture target ownership**：Camera ownership 已 relinquish 后，`Release()` / `Destroy()` 任一步失败都返回 cleanup failure，并保留 `_captureTarget` 及必要状态；只有两步成功后才丢引用。异常不再被吞掉。
- **activation 前的 ownership tracking（durable invariant）**：capture target 从 **RenderTexture 创建成功那一刻**起就受 ownership tracking——创建后到 source activation 完成之间的任何失败路径都必须二选一：Release + Destroy 均成功，或引用保存在 `_captureTarget`（可观察、可重试），绝不作为 local reference 丢失。其中：
  - 尚未接触任何 Camera 的失败（`IsCreated()==false`、读取 saved old target 抛异常、销毁失败）→ 只保留 `_captureTarget`，`_sourceActive` 保持 false，由 `Stop()` 的 source-inactive retry path 重试 `TryDestroyTexture`；
  - 已可能写入过 Camera 的失败（partial Camera assignment）→ 因为 `_captureTarget` / capture dimensions / camera refs / saved old targets 在**第一次 Camera 写入之前**就已登记，`_sourceActive` 表示"ownership transaction 已开始（可能 partial）"，失败即走同一个 `RestoreCameraSource`（不新增第二套 partial cleanup）；rollback 写失败时保留全部 refs 作为 residual，下一次 `Stop()` 重试；
  - **只有 `IsCaptureTargetStillReferenced() == false` 时才 Release / Destroy RT**：partial 状态下即使 cleanup 失败也**不会**销毁仍可能被 live Camera 引用的 RenderTexture。
- 若 Camera 已 relinquish、host 已销毁，但 capture target 销毁失败，scheduler 的 `_captureGeneration` 与 `FrameCaptureDriver.HasOwnedCaptureTarget` 仍构成 residual ownership；下一次 cleanup 只需重试资源销毁，不会重新改写 Camera。
- **降采样链 ownership（0.3.7.0 第二闭环，durable invariant）**：Source RT 与降采样各级使用同一套规则 —— **构造成功后立即登记**（`_captureTarget` / `_downsampleTargets[i]`），之后才执行可能抛异常的属性设置 / `Create` / `IsCreated` 校验；**所有 RT 准备成功后**才允许接管 Camera。释放按**叶子 → 根**，每级都必须 `Release` + `Destroy` 全成功才清空该槽位，任一失败即保留 residual（`HasOwnedDownsampleChain`）并由下一次 `Stop()` 重试。**不使用 `RenderTexture.GetTemporary`**：池化 RT 的生命周期不由本模块独占，无法与 residual / 重试语义共存。
- **GPU 状态 ownership（0.3.7.0 第二闭环，durable invariant）**：`RenderTexture.active` 与 `GL.sRGBWrite` 是**两个独立的 restore token**，独立恢复；任一未恢复 ⇒ 当前帧失败（不 `Apply` / 不 `EncodeToPNG` / 不写盘 / 不 commit）且保留 residual（`HasResidualGpuState`）。**保存失败且尚未发生任何状态修改时不产生虚假 residual**。**GPU 状态未全部恢复前绝不 Release / Destroy 任何可能仍被该状态引用的 RT**（`RestoreCameraSource` 在 GPU 状态未恢复时直接返回 false，不释放链与 Source）。CPU 侧的 `Apply` / 编码 / 写盘严格发生在 GPU 状态全部恢复**之后**。
- **residual gate 覆盖新增 ownership**：`HasOwnedDownsampleChain` 与 `HasResidualGpuState` 已并入 `DeterministicFrameScheduler.HasResidualOwnership()`（见 §3.5 开头的统一原则），因此这些未收敛状态同样会在创建 session 目录前 fail-closed。
- `RestoreAll` 不短路：各项尽量恢复并收集 failures；有任何 failure 时 residual token 不应被提前清空。下一次 Start / Stop / Mod disable 会先补做 cleanup。
- 0.3.5.1 已实机确认 residual ownership 可跨调用保留并在下一次入口补做；本轮 hardening 针对的是此前未覆盖到的 host Destroy / RT Release/Destroy 异常分支。该异常分支仍缺真实 Unity fault injection。

---

## 4. Render Time、Output FPS 与 Safety

### 4.1 Render Time

已实机确认：

```text
Time.captureFramerate = OutputFps
Time.captureDeltaTime = 1 / OutputFps
Time.deltaTime        = 1 / OutputFps
```

capture transaction 中 1 个 output frame 对应 1 个连续 Unity frame。

- `Time.timeScale` **不属于全局、永久的 Renderist ownership**：普通 gameplay 不随意改写；已实现的 native pre-entry hidden phase 则会**限域保存、写为 0，并在 G 成功 Commit 后恢复原始值**。详见 §10.5；不得因“通常不写”否认这一已实施例外。
- `controller.paused` 是 playback runtime condition，不是 editor-idle 启动前条件：editor idle / 返回编辑模式时 `paused=true` 正常；官方 `editor.Play() → scnGame.Play()` 才清为 false。只有 lifecycle 到达 `PlayerControl + playerAlive` 后仍 paused / 不可读才 fail-closed。
- Renderist 不写 `controller.paused`、不主动调用 `TogglePauseGame`。

Frame index：

- canonical/output frame 编号与主要计数链用 `long`：MasterTimeline、scheduler output/pending/capture/tail/completion counts、FrameCaptureDriver 文件编号。
- Unity `Time.frameCount`、FPS 配置与 Unity API 仍为 int，这是外部 API 域。
- `frame_<index>.png` 的 6 位只是**最小补零宽度**，超过 6 位自然扩展，不截断。
- `frame → seconds` 使用 double。超过约 `2^53` frame 后相邻整数不一定能在 double 时间中逐一表示；这是数值类型精度边界，不是产品上限。不得用人为 FPS/时长限制“修复”它。
- frame index 到 `long.MaxValue` 无法表达下一帧时显式 fail-closed `frame-index-exhausted`，属于结构边界。

### 4.2 Output FPS

`OutputFpsPolicy` 唯一语义：**当前 int/API 可表达的正整数都合法**。

- Minimum = 1；默认 60；没有产品级 Maximum。
- `Application.targetFrameRate` 只是 best-effort derived hint；`outputFps * 4` 用 long 计算并饱和到 int.MaxValue，绝不反向限制 Output FPS。
- 30 FPS / 1000 FPS 完整实机导出均通过。

### 4.3 Safety

`SafetyFrameLimitPolicy` 是唯一解析/判定点：

- `<=0` → unbounded。
- legacy `36000` → unbounded。
- 其它正整数 → explicit frame limit，原样使用，不 clamp。
- `36000` 存在已知兼容歧义：无法区分历史默认值与用户手工显式写入值，因此统一按 unbounded 解释。
- unbounded 的 metadata 对外写 `safetyPolicy=unbounded`、frame/duration 为 null，不用 0/long.MaxValue 伪装真实上限。
- watchdog 只保护 initialization、单次 capture callback、逐帧 progress 的“无进展”异常；不是总时长限制。

---

## 5. End Tail policy

用户配置一组 `EditorEndTailValue + EditorEndTailUnit`，单位 Frames / Seconds / Beats，默认 12 Frames。

解析：

- Frames：必须为非负整数，resolved = input。
- Seconds：`ceil(seconds × OutputFps)`。
- Beats：`ceil(beats × 60 × OutputFps / (completionBpm × pitch))`。
- completion signal 当帧仍输出；tail 从其后的成功 commit 开始计数。
- 0 tail = 不额外输出；1 tail = completion 后再输出 1 个成功 commit frame。
- double→long 在 2^63 可表达边界显式 fail-closed，不用人为产品上限规避。

### 5.1 Frames 整数语义

直接 Frames 输入采用**固定绝对容差 `1e-10`**判断是否整数，不能随 magnitude 放大。

旧的 `1e-10 × max(1, abs(value))` 相对容差已确认会在大数上误接收明显小数（例如 `1e9+0.05`、`1e12+0.5`）。因此产品输入整数判定必须使用固定绝对容差，与 GUI 文本解析语义一致。

Seconds / Beats 换算得到的 raw frame count 属于计算结果，不等同于用户 Frames 输入：仅允许用 `max(1e-10, half-ULP(nearestInteger))` 吸收浮点表示误差，然后再 `Ceiling`。half-ULP 是数值表示边界，不是参数上限。

### 5.2 Persisted End Tail 语义（已确定采用方案 A，代码已实施）

`Settings.EditorEndTailValue` / `EditorEndTailUnit` 是唯一 persisted authority；GUI 字段（`_endTailValueText` / `_endTailDisplayedUnit` / `_endTailInputValid` / `_endTailInputError`）只是 view / edit state。

- `ModEntry.ResetEndTailGuiState()` 只重建 view：**不 sanitize、不写回 Settings**（无默认值替换、无 clamp 到 0、无 fractional Frames 的 `ceil`、无非法 unit 替换），展示使用无损格式化（NaN / Infinity / 负数 / 小数如实显示）。
- 非法 persisted 值保持非法并 fail-closed；export 由既有链路阻断：`EditorExportPreflight → EndTailPolicy.TryResolve → InvalidEndTail → EditorExportController.Start 拒绝`（未新增第二套校验）。
- 唯一写回路径是用户显式编辑 `ApplyEndTailTextInput`（parse 成功且 `EndTailPolicy.TryValidateInput` 通过才写 Settings）；`SwitchEndTailUnit` 在非法状态下不写 Settings、不做 default laundering；合法值的单位换算行为不变。
- 可达范围（XmlSerializer 实测）：`EditorEndTailValue` 为 NaN / ±Infinity / 负数 / 小数（含 `1e308`）都能成功 Load → 属本语义负责范围；`EditorEndTailUnit` 为未定义枚举值，或数值溢出（如 `1E+309`）会在 `ModSettings.Load` 阶段抛异常 → 属 malformed `Settings.xml`，**不在**本语义范围（不为此扩展 Settings loader）。
- 合法 persisted 值（Frames / Seconds / Beats）行为不变；`EndTailPolicy` 未修改（`end-tail-frame-count-overflow` 仍是 long 表示边界而非产品上限）。
- 代码语义已实施（静态 / 逻辑验证 25/25）并已**通过用户实机验证**：`-5` / `12.5` / `NaN` 均保持非法且未被 sanitize 或 ceil，显式改为合法值 `12 Frames` 后导出恢复；**PERSISTED END TAIL RUNTIME PASS**。
---

## 6. Deterministic autoplay

`RenderistAutoPlay` 只消费 due floor 并复用官方 `scrPlayer.Hit(true)`；它不拥有时间，也不是 replay。

关键 invariant：

- 每次 Hit 前 fail-closed 建立：`multipressPenalty=false`、`multipressAndHasPressedFirstPress=false`、`consecMultipressCounter=0`、`keyTimes.Clear()`、`SetAllPlayerResponsive(true)`。
- 任一 load-bearing 准备失败，不调用 `Hit(true)`，以 `hit-state-prepare-failed:*` 结束。
- `SetAllPlayerResponsive(true)` 是 required API：静态 IL 确认 `scrPlayer.Hit(bool)` 第一个 guard 就读取 `responsive`。
- `RDC.auto` 只在单次 Hit 事务内临时置 true，finally 恢复；恢复失败优先上报。
- 不写 `averageFrameTime`。该字段唯一 writer 是 `scrController.PlayerControl_Update()`，唯一外部 reader 是 `scrPlanet.SwitchChosen()`；它影响 multipress 惩罚/显示，不是基础命中时间 authority。
- 单 output frame 的 progression bound = 当前 `floors.Count`，不是固定魔数；每次 Hit 要求 seqID 严格前进。

0.3.5.1 实机曾验证连续高密度单帧 27–28 次命中、completion 与 End Tail 均正常；0.3.6.1 30 FPS 实机也出现同一 output frame 成功 Hit 两次。

---

## 7. 关键模块

- `MasterTimeline`：`FrameIndex` 为 long；唯一 export/chart timeline authority。
- `DeterministicFrameScheduler`：启动、Initialization Hold、Conductor Prefix/Postfix、autoplay、capture transaction、completion、tail、input guard、native stop observer、cleanup。
- `PlaybackLifecycleHandoff`：关联本次 Renderist-owned `editor.Play()`；ready 需要 Start + OnMusicScheduled + Countdown + PlayerControl + playerAlive + !paused。
- `EditorVisualClock`：强制视觉 songposition，并精确撤销 hooks。
- `RenderistAutoPlay`：due-floor helper，复用官方 Hit。
- `FrameCaptureDriver`：generation 隔离、两阶段 Camera source activation（创建 Source RT → **PNG 且 scale>1 时创建降采样链** → 读并校验三台 aspect baseline → 登记 targetTexture + aspect ownership → 逐 Camera 接管 targetTexture 并写入统一 aspect）、同步帧末事务（PNG：可选降采样链 → ReadPixels → **GPU 状态全部恢复** → Apply/编码/写盘；log-only：仅校验并以 `imageWritten=false` 成功返回）、ownership-aware cleanup（activation 窗口内也不丢 ownership；targetTexture / aspect / **降采样链** / **GPU 状态** 各自独立释放、合并收敛）。Source 与各级 RT 的尺寸、最终 output 尺寸与统一 aspect 由 scheduler 在 session 开始时冻结后传入，驱动**不读 Screen**；模式由 `Start` 一次性冻结（`_imageOutputEnabled`），session 期间不再读 Settings。**scale=1 时完全不进入降采样 / Blit / sRGBWrite 路径。**
- `OutputGeometryPolicy`：输出分辨率模式、**超采样倍率**、合法性判定、解析与**降采样链规划**的唯一单点（legacy-window / custom-resolution；`multi-stage-bilinear`）；只做 int 表达能力（`checked` 乘法）+ `SystemInfo.maxTextureSize` 硬件能力检查（作用于**实际 render 尺寸**），不定义产品级上限。GUI 文本解析（正整数 / 倍率）也走这里，因此 GUI 与 preflight / scheduler 的合法集合完全一致。
- `RenderEnvironmentInventory`：只读运行时渲染环境 inventory（色彩空间 / GPU / capture RT 形态），供 session metadata 与启动日志核对；任何单项读取失败折成 null / `unavailable`，绝不阻断导出。
- `EndTailPolicy`：Frames / Seconds / Beats 校验与 output-frame 解析。
- `OutputFpsPolicy`：正整数 Output FPS 与 targetFrameRate 安全派生。
- `SafetyFrameLimitPolicy`：unbounded / explicit frame limit 的唯一 policy。
- `EditorExportController` / `EditorExportSession`：preflight、session、metadata、终态。
- `EditorGameReflection`：当前 ADOFAI 内部 API 的运行时反射层。

---

## 8. Termination 与 metadata

主要终态：

| 状态 | 条件 | terminationKind |
| --- | --- | --- |
| Completed | canonical completion + tail 排空 | `canonical-completion` |
| Cancelled | Stop / Esc / leave editor / mod disabled | `user-cancel` |
| Failed | 显式 safety frame limit | `safety-limit` |
| Failed | initialization/capture/progress watchdog | `watchdog` |
| Failed | 帧末事务 / PNG 请求 / 写盘 | `capture-failure` |
| Failed | API/lifecycle/cleanup | `lifecycle-failure` |

取消时允许 `captureRequestCount = capturedFrameCount + 1`：一个已请求但未 commit 的事务可以被取消，不能为了计数相等而伪 commit。

### 8.1 输出模式与计数语义（log-only 最小闭环，代码已实施）

`Settings.EditorImageOutputEnabled`（默认 `true` = 既有 PNG 序列行为）在 **session 开始时一次性冻结**为 scheduler 的 `_imageOutputEnabled`；session 期间不再读 Settings，运行中修改 GUI 不影响当前 session（GUI 在忙碌时禁用编辑，与 Output FPS / End Tail 一致）。该开关与 `VerboseLogging` 无关。

两种模式共用**同一个** scheduler、同一条帧事务入口（`FrameCaptureDriver.RequestCapture`）、同一个 EOF coroutine、同一个结果回调与**唯一** `CommitFrame`：

- Log-only 仍接管 Camera source / RenderTexture、仍走 `WaitForEndOfFrame`、仍做 source / generation / pending-index 校验，只是跳过 `EnsureTexture` / `Texture2D` 创建 / `ReadPixels` / `Apply` / `EncodeToPNG` / `File.WriteAllBytes`，并**不构造 PNG 文件路径**。
- 帧末结果用显式 `imageWritten` 标志区分；`imageWritten != _imageOutputEnabled` 时 fail-closed（`frame-transaction-mode-mismatch`）。

计数拆分（避免用 PNG 成功数冒充逻辑推进）：

| 字段 | 语义 | log-only |
| --- | --- | --- |
| `frameTransactionRequestCount` | 请求过的逻辑帧事务数 | `= logicalFrameCount` |
| `captureRequestCount` | PNG 图像请求数 | `0` |
| `logicalFrameCount` | 成功提交的逻辑输出帧数（派生自 `_outputFrameIndex`，不重复维护计数器） | 正常递增 |
| `writtenPngFrameCount` / `capturedFrameCount` | 成功写盘 PNG 帧数（同一个计数） | `0` |
| `tailFramesCommitted` | 成功提交的逻辑尾帧数 | 正常递增 |
| `tailFramesCaptured` | 成功写盘的 PNG 尾帧数 | `0` |

- PNG 完成会话：`frameTransactionRequestCount = captureRequestCount = logicalFrameCount = writtenPngFrameCount = capturedFrameCount`。
- Log-only 完成会话：`frameTransactionRequestCount = logicalFrameCount`，其余 PNG 计数为 `0`。
- completion / End Tail / safety limit / progress watchdog 全部使用**逻辑** authority（`_outputFrameIndex` / `_tailFramesCommitted`）；pre-entry 内部成功计数同样按逻辑 commit。PNG 模式仍保持“只有成功写盘后才 commit”。
- PNG 模式的写盘失败原因不变（`write-png-failed`）；log-only 的帧末事务失败为 `frame-transaction-failed`，不会被误报为 `write-png-failed`。
- metadata 的 `mode`：PNG 为 `editor-export-png-sequence`（既有值不变），log-only 为 `editor-export-log-only`；新增 `imageOutputEnabled`。旧字段全部保留。

metadata 记录：版本/phase/mode/imageOutputEnabled、state/detail/reason/kind、completion signal/index、output FPS、pitch、End Tail 输入与解析结果、safety policy、capture source/尺寸、上述全部逻辑与 PNG 计数。

### 8.2 输出几何与运行时 inventory（0.3.7.0 新增字段）

输出几何字段（`0.3.7.0` 新增；旧字段全部保留）：

| 字段 | 语义 |
| --- | --- |
| `outputGeometryMode` | `legacy-window`（沿用窗口，默认）或 `custom-resolution` |
| `geometryCustomResolutionEnabled` | session 开始时冻结的自定义分辨率开关 |
| `geometryConfiguredWidth` / `geometryConfiguredHeight` | Settings 中 persisted 的原始宽高（**未解析、未 sanitize**，诊断用） |
| `outputWidth` / `outputHeight` | session 开始时冻结的**最终输出尺寸**；scale=1 时等于 Source/Capture RT，scale>1 时**小于** `renderWidth/Height` 与 `captureWidth/Height` |
| `outputAspect` | 冻结的统一输出 aspect（三台原生 Camera 与 capture RT 共用） |

只读运行时渲染环境 inventory（`0.3.7.0` 新增；读取失败为 `null`，绝不伪造值）：

`colorSpace`（`QualitySettings.activeColorSpace`）、`graphicsDeviceType` / `graphicsDeviceName` / `graphicsDeviceVersion`、`graphicsShaderLevel`、`maxTextureSize`、`supportsComputeShaders`、`systemMemorySizeMb`、`renderTextureFormat` / `renderTextureGraphicsFormat` / `renderTextureAntiAliasing` / `renderTextureUseMipMap`。

- 既有 `captureWidth` / `captureHeight` 保留（仅在激活后非 0）；`outputWidth` / `outputHeight` 从 session 开始即已知。
- 该 inventory 是后续 supersampling / 降采样工作的基线事实来源（尤其 `colorSpace` 与 RT format）。
- **当前受测环境的实测值**（`0.3.7.0` 实机 8/8 session metadata 完全一致；仅用于记录本次受测环境，**不得推广为所有用户环境**）：`colorSpace=Gamma`、`graphicsDeviceType=Direct3D11`（`graphicsDeviceVersion=Direct3D 11.0 [level 11.1]`）、`graphicsShaderLevel=50`、`maxTextureSize=16384`、`supportsComputeShaders=true`、`systemMemorySizeMb=32304`、`renderTextureFormat=ARGB32`、`renderTextureGraphicsFormat=R8G8B8A8_UNorm`、`renderTextureAntiAliasing=1`、`renderTextureUseMipMap=false`；GPU 名称为受测机型号，不作为基线要求。
- 同一受测环境中 `0Harmony.dll` 实际 `FileVersion` 实测为 **`2.3.6.0`**（`references\UMM` 与游戏 `Managed\UnityModManager` 两处一致），与 §1 记录的 Harmony 基线相符。

---

## 9. Runtime / 静态验证基线

### 9.1 实机（0.3.6.1 / 0.3.6.2 / 0.3.6.3 / 0.3.7.0）

**0.3.6.1 30 FPS**：

- editor idle 直接启动正常；paused 生命周期修复有效。
- lifecycle：Start → OnMusicScheduled → Countdown → PlayerControl。
- autoplay 正常，同帧双 Hit 成功。
- canonical completion 正常。
- completion 后精确 12 End Tail。
- `frame_000000.png .. frame_000113.png` 连续。
- 最终 Completed。

**0.3.6.1 1000 FPS**：

- `state=Completed`
- `stopReason=canonical-completion-tail-drained`
- `terminationKind=canonical-completion`
- `completionFrameIndex=3323`
- `safetyPolicy=unbounded`，frame/duration 均 null
- `resolvedTailFrames=12`，`tailFramesCaptured=12`
- `captureRequestCount=capturedFrameCount=3336`
- capture 3072×1920
- 未出现 controller-paused、hit-state、RDC restore、safety-limit、frame-index-exhausted 或 watchdog failure。

**0.3.6.2 30 FPS smoke**（activation ownership hardening 的正常 Player 路径）：

- `state=Completed`，`stopReason=canonical-completion-tail-drained`，`terminationKind=canonical-completion`
- `completionFrameIndex=101`；`resolvedTailFrames=12`，`tailFramesCaptured=12`
- `captureRequestCount=capturedFrameCount=114`，frames `0..113` 连续（PNG 与 scheduler commit 均 114 次、无缺号）
- `captureSource=scrCamera-rendertexture`，3072×1920
- 未出现 `capture-source-failed`、`capture-target-*`、`cleanup-failed`、`capture-stop`、`tick-exception`、host destroy / RT Release / RT Destroy failure、restore incomplete 或 residual retry warning

**0.3.6.3 native pre-entry 正式化后的实机验证**（关键时间模型见 §10.5；此处只保留验收摘要）：

- 当前基准谱面（Output FPS=30、pitch=1）**连续双跑 PASS**：`B=G=44`，frame 43 为最后一个 pre-entry，frame 44 = gameplay frame 0（partial timestep），frame 45 恢复完整 timestep；Planet / Trail boundary continuity PASS；两次均 `Completed` / `canonical-completion-tail-drained` / 12 帧 End Tail。
- 第二组谱面（Output FPS=30、BPM=100、pitch=1）**PASS**：`B=G=60`、`partialFraction=0.1`，frame 59 / 60 / 61 归属正确，Planet / Trail continuity 正常，timeScale 于 G commit 后恢复，`Completed` / 12 帧 End Tail。
- **persisted End Tail 方案 A 实机 PASS**：非法 persisted 值（`-5`、`12.5` Frames、`NaN`）保持非法且未被 sanitize 或 ceil，显式改为合法 `12 Frames` 后导出恢复。
- 上述通过只对应 `0.3.6.3` 正式化后的实现与该两组谱面 / 设置；早期版本的通过结果仍按各自版本记录，不视为对最新版本的覆盖。

**0.3.6.4 Log-only Frame Transactions 实机验收**（结果由用户提供；同一基准谱面，Output FPS / pitch 沿用当前基准设置）：

- PNG 与 Log-only 两种模式**均正常完成**。
- 两模式均提交 **251 个连续逻辑帧**（`logicalFrameCount=251`）。
- `completionFrameIndex = 238`；`tailFramesCommitted = 12`（两模式一致）。
- Pre-entry **`B = G = 57`**，`partialFraction = 0.64`。
- 两模式的 **Hit frame index 一致**。
- **PNG → Log-only → PNG 切换正常**。
- `cachedAngle` 的首次运行差异可在 **PNG → PNG** 中复现，因此**不属于 Log-only 独有差异**。
- Log-only gameplay 中途取消两次：分别为 **90 成功提交帧 / 91 事务请求**、**183 / 184**；取消**没有伪提交 pending frame**；取消后 Log-only 再次完整导出成功。
- 用户已确认 Log-only 的实际输出目录**无 PNG**。
- 适用范围限定：上述通过只对应 `0.3.6.4` 的实现与该基准谱面 / 设置。**未直接覆盖**的场景见 §9.4 与 §11（pre-entry 阶段取消 / End Tail 阶段取消 / 取消后切换 PNG），不得记为已通过。

**0.3.7.0 第一闭环 Custom Resolution 实机验收（已通过）**：

实机材料：`Player.log`（单一游戏进程，8 次导出）+ 输出根目录 `D:\Output\ADOFAI Captures` 下 8 个 `0.3.7.0` session 目录（每个含 `metadata.json`）。受测构建身份：已部署 `Mods\ADOFAI.Renderist\ADOFAI.Renderist.dll` 与 `bin\Release` 逐字节一致（SHA256 `14D335FD2E2C051DBCE43BF1DB414F8ED177BEB221846EFB4FB50D761DBFBBBA`，`ProductVersion=0.3.7.0+05a3b4a…`），发布包 zip SHA256 `B251B6A4631401E44F96130E152FB834B70B47CE6E75CA45304DC43380A4155F`。

- **8 个 session** = **6 个 PNG session**（合计 **1117 张 PNG**）+ **2 个 Log-only session**（PNG 计数全 0）。终态：Completed 5 次、Cancelled 3 次。
- **输出尺寸验证**（读 PNG IHDR 实测，不依赖 metadata 声称）：`legacy-window` 3072×1920（251 张）、`custom-resolution` 1080×1080（251 / 186 / 51 张）、256×256（251 张）、1920×1080（127 张）。每个 session 内**所有 PNG 尺寸单一**，且与该 session 的 `outputWidth` / `outputHeight` 一致。
- **PNG 编号与数量**：编号 `frame_000000.png …` **连续、无缺号、无重复、无异常命名**；**磁盘 PNG 数 = 日志 `FrameCaptureDriver: wrote` 行数 = metadata `writtenPngFrameCount`**（251 / 251 / 251 / 186 / 127 / 51，合计 1117），三方一致。
- **PNG 与 Log-only 逻辑计数一致**：PNG 完成会话 `frameTransactionRequestCount = logicalFrameCount = captureRequestCount = writtenPngFrameCount = capturedFrameCount`；Log-only 完成会话 `frameTransactionRequestCount = logicalFrameCount = 251`、PNG 计数全 0。与**参数同构**的一对（`152418` Log-only vs `152427` PNG，同为 legacy 3072×1920、Output FPS 30、pitch 1、BPM 100、End Tail 12 Frames）逐项相等：逻辑帧 251、事务请求 251、`completionFrameIndex` 238、`tailFramesCommitted` 12、`stopReason` 一致。
- **Completed / Cancelled cleanup 正常**：日志无 `cleanup-failed`、无 `capture source 释放未完成`、无 residual、无 RT `Release` / `Destroy` 失败、无 `capture-source-assign-failed`；三次取消之后的下一 session **均正常启动并跑到终态** ⇒ 无跨会话 residual ownership（residual gate 是 fail-closed 的，会阻止新 session 创建目录）。
- **取消路径覆盖（三种时机，仅 PNG 模式）**：
  - `152602`：Esc 触发，`stopReason=native-playback-stopped`，`logicalFrameCount=186` / `frameTransactionRequestCount=187`（一个已请求但未 commit 的事务被取消，**无伪 commit**）；日志确认 native `SwitchToEditMode → SetupRTCam(false)` 已先把三台 `targetTexture` 置 null，Renderist 未覆盖、只记录；
  - `152622`：GUI 停止，`stopReason=user-stop`，127 / 127（取消落在两次请求之间）；
  - `152633`：GUI 停止，**取消发生在 pre-entry / Countdown 阶段** —— 该 session `boundaryOutputFrameIndex=57`、`logicalFrameCount=51`，逐帧为 `timeline=native-preentry controllerState=Countdown`，且 8 个 session 中唯独它**没有**出现 `回放就绪，Initialization Hold 已释放`；同样**无伪提交、cleanup 正常、后续 session（`152644`）正常启动并 Completed**。
  - 边界提醒：上述 pre-entry 取消证据**只覆盖 PNG 模式**；Log-only 的 pre-entry 取消仍未取得实机证据（见 §9.4 与 §11）。
- **跨会话 Camera aspect 恢复正常**：8 次会话每次 `FrameCaptureDriver: capture source active` 的三台 `baselineAspect` 均为 `1.6`（= 当前窗口自动值），而前一次会话写入的是 `1` 或 `1.777778` ⇒ Completed 与 Cancelled 路径上 aspect 均已回到自动模式。结合 cleanup 侧证据（`cleanup-failed` = 0、`aspect ownership already relinquished` = 0、setter 失败 = 0，因此在写入过 aspect 的前提下 `RelinquishAspect` 唯一可成功返回的分支就是 `ResetAspect()` 成功），构成 aspect 恢复的独立日志证据。
- **不同宽高比下的几何一致性（像素证据）**：取相同逻辑帧号抽样比对——1:1 输出与 16:9 输出的中央 1:1 裁剪、legacy 1.6 与 1:1、以及 256×256 降采样比对，平均绝对误差（MAE）落在重采样 / 抗锯齿残差量级（0.17–1.91），而错位裁剪对照为 16.5–25.8 ⇒ 排除「aspect 未接管导致拉伸 / 挤压」与「RT 尺寸与相机 aspect 不一致导致构图错位」。
- **legacy 兼容性（像素证据）**：`0.3.7.0` legacy-window（3072×1920）与历史 PNG 基线同几何、同谱面比对，MAE **0.45–0.80** ⇒ 新增的 aspect 写入（其数值等于窗口 aspect）未改变 legacy 输出。该结果描述为**像素级高度一致**，**不是**逐像素完全相同。
- **帧时间确定性无回归**：8/8 session `boundaryOutputFrameIndex = 57`、`previousBoundaryTime = 1.778667`、`canonicalStart = 1.8`、`partialFraction = 0.64`，与上面 `0.3.6.4` 记录的数值一致。
- **未发现阻塞性产品问题。**

**0.3.7.0 第一闭环证据边界（不得互相冒充）**：

| 结论 | 证据类型 |
| --- | --- |
| session 数 / 终态 / `stopReason` / `completionFrameIndex` / End Tail / 输出几何冻结值 / 全部逻辑与 PNG 计数 / 运行时 inventory | session `metadata.json` |
| 每次导出的启动与停止、`frozen output geometry`、`render environment inventory`、`capture source active` + `baselineAspect`、`wrote` 行数、cleanup 是否成功（有无 `cleanup-failed`）、失败 token 计数、Exception 计数 | `Player.log` |
| PNG 实际尺寸单一性、编号连续性、数量与 metadata 一致、跨几何构图一致性、legacy 与历史基线一致性 | PNG 像素分析（读 PNG header + 抽样相同逻辑帧号定点比对；本次分析侧不具备图像可视化能力，故不据此断言观感） |
| 窗口 resize 后 Camera aspect **自动跟随窗口**这一动态行为；不同宽高比下画面构图的**最终视觉观感** | 用户实机观察（本轮日志中窗口尺寸恒为 3072×1920、无 resize 事件，因此日志**无法**独立复核该动态行为） |
| `OutputGeometryPolicy` 参数语义、几何冻结时机、aspect ownership / `ResetAspect` 路径、唯一 `CommitFrame`、PNG 与 Log-only 共用 EOF 事务、metadata 字段来源 | 静态代码核对（本轮只读核对，未修改代码） |

### 9.2 已验证的历史事实（仍影响当前设计）

- RenderTexture source isolation 已实机验证：PNG 不含 Editor/UMM/Renderist UI；测试谱面视觉内容正常。
- Esc native observer + ownership-aware Camera restore 已实机验证：native 先把 Camera targetTexture 置空时 Renderist 不覆盖；可立即再次导出。
- residual cleanup 跨调用保留与补做已实机验证；异常后新 Start 会在创建 session 目录前被 residual gate 拒绝，解除故障后可补做恢复。
- 同进程 Completed / Esc Cancelled 多轮连续导出无 residual 累积。
- 官方 Editor Auto（`RDC.auto=true`）会破坏正常 handoff：可能在 Countdown/PlayerControl ready 前自行完成关卡；正常 Renderist 使用与验证应保持官方 Auto 关闭。Renderist 不修改用户原有 RDC.auto 持久状态。

### 9.3 静态 / 数值回归要点

- Output FPS 正整数无产品级 Maximum；targetFrameRate 大值安全饱和。
- safety 默认 unbounded；legacy 36000 兼容策略如 §4.3。
- long frame chain 无 int 截断；filename 超 6 位自然扩展。
- End Tail 直接 Frames 的大数小数不能被 magnitude-relative tolerance 误接收。
- `TryPrepareHitState` failure path 在官方 Hit 前退出，且 RDC.auto finally 仍覆盖事务。

`0.3.6.2` 收敛时的验证结果（临时 harness / 静态断言，测后已全部删除）：

| 验证 | 结果 |
| --- | --- |
| activation fault injection（production `FrameCaptureDriver.cs` + Unity stub，注入 Destroy / Release / targetTexture 异常） | 45 checks / 0 failures |
| activation 静态控制流断言 | 29 / 0 |
| End Tail harness | 68 / 0 |
| cross-module 回归（Output FPS / safety / long frame / End Tail） | 119 / 0 |
| cleanup 静态证明 | 36 / 0 |
| Release Rebuild / package / verify | 0 error / success / PASS 11–0 |
| 30 FPS Unity smoke | 通过（见 §9.1） |

### 9.4 log-only 最小闭环（0.3.6.4）验证状态

**最终审查 + 发布收敛（0.3.6.4）实测结果**：

| 验证 | 方式 | 结果 |
| --- | --- | --- |
| 最终代码审查 | 75 项结构断言（覆盖下方 10 项审查重点 + 取消路径 + 范围控制） | 75 PASS / 0 FAIL；**未发现阻塞性问题** |
| Release Rebuild | `dotnet build -c Release -t:Rebuild` | 0 error / 0 warning；内嵌 `FileVersion=0.3.6.4` |
| package + verify | `scripts/package-release.ps1 -Configuration Release -Version 0.3.6.4 -Force`（提交前）与提交后 `-SkipBuild` 重新打包 | 两次均成功；verify **PASS 11 checks / 0 failures** |
| 发布包内容 | 独立解包复核 | 仅 3 个顶层文件（`Info.json` / `ADOFAI.Renderist.dll` / `LICENSE`），无目录、无 banned 内容；包内 DLL 与 `bin\Release` 逐字节一致 |
| metadata 计数与序列化 | **临时 harness（生产 `EditorExportSession.cs` + 最小 stub，已删除）** | 20 PASS / 0 FAIL：PNG 251 计数等式、Log-only `ftr=lf=251` 且 PNG 计数为 0、取消形态（`lf=90` / `ftr=91`）无伪提交、`mode` 取值、29 个字段齐全、JSON 严格可解析、无重复键、转义往返 |
| Settings.xml 向后兼容 | 同 harness（XmlSerializer + 生产 `Settings.cs` 声明比对） | PASS：旧 Settings.xml 缺字段时 `EditorImageOutputEnabled=true`；显式 `false` 可读回/写出；不绑定 `VerboseLogging` |

审查覆盖的 10 项重点及结论（均为静态/结构证据）：

1. Log-only 与 PNG 共用同一个 EOF 帧事务 —— PASS（`Observe()` / `new WaitForEndOfFrame()` 各 1 处且不按模式分支）。
2. `CommitFrame` 仍是唯一逻辑推进点 —— PASS（唯 1 调用点、`_outputFrameIndex++` 唯 1 处）。
3. 模式在 session 开始时冻结 —— PASS（controller 只读一次 Settings；scheduler 不读 Settings；`_imageOutputEnabled` 仅在 `TryStart` 赋值）。
4. Pre-entry 与 End Tail 依赖逻辑 commit —— PASS（3 处 completion/End Tail 判定全部使用 `_tailFramesCommitted`；`_tailFramesCaptured` 不参与任何判定）。
5. 取消正确废弃 pending transaction —— PASS（`ProcessStop` 先置 `_running=false`，`OnCaptureResult` 首行 `if (!_running) return;`；`FrameCaptureDriver.Stop()` 先失效 generation 再 `Shutdown()`；驱动 `Shutdown()` 清 pending / callback 并停 coroutine）。
6. 迟到 callback 受 generation 与 pending-index 防护 —— PASS（`_pendingStopReason` → `_captureGeneration` → `_pendingCaptureIndex` 顺序不变）。
7. cleanup 无模式相关遗漏 —— PASS（`RestoreAll` 内 `_imageOutputEnabled` 命中为 **0**；pre-entry timeScale / scoped beat override 恢复与模式无关）。
8. PNG 模式保持"写盘成功后才 commit" —— PASS（`File.WriteAllBytes` 先于成功回调；失败仍为 `write-png-failed`）。
9. metadata 新旧字段兼容 —— PASS（新增 9 个字段；既有字段全部保留；PNG `mode` 值不变）。
10. Settings 默认 `true` 保持旧用户 PNG 行为 —— PASS（实际用户 Settings.xml 不含该字段，按默认 `true` 加载）。

**未覆盖场景的针对性静态审查结论**（取消路径在代码上可达，但未取得实机结果）：

- Esc 观察者 `OnEditorSwitchToEditModePostfix` 接受 `InitializationHold` 与 `Capturing`，且 `_ownsPlayback` 在 `TrySelectFloor0`（TryStart 第 1 步）即置 true，早于 observer 注册 —— 因此 **pre-entry 取消与 End Tail 取消在代码上可达**，只是 Log-only 导出极快、难以稳定命中时机。
- 取消路径本身**不含任何模式相关分支**（Esc 请求路径与 `RestoreAll` 均无 `_imageOutputEnabled`），因此"取消后切换 PNG"不共享任何模式状态；下一 session 的模式仍由 `Settings` 在 `TryStart` 重新冻结，且 residual gate 在创建 session 目录前 fail-closed。
- 这三项**不得记为实机通过**：pre-entry 阶段取消、End Tail 阶段取消、取消后切换 PNG。
- **补充（`0.3.7.0` 轮次新增，注意模式不可混用）**：`0.3.7.0` 轮次取得了一条**PNG 模式**的 pre-entry / Countdown 阶段取消实机证据（session `152633`，见 §9.1）。它属于 **PNG 模式**，**不能**用于证明本节这些 **Log-only 专属**未覆盖项；Log-only 的 pre-entry 阶段取消、End Tail 阶段取消、取消后切换 PNG **仍然没有实机证据，继续不得记为已通过**。

### 9.5 `0.3.7.0` 第一闭环实机验收清单（**已执行；结果与证据分级见 §9.1**）

本节记录第一闭环**当时**的实机验收步骤；结论与证据分级以 §9.1 为准。第一闭环验收时 supersampling 尚未实现；**其后第二闭环已实现并验收**，见 §9.7.1 / §9.7.3。

验收步骤：

1. 导出前调整游戏窗口宽高比（例如从 16:9 改为明显不同的比例）。
2. 记录三台 Camera 的 `Camera.aspect`（GUI「开发者 Diagnostics」段的「谱面相机 aspect」行，顺序为 `Bgcamstatic / BGcam / camobj`；也可读 Player.log 中 `FrameCaptureDriver: capture source active ... baselineAspect={...}` 一行）。
3. 开启「自定义输出分辨率」并使用 **1:1** 自定义宽高（例如 1080×1080）执行导出。
4. 完成或取消导出。
5. 再次调整窗口宽高比。
6. 检查三台 Camera 是否重新自动跟随窗口（GUI aspect 行应随窗口变化；`ResetAspect()` 生效）。
7. 检查 PNG 实际尺寸与画面构图（尺寸应等于自定义宽高；三层 Camera 不应错位）。

同时观察的日志判据：

- `DeterministicFrameScheduler frozen output geometry: custom-resolution <W>x<H> aspect=...`
- `DeterministicFrameScheduler render environment inventory: colorSpace=...`
- `FrameCaptureDriver: capture source active ... unifiedAspect=<W/H> baselineAspect={Bgcamstatic=...,BGcam=...,camobj=...}`
- cleanup 后**不应**出现 `capture source 释放未完成` / `aspect ownership already relinquished` 之外的异常，且不应有 residual ownership（下一次导出不应被 residual gate 拒绝）。

→ **实测结果**：8 次 session 全部未出现 `cleanup-failed`、`capture source 释放未完成`、residual 或 RT 失败；仅 `152602`（Esc）出现预期的 *native 已先置 null* 的三条 `target ownership already relinquished` 记录；取消后各 session 均正常启动。

另外需要确认的兼容性项：**自定义分辨率关闭（默认）时，输出尺寸与构图应与 `0.3.6.4` 完全一致**（legacy-window 模式本轮新增了 aspect 写入，其数值等于窗口 aspect，预期不改变构图，但需要实机确认）。

→ **已确认**：`0.3.7.0` legacy-window（3072×1920）与历史 PNG 基线同几何比对 MAE **0.45–0.80**，描述为**像素级高度一致**（不写成逐像素完全相同）；新增 aspect 写入未改变 legacy 输出。

### 9.6 `0.3.7.0` 第一闭环的非实机验证结果（**已执行**）

| 验证 | 方式 | 结果 |
| --- | --- | --- |
| 生产代码 harness（几何 / Settings XML / Camera aspect / RT fault injection / metadata） | 生产源文件（`OutputGeometryPolicy.cs` / `FrameCaptureDriver.cs` / `EditorExportSession.cs` / `Settings.cs`）+ 最小 Unity stub，临时控制台 harness（net10.0；net8.0 targeting pack 在本机离线不可还原） | **229 PASS / 0 FAIL**，exit code 0 |
| 静态不变量断言 | 注释/字符串剔除后对生产源计数 | **23 PASS / 0 FAIL** |
| Release Rebuild | `dotnet build -c Release -t:Rebuild` | **0 error**；2 个 `NU1900`（离线无法加载 nuget.org 漏洞数据，环境噪声，非代码/非回归） |
| package + verify | `scripts/package-release.ps1 -Configuration Release -Version 0.3.7.0 -Force` | 成功；verify **PASS 11 checks / 0 failures** |
| 发布包内容 | 独立解包复核 | 仅 3 个顶层文件（`Info.json` / `ADOFAI.Renderist.dll` / `LICENSE`），无目录；包内 DLL 与 `bin\Release` **逐字节一致** |
| `git diff --check` | — | clean（exit 0） |

harness 覆盖的关键证据（全部 PASS）：

- **几何策略参数**：legacy 使用冻结窗口尺寸；自定义 1080×1080 → aspect 精确 `1.0`；宽/高为 0 或负 → 拒绝；超过 `SystemInfo.maxTextureSize` → 拒绝并给出硬件上限；`maxTextureSize` 读不到（0）→ 跳过硬件判定、不发明上限；legacy 模式 `Screen.width == 0` → 拒绝；GUI 文本解析拒绝 `""` / 空白 / `"+12"` / `"-1"` / `"1.5"` / `"1e3"` / `"abc"` / 溢出，接受 `"1920"`（`"12 "` 按 trim 后接受）。
- **Settings XML 兼容**：旧 `Settings.xml`（不含新元素）→ `false` / `1920` / `1080` 默认，且 `EditorImageOutputEnabled` 与 historical safety `36000` 不受影响；显式值往返不变；自定义开启 + persisted 宽 0 → 校验失败且**值未被自动修复**。
- **Camera aspect baseline gate**：三台 baseline 不一致 → fail-closed，且**零 Camera 写入**、capture RT 已销毁、无 residual；aspect 不可读 / `NaN` / `0` / 负数 → 同样 fail-closed 且无任何写入。
- **aspect ownership**：正常 `Stop()` → 三台 `targetTexture` 恢复 + **每台恰好一次** `ResetAspect()`；partial assignment（三台 targetTexture 已写入、aspect 第 2 台 setter 抛异常）→ 统一收敛、无 residual、三台恢复原值；partial + `ResetAspect()` 失败 → **residual 保留**，清除故障后下一次 `Stop()` 收敛；**setter 成功后读回失败 → ownership 未丢失**（三台仍被 `ResetAspect()`）；外部改写的 aspect → **不调用** `ResetAspect()`、不覆盖该值，其余两台正常恢复；**baseline 数值恰好等于目标 aspect 时，未真正写入的 Camera 不会被 `ResetAspect()`**（断言精确为 `1/0/0`，这正是“不覆盖外部状态”的关键判别项）。
- **RT fault injection**：`Create()` 抛异常 / `IsCreated() == false` / `Release()` 抛异常 / `Destroy(rt)` 抛异常 → 均保留可重试 ownership，清除故障后 `Stop()` 收敛；正常路径结束后**无 RenderTexture 泄漏**（成功创建但未销毁 = 0）；`Stop()` 幂等。
- **metadata**：新增 **19 个键**齐全、**无重复键**、严格可解析（`System.Text.Json`）、`mode` 取值分别为 `editor-export-png-sequence` / `editor-export-log-only`、`phase` 精确等于 `Phase 3.7.0 Custom Resolution & Supersampling`、inventory 缺失时序列化为 JSON `null`（不是空字符串）。
  - **键数修正（依据实际 schema 差集）**：`0.3.6.x` metadata 为 **39 键**，`0.3.7.0` metadata 为 **58 键**，本轮新增 **19 键**（不是此前的 22 键）；差集内容与 §8.2 自列字段完全一致 —— 输出几何 7 键（`outputGeometryMode` / `geometryCustomResolutionEnabled` / `geometryConfiguredWidth` / `geometryConfiguredHeight` / `outputWidth` / `outputHeight` / `outputAspect`）+ 运行环境 inventory 12 键（`colorSpace` / `graphicsDeviceType` / `graphicsDeviceName` / `graphicsDeviceVersion` / `graphicsShaderLevel` / `maxTextureSize` / `supportsComputeShaders` / `systemMemorySizeMb` / `renderTextureFormat` / `renderTextureGraphicsFormat` / `renderTextureAntiAliasing` / `renderTextureUseMipMap`），且**无任何旧键被删除**。此处只保留修正后的数字，不保留旧的 22 键说法。

静态不变量要点（23 PASS / 0 FAIL）：`CommitFrame` 唯一调用点；`_outputFrameIndex++` 唯一；`new WaitForEndOfFrame()` 唯一且不按模式分支；`Observe()` 仅 1 处；`Graphics.Blit` **0 处**；`new RenderTexture(...)` 全仓库仅 **1 处**（ARGB32 / depth 24 / MSAA 1 / 无 mipmap 基线保持）；`FrameCaptureDriver` **0 处 Screen 读取**；`camera.aspect =` 仅 3 处（每台一次）；`ResetAspect()` 仅 1 处调用点；scheduler 全程 **0 处读取 `ModEntry.Settings`**（模式与几何都在 `TryStart` 冻结）。

**harness 与其它临时验证资产在结论记录后已删除**（`temp/` 为 gitignored）；上表结果是删除前实测所得。

### 9.7 `0.3.7.0` 第二闭环 Supersampling 的验证结果（非实机 harness **已执行**；实机验收见 §9.7.3）

> 本节保留**当时**的非实机验证证据；不能用 harness 代替 Unity 实机。第二闭环**后来已取得实机验收结果**，见 §9.7.1 / §9.7.3，未覆盖边界以 §9.7.3 为准。

| 验证 | 方式 | 结果 |
| --- | --- | --- |
| 生产源码 harness（几何 / 规划器 / Settings XML / RT 与链 fault injection / Blit / GPU 状态 / 帧事务 / metadata / 静态不变量） | 生产源文件（`OutputGeometryPolicy.cs`、`FrameCaptureDriver.cs`、`EditorExportSession.cs`、`Settings.cs`、`Log.cs`、`EditorExportState.cs`）+ 最小 Unity stub（含 `RenderTextureDescriptor` / `Graphics.Blit` / `GL.sRGBWrite` / `GraphicsFormatUtility` 与可注入故障）+ 真实 `XmlSerializer`，临时控制台 harness（net10.0；net8.0 targeting pack 在本机离线不可还原） | **5083 PASS / 0 FAIL**，exit code 0 |
| Release Rebuild | `dotnet build -c Release -t:Rebuild` | **0 error**；2 个 `NU1900`（离线无法加载 nuget.org 漏洞数据，既有环境噪声，非代码/非回归） |
| package + verify | `scripts/package-release.ps1 -Configuration Release -Version 0.3.7.0 -Force` + 独立 `verify-release-package.ps1` | 两次均 **PASS 11 checks / 0 failures**；独立解包确认包内仅 3 个顶层文件、无目录，包内 DLL 与 `bin\Release` 逐字节一致 |
| `git diff --check` | — | clean（exit 0） |

harness 覆盖的关键证据（全部 PASS，均针对生产源文件）：

- **规划器**：`1→空`、`2→[1]`、`3→[2,1]`、`4→[2,1]`、`5→[3,2,1]`、`9→[5,3,2,1]`；对 `scale = 2..200` 逐步验证「严格递减 + 比例 ≤ 2:1 + 末级精确等于 output」；对 6 组**奇数输出尺寸**（含 `1×1`、`1×7`、`999×1`、`1919×1079`）验证每级 `W·H` 交叉相乘精确保持宽高比。
- **checked 乘法与硬件门**：`16384 × 200000` → `geometry-supersampling-scale-overflow`；`render 尺寸 > maxTextureSize` → `geometry-render-width-exceeds-hardware-max`；`maxTextureSize = 0`（读不到）→ 跳过判定、不发明上限；scale=1 时 output 越界仍返回第一闭环的 `geometry-width-exceeds-hardware-max`。
- **scale 合法集合**：`0` / 负数 / 空白 / `"+2"` / `"1.5"` / `"1e3"` / `"abc"` / int 溢出均拒绝；`" 3 "` 按 trim 接受（与宽高同规则）。scale 在**自定义分辨率关闭时同样参与校验**。
- **Settings XML 兼容**：旧 `Settings.xml`（不含新元素）→ `EditorSupersamplingScale = 1`，且 `EditorImageOutputEnabled` / safety `36000` / VerboseLogging 不受影响；显式值往返不变；persisted `0` / `-3` **保持原值不被 sanitize** 且 fail-closed。
- **RT 与链 fault injection**：Source `ctor` 抛异常 / `Create` 抛异常 / `IsCreated()==false` / `Release` 抛异常 / `Destroy` 抛异常 / **descriptor 读取抛异常** / 第 k 级 `Create` 抛异常 → 全部 fail-closed 且**保留可重试 ownership**；清除故障后 `Stop()` 收敛；正常路径 `created == destroyed`、无泄漏、`Stop()` 幂等。
- **GPU 状态**：`RenderTexture.active` 恢复失败 → residual 保留 + 帧失败 + 不 encode，`Stop()` 重试后收敛；`GL.sRGBWrite` 恢复失败同理；**保存失败且未修改状态时不产生 residual**；Gamma 与 scale=1 **完全不读写 `GL.sRGBWrite`**；Linear 下按 destination sRGB 语义设置；**`Apply` 严格发生在 GPU 状态恢复之后**。
- **帧事务**：scale=2 恰好 1 次 Blit、`ReadPixels` 矩形 = output 尺寸、`Texture2D` = output 尺寸；scale=1 **0 次 Blit**、0 次 sRGBWrite；log-only **0 次 Blit / ReadPixels / Texture2D / encode** 且 `imageWritten=false` / `filePath=null`；Blit 中途抛异常 → 帧失败、无 encode、GPU 状态仍恢复。
- **Camera aspect ownership 回归**：三台 `ResetAspect()` 各一次；外部改写的 aspect **不被覆盖**；aspect 不可读 / 三台不一致 → fail-closed 且**零 Camera 写入**；`ResetAspect` 抛异常 → residual 保留、重试收敛。
- **metadata**：新键齐全、**无重复键**、`System.Text.Json` 严格可解析；`mode` 取值不变；scale=1 时 `captureWidth == renderWidth == outputWidth`，scale=3 时 `outputWidth=1080` / `renderWidth=3240` / `captureWidth=3240`。
- **静态不变量（注释与字符串剔除后计数）**：`Graphics.Blit(` **1 处**调用点；`new WaitForEndOfFrame()` **1 处**；`Observe()` **1 处**；`camera.ResetAspect(` **1 处**；`camera.aspect =` **3 处**；`new RenderTexture(` **2 处**（Source ctor + descriptor 链 ctor）；`FrameCaptureDriver` **0 处** `Screen.` 读取；`GetTemporary` **0 处**；scheduler `CommitFrame(...)` 调用 **1 处**、`CommitFrame` 定义 **1 处**、`_outputFrameIndex++` **1 处**、scheduler `0 处` Blit / EOF wait；`Screen.width/height` 仅在 `OutputGeometryPolicy` 各 1 处。

harness 发现并已修复的实现缺陷（**1 项**）：

- `TryCreateDownsampleChain` 中 `_captureTarget.descriptor` 的读取原本位于 try/catch **之外**，descriptor 读取抛异常会**逃逸**出 `TryActivateCameraSource`（而非 fail-closed）。已改为返回 `downsample-chain-descriptor-unavailable:<msg>`，由既有 ownership 收敛路径处理（提交 `7efcacd`）。这正是 harness 的价值所在：该路径在纯静态审查中未暴露。

#### 9.7.1 首轮实机验收（6 个 session）与随之的勘误（`ce34ad4`）

首轮第二闭环实机有 **6 个 session**（`editor_20260923_0852xx/0853xx/0854xx/0855xx`），受测构建为 `7efcacd`（DLL `E16954C57718…`）。**产物侧统计已复核无误**，但当时的**分析报告有三处表述错误**，此处以实测更正，避免后续 agent 沿用：

1. **PNG 总数 = 961，不是 879**。逐文件重新读取 IHDR 确认：254 + 254 + 254 + 123 + 76 = **961**，且 **961 张全部经过尺寸扫描**（`085216/085258/085334/085430/085451` 五个目录），全部严格为 1080×1080、编号连续；`metadata` 之和、日志 `wrote` 之和、磁盘文件数**三方均为 961**。此前报告中的 "879" 属算术/笔误。
2. **`frozen supersampling` 日志为 5 条，不是 6 条**；且 **scale=1 的 session 确实没有该行**。代码上该行由 `if (_supersamplingScale > 1)` 门控（`DeterministicFrameScheduler`），5 条分别对应 scale=2 / 3 / 4 / 4 / **4(log-only)** 这 5 个 session。此前报告「6 条」与「scale=1 无该行」并存，前者错误、后者正确。
3. **跨构建（第一闭环 ↔ 第二闭环）scale=1 比较不得称为「严格逐帧等价」**。两批次的 pre-entry 边界与 partial timestep 不同（`B=57`/`partialFraction=0.64` vs `B=60`/`partialFraction=0.1`），`completionFrameIndex` 因此为 238 vs 241。偏移扫描（`MAE(2nd-loop f(N+off), 1st-loop f(N))`）显示**每帧最优 off 会漂移**：`N=0 → off=1`、`N=20/30/40 → off=2`（0.722/0.728/0.742）、`N≥60 → off=3`（0.543–0.619）。因此正确表述是「**在最佳对齐下内容一致、差异与同一 run 内跨 scale 同量级**」，而**不是**「off=3 是唯一最小值」或严格逐帧等价。
4. **首轮画质观感曾被误标 `USER PASS`**：用户当时**未**对画质作出确认；后续仅有 §9.7.3 中五帧抽样目视记录，不能推及全序列。像素统计证据（几何亚像素一致、颜色 ≤0.33% 且无系统性漂移、`stepRetained≈1.00` 即边缘对比度零流失、硬边像素 −43%…−98.7%）**保留**，但「观感是否可接受 / 是否更好」当前状态是 **待用户目视确认**。同时不得宣称「所有倍率都普遍优于较低倍率」：硬边减少幅度**非单调**（frame 40：scale2=26、scale3=8、scale4=23；frame 100：126/140/194），该现象**尚未解释**。
5. **`RenderTexture.active` 的实机结论只能是**：「正常帧事务与 cleanup 中**未观察到恢复失败**」（`gpu-state` / `capture-failed` / `cleanup-failed` 均 0，且 961 次写盘与 commit 一一对应）。成功的恢复**不写日志**，因此**不能**把「无错误日志」说成「每一帧的状态值都已独立读回验证」。**Linear 路径**（`GL.sRGBWrite` 按 destination 设置）在本轮 Gamma 环境下**完全未实机覆盖**，继续记为未验证。

#### 9.7.2 `downsampleLevelCount` 实际级数语义修正（`ce34ad4`）的非实机验证

| 验证 | 结果 |
| --- | --- |
| 生产源 + Unity stub harness（net10.0，临时） | **139 PASS / 0 FAIL**，exit 0 |

覆盖：Driver 实际级数 —— scale=1 PNG `0`、scale=2 PNG `1`、scale=3 PNG `2`、scale=4 PNG `2`、scale=4/2 log-only `0`，且 `Stop()` 后 live 计数回到 0；activation 失败（链级 `Create` 抛异常 / descriptor 读取抛异常 / Source ctor 抛异常）均 fail-closed 且**不声称任何级数**、无 residual；metadata 严格可解析、**无重复键**、5 种场景的级数取值正确、**既有键全部保留**；静态不变量 —— activation 成功路径上**恰好 2 处**快照（均紧邻 `_captureWidth` 成功块）、每 run **1 处**归零、accessor 返回实际值、controller **不再**预写计划值、driver 只统计非 null 槽位；既有不变量 —— `Graphics.Blit(` 1 处、`new WaitForEndOfFrame()` 1 处、`camera.ResetAspect(` 1 处、`new RenderTexture(` 2 处、`GetTemporary` 0 处、driver `Screen.` 0 处、`CommitFrame` 1 处调用 + 1 处定义、`_outputFrameIndex++` 1 处、residual gate 仍覆盖链与 GPU 状态。
该 harness **未**改变 PNG/Log-only 逻辑计数、RT ownership、Camera aspect、Blit 与色彩算法（diff 仅 5 文件 +59/−11）。

**harness 与其它临时验证资产在结论记录后已删除**（`temp/ss-harness/`、`temp/md-harness/`，`temp/` 为 gitignored）；上表结果是删除前实测所得。

**仍未验证（该清单在 §9.7.3 已更新）**：本节原先列出的「Blit 真实 GPU 行为 / 色彩正确性 / scale=1 实机回归」已由后续实机轮次覆盖，结论见 §9.7.1 与 **§9.7.3**；当前仍然未覆盖的项以 §9.7.3 的「未覆盖边界」为准（**Linear 色彩空间**、**极端资源失败**、逐帧 GPU 状态读回、跨会话 residual gate、画质全序列目视）。

#### 9.7.3 第二闭环最终实机验收（scale=4 完整导出首次通过）

| 项 | 实测 |
| --- | --- |
| Session | **`editor_20260923_093423`**（2026-09-23 09:34:24–09:34:38） |
| 受测构建 | `ce34ad4` 构建，DLL SHA256 `E6747B520FD0C2844238901361251CD127B675041222A0BB1F6A0E8782D6E6E1`，`ProductVersion=0.3.7.0+ce34ad4…` |
| 模式 / 几何 | PNG（`editor-export-png-sequence`，`imageOutputEnabled=true`）；自定义 1080×1080；**scale=4** |
| Source / Capture RT | **4320×4320**（`renderWidth/Height` 与 `captureWidth/Height` 均为 4320） |
| 实际 Downsample RT | **2 级**（`downsampleLevelCount=2`；链首级 `downsampleRenderTextureFormat=ARGB32` / `R8G8B8A8_UNorm`，与 Source 逐字段一致） |
| 逻辑帧 | **254**（`frameTransactionRequestCount = logicalFrameCount = captureRequestCount = writtenPngFrameCount = capturedFrameCount = 254`） |
| PNG | **254 张，全部实测 1080×1080**，编号 `frame_000000…000253` **连续无缺号**；磁盘 / 日志 `wrote` / metadata **三方均为 254** |
| completion | `completionFrameIndex=241`，`tailFramesCommitted=12`，`tailFramesCaptured=12`，`resolvedTailFrames=12` |
| 终态 | `state=Completed`，`stopReason=canonical-completion-tail-drained`，`terminationKind=canonical-completion` |
| 时间线签名 | `B=60`、`previousBoundaryTime=1.796667`、`canonicalStart=1.8`、`partialFraction=0.1`、`outputFps=30`、`completionBpm=100`、`pitch=1`、End Tail 12 Frames —— 与既有 BPM=100 基准组一致 |
| 失败面 | **无已观察到的**捕获失败 / `cleanup-failed` / residual / GPU 状态异常 / watchdog / safety-limit；`restored captureFramerate` 1 次；唯一 `WaitForEndOfFrame` 入口与唯一 `CommitFrame` 提交点属于**源码结构**结论，并非各自在实机仅执行一次；全文仅 2 条既有无关 Exception（Discord 初始化、`Mods detected! Disabling exception capturing`） |
| 跨 scale 一致性（像素） | 该 session 与同时间线的 `085216`(scale1)/`085258`(scale2)/`085334`(scale3) 同帧直接比对 MAE：vs scale1 **0.550–0.850**、vs scale2 **0.485–0.699**、**vs scale3 0.354–0.573（最近）** ⇒ 亚像素级一致、无构图偏移/拉伸 |
| 颜色（像素） | 全帧通道均值 Δ(scale4 − scale1) = **(−0.171, −0.131, −0.124)**，三通道同向等量（≤0.75%），**无偏色/色相漂移**；轻微压暗的**具体因果归因未经验证**，不得写成既定事实 |
| 画质观感 | 用户确认**完整导出已完成**；**网页版 GPT 仅对 `frame_000058`–`000062` 五张抽样**目视未见明显拉伸 / 过度模糊 / 亮边 / 暗边 / 渗色 ⇒ **只覆盖该 5 帧抽样，不得推广为全序列逐帧目视通过**，也不得据此宣称「高倍率普遍优于低倍率」 |

**未覆盖边界（继续不得记为通过）**：

1. **Linear 色彩空间**：历次实机均为 **Gamma / Direct3D11**；`GL.sRGBWrite` 按 destination 设置的 Linear 分支**从未实机运行**。Gamma 结论不得外推。
2. **极端资源失败**：本轮最大 render 为 4320×4320，**远低于** `maxTextureSize=16384`；接近硬件上限的 render 尺寸、极大 scale 的显存分配失败及失败时的 fail-closed / residual 表现**均未实机测**（harness 只覆盖 stub 语义，不构成 GPU 实机证据）。
3. **GPU 状态逐帧读回**：`RenderTexture.active` / `GL.sRGBWrite` 的**成功恢复不写日志**，实机只能得到「未观察到恢复失败」这一侧结论；**不得**把无错误日志写成每帧状态值均已独立验证。
4. **跨会话 residual gate**：`editor_20260923_093423` 所在 run **只有一个 session**，未取得新增证据；该结论仍沿用第一闭环轮次的记录（三次取消后下一 session 均正常启动）。
5. **画质全序列目视**：中段 gameplay、completion 与 End Tail 区间**未目视**。

#### 9.7.4 算法命名与等价性（durable）

实现名称为 **`multi-stage-bilinear`**（逐级 bilinear 采样）。**只有相邻两级比例恰为 2:1 时**，该级 bilinear 采样才近似 2×2 box 平均；`3W→2W`（1.5:1，出现在 scale=3/5/6 的首级）**不是** box 平均。因此**不得**把本算法普遍等价为 box filtering / box downsample。Gamma 空间对伽马编码值求平均是该实现的已知性质，**不是**对 box filter 的等价声明。

---

## 10. 当前 ADOFAI 内部关键事实（Assembly-CSharp 0.4.3.0）

### 10.1 scrCamera / targetTexture

- `scrCamera.instance` 是 static property。
- `Bgcamstatic` / `BGcam` / `camobj` 是 public Camera fields。
- `SetupRTCam(bool)` 是游戏内写这三台 targetTexture 的关键入口：true → 自有 camRT；false → null，并同步 Overlaycam/quad。
- 调用点包含 `scrCamera.Awake/Update`、`scnGame.Play`、`scnEditor.Start`、`scnEditor.SwitchToEditMode`；`scnEditor.Play` 本身不直接调用它。
- `scnEditor.Start` / `SwitchToEditMode` 结尾会 `SetupRTCam(false)`。
- AdofaiTweaks 已排除对该 targetTexture 链的直接引用干扰（此前基线扫描为 0）。
- **`Camera.aspect` / `ResetAspect`（0.3.7.0 本轮新验证）**：对当前 `Assembly-CSharp.dll`（FileVersion `0.4.3.0`）做字节级扫描，全程序集**不存在** `set_aspect`、`ResetAspect`，也不存在 UTF-16 的 `"aspect"` 字符串字面量（因此也不存在以字符串反射写 aspect 的路径）；仅有一处小写 `get_aspect`（读取；未进一步解析其声明类型）。
  - ⇒ 结论：当前 ADOFAI 基线的编译代码**不写** `Camera.aspect`、也**不调用** `ResetAspect`。因此 session 内不存在 native 主动改写 aspect 的已知路径，Renderist 可以安全地在 session 内拥有 aspect，并在 cleanup 用 `ResetAspect()` 恢复 Unity 自动行为。
  - 有限的旁证：`UnityEngine.CoreModule.dll` 确实提供 `get_aspect` / `set_aspect` / `ResetAspect`（即 Unity 侧 API 可用）。
  - **仍未验证**：Unity 在“Camera 有 `targetTexture`”时自动 aspect 的取值来源（屏幕还是 RT），以及 `Camera.aspect` getter 在自动模式下是否恒为非 0。该行为需要实机确认（见 §9.5 / §11）。

### 10.2 scnEditor 生命周期 / input

- `SwitchToEditMode` exact signature：`void SwitchToEditMode(bool)`；当前程序集调用点是 Start 与 Update Esc 分支。
- `scnEditor.playMode` 是派生属性，编辑器内本质与 `controller.paused` 互补，不能单独当作稳定 lifecycle authority。
- `inStrictlyEditingMode` 只写不读，不能作为 playback stop 判据。
- Esc 分支直接 `SwitchToEditMode(false); return;`，不经过 `scrController.TogglePauseGame`。
- `scnEditor.Play()` 会 `playerManager.UnlockAllPlayerInput()`，所以 Input Guard 必须在 Play 前安装。

### 10.3 Canonical completion

- `scrController.OnLandOnPortal(scrPlanet, Portal, string)` 是当前正常完成入口，最终驱动 state → Won。
- `Won_Enter/Won_Update` 是后续胜利态处理。
- `scrPlayer.Hit(bool)` 只处理输入/floor/视觉 progression，不等于 canonical completion。
- `BeatLevel` 不是当前正常完成 authority。

### 10.4 `controller.paused`

直接 `set_paused` 的关键写入者：

- `scrController.Awake`: `paused = ADOBase.isLevelEditor` → editor idle 为 true。
- `scnGame.Play`: `paused=false`；`scnEditor.Play()` 会进入该路径。
- `scrController.TogglePauseGame`: pause/resume；session 期间被 Input Guard 抑制。
- 另有与本阶段无关的 RedeemCode / MobileMenu 路径。

返回 edit mode 的 `SwitchToEditMode → ResetScene → TogglePauseGame` 会重新 pause。因此 paused 的正确 gate 在 playback readiness，而不是 Start 前。

### 10.5 Native pre-entry / count-in 时间 authority（当前 DLL 与正式实现基线）

- **已实施 pre-entry 边界模型**：`step = pitch / OutputFps`；`B = ceil((canonicalStart - anchor) / step)`（SnapNearInteger + ULP-only grid bracket）；`[0,B-1]` 为 pre-entry、`G=B` 为 gameplay frame 0。hidden phase 保存并限域冻结 `timeScale=0`，以 `Countdown_Update` scoped beat override 过渡；G 使用 `canonicalStart - previousBoundaryTime` 的 partial scaled timestep，**G 成功 Commit 后恢复原始 timeScale**，G+1 使用完整 timestep。B/G 数值只对具体谱面和设置成立（验收见 §9.1）。
- `scrConductor.Update` 每帧以 `AudioSettings.dspTime` 更新 `dspTime`，再按 `((dspTime - dspTimeSong - calibration_i) * song.pitch) - addoffset` 写入 `songposition_minusi`；它不是 output-frame clock。`beatNumber` 每次最多跨一个 beat；Countdown 边界条件 `beatNumber >= adjustedCountdownTicks` 使用 **1-based** 计数，因此 native 边界等同于地图时间 `floor0EntryTime + (adjustedCountdownTicks - 1) × crotchetAtStart × pitch`（实机与 `canonicalStart` 一致）。
- 原生 Countdown 的原始 authority 是 **DSP / 实时时间**：raw DSP 驱动的早期实机中相邻 output frame 的 songposition 前进约 `0.11–0.12 s`（而非 `1/30 s`），chosen planet angle 曾约 `0.899 rad`/frame（轨迹呈 chord / polygon）。这是 `Time.captureFramerate` 单独不足、必须由 Renderist 接管 chart time 的原因。
- 当前正式实现由 `EditorVisualClock` 对 `songposition_minusi` 的 getter/setter patch 提供 forced chart time，`scrPlanet.Update_RefreshAngles` 直接消费该值：接管后相邻 committed output frame 的 chosenPlanetAngle 增量恒为 `π × (BPM / 60) × pitch / OutputFps`（30 FPS / 140 BPM / pitch 1 时为 `0.244346 rad`），planet motion 与 trail aging 由同一个 output-frame clock 驱动。
- IL 已确认（`Assembly-CSharp` 0.4.3.0）：`scrController.Countdown_Update` 是 `void Countdown_Update()`（无参，IL 约 123 字节），阈值比较与 `ChangeState` **都在方法体内**（`ldfld scrConductor.beatNumber` → `callvirt get_adjustedCountdownTicks` → `callvirt ChangeState`）；状态分发入口 `scrController.Update` 不引用这三者，因此不存在“调用方先判定再调用”的形态——scoped Prefix/Postfix 可直接门控这次 transition。`scrConductor.beatNumber` 是 **Int32 字段**，`adjustedCountdownTicks` 是**只读 float 属性**，故注入只能写该字段，最小满足值为 `ceil(adjustedCountdownTicks)`。`scrPlanet.Update` 的 IL 调用 `Update_RefreshAngles`。
- 状态机链与跨 turn 行为：`StateEngine.Update` → 当前 state 的 `Update` delegate → `Countdown_Update` → `ChangeState(PlayerControl)` → `ChangeToNewStateRoutine`；Countdown 无自定义 Exit，先 `yield return StartCoroutine(DoNothingCoroutine())`，随后设置 current state 并同步执行 void `PlayerControl_Enter`。该路径没有 `WaitForSeconds` / `WaitForEndOfFrame` / `WaitForSecondsRealtime`，也不读 `deltaTime` / `timeScale`；实机确认 `timeScale = 0` 时仍可跨 scheduler turn 进入 PlayerControl。forced clock 生效时 `beatNumber` 由被强制的 `songposition_minusi` 驱动、而状态机读到的是**上一帧**写入的 beat，因此 `Countdown → PlayerControl` 比 forced clock 跨过边界晚 1 个 Unity frame——这正是需要 partial-timestep 边界处理与独立 lifecycle bridge 的原因。
- Trail / stateful history：`TrailRenderer` point lifetime 以秒计，但 Unity 6 API **没有**公开它使用 scaled 还是 unscaled clock，故不得声称已静态确认其内部实现。实机因果证据：hidden phase 让 native 世界消费过 future chart time 时，frame G 出现 future Trail blob / kink；改为「hidden phase `timeScale = 0` + G partial timestep」后该现象消失（数值与验收见 §9.1）。
- 遗留风险：`songposition_minusi` 的存储字段是否被 forced 值写回，取决于 conductor 的写回是否经过被 patch 的 setter（同一进程先后两次实机 session 分别观测到「始终为 raw DSP 值」与「从第 1 帧起为上一帧 forced 值」）。因此 `ReadUnforcedSongPositionValue()` / `backingSongposition` 不是稳定的 native 证据，不得作为 handoff anchor 判据；所有 reader 走 getter，forced 值仍是权威视觉时间。
- `scrCountdown` 文本 / count-in SFX 仍直接依赖 DSP schedule；audio 不应被 Renderist 篡改。其与受控 visual clock 的一致性、以及 `lifecycleSongPosition` 的非权威差异仍待调查，不得仅凭静态结论宣称完整通过。
---

## 11. 未解决问题 / 风险 / 下一步

> 此处只列**当前未收敛事项**；已完成的 End Tail、pre-entry、Log-only、Custom Resolution 和 Supersampling 不再重复充当待办。历史验收数据见 §9，实施不变量见 §3–§8，当前 ADOFAI 内部事实见 §10。

1. **0.3.8.0 视频导出（L1 完成并实机验收；L2 已实现；L3 未开始）**：`0.3.7.1` → **`0.3.8.0`** 已执行，Phase 仍为 `Phase 3.8.0 FFmpeg Video Export Pipeline — L1 Component Management`（§12 / §12.2）—— **L1 完成，L2 已在本轮实现（§2.8），L3 未实施**。L1 完成通知丢失缺陷与"重复完成通知误删在用归档"ownership 缺陷均已修复（§2.6 / §2.7）。目标游戏中固定资产下载、SHA 校验、安全安装、真实能力探测、GUI Ready、重启后重新发现、空闲状态禁用/重新启用，以及存在 Ready 组件时的 Log-only / PNG 导出**已有实机证据**（绑定 `F8B3553F` / `+bea143d` 及后续 `1D70BE45` / `+02c7fb8`，见 §2.7）。**仍未验收**：活动下载期间禁用/卸载、异常网络（证书/断线）、旧通知与新请求真实交错。**`0.3.8.0` 正式构建（`AE2326D3…`）已通过用户最小实机验收**（§2.7），故 **L1 主路径具备阶段性基线证据**；上述三项边界不影响该基线，但**不得记为已通过**。**L2 已实现并通过独立 net48 回归（128/0/0，含真实 Gyan 9.0.2 fixture），但仍未接入 Unity**；**L3（MP4 帧事务 / Finalizing 关口）未实施** —— 不得因 L2 实现就宣称 MP4 导出可用。Event-Driven A+ 决策见 §2.1；接入 L3 前仍需 §2.2 的视觉确定性补测结论，以及 Mono 下的进程与异步 IO 行为验证。L1 Ready **不是** MP4 运行时验收，L2 源码回归同样**不是**实机验收。
2. **Linear 与极端 GPU 资源失败**：Supersampling 已在 Gamma / Direct3D11 的所述范围实机通过，**Linear 色彩空间从未实机覆盖**；接近硬件极限的 RT 分配与 GPU 状态恢复故障仍只有 stub / 静态证据。详见 §9.7.3。
3. **真实 Unity 故障注入**：host Destroy、RT Release/Destroy、partial Camera assignment 的失败与重试，已有生产源码 + Unity stub 的确定性测试，**未在真实 Unity Player 注入这些异常**；正常路径与跨调用 residual 的既有实机结果不能替代异常证据（§3.5、§9.3）。
4. **其余边界测试**：显式 safety frame-limit runtime trigger 与 `TryPrepareHitState` 故障注入主要依赖静态 / 纯计算证据；需要扩大 BPM change、Twirl、Midspin、event-heavy、特殊 startup、长谱面覆盖。无须为此设置人为帧数或时长上限。
5. **Log-only 取消覆盖**：pre-entry 阶段取消、End Tail 阶段取消、取消后切换 PNG 尚无**该模式**实机证据（§9.4）。`0.3.7.0` 的 PNG pre-entry 取消（session `152633`）不能代替 Log-only 验收。
6. **Camera aspect 边界**：当前受测环境的三台 baseline 均一致（1.6）；Unity 自动 aspect 对屏幕 / camRT 的内部来源未确认。若游戏合法出现三台 Camera 不同 aspect，现有 baseline-incompatible gate 可能误拒绝；不得将该理论风险记成已经发生（§9.1、§10.1）。
7. **原生 lifecycle / 音频遗留**：`lifecycleSongPosition` 与 raw DSP 非视觉 authority；count-in 文本/SFX 和未来音频同步仍待独立调查（§10.5）。曾见 `Coroutine couldn't be started ... Conductor is inactive` 的 native Esc teardown 警告，尚未做 disable-mod A/B。
8. **工具债务**：`set-version.ps1 -Phase` 不同步全部 phase 文案，后续升级必须人工核查 `EditorExportSession.PhaseLabel` 与相关注释（§2）；不阻塞当前稳定基线。

---

## 12. 发布与部署

- 当前产品版本为 **`0.3.8.0`**，Phase 为 **`Phase 3.8.0 FFmpeg Video Export Pipeline — L1 Component Management`**（前三位 `0.3.7` → `0.3.8` 与 Phase 文案**均由用户明确批准**；**L2 实现轮按用户要求未改版本号、也未改 Phase 文案**）。`0.3.8.0` 开启 FFmpeg 视频导出管线阶段：**L1（FFmpeg 组件管理 + HTTPS 下载）已完成并通过用户最小实机验收**，**L2（FFmpeg Video Process Pipeline）已实现并通过独立 net48 回归但尚未接入 Unity（§2.8）**，**L3（Unity MP4 Frame Transactions）尚未实施 —— 因此不得宣称 MP4 导出已经可用。** `0.3.8.0` 现有**两份不同的构建身份**（L1 实机验收版 §12.2 / L2 实现轮工作区版 §12.3），引用时必须同时给出 DLL SHA256 或 `+hash`。 其下 `0.3.7.1` = Custom Resolution + Supersampling 双闭环稳定性收敛版（**上一稳定基线**，Phase 为 `Phase 3.7.0 Custom Resolution & Supersampling`），`0.3.7.0` = 双闭环功能版，`0.3.6.4` = Log-only Frame Transactions。
- 本轮（0.3.7.0 第一闭环）相对基线 `70df55b` 的改动：新增 `OutputGeometryPolicy.cs`、`RenderEnvironmentInventory.cs`；修改 `Settings.cs`、`UiText.cs`、`ModEntry.cs`、`EditorExportReadiness.cs`、`EditorExportPreflight.cs`、`EditorExportController.cs`、`EditorExportSession.cs`、`DeterministicFrameScheduler.cs`、`FrameCaptureDriver.cs`；版本点 `mod/Info.json`、csproj `<Version>`、`ModEntry.ModVersion` 与 `ModEntry` 启动日志，外加**人工同步**的 `EditorExportSession.PhaseLabel` 与类注释 phase 文案（`set-version.ps1` 的已知范围限制，见 §2）。
- 本轮**未**新增 Harmony Patch、未新增 ADOFAI 内部 API 依赖、未修改 README。
- 发布包：`Info.json` + `ADOFAI.Renderist.dll` + `LICENSE`；`dist/` ignored。本轮以 `scripts/package-release.ps1 -Configuration Release -Version 0.3.7.0 -Force` 打包，并在发布提交 `05a3b4a` 之后重新 Release Rebuild 并以 `-SkipBuild` 重新打包，产出 `dist/ADOFAI.Renderist.zip`（zip SHA256 `B251B6A4631401E44F96130E152FB834B70B47CE6E75CA45304DC43380A4155F`，sidecar `dist/ADOFAI.Renderist.zip.sha256` 同值）；`verify-release-package.ps1` 结果 **PASS 11 checks / 0 failures**，独立解包复核确认包内仅有 3 个顶层文件、无目录，且包内 DLL 与 `bin\Release` 逐字节一致。
- 发布包内 DLL 的 `ProductVersion` 形如 `<version>+<构建时 HEAD 的完整提交哈希>`：该 `+hash` 是 SourceLink/InformationalVersion 在构建时记录的 **HEAD 提交**，不是工作区改动。`0.3.7.0` 的最终发布包在发布提交 `05a3b4a` 之后重建，因此 `ProductVersion = 0.3.7.0+05a3b4adda0fb5d9ce89c5ca29af1a6f496d75f3`（`FileVersion = 0.3.7.0`，DLL SHA256 `14D335FD2E2C051DBCE43BF1DB414F8ED177BEB221846EFB4FB50D761DBFBBBA`），即包内构建标识精确指向承载本版本的提交。注意：若在打包后再提交任何改动，`+hash` 不会自动更新；应避免在打包后 `amend` 发布提交（会改变哈希并使包内标识失效）。`verify-release-package.ps1` 比较版本时会剥离 `+hash` 后缀。
- 历史：`0.3.6.1`（Output FPS 无上限、safety 默认 unbounded、long frame chain、autoplay fail-closed、paused 阶段修正）→ `8bceeef` + `1af1205` hardening → `0.3.6.2` → native pre-entry 正式化 + persisted End Tail semantic fix → `0.3.6.3` → Log-only Frame Transactions → `0.3.6.4` → `05a3b4a` Custom Resolution（**第一闭环；已通过实机验收**）→ `2585664` + `7efcacd` Supersampling & Downsampling（**第二闭环；已通过实机验收**，见 §9.7.3）→ `ce34ad4` metadata `downsampleLevelCount` 实际级数语义修正（**未改变任何渲染/计数/ownership 行为**）→ **`0.3.7.1` 稳定性收敛**（仅第四位递增 + 一处算法注释勘误，见下）。
- **`0.3.7.0` 存在三个不同的发布包身份（重要，勿混用）**：版本号始终为 `0.3.7.0`（用户明确要求不递增第四位），`dist/ADOFAI.Renderist.zip` 已被**重建覆盖三次**：
  - **第一闭环（仅 Custom Resolution）**：`05a3b4a` 构建，DLL SHA256 `14D335FD2E2C051DBCE43BF1DB414F8ED177BEB221846EFB4FB50D761DBFBBBA`，`ProductVersion = 0.3.7.0+05a3b4a…`，zip SHA256 `B251B6A4631401E44F96130E152FB834B70B47CE6E75CA45304DC43380A4155F`（**已实机验收的那一份**）。
  - **第二闭环（含 Supersampling，首轮实机受测）**：`7efcacd` 构建，DLL SHA256 `E16954C57718F1424BC067985DB6BAEF490987C72064A0B46D591313A3815404`，`ProductVersion = 0.3.7.0+7efcacd5a0af2f3e736987057f0218e55f21a32c`，zip SHA256 `F667A0C740636D4A6920EF2AEBD1C2C753A1B80A98C17C783A310C30C3C9F466`（已完成首轮 6 session 实机；**metadata `downsampleLevelCount` 为计划级数**的旧语义）。
  - **第二闭环 + metadata 语义修正**：`ce34ad4` 构建，DLL SHA256 `E6747B520FD0C2844238901361251CD127B675041222A0BB1F6A0E8782D6E6E1`，`ProductVersion = 0.3.7.0+ce34ad49ae8d80d791a06887ece55fce31b3efbf`（**scale=4 完整导出实机验收的那一份**，见 §9.7.3）。
  - **`0.3.7.1` 稳定性收敛（当前稳定版本）**：构建身份见本文件末尾「最终发布身份」段（DLL / zip / `ProductVersion +hash` 以该处为准）。
  - 因此**不能**再用「0.3.7.0 的包哈希」唯一指代某个构建；引用时必须同时给出**版本号 + DLL SHA256 或 `ProductVersion` 的 `+hash`**。`verify-release-package.ps1` 比较版本时会剥离 `+hash` 后缀。
  - **受测构建的自我判别**：`ce34ad4` 起，log-only + scale>1 的 session metadata `downsampleLevelCount` 为 **0**；`7efcacd`（及更早）为**计划级数**（如 scale=4 时为 2）。但 **PNG + scale>1 等场景下该字段在新旧构建下取值相同**（例如 PNG+scale=4 两版都是 2），因此该判别法**只适用于 log-only + scale>1**；其余情况须依赖部署时间序或 DLL 哈希。
- 各轮 Release Rebuild / package / verify：**0 error / PASS 11 checks / 0 failures**（细节见 §9.7）。
- 部署使用 `scripts/copy-to-mods.ps1`，只更新 `Mods\ADOFAI.Renderist\`；路径来自本地 ignored `build/local.props`，未配置时不得猜测。目录内的 `ADOFAI.Renderist.dll.<pid>.cache` 是 UMM/Mono 按**进程 id** 命名的运行时缓存（`AdofaiTweaks` 同样存在），脚本默认保留、可用 `-CleanRuntimeCache` 清除；它**不是**被加载的产物（DLL 才是），且历史观测显示每次游戏运行都会重新生成并淘汰旧 pid 的缓存。**部署本身不等于实机验收**：第一闭环见 §9.1，第二闭环（含 scale=4 完整导出）见 §9.7.3；当前仍未覆盖的边界（Linear、极端资源失败等）见 §9.7.3。
- 自动验证链：
  `dotnet build src/ADOFAI.Renderist/ADOFAI.Renderist.csproj -c Release -t:Rebuild`
  → `scripts/package-release.ps1 -Configuration Release -Force`
  → `scripts/verify-release-package.ps1 -ZipPath dist/ADOFAI.Renderist.zip`。

- **版本收敛：`0.3.7.1` → `0.3.8.0`（2026-09-24 执行，用户裁定）**：**此前建议采用 `0.3.7.2` 的方案已由用户明确作废**（正确目标为 `0.3.8.0`；**不得使用 `0.3.7.2`**）。本轮直接开启 `0.3.8.0` 版本线，并采用 Phase `Phase 3.8.0 FFmpeg Video Export Pipeline — L1 Component Management`，使版本号与 Phase **同时**表达「当前开发阶段」与「已完成的功能」：`0.3.8.0` + `Phase 3.8.0` 表示已进入 FFmpeg 视频导出管线阶段，`— L1 Component Management` 子标题表示当时**仅**完成 L1。**该时点 L2 帧流进程与 L3 MP4 帧事务均未实施（L2 现已实现，见 §2.8；L3 仍未实施），因此不得因进入 `0.3.8.0` 而宣称 MP4 导出可用。** 已同步的版本点：`mod/Info.json`、csproj `<Version>`、`ModEntry.ModVersion`、`ModEntry` 启动日志（版本 + Phase）、`EditorExportSession.PhaseLabel`、`ModEntry` 类注释 Phase，以及 `PROJECT_UNDERSTANDING.md`；发布身份见 §12.2。**`0.3.8.0` 正式构建（`AE2326D3…` / `+4d35801`）已部署并通过用户最小实机验收**（§2.7）；此前 `1D70BE45…`（`+02c7fb8`）为收敛前的 L1 源码证据，保留于 §2.7。

### 12.1 最终发布身份（`0.3.7.1` 稳定性收敛）

| 项 | 值 |
| --- | --- |
| 发布源码提交（release source commit） | **`647b1d7188850150cca5709e9b092765fb78888d`**（`chore(release): 0.3.7.1`） |
| 产品版本 / FileVersion | `0.3.7.1` |
| **ProductVersion（含 `+hash`）** | **`0.3.7.1+647b1d7188850150cca5709e9b092765fb78888d`** ⇒ 精确指向上述发布源码提交 |
| DLL SHA256 | **`80B2402BECB83075C1ECDCBED14E4D273D3AE6B6FD0289AB5A88B8DDF1F9E5C9`** |
| 发布包 | `dist/ADOFAI.Renderist.zip`（`dist/` 为 gitignored，不入库） |
| ZIP SHA256 | **`D6B40EBD326F2640C56AA8B18B32F2E5C326CD38C9B9FFBC02A9C3301FEBDE3A`**（sidecar 同值） |
| 包内容 | 仅 3 个顶层文件（`Info.json` / `ADOFAI.Renderist.dll` / `LICENSE`），**0 个嵌套目录，无 banned 内容** |
| 一致性 | 部署 DLL == `bin\Release` == 包内 DLL（三者 SHA256 相同），逐字节一致 |
| 构建/验证 | Release Rebuild **0 error**；package + verify（内置与独立各一次）**PASS 11 checks / 0 failures** |
| 本轮源码改动 | 仅：三个版本点（`Info.json` / csproj `<Version>` / `ModEntry.ModVersion` + 启动日志）+ `OutputGeometryPolicy` 规划器注释勘误；**Phase 未变** |

**关于 `+hash` 与 HEAD 的关系（顺序说明）**：DLL 的 `ProductVersion +hash` 记录**构建时的 HEAD**。由于该 hash 嵌在 DLL 字节里，`DLL SHA256` 只能在提交之后才能算出，因此本仓库采用（与 `0.3.7.0` 相同）的顺序：**先提交发布源码（`647b1d7`）→ 从该提交 Rebuild / package → 再用一个 docs-only 提交记录最终身份**。因此 `+hash` 指向 `647b1d7`（**最终源码提交**），而 HEAD 可能比它多一个 docs-only 提交 —— 这不影响产物身份。若在打包之后又产生任何**源码**改动，必须重新 Rebuild + 重新打包并更新本节。

### 12.2 L1 发布身份（`0.3.8.0` — L1 Component Management；**当前唯一经过实机验收的构建**）

| 项 | 值 |
| --- | --- |
| 发布源码提交（release source commit） | **`4d35801862d0828ea248d9934e8ebe74af11f2bb`**（`chore(release): 0.3.8.0 FFmpeg Video Export Pipeline (L1 Component Management)`） |
| 产品版本 / FileVersion | `0.3.8.0` |
| Phase | `Phase 3.8.0 FFmpeg Video Export Pipeline — L1 Component Management` |
| **ProductVersion（含 `+hash`）** | **`0.3.8.0+4d35801862d0828ea248d9934e8ebe74af11f2bb`** ⇒ 精确指向上述发布源码提交 |
| DLL SHA256 | **`AE2326D3D2B13FFA5B300CD1E3E06241F8A6A5522B5AB3EF1260085721DBC6E9`**（268288 字节） |
| 发布包 | `dist/ADOFAI.Renderist.zip`（`dist/` 为 gitignored，不入库） |
| ZIP SHA256 | **`DB0F5B22CEB12D57241C347BCFDA55BE83E44224C9A0149BC529B85FD0476A3E`**（sidecar 同值） |
| 包内容 | 仅 3 个顶层文件（`Info.json` / `ADOFAI.Renderist.dll` / `LICENSE`），**0 个嵌套目录，无 banned 内容**（独立解包复核） |
| 一致性 | `bin\Release` DLL == 包内 DLL == **游戏 `Mods` 内已部署 DLL**（三者 SHA256 均为 `AE2326D3…`，逐字节一致；部署后已重新读取文件核对）。部署时间 2026-09-24 15:07:02，`Info.json` 同步为 `0.3.8.0`（与 DLL `FileVersion` 一致），`LICENSE` 保留，`Settings.xml`（`F81452BB…`）与托管 FFmpeg（两份 EXE 哈希仍与 manifest 一致）**未改动**，未放置任何额外的可被 UMM 加载的备份 DLL。**并已通过用户最小实机验收**（§2.7）：UMM 显示 `0.3.8.0`、FFmpeg `Ready`、`libx264` / `mp4` / `rawvideo`、禁用并重新启用、无新可见异常。 |
| 构建/验证 | 强制 Release Rebuild **0 error / 0 C# warning**（仅 `NU1900` 离线 NuGet 环境噪声）；package + verify（内置与独立各一次）**PASS 11 checks / 0 failures**；离线回归 **72 passed / 0 failed / 1 skipped**；真实 Gyan 9.0.2 fixture 回归 **73 passed / 0 failed / 0 skipped**；`git diff --check` clean |
| 本轮源码改动 | 版本点（`Info.json` / csproj `<Version>` / `ModEntry.ModVersion` + 启动日志）+ `EditorExportSession.PhaseLabel` + `ModEntry` 类注释 Phase；**无功能性代码改动** |

**顺序（与 §12.1 相同的仓库约定）**：先提交版本收敛源码 → 从该提交**强制 Release Rebuild** + package → 再用一个 **docs-only 提交**把最终 DLL / ZIP 身份填入本表。因此 `+hash` 指向**承载 `0.3.8.0` 版本变更的提交**，HEAD 可能比它多一个 docs-only 提交，这不影响产物身份。**若在打包之后又产生任何源码改动，必须重新 Rebuild + 重新打包并更新本表。** 本仓库**刻意不在记录身份的 docs-only 提交之后再打包**：那会让同一个 `0.3.8.0` 出现两个不同哈希的发布包身份（对照 §12 对 `0.3.7.0` 三份包身份的警示）。因此**L1 收敛时**唯一的 `0.3.8.0` 产物就是本表中由 `4d35801` 构建的那一份。

### 12.3 L2 工作区构建身份（与 §12.2 的 L1 产物**不是**同一份）

**L2 实现与随后的生命周期/ownership 修复轮（§2.8 / §2.8.1）在 `7a67546` 之上产生了新的源码改动，因此 `0.3.8.0` 现在有第二份构建身份。产品版本**仍为** `0.3.8.0`（用户要求不递增第四位），**Phase 文案未修改**。**

| 项 | 值 |
| --- | --- |
| 基线（起始 HEAD） | `7a67546a62bf02e7079ebf77cbecfcd282d8b0e2` |
| 产品版本 / FileVersion | `0.3.8.0` |
| L2 源码提交 | `22f346f`（feat）→ `a7a4ef1`（test）→ `e475d96`（refactor）→ `6d8522e`（fix: lifetime/ownership hardening）→ **`cede25d65c529ad616e24ed6454e215336ba2bd8`（test，末次源码提交）** |
| **ProductVersion（含 `+hash`）** | **`0.3.8.0+cede25d65c529ad616e24ed6454e215336ba2bd8`** ⇒ 精确指向**末次 L2 源码提交**（在其之后只有 docs-only 提交） |
| DLL SHA256 | **`5B16C9C1936442E8B8AE2D22309082E85092A567F82E78BEFFC1D2725967AE28`**（322560 字节） |
| 发布包 ZIP SHA256 | **`C8720F91F37B14BC723C09027E5544DD40FAB0957F5F978F3B867DEE04CC38B0`** |
| 包内容 | 仅 3 个顶层文件（`Info.json` / `ADOFAI.Renderist.dll` / `LICENSE`），**0 个嵌套目录** |
| 构建/验证 | 强制 Release Rebuild **0 error**（仅 `NU1900` 离线噪声）；package + 独立 verify 均 **PASS 11 checks / 0 failures**；`git diff --check` clean；离线回归 **128/0/12**、真实 fixture 回归 **140/0/0**（§2.8 / §2.8.1） |
| 与 §12.2 的关系 | **两份都是 `0.3.8.0`，内容不同**：§12.2 的 `AE2326D3…`（`+4d35801`）是**已部署并通过用户实机验收的 L1 构建**；本节 `5B16C9C1…`（`+cede25d`）**包含 L2 与修复轮源码，但尚未部署、尚无任何实机证据**。引用 `0.3.8.0` 时必须同时给出 DLL SHA256 或 `ProductVersion` 的 `+hash`。**部署前不得用本节身份替代 §12.2 的实机验收结论。** |
| 被取代的 L2 中间产物 | 修复轮之前的 L2 包（DLL `C0092C4C…` / `+e475d96`、ZIP `B13ACCE2…`）**已被本表取代**，且从未部署、从未被任何人验收：不要再用它指代 L2 构建。 |

**`0.3.8.0` 的能力边界（不得误读）**：`0.3.8.0` 的**已部署构建**覆盖 **L1 — FFmpeg Component Management**（组件发现 / 固定 manifest / 能力探测 / 安全安装 / HTTPS 下载）；**L2（FFmpeg Video Process Pipeline）已实现为独立 Unity-free 源码并通过 net48 回归，但尚未接入 Unity、也未部署（§2.8 / §12.3）**；**L3（Unity MP4 Frame Transactions）未实施**。因此**任何** `0.3.8.0` DLL 都 **不具备** MP4 导出能力，本文件任何位置都不得表述为「MP4 可用」。**`0.3.8.0` L1 构建本身已通过用户最小实机验收**（UMM 显示 `0.3.8.0`、FFmpeg `Ready`、`libx264` / `mp4` / `rawvideo`、禁用并重新启用、无新可见异常；见 §2.7）；收敛前的 `1D70BE45…`（`+02c7fb8`）同类证据保留于 §2.7。

