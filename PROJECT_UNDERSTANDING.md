# ADOFAI Renderist 项目理解

> 本文件只保留当前项目状态、后续 Agent 持续需要的已验证事实、已确定技术路线、失败模式与未解决风险。
> 规则冲突：当前用户要求 > 网页版 GPT 项目指令 > `AGENTS.md` > 本文件。
> 事实冲突：实际仓库、diff、构建、测试、日志和实机结果 > 本文件。

---

## 1. 当前目标与硬边界

ADOFAI Renderist 是基于 **Unity Mod Manager（UMM）** 的 ADOFAI 编辑器内非实时渲染导出 Mod。

当前路线：

```text
编辑器内导出
→ 原生谱面 Camera 链接管到 Renderist-owned RenderTexture
→ PNG 截图序列
→ MasterTimeline 可控逐帧
→ 更完整的非实时渲染
```

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

---

## 2. 当前版本、Git 与工具基线

| 项目 | 当前状态 |
| --- | --- |
| 产品版本 | `0.3.6.2` |
| Phase | `Phase 3.6.0 Render Time Determinism` |
| 版本定位 | `0.3.6.2` = **correctness hardening revision**（cleanup / activation ownership + End Tail numerical correctness）；无新功能 |
| 稳定实机基线 | 30 FPS（0.3.6.1 与 0.3.6.2 各一次）与 1000 FPS 完整导出通过（2026-09-15，见 §9.1） |
| 当前开发方向 | correctness hardening 已收敛；log-only / image-output-disabled 顺延为 0.3.6.3 候选，尚未开始 |

`0.3.6.2` 相对 `0.3.6.1` 的四个 hardening 点（功能语义不变，只收敛异常路径与输入判定）：

1. **cleanup host ownership**：`Destroy(host)` 成功前不丢 `_host` / `_behaviour`；failure 保留 ownership，下一次 `Stop()` 可重试。
2. **capture target ownership**：`Release` / `Destroy` 成功前不丢 `_captureTarget`；live Camera 仍引用 target 时绝不销毁；failure 保留 residual ownership。
3. **activation ownership**：RenderTexture 创建成功后不存在 untracked ownership window；Camera assignment 之前先 publish ownership；partial Camera assignment failure 走同一个 `RestoreCameraSource`；rollback failure 保留 Camera refs / saved targets / capture target，下一次 `Stop()` 继续收敛。
4. **End Tail numerical hardening**：直接 Frames 输入使用**固定绝对容差 `1e-10`**（不再使用 magnitude-relative tolerance）；Seconds / Beats computed frame count 只用 `max(1e-10, half-ULP)` 吸收浮点表示误差；不引入任何 FPS / frame / duration 人为产品上限。

对应实现提交：`8bceeef`（cleanup ownership + End Tail validation）与 `1af1205`（activation ownership）。

版本同步事实：

- `scripts/set-version.ps1` 实际自动修改：`mod/Info.json Version`、csproj `<Version>`、`ModEntry.ModVersion`、`ModEntry` 启动日志里的 version + phase。
- `-Phase` **不是全仓库 phase 同步器**：`EditorExportSession.PhaseLabel`、`ModEntry` / `FrameCaptureDriver` 类注释等 phase 文案仍需人工核对。脚本 DESCRIPTION 对“只修改三类文件”的描述是准确的，但 SYNOPSIS“Synchronizes ... version and phase text”容易被理解得过宽；当前属于非阻塞 tooling debt。
- `package-release.ps1` 在打包前交叉校验 Info.json / csproj / `ModEntry.ModVersion`。
- 本地编译引用由 `scripts/prepare-references.ps1` 从真实 ADOFAI / UMM 安装生成 ignored `build/local.props`；仓库不跟踪 `references/`。
- `Assembly-CSharp.dll` 只作为当前游戏内部行为调查基线，不是 compile-time reference。

---

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
→ ReadPixels → EncodeToPNG → File.WriteAllBytes
→ 仅写盘成功后 commit N
→ completion 后按成功 commit 的 output frame 计 End Tail
```

关键不变量：**Frame N 未成功完成 capture transaction，就不 commit N，也不开始 N+1。**

`MasterTimeline.FrameIndex` 是 export / chart timeline authority；wall clock 与 audible audio 都不推进它。`Time.captureFramerate` 负责 Unity engine timestep，不等于 MasterTimeline 拥有全部 Unity state。

### 3.3 Completion 与取消

Canonical completion：

```text
scrController.OnLandOnPortal Postfix 观察到完成请求
+ controller.state == Won
+ completion 当帧成功 capture/commit
+ 配置 End Tail 全部成功 commit
= Completed
```

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
- `HasResidualOwnership()` 覆盖 playback、Unity timing、lifecycle handoff、`_captureGeneration`、capture host/source/target、input guard、forced clock、Conductor/AsyncInput/completion/native-stop hooks、`RDC.auto` 与 editor selection。
- Camera `targetTexture` 只在当前值仍 `ReferenceEquals(captureTarget)` 时恢复 saved old target；native 已先接管/置空则不覆盖。
- live Camera 仍引用 captureTarget 时绝不 Release/Destroy RT。
- **capture host ownership**：`CaptureHostBehaviour.Shutdown()` 先同步失效 coroutine/callback；`_host/_behaviour` 只有在 `Destroy(host)` 返回成功后才清空。Destroy 抛异常时保留引用，下一次 `Stop()` 可重试。
- **capture target ownership**：Camera ownership 已 relinquish 后，`Release()` / `Destroy()` 任一步失败都返回 cleanup failure，并保留 `_captureTarget` 及必要状态；只有两步成功后才丢引用。异常不再被吞掉。
- **activation 前的 ownership tracking（durable invariant）**：capture target 从 **RenderTexture 创建成功那一刻**起就受 ownership tracking——创建后到 source activation 完成之间的任何失败路径都必须二选一：Release + Destroy 均成功，或引用保存在 `_captureTarget`（可观察、可重试），绝不作为 local reference 丢失。其中：
  - 尚未接触任何 Camera 的失败（`IsCreated()==false`、读取 saved old target 抛异常、销毁失败）→ 只保留 `_captureTarget`，`_sourceActive` 保持 false，由 `Stop()` 的 source-inactive retry path 重试 `TryDestroyTexture`；
  - 已可能写入过 Camera 的失败（partial Camera assignment）→ 因为 `_captureTarget` / capture dimensions / camera refs / saved old targets 在**第一次 Camera 写入之前**就已登记，`_sourceActive` 表示"ownership transaction 已开始（可能 partial）"，失败即走同一个 `RestoreCameraSource`（不新增第二套 partial cleanup）；rollback 写失败时保留全部 refs 作为 residual，下一次 `Stop()` 重试；
  - **只有 `IsCaptureTargetStillReferenced() == false` 时才 Release / Destroy RT**：partial 状态下即使 cleanup 失败也**不会**销毁仍可能被 live Camera 引用的 RenderTexture。
- 若 Camera 已 relinquish、host 已销毁，但 capture target 销毁失败，scheduler 的 `_captureGeneration` 与 `FrameCaptureDriver.HasOwnedCaptureTarget` 仍构成 residual ownership；下一次 cleanup 只需重试资源销毁，不会重新改写 Camera。
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

- `Time.timeScale` 不是 Renderist-owned state；Renderist 不写。
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

### 5.2 Persisted End Tail 的未决治理冲突

当前 `ModEntry.Load → ResetEndTailGuiState()` 会在 preflight 之前对已持久化的非法设置做静默 sanitize，并写回内存 Settings：

- 非法 unit →默认 Frames；
- NaN / Infinity →默认值 12；
- 负数 → 0；
- fractional Frames → `ceil`。

因此“`EndTailPolicy.TryValidateInput` 对传入非法值 fail-closed”是真的，但“任何持久化非法 End Tail 都会在启动前原样 fail-closed”**不是真的**：部分值在到达 policy 前已被 GUI state reset 修复。

这是产品行为选择，不在 correctness hardening 中擅自改变。后续需用户选择：

A. **保留非法 persisted value + fail-closed**：不静默改用户配置，GUI 明确显示错误，用户手工修正后才能启动。

B. **显式 migration/sanitize**：继续自动修复，但要把它定义成正式 migration 行为，明确哪些值如何迁移、是否写回磁盘、是否提示用户，并与 preflight 文案一致。

在用户确认前不得把任一方案写成既定产品语义。

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
- `FrameCaptureDriver`：generation 隔离、两阶段 Camera source activation（创建 target → 登记 ownership → 逐 Camera 接管）、同步 PNG、ownership-aware cleanup（activation 窗口内也不丢 ownership）。
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
| Failed | PNG 请求/写盘/transaction | `capture-failure` |
| Failed | API/lifecycle/cleanup | `lifecycle-failure` |

取消时允许 `captureRequestCount = capturedFrameCount + 1`：一个已请求但未 commit 的事务可以被取消，不能为了计数相等而伪 commit。

metadata 记录：版本/phase、state/detail/reason/kind、completion signal/index、output FPS、pitch、End Tail 输入与解析结果、safety policy、capture source/尺寸、request/captured counts。

---

## 9. Runtime / 静态验证基线

### 9.1 实机（0.3.6.1 / 0.3.6.2）

**0.3.6.1 30 FPS**：

- editor idle 直接启动正常；paused 生命周期修复有效。
- lifecycle：Start → OnMusicScheduled → Countdown → PlayerControl。
- autoplay 正常，同帧双 Hit 成功。
- canonical completion 正常。
- completion 后精确 12 End Tail。
- `frame_000000.png .. frame_000113.png` 连续。
- 最终 Completed。

**0.3.6.1 1000 FPS session `editor_20260915_095106`**：

- `state=Completed`
- `stopReason=canonical-completion-tail-drained`
- `terminationKind=canonical-completion`
- `completionFrameIndex=3323`
- `safetyPolicy=unbounded`，frame/duration 均 null
- `resolvedTailFrames=12`，`tailFramesCaptured=12`
- `captureRequestCount=capturedFrameCount=3336`
- capture 3072×1920
- 未出现 controller-paused、hit-state、RDC restore、safety-limit、frame-index-exhausted 或 watchdog failure。

**0.3.6.2 30 FPS smoke session `editor_20260915_115023`**（activation ownership hardening 的正常 Player 路径）：

- `state=Completed`，`stopReason=canonical-completion-tail-drained`，`terminationKind=canonical-completion`
- `completionFrameIndex=101`；`resolvedTailFrames=12`，`tailFramesCaptured=12`
- `captureRequestCount=capturedFrameCount=114`，frames `0..113` 连续（PNG 与 scheduler commit 均 114 次、无缺号）
- `captureSource=scrCamera-rendertexture`，3072×1920
- 未出现 `capture-source-failed`、`capture-target-*`、`cleanup-failed`、`capture-stop`、`tick-exception`、host destroy / RT Release / RT Destroy failure、restore incomplete 或 residual retry warning

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
| 30 FPS Unity smoke（`editor_20260915_115023`） | 通过（见 §9.1） |

---

## 10. 当前 ADOFAI 内部关键事实（Assembly-CSharp 0.4.3.0）

### 10.1 scrCamera / targetTexture

- `scrCamera.instance` 是 static property。
- `Bgcamstatic` / `BGcam` / `camobj` 是 public Camera fields。
- `SetupRTCam(bool)` 是游戏内写这三台 targetTexture 的关键入口：true → 自有 camRT；false → null，并同步 Overlaycam/quad。
- 调用点包含 `scrCamera.Awake/Update`、`scnGame.Play`、`scnEditor.Start`、`scnEditor.SwitchToEditMode`；`scnEditor.Play` 本身不直接调用它。
- `scnEditor.Start` / `SwitchToEditMode` 结尾会 `SetupRTCam(false)`。
- AdofaiTweaks 已排除对该 targetTexture 链的直接引用干扰（此前基线扫描为 0）。

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

---

## 11. 未解决问题 / 风险 / 下一步

1. **Persisted End Tail 政策待用户选择**：保留非法值 fail-closed vs 显式 migration/sanitize（见 §5.2）。
2. **cleanup / activation 异常注入的真实 Unity 验证**：host Destroy、RT Release/Destroy 与 partial Camera assignment 的异常路径已用 **production source + Unity stub 的确定性 fault injection** 与静态断言验证（含"仍被引用时绝不 Destroy"、"失败后 ownership 保留"、"重试后清空"）；**仍未在真实 Unity Player 内注入 `Object.Destroy` / `RenderTexture.Release` 异常**。正常路径已有历史实机基线。
3. **显式 safety frame-limit runtime trigger**：目前主要是静态/纯计算证据。
4. **TryPrepareHitState 故障注入**：正常路径已实机，注入失败路径主要是 IL/control-flow 证据。
5. **trail / star-trail 待录屏**：用户观察 capture 正式开始前拖尾/星轨轨迹僵硬；尚未调查，不猜根因。需录屏后确认 Initialization Hold → lifecycle-ready → forced clock/source activation → frame 0 是否有状态不连续、是否污染成品帧。
6. **更广泛谱面覆盖**：BPM change、Twirl、Midspin、event-heavy、特殊 startup、长时大规模导出。
7. **native Esc teardown 警告**：曾见 Unity `Coroutine couldn't be started ... Conductor is inactive`，静态证据更像 native teardown；未做 disable-mod A/B。
8. custom resolution / supersampling、audio、FFmpeg、replay、Preview Bridge 均未实现。
9. **log-only / image-output-disabled 诊断模式（0.3.6.3 候选）**：不属于 `0.3.6.2`，尚未实现，不应与 VerboseLogging 永久绑定。未来设计必须先明确 logical commit、capture request、captured/written frame count 和 metadata 语义，再决定是否仍需激活 RT/Camera source；不得静默改变 completion / End Tail 计数。
10. `set-version.ps1` phase 同步范围说明可在后续 tooling 清理时收紧，当前不阻塞产品一致性。

---

## 12. 发布与部署

- 当前产品版本为 `0.3.6.2`（correctness hardening revision：cleanup / activation ownership + End Tail numerical semantics，无新功能）。前三位 `0.3.6` 与 Phase `Phase 3.6.0 Render Time Determinism` 均不变。
- 历史：`0.3.6.1`（Output FPS 无上限、safety 默认 unbounded、long frame chain、autoplay fail-closed、paused 阶段修正）→ `8bceeef` + `1af1205` hardening → `0.3.6.2`。
- 发布包：`Info.json` + `ADOFAI.Renderist.dll` + `LICENSE`；`dist/` ignored。
- 自动验证链：
  `dotnet build src/ADOFAI.Renderist/ADOFAI.Renderist.csproj -c Release -t:Rebuild`
  → `scripts/package-release.ps1 -Configuration Release -Force`
  → `scripts/verify-release-package.ps1 -ZipPath dist/ADOFAI.Renderist.zip`。
- 部署使用 `scripts/copy-to-mods.ps1`，只更新 `Mods\ADOFAI.Renderist\`；路径来自本地 ignored `build/local.props`，未配置时不得猜测。
