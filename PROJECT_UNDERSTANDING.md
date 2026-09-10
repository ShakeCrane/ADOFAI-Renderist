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
- 四位版本号；`0.3.4.0 → 0.3.5.0` 已获用户明确批准并已同步

---

## 2. 当前版本与工程基线

| 项目 | 当前状态 |
| --- | --- |
| 产品版本 | `0.3.5.0` |
| 阶段 | `Phase 3.5.0 Render Source Isolation` |
| Git 基线 | `main`；`0.3.5.0` checkpoint 已提交为 `79706b3b67c7c8c03da2cde9a0177f49a9dc7ca2` |
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

关键不变量：**Frame N 的 PNG 没有成功写盘，就不 commit N，也不开始 N+1。** `MasterTimeline` 的 FrameIndex 是唯一逻辑时间 authority；wall clock、Unity Update 次数与 audio 都不推进时间。

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

---

## 4. 关键模块

- `MasterTimeline`：FrameIndex 是唯一逻辑时间 authority。
- `PlaybackLifecycleHandoff`：关联 Renderist-owned `editor.Play()` 的 `StateEngine.Changed` 与 `OnMusicScheduled`。
- `EditorVisualClock`：强制 `songposition_minusi` getter/setter，并精确撤销 Harmony Patch。
- `RenderistAutoPlay`：按 `nextFloor.entryTime` 消费 due floor；`RDC.auto` 只在单次官方 `Hit(true)` 事务内临时置 true 并恢复。它不拥有时间，也不决定 session 完成。
- `EndTailPolicy`：校验并把单一玩家输入的 Frames / Seconds / Beats 换算为 output-frame tail。
- `DeterministicFrameScheduler`：启动、Initialization Hold、逐帧事务、canonical completion 观测、native Esc observer、input guard、冻结/解析 tail、capture commit、停止与恢复。
- `FrameCaptureDriver`：同步 PNG 后端，带 generation 隔离。两阶段生命周期：`Start()` 只建立 generation / host / coroutine；`TryActivateCameraSource()` 才取得当前 session 的摄像机链并接管 `targetTexture`。source 未激活时 `RequestCapture` 一律拒绝，不回退 Screen framebuffer。
- `EditorExportController` / `EditorExportSession`：preflight、独立 session、metadata、Completed/Cancelled/Failed 生命周期。
- `EditorGameReflection`：当前 ADOFAI 内部 API 的运行时反射层（摄像机链、canonical completion、input guard 目标、只读生命周期诊断）。

固定 `targetFrameCount=180` 已从正常终止 authority 移除；180 帧只是历史验证基线。

---

## 5. 终止模型与 metadata

`completionFrameIndex` 是确认 `Won` 时正在捕获、尚未 commit 的 output frame index。该帧仍会正常捕获并 commit；tail 只从其后成功 commit 的 output frame 开始计数。每一帧只有 PNG 成功后才 commit。

metadata 通过 `terminationKind`、`stopReason`、`completionSignal`、`completionFrameIndex`、canonical 两个观测布尔值、`tailFramesCaptured` 与 `safetyFrameLimit` 区分。End Tail 另记录 `endTailInputValue`、`endTailInputUnit`、`resolvedTailFrames`、`resolvedTailSeconds`、`resolvedTailBeats`、`completionBpm`、`outputFps` 与 `pitch`。Render Source 另记录 `captureSource`（值 `scrCamera-rendertexture`）、`captureWidth`、`captureHeight`。

| 结果 | 条件 | `terminationKind` |
| --- | --- | --- |
| Completed | canonical completion + tail 排空 | `canonical-completion` |
| Cancelled | 用户 Stop、Esc 导致的 native stop、离开编辑器、Mod 禁用等 | `user-cancel` |
| Failed | safety 上限命中 | `safety-limit` |
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

- 正常 session 可持续到 canonical completion，不受固定 180 帧截断。
- `EditorExportSafetyFrameLimit` 默认 `36000`，最大 `1000000`；`0` 使用默认值。它只是 completion 永不出现时的 fail-safe。
- 三个 wall-clock watchdog 都只是失败保护，且都不推进 `MasterTimeline`；它们分别只覆盖单个阶段或单次帧进度间隙，不覆盖整个 session：initialization readiness（frame 0 之前）30 秒；单次 capture callback 事务 30 秒；逐帧 progress（连续 30 秒没有成功 commit 的帧）。实机已出现一次 wall-clock 约 `50.45` 秒且正常 canonical completion 的 session，因此 30 秒 watchdog 不是全局 session 上限。
- terminal session 仍允许单击重新启动；启动前先执行 residual ownership gate。Completed 后仍通过已确认的 `scrController.ChangeToStartState()` re-arm 到 Start，之后才创建新 session 并调用一次官方 `editor.Play()`。Esc（Cancelled）后可直接再次 export。
- 用户关卡不会被修改。

---

## 8. Runtime 验证状态

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

### 尚未验证 / 仍需更广泛验证

- `safetyFrameLimit=36000` 的专门异常注入：只有静态代码 / build 证据，未做 runtime injection。
- 任意 FPS / pitch 组合、长时间大规模导出稳定性。
- 更极端的复杂谱面类型：BPM change、Twirl、Midspin、复杂角度 / 旋转方向、event-heavy chart、checkpoint / 特殊 startup（Hold 已实测正常）。
- custom resolution / supersampling、audio capture、FFmpeg、replay。
- Preview Bridge。

---

## 9. 已知边界（当前阶段不处理）

1. **导出期间游戏窗口 world view 静止**：三台谱面 Camera 的输出已重定向到 Renderist CaptureTarget，ADOFAI 自身的 `camRT` 不再更新，当前没有 Preview Bridge（`Overlaycam` / `quad` 未接管）。**不影响成品 PNG。**
2. **audible music / metronome 仍按 wall-clock 原速**：`MasterTimeline` 当前只拥有 visual / gameplay logical timeline，不拥有 audible audio timeline。不得为了「听起来同步」改动 Conductor 时间设计。
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

---

## 11. 发布与部署

- `0.3.5.0` checkpoint：`79706b3b67c7c8c03da2cde9a0177f49a9dc7ca2`（`main`）。
- 发布包固定为 `Info.json` + `ADOFAI.Renderist.dll` + `LICENSE`；`dist/` 保持 Git 忽略。
- 自动验证链：`dotnet build src/ADOFAI.Renderist/ADOFAI.Renderist.csproj -c Release -t:Rebuild` → `scripts/package-release.ps1 -Configuration Release -Force` → `scripts/verify-release-package.ps1`。
- 部署使用 `scripts/copy-to-mods.ps1`（Release），只更新 `Mods\ADOFAI.Renderist\` 下本 Mod 自身文件，不触碰其他 Mod；可选 `-CleanRuntimeCache` 清除 UMM 运行时缓存。
- 本地 Mods 路径由 `build/local.props` 的 `AdofaiInstallDir` 决定；未配置时不猜测路径。
