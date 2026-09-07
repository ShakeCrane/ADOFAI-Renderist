# ADOFAI Renderist 项目理解

> **维护权说明**
>
> 本文件由 **网页版 GPT 独占维护**，用于记录 ADOFAI Renderist 的当前事实、阶段进度、工程边界和下一步接续点。
>
> **DSH（DeepSeek Harness）只读本文件，不得修改、重写、补充或格式化本文件。**
>
> 如 DSH 在分析或实现过程中发现本文件与仓库事实不一致，应在最终报告中列出差异，由用户交回网页版 GPT 更新本文件。
>
> 本文件是跨会话项目上下文的唯一共享来源之一，但**仓库中的实际代码、AGENTS.md、构建脚本和当前 git 状态始终是执行时事实来源**。若二者冲突，DSH 必须停止假设并报告差异，不得自行改写本文件来“修正上下文”。
>
> 本文件已加入 `.gitignore`，不随仓库提交。

---

## 1. 项目定位

ADOFAI Renderist 是面向 **A Dance of Fire and Ice（ADOFAI）** 的 **Unity Mod Manager（UMM）模组**。

核心目标：

> 在 ADOFAI 编辑器环境中实现可控、可复现的高质量导出，最终发展为非实时逐帧渲染流程。

当前路线强调：

- 优先编辑器内导出
- 优先脱离 replay 依赖
- autoplay 仅作为辅助验证手段
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
| 当前版本 | `0.2.4.0` |
| 当前阶段 | `Phase 2.4 editor export skeleton` |
| ADOFAI | **`v3.3.1`（最新正式版目标基线）** |
| Unity | **待基于 ADOFAI v3.3.1 实机重新确认**；上一已确认值为 `6000.3.10f1` |
| Unity 运行形态 | **待基于 v3.3.1 重新确认**；上一已确认为 Mono（`MonoBleedingEdge/` 存在，`GameAssembly.dll` 不存在） |
| UMM | **`0.33.0`（最新稳定主文件目标基线）** |
| Harmony | **待从 UMM 0.33.0 实际 `0Harmony.dll` 读取 FileVersion 后确定**；上一基线为 `2.3.6.0` |
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
    本文件。由网页版 GPT 维护，DSH 只读。

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

---

## 7. Phase 进度

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

### Phase 2.4 — 修复复验已通过，进入实机验证

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

### 9.4 Mod 自身版本

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

### 9.5 `Assembly-CSharp.dll`

当前 Phase 2.4 仍：

> 不作为编译引用引入。

为了检查 v3.3.1 兼容性，可以读取文件存在性、版本、大小、hash 等基础事实；如需反编译分析必须单独说明，但本轮不得把它加入 csproj 或提交仓库。

---

## 10. 当前明确未实现

以下能力当前不存在：

- 编辑器真实截图后端
- 非实时帧推进
- 可控游戏时间
- `Time.captureFramerate` 驱动
- `Time.timeScale` 驱动
- 编辑器相机锁定
- UI / Canvas 隐藏
- ADOFAI 内部 Editor Hook
- Harmony Patch
- replay 驱动
- TUFReplay / Creplay 适配
- 音频同步
- 视频编码

任何 Agent 报告不得把这些能力描述为已经存在。

---

## 11. 下一步接续点

在 Phase 2.4 游戏内实机验证前，先执行：

> **最新环境基线对齐：ADOFAI v3.3.1 + UMM 0.33.0。**

DSH 当前批准任务：

1. 检查本机游戏是否已是 ADOFAI v3.3.1。
2. 重新确认 Unity / Mono / Managed / DLL 基线。
3. 检查或准备 UMM 0.33.0。
4. 从实际 UMM 0.33.0 读取 `UnityModManager.dll` / `0Harmony.dll` 版本。
5. 更新仓库中所有真实的基线数据位置。
6. 保持 Renderist `0.2.4.0` 和 Phase 2.4 不变。
7. 使用新引用重新构建、打包、验证。
8. 不修改 `PROJECT_UNDERSTANDING.md`。
9. 不提交、不 push，先交网页版 GPT 审查。

对齐通过后：

> **Phase 2.4 游戏内实机验证（在 ADOFAI v3.3.1 + UMM 0.33.0 上）。**

实机验证通过后，网页版 GPT 再判断：

- Phase 2.4 是否正式完成
- 是否提交当前 Phase 2.4 修复 + 基线对齐修改
- 下一阶段编号与目标
- 是否开始真实 Editor Capture Backend
- 是否需要分析 `Assembly-CSharp.dll`
- 是否引入有限 Harmony Hook
- 是否变更 Renderist 版本号

DSH 不得自行进入下一阶段。

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

---

## 13. DSH 读取本文件时的规则

DSH 每次开始任务必须：

1. 读取 `AGENTS.md`
2. 读取本文件
3. 检查 `git status`
4. 检查当前分支
5. 检查相关实际代码
6. 将本文件作为“项目进度与设计意图”
7. 将仓库实际内容作为“当前执行事实”

若发现冲突：

```text
不要修改 PROJECT_UNDERSTANDING.md
不要自行猜测哪个版本正确
不要扩大任务
在报告中列出冲突，等待用户和网页版 GPT 决定
```

DSH 永远不得修改本文件。
