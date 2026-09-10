# ADOFAI Renderist 项目理解

> 本文件记录当前项目状态与可持续复用的已验证结论，不作为聊天记录或变更日志。
> 规则优先级：当前用户要求 > 网页版 GPT 项目指令 > `AGENTS.md` > 本文件。
> 事实冲突：实际仓库、diff、构建、日志和实机结果 > 本文件。

---

## 1. 当前目标与硬边界

ADOFAI Renderist 是基于 **Unity Mod Manager（UMM）** 的 ADOFAI 编辑器内非实时渲染导出 Mod。

当前路线：编辑器导出 → PNG 截图序列 → 可控逐帧 → 完整非实时渲染。

硬边界：

- 仅支持最新已验证 ADOFAI 正式版；当前机器基线为 Steam public buildid `24397494` + `Assembly-CSharp.dll` FileVersion `0.4.3.0`
- UMM-only；禁止 BepInEx / MelonLoader / Doorstop / 其它 Loader
- `net48`，Harmony 优先 Prefix / Postfix
- editor-first；当前不实现 replay / TUFReplay / Creplay
- Official Autoplay 只用于辅助/参考验证；不拥有 deterministic time authority
- 不提交游戏、Unity、UMM、Harmony、第三方 Mod DLL 或反编译源码
- README 由用户维护，默认不修改
- 发布包固定为 `Info.json` + `ADOFAI.Renderist.dll` + `LICENSE`
- 四位版本号；本阶段 `0.3.3.1 → 0.3.4.0` 已获用户明确批准

---

## 2. 当前版本与工程基线

| 项目 | 当前状态 |
| --- | --- |
| 产品版本 | `0.3.4.0` |
| 阶段 | `Phase 3.4.0` |
| Git 基线 | `main`；本阶段实现作为 `0.3.4.0` checkpoint 提交 |
| ADOFAI | Steam public buildid `24397494`；`Assembly-CSharp.dll` FileVersion `0.4.3.0` |
| Unity | `6000.3.10f1` / Mono |
| UMM | `0.33.0` |
| Harmony | `2.3.6.0` |
| TargetFramework / LangVersion | `net48` / `9.0` |

版本同步由 `scripts/set-version.ps1` 维护 `mod/Info.json`、`.csproj`、`ModEntry.ModVersion` 与启动日志；`package-release.ps1` 在打包前交叉检查 Info.json / csproj / ModEntry.ModVersion。

本地引用由 `scripts/prepare-references.ps1` 管理。`Assembly-CSharp.dll` 只用于运行时反射/分析基线，不作为 compile-time reference；已构建 DLL 的引用列表未包含 `Assembly-CSharp`。

---

## 3. 当前正式实现

主链路：

```text
FrameIndex N
  → outputTime = N / OutputFps
  → chartTime = canonicalStart + outputTime × pitch
  → scrConductor.Update Prefix 强制本帧 songposition
  → 原生 Conductor.Update
  → Postfix: RenderistAutoPlay 消费当前帧所有 due floor
  → 官方 scrPlayer.Hit(true)
  → 原生 scrController.OnLandOnPortal(...) 被 Postfix 观察
  → controller.state == Won 被确认
  → WaitForEndOfFrame
  → ReadPixels → EncodeToPNG → File.WriteAllBytes
  → PNG 成功后 commit N
  → completion 后按 output-frame 计数捕获 deterministic tail
  → tail 排空后 Completed；否则 N++
```

关键模块：

- `MasterTimeline`：FrameIndex 是唯一逻辑时间 authority；wall clock / Unity Update / audio 不推进时间。
- `PlaybackLifecycleHandoff`：关联 Renderist-owned `editor.Play()` 的 `StateEngine.Changed` 与 `OnMusicScheduled`。
- `EditorVisualClock`：强制 `songposition_minusi` getter/setter，并精确撤销 Harmony Patch。
- `RenderistAutoPlay`：按 `nextFloor.entryTime` 消费 due floor；`RDC.auto` 只在单次官方 `Hit(true)` 事务内临时置 true 并恢复。它不决定 session 完成。
- `EndTailPolicy`：校验并把单一玩家输入的 Frames / Seconds / Beats 换算为 output-frame tail。
- `DeterministicFrameScheduler`：启动、Initialization Hold、逐帧事务、canonical completion 观测、冻结/解析 tail、capture commit、停止与恢复。
- `FrameCaptureDriver`：当前分辨率同步 PNG 后端，带 generation 隔离。
- `EditorExportController` / `EditorExportSession`：preflight、独立 session、metadata、Completed/Cancelled/Failed 生命周期。
- `EditorGameReflection`：当前 ADOFAI 内部 API 的运行时反射层，包括 canonical completion 方法解析。

固定 `targetFrameCount=180` 已从正常终止 authority 移除。180 帧仍可作为历史验证基线，但不再决定任意谱面的正常结束。

---

## 4. Canonical completion 调查结论与证据

调查对象是本机当前 ADOFAI `Assembly-CSharp.dll`：Steam public buildid `24397494`、FileVersion `0.4.3.0`。结论来自真实 DLL 的反射枚举与 IL 检查：

- `scrController` 继承 `MonsterLove.StateMachine.StateBehaviour`，拥有 `state` 状态机属性。
- `MonsterLove.StateMachine.States` 枚举包含 `Won = 7`；`Fail = 5`、`Fail2 = 6` 是失败态。
- 当前 DLL 存在精确实例方法：`scrController.OnLandOnPortal(scrPlanet, Portal, string)`。
- 该方法的 IL 包含原生关卡结束处理：写入 `winTime`、处理音乐/灯笼/结束 UI、保存进度、调用胜利 UI，并在末尾调用 `StateBehaviour.ChangeState(Enum)`。结合 `States.Won = 7`，这是当前版本正常关卡完成入口。
- `scrController.Won_Enter` / `Won_Update` 是进入胜利态后的后续处理，不是更早的完成请求入口。
- `scrPlayer.Hit(bool)` 的 IL 只处理输入、floor 进度和视觉推进；未调用 `BeatLevel`、`OnLandOnPortal` 或状态切换，因此“最后一个 floor 已 Hit”不能直接等价为关卡完成。
- `scrController.BeatLevel` 的调用扫描只发现调试路径和 `OttoButtonController` 路径，不是当前正常 `scrPlayer.Hit(true)` 主链的完成依据。

最终选择：

1. 观察原生 `OnLandOnPortal`，不调用、不替换、不改变其参数或返回值。
2. 再读取 `scrController.state`，只有状态确认为 `Won` 后才接受 canonical completion。
3. 不使用 Transpiler，不调用 `BeatLevel`，不从 floor index 或 autoplay 命中数推导完成。

Harmony 变化：`DeterministicFrameScheduler` 对上述精确 `MethodInfo` 安装 Postfix `OnLandOnPortalPostfix(object __instance)`。该 Hook 只在当前 Renderist session、Capturing 状态且实例为当前 controller 时记录观察结果；实际取得的 `MethodInfo` 被保存并在 cleanup 中精确 Unpatch。注册失败会阻止 session 启动，撤销失败会保留 residual ownership 并阻止下一次启动覆盖现场。

---

## 5. 终止模型与 metadata

正常完成必须同时满足：

```text
OnLandOnPortal Postfix 已观察
  + controller.state == Won 已确认
  + completion 当帧及其后的配置 tail 已成功 capture/commit
  = Scheduler Completed / Session Completed
```

`completionFrameIndex` 是确认 `Won` 时正在捕获、尚未 commit 的 output frame index。该帧仍会正常捕获并 commit；tail 只从其后成功 commit 的 output frame 开始计数。每一帧只有 PNG 成功后才 commit，未 commit 不会推进到下一帧。

metadata 通过 `terminationKind`、`stopReason`、`completionSignal`、`completionFrameIndex`、canonical 两个观测布尔值、`tailFramesCaptured` 与 `safetyFrameLimit` 区分。End Tail 另记录 `endTailInputValue`、`endTailInputUnit`、`resolvedTailFrames`、`resolvedTailSeconds`、`resolvedTailBeats`、`completionBpm`、`outputFps` 与 `pitch`，从而保留玩家输入及 session 实际采用的冻结换算结果：

| 结果 | 条件 | `terminationKind` |
| --- | --- | --- |
| Completed | canonical completion + tail 排空 | `canonical-completion` |
| Cancelled | 用户 Stop、Esc 导致的 native stop、离开编辑器、Mod 禁用等 | `user-cancel` |
| Failed | safety 上限命中 | `safety-limit` |
| Failed | 初始化/捕获/逐帧 watchdog 超时 | `watchdog` |
| Failed | PNG 请求、写盘或帧事务失败 | `capture-failure` |
| Failed | 其它 API、生命周期或 cleanup 失败 | `lifecycle-failure` |

Safety / watchdog 绝不能把未完成谱面伪装成 Completed。

---

## 6. Tail policy

- 玩家只配置一组 `EditorEndTailValue + EditorEndTailUnit`；单位可选 Frames、Seconds、Beats，默认 `12 Frames`。
- GUI 单位切换以未格式化的 canonical output duration 为中间值。Seconds / Beats 不从格式化显示文本继续链式换算；切到 Frames 时只做一次 `ceil` 量化，并把量化后的精确 output duration 设为新的 canonical 值，避免反复切换累积漂移。
- Start 时冻结输入值和单位，当前 session 不再读取后续 GUI 修改。scheduler 最终只消费整数 `ResolvedTailFrameCount`：
  - Frames：输入必须为非负整数，`resolved = input`
  - Seconds：`resolved = ceil(seconds × OutputFps)`
  - Beats：`resolved = ceil(beats × 60 × OutputFps / (completionBpm × pitch))`
- Frames 是固定 output-frame 数；Seconds 是固定 output duration；Beats 按 completion 时有效 BPM 和当前 session pitch 解释。pitch 只进入 Beats 的一次解析，不在 scheduler 中再次缩放。
- 当前 DLL 的可靠 BPM 依据已经静态确认：`scrController.Start_Rewind` 把 `scrConductor.bpm × scrFloor.speed` 的变化写入 `listBPM`，`scrLevelMaker.CalculateFloorEntryTimes` 也使用同一组合计算 floor 时间。preflight/GUI preview 读取 base BPM × 最后有效 floor speed；canonical completion 时优先读取 `listBPM` 最后一项的 effective BPM，并以相同公式作为 fallback，无需新增 Harmony Hook。
- 非法、负数、非整数 Frames、缺少 Beats 所需 BPM/pitch、数值溢出，以及 `ResolvedTailFrameCount >= safetyFrameLimit` 均在启动前阻断；completion 时若实际 BPM 无法安全解析则 session 失败，不猜测默认 BPM。
- tail 最终仍以成功 commit 的 output frame 计数，不以 wall clock 推进。
- completion signal 所在帧仍必须 capture；tail 只统计其后成功 commit 的帧。
- `ResolvedTailFrameCount = 0` 时不额外输出 tail，但 completion 当帧仍会输出；值为 `1` 时恰好再输出一个成功 commit 的帧。
- 当前不尝试自动判断所有特效、相机或行星动画何时静止；显式 tail 是可解释的确定性上界，不代表所有视觉资产都已自动稳定。

---

## 7. Arbitrary-length、安全上限与恢复

- 正常 session 可持续到 canonical completion，不再受固定 180 帧截断。
- `EditorExportSafetyFrameLimit` 默认 `36000`，最大限制为 `1000000`；`0` 使用默认值。它只是 completion 永不出现时的 fail-safe，不参与正常完成判断。
- 三个 wall-clock watchdog 都只是失败保护，且都不推进 `MasterTimeline`；它们分别只覆盖单个阶段或单次帧进度间隙，不覆盖整个 session：
  - initialization readiness（frame 0 之前）30 秒；
  - 单次 capture callback 事务 30 秒；
  - 逐帧 progress（连续 30 秒没有成功 commit 的帧）。
  实机已出现一次 wall-clock 约 `50.45` 秒且正常 canonical completion 的 session（`createdAt 2026-09-10T01:44:22.783Z` → `endedAt 2026-09-10T01:45:13.235Z`），因此 30 秒 watchdog 不是整个 arbitrary-length export 的全局总时长限制。
- `FrameCaptureDriver` generation、pending capture、PNG commit、Harmony ownership、lifecycle handoff、RDC.auto、编辑器选择、播放状态和 Unity timing 的 cleanup/restart 安全模型保持原有顺序。
- terminal session 仍允许单击重新启动；启动前先执行 residual ownership gate。Completed 后仍通过已确认的 `scrController.ChangeToStartState()` re-arm 到 Start，之后才创建新 session 并调用一次官方 `editor.Play()`。
- 用户关卡不会被修改。

---

## 8. Runtime 验证范围

### 0.3.4.0 — RUNTIME VALIDATED（用户实机 A–I 报告全部正常）

- canonical completion：`scrController.OnLandOnPortal` 被观察 + `controller.state == Won`；session 正常结束为 `Completed` / `stopReason=canonical-completion-tail-drained` / `terminationKind=canonical-completion`。
- completion 当帧保留：`completionFrameIndex=201`；该帧照常 capture/commit，tail 只统计其后成功 commit 的帧。
- configurable End Tail 三种单位实机路径均正常：
  - Frames 默认 `12` → `completionFrameIndex=201`、`tailFramesCaptured=12`、`capturedFrameCount=214`；
  - Frames `0` → `capturedFrameCount=202`（completion 当帧仍输出，随后立即 Completed）；
  - Frames `1` → `capturedFrameCount=203`（completion 后精确多一个 committed frame）。
- Seconds / Beats 换算实机正常：在 completion BPM=140、pitch=1、60 FPS 附近观察到 1 beat 被离散解析为约 `26 frames = 0.433333 s = 1.011111 beats`，默认 `12 frames = 0.2 s = 0.466667 beats`，与 `ceil` frame quantization 语义一致。
- Beats 使用 completion effective BPM 与 session pitch（各只应用一次）在用户实际覆盖的 BPM/pitch 条件下正常；不泛化为任意 BPM / pitch 组合全面兼容。
- arbitrary-length：canonical completion 出现在 frame 201，已实际证明固定 180 帧不再是正常终止 authority。
- Running 期间 End Tail 配置 UI 被禁用：End Tail 在 session Start 时冻结，当前 session 不可能被 GUI 修改影响。
- lifecycle：Completed 后 single-click restart 正常；原生 Esc 得到 `state=Cancelled` / `stopReason=native-playback-stopped` / `terminationKind=user-cancel` / `completionSignal=null`（不得误记为 completion）；Esc 后 restart 正常。
- watchdog 作用域经实机确认：存在一次 wall-clock 约 `50.45` 秒且正常 canonical completion 的 session，因此 30 秒 watchdog 不是全局 session 上限（作用域见第 7 节）。

### 仍未 runtime validated

- `safetyFrameLimit=36000` 的专门异常注入：只有静态代码 / build 证据，未做 runtime injection，不得写成 runtime validated。
- 0.3.3.1 基线以外未实测的谱面类型：BPM change、Twirl、Midspin、hold、复杂角度 / 旋转方向、event-heavy chart、checkpoint / 特殊 startup。
- 任意 FPS、任意 pitch 组合、长时间大规模导出稳定性。
- UI hiding、camera、custom resolution / aspect / supersampling、audio、FFmpeg、replay。

音频边界：MasterTimeline 只拥有 visual/gameplay logical timeline，不拥有 audible audio timeline；audible music 与 beat / metronome 跟随 wall-clock。

---

## 9. 本阶段修改与后续重点

本阶段修改集中在：

- `EditorGameReflection`：解析并校验当前 DLL 的 `OnLandOnPortal` 精确签名与 `Won` 状态。
- `EndTailPolicy` / `EditorGameReflection`：纯 End Tail 换算、当前 DLL 的 effective BPM 读取，以及不新增 completion Hook 的 Beats 解析依据。
- `DeterministicFrameScheduler`：canonical completion observation、冻结并最终解析 output-frame tail、arbitrary-length session、安全上限、progress watchdog、termination classification 与 Hook cleanup。
- `EditorExportController` / `EditorExportSession`：移除 target frame metadata，记录 End Tail 输入/解析、BPM/pitch、safety、canonical completion 与 termination metadata。
- `Settings` / `ModEntry` / `UiText`：单值 + 单位 End Tail 配置、无累计漂移的单位切换、输入/换算状态显示、版本/阶段文案。
- `PROJECT_UNDERSTANDING.md`：根据当前 DLL、代码、构建与用户实机证据同步本阶段基线。

上述最小实机回归（短谱面 canonical completion、三种 End Tail 单位含 0/1 边界、最终 BPM / pitch 下的 Beats 换算、session 配置冻结、长于 180 帧、原生 Esc、Completed 后重启）已由用户完成并报告正常，`0.3.4.0` 作为稳定 checkpoint 冻结。`safetyFrameLimit` 异常注入与复杂谱面兼容性仍未验证；UI / camera / 分辨率 / audio / FFmpeg / replay 不属于本阶段实现范围。
