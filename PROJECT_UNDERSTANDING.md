# ADOFAI Renderist 项目理解

> 本文件记录当前项目状态与可持续复用的已验证结论，不作为聊天记录或变更日志。
> 规则优先级：当前用户要求 > 网页版 GPT 项目指令 > `AGENTS.md` > 本文件。
> 事实冲突：实际仓库、diff、构建、日志和实机结果 > 本文件。

---

## 1. 当前目标与硬边界

ADOFAI Renderist 是基于 **Unity Mod Manager（UMM）** 的 ADOFAI 编辑器内非实时渲染导出 Mod。

当前路线：编辑器导出 → 从根源隔离的谱面 RenderTexture → PNG 截图序列 → 可控逐帧 → 完整非实时渲染。

硬边界：

- 仅支持最新已验证 ADOFAI 正式版；当前机器基线为 Steam public buildid `24397494` + `Assembly-CSharp.dll` FileVersion `0.4.3.0`
- UMM-only；禁止 BepInEx / MelonLoader / Doorstop / 其它 Loader
- `net48`，Harmony 优先 Prefix / Postfix
- editor-first；当前不实现 replay / TUFReplay / Creplay
- Official Autoplay 只用于辅助/参考验证；不拥有 deterministic time authority
- 不提交游戏、Unity、UMM、Harmony、第三方 Mod DLL 或反编译源码
- README 由用户维护，默认不修改
- 发布包固定为 `Info.json` + `ADOFAI.Renderist.dll` + `LICENSE`
- **非实时导出原则**：不得因性能、wall-clock 处理速度、PNG 编码耗时、文件数量、磁盘写入速度或预计导出时长而人为限制导出参数。"参数合法性" ≠ "当前机器性能是否足够"；1000 FPS 不要求现实时间每秒完成 1000 张 PNG。性能问题只用 warning / estimate / benchmark / disk estimate / recommendation 表达，**不用 legality gate 禁止参数**
- **不存在内建的最大导出时长或最大总输出帧数**：safety 默认为未配置（unbounded，见 §3.8）；正常谱面长度、正常总帧数与高 Output FPS 都不是故障条件。导出只由 canonical completion、用户 cancel、异常 fail-closed 与"无进展 / 卡死 / 状态异常"watchdog 终止
- 四位版本号；`0.3.4.0 → 0.3.5.0`、`0.3.5.0 → 0.3.5.1`、`0.3.5.1 → 0.3.6.0` 与 `0.3.6.0 → 0.3.6.1` 均已获用户明确批准并已同步

---

## 2. 当前版本与工程基线

| 项目 | 当前状态 |
| --- | --- |
| 产品版本 | `0.3.6.1` |
| 阶段 | `Phase 3.6.0 Render Time Determinism` |
| Git 基线 | `main`；`0.3.6.0` 收敛内容见本文件 §11，`0.3.6.1` 为 Output FPS legality 语义、safety 默认 unbounded（移除人为总帧数 / 总时长上限）、canonical frame index `long`、`PrepareHitState` fail-closed 与 `controller.paused` 阶段修正的修订版本 |
| 实机验证状态 | `0.3.6.1`：**30 FPS 与 1000 FPS 完整导出均通过**（用户实机 2026-09-15，见 §8） |
| ADOFAI | Steam public buildid `24397494`；`Assembly-CSharp.dll` FileVersion `0.4.3.0` |
| Unity | `6000.3.10f1` / Mono |
| UMM | `0.33.0` |
| Harmony | `2.3.6.0` |
| TargetFramework / LangVersion | `net48` / `9.0` |

版本同步由 `scripts/set-version.ps1` 维护 `mod/Info.json`、`.csproj`、`ModEntry.ModVersion` 与启动日志；`package-release.ps1` 在打包前交叉检查 Info.json / csproj / ModEntry.ModVersion。当前阶段文案另同步在 `ModEntry` 类注释、`EditorExportSession.PhaseLabel`（写入 metadata 的 `phase`）与 `FrameCaptureDriver` 类注释。历史模块注释中标注的 `Phase 3.4.0` 是该模块的引入阶段provenance，按原样保留。

本地引用由 `scripts/prepare-references.ps1` 管理。`Assembly-CSharp.dll` 只用于运行时反射/分析基线，**不作为 compile-time reference**；已构建 DLL 的引用列表只有 `mscorlib` / `UnityModManager` / `0Harmony` / `System` / `UnityEngine.CoreModule` / `UnityEngine.IMGUIModule` / `UnityEngine.ImageConversionModule`。

---

## 3. 正式架构

### 3.1 Render Source（根源隔离）

```text
scrCamera.instance
  → Bgcamstatic
  → BGcam
  → camobj
  → Renderist-owned CaptureTarget (RenderTexture, Screen.width × Screen.height, 24, ARGB32)
```

三台 ADOFAI 原生谱面摄像机的 `targetTexture` 在本 session 内被接管到 Renderist 自有的 RenderTexture。Unity 仍按正常帧渲染流程渲染这三台 Camera，因此 **Screen Space UI（Editor / UMM / Renderist）不进入该 RT**，谱面特效（含 CameraFilterPack 后处理）经真实原 Camera 完整保留。**不**使用 `Camera.main`、camera clone、`Camera.Render()`、UI SetActive/Canvas/cullingMask 抑制、`ScreenCapture` 或 `AsyncGPUReadback`。

### 3.2 Frame transaction

```text
FrameIndex N
  → MasterTimeline（outputTime = N / OutputFps，chartTime = canonicalStart + outputTime × pitch）
  → scrConductor.Update Prefix：强制本帧 songposition
  → 原生 Conductor.Update
  → Postfix：RenderistAutoPlay 消费当前帧所有 due floor → 官方 scrPlayer.Hit(true)
  → native visual render（三台 Camera → CaptureTarget）
  → WaitForEndOfFrame
  → RenderTexture.active = CaptureTarget → Texture2D.ReadPixels(captureWidth × captureHeight)
  → EncodeToPNG → File.WriteAllBytes
  → PNG 成功后 commit N
  → completion 后按 output-frame 计数捕获 deterministic tail
  → tail 排空后 Completed；否则 N++
```

关键不变量：**Frame N 的 PNG 没有成功写盘，就不 commit N，也不开始 N+1。** `MasterTimeline` 的 FrameIndex 是唯一 **export / chart timeline** authority（类型为 `long`，见 §3.7）；wall clock 与 audio 都不推进它。（引擎侧时间另由 `Time.captureFramerate` 决定，见 §3.7——不要把它读成"MasterTimeline 直接拥有全部 Unity engine state"。）

### 3.3 Completion

```text
scrController.OnLandOnPortal 被 Postfix 观察
  + controller.state == Won 被确认
  + completion 当帧已成功 capture/commit
  + 配置的 End Tail 已成功 commit
  = Scheduler Completed / Session Completed
```

### 3.4 Esc / cancellation lifecycle

```text
用户按 Esc
  → native scnEditor.Update 的 Esc 分支 → scnEditor.SwitchToEditMode(false)
  → Renderist 的 exact Postfix observer 命中
  → RequestStop("native-playback-stopped")
  → 下一次 Tick 的 ProcessStop → ownership-aware cleanup
  → Scheduler Cancelled / terminationKind = user-cancel
```

设计依据（最小必要历史结论）：Input Guard 抑制 `scrController.TogglePauseGame` 之后，`scnEditor.playMode` 的**派生语义**（见 10.3）不再能可靠表示 native editor playback 生命周期，因此精确观察 `scnEditor.SwitchToEditMode(bool)` 才是当前可靠的 stop signal。30 秒 frame-progress watchdog 只是失败保护，**不是**正常 Esc cancellation 路径。

### 3.5 Input isolation（session-scoped）

在官方 `editor.Play()` **之前**安装 6 个 exact Harmony **Prefix**，只在当前 session 的 `InitializationHold` / `Capturing` 期间生效，cleanup 时精确撤销：

| 目标 | exact signature | 抑制语义 |
| --- | --- | --- |
| `scrPlayerManager.AnyValidInputWasTriggered` | `bool ()` | `false` |
| `scrPlayer.ValidInputWasTriggered` | `bool ()` | `false` |
| `scrPlayer.ValidInputWasReleased` | `bool ()` | `false` |
| `scrPlayer.CountValidKeysPressed` | `int ()` | `0` |
| `scnEditor.ZoomCamera` | `void (float, bool, bool)` | 跳过原方法 |
| `scrController.TogglePauseGame` | `bool ()` | 跳过原方法，返回当前 `controller.paused` |

未 Patch `scrPlayer.Hit`、`scrConductor.Update`、`scnEditor.Update`、`scrPlayerManager.SetAllPlayerResponsive`、`scrPlanet.Update_RefreshAngles`；`RDC.auto` 与官方状态推进不受影响。**Esc 不经过 `TogglePauseGame`**（见 10.3），因此本组 guard 不会阻断取消路径。

### 3.6 Cleanup / ownership

- 所有 Harmony Patch 都保存**实际成功注册**的 original `MethodInfo`，cleanup 用 `Unpatch(original, prefix/postfix)` 精确撤销，不使用 `UnpatchAll` 作为正常路径；撤销失败保留 residual ownership 并归入 `cleanup-failed`，允许后续重试。`HasResidualOwnership()` 覆盖全部 hook、`FrameCaptureDriver.IsRunning`、`HasActiveCameraSource`、input guard 列表、`_ownsPlayback`、`_ownsUnityTiming`、lifecycle handoff、`RDC.auto` 与编辑器选择。
- **Camera targetTexture 采用 ownership-aware restore**：逐 Camera 读取 `camera.targetTexture`，**只有当前值仍精确等于本 session 的 `captureTarget` 时**才写回 session 前的真实值；若 native / 游戏已在 session 期间接管该属性（Esc 时 `SetupRTCam(false)` 会把三台置 `null`），则记录 `target ownership already relinquished / externally changed` 并**不覆盖**，且这不算 cleanup failure。
- 逐 Camera 处理完成后确认是否仍有 live Camera 精确引用 `captureTarget`：仍有则保留 `captureTarget` 与 ownership、返回失败供下次重试，**绝不** Release / Destroy 仍被引用的 RenderTexture；无则 `Release()` + `Destroy()` 并清空引用与冻结尺寸。
- 全部 cleanup/restore 幂等、partial-safe、retry-safe。`RenderTexture.active` 只在单次 `ReadPixels` 临界区内改变，`finally` 恢复。
- cleanup 顺序保持：先停 capture source 与全部 hook，再恢复 `RDC.auto` / 编辑器选择 / playback，最后恢复 Unity timing。
- **`EnsureCleanedUp(stopEvent, stopReason)` 是统一 ownership 收敛入口**：`_running` 时先 `StopNow` 再走 residual gate，非 running 时直接重试 `RestoreAll`；幂等、可重复调用，且不依赖 controller session 是否 terminal（**session terminal ≠ scheduler owns nothing**）。
- **residual ownership 跨调用保留已实机验证**：`RestoreAll` **不短路**，逐项尝试恢复并收集 `failures`；单项失败不会阻止其余项完成，但 `_restored` / `_savedRdcAuto` / `_savedSelectedFloorSeqs` **只在 `failures.Count == 0` 时清零**。因此 cleanup 失败后 residual 会真实保留到**下一次外部调用**（下一次 `Start` 的 `ValidatePreStartConditions`、terminal session 的 `Stop`/`Cancel`、`OnToggle(false)` 的 Mod disable），并在那里补做成功。此时 session 层 stopReason 可能仍是硬编码的 `controller-fail`，`cleanup-failed:<失败项列表>` 只出现在 scheduler 层与启动拒绝原因中。
- Start gate 与 terminal 收敛共用同一 retry 语义：`ValidatePreStartConditions → EnsurePreviousRunCleanedUp → RestoreAll`。只要 residual 未清零，新的 Start 会被 `cleanup-failed:*` 拒绝且**不创建 session 目录**（gate 在目录创建之前）。

### 3.7 Render Time（0.3.6.0）

职责划分必须区分两层，不要混为一谈：

- **chart timeline**：`MasterTimeline.FrameIndex` 是唯一 authority（§3.2）。
- **engine time**：由 `Time.captureFramerate` 承担，**不是** `MasterTimeline` 直接拥有。

已实机确认（60 / 30 FPS 两组导出，见 §8）：

```text
Time.captureFramerate = OutputFps
Time.captureDeltaTime = 1 / OutputFps
Time.deltaTime        = 1 / OutputFps
```

并且 **capture transaction 中 1 个 output frame 对应 1 个连续 Unity frame**（output frame index 与 `Time.frameCount` 均严格 +1）。因此 `outputFps` 是输出采样密度：引擎侧 scaled 时间每帧恰好前进 `1/OutputFps`。

- **`Time.timeScale` 不是 Renderist-owned state**。Renderist 只读（诊断）不写。output capture frames 实机恒为 `1`；`Play` / `InitializationHold` 边界可出现 native transient `0`（本 DLL 内唯一的持续非 1 写入者是 `scrController.TogglePauseGame` 且 `paused == true`，已被 Input Guard 抑制）。**不要新增 `Time.timeScale == 1` 的启动 preflight 或 ownership。**
- **`controller.paused` 是 runtime condition，不是启动前条件**（0.3.6.1 修正）。真实语义（静态 IL 已确认，见 §10.5）：
  - 编辑器 idle（以及 Esc 返回编辑模式后）`paused == true` 是**正常状态**：`scrController.Awake` 执行 `paused = ADOBase.isLevelEditor`，返回编辑模式时 `scnEditor.SwitchToEditMode → TogglePause → scnGame.ResetScene` 的编辑器分支又会重新 pause。
  - 唯一会把它清零的是官方 `editor.Play()` → `scnGame.Play()`（`paused = false`），因此**启动前检查它必然误杀正常导出**。0.3.6.0 的 `controller-paused` 启动拒绝即为此阶段错误，已删除。
  - 正确判定点是 **playback readiness**：生命周期真正到达 `PlayerControl` + `playerAlive` 之后仍 `paused == true`（或 `paused` 不可读）才是异常，立即 fail-closed（`controller-paused` / `controller-paused-during-playback` / `controller-paused-state-unavailable-during-playback`）。`PlaybackLifecycleHandoff.IsReady` 仍要求 `!paused`，`IsReadyExceptPaused` 只描述"生命周期是否已到达可判定阶段"。
  - 只阻止，不修复：**Renderist 在任何阶段都不写 `paused`、不调用 `TogglePauseGame`、不写 `Time.timeScale`**；读取失败不得当作 false 放行（`IsPlaybackReady` 与异常判定都 fail-closed）。
- **Output FPS 合法性**由 `Export/OutputFpsPolicy` 单点定义（0.3.6.1 起）：**没有产品级上限**，合法即"当前配置 / API 能表达的正整数"（`Settings.EditorTargetFrameRate` 与 `Time.captureFramerate` 都是 `int`，因此无需额外定义 Maximum；`int.MaxValue` 只是数据类型边界，不是推荐值或性能目标）。GUI 文案为"必须是正整数"。默认 `60`，最小 `1`（`Time.captureFramerate == 0` 在 Unity 约定中表示"未启用捕获节拍"）。
- `Application.targetFrameRate` 只是 **best-effort derived hint**（让 Unity 不节流导出循环），**不是** authority：`outputFps * 4` 一律用 `long` 计算并在超过 `int.MaxValue` 时饱和到 `int.MaxValue`，**绝不**因为这个内部派生量拒绝用户的 Output FPS。
- 性能 / PNG 编码耗时 / 磁盘占用 / 预计时长都不是合法性条件，也不得成为新的参数上限；未来只用 warning / estimate / benchmark / disk estimate / recommendation 表达（见 §1 非实时导出原则）。
- **帧编号类型**：Renderist 自有的 canonical / output frame 编号与计数一律是 `long`——`MasterTimeline.FrameIndex` / `Prepare(long)`、`_outputFrameIndex`（prepared / committed / pending capture index）、capture request / captured / tail 计数、`FrameCaptureDriver` 的帧号与 PNG 文件名编号、`completionFrameIndex` 与 `resolvedTailFrames`。`long` 只是内部帧序号的**结构可表达边界**，不是产品级上限，也不允许建立人为 `long` 上限。
- **仍是 `int` 的部分**（有意保留）：`Settings.EditorTargetFrameRate` 与 `Time.captureFramerate`（Unity API / 配置类型，都是正 int）、`Application.targetFrameRate` 派生（饱和到 `int.MaxValue`）、Unity 侧 `Time.frameCount` 与 `_activationUnityFrame` / `_lastPrepareUnityFrame`、单帧命中数 `_hitsThisFrame`（上界是当前谱面 floor 数）、文件名 `ZeroPadWidth`（只是**最小**补零宽度，位数超过时自然扩展，不截断）。
- **frame → 逻辑时间换算**在 `double` 中进行（`(double)frameIndex / OutputFps`），不存在 int 中间值乘法或转换；因此 `int.MaxValue + 1L` 及更大的帧号不会出现负数、截断或 int cast。
- **double timeline 仍有有限浮点精度边界**：`outputTime` / `chartTime` 是 `double`，尾数 53 位，因此当帧号超过 `2^53 ≈ 9.007e15` 后，相邻帧号不再能被逐一精确表示，`outputTime` 的步进会出现量化（相邻帧可能落在同一 double 上）。这是数据类型精度边界，不是产品上限：按 1000 FPS 计 `2^53` 帧约合 9e12 秒（约 28 万年），实际导出（含 1000 FPS 的 3336 帧实机用例）远在边界之内；如未来要支持该量级，需要改用更高精度的时间表示，而不是给帧数或 FPS 加人为上限。
- **long 边界的 fail-closed**：帧号理论上到达 `long.MaxValue` 时无法再表达"下一帧"，`PrepareFrame` / `CommitFrame` 在递增前显式 `RequestStop("frame-index-exhausted", "output-frame-index-exceeds-long-range")`，**不依赖 unchecked 递增回绕**；End Tail 的 double → long 转换同样有显式边界检查（`9223372036854775808.0`，即 2^63），不使用 C# 的 `checked`（对浮点→整数转换无效）。

### 3.8 Safety policy：默认 unbounded，显式 frame limit 可选（0.3.6.1）

safety 上限的唯一解析点与判定点是 `Export/SafetyFrameLimitPolicy`；`Export/EndTailPolicy` 与 `DeterministicFrameScheduler` 都只通过 `SafetyFrameLimitPolicy.IsFrameLimitReached(frameLimit, count)` 做帧数判定，不存在第二处比较。

| 配置情况 | 解释 | 本 session 的 frame 上限 |
| --- | --- | --- |
| `EditorExportSafetyFrameLimit <= 0` | 未配置（内建默认，Settings 字段默认值就是 `0`） | **无限制**：不存在总帧数 / 总时长上限 |
| `EditorExportSafetyFrameLimit == 36000` | legacy 历史默认标记（0.3.6.0 及更早的内建默认值） | **无限制**（同上） |
| 其它正整数（显式设置） | 显式 output-frame 上限 | **按配置值原样使用，不夹取** |

- 判定语义：`frameLimit <= 0` 时 `IsFrameLimitReached` 恒为 `false`，即 unbounded 状态下导出**永远不会因为帧数而终止**；只有显式配置的正整数会触发 `safety-limit` 终止。
- **已移除的人为限制**：`min(value, 1000000)` 夹取、`ceil(600 × outputFps)` / 600 秒默认 duration policy、Output FPS 上界。`int.MaxValue` 只是 `Settings` 字段当前数据类型的结构边界，不是产品推荐值或性能限制。
- safety 的职责边界：保留 manual cancel、异常 fail-closed 与"无进展 / 卡死 / 状态异常"watchdog（initialization readiness 30 s、单次 capture 事务 30 s、逐帧 progress 30 s，均由成功 commit 刷新；它们只覆盖单个阶段或一次进度间隙，不是总帧数或总时长上限，也绝不推进 `MasterTimeline`）。**不得**用"总帧数过多""谱面超过 N 秒""FPS 太高"作为默认故障判断。
- **legacy Settings 兼容**（无配置版本系统，含一处刻意接受的歧义）：
  - UMM 的 `OnSaveGUI → ModSettings.Save` 会把整个 Settings 对象（含默认值）序列化到 `Settings.xml`，历史默认 `36000` 因此会真实落盘（实测本机 `Mods\ADOFAI.Renderist\Settings.xml` 即带 `<EditorExportSafetyFrameLimit>36000</EditorExportSafetyFrameLimit>`）。
  - safety 配置从未在 GUI 暴露，因此**正常 GUI 使用下**写盘的 `36000` 就是 legacy 默认值。
  - 但用户**仍可手工编辑** `Settings.xml` 写成 `36000`；Renderist **无法区分**"legacy 默认 36000"与"手工显式 36000"。
  - 当前**刻意选择**把 `36000` 统一迁移解释为 unbounded（与 `<= 0` 同义），其它正整数才按显式 frame limit 解释；代价是手工写入的 `36000` 也会被当作 unbounded。这是兼容策略的**已知歧义**，不使用配置版本系统来消除。
- 表示方式：内部与 End Tail 边界用 `0` 表示未配置（并由上述谓词统一守卫）；**对外表示一律用"无值"**——`EditorExportReadinessReport.SafetyFrameLimit`、`EditorExportSession.SafetyFrameLimit` / `SafetyDurationSeconds`、`DeterministicFrameScheduler.SafetyFrameLimit` / `SafetyDurationSeconds` 都是可空类型，unbounded 时为 `null`，日志与 GUI 显示 `unbounded` / "未配置"，**不用 `0` 或 `long.MaxValue` sentinel 冒充真实上限**。
- metadata 记录最终实际采用的 policy：`safetyPolicy`（`unbounded` / `explicit-frames`）、`safetyFrameLimit`（帧数或 `null`）、`safetyDurationSeconds`（秒数或 `null`；仅显式配置时有真实含义）；启动日志另记录 `configuredSafetyFrameLimit`（原始配置值，仅诊断 legacy 来源）。

### 3.9 `averageFrameTime` 与 multipress gate（Renderist 不拥有）

- `scrController.averageFrameTime`（`public float`）：**唯一 writer 是 `scrController.PlayerControl_Update()`**，语义为 EMA `a_n = 0.5·a_(n-1) + 0.5·Time.deltaTime`；**唯一外部 reader 是 `scrPlanet.SwitchChosen()`**。无属性、无第二读者。它**不参与命中判定**（`GetHitMargin` / `SnapAngleCardinal` / `targetExitAngle` 都不读它），只影响**多重按压惩罚记账与显示**（`scrFailBar` 伤害、`missesOnCurrFloor` miss 标记、判定文字被改写为 `HitMargin.Multipress`）。
- `SwitchChosen` 的 multipress 门是**跨调用状态**：门在 `@2100/@2116` 读取 `multipressAndHasPressedFirstPress` / `multipressPenalty`，而武装它们的 `MoveToNextFloor` 在同一方法内更晚的 `@3240` 才执行（1-floor lag）。
- **Renderist 不拥有 `averageFrameTime`**：不写、不 seed、不 pin、不新增 ownership。
- **`RenderistAutoPlay.TryPrepareHitState` 对 `multipressPenalty` / `multipressAndHasPressedFirstPress` / `consecMultipressCounter` / `keyTimes` 的中和是 deterministic autoplay invariant，且自 0.3.6.1 起是 fail-closed 的**：每次官方 `Hit(true)` 之前把 gate 的两个 flag 清回 `false`、清空 `keyTimes`、清零 `consecMultipressCounter`；任一写入失败即返回 `false`，调用方在官方 `Hit(true)` **之前**停止并以 `hit-state-prepare-failed:<具体原因>` 结束 session。该不变量的正常路径与 completion 路径均已实机确认（0.3.6.0）。
- **`SetAllPlayerResponsive(true)` 同样是 load-bearing 项**（0.3.6.1 起必需）：静态 IL 已确认 `scrPlayer.Hit(bool)` 的第一个 guard 就是 `ldfld scrPlayer::responsive; brfalse → return false`（`IL_0009`/`IL_000e`），而 `scrPlayerManager.SetAllPlayerResponsive(bool)` 的实现就是逐个 `stfld scrPlayer::responsive`。`scnEditor.Play()` 经 `scrPlayerManager.UnlockAllPlayerInput() → scrPlayer.UnlockInput()` 在 session 启动时给出该保证，`scrController.Update()` 也会调用它；Renderist 在每次命中前重新断言 `true`，因此该 API 不可解析时不再静默降级（`EnsureAvailable` 返回 `autoplay-player-responsive-api-unavailable`，session 不启动）。
- 该不变量**不需要**用常驻 Harmony 监控来守护：为一个已被实机确认不可达的原生分支永久增加 patch 是不必要的复杂度。
- `TryPrepareHitState` **不再写 `controller.paused`**：`CatchUp` 在进入命中循环前已对 `paused == true` 提前 return，因此该写入是可证明的 no-op，移除它避免在没有 ownership 的情况下修改游戏状态。
- **RDC.auto 的 finally 恢复不受 fail-closed 影响**：`TryPrepareHitState` 失败路径仍经过同一 `try/finally`（IL 的 `Finally try=IL_01fe+352 handler=IL_035e+33` 覆盖整个命中事务），因此 `RDC.auto` 一定恢复原值，恢复失败仍以 `rdc-auto-restore-failed` 优先上报。

---

## 4. 关键模块

- `MasterTimeline`：FrameIndex 是唯一 **export / chart timeline** authority（`long`）；`Prepare(long)` 用 double 完成 frame → outputTime / chartTime 换算。
- `PlaybackLifecycleHandoff`：关联 Renderist-owned `editor.Play()` 的 `StateEngine.Changed` 与 `OnMusicScheduled`。**`IsReady` 的前置条件是全部满足**：`PlayRequested && PlayReturned && SawStart && SawMusicScheduled && SawCountdown && SawPlayerControl && 当前 state=="PlayerControl" && playerAlive && !paused`（实现上 `IsReady = IsReadyExceptPaused && !paused`，后者只描述"生命周期是否已到达可判定阶段"，供 paused 异常判定复用）。其中任一（尤其 `SawCountdown`：必须真的观察到一次 `Countdown` 状态提交）缺失，`InitializationHold` 就会每个 Tick 提前返回、永不释放，session 最终以 `native-playback-stopped` 取消且 `outputFrameIndex` 恒为 0。
- `EditorVisualClock`：强制 `songposition_minusi` getter/setter，并精确撤销 Harmony Patch。
- `RenderistAutoPlay`：按 `nextFloor.entryTime` 消费 due floor；`RDC.auto` 只在单次官方 `Hit(true)` 事务内临时置 true 并恢复。它不拥有时间，也不决定 session 完成。**`TryPrepareHitState` 的 multipress / `keyTimes` / `consecMultipressCounter` / `SetAllPlayerResponsive(true)` 是 fail-closed 的 deterministic autoplay invariant（见 §3.9）。** **progression bound 已移除固定魔数**：单次 `CatchUp`（一个输出帧）的合法命中上界是**当前谱面 `floors.Count`**（每次 `Hit` 都要求 `seqID` 严格向前）；`floors` 不可读时 fail-closed。成功条件是 canonical progression 严格单调向前：`after == before` 与 `after < before` 都立即 `hit-progression-not-forward` fail-closed；多格前进允许（记录但接受）。bound 耗尽后的尾部检查与主循环**同一判定**：`next == null` → success；`entryTime` 不可读 / NaN / Infinity → `next-entry-time-unavailable` fail-closed；仍 due → `autoplay-progression-bound-exceeded`；未 due → success。
- `EndTailPolicy`：校验并把单一玩家输入的 Frames / Seconds / Beats 换算为 output-frame tail；safety 参数是 `long`，`0` 表示未配置上限（unbounded），显式配置时按配置值原样判定（见 §3.8）。
- `OutputFpsPolicy`：Output FPS 的唯一合法性 / 派生单点（正整数，无产品级上限；`Application.targetFrameRate` 是饱和派生的 best-effort hint）。
- `SafetyFrameLimitPolicy`：safety 上限的唯一解析点与判定点（默认 unbounded，显式 frame limit 原样使用，见 §3.8）。
- `DeterministicFrameScheduler`：启动、Initialization Hold、逐帧事务、canonical completion 观测、native Esc observer、input guard、冻结/解析 tail 与 safety policy、capture commit、停止与恢复；canonical frame 编号 / 计数为 `long` 并带 long 边界 fail-closed。
- `FrameCaptureDriver`：同步 PNG 后端，带 generation 隔离。两阶段生命周期：`Start()` 只建立 generation / host / coroutine；`TryActivateCameraSource()` 才取得当前 session 的摄像机链并接管 `targetTexture`。source 未激活时 `RequestCapture` 一律拒绝，不回退 Screen framebuffer。帧号是 `long`；文件名 `frame_<index>.png` 采用**最小 6 位补零**（`frame_000000.png`），编号超过 6 位时自然扩展（`frame_1000000.png`）、绝不截断。
- `EditorExportController` / `EditorExportSession`：preflight、独立 session、metadata、Completed/Cancelled/Failed 生命周期。
- `EditorGameReflection`：当前 ADOFAI 内部 API 的运行时反射层（摄像机链、canonical completion、input guard 目标、只读生命周期诊断）。

固定 `targetFrameCount=180` 已从正常终止 authority 移除；180 帧只是历史验证基线。

---

## 5. 终止模型与 metadata

`completionFrameIndex` 是确认 `Won` 时正在捕获、尚未 commit 的 output frame index（`long`）。该帧仍会正常捕获并 commit；tail 只从其后成功 commit 的 output frame 开始计数。每一帧只有 PNG 成功后才 commit。

metadata 通过 `terminationKind`、`stopReason`、`completionSignal`、`completionFrameIndex`、canonical 两个观测布尔值、`tailFramesCaptured` 与 safety 三元组区分。Safety 三元组是 `safetyPolicy`（`unbounded` / `explicit-frames`）、`safetyFrameLimit`（显式配置时的 output-frame 上限，unbounded 时为 `null`）与 `safetyDurationSeconds`（仅显式配置时有真实含义，unbounded 时为 `null`）；**unbounded 状态绝不写出 `0`、`600` 或 sentinel 冒充上限**。End Tail 另记录 `endTailInputValue`、`endTailInputUnit`、`resolvedTailFrames`、`resolvedTailSeconds`、`resolvedTailBeats`、`completionBpm`、`outputFps` 与 `pitch`。Render Source 另记录 `captureSource`（值 `scrCamera-rendertexture`）、`captureWidth`、`captureHeight`。

| 结果 | 条件 | `terminationKind` |
| --- | --- | --- |
| Completed | canonical completion + tail 排空 | `canonical-completion` |
| Cancelled | 用户 Stop、Esc 导致的 native stop、离开编辑器、Mod 禁用等 | `user-cancel` |
| Failed | safety 上限命中（**仅显式配置 frame limit 时可能**） | `safety-limit` |
| Failed | 初始化/捕获/逐帧 watchdog 超时 | `watchdog` |
| Failed | PNG 请求、写盘或帧事务失败 | `capture-failure` |
| Failed | 其它 API、生命周期或 cleanup 失败 | `lifecycle-failure` |

Cancelled 边界语义：取消发生时允许存在**一个已请求但尚未成功 commit 的 frame**，因此取消 session 允许出现 `captureRequestCount = capturedFrameCount + 1`（例：28 / 27）。这是正确的事务边界，不得为了让数字相等而修改提交语义。

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
- tail 最终仍以成功 commit 的 output frame 计数，不以 wall clock 推进。completion signal 所在帧仍必须 capture。
- `ResolvedTailFrameCount = 0` 时不额外输出 tail，但 completion 当帧仍会输出；值为 `1` 时恰好再输出一个成功 commit 的帧。
- 当前不尝试自动判断所有特效、相机或行星动画何时静止；显式 tail 是可解释的确定性上界。

---

## 7. Arbitrary-length、安全上限与恢复

- 正常 session 可持续到 canonical completion，不受固定 180 帧截断，也不受任何内建总帧数 / 总时长限制（safety 默认 unbounded，见 §3.8）。
- `EditorExportSafetyFrameLimit` 是**可选**的显式 output-frame 上限：`0`（字段默认值）与 legacy 历史默认 `36000` 都表示"未配置"；其它正整数按配置值原样生效、不夹取。它只是 completion 永不出现时的 fail-safe，**不是**导出能力上限。显式上限同样约束 End Tail 解析（`end-tail-exceeds-safety-limit` 只在显式配置下可能出现）。
- 三个 wall-clock watchdog 都只是失败保护，且都不推进 `MasterTimeline`；它们分别只覆盖单个阶段或单次帧进度间隙，不覆盖整个 session：initialization readiness（frame 0 之前）30 秒；单次 capture callback 事务 30 秒；逐帧 progress（连续 30 秒没有成功 commit 的帧）。实机已出现一次 wall-clock 约 `50.45` 秒且正常 canonical completion 的 session，因此 30 秒 watchdog 不是全局 session 上限。
- terminal session 仍允许单击重新启动；启动前先执行 residual ownership gate。Completed 后仍通过已确认的 `scrController.ChangeToStartState()` re-arm 到 Start，之后才创建新 session 并调用一次官方 `editor.Play()`。Esc（Cancelled）后可直接再次 export。
- 用户关卡不会被修改。

---

## 8. Runtime 验证状态

### 0.3.6.1 — RUNTIME VALIDATED（用户实机：30 FPS 与 1000 FPS 完整导出）

实机结论（用户，session `editor_20260915_095106` 为 1000 FPS 组）：

- **`controller.paused` 阶段修复有效**：编辑器 idle 直接启动不再被拒（修复前的第一次实机曾稳定出现 `EditorExportPreflight: Ready / None` → `编辑器导出启动被拒绝：controller-paused`；根因见 §10.5），lifecycle 正常走完 `Start → OnMusicScheduled → Countdown → PlayerControl`，Initialization Hold 正常释放。
- **30 FPS 完整导出通过**：autoplay 正常（同一 output frame 曾成功执行 2 次 Hit）、canonical completion 正常、completion 后精确捕获 12 个 End Tail、`frame_000000.png`..`frame_000113.png` 连续、最终 `Completed`。
- **1000 FPS 完整导出通过**：

| metadata | 值 |
| --- | --- |
| `version` / `state` / `stopReason` | `0.3.6.1` / `Completed` / `canonical-completion-tail-drained` |
| `terminationKind` / `completionSignal` | `canonical-completion` / `scrController.OnLandOnPortal+state=Won` |
| `completionFrameIndex` | `3323`（`canonicalCompletionCallbackSeen` 与 `canonicalCompletionStateSeen` 均为 `true`） |
| `outputFps` / safety | `1000` / `safetyPolicy=unbounded`、`safetyFrameLimit=null`、`safetyDurationSeconds=null` |
| End Tail | `resolvedTailFrames=12`、`resolvedTailSeconds=0.012`、`tailFramesCaptured=12` |
| frames | `captureRequestCount=3336`、`capturedFrameCount=3336`；capture 尺寸 3072×1920 |

完整 Player.log 确认：scheduler 以 `outputFps=1000` 启动、safety 为 unbounded、lifecycle 正常到 PlayerControl、`canonical completion observed at outputFrame=3323`、最后写入/commit frame 为 3335、最终 `canonical-completion-tail-drained` / `Completed`；**未**观察到 `controller-paused`、`controller-paused-during-playback`、`hit-state-prepare-failed`、`rdc-auto-restore-failed`、`safety-limit`、`frame-index-exhausted` 或任何 Renderist watchdog failure。

**静态 / 离线证据**（与上述实机结论一致，作为回归基线保留）：

- **Output FPS legality / `Application.targetFrameRate` 派生**：`1/30/60/120/240/500/1000/10000/65535/1000000/536870911/536870912/int.MaxValue` 全部合法（正整数语义）；`0/-1/int.MinValue` 全部以 `output-fps-not-positive` 拒绝；派生量在 `536870912` 起饱和到 `int.MaxValue`，`536870911 → 2147483644` 仍精确。
- **safety 默认 unbounded**：`<= 0` 与 legacy `36000` 都解析为 `unbounded`（无 frame 上限）；`IsFrameLimitReached(0, N)` 对 `N` 直到 `long.MaxValue` 恒为 `false`；解析结果与 Output FPS 无关（改变 FPS 不会重新产生 duration / frame limit）。**显式 frame limit 原样生效**：`1/2/35999/36001/1000000/1000001/5000000/int.MaxValue` 均无夹取，判定恰好在配置值处触发；End Tail 同理（unbounded 下 `10,000,000` 帧 tail 仍可解析，显式 `5000` 帧上限仍按配置拒绝 `5000` 帧 tail）。
- **scheduler 判定路径（构建产物级）**：`PrepareFrame` 与 `CommitFrame` 的帧数判定都经 `SafetyFrameLimitPolicy::IsFrameLimitReached`，`TryStart` 经同一 `Resolve` 冻结 policy；不存在第二处 `_safetyFrameLimit` 比较。
- **long 帧号链（离线 + 构建产物级）**：`MasterTimeline.Prepare(Int64)`、`FrameCaptureDriver.BuildFilePath(Int64)`、`OnCaptureResult(Int64, Int64, …)`、`CommitFrame(Int64, String)`、`RenderistAutoPlay.CatchUp(Int64, …)` 均为 64 位帧号，帧号路径中不含 `conv.i4` 或 `Int32::ToString`；`0 / 1 / int.MaxValue-1 / int.MaxValue / int.MaxValue+1L / 3e9 / long.MaxValue` 全部无负数、无截断、无回绕；`PrepareFrame` / `CommitFrame` 内含 `_outputFrameIndex == long.MaxValue` 的结构性 fail-closed（`frame-index-exhausted`），不依赖 unchecked 递增；End Tail 的 double → long 转换在 2^63 边界显式报 `end-tail-frame-count-overflow`，而 `3e9` / `9e18` 帧这类超过 int 的合法值正常解析为 long。文件名在 `999999 / 1000000 / int.MaxValue / int.MaxValue+1L` 下完整输出编号（`PadLeft` 只补不截）。
- **`TryPrepareHitState` fail-closed 的控制流（构建产物级）**：命中调用位于 `TryPrepareHitState` 之后并由其结果守卫，失败路径跳过整个命中块；异常表 `Finally` 覆盖整个命中事务，`RDC.auto` 恢复在两条路径上都执行；判定顺序为 `rdc-auto-restore-failed` → `hit-state-prepare-failed:<原因>` → `scr-player-hit-threw`。
- **paused 阶段修复（构建产物级）**：`ValidateStartConditions` 的 IL 中已无任何 `controller-paused` 拒绝串，也不再调用 `EditorGameReflection::ReadControllerPaused`；`ReadControllerPaused` 的调用点只剩 `TogglePauseGamePrefix`（返回兼容）、`IsPlaybackReady`、`TryDetectAbnormalPlaybackPause`（playback readiness 阶段）与 Esc observer 诊断；`IsReady` 仍组合 `!paused`，`IsReadyExceptPaused(Object, Boolean)` 不含 paused；全仓库无 `paused` 写入、无 `TogglePauseGame` 直接调用、无 `Time.timeScale` 写入。
- 版本一致性：`mod/Info.json` = csproj `<Version>` = `ModEntry.ModVersion` = 启动日志 = `0.3.6.1`；`RenderTimeProbe` 未恢复（构建产物中不含该符号）。

**仍然未验证（不阻塞 0.3.6.1）**：

1. **显式 safety frame limit 的触发 / 耗尽注入**：只有静态与纯计算证据（默认 unbounded，因此这不是正常导出的终止条件）。
2. **`TryPrepareHitState` 故障注入**：只有 IL 级控制流证据，未做实机注入（需要真实 `scrPlayer` / `scrController`）。
3. 更广泛的谱面 / 时长覆盖：BPM change、Twirl、Midspin、长时导出、多轮连续导出（部分已在 0.3.5.1 覆盖）。
4. trail / star-trail 视觉状态（见下节"尚未验证 / 仍需更广泛验证"）。

### 0.3.6.0 — RUNTIME VALIDATED（用户实机，Render Time Determinism P0）

> 实机数据来自当时部署的开发构建（该 DLL 的 FileVersion 为 `0.3.5.1`、含 `RenderTimeProbe`、尚不含 multipress 中和，且**没有**启动前 paused gate）；随后的发布构建（probe 移除 + multipress invariant 固化 + 当时的 `paused` 启动前 precondition）与 `0.3.6.1` 的二进制**尚未**实机验证。

同一谱面、同一配置、仅 `outputFps` 不同（60 / 30）的两组导出，均正常 `Completed`：

| 项 | 60 FPS | 30 FPS |
| --- | --- | --- |
| `completionFrameIndex` | 201 | 101 |
| `capturedFrameCount` / `captureRequestCount` | 214 / 214 | 114 / 114 |
| `tailFramesCaptured` / `resolvedTailFrames` | 12 / 12 | 12 / 12 |
| `terminationKind` | `canonical-completion` | `canonical-completion` |
| `captureSource` / 尺寸 | `scrCamera-rendertexture` / 3072×1920 | 同 |

**engine time（逐帧观测，全帧无例外）**

- `Time.captureFramerate` 恒等于 `outputFps`；`Time.captureDeltaTime` 与 `Time.deltaTime` 恒等于 `1/outputFps`（60 → 0.016667，30 → 0.033333）。
- output frame index 与 `Time.frameCount` 均严格 +1，**1 output frame = 1 连续 Unity frame**。
- output capture frames 期间 `Time.timeScale` 恒为 `1`、`paused` 恒为 `false`；`Play` / `InitializationHold` 边界可观测到 native transient `timeScale = 0` / `deltaTime = 0`，进入 output frame 0 前已恢复 —— 因此**不需要** `timeScale` preflight 或 ownership（见 §3.7）。

**`averageFrameTime`**

- 逐帧轨迹符合 EMA `a_n = 0.5·a_(n-1) + 0.5·Time.deltaTime`，稳态收敛到 `1/outputFps`。
- **跨 session history carry 已实机确认**：30 FPS 会话的 frame 0 值恰为 `0.5 × (1/60) + 0.5 × (1/30) = 0.025`，即继承了前一 60 FPS 会话的稳态值。

**multipress block / completion**

- 两组共 22 次 `SwitchChosen` transaction：**全部 `multipressBlockEntered = false`，无 `AngleToTime` 判定事件、无 `OnDamage` 事件**；每次 transaction 入口 `keyTimes = 0`、`multipressAndHasPressedFirstPress = false`。
- 普通命中的最后一次 transaction 与 completion transaction **同样未进入该 block**。
- 这实机验证了 §3.9 的 deterministic autoplay invariant。**Renderist 不拥有 `averageFrameTime`。**

### 0.3.5.0 — RUNTIME VALIDATED（用户实机）

**Render Source Isolation**

- PNG **不含** Editor UI / UMM UI / Renderist UI；floor、planets、background、decorations 全部正常。
- 用户测试的谱面特效完整；PNG 朝向正常，**未发现上下或左右翻转，不需要 vflip**。
- 实际成功尺寸 `3072x1920`，`captureSource=scrCamera-rendertexture`。

**Input Guard**

- 导出期间普通玩家输入**不产生**额外命中，**不造成** fail。
- `Space` 无法暂停；鼠标滚轮不改变 editor camera / export。
- Hold 谱正常；RenderistAutoPlay 正常。

**Esc lifecycle / native SwitchToEditMode observer**

- 实机日志确认 observer 命中：`status=Capturing playMode=true inStrictlyEditingMode=true controllerState=PlayerControl conductorActive=false paused=false outputFrameIndex=27`，随后立即 `确定性帧调度器已停止（native-playback-stopped），终态=Cancelled`。
- `state=Cancelled` / `stopReason=native-playback-stopped` / `terminationKind=user-cancel`；**不再等待约 30 秒 watchdog**；editor 画面立即恢复。
- Esc 后可立即再次 export。

**ownership-aware Camera restore**

- Esc cleanup 时三台 Camera 均记录 `target ownership already relinquished / externally changed` / `current=null` / `savedOldTarget=''`，即 native 已 relinquish，Renderist 正确**不覆盖**。stale `camRT` 覆盖问题已解决。

**normal Completed regression**

- Esc 后再次 export 完整成功：`state=Completed` / `stopReason=canonical-completion-tail-drained` / `terminationKind=canonical-completion` / `completionSignal=scrController.OnLandOnPortal+state=Won` / `completionFrameIndex=201` / `tailFramesCaptured=12` / `captureRequestCount=214` / `capturedFrameCount=214` / `outputFps=60` / `pitch=1`。
- completion 当帧保留、End Tail 正常、successful commit semantics 正常；Esc 修复未造成 completion regression。

**0.3.4.0 基线（延续有效）**

- configurable End Tail 三种单位均正常：Frames `12` → `214`；Frames `0` → `202`（completion 当帧仍输出）；Frames `1` → `203`。
- Seconds / Beats 换算正常：completion BPM=140、pitch=1、60 FPS 下 1 beat 被离散解析为约 `26 frames = 0.433333 s = 1.011111 beats`，默认 `12 frames = 0.2 s = 0.466667 beats`，与 `ceil` frame quantization 一致。
- Running 期间 End Tail 配置 UI 被禁用（Start 时冻结）。
- canonical completion 出现在 frame 201，证明固定 180 帧不再是正常终止 authority。

### 0.3.5.1 — RUNTIME VALIDATED（用户实机，异常路径与边界专项）

**cleanup / residual ownership 收敛**

- 异常路径（会话启动阶段故障、capture 后端启动故障）后 cleanup 全项收敛：`[debug] restored captureFramerate=… targetFrameRate=… vSyncCount=… rdcAuto=…` 只在 `failures.Count == 0` 时打印，是"全项收敛"的正面证据。
- residual 跨调用保留与补做已实测：cleanup 失败后 session 进入 terminal 且 residual 真实保留；下一次 `Start` 被 `cleanup-failed:*` 拒绝（**且不创建 session 目录**）；解除故障后同一 `Start` 路径先补做 `RestoreAll` 再正常启动。
- terminal session 的 Mod disable 链路（`OnToggle(false) → Cancel → EnsureCleanedUp`）同样能补做 residual；成功时**不打印** `确定性帧调度器已停止`（`_running == false`，不走 `ProcessStop`），唯一正面证据是那条 `restored` debug 行。
- 已确认的 residual 清理项与恢复顺序：capture host / 全部 hook / forced clock / input guard / lifecycle handoff / playback / 编辑器选择 / Unity timing / `RDC.auto`。cleanup 失败时 `RDC.auto` ownership 会作为唯一残留项保留到下次补做。
- idle（无任何 ownership）时的 disable/enable 是安全 no-op：`EnsureCleanedUp` 走 `!HasResidualOwnership()` 快速返回分支，不执行 `RestoreAll`、不打印 `restored`、不产生 `cleanup-failed`。

**progression bound（单输出帧高频命中）**

- 固定 `MaxHitsPerFrame` 魔数已移除，上界改为 `floors.Count` 后已实测通过：单输出帧 27–28 次连续命中在多个连续帧上稳定成立，progression 逐次 `+1` 严格单调、零违例、零链条断裂，未出现 `autoplay-progression-bound-exceeded` 或任何其它 fail-closed 误报。
- 高密度谱需保证 `floors.Count` 显著大于单帧命中数（`progressionBound = floors.Count`，谱面过短会失去测试意义）。
- 实践换算：`floorsPerFrame ≈ B_eff / (60 × outputFps)`（`B_eff = scrConductor.bpm × scrFloor.speed`），实测与预测吻合。

**completion + End Tail（高密度谱回归）**

- 高密度谱完整跑通 canonical completion：`OnLandOnPortal`（state 仍为 `PlayerControl`）→ `Won` 确认 → `canonical-completion-tail-drained` / `terminationKind=canonical-completion`；completion 当帧仍 commit，tail 帧数精确等于 `resolvedTailFrames`，`captureRequestCount == capturedFrameCount`。
- 多帧连续高频命中不影响完成语义与 tail 计数；末 floor（`nextFloor=null`）正常命中，未触发 `next-entry-time-unavailable`。

**多轮连续导出**

- 同一游戏进程内连续多次导出（含 Completed 与 Esc Cancelled 交替）无 residual 累积：每一轮都有自己独立的一次 `restored`，无 gate 拒绝、无 `cleanup-failed`。
- Completed 之后控制器停在 `Won`，下一次 `Start` **不**经 `TryBeginTerminalControllerRearm`（`Won` 不是 Fail/Fail2），而是由官方 `editor.Play()` 自行重置 `Won → Start → Countdown → PlayerControl`；该路径已实测正常。

**RDC.auto 与官方 Auto 模式互斥**

- `RDC.auto == true`（ADOFAI 编辑器自身 Auto / 自动命中处于开启状态）会在 Start 阶段破坏 session 前置条件：控制器可能在 `InitializationHold` 期间就被游戏自身打到 `Won` 并触发 native teardown，于是永远观察不到 `Countdown` 状态提交，`PlaybackLifecycleHandoff.IsReady` 恒为 false，`InitializationHold` 永不释放，session 以 `native-playback-stopped` 取消且 `outputFrameIndex` 恒为 0（未接管捕获）。
- 该情况下 Renderist **不会**接管相机、不写帧，且**不修改游戏原有的 `RDC.auto` 值**（`savedRdcAuto` 原样恢复）——属正确的 fail-safe，不是缺陷。
- 因此实机验证与正常使用都应保持编辑器 Auto **关闭**；判定任何 session 是否有效，必须先确认日志中出现 `InitializationHold` 之后完整的 `Countdown → PlayerControl → lifecycle-ready → capture source active` 序列，再解读 `hitsThisFrame` 等帧级指标。

**编排（高密度谱的可用结构）**

- 谱面应从常规 BPM 起步、把高密度 burst 放在中后段、并在终点前回到常规 BPM 收尾到 portal；开局直接进入极端状态会让关卡在 lifecycle 完成前就结束。实测可行的梯度是"常规 BPM 起步 → 逐级提速到目标 burst → 常规 BPM 收尾"。

### 尚未验证 / 仍需更广泛验证

- **显式 safety frame limit 的触发 / 耗尽注入**：只有静态与纯计算证据，未做 runtime validation（默认状态 unbounded，因此这不是正常导出的终止条件）。
- `TryPrepareHitState` fail-closed 的**故障注入**：只有 IL 级控制流证据，未做实机注入（需要真实 `scrPlayer` / `scrController`）。
- 任意 FPS / pitch 组合、长时间大规模导出稳定性。
- 更极端的复杂谱面类型：BPM change、Twirl、Midspin、复杂角度 / 旋转方向、event-heavy chart、checkpoint / 特殊 startup（Hold 已实测正常）。
- custom resolution / supersampling、audio capture、FFmpeg、replay。
- Preview Bridge。
- **trail / star-trail 视觉状态（待录屏验证）**：用户观察在 capture 正式开始**之前**，球拖尾与星轨的运动看起来比正常轨迹僵硬。**尚未调查、未改代码、不记录根因猜测**；需要用户提供录屏后确认 `Initialization Hold` / capture activation 前后的 trail / star-trail 状态是否连续，以及是否影响实际 frame 0+ 的导出内容（30 FPS / 1000 FPS 两次完整导出的 PNG 序列本身已通过）。
- native Esc teardown 期间 Unity 引擎会打印 `Coroutine couldn't be started because the the game object 'Conductor' is inactive!`（无 mod 前缀）。静态证据指向 native teardown，Renderist 唯一的 `StartCoroutine` 位于自有 host GameObject，未观察到功能性影响；**未做禁用 mod 的 A/B 对照**，仍属待确认。

### 后续需求候选（不属当前阶段，建议 0.3.6.2 独立小闭环）

- **log-only / image-output-disabled 诊断模式**：开启详细日志时可选"不输出 PNG、只输出日志"，避免高 FPS 调试大量占用磁盘。设计要求（尚未实现、本轮不得实现）：
  - `MasterTimeline` / lifecycle / autoplay / completion / End Tail 照常运行（时间与命中语义不变）；
  - 可关闭 PNG 编码与文件写出；
  - **不要**把"详细日志"与"不写 PNG"永久绑定（两个独立开关）；
  - metadata 需要明确表示 image output 是否启用；
  - `capturedFrameCount` / written frame count 的语义（commit 是否仍以"写出成功"为条件、capture request 如何计数）必须在实现前结合当前代码确定；
  - frame commit 语义与 completion / End Tail 计数不得因此被静默改变。

---

## 9. 已知边界（当前阶段不处理）

1. **导出期间游戏窗口 world view 静止**：三台谱面 Camera 的输出已重定向到 Renderist CaptureTarget，ADOFAI 自身的 `camRT` 不再更新，当前没有 Preview Bridge（`Overlaycam` / `quad` 未接管）。**不影响成品 PNG。**
2. **audible music / metronome 仍按 wall-clock 原速**：`MasterTimeline` 只拥有 export / chart timeline（visual / gameplay logical timeline），不拥有 audible audio timeline。不得为了「听起来同步」改动 Conductor 时间设计。
3. **尚未实现**：custom resolution、supersampling、audio capture、FFmpeg、replay。
4. **尚未实现/未纳入**：任何 Loader 抽象层、多 Loader 支持。

---

## 10. 当前 DLL 关键事实（静态确认，供后续 Agent 复用）

证据来源：本机 Steam `Assembly-CSharp.dll`，FileVersion `0.4.3.0`，只读 ECMA-335 metadata + IL 扫描（未加载程序集、未使用反编译器）。

### 10.1 scrCamera 摄像机链

- 类型 `scrCamera` 存在，`public`、`baseType = ADOBase`、非 sealed / 非 abstract。
- 单例入口是 **static 属性** `scrCamera instance { get; set; }`，不是 static 字段。
- 三台谱面摄像机是 **public 实例字段**，类型均为 `UnityEngine.Camera`：`Bgcamstatic`、`BGcam`、`camobj`。
- 另有 `Overlaycam`、`PausePlanetsCam`（`Camera`）、`quad`（`GameObject`）、`camRT`（private `RenderTexture`）、`forceRTCam`、`useRTCam`、`SetupRTCam(bool)`、`camRTNeedsRecreation`。
- `scrCamera3D` 是独立类型（只有 `speed` 与 `Update`），与谱面摄像机链无关。

### 10.2 targetTexture 与 ADOFAI 自身 RT cam

- 全程序集只有 5 个方法触碰 `Camera.targetTexture`；其中唯一会写这三台摄像机的是 `scrCamera.SetupRTCam(bool)`。
- `SetupRTCam(bool force)` 语义等价于：`useRTCam = force;` → `force` 为真则把三台 `targetTexture` 设为未命名的 `camRT`，否则设为 `null`；随后 `Overlaycam.gameObject.SetActive(force)`、`quad.SetActive(force)`。
- `scrCamera.Update` 只在 `useRTCam == true && camRTNeedsRecreation == true` 时才重建 `camRT` 并调用 `SetupRTCam(true)`；`Update` / `LateUpdate` 不会逐帧重写 `targetTexture`。
- `SetupRTCam` 的全部调用点是 `scrCamera.Awake`（仅当 `forceRTCam`）、`scrCamera.Update`、`scnGame.Play`、`scnEditor.Start`、`scnEditor.SwitchToEditMode`。`scnEditor.Play` **不在**其中。
- `scnEditor.Start` 与 `scnEditor.SwitchToEditMode` 结尾都调用 `SetupRTCam(false)`（三台置 `null`）。
- `PauseMenu.UpdatePausePlanetRender` 写的是 `PausePlanetsCam`（不在接管链内）；`CameraFilterPack_Blend2Camera_*` 只在 `Start` / `OnDisable` 写各自 `Camera2`。
- 已排除第三方 Mod 干扰：`AdofaiTweaks` 对 `targetTexture` / `scrCamera` / `camRT` / `useRTCam` 的引用数为 0。

### 10.3 scnEditor 生命周期与输入语义

- `scnEditor.SwitchToEditMode` exact 签名：**`public void SwitchToEditMode(bool)`**（类内唯一同名重载）。全部调用点只有 `scnEditor.Start` 与 `scnEditor.Update` 的 Esc 分支。
- **`scnEditor.playMode` 是只读派生属性**，IL 语义为 `pausedInPlayMode ? true : !controller.paused` —— 它**不表示「是否处于播放模式」**，只反映暂停状态。这是把 Esc 检测从 polling 改为 exact observer 的原因。
- `scnEditor.inStrictlyEditingMode` 是 private `bool` 字段，全程序集内**只被写入、从不被游戏自身读取**；`SwitchToEditMode` 恒写 `true`，编辑器场景加载后恒为 `true`。**因此不能用作「playback 已停止」判据。**
- `scnEditor.Update` 的 Esc 分支在 Space 分支之前并直接 `ret`：
  `if (Input.GetKeyDown(KeyCode.Escape) && playMode) { SwitchToEditMode(false); return; }`
  因此 **Esc 不经过 `scrController.TogglePauseGame`**；`scnEditor.SwitchToEditMode` 调用的 `TogglePause(bool)` 是 `scnEditor` 私有方法（实现即 `scnGame.ResetScene(bool)`）。
- `scnEditor.Play()` 会调用 `playerManager.UnlockAllPlayerInput()`，因此 Input Guard 必须在 `editor.Play()` **之前**安装。
- `scnGame.ResetScene(bool)` 的两条 `TogglePauseGame` 分支互斥：`if (isLevelEditor && !paused)` 与 `if (isScnGame && paused)`；编辑器内只走第一条。

### 10.4 Canonical completion

- `scrController` 继承 `MonsterLove.StateMachine.StateBehaviour`；`States.Won = 7`，`Fail = 5` / `Fail2 = 6` 是失败态。
- 存在精确实例方法 `scrController.OnLandOnPortal(scrPlanet, Portal, string)`，其 IL 含原生关卡结束处理并在末尾调用 `StateBehaviour.ChangeState(Enum)`，是当前版本正常完成入口。
- `scrController.Won_Enter` / `Won_Update` 是进入胜利态后的后续处理，不是更早的完成请求入口。
- `scrPlayer.Hit(bool)` 的 IL 只处理输入、floor 进度与视觉推进，不调用 `BeatLevel` / `OnLandOnPortal` / 状态切换，也**不调用** input guard 的 6 个目标方法；因此「最后一个 floor 已 Hit」不等于关卡完成，且 Input Guard 不影响自动命中。
- `scrController.BeatLevel` 的调用只出现在调试路径与 `OttoButtonController`，不是正常完成依据。
- 最终选择：只观察 `OnLandOnPortal` Postfix + 确认 `state == Won`；不调用、不替换、不改参数、不使用 Transpiler、不从 floor index 或命中数推导完成。

### 10.5 `scrController.paused` 的写入者与阶段语义

`paused` 是属性（`get_paused` / `set_paused`）。全程序集内 **直接** 调用 `set_paused` 的只有 5 处：

| 写入者 | 语义 |
| --- | --- |
| `scrController.Awake` | `paused = ADOBase.isLevelEditor`（IL: `ldarg.0` → `get_isLevelEditor` → `set_paused`）⇒ **编辑器场景加载后 paused 恒为 true** |
| `scnGame.Play(Int32, Boolean)` | `paused = false`（IL: `ldc.i4.0` → `set_paused`）；调用者包含 `scnEditor.Play()`（IL: `ldc.i4.1` → `call scnGame::Play`）⇒ **进入播放时才清零** |
| `scrController.TogglePauseGame` | 暂停/恢复切换（也是 `Time.timeScale` 的持续写入者）；被 Input Guard 以 Prefix 抑制 |
| `RedeemCode.EnablePanel` / `MobileMenu.scnMobileMenu.Start` | 与本阶段无关的场景 |

间接路径：`scnEditor.SwitchToEditMode` → `scnEditor.TogglePause` → `scnGame.ResetScene(bool)`；`ResetScene` 的两个分支（`if (isLevelEditor && !paused)` 与 `if (isScnGame && paused)`）都通过 `TogglePauseGame` 生效，编辑器内走第一条 ⇒ **从播放返回编辑模式后重新 paused**。

因此阶段语义为：editor idle / 返回编辑模式 → `paused == true`（正常）；`editor.Play()` 之后 → `paused == false`；`Countdown` / `PlayerControl`（playback ready）→ 必须仍为 false，否则是该阶段可判定的异常。`scnEditor.playMode`（= `pausedInPlayMode ? true : !controller.paused`）在编辑器内与 `paused` 互补，`editor-already-playing` 这个启动前 gate 已覆盖"编辑器已处于播放状态"的情况，因此启动前**不需要**额外检查 `paused`。

---

## 11. 发布与部署

- 版本历史：`0.3.5.0` checkpoint = `79706b3b67c7c8c03da2cde9a0177f49a9dc7ca2`；`0.3.5.1` 为异常路径 ownership 收敛与 autoplay progression bound 的修订版本；`0.3.6.0` 为 Render Time Determinism（engine time 实机确认、`averageFrameTime` / multipress 边界定案、Output FPS 正式化、当时的 `paused` session precondition）；`0.3.6.1` 移除 Output FPS 人为上限、把 safety 默认改为 unbounded（移除内建总时长 / 总帧数上限与显式值夹取）、把 canonical output frame 编号与计数收敛到 `long`（含 long 边界 fail-closed）、把 `PrepareHitState` 改为 fail-closed，并把 `controller.paused` fail-closed 从启动前移到 playback readiness 阶段——**该版本已实机验证通过（30 FPS 与 1000 FPS 完整导出，见 §8）**。
- 发布包固定为 `Info.json` + `ADOFAI.Renderist.dll` + `LICENSE`；`dist/` 保持 Git 忽略。
- 自动验证链：`dotnet build src/ADOFAI.Renderist/ADOFAI.Renderist.csproj -c Release -t:Rebuild` → `scripts/package-release.ps1 -Configuration Release -Force` → `scripts/verify-release-package.ps1 -ZipPath dist/ADOFAI.Renderist.zip`（`-ZipPath` 是 **mandatory** 参数，省略会直接报错）。
- 部署使用 `scripts/copy-to-mods.ps1`（Release），只更新 `Mods\ADOFAI.Renderist\` 下本 Mod 自身文件，不触碰其他 Mod；可选 `-CleanRuntimeCache` 清除 UMM 运行时缓存。
- 本地 Mods 路径由 `build/local.props` 的 `AdofaiInstallDir` 决定；未配置时不猜测路径。
