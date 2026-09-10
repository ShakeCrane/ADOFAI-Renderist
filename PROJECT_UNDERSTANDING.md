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
- 四位版本号；前三位变更必须得到用户确认

---

## 2. 当前版本与工程基线

| 项目 | 当前状态 |
| --- | --- |
| 产品版本 | `0.3.3.1` |
| 阶段 | `Phase 3.3.0` |
| 当前候选提交标签 | `0.3.3.1-beta`（产品版本不变；本轮修复待用户实机回归） |
| ADOFAI | Steam public buildid `24397494`；`Assembly-CSharp.dll` FileVersion `0.4.3.0` |
| Unity | `6000.3.10f1` / Mono |
| UMM | `0.33.0` |
| Harmony | `2.3.6.0` |
| TargetFramework / LangVersion | `net48` / `9.0` |

版本同步由 `scripts/set-version.ps1` 维护 `mod/Info.json`、`.csproj`、`ModEntry.ModVersion` 与启动日志；`package-release.ps1` 在打包前交叉检查 Info.json / csproj / ModEntry.ModVersion。

本地引用由 `scripts/prepare-references.ps1` 管理。当前 ADOFAI machine gate 使用 public Steam buildid 与 `Assembly-CSharp.dll` FileVersion；不再依赖未经可靠核对的人类版本标签。`Assembly-CSharp.dll` 只用于运行时反射/分析基线，不作为 compile-time reference。

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
  → WaitForEndOfFrame
  → ReadPixels → EncodeToPNG → File.WriteAllBytes
  → PNG 成功后 commit N
  → N++
```

关键模块：

- `MasterTimeline`：FrameIndex 是唯一逻辑时间 authority；wall clock / Unity Update / audio 不推进时间。
- `PlaybackLifecycleHandoff`：关联 Renderist-owned `editor.Play()` 的 `StateEngine.Changed` 与 `OnMusicScheduled`。
- `EditorVisualClock`：强制 `songposition_minusi` getter/setter，并精确撤销 Harmony Patch。
- `RenderistAutoPlay`：按 `nextFloor.entryTime` 消费 due floor；`RDC.auto` 只在单次官方 `Hit(true)` 事务内临时置 true 并恢复。
- `DeterministicFrameScheduler`：启动、Initialization Hold、逐帧事务、capture commit、停止与恢复。
- `FrameCaptureDriver`：当前分辨率同步 PNG 后端，带 generation 隔离。
- `EditorExportController` / `EditorExportSession`：preflight、session、metadata、Completed/Cancelled/Failed 生命周期。
- `EditorGameReflection`：当前 ADOFAI 内部 API 的运行时反射层。

F9/F10 旧 realtime screenshot 路径与旧 Diagnostics/PoC 已从正式代码删除。

---

## 4. Runtime Validated 范围

`0.3.3.1` 稳定基线已完成 A/B/C/D 实机闭环：

- ordinary floor0
- pitch=1
- outputFps=60
- fixed targetFrameCount=180（frame 0..179）
- lifecycle `Start → OnMusicScheduled → Countdown → PlayerControl`
- canonical gameplay-start anchor 与 forced visual clock
- due-floor progression / 官方 `scrPlayer.Hit(true)`
- synchronous PNG commit；Frame N 未成功捕获则不提交 N+1
- normal completion：`Completed / target-frame-count-reached`，180/180
- capture / Harmony / lifecycle / playback cleanup
- Completed 后 single-click restart；通过已确认 `scrController.ChangeToStartState()` re-arm 到 Start 后才创建新 session
- 原生 Esc：`Cancelled / native-playback-stopped`
- Esc 后 single-click restart
- 已测试路径未观察到 residual capture / Harmony / playback ownership

本节只描述已测试的窄基线，不得泛化到复杂谱面或任意参数。

`0.3.3.1-beta` 本轮只做 hygiene / safety 修复；这些新增修复在用户下一次实机测试前属于静态审查状态，不覆盖上述稳定基线的历史 runtime 证据。

---

## 5. 当前关键不变量

- MasterTimeline 只由 FrameIndex 推进；`Time.realtimeSinceStartupAsDouble` 仅用于 watchdog。
- pitch 只在 `chartTime = canonicalStart + outputTime × pitch` 映射中应用一次。
- due floor 必须在所属 output frame 内真正推进到刚才观察到的 `nextfloor`；不能仅凭 `Hit(bool)` 返回值把未推进 floor 当作成功。
- `RDC.auto` 必须读取旧值、临时写 true、调用官方 Hit、恢复旧值；恢复失败即失败。
- 生命周期/Harmony ownership 只在实际取得 ownership 后记录；cleanup 失败保留 handle 并阻止下一 Start 覆盖 residual state。
- `FrameCaptureDriver.Stop()` 先使 generation/callback 失效，再同步 Shutdown；scheduler 只有在 Stop 成功后才清自己的 generation ownership。
- cleanup 顺序保持 capture → forced clock/hooks → lifecycle handoff → RDC.auto / editor selection → playback → Unity timing。
- terminal session（Completed / Cancelled / Failed）是上一轮结果，不是永久 restart latch；真正安全性由 pre-start/residual gate 判断。
- pre-start rejection 必须发生在创建 session directory / metadata 之前。
- 当前 editor selection 恢复实现不能精确恢复非连续 multi-select；preflight 对该状态 fail-closed，避免运行后出现 selected-floor restore failure / residual cleanup blocker。selectedFloors 为空的语义尚未单独验证，因此 beta 不据此阻断普通路径。

---

## 6. 当前未验证 / 未实现

不得宣称以下能力已经可用：

- BPM change、Twirl、Midspin、hold、复杂角度/旋转方向、event-heavy chart
- checkpoint / 特殊 startup
- 任意 pitch、任意 FPS、长时间导出
- canonical level completion、tail policy、arbitrary-length formal session
- UI hiding、camera control、custom resolution / aspect / supersampling
- audio export / offline audio / timestretch
- FFmpeg / 外部视频编码
- replay / TUFReplay / Creplay

watchdog 的 timeout injection 路径仍只有静态/build 证据，没有专门 runtime injection。

音频已验证边界：可听音乐和 beat/metronome 仍按 wall-clock 原速播放；MasterTimeline 当前只拥有 visual/gameplay logical timeline，不拥有 audible audio timeline。此项不属于 0.3.3.1 blocker。

UI 已验证边界：当前尚未实现 UI hiding；当 UMM 面板在捕获时仍可见，实机曾观察到其进入输出画面。尚未单独验证具体 UMM OnGUI 时序因果。

---

## 7. 已排除 / 已退役 与延后项目

已排除或退役：

- BepInEx / MelonLoader / Doorstop / 多 Loader 抽象
- Assembly-CSharp.dll compile-time reference
- F9/F10 realtime screenshot 路径
- 已完成使命的旧 Diagnostics / PoC / probe
- AudioRenderer 作为 MasterTimeline 逻辑时间 owner

明确延后、不是“已排除”：

- canonical level completion / tail policy / arbitrary-length session
- complex chart compatibility matrix
- UI hiding / camera / custom resolution
- audio
- FFmpeg / video encoding
- replay compatibility

---

## 8. 当前风险与下一步

`0.3.3.1-beta` 修复点：

- preflight fail-closed，防止当前无法精确恢复的**非连续 multi-select**进入 session；不对 selectedFloors 为空作未经验证的阻断
- due-floor 成功条件改为实际推进到预期 `nextfloor`，避免 `Hit(false)` 静默遗留 due floor
- `PlaybackLifecycleHandoff` 仅在 `Changed` 事件订阅成功后记录 subscription ownership
- 删除已无调用者的旧 `ResolveSessionDirectory` 与 F9/F10 残留说明
- 修正 EditorEnv / packaging / 项目理解中的过时注释与事实矛盾
- package 增加 `ModEntry.ModVersion` 一致性检查
- prepare-references 以当前 public Steam buildid `24397494` + `Assembly-CSharp.dll` FileVersion `0.4.3.0` 作为 supported ADOFAI machine gate；旧 `23935606` 已是 Steam `older` branch，不再作为 latest baseline

下一步不是继续开发：先完成 prepare/build/package/deploy，再由用户对 `0.3.3.1-beta` 做普通 180 帧、连续复跑、原生 Esc、Esc 后复跑的回归；若用户使用非连续 multi-select，再确认 preflight 在创建 session 前正确阻断。

通过后冻结该 hygiene candidate，再由用户决定是否批准前三位版本变化并进入后续功能阶段。
