# 0.3.12.0 — Camera & Particle Visual Time Ownership

状态：用户已批准 `0.3.11.0 → 0.3.12.0`。**Gate A 功能主路径实机 PASS（§8.5），视觉 FAIL（§8.6）。最新调度见 `PROJECT_UNDERSTANDING.md` §2.2.23 与 [现有渲染器源码研究](EXISTING_RENDERERS_RESEARCH_0.3.12.0.md)：R0a/R1 自研观测设施设计暂停，先审查现有实现的帧内交付等待与本项目主线程回执/取消的兼容性。常规 Gate B Tween 和正式 Particle owner 未放行；§8.7 隔离粒子语义矩阵保留，不自动启动。** 第 1–7 节保留技术方案与已完成步骤。

> **执行进度（2026-10-17 dsh）**：§7 步骤 **1–3 已实现**并自动化通过，源码 / Release 复建 / 游戏 `Mods` 部署均为 `0.3.12.0`（`Phase 3.10.0 Camera & Particle Visual Time Ownership`）。新增 `Export/VisualFrameAuthorizer.cs` 与 `Export/CameraNativeTimeOwnership.cs`；scheduler 复用 capture generation 绑定授权、在 Prepare 边界发布、在 `FrameCaptureDriver` 的 EOF 读回**之前**校验并关闭授权。**步骤 4–8 未执行**：Camera Tween owner、Shake RNG scope、Particle 语义门槛 / 正式 owner、联合视觉验收、NVT 与探针清理都还没有开始。
> **Gate A 首轮 Correctness 修正**（历史，§8.2 / 项目理解 §2.2.17）：static Time getter 的替换序列已修为 `ldarg.0 + call helper`，真实 DLL 静态核对 4 个方法 / helper 计数 `3/1/1/1`；Arm 注册清理与 EOF 同步中止已修。但 Waiting 的 Postfix、Sealed 的完成 key、独立 UpdateSize 与 frame 0 nested/snapshot 仍有生命周期漏项，不能把首轮 382/0/0 当作这些路径已正确的证据。
> **当前 Gate A 生命周期接管结果（GPT Work，同版本）**：上述漏项及有行为证据的异常/迟到回调隔离已修，生产 owner/authorizer 的执行测试通过；完整 net48 **401/0/0**。当前 DLL/部署 SHA256 `F97722C6…` / 501248 bytes。只有主 Update 领取 Camera 步；独立 UpdateSize 为 0，嵌套调用独立清理并共享主步；Waiting 无回执，Authorized 零步也要回执；Sealed 每次调用配对；frame 0 快照/首次/nested 都为 0。详见 §8.4 与项目理解 §2.2.18。**Gate B 未开始。**
> **必须记住**：**目标 Unity Gate A 功能主路径已于 2026-10-17 实机 PASS（四轮 session，§8.5）** ：四轮均已证实目标 Mono 安装四个 Patch（计数 3/1/1/1），正常主路径经过 Sealed/G/EOF 而无相位拒绝；这不等于有逐个原生帧 getter trace，也没有故障注入证据。`editor.Play()`→`Arm` 同步窗口、Mono 异常 Finalizer 和 custom FPS 特例仍待针对性验收，不能擅自改变游戏生命周期。

## 1. 初始设计审查基线（历史；当前构建身份见 §8.4）

- 本地 `main` / HEAD `9f9ff34eee4ff1f9a73a260d7d753219297d0950`；相对本地跟踪的 `origin/main` 为 0/0，未访问远端确认。开始本轮时 20 个 tracked 文件有 unstaged 修改，另有 4 个 untracked 源码/测试文件；无 staged 修改。它们包含 L3-C Finalizing、hardening 和 NVT，必须保留，不能用 HEAD 覆盖工作区。
- ADOFAI Steam public buildid `24397494`；`Assembly-CSharp.dll` FileVersion `0.4.3.0`，SHA256 `0D1524DABF0F8C67AA58BA1992D6ED858522B9094490EBBB00E5E8AEFF7F40CD`；与现有 Atlas 扫描来源相同。本轮直接复查相关 IL，未复制 DLL 或引入游戏编译引用。
- 最新 `Player.log` 为 2026-10-13，只包含启动/退出，没有新导出。原第二轮 NVT 保留在 2026-10-10 的 `Player-prev.log`；其已完成的取证不重跑。启动日志再次确认 Unity `6000.3.10f1` / Mono、Renderist `0.3.11.0` 诊断构建。
- `build/local.props`：游戏安装在 `D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice`，Unity 引用取实际 Managed；UMM 引用取 ignored `references/UMM`。本轮逐字节核对两份 UMM/Harmony 与游戏安装中的对应 DLL：UMM `0.33.0`、Harmony `2.3.6.0` 相同。`net48` / C# 9 不变。
- DOTween 实际版本字符串 `1.2.765`（Assembly FileVersion `1.0.0.0`），SHA256 `8E90FC43AB564A5A51424AF56531AB074A5A2E9CAB271B2D653D287954237B83`；与 `dump-dotween` 来源一致。
- 现有发布/部署 DLL SHA256 `3001F7D851B9D31C5C4EE5FD15D8BE0B712B24A65AF1D862F39D74D3C7C2F74D`，460800 bytes。2026-10-17 Release Rebuild 后仍相同。

只增加 Camera owner、Planet Particle owner，以及 scheduler 内小型授权合同。`MasterTimeline` 仍是唯一逻辑时间权威；沿用既有 Planet Tween owner。保持 UMM-only、editor-first、autoplay helper、不依赖 replay、不改 README。L3-C 的单帧交付、计数、Finalizing、核验和原子发布规则不改。

## 2. scheduler 的共同授权合同

实现一个 Unity-free 的值类型/小型状态对象即可；它不是全局视觉管理器，也不主动推进时间。

每次 Prepare 发布不可变授权：既有 capture `generation`、absolute `outputFrameIndex`（long）、本次允许消费的 Unity frame、chart sample、visual scaled/unscaled 增量、pre-entry/G 标识。使用现有 generation，不能另建 session authority。

| 阶段 | 授权与消费规则 |
| --- | --- |
| owner 安装至首帧 | 在 `editor.Play()` 前完成安装、绑定 generation、登记当前实例；初始化目标/原生 Play/Clear 等生命周期照常执行，时间增量为 0。不能等第一次 AwaitingDelivery 才暂停。 |
| frame 0 | 在已确认的初始化状态上采样，默认视觉增量 0；不得套用 Planet 特例的 `creationLeadSteps=4`。首帧初始化是否还需专属原生 bootstrap，由第 7 节的定向验证裁决，不能用任意 Unity Update 次数补齐。 |
| 普通 pre-entry / gameplay | 相邻 chart sample 相差 `pitch/FPS`；visual unscaled 增量为 `1/FPS`，scaled 增量为该步适用的原生基础 timeScale/FPS。pitch 不再额外乘进视觉增量。 |
| B−1 EOF 后、hidden | 关闭已消费授权；hidden 只过生命周期，不发新视觉步。B−1 commit 延迟多少 Unity frame 都不能再消费。 |
| G partial | 用既有 `partialFraction=(canonicalStart-previousBoundaryTime)/(pitch/FPS)`，visual unscaled 为 `partialFraction/FPS`，scaled 再乘既有保存的基础 timeScale。不能直接读滞后一帧的 `Time.deltaTime`，也不能把已缩小的 timeScale 再乘一次 fraction。 |
| G EOF / commit → G+1 Prepare | G 已消费，恢复原生 timeScale 不授予任何新步；G+1 Prepare 才发完整步。 |
| EOF→交付等待 / Finalizing / 取消 | 不发授权、不补时、不因重新进入 Unity Update 重放授权。 |

这里的 visual unscaled 是受控视觉步长；它不是墙钟 `unscaledDeltaTime`。G 的 output duration 仍为 `1/FPS`，不能与 partial visual span 混为一谈。原生 timeScale 的适用相位、pitch 非 1、首次初始化必须纳入验收，不能凭当前测试谱面的 timeScale=1 写死。

各消费者有独立回执：Camera 原生 Update、各 owned Tween、整个 Particle batch。不是第一个消费者读完就全局清空。一次成功/开始过的消费不能在同一逻辑帧重试；半步失败即失败该帧，绝不补走剩余时间。

Gate A 实现合同：Waiting 中的调用从开启到正常结束保持无消费/无回执，不据 `Claimed=false` 失败；Authorized 下主 Update 必须实际领取并完成（frame 0 的真实授权两个增量也是 0），无读取或未完成仍阻止 EOF。Sealed 是跨 native frame 的 lifecycle-only 授权；每次独立主 Update 仍配对登记/完成自己的 key，上次未完成不得覆盖。只有主 Update 领取 Camera 槽位，独立 UpdateSize 返回 0，嵌套 UpdateSize 通过 root 共享步，但每次调用都有独立的异常/清理 token。所有完成操作校验完整 key 和 generation，旧会话回调不操作新 owner。

在 capture driver 的既有 EOF coroutine **读回或返回 Log-only 成功之前**验证所有必需回执，并关闭授权。只在 `OnCaptureResult` 检查会晚于 PNG 写盘/RGB24 形成，不能作为唯一关口。此处只加只读验证入口，保留既有捕获/lease/交付/Commit 协议。

授权限定 Prepare 所在的原生消费帧。Camera 或 Tween 早于 Prepare、Particle 晚于渲染、调用重复或实例创建晚于本步相应消费相位，都必须给出具体相位错误；不能把消费顺延至任意等待帧，或在 EOF 临时补跑原生 Update。

## 3. Camera：保留原生运算，仅替换局部时间读取

### 已确认的 Patch 目标

| 当前 DLL 的 exact signature | Patch 与目的 |
| --- | --- |
| `private void scrCamera.Update()` | Prefix/Postfix/Finalizer 管调用作用域；窄 Transpiler 只替换 3 处 `Time.get_deltaTime()`（timer、rottimer、coltimer）。 |
| `public void scrCamera.UpdateSize()` | 同一时间 helper，替换 1 处 `Time.get_deltaTime()`（pulsetimer）；由 Update 嵌套调用时共用该次授权，不能再领一步。 |
| `public void scrCamera.CheckUpdateFrameRateScreen()` | 仅替换 1 处 `Time.get_time()`，让 native custom-FPS 检查读局部累计视觉时间。保留 lock 标志的消费和原有分支。 |
| `private void scrCamera.UpdateCustomFrameRateScreen()` | 同样替换 1 处 `Time.get_time()`，使 `lastCustomFPSUpdate` 写入与比较使用同一时钟。 |
| `private void scrCamera.LateUpdate()` | 保留原方法；只在确有需要时加 Postfix 观察相位，不跳过世界 Z 整理。 |

`UpdateSize` 还有编辑器 `ZoomCamera` 闭包调用点；虽然现有 Input Guard 抑制该入口，helper 仍必须按 Camera 实例和当前调用作用域隔离。导出期间外部独立调用只得到 0 增量，不能偷领另一帧。

**这是“避免 Transpiler”的有限例外选择**：整个 Update 跳过会丢 RT 重建、BPM/speed-trial 重算、positionState 写目标、一次性 async input 覆盖；保存再恢复全部视觉字段会覆盖原生目标语义；Prefix 预减 delta 再由原生加回也存在 float 舍入和分支耦合，不能证明等待期间值恒定。只替换已确认的时间调用点，原分支、Lerp、Ease、Transform、RT 与目标写入继续由原代码执行；不 Patch Unity 全局 Time getter，不临时改全局 timeScale。

Transpiler 必须验证方法签名、目标调用数量和周边 IL 结构，保留 labels/exception blocks；未知 IL 或其它 Patch 导致形态不符时拒绝安装。helper 使用 `ldarg.0` 传入实例并检查 owner/generation/作用域；无活动 owner 时读原生 Time，活动 owner 无授权时返回 0。禁止宽泛替换整个程序集。

局部绝对时间从接管时记录的原生 time anchor 加 scheduler 授权累计值投影，不能在每个 Unity Update 累加；custom-FPS 的两个读取必须同时处理。RT/aspect/capture target 仍归既有 capture driver，Camera owner 不另建 Camera/RT 管线。

### Harmony 异常与退出

- Prefix 的 `__state` 先登记有效恢复 token，再开放调用作用域；Postfix 只在原方法正常结束后记回执。
- Finalizer 与 Prefix/Postfix 放在同一 Patch 类，使用相同 `__state`。Finalizer 无论原方法还是其它 Patch 抛错都幂等关闭该作用域；保留原始异常，不返回 null 吞掉游戏错误。owner 自身清理错误只请求 scheduler 停止并保留 residual。
- 异常后该帧授权失效，不重试、不 Commit；无法事务回滚原生 RT/Transform/回调副作用时，走既有失败/cleanup 路线，不能伪装成回滚成功。
- 逐个登记安装目标与具体 Patch MethodInfo；安装一半失败也能撤销已装部分。只移除自己的方法，不用全局 UnpatchAll；撤销失败保留引用并阻止 restart。
- 必须在目标 Unity Mono 验证实际 Patch 成功、异常 Finalizer 执行、嵌套 UpdateSize 与 custom FPS；net48 编译不能代替这些结果。

## 4. Camera Tween：独立实例，以原生 per-tween 更新消费授权

### 获取边界

| 生产者（均保留原方法执行） | 必须获取的实例/约束 |
| --- | --- |
| `void ffxCameraPlus.StartEffect(scrPlanet)` | private static `moveXTween/moveYTween/rotationTween/zoomTween`；依据实际分支取新实例，核对 setter 写入对应 Camera parent、camAngle、zoomSize。原替换路径是 `Kill(true)`。 |
| `void ffxCamShake.StartEffect(scrPlanet)` | 实例字段 `tween`，Vector3 Shake setter 写目标 Camera.shake；原替换是 `Kill(false)`。 |
| `void ffxShakeScreenPlus.StartEffect(scrPlanet)` | static `shakeTween` **及**该次创建的 multiplier/value 包络 Tween。IL 176/257/311 的 float `DOTween.To` 返回值被丢弃，不能只检查 shakeTween；这些 setter 共享/嵌套闭包，决定最终 shake 幅度。原 shake 替换是 `Kill(true)`。 |
| `void scrPlayer.CancelHold(bool)`（private） | 只接管闭包写 `holdOffset` 的 Vector2 Tween；同方法另建的 Transform.DOMove 不属于本 owner，绝不能整体拿走。 |
| `void scrCamera.FlashPlus(float, Color?, Color?, bool, Ease)` | 只接管本次相机 flash 材质 DOColor；保留原生材质 `Kill(material,true)` 和即时尺寸/颜色写入。 |
| `void ffxFlashPlus.DoFlash()`（private） | `fgFlashTween/bgFlashTween` 和对应材质；保留原生完成回调写 `material.color=endColor`。 |

每个生产者用 Prefix/Postfix/Finalizer 建立可嵌套创建作用域，使用 active playing+paused 实例差分、已确认字段、getter/setter MethodInfo 和真实闭包/Camera/材质引用交叉核对。不能只用 target/id，也不能要求本次全局恰好新增一个 Tween。接管已有相机相关实例的启动快照；无法确认身份时具体报告，不能把未知实例算成已覆盖。

包络 Tween 与 Shake 作为同一因果组登记，但保留各自身份、原创建/更新顺序。回调中再建 Tween 不递归消费当前步：当前 DOTween 原生批次在入口保存 `_maxActiveLookupId+1` 作为遍历上界，不能改成反复 drain。嵌套作用域必须去重，不由两个 producer 重复认领。

### 时间路径选择（静态审查已明确）

当前 DLL 存在 exact method：

`static bool DG.Tweening.Core.TweenManager.Update(Tween, float, float, bool)`。

采用这个方法的 **Prefix/Postfix/Finalizer**，仅对 owner 字典中严格命中的 Camera Tween 替换两个 delta 参数；未命中实例完全透传。使用 `Tween __0, ref float __1, ref float __2, bool __3` 等位置参数，避免依赖内部参数名称。保持原生 DOTweenComponent/manager 调用相位与遍历顺序，不手动调用整个 manager，也不跳过原方法。

原生方法先处理 Tween link / active / isPlaying，再读取 delta、乘实例 timeScale，最后以 UpdateMode.Update 调 `DoGoto`，按返回值 `MarkForKilling`。无授权时两个 delta 都置 0，使原生时间分支不推进；不能直接 return false 丢掉非时间的 link/销毁处理。

已授权时 scaled delta 按受控视觉步与当前 DOTween 全局 timeScale 投影，independent delta 还保留 `DOTween.unscaledTimeScale` 的既有因子；实例 timeScale 留给原方法乘，不能重复乘。两种 delta 都不采用等待期间墙钟。仅允许预期的自动更新调用，重复消费/外部单实例 ManualUpdate 请求不能再领一步。具体相位在第 7 节验证，不能只凭脚本名称推断执行顺序。

**不把 Pause/Goto 用作本轮默认推进方案**：本 DLL 中普通 `Goto` 走 UpdateMode.Goto，首次 onStart/onPlay 与原生 Update 的路径不同；`TweenManager.Goto` 返回 `DoGoto` 结果，而公开扩展丢弃该结果，不能从“到终点”推定已执行原生自动回收。Planet 的 terminal-hold 特例不得推广到 Camera。`Tween.ManualUpdate` 确实存在且走原生单实例更新，但暂停状态下不执行；改为 Manual updateType 又会改变原 manager 调度/回收相位。本轮选择 per-instance delta Prefix，避免这些额外状态变更。

保留原始 Ease、getter/setter、delay、loops、AutoKill、isPlaying 和全部回调，不清空回调，不 SetAutoKill(false)，不为清理而强制 Complete/Kill。原生 `Kill(true/false)`、替换、回调清 shake 继续原样发生；已退休旧记录不能复活。可回收 Tween 对象引用存在复用风险：首轮严格审计 `isRecyclable=false`，不满足时拒绝接管并交 GPT Work；不能拿相同对象引用当作永久创建身份。

### Shake 随机性与恢复

当前 `DOTween.Shake` 在创建阶段调用 Unity Random.Range 并生成数组。之后冻结时间不能消除创建时随机差异。只暂停/定位现成 Shake 不能据此宣称跨导出逐帧等价。

实现时在已确认 Camera producer 内调用的 exact `DOTween.Shake(DOGetter<Vector3>, DOSetter<Vector3>, float, float, int, float, bool, bool, ShakeRandomnessMode)` 工厂处建立局部 RNG scope：保存 Random.state，用冻结的 visual seed 与确定性事件/创建序号生成种子，调用原生 Shake 一次，最后恢复 Random.state。generation、Unity InstanceID、Unity frameCount 和墙钟不得进入 seed。getter/setter/Ease/生成数组不重建；无关 Shake 不进入 scope。

该恢复也必须由 Finalizer 保证；恢复失败计入 residual 并停止当前帧。固定 seed 的两次创建、创建期间故障、外部 RNG 状态完全恢复均属于实机门槛；尚未验证。Particle 的 seed/初始化另见下节，不能因此宣称所有视觉随机源都已统一。

## 5. Planet Particle：暂停自动积分，独立保留原生生命周期意图

### 发现与原生行为

遍历当前 `scrPlayerManager.instance.players` / `allPlayers` 的实际玩家，取各 `scrPlayer.planetarySystem`；按真实引用去重。合并每个系统的 `allPlanets/planetList/availablePlanets`、已知命名行星与 chosen，再取每个 PlanetRenderer 的 tail/coop/core/sparks/deathExplosion 及包含 inactive 的粒子子树。不能沿用 NVT 的最多 6 个系统或缓存层级快照。

保留已登记实例到 Unity 真正销毁或可靠完成释放，不能因为它离开 planetList、换名、失去当前字段引用就丢弃。coop Awake 会重指 tailParticles 并使原 tail 的 `...old` GameObject inactive；重新激活必须再次发现/暂停。范围仅为已关联的 Planet 粒子，不接管全场景 decoration 粒子；场景 orphan 审计只能证明所测实例，不能宣称全场景无遗漏。

原生生命周期方法均为实例 `void`：`PlanetRenderer.Awake()`、`Revive(bool)`、`PlayParticles()`、`StopParticles()`、`DisableParticles()`、`ClearParticles()`、`Destroy(bool)`、`Explode(float)`。使用 Prefix/Postfix/Finalizer 观察本次动作及实际实例，原方法照常执行；完成后立即暂停仍需受控积分的实例。`Explode` 通过激活 deathExplosion GameObject 触发其行为，没有显式 Play，不能只 Hook PlayParticles。

| 原生意图 | owner 行为 |
| --- | --- |
| Play / Revive / 爆炸激活 | 允许原生目标、配置和播放动作；马上建立/更新实例记录与暂停状态，下一授权相位才能积分。 |
| StopParticles | 当前 IL 是 `Stop(true, StopEmitting)`，常量 1；记录 draining，后续授权仍须衰减现存粒子，不重新发射。 |
| DisableParticles / Destroy(bool) | 当前 IL 是 `Stop(true, StopEmittingAndClear)`，常量 0（deathExplosion 另有 Clear/SetActive）；保留已清空/禁用意图，不用 Simulate/Play 复活。 |
| ClearParticles | Clear 不等价于 Stop；清空粒子与继续发射意图分别保留。 |
| scrPlanet.Update 的 core emission | 原生每帧用 isChosen 写 emission.enabled，不能由 owner 恢复旧 emission 开关覆盖它。 |

记录原生意图与 owner 为冻结而造成的 `isPaused` 必须分开；不能每帧把 `isPlaying=false` 误解为游戏主动停止。原生重复 Play、StopEmitting 后 Play、owner 暂停后 Play 之间的差异，见运行时门槛。不得用每步 Play/Stop 切换来猜测恢复语义。

### 模拟相位与参数

候选为该 owner 自己的一个 PlayerLoop slot：本帧所有原生 Transform/LateUpdate 已执行、原生 Particle jobs 已收敛、Camera cull/render 尚未开始的位置；优先验证 `PostLateUpdate.ParticleSystemEndUpdateAll` 之后。当前 DLL 有 `PreLateUpdate.ParticleSystemBeginUpdateAll`、`ScriptRunBehaviourLateUpdate`、`PostLateUpdate.ParticleSystemEndUpdateAll` 类型，**类型存在不证明实际 PlayerLoop 的顺序或可用插槽**，须读取目标运行中的 GetCurrentPlayerLoop 并验证。

不用每个 PlanetRenderer.LateUpdate Postfix 各跑一次：它不能保证其它行星 Transform 已更新，也不能防止父子系统重复积分。不在 EOF 读回后才模拟。

安装时读取当前 loop，精确插入自己的唯一 delegate；卸载时在**最新** loop 中只删除自己的条目，不能把启动时的整棵 loop 快照写回、覆盖其它 Mod 的修改。安装半失败、节点丢失、重复节点、删除失败均计入残余门禁。

所有已控制粒子在原生 BeginUpdate 之前保持暂停；原生 Play/激活结束后立即恢复冻结，不能等本帧末再补 Pause。候选调用 `ps.Simulate(authorizedDelta, withChildren:false, restart:false, fixedTimeStep:false)`；所有受控子系统显式去重，以免 Simulate(true) 再单独模拟子系统。保留 native simulationSpeed，先验证 Unity 是否已将它应用到显式 t，禁止再猜乘一次 speed/pitch。

**这条调用尚未获目标 Unity 行为验证**：官方只承诺模拟给定时长后暂停；未证明暂停状态仍正确连续发射、StopEmitting 只排空、Play/Revive 不改变本应有的重启/prewarm 行为。`time` 可循环，不能单独作年龄凭据；用 totalTime、粒子稳定 randomSeed/寿命/位置与发射数量联合验证。draining 清空前不能释放记录。

初始粒子状态、useAutoRandomSeed/randomSeed、prewarm、culling、rateOverDistance、trails、sub-emitter、stopAction、child graph 必须在最小目标 Unity 验证中实际记录。跨导出比较使用相同初始状态/seed；未经验证的自动 seed 不能算成确定性通过。若需要设置正式 seed，应在原生生命周期开始前登记其原值并保证恢复，不得在已发射后写 seed 假装修复历史。

不为取得一致初态擅自 Clear 用户原有粒子、不重建效果、不修改关卡。若原生初始化无法提供可复现起点，或暂停改变重复 Play/Stop 的意义，停在该阶段交 GPT Work 定向修正，不以全局 simulationSpeed=0 或每帧 restart=true 蒙混通过。

## 6. 共同失败与清理合同

先封闭授权/使旧 generation 失效，再关 EOF 请求；原生生命周期恢复按既有 Stop→编辑模式路线执行。owner 的物理冻结与生命周期 observer 在恢复所需阶段仍可用，不能 `_running=false` 就让部分冻结泄漏，或在先恢复 Unity timeScale 后留下活动系统多跑。

只撤销自己的 Patch/loop slot/RNG scope。Tween 不强制完成/杀掉，实例 delta 拦截撤销后自然回归原生；已原生销毁的实例不复活。Particle 按**最后原生意图**恢复，不能盲目恢复接管前的 isPlaying：之后游戏已执行 Stop/Clear/Destroy 的实例绝不 Play。正在排空者也不能因 cleanup 重启发射；该分支必须由实机语义门槛证明。无法恢复时保留具体实例及恢复 token，阻止新 session。

所有 owner 的 `HasResidualOwnership/TryDispose` 汇入 scheduler 既有 residual gate；失败不清引用，Stop/restart/Mod disable 可继续收敛。normal completion 只有 Unity 侧全部恢复成功才允许把同一 RGB24 context 交给 L3-C Finalizing；任何恢复失败按原取消路径撤销交接，不发布失败会话。

## 7. dsh 执行顺序、验收与停止条件

以下保留阶段全计划。**当前调度按 `PROJECT_UNDERSTANDING.md` §2.2.23：步骤 4 暂停；步骤 5 的粒子语义矩阵保留，须在交付结构裁决后确定是否执行；步骤 6 未放行；步骤 7–8 亦未执行。R0a/R1 自研设施设计也暂停。** 每阶段先验证再扩展；没有把未来结果写成已通过。

1. **版本与引用**：用现有 `set-version.ps1` 原子同步 Info.json/csproj/ModEntry 和需要更新的 Phase 文本至 `0.3.12.0`；包版本从 Info 派生，不另造版本配置。核对本节 DLL/hash/Mono 与实际引用，ParticleSystemModule 转为 Private=false 的正式引用。保留当前全部未提交工作，不改 README。 → **已完成升版**；`ParticleSystemModule` 的正式化**刻意推迟**：步骤 1–3 没有任何消费方，正式化并入步骤 8 与探针一起收尾（当前引用已是 `Private=false`，只是注释仍标记为 TEMP DIAGNOSTIC）。
2. **授权合同**：先实现 generation/frame/phase、first-frame/普通/G partial/hidden/等待的数值投影和一次消费/EOF 完整性；补 Unity-free 行为测试（重复、过期、失败、两 owner 独立回执、极端合法 FPS 的数值表达）。不改 L3-C 状态与计数 authority。 → **已完成**（`Export/VisualFrameAuthorizer.cs` + `VisualFrameAuthorizationTests`）。
3. **Camera 原生 owner**：安装上述窄时间替换与 Finalizer；验证原生目标/async input/RT/custom FPS 仍执行。故障注入覆盖安装半失败、原生 Update 异常、嵌套 UpdateSize、restore/unpatch 失败与 restart 门禁。 → **代码已实现且目标 Unity 功能主路径已实机 PASS（§8.5）**，四个 Patch 的 Mono 安装/计数及正常捕获、取消重启已验证；异常 Finalizer 故障注入和特殊 custom FPS 仍为独立待验收边界，不得视为已测试。
4. **Camera Tween（当前暂停）**：恢复开发须先完成 §8.7 的定向裁决。原技术方案保留：分生产者逐个接管；先 Move/Rotate/Zoom，再 Hold/Flash，再 Shake 完整因果组、局部 RNG 恢复。核对 per-tween Prefix 确在原生调用相位执行；两次同帧 Hit、覆盖、Kill(true/false)、自然完成、AutoKill、callback once、0 duration、paused/已退休/对象身份安全分别验证。若 Harmony 无法可靠 Patch 当前 Mono 内部目标，交 GPT Work，不擅自改成全局 ManualUpdate。
5. **Particle 最小语义门槛（需一次目标 Unity 定向运行）**：先用隔离且可清理的验证对象验证 Pause→连续 Simulate、StopEmitting→Pause→Simulate 直到排空、Clear 后继续发射、Stopped/Paused/Playing 各态下 Play、Revive/Explode 的激活与 prewarm；对照原生单步，检查 lifetime/count/seed/position/trails。记录当前 PlayerLoop 与 render 前后相位。必要时此阶段先构建/部署仅供验证的 `0.3.12.0` 构建，明确身份；不宣称 Particle owner 已完成。它验证新 API 的语义，**不是重跑已有 NVT 漂移取证**。
6. **Particle 正式 owner**：只有第 5 步通过才接入真实 Planet 全池/coop/旧系统、lifecycle、PlayerLoop slot 与恢复。覆盖 StopEmitting 排空、不活跃/销毁/场景切换、Play/Revive 后再次冻结、Cancel→Restart。任何门槛失败停止该阶段，由 GPT Work 接管，避免 dsh 反复试同一未知 native 行为。
7. **联合实机视觉验收**：30/60 FPS，pitch=1 与非 1；frame 0、B−1、首次 hidden、G partial、G+1、两次 Hit/覆盖、tail。用有控制的同逻辑帧交付等待与无延迟对照，要求 EOF→等待→commit 的 Camera 时间/相关 Tween/粒子年龄不再推进；逐帧对比使用匹配初态/seed。跑现有 L3-B 取消/lease 双屏障，L3-C 254/254 封装核验发布、Finalizing 中取消/重开门禁，以及 PNG/Log-only smoke。MP4 字节相同不是视觉唯一判据。
8. **清理与版本闭环**：只有视觉验收通过、可靠结论已记录后，删除 NativeVisualTimeProbe、所有 NVT 接线/横幅/diagnosticProbe/临时契约测试及新语义验证探针；保留正式使用的 ParticleSystemModule 引用。全量回归、Release Rebuild、package、strict verify、diff --check、状态检查，重新记录正式 DLL/ZIP/部署哈希。已有有效 Mods 路径，待实机的新构建成功后只部署 Renderist 自身文件。稳定可回退闭环形成后再决定提交/推送，本轮没有代替该决定。

最小正式改动范围：两局部 owner、一个 Unity-free 授权合同及对应行为测试；scheduler 的安装/绑定/Prepare/EOF/cleanup/residual 接线，capture driver 的 EOF 前置验证入口；版本载体、必要的 metadata 投影与项目理解。原 RGB24 transport、L2 pipeline、FinalizationPolicy/verifier/publisher 不因本功能重构。

## 8. 本轮实际验证与参考

### 8.1 2026-10-17 dsh —— Gate A（§7 步骤 1–3）

- 目标 DLL 静态核对（独立 `System.Reflection.Metadata` 工具，只读，不加载程序集）：`scrCamera` 存在（`base = ADOBase`）；`Update` RVA `174152` 3 处 deltaTime（`0197` timer / `054C` rottimer / `05B8` coltimer）、`UpdateSize` RVA `175788` 1 处（`0013` pulsetimer）、`CheckUpdateFrameRateScreen` RVA `176955` 1 处 time（`0018`）、`UpdateCustomFrameRateScreen` RVA `177016` 1 处 time（`0001`）；形态核对 **4/4 目标、6/6 读取点、0 mismatch**。调用点核对：`UpdateSize` 由 `scrCamera.Update +060E` 嵌套调用一次，另有编辑器 ZoomCamera 闭包 +004A；`UpdateCustomFrameRateScreen` 只由 `SetCustomFrameRate +0041` 与 `CheckUpdateFrameRateScreen +0035` 调用。Harmony `2.3.6.0` 已确认存在 `Patch(MethodBase, HarmonyMethod prefix, HarmonyMethod postfix, HarmonyMethod transpiler, HarmonyMethod finalizer)` 与 `Unpatch(MethodBase, MethodInfo)`。
- net48 回归（固定本地 Gyan 9.0.2 fixture）：**371 passed / 0 failed / 0 skipped**，含新增的 `VisualFrameAuthorizationTests`（授权合同行为）与 `CameraVisualTimeContractTests`（接线 / 安装 / Finalizer / 窄替换 / authority 边界）。
- Release Rebuild（no-restore）：**0 error / 0 compiler warning**（仅离线 NuGet 源导致的 `NU1900` 环境警告）；DLL `497152` bytes、SHA256 `35DDD80C7E0D3728210D85315A6430DBA024703D691181605292E9F02AECB672`、FileVersion `0.3.12.0`。
- 打包：`dist/ADOFAI.Renderist.zip`（sha256 `B613A25EEB6B29BFA0A6E8A0153FDC1316880EB843CC652FCE7B5FC90C95C9F0`），内置 verify 与独立 `-Strict` 均 **11 checks / 0 failures**，严格三文件。`git diff --check` 0。
- 部署：新构建成功后只更新 `Mods\ADOFAI.Renderist\` 自身文件（DLL + Info.json），`AdofaiTweaks` 未触碰。
- **本轮没有目标 Unity 实机验证**：Harmony 安装 / Transpiler 实际替换 / `installed / applied` 日志、原生调用相位、异常 Finalizer、嵌套 `UpdateSize`、custom FPS 同钟语义、UMM 显示版本都还没有证据。**没有提交、没有推送。**

### 8.2 2026-10-17 dsh —— Gate A Correctness 修正

- **根因（最关键）**：真实 DLL 反射确认 `UnityEngine.Time.get_deltaTime` / `get_time` 是 `static float get_X()`（0 参数）；首版把它们当作实例 getter 而直接替换成 `call Helper(object)`，helper 因此会消费栈顶那个**字段值**，并让其后的 `add` / `stfld` 少一个操作数。修正为 `ldarg.0 + call Helper`，并把原 `call` 上的分支标签搬到 `ldarg.0` 上。
- **真实 DLL 端到端核对**（一次性 net48 工具，只读、已删除）：反射打印 4 个签名；对 4 个目标方法运行生产 Transpiler，结果 `helperCalls = 3/1/1/1`、`leftoverGetterCalls = 0`、`ldarg.0` 增量恰为 `+1/站点`、**每个 helper 调用紧邻 `ldarg.0`**、**未抛形态审计异常**。
- **合成 IL / 行为回归**：新增 `SyntheticIlContractTests`（复刻真实累计形态与 custom-FPS 形态；证明 `ldarg.0 + helper` 保持栈平衡、**省略压栈必须被拒**、branch label 搬到压栈上、try/finally 范围不变）与扩充的 `VisualFrameAuthorizationTests`（Completed/Cancelled/Failed 后 restart 可重新注册、Sealed 不占帧序且跨帧不重复消费、Sealed→G 同 index、frame 0 零步、等待态不算失败、真实帧仍严格一次消费）。
- **net48 回归**：**382 passed / 0 failed / 0 skipped**。Release Rebuild：**0 error / 0 compiler warning**（仅 `NU1900`）。打包 `dist/ADOFAI.Renderist.zip`（sha256 `19632AC13D6D75339E1A9B271072849BB27E736120E69BD76B660A758E50A1E0`），内置 verify 与独立 `-Strict` 均 **11 checks / 0 failures**；`git diff --check` 0。
- **部署**：仅更新 `Mods\ADOFAI.Renderist\` 自身文件（DLL `500224` bytes / `CD95DFA3…` + Info.json），`AdofaiTweaks` 未触碰。
- **仍未执行**：目标 Unity 实机验证（Harmony 安装 / Transpiler applied 日志 / 原生调用相位 / 异常 Finalizer / 嵌套 UpdateSize / custom FPS 同钟 / `Play()`→`Arm` 窗口 / UMM 版本显示）。**没有提交、没有推送。**

该轮 Waiting/Sealed/frame 0 用例未执行完整的生产 Camera 消费/完成生命周期，后续漏项与修复证据见 §8.4；其 DLL/ZIP 身份仅为历史构建。

### 8.3 前一轮基线（保留）

- Release Rebuild（no-restore）：0 warning / 0 error；产物 SHA256 与原部署诊断构建相同。
- 既有 net48 回归（固定本地 Gyan 9.0.2 fixture）：338 passed / 0 failed / 0 skipped。首次执行卡在既有非法 EXE 启动用例，终止该次测试进程；第二次在父进程设置并最终恢复 Windows ErrorMode（抑制系统错误对话框）后完整通过，未改测试/生产源码。
- 新建基线审计包 `dist/visual-boundary-audit/ADOFAI.Renderist.zip`：仍为 0.3.11.0，内置 verify 与独立 `-Strict` 均 11 checks / 0 failures，严格三文件。该包不是发布包。
- 本轮没有新实机验证、没有新 Patch/owner 实现、没有升版/部署/提交/推送。第 7 节是后续计划，不是已执行记录。

### 8.4 2026-10-17 GPT Work —— Gate A 生命周期正确性接管（修复时点记录；最新实机证据见 §8.5）

**范围与证据**：保留原未提交工作区，版本保持 `0.3.12.0`，不进入 Gate B、不改 README、不提交/推送。只调整 Camera 原生调用作用域与 authorizer 配对行为；Patch 精确安装/撤销策略、MasterTimeline、scheduler/RGB24/L3-C 协议未改。修复前新增行为测试运行得到 **384/13/0**，直接复现四个指定问题，以及异常未请求停止、重复消费未使 EOF 失败、旧 owner/旧 generation 回执污染新会话。

**修复合同**：

- Waiting→Authorized：Waiting 可以读取 0 或不读 delta 正常结束，不消费、不回执；Authorized 不放宽缺失回执。
- B−1→Sealed→G：每个 hidden 主调用持有自己的完成 key；仅在前一次完成后允许下一次 0 步，保留 G 同 index 发布与普通帧严格单次消费。
- UpdateSize：独立调用不领步、不完成 Camera 回执；嵌套调用只共享主 Update 授权。每次调用各有 `__state.Owner` 和清理 token，嵌套异常即使被外层捕获也使帧失败。
- frame 0→frame 1：不可变快照、首次、nested 都为 0；frame 1 恢复正常步。数值测试在非零输出帧检查正的 FPS/partial 增量。
- EOF→AwaitingDelivery / Cancel→Restart：等待不重放时间；每次 delta 读取重新校验原 key；完成检查 generation/current/failed/完整 key；迟到回调使用自己 token 中的 owner，不能借静态 Active 操作新会话。任何 fail-closed 同时阻止 EOF 并请求统一停止，Finalizer 保留原始异常并幂等清栈。

**自动化实际层级**：新增 19 项行为测试，编译并执行生产 `CameraNativeTimeOwnership` 与 `VisualFrameAuthorizer` 的 Prefix/helper/Postfix/Finalizer、消费和完成逻辑。仅 native Time/Object/日志使用 managed stand-in，Harmony 使用本地真实 DLL 类型；不执行真实 Unity 引擎，也不模拟其 PlayerLoop。原有 IL/源码契约仍保留。完整 net48 固定 Gyan 9.0.2 fixture 回归 **401 passed / 0 failed / 0 skipped**；测试工程有 6 个既有 SyntheticIl `CS0649` 警告。Release Rebuild **0 error / 0 compiler warning**，1 个离线源 `NU1900` 警告。package 内置 verify 与独立 strict verify 均 **11/0**；diff --check clean。

**当前产物身份**：

- DLL `501248` bytes，FileVersion `0.3.12.0`，ProductVersion `0.3.12.0+9f9ff34eee4ff1f9a73a260d7d753219297d0950`；SHA256 **`F97722C6624E028640F87E23971E52BA4561E851AF870AF5E64030053854F699`**。仍为未提交工作区，+hash 不表示已提交本轮修复。
- ZIP `dist/ADOFAI.Renderist.zip`，SHA256 **`082698F3AE98431DAD4DDCE453EE9301BDD99740DE375335900BF5CE5B704BD5`**；严格三文件，无测试或依赖 DLL。
- 回归和构建成功后，仅部署 DLL + Info.json 到 `D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAI.Renderist\`；现有脚本核验构建/部署 SHA256 一致，未写其它 Mod。

**最小目标 Unity 实机步骤（尚未执行）**：

1. 完全退出游戏后重开，UMM 确认 `0.3.12.0`；启动一次导出，在 Player.log 检查 `CameraNativeTimeOwnership installed`、`armed` 以及四条 `transpiler applied`（Update / UpdateSize / CheckUpdateFrameRateScreen / UpdateCustomFrameRateScreen，替换计数 `3/1/1/1`）。同版本旧 DLL 的身份以以上 SHA256 区分。
2. 用已有 count-in 测试谱面，30 FPS Log-only 从 floor 0 跑过 frame 0、B−1、hidden、G、G+1；不得出现 `camera-scope-*`、`camera-delta-consume-*`、`visual-frame-authorization-failed` 或漏帧。若无法到达预期相位，以日志定位，不改成延后消费或 EOF 补跑。该步骤验证 Gate A 逻辑接线，不要求尚未实现的 Tween/Particle 视觉确定性。
3. 同谱面 PNG 导出进入 gameplay 后 Stop，再立即 Start 并完成一次；检查 generation 更新、没有 `consumer-already-registered`/scope 残留，PNG 与 metadata 帧事务正常。若谱面原生启用 custom FPS，同时确认无新的时钟/RT 异常，不为测试改写用户关卡。
4. 原有短 MP4 测试谱面自然导出一次，覆盖 AwaitingDelivery、B−1/hidden/G 和正常 Finalizing；核对 metadata 的交付/核验帧数与最终发布结果。不要仅凭 MP4 文件存在宣称 Gate A 或视觉通过。

**剩余运行时限制**：Mono 真正安装/执行 Patch、原生 Conductor→Camera→EOF 顺序、`Play()`→`Arm` 窗口、真实 Mono 异常 Finalizer 与 custom FPS 都还未实机覆盖；不能用 managed stand-in 代替这些结论。普通 smoke 不等于已完成原生异常注入验证。当前具备 Gate A 目标 Unity 验收部署条件，尚未验收通过。出现异常时保留最新 Player.log 和对应 session metadata，按真实结果定位；不新增未经验证的替代方案。

外部资料只用于 API 合同，游戏/DOTween 当前行为以上述本地 DLL IL 为准：

- [Harmony Finalizer](https://harmony.pardeike.net/v2/articles/patching-finalizer.html)：异常观察与无条件清理；不能以 Postfix 代替 finally。
- [DOTween 官方文档](https://dotween.demigiant.com/documentation.php)：Goto/Pause/单实例 ManualUpdate 与 AutoKill；本地版本特殊行为仍需对应 IL 和实机核对。
- [Unity 6.3 ParticleSystem.Simulate](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/ParticleSystem.Simulate.html)、[Pause](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/ParticleSystem.Pause.html)、[Play](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/ParticleSystem.Play.html)、[Stop](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/ParticleSystem.Stop.html)：不能据此宣称排空/生命周期矩阵已在目标 Unity 通过。
- [GetCurrentPlayerLoop](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/LowLevel.PlayerLoop.GetCurrentPlayerLoop.html)：读取实际循环；默认类型表不能替代当前运行的循环。


### 8.5 2026-10-17 — Gate A 目标 Unity 功能主路径实机验收

**判定：Gate A 功能主路径 RUNTIME PASS；Gate B 未实现；联合视觉确定性仍未通过。** 以本机游戏 `Mods` 下 501248-byte DLL（SHA256 `F97722C6624E028640F87E23971E52BA4561E851AF870AF5E64030053854F699`）为受测对象，核对当日新 `Player.log`、四份 `metadata.json` 与磁盘产物；版本仍为 `0.3.12.0`。

| Generation | Session | 模式 | 运行结果 |
| --- | --- | --- | --- |
| 1 | `editor_20261017_202612` | 30 FPS Log-only | Completed；254 逻辑帧、0 PNG |
| 2 | `editor_20261017_202632` | PNG 取消 | Cancelled；52 张 PNG（0–51 连续），53 次请求/52 次提交 |
| 3 | `editor_20261017_202643` | PNG 重启并完成 | Completed；254 张 PNG（0–253 连续），无 registry/scope residual |
| 4 | `editor_20261017_202720` | MP4 | Completed；254 交付/254 解码/254 packets，Verified、H.264/yuv420p、encoder exit 0、residual=false、实际 `video.mp4` 为 4326676 bytes |

- 四轮均出现 Camera 四个 `transpiler applied`（替换计数 `3/1/1/1`）、`installed`、`armed`、正常 `disposed: failureReason=null`；未出现 `camera-scope-*`、`camera-delta-consume-*`、`visual-frame-authorization-failed`。
- 自然完成的 generation 1、3、4 均覆盖 count-in B=60、Sealed、G=60（`partialFraction=0.1`）、G+1=61；canonical completion 在 frame 241，End Tail 实际 12 帧。正常 EOF/回执顺序表现一致，未见相位误拒绝。
- 尚未覆盖：Mono 异常 Finalizer 主动注入、自定义 FPS 目标谱面、`editor.Play()`→`Arm` 同步窗口的针对性观察、Camera Tween、Shake、Particle，以及视觉帧画质/运动对比。`diagnosticProbe=true` 是按步骤 8 待清理的临时 NVT 探针，不是正式发布验收。**用户随后明确报告当前画面视觉 FAIL；下一步应先调查 Particle 在 AwaitingDelivery 无授权自推进及其它画面异常（§8.6），暂缓常规 Gate B Tween 任务；Particle 正式接入仍须先过目标 Unity API 行为门槛**。

### 8.6 2026-10-17 — 用户视觉 FAIL 与当前粒子无授权推进

用户对已发布的 0.3.12.0 画面明确反馈「画面完全不对，拖尾问题很大」。**§8.5 的成功仅为导出事务与 Camera 授权的功能主路径，不是视觉验收通过，不可作为稳定画面基线。**

本版本当前 `Player.log` 的 MP4 generation 4、逻辑 frame `f=3`、`AwaitingDelivery` 的 `NVT|` 连续采样（行 5287–5289）：Unity frame **16488→16489→16490**；Camera `timer=0.100000` 一直不变；相同 `Tail` id `169332` 的 `time=0.733333→0.766666→0.800000`，`particleCount=367→384→401`，并保持 `play=1,emit=1,pause=0`。**同一逻辑输出帧冻结时原生 Tail 已额外积分 2×(1/30s) 并新增 34 粒子**，此为 0.3.12.0 当前实机的直接证据。Particle owner 尚未实现，不能把此缺陷解释为 MP4 编码帧数问题；其余画面异常仍待和游戏实时基线/同逻辑帧图像比较。

**停止条件及下一步**：暂停常规 Gate B Tween 实施和版本稳定收敛；GPT Work 优先用已有 NVT、游戏 DLL / PlayerLoop 和产物调查视觉错位的独立根因，并确认 Particle Pause/Simulate / StopEmitting 排空 / Play/Revive 行为所需最小 Unity 验证。禁止把静态 API 形态当运行时正确性，禁止无证据直接修改核心 Patch、清空粒子、改变 Loader、重做已通过的帧事务。保持当前版本及未提交工作区，NVT 暂保留供诊断。

### 8.7 2026-10-18 — Planet Tail 定向调查、实施裁决与最小实机验收

**调度说明**：本节保留定向调查事实及 S1–S6 的候选粒子语义测试。用户后续要求先研究已有实现；最新源码研究及执行顺序见 `PROJECT_UNDERSTANDING.md` §2.2.23 与 [研究报告](EXISTING_RENDERERS_RESEARCH_0.3.12.0.md)。本节不构成立即启动新观测设施或 Particle owner 的授权。

**本轮裁决：原生 Particle 无授权积分/发射是确定缺陷；局部 Particle owner 仍是有条件可行的候选，尚无目标 Unity 语义证据可放行正式实施。** 本轮只复核现有 worktree、DLL、日志、metadata 与 PNG；未改代码、未构建/部署、未清理探针。保留版本 0.3.12.0，不提交、不推送、不改 README。

#### 8.7.1 实际证据与覆盖边界

- 基线仍为 `main` / HEAD `9f9ff34eee4ff1f9a73a260d7d753219297d0950`；无 staged，保留全部已有修改。Release 与 Mods DLL 同为 501248 bytes / SHA256 `F97722C6624E028640F87E23971E52BA4561E851AF870AF5E64030053854F699`。Assembly-CSharp SHA256 `0D1524DA…` 与已有 Atlas 相同，复用 §5 的生命周期 IL，不重新扫描。
- 当前 Player.log 为 12388995 bytes、2026-10-17 20:27:51 最后写入；四份 metadata 和磁盘产物仍与 §8.5 一致。gen3 有 254 PNG，gen4 video.mp4 为 4326676 bytes；这些是输出事实，不是视觉正确证据。

| 当前采样 | 已观察到的状态 | 能得出的结论 |
| --- | --- | --- |
| gen4/f3，5287–5289，uf16488–16490 | Camera timer=.100000；Tail169332 t=.733333→.800000/count367→401；Core169330 同步推进/count367→400；Sparks169328 同步推进/count5→5；第二 Tail169406 同步推进/count367→401 | 等待窗口真实积分和发射，且不限于首个 Tail。Sparks 固定数量不等于年龄/组成不变 |
| 同窗口的 Tail age 样本 | remainingLifetime=.222374→.189040→.155707，startLifetime=.94 | 与额外 2/30 s 衰老一致；没有 seed/稳定粒子身份，不能声称逐粒子已配对 |
| gen4/f0，5246→5249→5257 | Prepare 已 t=.233333/count117；EOF t=.266666/count133/total1.6067；commit t=.366666/count183/total1.7067；Camera timer=0 | bootstrap 与 frame0 也未归 Particle 授权管理；EOF 后又多 .1 s/50 粒子，不只是后期跟随误差 |
| gen3/f59→hidden→G Prepare，3348–3366 | 四个活动受测系统时间不变；Tail age 样本也不变 | 当前 PNG 轮次没有复现旧日志的 hidden 入口残差；不是 MP4 hidden 的证据 |
| gen3/G=60→G+1，3366–3384 | G 仅约 .003333 s；G EOF=commit=G+1 Prepare，G+1 EOF 才另进约 .033333 s | 该 PNG 样本 partial 与退出边界无可见额外步，不能照搬旧版本泄漏数值 |
| gen3/f80，3556→3560 | Core play=1/emit=0，time=.189998→.223332，count437→420 | 发射关闭后仍有排空；不能按 isEmitting 筛选模拟对象或用暂停状态推断原生 Stop 意图 |
| gen3 全部 254 对 EOF→commit | native frame 差为 0 | PNG 主路径没有 MP4 异步交付等待；成功不能代替等待不变量 |

Tail169332 的完整采样配置：World、useUnscaledTime=0、simulationSpeed=1、life=.94、loop=1、duration=1.34、prewarm=1、rateOverTime=501.2/s、rateOverDistance=0、trails.enabled=0。每额外原生步约 16–17 粒子与 rate 相符。此处“拖尾”不是已证实启用 Unity trails module；不能只修 TrailModule，更不能把这组完整配置推广给 Core/Sparks。time 循环与 totalTime 的 duration 偏移均已看到，但不能单凭偏移判定实际 prewarm 初始化过程。

**不得遗漏的证据限制**：

1. 日志 **5304** 已 `NVT|ph=budget-exhausted|records=2500|sampling-disabled`。预算跨 session 累计，gen4 仅采到 f0–4；本轮 MP4 的 hidden/G/G+1、canonical completion、末帧及 Finalizing 粒子行为未记录。下一轮只补这些缺口，不能拿 gen3 的同步 PNG 当替代，也不需要重证 f3 漂移。
2. 现探针上限六个系统；详细配置及 age 实际只取前两个（本例 Tail/ inactive Coop）。层级 `hier` 的 active/count 是首次成功扫描缓存，不能当当前粒子数。其余池、coop-old、children/sub-emitter、销毁/新建与 orphan 均没有全量证明。
3. gen3/gen4 frame0 EOF 的 Camera pulseT=2.199999/.466667；Tail frame0 time 在 gen1/3/4 为 .273790/.266666/.266666。gen3/gen4 的 Tail 时间相同不等于粒子 seed/完整状态相同。现有跨 session 产物不满足同初态对照；pulse 残留是否实际改变画面仍待验证，禁止直接重置 Camera 私有字段。
4. f3 的 Camera timer、已记录目标/位置/zoom/shake 没有相应等待漂移；这个近静止窗口不能证明运动、Hit 重定目标和外部 Tween 正确。Gate A 没有接管 Camera parent/zoom/hold/shake/material Tween；仍保留 §4 已知 writer 边界，暂停常规实现。已查看 gen3 f0/f80 PNG，未获得匹配的原生实时参考图，不作全画面根因或视觉 PASS 结论。

#### 8.7.2 必须先完成的目标 Unity 语义矩阵（未执行）

验证设施只创建 **Renderist 自有、可清理的隔离对象**，不对用户实例调用 Pause/Simulate/Clear/Play，不复制带 Awake/Start/单例副作用的整个 PlanetRenderer。先只读记录实际 Planet 粒子配置/child graph/seed；从安全的粒子配置建立隔离对象，使用测试自有相机/目标记录对照图。若无法可靠复制曲线、模块、材质/renderer 或 children，记录差异并停止“等价于游戏粒子”的判定，不能拿默认空白 ParticleSystem 顶替。

每组从新建 inactive 对象、相同显式非零 seed、相同参数与 Transform 路径开始；seed 在第一次 Play 前设置。原生自动分支作为基准，另一分支 Pause 后只按候选增量调用 `Simulate(t,false,false,false)`。初始化/prewarm 状态单独采样，不以 Clear/每步 Play/每步 restart 取得表面一致。先固定 30 FPS；通过后补 60 FPS 与 simulationSpeed=.5/2、真实非 1 pitch（只应用一次原生 speed，不猜乘）。不能通过修改全局时间来掩盖候选 owner 问题。

| 顺序 | 最小操作 | 必须保存/裁决 |
| --- | --- | --- |
| S0：初态与相位 | 读实际 GetCurrentPlayerLoop；在原生 Particle Begin/End、所有相关 Transform/LateUpdate 完成处、候选 slot、实际 capture camera cull/render 前后记录顺序。记录 playOnAwake/prewarm/seed/autoSeed、simulationSpace/Speed/useUnscaledTime、cullingMode/stopAction、emission curves、trails、sub-emitter/child graph | 找到原生 jobs 完成且所有 emitter Transform 已就绪、又早于首个相关相机渲染的真实插槽。仅类型名或 EOF 状态正确不足以通过 |
| S1：Pause→连续 Simulate | 完成一次原生 Play 的初始化采样后 Pause，连续两个 native frame 不 Simulate；再每步 1/30 连续至少 30 步，与原生同初态/同 cadence 分支比较；另测零授权不调用 Simulate、G 的 .1/30、G+1 的 1/30 | time/totalTime、全部测试粒子 seed/remainingLifetime/startLifetime/position/velocity、count、状态及同逻辑帧图像；确认暂停不自动推进、显式调用连续发射、不重启、不重复 prewarm，且部分步不被错误量化。记录浮点/图像误差实测值，不用任意宽容差盖过相位差 |
| S2：停止发射/关闭 emission | 有存活粒子后 Stop(false,StopEmitting)→Pause→授权 Simulate 直至排空；另组只关闭 emission.enabled（模拟 Core 非 chosen），再打开 | 两种原生意图分开；无额外新生粒子、现有粒子衰老到空，开 emission 后按原生语义续发。检查 stopAction 及排空后状态/回调；数量下降不能独自证明未发射，须结合 seed/年龄/出生记录 |
| S3：Clear 与停止清空 | 分别测 playing/owner-paused/draining 的 Clear(false)、Stop(false,StopEmittingAndClear)，之后保持等待，再按各自原生意图给步 | Clear 是否保留后续发射、Stop-clear 是否保持停止；不能把所有零粒子态统一为 Play 或 stopped |
| S4：Play/激活/prewarm | 分别从 playing、游戏 paused、owner-paused、draining、stopped、inactive 测重复 Play/SetActive；prewarm 开/关各测，连续 Play 两次；对照现有 Revive/PlayParticles/Explode 的实际调用顺序 | 尤其检查 owner 的物理 Pause 是否令原生重复 Play 改变 time/seed/预热/发射；第一次原生积分前能否冻结。隔离 API 链通过只证明 API 链，真实 Revive/Explode 仍需一次游戏自然生命周期观测，不能假称已验收 |
| S5：退出/异常/重开 | 从 emitting、draining、游戏 paused、Stop-clear、inactive 分别撤销测试控制；模拟中注入验证设施异常，销毁仅测试自有对象，再重建新 generation | 恢复最后原生意图，特别是 draining 恢复不得因 Play 重开 emission；不复活已销毁/停用对象，不遗留 loop delegate/恢复 token。无法证明安全恢复即该候选失败 |
| S6：条件模块 | 对实际发现的 children/sub-emitter、rateOverDistance、trails、culling/stopAction 非默认配置各补对应组 | 父子每步只积分一次；世界空间轨迹/移动发射保留原生语义。未出现的配置标为未覆盖，不能扩大兼容性声明 |

粒子 randomSeed 只可作匹配线索，不能保证全局唯一；结合出生顺序/寿命/完整状态，重复 seed 或无法配对必须标记。测试设施可以完整复制自己创建的少量粒子作观测；不要把 GetPlaybackState/SetPlaybackState 的 API 存在当作正式恢复正确。若 Play 改变了 paused/draining 的含义或需要每步 Play 才能继续发射，**停止正式 owner 阶段**，把该最小失败序列交 GPT Work，不直接添加替代路径。

**最小实机执行流程**：dsh 先实现上述临时、默认关闭、可独立运行的测试设施，完成 net48 回归/Release/package/strict verify，成功后只部署 Renderist 文件并记新 DLL hash；现 `F977…` 构建没有此设施，不能要求用户在现版 UI 执行不存在的 Pause/Simulate 验收。目标游戏一次运行自动执行 S0–S5，输出逐组 PASS/FAIL/未覆盖及对应初态/阶段数据和图像，失败组停止后续正式接管；自然生命周期缺口再用原测试谱面启动→停止→重启补一次只读观测。无需用户手动修改谱面或粒子。

#### 8.7.3 正式接入前必须收紧的合同及 dsh 边界

1. **授权及首帧**：沿用现有 MasterTimeline/authorizer 的同一 generation/frame/immutable delta；ParticleBatch 仅在正式 owner 全部安装成功且能回执时登记。Waiting 不领取、不积分；frame0/Sealed 的零步要走合法完成路径，但不能用 `Simulate(0)` 触发额外初始化；普通步只一次、G partial 只乘一次、G EOF→G+1 Prepare 不补步。无注册的现 Gate A 不能被记成 Particle PASS。
2. **初始化是独立门槛**：现日志显示首次 Prepare 前 Particle 已运行。正式接管必须覆盖原生 Play/激活至首次 native Begin 的 bootstrap，不允许等首个 Prepare 才 Pause。prewarm/生命周期引发的即时状态变化与时间消费分别记录；固定同初态的起点及允许的初始化动作，不能要求 Camera 的零增量自动修复已发生的粒子历史。未有安全、可复现的初始化路径时停在该门槛。
3. **原生意图**：继续采用 §5 的 exact PlanetRenderer 生命周期函数观察，保留 Core emission 的原生 writer。模型至少区分 emitting、draining、游戏 paused、已清空/停止、inactive/destroyed 与 owner 物理暂停；不能只看 isPlaying/isEmitting/isPaused，也不能恢复初始 emission 开关覆盖后来 Hit 的选择变化。原生重复 Play 在 owner-paused 下的语义兼容性由 S4 决定，非既定通过。
4. **局部相位/集合**：§5 的 Planet 全池/coop/old/children 发现、native Begin 前冻结与精确唯一 loop slot 仍是候选，不沿用 NVT 六实例/缓存层级为正式集合。消费须在 S0 证明的 render 前位置同步完成、逐实例去重；LateUpdate Postfix 和 EOF 补模拟都不能代替相位证明。slot 安装/卸载只编辑自己的节点，不写回陈旧整树。
5. **失败及恢复**：S5 证明 emitting/draining/paused/cleared/inactive 各自退出，才能接 scheduler residual gate；恢复失败保留具体 token、阻止重开。不清用户粒子，不调用未经验证的 Play 恢复排空，不把设置 simulationSpeed=0、强制全部 restart 或全局 Time 改写当 fallback。保留 RGB24 lease/背压、EOF 读回、L3-C Finalizing/核验/发布合同。

**交给 dsh 的当前范围仅为 S0–S6 临时验证设施与必要行为测试**，不做 Camera Tween/Shake RNG、不加正式 Particle owner/全局 Particle API Patch、不清 NVT、不升版。设施必须自标识、独立预算，优先边界采样以免再次在 f4 耗尽；如需观察真实生命周期只读关联已知实例，不向用户系统调用变更 API。原有精确 Harmony 安装/撤销策略保持不变。语义矩阵回传后由 GPT Work 作通过/否决及最小正式 Patch 审查；只有通过的分支才允许 dsh 分阶段实现 §5 与 scheduler 的必要接线。

正式 owner 可进入验收后，再从匹配初态/seed/输入事件建立两次相同逻辑帧的 PNG 基准：一次无等待，一次有可控交付等待，覆盖 f0、B−1、首次 hidden、G、G+1、Hit 前后、canonical completion、EOF/AwaitingDelivery、End Tail 和 Finalizing 边界。每个窗口都要求无授权时 time/totalTime/粒子年龄/出生集合不变（明确的原生生命周期动作单独标记），并在实际捕获相位比较 Camera 目标/矩阵/size/Tween 状态和粒子世界轨迹及源图；PNG↔有损 MP4 不使用字节相等标准。相同帧仍有差异才按粒子、Camera 目标/平滑、外部 Tween、初始化、渲染材质逐项定位，不先把所有错误判给同一个 owner。联合验收至少补 60 FPS、非 1 pitch、Cancel→Restart、场景卸载/异常残余，并重新执行既有事务回归；MP4 Verified 单独列为封装结果。
