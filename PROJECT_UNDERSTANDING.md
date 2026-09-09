# ADOFAI Renderist 项目理解

> **维护权说明**
>
> 本文件由参与项目工作的 Agent 按 `AGENTS.md` 和实际证据共同维护，用于记录 ADOFAI Renderist 的当前事实、阶段进度、工程边界和下一步接续点。
>
> DSH、GPT Work 和其它参与 Agent 均须读取本文件；是否修改以 `AGENTS.md`、当前用户要求和实际证据为准。
>
> 本文件是跨会话项目上下文的唯一共享来源之一，但**仓库中的实际代码、AGENTS.md、构建脚本和当前 git 状态始终是执行时事实来源**。若二者冲突，DSH 必须停止假设并报告差异，不得自行改写本文件来“修正上下文”。

---

## 1. 项目定位

ADOFAI Renderist 是面向 **A Dance of Fire and Ice（ADOFAI）** 的 **Unity Mod Manager（UMM）模组**。

核心目标：

> 在 ADOFAI 编辑器环境中实现可控、可复现的高质量导出，最终发展为非实时逐帧渲染流程。

当前路线强调：

- 优先编辑器内导出
- 优先脱离 replay 依赖
- Route B deterministic core PoC 已 PASS：普通 floor0 MasterTimeline baseline 已完成完整 180-frame 实机验证（RUNTIME VALIDATED PASS，限定范围见下文）；当前阶段为 `Phase 3.2.0`，正式非实时离线导出尚未完成。
- replay / 第三方 replay Mod 适配延后
- 音频与视频编码后置
- 仅支持 UMM

阶段方向：

1. 实时截图 / 序列截图：验证基础输出能力
2. 编辑器导出前置能力与会话骨架
3. 编辑器真实捕获后端
4. 时间控制 / 可控帧推进
5. 相机 / UI / 编辑器状态控制
6. 非实时逐帧导出闭环
7. 音频与外部编码
8. replay / autoplay 扩展适配

禁止将 replay 作为当前主路线前提。

---

## 2. 当前工程基线

| 项目 | 当前基线 |
| --- | --- |
| Mod ID / DLL / Namespace | `ADOFAI.Renderist` |
| 当前 PoC 版本 | `0.3.3.0`（MasterTimeline Deterministic Gameplay Handoff；普通 floor0 baseline 已 runtime PASS） |
| 当前阶段 | `Phase 3.2.0 — Route B 正式非实时离线导出前的内部行为调查、PoC 与实现准备` |
| ADOFAI | **`Assembly-CSharp.dll` `0.4.3.0`（本机 Steam 安装已确认）** |
| Unity | **`6000.3.10f1`（当前本机安装基线）** |
| Unity 运行形态 | **Mono（当前安装存在 `MonoBleedingEdge/`）** |
| UMM | **`0.33.0`（最新稳定主文件目标基线）** |
| Harmony | **`2.3.6.0`（本机 UMM 0.33.0 实际 `0Harmony.dll` 已确认）** |
| TargetFramework | `net48` |
| LangVersion | `9.0` |
| License | Apache-2.0 |

版本同步位置：

- `mod/Info.json`
- `src/ADOFAI.Renderist/ADOFAI.Renderist.csproj`
- `src/ADOFAI.Renderist/ModEntry.cs`

版本变更必须优先使用：

```powershell
scripts/set-version.ps1
```

---

## 当前 Phase 3.2.0 状态（长期基线）

- 0.3.3.0 当前仍属于 Phase 3.2.0 的 MasterTimeline Deterministic Gameplay Handoff，不等于正式离线渲染发布。只有完整实现并经过实机验证的非实时逐帧离线导出才进入 `Phase 4.0.0`；0.3.2.0 是前一生命周期调查基线。
- Official Autoplay 指 ADOFAI 原生 `RDC.auto` / PlayerControl 路径。它仍提供官方 `Hit` 行为和正常 floor 状态迁移，但 Official-Autoplay-only 已否定为 deterministic production authority。
- `RenderistAutoPlay` 是当前 Route B 候选 gameplay advancement authority：Master Timeline 决定 floor 是否 due，然后调用官方 `scrPlayer.Hit(isAuto:true)`；不重写 ADOFAI Hit 行为，不等同于旧 EditorVisualClockPoc DVA。
- `0.3.3.0` 已将 session-owned lifecycle handoff 接入 MasterTimeline Deterministic Gameplay Handoff；当前实现包含 FrameIndex-driven timeline、forced Conductor bridge、Conductor Postfix CatchUp、due-floor、官方 `scrPlayer.Hit(true)` 与现有 EOF capture/commit 链。普通 floor0 baseline 已完成完整 180-frame 实机验证并整体 PASS（范围见下文「runtime 验证范围」）。
- 当前 DLL 的 `MonsterLove.StateMachine.StateEngine.Changed` 已静态确认为公开 `event Action<Enum> Changed`；PoC 在 `Play()` 前从当前 `scrController.stateMachine` 取得该实例并订阅，只接受仍属于当前 controller state machine 的 commit。
- `scrController.OnMusicScheduled()` 以独立 Harmony owner 的 Postfix 只观察、不改变 original；没有加入 `Start_Rewind` patch，因为 `StateEngine.Changed + OnMusicScheduled` 已覆盖本轮需要验证的启动关联信号，是否需要辅助 marker 留到 runtime 证据。MasterTimeline marker 使用 UMM `ModEntry.Logger`，实际输出在 Unity/ADOFAI `Player.log`，不会自动生成独立的 MasterTimeline 日志文件。
- lifecycle PoC 的 startup scope 由 Renderist 自己创建并关联本次 `scnEditor.Play()`；`scopeActive`、`playRequested`、`playReturned` 只是本地 correlation context，不是 ADOFAI 官方 playback generation/session token。
- 独立 Playback lifecycle readiness 已根据用户提供的普通 floor0 runtime trace 判定为 PASS：Renderist-owned `editor.Play()` 后依次观察到 `Start`、`OnMusicScheduled`、`Countdown`、`PlayerControl`，并完成 cleanup；`Start` 可以由 handoff 建立时的 current-state snapshot 满足，不能要求请求后再次发生相同的 `Changed(Start)`。最新日志进一步确认 session-owned ready gate 已通过并进入 Frame 0。
- 旧 DVA 仅保留历史诊断价值，不直接迁移生产。
- Phase 3.2.0 的 MasterTimeline ownership 已确定为 FrameIndex；Unity `AudioRenderer` 不拥有逻辑时间，只保留 future slave / validator 方向。不输出最终音频文件、不做 FFmpeg / Video/VFX bridge。
- `T_N=N/FPS`、AudioRenderer slave / validator、Official Hit、floor/currentSeqID 和两次 90 logical frames comparison PASS 属于已确认的 PoC 事实；不等于正式离线导出已完成。
- 普通 floor0 的 chart 基准仍是 `floors[0].entryTime = 0.0`，但 native 进入 `PlayerControl` 时已处于 countdown 之后的 gameplay 时间。当前 DLL 静态证明 `Countdown_Update` 的条件是 `beatNumber >= adjustedCountdownTicks`，而 `scrConductor.Update` 从 `nextBeatTime=0` 开始，因此理论阈值对应 `floor0.entryTime + max(0, adjustedCountdownTicks - 1) × crotchetAtStart`；`adjustedCountdownTicks = countdownTicks / countdownSpeedMultiplier`、`crotchetAtStart = 60 / bpm`。pitch 在 native countdown 调度和 `songposition_minusi` 计算中抵消，不在此处重复乘除。上一版 `adjustedCountdownTicks × crotchetAtStart = 1.411765` 已被实机否定，并且该值恰好是 `floor1.entryTime`，不是 gameplay-start anchor。
- `HitScope`、临时 `RDC.auto`、`keyTimes` / multipress 清理和 Planet `targetExitAngle → cachedAngle` 的通用适用范围尚未实机定案。
- `calibration_i=0` 目前仍只是候选 deterministic bridge 策略；其属性和调用来源已可静态确认，但“生产必须固定为 0”的必要性尚未独立实机确认。当前已实施的最小角度桥接是：forced capture 期间仅对当前 `playerOne` 抑制 `AsyncInputUtils.AdjustAngle(scrPlayer, ulong)`，使其不能用 `AsyncInputManager.targetSongTick` 覆盖 native Conductor 的 forced Planet 角度；Countdown / lifecycle hold 期间不抑制。
- 已退役的 `CanonicalStartupProbe` v1 运行结果确认：Play 前的 `controllerState` 可能继承此前编辑器 / 游戏会话；`scnEditor.Play()` 返回后旧状态也可能短暂保留，随后才经过 `Start` / `Countdown`。因此 Play 返回值和单一旧状态都不是 canonical playback boundary。
- 已退役的 `GameplayHandoffProbe` 实机 trace 已确认：普通 floor0、`RDC.auto=false`、无输入且不使用 RenderistAutoPlay 时，官方流程会自然完成 Countdown 并进入 `PlayerControl`。旧 `CanonicalStartupProbe` v1 的 120 Update watchdog 确实不足以覆盖该测试谱面的完整启动周期（首次 Countdown 约 frame 12527，首次 `PlayerControl_Update` 为 frame 12800），所以旧 probe 未观察到 `PlayerControl` 不能作为 handoff 缺失证据。`hasSongStarted=false` → `true` 仍明显早于 `PlayerControl`，不能单独定义 gameplay readiness。
- 当前 0.3.3.0 实际调用链为 `FrameIndex N → forced scrConductor.Update Prefix → native Conductor.Update → Conductor Postfix → RenderistAutoPlay.CatchUp → 官方 scrPlayer.Hit(true) → downstream gameplay/Planet → WaitForEndOfFrame → commit N`。启动时保持 `RDC.auto=false`，仅在 due-hit 的官方 Hit 调用期间临时置为 `true`。lifecycle-ready、Frame 0 transaction 与 EOF/commit 已取得 runtime evidence；普通 floor0 baseline 的跨 gameplay 完整 180-frame 链路已 runtime validated。
- `EditorExportController.Start()` 在 `TryStart()` 只要成功返回后就把 UI session 标为 `Running`；此时 scheduler 仍处于 `InitializationHold`，所以 UI `Running` 不等于 lifecycle-ready。`InitializationHold` 的 ready gate 实际要求 `PlayRequested`、`PlayReturned`、本次 handoff 建立时或之后确认的 `Start`、`OnMusicScheduled`、`Countdown`、`PlayerControl`、当前 state 为 `PlayerControl`、player alive 且未 pause。
- gameplay-start anchor 的 `native-gameplay-anchor-mismatch` 与大幅 Planet rewind 已消除：最终验证运行中 `lifecycleReadySongPosition=1.074622`、`gameplayStart=1.058824`，差值约 `0.015798`，通过 `0.05` tolerance；Frame 0 完整经过 Conductor Prefix/Postfix、AutoPlay、EOF、PNG、FrameBoundary 与 COMMIT。旧版本 Frame 6/7 early death 的真实原因确认为异步角度覆盖，修复后的完整 180-frame 运行已确认不再出现。
- Startup Probe v1 已完成“发现启动状态污染、确认 Play 返回不可靠、确认 `hasSongStarted` 非边界”的任务；不应通过机械扩大 watchdog 或修改现有探针来替代 handoff 调查。Official Autoplay reference probe 仍只能作为诊断手段，不能改变生产 authority 决策。
- 当前 DLL 静态状态机显示：`OnMusicScheduled` 在普通 floor0、非 `forceNoCountdown` 情况下设置 `Countdown`；`Countdown_Update` 在 `beatNumber >= adjustedCountdownTicks`，或非 gameworld / `forceNoCountdown` 时直接请求 `PlayerControl`。`beatNumber` 在 `scrConductor.Update` 中按 `songposition_minusi > nextBeatTime` 递增，`nextBeatTime` 从 `0` 开始并每次增加 `crotchetAtStart`；因此普通 floor0 的理论 gameplay-start 阈值是 `floor0.entryTime + max(0, adjustedCountdownTicks - 1) × crotchetAtStart`。当前运行实际得到 `countdownOffset=1.058824`；此前“`adjustedCountdownTicks=2`、offset≈`0.705882`”不是已验证的本次运行值，不得继续作为事实。第一 Hit 不负责把普通流程从 Countdown 推入 PlayerControl。
- `RenderistAutoPlay` 的 `PlayerControl` gate 是 Renderist PoC 自己的安全门，不是 `scrPlayer.Hit(bool)` 的官方硬性要求。`Hit(true)` 不检查 controller state，且 `HitInputEvent(true, ...)` 静态上绕过物理输入。最终验证运行中 due-floor Hit 顺序推进为 floor0→floor1（Frame22）至 floor7→floor8（Frame170）共 8 次：每次 `due=true`、`hitResult=True`、`hitsThisFrame=1`、afterFloor 正确、无 duplicate Hit；`nextFloor` 超出 180-frame 范围时未 Hit 属正常。一次性 `scrPlayer.Die` DeathDiagnostic 已完成使命并从正式路径删除。
- 当前 DLL 的 `scrPlayer.CheckPostHoldFail(Nullable<UInt64>)` 使用方向性的未归一化差值：`delta = chosenPlanet.angle - chosenPlanet.targetExitAngle`，`!isCW` 时取反；普通非安全 floor 的 `maxMargin = max(PI, _minAngleMargin * 2)`，`delta > maxMargin` 时调用官方 `Die(false, false, "", false)`（missed-tile 语义，普通情况约允许错过后继续 π rad / 180°）。没有 `DeltaAngle`、modulo 或 wrap，不是应通过 shortest-angle 修复的游戏逻辑错误。
- early death 的 Renderist 根因确认为：`CheckPostHoldFail` 在有 `targetTick` 时先调用 `AsyncInputUtils.AdjustAngle`；该路径通过 `AsyncInputManager.targetSongTick` 与 `GetSongPosition` 调用 `scrPlanet.AsyncRefreshAngles`，而不是读取 Renderist 强制的 `conductor.songposition_minusi`。因此 forced Conductor 只控制 native `Update_RefreshAngles` 路径，随后异步 tick 路径又覆盖同一 `chosenPlanet.angle`。当前生产修复在 forced capture、当前 `playerOne` 范围内抑制 `AsyncInputUtils.AdjustAngle(scrPlayer, ulong)` 该入口，不修改 `CheckPostHoldFail` / `Die` / `Fail`；该修复已通过修复后的完整 180-frame 实机结果确认解决。
- 对当前普通 floor0 路径，"必须 Hit 才能进入 PlayerControl，但 Renderist 必须等 PlayerControl 才 Hit" 的 startup handoff deadlock 已被 runtime 排除（RUNTIME EXCLUDED）。官方首次 `PlayerControl_Update` 到 `Die` 相隔约 111 Unity frame，当前 `RenderistAutoPlay` 的 `PlayerControl` gate 不是 startup blocker；该结论不外推到 Hold、Midspin、Twirl、特殊 startup 或 checkpoint start。参考 ChartRendering 也采用 `RDC.auto=false` 启动编辑器播放并在 `controller.state == PlayerControl` 后才运行 AutoPlayer；该实现仅作为参考证据，不替代当前 DLL 结论。
- 最新 deterministic core comparison 只比较 gameplay / Planet 等字段，未比较 `controllerState`：一次 deterministic phase 起点为 `Countdown`，另一次为 `PlayerControl`，随后第二次短暂出现 `Start` / `Countdown`，但 frame 0–89 的既有比较仍全部相同。因此 core PoC PASS 不代表 startup controller state 已经 canonical。
- `Bootstrap Ready` ≠ `Frame 0 Canonical State` 现在获得了普通 floor0 runtime 支持的分层：Bootstrap Ready 表示本次 session-owned Play 已完成、controller 已进入真实 `PlayerControl` lifecycle、player alive、not paused、floor0 基础状态有效且未 Fail；随后才建立 gameplay-start anchor 并激活 forced clock。该分层、anchor mismatch guard、Frame 0 transaction 和 EOF/commit 均已有 runtime evidence；异步角度覆盖修复为 IMPLEMENTED / RUNTIME VALIDATED（普通 floor0 baseline），跨 gameplay 帧不再 early death 已确认。不加入 `hasSongStarted=true`，也不把 `currentSeqID == playerFloor` 写成通用 equality invariant。
- Handoff Ordering Probe runtime 已确认：`ChangeState(PlayerControl)` request 是 deferred，ChangeState Postfix 仍可观察到 `Countdown`；随后下一帧仍为 Countdown，再下一帧的 `scrConductor.Update()` Prefix 首次看到 `PlayerControl`。该 Unity frame 内顺序为 `Conductor.Update` → Renderist `OnUpdate` → `PlayerControl_Update`，因此在 Renderist `OnUpdate` 才激活 forced clock 存在 runtime-confirmed late-tick 风险；Conductor 已在该 marker 前消费了 realtime songposition。普通 floor0 的最强 handoff 候选点是首个满足 `PlayerControl`、player alive、not paused 且 session active 的 `Conductor.Update Prefix`，但仍是 `RUNTIME-SUPPORTED CANDIDATE`，不是 production-final boundary。
- `GameplayHandoffProbe` 已完成 Handoff Ordering 调查使命，现已删除代码和 GUI；其 ring-buffer 结论保留在本文件，不再要求重复运行。
- 最小 `DeterministicHandoffPoc` 的历史实机运行只能判定为 `PARTIAL PASS`：activation 曾在 Play 请求同一帧的遗留 `PlayerControl` 上提前发生，随后才观察到本次启动的 `Start` / `Countdown` / 新 `PlayerControl`。该代码和 GUI 已删除；其 fresh-bootstrap heuristic 结论仅保留为 diagnostics-only 历史事实，不作为 production boundary。
- 新 HandoffTrace 已确认 `Die` 与 `FailAction` 可在同一 Unity frame 连续发生；`Fail2Action` 被调用时 trace 中 `controllerState` 仍为 `Fail`，因此不能仅凭方法名宣称已观察到 `state == Fail2`。这些自然失败事实不是当前 startup blocker。
- 当前 `Assembly-CSharp.dll` `0.4.3.0` 的完整静态链已确认：编辑器 `scnEditor.Play()` 在 editor 路径中调用 `scnGame.Play(startFloor)`；后者依次执行 conductor/controller rewind、`scrController.Awake_Rewind()` 请求 `Start`、`scrConductor.Start()`、`scrController.Start_Rewind()`，而 `Start_Rewind()` 启动 `StartMusic(PostSong, OnMusicScheduled)` 协程。音乐 schedule 回调再为普通 floor0 请求 `Countdown`（`forceNoCountdown` 时请求 `PlayerControl`），`Countdown_Update()` 在 countdown beat 条件满足后请求 `PlayerControl`。
- 当前官方实现不存在可直接读取的 playback generation/session token。`waitForStartCoCallCount` 只是使 `WaitForStartCo` 的旧实例失效的递增计数，并不是 `editor.Play()` generation；`hasSongStarted` 也只是音频/DSP 时序标志。全程序集写入点为 `scrController.Start_Rewind`、checkpoint 分支的 `scrController.OnMusicScheduled`、`scrConductor.Rewind` 和 `scrConductor.ToggleHasSongStarted`，不能单独作为 fresh playback marker。
- `MonsterLove.StateMachine.StateEngine.ChangeState()` 只设置 destination 并启动 deferred `ChangeToNewStateRoutine`；真正 commit 在该 routine 设置 `currentState`、完成 Enter 后发生，并随后触发公开的 `Changed` event。正式 startup 设计应由 RenderSession 自己发起 `Play()`，结合预期的官方 lifecycle 请求与 `StateEngine.Changed` commit 观察建立 session-owned boundary；不能用旧 `controller.state`、`hasSongStarted` 或 `Countdown OR PlayerControl` 代替。已退役 Probe 的 Start/Countdown fresh-bootstrap heuristic 仅保留为历史 diagnostics 结论，不是 production boundary。
- 参考仓库 `ADOFAI.EditorTweaks.ChartRendering` main（研究快照 commit `7e79127bfa5b89496b911215817f401eb9c6acd0`）的 `ChartRenderPlaybackController.IsPlaybackScheduled()` 仍是 `hasSongStarted || state == Countdown || state == PlayerControl`，未使用 generation、`StateEngine.Changed` 或新的 Play-session marker；因此它没有真正解决 Renderist 已发现的 stale startup，startup readiness 弱于正式 Route B 需求。可借鉴的是 visual clock / auto-player / capture 的职责分离，而不是该 readiness 条件。
- Route B 的 MasterTimeline Deterministic Gameplay Handoff 已实现：`FrameIndex N → T_N=N/OutputFps → Conductor.Update 前暴露 forced chart time → 原生 Conductor.Update 消费 T_N → Conductor.Update Postfix 执行 due-floor CatchUp 并调用官方 Hit(true) → downstream gameplay/Planet/camera/VFX → WaitForEndOfFrame capture → commit N → N++`。Postfix 是当前证据最强的 Hit 位置；Renderist OnUpdate 不承担时间或 Hit authority。
- 参考实现以 `ChartUnityAudioCapture.CapturedSeconds` 驱动下一帧 visual clock，并以 `index/fps` 作为 fallback；Renderist 正式目标保持相反 ownership：`T_N` 由 FrameIndex 计算，AudioRenderer 只作 offline audio slave / validator。当前 DLL 的 songposition 计算已将 pitch 纳入 conductor chart time，正式映射只允许对 output seconds 乘 pitch 一次：`forcedSongPosition_N = gameplayStart + (N/OutputFps) * pitch`；`gameplayStart` 是一次性由 native countdown 参数确定的 gameplay handoff anchor。

0.3.3.0 MasterTimeline Deterministic Gameplay Handoff 的最小闭环为：

```text
FrameIndex N
  ↓
outputTime_N = N / OutputFps
  ↓
forced chartTime_N = canonicalStart + outputTime_N × pitch
  ↓
Forced Conductor → native Update → Postfix CatchUp
  ↓
due-floor → 官方 scrPlayer.Hit(true)
  ↓
downstream gameplay / Planet / camera / VFX
  ↓
WaitForEndOfFrame FrameBoundary → commit N
```

状态标签：上述链路、gameplay-start anchor 与 `AsyncInputUtils.AdjustAngle` forced-capture suppression 为 IMPLEMENTED；普通 floor0、pitch=1、60 FPS、180-frame baseline（Official AutoPlay OFF、`RDC.auto=false`、无人工输入）的 lifecycle handoff、Frame 0 transaction、EOF/PNG/commit、due-floor Hit 进展（floor0→floor8 共 8 次 due Hit）、全程 `playerAlive=true` / `controllerState=PlayerControl` 与 `stopReason=target-frame-count-reached` / 终态 Completed 均已实机确认，整体 MasterTimeline 当前为 RUNTIME VALIDATED PASS（仅限该 baseline，不泛化到未测谱面类型）。当前实现同时拒绝从 `Fail/Fail2` 状态启动。

当前 DLL 事实（ADOFAI `Assembly-CSharp.dll` 0.4.3.0，Unity 6000.3.10f1）：`scrConductor.Update()`、`scrController.PlayerControl_Update()`、`scrController.LateUpdate()`、`scrPlayer.Hit(bool)`、`scrPlanet.Update_RefreshAngles()` 与 `AsyncInputUtils.AdjustAngle(scrPlayer, ulong)` 均可由当前实现按签名解析；MasterTimeline runtime 结果已按最近一轮完整 180-frame 实机日志完成验证（普通 floor0 baseline）。

---
## 3. Agent 与协作方式

当前本地编码 Agent：

> **DeepSeek Harness（DSH）**

协作流程：

1. 网页版 GPT 负责：
   - 项目规划
   - 阶段拆分
   - 实施/审计提示词
   - 技术路线审查
   - 风险判断
   - 维护本文件
2. DSH 负责：
   - 读取 `AGENTS.md`
   - 读取本文件
   - 检查仓库当前状态
   - 按提示词执行分析或实现
   - 构建 / 静态验证
   - 输出实施或审计报告
3. 用户将 DSH 报告交回网页版 GPT。
4. 网页版 GPT 审查后：
   - 决定是否进入下一步
   - 必要时更新本文件
   - 再生成下一轮 DSH 指令

DSH 不拥有阶段规划权，也不得自行宣布进入下一 Phase。

---

## 4. 仓库结构与职责

```text
AGENTS.md
    DSH 的仓库执行约束和 Agent 行为规范。

PROJECT_UNDERSTANDING.md
    本文件。由参与项目工作的 Agent 按实际证据维护。

Directory.Build.props
    .NET / C# 全局构建配置，并导入本地 local.props。

mod/Info.json
    UMM 元数据。

src/ADOFAI.Renderist/
    Mod 主源码。

references/
    仅本地引用占位与说明，不提交 DLL。

build/local.props
    本地生成，不入库。

scripts/
    引用准备、构建、部署、打包、验证、版本同步脚本。

dist/
    发布产物，本地使用，不入库。
```

发布包固定为：

```text
ADOFAI.Renderist.zip
├── Info.json
├── ADOFAI.Renderist.dll
└── LICENSE
```

禁止提交：

- 游戏 DLL
- Unity DLL
- UMM DLL
- Harmony DLL
- 第三方 DLL
- `dist/`
- `.build-tools/`
- `build/local.props`
- `references/` 中的实际 DLL

---

## 5. 当前源码模块

### 5.1 `ModEntry.cs`

职责：

- UMM `Load`
- `OnToggle`
- `OnGUI`
- `OnSaveGUI`
- `OnUpdate`
- F9 / F10 热键分发
- Capture Tick
- Editor Export Tick
- Preflight / EditorEnv / Readiness 缓存刷新
- Phase 2.4 编辑器导出 GUI 接入

注意：

- 当前可以实例化 Harmony，但 Phase 2.4 不使用 Patch。
- 普通 F9/F10 路径必须保持与 Phase 2.3 一致。

### 5.2 `Settings.cs`

UMM XML 持久化设置。

已有关键字段包括：

- OutputDirectory
- FilenamePrefix
- ZeroPadWidth
- SequenceStartIndex
- CaptureSuperSize
- CaptureEveryNFrames
- TargetCaptureFps
- MaxFramesPerSession
- EditorExportEnabled
- EditorTargetFrameRate
- HotkeysEnabled
- SingleCaptureHotkey
- SequenceHotkey
- VerboseLogging

Phase 2.4 没有新增 Settings 字段。

### 5.3 Capture 模块

#### `CaptureService.cs`

Phase 2.0 的实时截图核心。

负责：

- F9 单帧
- F10 序列
- `ScreenCapture.CaptureScreenshot`
- 实时节流
- 序列停止
- 请求计数 / 文件探测

重要边界：

> `CaptureService` 当前属于实时截图路径，不应直接演化成非实时编辑器导出控制器。

#### `OutputPath.cs`

负责：

- 输出目录解析
- 安全校验
- 会话目录创建
- 危险路径拒绝
- fallback

Preflight 使用无副作用校验接口。

#### `Metadata.cs`

负责现有 F9/F10 metadata。

Phase 2.4 为避免影响现有格式，没有强行扩展此路径。

---

## 6. Export 模块

### 6.1 Phase 2.2 / 2.3 基础

现有：

- `Preflight.cs`
- `PreflightReport.cs`
- `ExportCoordinator.cs`
- `ExportState.cs`
- `ExportStateMachine.cs`
- `EditorEnv.cs`
- `EditorExportPreflight.cs`
- `EditorExportReadiness.cs`

职责：

- 输出前检查
- F9/F10 协调
- 编辑器环境诊断
- 编辑器导出就绪判断

当前编辑器识别仍依赖：

```text
scnEditor
```

这是启发式白名单，不是已确认 ADOFAI 内部 Editor API。

### 6.2 Phase 2.4 新增

#### `EditorExportState.cs`

状态：

- `Idle`
- `Preparing`
- `Running`
- `Cleaning`
- `Completed`
- `Cancelled`
- `Failed`

终止状态保留供 GUI 查看，不立即自动重置为 Idle。

#### `EditorExportSession.cs`

保存编辑器导出骨架会话信息。

当前只记录真实存在的信息，例如：

- sessionId
- 创建/结束时间
- 输出目录
- sceneName
- state
- stateDetail
- stopReason
- tickCount
- captureRequestCount
- capturedFrameCount
- captureImplemented

当前约束：

```text
captureImplemented = false
captureRequestCount = 0
capturedFrameCount = 0
```

`tickCount` 只是会话 Update 次数，不能称为导出帧数。

#### `EditorExportCaptureDriver.cs`

未来捕获后端占位。

当前：

- 不调用 `ScreenCapture`
- 不调用 F9/F10
- 不产出图片
- `TryEmitFrame()` 不应产生副作用
- Controller 当前不应使用它执行捕获

#### `EditorExportController.cs`

职责：

- `Start`
- `Stop`
- `Cancel`
- `Tick`
- 会话状态管理
- Preflight 校验
- 输出目录准备
- metadata 生命周期
- 编辑器环境失效时终止
- 与 F9/F10 的会话互斥

当前 Phase 2.4 的 `Tick()`：

- 只维护生命周期
- 只增加 TickCount
- 不截图
- 不推进时间
- 不控制相机
- 不操作 ADOFAI 内部游戏状态

### 6.3 Diagnostics 基线

当前 UMM 面板保留的正式入口为 `MasterTimeline Deterministic Gameplay Handoff`，复用现有 `EditorExportController`、FrameCaptureDriver 和 cleanup 路径；普通 floor0 baseline 已 runtime PASS，但不代表正式离线导出已完成。`Diagnostics/` 目录已无任何运行时 PoC / Probe（相关文件全部退役并删除）。

已完成使命并退役的 diagnostics 代码：

- `CanonicalStartupProbe.cs`
- `GameplayHandoffProbe.cs`
- `DeterministicHandoffPoc.cs`
- `EditorTimeProbe.cs`
- `EditorVisualClockPoc.cs`
- `OfflineAudioClockPoC.cs`
- `PlaybackLifecyclePoC.cs`

这些 Probe/PoC 的 runtime/static 结论已转入本文件，旧 GUI 入口、Tick wiring、Harmony patch 与互斥引用均已移除。

---

## 7. Phase 进度（Phase 2 为历史记录）

### Phase 2.0 — 已实现

- F9 单帧截图
- F10 实时序列截图
- metadata
- every-N 节流
- target FPS 节流
- maxFrames 控制

### Phase 2.1 — 已实现

- 中文 GUI / 文案基线

### Phase 2.2 — 已实现

- Preflight
- ExportCoordinator
- 输出目录安全检查
- 目录浏览
- 单帧拒绝状态

### Phase 2.3 — 已实现

- EditorEnv
- EditorExportPreflight
- EditorExportReadiness
- 编辑器导出就绪检测

### Phase 2.4 — 历史记录（不代表当前阶段）

已完成代码实现：

- EditorExportController
- EditorExportSession
- EditorExportState
- EditorExportCaptureDriver
- 最小 GUI
- 生命周期接入
- F9/F10 基本互斥
- editor-export-skeleton metadata
- 版本升级至 `0.2.4.0`

已完成：

- Debug build
- Release build
- package-release
- verify-release-package
- 静态搜索
- Phase 2.4 定向只读代码审计

审计结论：

> **首次定向审计曾判定 C. 不通过；两项缺陷已完成修复，且修复复验已通过。当前允许进入 Phase 2.4 游戏内实机验证。**

已确认需要修复：

1. **阻断：Editor Export 会话目录同秒碰撞**
   - 当前 sessionId / 目录名基于 `editor_<yyyyMMdd_HHmmss>`。
   - 同一秒内 `Stop → Start` 会静默复用已有目录。
   - 新会话会覆盖上一会话 `metadata.json`。
   - 必须保证快速重启不会复用或覆盖上一会话目录/metadata。

2. **中等：运行中输出目录复核使用了全局 Settings**
   - 当前每 60 tick 校验 `settings.OutputDirectory`。
   - 当前会话实际输出仍固定在 `Session.OutputDirectory`。
   - 如果用户运行中修改全局输出目录为非法路径，会错误取消健康的当前会话。
   - 应改为校验当前会话固定的 `Session.OutputDirectory`。
   - 新 Settings 只能影响下一次 Start。

审计已确认通过：

- metadata JSON 转义正确，可被 `ConvertFrom-Json` 解析
- 状态机不会卡在 Preparing / Cleaning
- Stop / Cancel 收尾基本幂等
- UMM 回调异常有保护
- `captureImplemented=false`
- `captureRequestCount=0`
- `capturedFrameCount=0`
- Phase 2.4 没有真实截图、时间控制、相机控制、replay 或 Harmony Patch
- 受保护文件未被修改

因此：

> **Phase 2.4 当前状态：实现完成 → 首次定向审计未通过 → 最小修复完成 → 修复复验通过 → 当前进入游戏内实机验证。**

---

## 7.5 Phase 2.4 缺陷修复实施结果

DSH 已按批准范围实施最小修复，当前工作区存在 3 个未提交目标文件：

```text
src/ADOFAI.Renderist/Capture/OutputPath.cs
src/ADOFAI.Renderist/Export/EditorExportController.cs
src/ADOFAI.Renderist/UiText.cs
```

当前不创建 commit，等待修复复验。

### A. 会话目录唯一性修复

已新增 Editor Export 专用唯一目录解析逻辑：

```text
<baseName>
<baseName>_001
<baseName>_002
...
```

行为目标：

- 已存在目录不作为新 Session 静默复用
- 同秒连续 Start 获得不同目录
- 新会话不得覆盖旧 `metadata.json`
- `sessionId` 使用最终真实目录名，包含冲突后缀
- 原 `ResolveSessionDirectory` 不修改，F9/F10 路径保持原语义

DSH 临时测试报告显示：

```text
editor_20260902_130658
editor_20260902_130658_001
editor_20260902_130658_002
```

三次快速会话目录互不相同，上一会话 metadata 未被覆盖。

注意：

> 当前实现采用 `Directory.Exists` 探测后再 `Directory.CreateDirectory`。在 ADOFAI 单进程、主线程 Start 流程下可接受；修复复验仍需确认不存在代码级静默复用路径。

### B. Session 输出目录固定性修复

运行中每 60 tick 的目录健康检查已从：

```text
settings.OutputDirectory
```

改为当前 Session 固定目录：

```text
s.OutputDirectory
```

当前实现检查：

- 路径非空
- `Directory.Exists(sessionDir)`

因此：

- 当前 Session 不会因为用户修改全局 Settings 输出目录而被误取消
- 当前 Session metadata 仍写入自身固定目录
- 新的 Settings 仅影响下一次 Start

### C. 本次修复未触碰

- `CaptureService.cs`
- `ExportCoordinator.cs`
- `Metadata.cs`
- `Settings.cs`
- 版本号
- UMM 基线
- Harmony / Assembly-CSharp / replay / 时间控制 / 相机控制

### D. 构建与打包

DSH 报告：

- Debug build：通过
- Release build：通过
- `package-release.ps1 -Force -SkipBuild`：通过
- `verify-release-package.ps1`：11 checks / 0 failures

完整 `package-release.ps1 -Force` 因沙箱无法读取用户级 NuGet.Config 未完成，因此当前只认定为：

> **源码构建通过 + SkipBuild 打包验证通过**

不能将其表述为“完整打包脚本在当前沙箱环境全流程通过”。

---

## 7.6 Phase 2.4 修复复验结果

DSH 已完成定向只读复验，结论：

> **A. 修复复验通过，可以进入 Phase 2.4 实机验证。**

已确认：

- Editor Export 唯一目录逻辑只服务于 Editor Export。
- 已存在目录不会被新 Session 静默复用。
- 同秒快速 Start/Stop 会得到：
  - `editor_<timestamp>`
  - `editor_<timestamp>_001`
  - `editor_<timestamp>_002`
- 旧 Session `metadata.json` 不会被后续 Session 覆盖。
- `sessionId` 与最终实际目录名一致。
- 在当前项目调用模型下，Editor Export Start 只从 UMM/Unity 主线程 GUI 路径进入；不存在项目内部并发调用，因此 `Exists → CreateDirectory` 的理论外部进程 TOCTOU 当前判为非阻断。
- 当前 Session 运行中只检查 `s.OutputDirectory`，不再读取新的 `Settings.OutputDirectory`。
- Settings 的输出目录修改只影响下一次 Start。
- Session 目录被删除时会在周期检查中 Cancel。
- Session 目录存在但突然不可写，当前只会在下一次 metadata 写入时暴露；此项为非阻断观察项。
- `UiText.cs` 的修改仅增加唯一名耗尽日志文案，不改变 GUI、状态机或 F9/F10 行为。
- `CaptureService.cs`、`ExportCoordinator.cs`、`Metadata.cs`、`Settings.cs` 等受保护文件无修改。
- `captureImplemented=false`、`captureRequestCount=0`、`capturedFrameCount=0` 保持不变。
- Debug / Release `--no-restore` 构建通过。
- `-SkipBuild` 打包和发布包验证通过。
- 完整 `package-release.ps1 -Force` 仍受 DSH 沙箱读取用户级 NuGet.Config 权限限制，因此不能描述为完整 pipeline 全流程通过。

当前不存在实机验证前的代码阻断项。

---

## 8. Phase 2.4 当前修复与复验门槛

Phase 2.4 定向审计已经完成，不再处于“待审计”状态。

### 8.1 实机验证前阻断修复

#### A. 会话目录唯一性

当前已确认：

```text
editor_<yyyyMMdd_HHmmss>
```

仅精确到秒，且 `Directory.CreateDirectory()` 会对已存在目录静默成功。

结果：

```text
Start
→ Stop
→ 同一秒再次 Start
→ 复用原目录
→ 覆盖上一会话 metadata.json
```

硬要求：

> 每次 Editor Export Start 必须获得独立会话目录；快速重启不得覆盖上一会话任何 metadata。

修复必须保持 F9/F10 目录行为不变。

优先采用 Editor Export 专用的最小唯一性方案，不要为此重构通用 Capture 路径。

仅增加毫秒精度不足以构成严格唯一性保证；目标目录已存在时必须避免静默复用。

#### B. 当前 Session 输出目录校验

当前已确认：

- metadata 始终写入固定的 `Session.OutputDirectory`
- 但每 60 tick 环境复核错误地校验了 `settings.OutputDirectory`

正确语义：

```text
当前 Session 启动后
→ 输出目标固定为 Session.OutputDirectory
→ GUI 修改 Settings.OutputDirectory
→ 只影响下一次 Start
```

运行中复核应针对当前 Session 的固定路径，不能因为用户修改下一次会话的设置而取消当前健康会话。

### 8.2 修复后必须复验

DSH 修复后必须至少验证：

1. 同一秒内连续创建两个 Editor Export 会话，目录不同。
2. 第一会话 metadata 不被第二会话覆盖。
3. 已存在同名目录不会被新会话静默复用。
4. sessionId 与实际会话目录语义一致。
5. 运行中修改全局 `Settings.OutputDirectory` 不会改变或误取消当前健康 Session。
6. 当前 Session 仍只写自己的 `Session.OutputDirectory`。
7. `captureImplemented=false`。
8. `captureRequestCount=0`。
9. `capturedFrameCount=0`。
10. F9/F10 原核心路径不因修复改变。
11. Debug / Release build、package、verify 继续通过。

修复复验已通过，当前已允许进入实机验证。

### 8.3 已确认的非阻断观察项

当前不要求在本轮修复：

- metadata 使用 `File.WriteAllText` 直接覆盖，不是原子替换；异常中断可能留下半截 JSON。
- Running 期间 tickCount 不周期性落盘。
- F9 single capture in-flight 没有完整互斥状态建模。
- GUI 中存在一个不可达的 editorExportBusy 告警分支。
- `references/LOCAL_SETUP.md` 的 Phase 文案可能陈旧。

---

## 9. 最新基线对齐状态（2026-09-02）

网页版 GPT 已基于当前官方/权威公开信息重新确定目标：

```text
ADOFAI v3.3.1
UMM 0.33.0
```

这是**目标基线**，仓库尚需由 DSH 执行对齐。

### 9.1 ADOFAI

此前项目基线：

```text
ADOFAI v3.2.0
```

最新正式版目标：

```text
ADOFAI v3.3.1
```

在修改仓库基线前，DSH 必须检查本机 Steam 安装并重新确认：

- 游戏实际版本 / build 信息
- Unity 引擎版本
- Mono / IL2CPP 运行形态
- `MonoBleedingEdge/`
- `GameAssembly.dll`
- Managed 目录
- 关键 Unity DLL 是否仍存在
- 现有引用准备脚本是否仍适配

如果本机尚未更新到 v3.3.1，不得把旧游戏文件探测结果写成 v3.3.1 的事实。

### 9.2 UMM

此前存在的仓库事实：

```text
Info.json ManagerVersion = 0.32.4
prepare-references.ps1 baseline = 0.32.4
本地 UnityModManager.dll = 0.32.4
```

此前项目目标曾为 0.32.5。

现在统一目标改为：

```text
UMM 0.33.0
```

DSH 必须基于实际 UMM 0.33.0 包或已安装目录确认：

- `UnityModManager.dll` 版本
- `0Harmony.dll` FileVersion
- UMM 注入/安装形态
- 0.33.0 是否仍适用于当前 ADOFAI Mono 环境
- `Info.json ManagerVersion` 应与项目“仅支持最新 UMM”策略对齐
- `prepare-references.ps1`
- `references/LOCAL_SETUP.md`
- 其他仓库内基线记录

不得根据旧 0.32.x 数据推断 0.33.0 的 DLL 版本。

### 9.3 Harmony

公开 UMM 变更记录显示 0.32.3 曾更新 Harmony 至 2.3.6，0.33.0 的公开变更项未声明 Harmony 更新。

但项目基线仍必须：

> 以 **UMM 0.33.0 实际包内 `0Harmony.dll` 的 FileVersion** 为最终事实。

在 DSH 实际读取前，本文件不把 `2.3.6.0` 声称为已确认的新基线。

### 9.4 Mod 自身版本（历史 Phase 2.4 记录）

当前：

```text
ADOFAI.Renderist 0.2.4.0
Phase 2.4 editor export skeleton
```

本次是开发环境 / 基线数据对齐，不改变 Renderist 功能。

变更级别：

> **小幅基线维护**

因此：

- 不升级 Renderist 版本号
- 不修改 Phase 名称
- 不运行 `set-version.ps1`
- 不改变 Phase 2.4 功能边界

### 9.5 `Assembly-CSharp.dll`（当前 Phase 3 已重新确认）

历史 Phase 2.4 记录：

> 不作为编译引用引入。

当前 Phase 3 已通过本机 ADOFAI `Assembly-CSharp.dll` 0.4.3.0 的运行时反射核对内部 API；该 DLL 仍不作为编译引用或仓库文件提交。

---

## 10. 当前未完成能力

当前尚未形成正式生产能力：

- Route B deterministic render core 的产品化与最终收敛（普通 floor0 baseline 已 runtime PASS）；
- 正式 Deterministic Frame Scheduler / Frame Capture Backend 的产品化；
- 完整离线 PNG Sequence、最终音频输出和视频编码；
- 自定义输出分辨率 / 宽高比；
- replay / TUFReplay / Creplay 适配；
- 最终 Camera / UI / Video / VFX / DOTween / Animator 同步桥接。

当前已具备 PoC 代码边界：

- Forced Conductor；
- `RenderistAutoPlay` due-floor 诊断和官方 `scrPlayer.Hit(true)` 调用；
- `AudioRenderer` discard-buffer 采样；
- calibration_i=0 bridge 候选、AdjustAngle suppression 候选、Planet 状态和 `WaitForEndOfFrame` FrameBoundary 记录。

这些能力仍属于 Phase 3.2.0 调查和 PoC 边界，不能据此宣称正式非实时离线导出可用。

---
## 11. 下一步接续点

Phase 3.2.0 的普通 floor0 MasterTimeline baseline 已完成 runtime 验证（RUNTIME VALIDATED PASS）。当前「runtime 验证范围」如下。

已实机验证（仅限该条件组合，不泛化）：

- 普通 floor0、pitch=1、outputFps=60、targetFrameCount=180、Official AutoPlay OFF、`RDC.auto=false`、无人工输入；
- lifecycle handoff：`Start → OnMusicScheduled → Countdown → PlayerControl → lifecycle-ready`；
- gameplay-start anchor：`lifecycleReadySongPosition=1.074622` vs `gameplayStart=1.058824`，差约 `0.015798s`，通过 `0.05s` tolerance；
- Frame 0 transaction：BEGIN → Conductor → AutoPlay → EOF → PNG → FrameBoundary → COMMIT；
- due-floor Hit 进展：floor0→floor1（Frame22）至 floor7→floor8（Frame170）共 8 次，均 `due=true`、`hitResult=True`、`hitsThisFrame=1`、afterFloor 正确、无 duplicate Hit；
- `AsyncInputUtils.AdjustAngle(scrPlayer, ulong)` forced-capture suppression 生效，Frame 0–179 全程无 early death（`playerAlive=true`、`controllerState=PlayerControl` 持续）；
- 终态：`DeterministicFrameScheduler` 与 Editor export 均 `stopReason=target-frame-count-reached` / Completed。

未做或未验证（不得据此泛化）：

- BPM change；
- 任意 angle / 旋转方向；
- hold；
- event-heavy chart；
- checkpoint start / Twirl / Midspin / 特殊 startup；
- 任意 pitch / 帧率 / 更长 targetFrameCount；
- 完整非实时离线导出、音频输出、外部编码（Phase 4 及以后）。

静态结论保留：`CheckPostHoldFail` 的死亡角度是方向性未归一化角度域；当前生产路径不再保留 `scrPlayer.Die` 诊断 Hook，不修改官方死亡判定。Phase 4.0.0 的门槛仍是实际非实时逐帧导出、PNG 序列、wall-clock independence 与失败恢复全部完成并通过实机验证。

下一阶段的决策与范围由网页版 GPT 审查本收尾状态后给出；DSH 不自行宣布进入下一 Phase。

---
## 12. 工程红线

始终遵守：

- UMM-only
- 不引入 BepInEx / MelonLoader / Doorstop
- 不提交任何 DLL
- 不修改用户关卡
- 输出独立目录
- 不默认依赖 replay
- 不绑定第三方 replay Mod
- Harmony 优先 Prefix/Postfix，只有确认必要时才使用
- 不凭空假设 ADOFAI 内部 API
- 不未经确认修改版本号
- 不未经确认扩大 Phase 范围
- 不修改 README，除非用户明确要求
- 发布包只能包含 `Info.json` / DLL / `LICENSE`
