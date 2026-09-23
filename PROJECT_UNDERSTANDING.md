# ADOFAI Renderist 项目理解

> 本文件只保留当前项目状态、后续 Agent 持续需要的已验证事实、已确定技术路线、失败模式与未解决风险。
> 规则冲突：当前用户要求 > 网页版 GPT 项目指令 > `AGENTS.md` > 本文件。
> 事实冲突：实际仓库、diff、构建、测试、日志和实机结果 > 本文件。

---

## 1. 当前目标与硬边界

ADOFAI Renderist 是基于 **Unity Mod Manager（UMM）** 的 ADOFAI 编辑器内非实时渲染导出 Mod。

当前**已实现**路线：编辑器内原生 Camera → Renderist-owned RenderTexture → PNG 序列 / Log-only；`MasterTimeline` 控制逻辑帧。**下一阶段已批准、尚未实施**：`0.3.8.0 — FFmpeg Video Export Pipeline`，在保留两条现有路径的同时增加独立 MP4 输出（技术决策与待验证项见 §2.1）。

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
| 产品版本 | `0.3.7.1` |
| Phase | `Phase 3.7.0 Custom Resolution & Supersampling`（收敛第四位时 **Phase 保持不变**） |
| 版本定位 | `0.3.7.1` = **Custom Resolution + Supersampling 双闭环稳定性收敛版**（`0.3.7.0` 功能不变，仅第四位递增）。第一闭环 Custom Resolution 与第二闭环 Supersampling **均已在当前 Gamma / Direct3D11 环境通过实机验收**；其下 `0.3.6.4` = **Log-only Frame Transactions（image output disabled）** |
| 稳定实机基线 | **`0.3.7.1`**（= `0.3.7.0` 双闭环 + `ce34ad4` metadata 语义修正；构建身份见 §12）。`0.3.7.0` 第一闭环（Custom Resolution）与第二闭环（Supersampling，含 **scale=4 完整导出**）均已实机通过：第一闭环见 §9.1；第二闭环见 §9.7.1 + §9.7.3。更早的稳定基线 `0.3.6.4`（PNG 与 Log-only 同谱面跑通）仍见 §9.1。**未覆盖边界**（Linear 色彩空间、极端资源失败）见 §9.7.3。 |
| 当前开发方向 | 双闭环均已收敛；**未开始**音频 / FFmpeg / replay / 外部视频编码。第二闭环仍然**未覆盖** Unity **Linear** 色彩空间与极端资源失败路径（详见 §9.7.3），不得由 Gamma 结果外推。 |
| 下一阶段设计状态 | 用户已批准目标版本 `0.3.8.0`、名称 `FFmpeg Video Export Pipeline` 及准确 Phase 文案 `Phase 3.8.0 FFmpeg Video Export Pipeline`。截至本次架构调查，产品及源码仍为 `0.3.7.1`；视频管线**待实现、待验证**，本节批准不表示版本已变更。 |

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

### 2.1 Event-Driven A+ 视频导出设计（待实施）

**调查依据**：本次在干净工作区核对 HEAD `01555e2300d38c938801d69d6d4387691ee96b58`、版本点、`MasterTimeline`、`DeterministicFrameScheduler`、`FrameCaptureDriver`、`OutputGeometryPolicy`、`EditorExportController`、`EditorExportSession`、Settings / GUI / preflight / metadata。以下“已确认”均指当前 `0.3.7.1` 源码静态事实，不是 MP4 实机验收。

- **已确认基线**：唯一 `MasterTimeline` 用已提交的 `_outputFrameIndex` 映射输出/谱面时间；PNG / Log-only 共用一个 EOF coroutine 和 `OnCaptureResult` → 唯一 `CommitFrame`。当前捕获回调在 Unity 主线程同步执行；PNG 在 ReadPixels、GPU 状态恢复、`Texture2D.Apply`、PNG 编码及写盘成功后提交，Log-only 在帧末校验后提交。Source RT 与下采样链由冻结几何驱动；当前链只在 PNG 且 scale>1 时创建。metadata 的 `imageOutputEnabled` 是旧两模式布尔语义，不足以表示独立 MP4 类型。
- **已确认的改造点**：`PrepareFrame` 与 `PreparePreEntryFrame` 把跨 Unity 帧 `_pendingCapture` 视为失败；视频等待 IO 时不能沿用该判断。现有捕获/进展 watchdog 均为 30 秒 wall-clock deadline；合法的编码背压须从 EOF 捕获超时中分离。`CommitFrame` 内包含 B−1 / G timeScale 转换、End Tail 完成判断，不能另设视频提交点。`EditorExportController.FinalizeFromScheduler` 当前把 scheduler Completed 直接记为 session Completed；视频须增加 FFmpeg 封装确认关口。
- **已确定路线（用户批准，尚未实现）**：保留 PNG 与 Log-only，新增互斥的 MP4 输出类型；PNG 与 MP4 在 GUI 中平级，Log-only 可继续作为独立验证选项。一个 session 冻结输出类型、几何、FPS、编码参数及 FFmpeg 可执行文件身份。MP4 沿用现有 Camera / EOF / scale>1 降采样链，从最终 output 尺寸读回，复制到由视频管线独占的完整 CPU 帧缓冲；不编码/写入中间 PNG，不建立 Renderist 主动管理的多帧队列。主线程只做 Unity / GPU 操作和短暂交付，后台异步写 FFmpeg stdin；保持至多一帧在途，完整帧写入成功的完成事件回到 Unity 主线程并通过 session generation + pending index 校验后才调用原 `CommitFrame`。下一帧的准备和逻辑时间推进必须等待该提交；IO 慢则自然背压，不能用固定单帧时限或性能估算拒绝合法工作。等待 FFmpeg 不使用 Update / Coroutine / 固定间隔轮询；Unity 既有 Tick 仍负责既有环境与生命周期观察，不充当视频 IO 探测器。
- **终态与隔离（待实现）**：FFmpeg stdin 完整写入只说明帧已交付，**不是 MP4 成功**。canonical completion / End Tail 排空后关闭 stdin、异步等待进程退出及 stderr 完成，确认退出码 0 和临时 MP4 有效，再将同卷临时文件发布为最终文件，随后标记 session Completed。取消/失败时废弃当前 generation、关闭管道、终止并回收进程、清理或隔离临时文件；迟到回调不能提交新 session 帧，cleanup residual 未收敛不得重开。后台线程不接触 Unity 对象。Unity 主线程完成通知采用事件驱动调度（需验证当前 Unity 的 SynchronizationContext 可用性），不得用定期轮询 IO 代替。
- **安装路线（待实现）**：优先级建议为用户明确指定路径 → Renderist 私有安装 → 系统 PATH → 提供用户主动触发的一键安装；明确指定的路径失效时提示修复，不能静默换用其他候选。首选来源倾向 Gyan.dev 的 Windows Release Essentials **ZIP**（免 7z 解压依赖），BtbN 为备选；这只是选源倾向，**具体版本、URL、资产 SHA-256 尚未锁定**。安装须使用审核并钉住的资产清单，经 HTTPS 下载到独立用户可写目录、哈希校验、限制归档解压路径并原子安装；不修改 PATH、不要求管理员权限、不覆盖现有安装、不进入三文件发布 ZIP。MP4 + H.264 / `libx264` 软件编码是初期**建议采用**的具体方案，尚需能力、奇数尺寸与许可证验证；硬件编码不属于首批前提。用户点击前不自动下载，FFmpeg 缺失不阻断 PNG / Log-only。
- **未解决 / 待验证**：Unity RGB24 CPU 像素缓冲的长度、行顺序与生命周期；MP4 像素格式/奇数尺寸的真实 codec 支持；等待编码器时 native Update、pre-entry lifecycle 与 visual clock 是否严格停在同一已渲染帧；主线程 SynchronizationContext 投递与取消竞态；FFmpeg 提前退出、管道半写、卡住时的取消、封装及发布失败、跨会话 residual；Gamma / Linear 色彩差异。实施验证应拆为静态不变量、独立进程故障注入、Unity 实机三层；当前均**未做 MP4 验证**。

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

1. **0.3.8.0 视频导出（尚未实施）**：Event-Driven A+ 决策见 §2.1。优先验证跨 Unity 帧的 pending、主线程完成投递与长时间 FFmpeg 背压下的原生 Update / visual state 是否漂移，再落实 FFmpeg 管理、MP4 接入、Finalizing 和完整故障收敛。静态设计**不是** MP4 运行时验收。
2. **Linear 与极端 GPU 资源失败**：Supersampling 已在 Gamma / Direct3D11 的所述范围实机通过，**Linear 色彩空间从未实机覆盖**；接近硬件极限的 RT 分配与 GPU 状态恢复故障仍只有 stub / 静态证据。详见 §9.7.3。
3. **真实 Unity 故障注入**：host Destroy、RT Release/Destroy、partial Camera assignment 的失败与重试，已有生产源码 + Unity stub 的确定性测试，**未在真实 Unity Player 注入这些异常**；正常路径与跨调用 residual 的既有实机结果不能替代异常证据（§3.5、§9.3）。
4. **其余边界测试**：显式 safety frame-limit runtime trigger 与 `TryPrepareHitState` 故障注入主要依赖静态 / 纯计算证据；需要扩大 BPM change、Twirl、Midspin、event-heavy、特殊 startup、长谱面覆盖。无须为此设置人为帧数或时长上限。
5. **Log-only 取消覆盖**：pre-entry 阶段取消、End Tail 阶段取消、取消后切换 PNG 尚无**该模式**实机证据（§9.4）。`0.3.7.0` 的 PNG pre-entry 取消（session `152633`）不能代替 Log-only 验收。
6. **Camera aspect 边界**：当前受测环境的三台 baseline 均一致（1.6）；Unity 自动 aspect 对屏幕 / camRT 的内部来源未确认。若游戏合法出现三台 Camera 不同 aspect，现有 baseline-incompatible gate 可能误拒绝；不得将该理论风险记成已经发生（§9.1、§10.1）。
7. **原生 lifecycle / 音频遗留**：`lifecycleSongPosition` 与 raw DSP 非视觉 authority；count-in 文本/SFX 和未来音频同步仍待独立调查（§10.5）。曾见 `Coroutine couldn't be started ... Conductor is inactive` 的 native Esc teardown 警告，尚未做 disable-mod A/B。
8. **工具债务**：`set-version.ps1 -Phase` 不同步全部 phase 文案，后续升级必须人工核查 `EditorExportSession.PhaseLabel` 与相关注释（§2）；不阻塞当前稳定基线。

---

## 12. 发布与部署

- 当前产品版本为 **`0.3.7.1`**（**Custom Resolution + Supersampling 双闭环稳定性收敛版**；`0.3.7.0` 功能不变，**仅第四位递增**，Phase 保持 `Phase 3.7.0 Custom Resolution & Supersampling`）；其下的 `0.3.7.0` = 双闭环功能版，`0.3.6.4` = Log-only Frame Transactions。前三位 `0.3.6` → `0.3.7` 与 Phase 文案在 `0.3.7.0` 轮次**均已变更**（用户明确批准）；`0.3.7.0` → `0.3.7.1` 未改 Phase。
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

