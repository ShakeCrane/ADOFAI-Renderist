# ADOFAI Renderist 项目理解

> 本文件是跨会话、跨模型的项目状态快照，由参与项目的 Agent 按实际证据共同维护。
> 规则优先级：当前用户要求 > 网页版 GPT 项目指令 > `AGENTS.md` > 本文件。
> 事实冲突时：实际仓库、diff、构建、日志和运行结果 > 本文件。

---

## 1. 项目目标与规则

ADOFAI Renderist 是面向 **A Dance of Fire and Ice（ADOFAI）** 的 **Unity Mod Manager（UMM）** 模组，
目标是 **编辑器内的可控、可复现非实时渲染导出**。

硬性边界：

- UMM-only；禁止 BepInEx / MelonLoader / Doorstop / 多 Loader 抽象
- `net48`
- Harmony（优先 Prefix / Postfix；避免 Transpiler）
- 最新 ADOFAI 正式版策略
- 编辑器内导出主路线；当前不依赖 replay
- 版本规则：严格四位（主.次.功能.修订）；前三位变更必须询问用户
- 发布包固定为 `Info.json` + `ADOFAI.Renderist.dll` + `LICENSE`
- 不提交任何 DLL / 反编译源码；不修改用户关卡；README 由用户维护

Rendering boundary（阶段顺序）：实时录屏（仅验证）→ 编辑器截图序列 → 可控逐帧 → 非实时渲染 → 音频 → 外部视频编码 → replay 扩展。

---

## 2. 当前版本 / 环境 / 工程基线

| 项目 | 当前基线 |
| --- | --- |
| Mod ID / DLL / Namespace | `ADOFAI.Renderist` |
| 当前版本 | `0.3.3.1`（第四位修订：Repository Hygiene + Deterministic Hardening） |
| 当前阶段 | `Phase 3.3.0 deterministic hardening` |
| ADOFAI | `Assembly-CSharp.dll` `0.4.3.0`（本机 Steam 安装，运行时反射核对，不作编译引用） |
| Unity | `6000.3.10f1`，Mono（`MonoBleedingEdge/` 存在） |
| UMM | `0.33.0`（`UnityModManager.dll`）；Harmony `2.3.6.0`（UMM 内置 `0Harmony.dll`） |
| TargetFramework / LangVersion | `net48` / `9.0` |
| License | Apache-2.0 |

版本同步位置：`mod/Info.json`、`src/ADOFAI.Renderist/ADOFAI.Renderist.csproj`、`ModEntry.cs`（ModVersion 常量 + 启动日志）。统一用 `scripts/set-version.ps1` 变更。

---

## 3. 当前正式实现

当前主路线是 **MasterTimeline deterministic editor export**：

```text
FrameIndex N
  → T_N = N / OutputFps
  → forced chartTime_N = gameplayStart + T_N × pitch
  → scrConductor.Update Prefix 写入 forced songposition
  → scrConductor.Update Postfix：RenderistAutoPlay due-floor → 官方 scrPlayer.Hit(true)
  → WaitForEndOfFrame → FrameCaptureDriver 同步 PNG
  → commit N → N++
```

关键组件：

- `MasterTimeline.cs`：FrameIndex 驱动的唯一逻辑时间 authority。
- `PlaybackLifecycleHandoff.cs`：关联本次 Renderist-owned `editor.Play()` 的官方启动 commit 链（`StateEngine.Changed` + `OnMusicScheduled` Postfix）。
- `RenderistAutoPlay.cs`：不在拥有时间，在官方 `Hit(true)` 调用期间临时置 `RDC.auto=true` 并立即恢复。
- `EditorVisualClock.cs`：`songposition_minusi` getter/setter 的 forced clock bridge。
- `DeterministicFrameScheduler.cs`：启动 / 逐帧 / 捕获 / 恢复的统一调度。
- `FrameCaptureDriver.cs`：`ReadPixels → EncodeToPNG → File.WriteAllBytes` 的同步 PNG 后端，带 generation 隔离。
- `EditorExportController.cs`：会话生命周期与元数据落盘。

`EditorGameReflection.cs` 以运行时反射解析 ADOFAI 内部 API；`Assembly-CSharp.dll` 刻意不作为 compile-time reference，不提交。

F9 / F10 历史实时截图路径已从正式仓库删除；当前项目仅保留 deterministic editor export 主路线。

---

## 4. Runtime-validated baseline

### 0.3.3.0 — 普通 floor0 baseline（历史 RUNTIME VALIDATED）

- pitch=1、outputFps=60、targetFrameCount=180、Official AutoPlay OFF、`RDC.auto=false`、无人工输入
- lifecycle：`Start → OnMusicScheduled → Countdown → PlayerControl → lifecycle-ready`
- gameplay-start anchor：`lifecycleReadySongPosition=1.074622` vs `gameplayStart=1.058824`，差约 `0.015798s`，通过 `0.05s` tolerance
- Frame 0 transaction：BEGIN → Conductor → AutoPlay → EOF → PNG → FrameBoundary → COMMIT
- Frame 0–179 全程 `playerAlive=true`、`controllerState=PlayerControl`，无 early death
- due-floor Hit 进展：floor0→floor1（Frame22）至 floor7→floor8（Frame170）共 8 次，均 `due=true`、`hitResult=True`、`hitsThisFrame=1`、无 duplicate
- `AsyncInputUtils.AdjustAngle(scrPlayer, ulong)` async overwrite 已确认解决（forced-capture 期间抑制）
- 终态：`stopReason=target-frame-count-reached` / `Completed`；180/180

### 0.3.3.1 — RUNTIME VALIDATED（lifecycle closure achieved）

`0.3.3.1 runtime closure achieved`：A/B/C/D 实机回归全部通过。

- **A. normal completion**（`editor_20260909_223622`）：`captureRequestCount=180`、`capturedFrameCount=180`、`state=Completed`、`stopReason=target-frame-count-reached`
- **B. Completed 后 single-click restart**（`editor_20260909_223700`）：一次 Start 即进入新 session、FrameIndex 从 0、无 transient controller-Fail session、再次 180/180 Completed；Player.log 记录 `terminal controller re-arm requested via ChangeToStartState` → `terminal controller re-arm confirmed: state=Start; beginning normal session`，说明 single-click terminal re-arm 已 runtime validated
- **C. 原生 Esc**（`editor_20260909_223803`）：`captureRequestCount=capturedFrameCount=94`、`state=Cancelled`、`stopReason=native-playback-stopped`；无 `cleanup-failed`、无 `playback-stopped-unexpectedly`
- **D. Esc 后 restart**（`editor_20260909_223829`）：一次 Start 直接进入 normal session、FrameIndex=0、capture 正常、无 controller-Fail、无 residual cleanup rejection

0.3.3.1 Runtime Validated scope（仅限以下已验证范围，不泛化）：

ordinary floor0、pitch=1、60 FPS、fixed targetFrameCount=180、deterministic frame 0..179、due-floor Hit progression、synchronous PNG commit、normal completion（`Completed` / `target-frame-count-reached`）、capture cleanup、terminal cleanup、Completed→single-click restart（terminal re-arm via confirmed `ChangeToStartState` path）、native Esc（`Cancelled` / `native-playback-stopped`）、Esc cleanup、Esc→subsequent restart、no residual capture / Harmony / playback ownership observed in tested paths。

---

## 5. MasterTimeline / lifecycle / gameplay invariants

- MasterTimeline 是唯一逻辑时间权威；wall clock、Unity Update 次数、AudioRenderer 都不拥有时间。
- `wall clock`（`Time.realtimeSinceStartupAsDouble`）只用于 watchdog 失败保护，绝不决定 FrameIndex / chartTime / outputTime。
- `RenderistAutoPlay` 只消费 due floor：`chartTime + tolerance >= nextFloor.entryTime` 才调用官方 `scrPlayer.Hit(true)`，且 `RDC.auto` 事务必须先读取旧值、写 true、完成后恢复原值；读取或恢复失败必须 fail。
- Official Autoplay 仅辅助/参考验证，不是 deterministic authority；RenderistAutoPlay 不是 replay。
- 只有 `stopEvent == "completed"`（`target-frame-count-reached`）映射为 `Completed`；`user` / `cancelled` / `mod-disabled` / `left-editor` / `native-playback-stopped` 全部映射为 `Cancelled`；其它异常为 `Failed`。
- `CheckPostHoldFail`：方向性未归一化差值 `delta = chosenPlanet.angle - targetExitAngle`（`!isCW` 取反），`delta > max(PI, _minAngleMargin*2)` 时官方 Die；无 DeltaAngle / modulo / wrap。普通 floor0 运行时按此正常 missed-tile 语义通过 180-frame 验证。

---

## 6. Hook / capture / cleanup ownership

- 每个组件自己精确记录待释放的 original `MethodInfo`，用 `Harmony.Unpatch(original, patchMethod)` 精确撤销，不依赖单一 bool；Patch / unpatch 异常时保留对应 handle 供重试。
- `EditorVisualClock`：`_patchedGetter` / `_patchedSetter`。
- `DeterministicFrameScheduler`：`_patchedConductorUpdate` / `_patchedAsyncInputAdjustAngle`。
- `PlaybackLifecycleHandoff`：精确 unpatch `OnMusicScheduled` Postfix + 可重试 `TryDispose`（unsubscribe `Changed` / 逐项清空字段）；scheduler 仅在成功后释放 `_handoff`。
- 最终 safety net 保留 `Harmony.UnpatchAll(HarmonyId)`（Mod disable 时），因为该 HarmonyId 属于 Renderist 自己。
- `FrameCaptureDriver`：`Start` 每次分配唯一 generation；旧 session 的 EOF callback 按 generation 拒绝；`Stop` 先使 generation 失效，再同步 `Shutdown` behaviour，成功后才清静态 host 引用并销毁 host；重复 Stop 可收敛为成功。
- Scheduler 在 `ResetRunStateForStart()` 前执行 residual cleanup gate；`ResetRunStateForStart()` 只重置已可重建的 transient state。`RestoreAll` 聚合清理结果，只有对应恢复成功才清 ownership handle；失败会使 scheduler Failed，并阻止下一次 Start 覆盖 residual ownership。
- `EditorExportController.Start()` 在创建唯一 session 目录前调用 scheduler 的 pre-start gate，因此 residual / busy / 当前游戏状态拒绝不会生成空 session folder 或 metadata。
- 本次 0.3.3.1 cleanup regression 的精确根因是：旧 `RestoreAll()` 在 `FrameCaptureDriver.Stop()` 正常返回后仍检查 scheduler 自己未清除的 `_captureGeneration != 0`，于是每次收尾都伪报 `capture-stop`；修复后仅在 `Stop()` 明确成功且 driver 不再运行时清除 scheduler generation。
- 最终 cleanup 顺序为：capture generation / callback 失效 → forced clock inert → scheduler hooks → lifecycle handoff → RDC.auto 与 selected floors → 最后 RestorePlayback / editor state；避免 scene/editor 切换提前影响 gameplay-scene 依赖状态。
- 旧 `ValidateStartConditions()` 无条件把 ADOFAI 内部 `controller.state == Fail/Fail2` 作为 `controller-fail-state` 拒绝；这会把上一轮正常完成后残留的 ADOFAI terminal controller state 错当成永久不可重启。当前实现不再直接让该状态进入 session：仅在 EditorExportController 已有 `Completed / Cancelled / Failed` terminal session 且 scheduler pre-start/residual gate 已通过时，调用当前 DLL 已确认的公开 `scrController.ChangeToStartState()`；该迁移由 ADOFAI StateEngine 异步完成，Controller Tick 只在观察到确切 `Start` 后创建 session。随后正式 scheduler 使用普通 Fail/Fail2 安全门并只调用一次 `editor.Play()`。
- 当前 ADOFAI 反编译还确认 `scnGame.Play()` → `Awake_Rewind()` → `ChangeState(Start)` 是异步状态迁移，因此旧“第一次 editor.Play 后立即建立 lifecycle”会观察到旧 Fail，而下一次调用才进入 Start；Renderist re-arm 不调用 editor.Play、不创建目录、metadata、capture generation 或 deterministic hooks。
- `CheckEarlyTermination()` 原先在 `_ownsPlayback && editor.playMode != true` 时无条件返回 `playback-stopped-unexpectedly`。当前只在 controller 非 Fail/Fail2 且 `playerOne.alive=true` 时返回 `native-playback-stopped`；cleanup 成功后映射为 `Cancelled`，不伪造 `user-stop`，未知/内部失败仍为 `Failed`。

---

## 7. 已排除或退役方案

- F9 / F10 实时截图（`CaptureService` / `Metadata` / `ExportCoordinator` / `Preflight` / `PreflightReport` / `ExportState` / `ExportStateMachine` / `DirectoryBrowserGui`）：已删除。
- `AudioRenderer` 作为逻辑时间 owner（旧 `OfflineAudioClockPoC`）：已删除；AudioRenderer 只保留 future slave / validator 方向。
- 旧 `EditorVisualClockPoc` / DVA / Frame-Order / `PlaybackLifecyclePoC` / `CanonicalStartupProbe` / `GameplayHandoffProbe` / `DeterministicHandoffPoc` / `EditorTimeProbe`：已退役并删除，结论转入本文件。
- BepInEx / MelonLoader / Doorstop / 多 Loader 抽象：禁止。
- replay / TUFReplay / Creplay / replay API：当前不实现。
- 自定义输出分辨率 / supersampling / canonical level completion / tail policy / checkpoint start / 大范围谱面兼容：当前不实现。
- Assembly-CSharp.dll 作为 compile-time reference：排除（只用运行时 reflection）。

---

## 8. 当前仍未验证 / 未完成能力

0.3.3.0 baseline 只覆盖普通 floor0、pitch=1、60 FPS、180 帧。以下**尚未验证，不得泛化**：

- BPM change、任意 angle / 旋转方向、Twirl、Midspin、hold、event-heavy chart
- checkpoint start、特殊 startup
- 任意 pitch / FPS / 更长的 targetFrameCount / arbitrary-length formal session
- canonical level completion、tail policy
- UI hiding（UMM 面板隐藏）、camera control、custom resolution / aspect / supersampling
- 音频输出、外部视频编码（FFmpeg）
- 完整非实时离线导出（Phase 4 门槛：实际逐帧导出 + PNG 序列 + wall-clock independence + 失败恢复，全部通过实机验证）

0.3.3.1 hardening 中新增的 watchdog（`playback-ready-timeout` / `capture-timeout`）路径为静态/build 验证，**runtime timeout injection 未执行**。

当前交互事实需区分两条路径：Renderist 自己的 Stop button 只存在于 UMM `OnGUI`；用户实际 manual stop 使用的是 ADOFAI 原生 `Esc`，且原游戏也只有这一种停止交互路径。Renderist 源码没有拦截 `Esc`，只在 scheduler 中观察 editor playMode / controller 状态。实机样本 `editor_20260909_215606` 已证明 Esc 可在 92/92 停止、cleanup 不产生 `capture-stop`，且后续可再次完整运行；旧代码的 `Failed / playback-stopped-unexpectedly` 只是分类错误，当前源码改为在 player 存活且无 controller Fail/Fail2 时使用 `Cancelled / native-playback-stopped`，不写 `user-stop`。新分类仍待实机回归。源码也没有 UMM 窗口开关/暂停 API。用户打开 UMM 时的 `paused=true` 与 Stop 未触发之间的因果不作为本版本判断依据。Frame 0 包含 UMM overlay 是因为捕获发生在 UMM `OnGUI` 仍可见的 `WaitForEndOfFrame`，本轮不擅自 patch UMM UI。

音频已验证边界：audible music 跟随 wall-clock / realtime playback；audible beat / metronome 也跟随 wall-clock；MasterTimeline 当前拥有 deterministic visual/gameplay logical time，但尚未拥有 audible audio timeline。这**不是** 0.3.3.1 blocker；本轮不修改 audio（不 patch AudioSource、不 seek/mute/timestretch、不引入 AudioRenderer/offline audio/FFmpeg）。180 output frames / 60 FPS 是约 3 秒视觉逻辑 timeline，而实际导出 wall-clock 约 28–29 秒。

---

## 9. 0.3.3.1 closure 状态

`0.3.3.1 lifecycle closure achieved`（A/B/C/D 全部 runtime PASS），`0.3.3.1` 已冻结。

剩余未验证项见第 8 节（BPM change / hold / Twirl / Midspin / checkpoints / special startup / arbitrary pitch / arbitrary FPS / long-duration export / event-heavy charts / canonical level completion / tail policy / arbitrary-length formal session / UI hiding / camera control / custom resolution & aspect / audio export / FFmpeg / replay）。

watchdog（`playback-ready-timeout` / `capture-timeout`）路径为静态/build 验证，未做 runtime timeout injection。

下一步：等待用户确认是否升级前三位 `0.3.3.1 → 0.3.4.0`；`0.3.3.1` 已冻结，不开始 0.3.4.0 实现。Phase 4.0.0 的完整非实时逐帧导出、wall-clock independence 与失败恢复门槛仍未满足。
