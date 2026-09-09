# AGENTS.md

本文件是 ADOFAI-Renderist 仓库的唯一 Coding Agent 指令文件，主要供 GPT Work 与 DeepSeek Harness（DSH）使用。

## 1. 项目定位

ADOFAI-Renderist 是一个基于 **Unity Mod Manager（UMM）** 的 ADOFAI 模组，目标是实现 **非实时渲染导出**。

当前优先方向：

> 编辑器内导出 → 截图序列 → 可控逐帧 → 非实时渲染

当前阶段暂不依赖 replay。

仅支持：

- UMM
- 最新 ADOFAI 正式版
- 编辑器内导出
- autoplay 辅助验证
- 后续外部编码

禁止引入：

- BepInEx
- MelonLoader
- 其他 Loader
- 多 Loader 抽象层

---

## 2. Agent 分工

### Web ChatGPT

负责：

- 产品规划
- 技术路线
- 阶段设计
- 计划审查
- 定期审查 `PROJECT_UNDERSTANDING.md`

### GPT Work

负责：

- 高难度或高风险任务
- 架构设计和复杂重构
- 复杂 Debug
- Hook 和 ADOFAI 内部行为调查
- 核心渲染流程
- 修正或接管 DSH 无法可靠完成的任务
- 完成重要调查、验证或实现后，主动检查 `PROJECT_UNDERSTANDING.md` 是否需要同步

越复杂、越不确定、越关键，越优先交给 GPT Work。

### DeepSeek Harness

负责：

- 阅读并遵守 `AGENTS.md`
- 阅读 `PROJECT_UNDERSTANDING.md`
- 仓库分析
- 编码
- 构建
- 自动化测试
- Git 检查
- 提交
- 方案明确后优先由 DSH 执行具体实现和验证；DSH 无法可靠完成、验证持续失败或任务风险显著上升时，转交 GPT Work，不重复执行同一任务。

### PROJECT_UNDERSTANDING.md

`PROJECT_UNDERSTANDING.md` 是 Web ChatGPT、GPT Work、DSH 及其他参与项目工作的 Agent 之间的跨会话、跨模型项目状态来源。

规则：

- 所有参与项目工作的 Agent 在开始任务前都应读取
- Web ChatGPT、GPT Work、DSH 及其他执行 Agent 均可按需维护
- 以下情况通常应更新：
1) 新的重要行为或技术事实得到验证
2) 原有结论被证伪或需要修正
3) Hook、内部类、方法、字段或调用关系得到可靠确认
4) 技术路线被确定、调整或排除
5) PoC 得到重要成功或失败结论
6) 当前实现基线发生变化
7) 出现新的限制、风险、阻塞或兼容性事实
8) 原“待验证”事项经构建、测试、日志或实机验证后得到结论
9) 下一位 Agent 若不知道该信息，可能重复调查或做出错误判断
- 以下情况通常无需更新：
1) 单纯机械 coding
2) 格式化或重命名
3) 不影响项目理解的小型实现细节
4) 临时实验和逐条命令
5) 已被现有文档准确覆盖的信息
6) 没有产生新项目知识的常规构建、打包或测试

- 宁可及时记录一个对后续 Agent 有用的可靠结论，也不要因为“变化不够重大”而让关键上下文只存在于单次 Agent 会话中。
- 更新前必须结合实际仓库、diff、构建、测试、日志或其他可靠证据核实。
- 发现旧内容过时或错误时，应直接修正，而不是继续追加冲突信息。

优先级：

- 规则冲突：当前用户要求 > 网页版 GPT 项目指令 > `AGENTS.md` > `PROJECT_UNDERSTANDING.md`
- 事实冲突：实际仓库、diff、构建、测试和运行结果 > `PROJECT_UNDERSTANDING.md`

不得因为 `PROJECT_UNDERSTANDING.md` 中已有记录，就跳过必要的仓库检查或把未经验证的信息直接视为事实。

---

## 3. 默认工作方式

收到任务后：

1. 阅读 `AGENTS.md`
2. 阅读 `PROJECT_UNDERSTANDING.md`
3. 检查 Git 与仓库状态
4. 核对当前基线
5. 按用户提示词执行任务
6. 完成构建 / 测试 / Git 检查
7. 输出工作报告

如果用户要求只分析或计划：

- 不修改文件
- 不创建文件
- 不提交

不要擅自扩大修改范围。

临时验证代码应及时清理：

- 为验证接口、Hook、内部行为或技术可行性而新增的非功能性模块、PoC、探针、临时日志或辅助代码，在验证完成且结论已记录后应及时删除
- 不将已完成使命的验证代码长期保留在正式项目中，除非后续仍有明确用途
- 清理前确认有持续价值的结论已同步到 `PROJECT_UNDERSTANDING.md`
- 时刻保持仓库结构和正式代码干净，避免临时验证资产积累

---

## 4. README 与版本

README 由用户维护。

除非用户明确要求：

- 不创建 README
- 不修改 README
- 不格式化 README

版本规则（严格四位：主版本.次版本.功能版本.修订版本）：

- 文档、提示词等不影响产物：通常不改版本；
- 小 Bug fix / 轻微逻辑调整 / 稳定收敛：可递增第四位（修订版本）；
- 一个有独立回退价值的小闭环：Web GPT 应主动考虑增加第四位；
- 未完成实验不机械递增；
- 前三位变更必须询问用户；
- 新模块 / 结构调整前三位先问；
- 新核心功能 / 渲染流程 / Patch：前三位变更必须询问。

版本号未经用户确认不得擅自修改。

涉及版本变更时同步：

- `Info.json`
- `.csproj`
- 代码版本文本
- 打包结果

---

## 5. DLL 与构建

当前：

```text
TargetFramework net48
```

优先使用现有构建与脚本体系。

禁止提交：

- ADOFAI DLL
- Unity DLL
- UMM DLL
- Harmony DLL
- 第三方 Mod DLL
- 游戏反编译源码

本地引用使用：

```text
references/
build/local.props
```

除非任务明确需要，否则不要引入 `Assembly-CSharp.dll`。

如需引入，必须先确认：

- 当前 ADOFAI 版本
- DLL 来源
- 需要的内部 API
- 防误提交措施

---

## 6. UMM / Harmony

保持 UMM-only。

Harmony Patch 前必须确认：

- 目标类
- 目标方法
- 方法签名
- Patch 类型
- 风险

优先：

- Prefix
- Postfix

避免：

- Transpiler
- 未验证 API
- 旧版本 Hook 直接复用

ADOFAI 已更新时，内部 API 必须重新基于当前游戏 DLL 确认。

遇到依赖游戏运行时状态、编辑器实际表现、当前版本 DLL、未文档化内部行为、本地日志或只能由用户在游戏中直接观察的信息，且现有仓库与公开资料不足以可靠判断时，应先向用户取得最小必要信息或请求一次针对性实机验证，再继续调查；已有仓库、日志、代码或公开资料足够时直接自行解决。

---

## 7. 渲染边界

阶段顺序：

1. 实时录屏：仅验证
2. 编辑器截图序列
3. 可控逐帧
4. 非实时渲染
5. 音频
6. 外部视频编码
7. replay 扩展

重点关注：

- 编辑器状态
- 时间控制
- 帧推进
- UI / 相机
- 特效
- 文件输出
- 中断恢复
- 性能与磁盘

不得污染或修改用户关卡。

未经验证，不得宣称完整非实时渲染已经可用。

---

## 8. replay / autoplay

当前策略：

- autoplay 仅用于辅助验证
- 当前阶段不依赖 replay
- 不设计 replay API
- 不绑定 TUFReplay / Creplay
- 不重写 replay 系统

Official Autoplay：仅辅助/参考验证，不拥有 deterministic time authority。

RenderistAutoPlay：MasterTimeline 驱动的 due-floor helper，复用官方 `scrPlayer.Hit(true)`，不拥有时间，不是 replay。

replay 兼容留到后续阶段。

---

## 9. 发布包

发布包保持：

```text
ADOFAI.Renderist.zip
├── Info.json
├── ADOFAI.Renderist.dll
└── LICENSE
```

不得加入本地或第三方依赖。

`dist/` 保持 Git 忽略。

---

## 10. 验证与报告

实现后尽可能执行：

- build
- 自动化测试
- package
- verify
- `git diff --check`
- `git status`

未经实际游戏测试，不得声称实机验证通过。

当下一步明确需要用户实机测试，且本地已配置有效的游戏 `Mods` 路径时：

- 先构建当前工作区最新版，构建成功后自动部署 ADOFAI Renderist 到游戏 `Mods` 目录
- 仅更新 ADOFAI Renderist 自身文件，不修改其他 Mod
- 构建失败时不得部署旧版本冒充最新版本
- `Mods` 路径未配置或无法确认时，不猜测路径，并在工作报告中说明
- 部署后在工作报告中说明实际部署位置和待用户验证项目

工作报告至少包含：

1. 修改内容
2. 修改文件
3. 构建 / 测试结果
4. 未完成验证
5. 风险 / 待确认项
6. 是否修改版本号
7. 是否修改 README
8. 必须明确说明：
PROJECT_UNDERSTANDING.md 未修改。
或：
PROJECT_UNDERSTANDING.md 已更新：<简述更新内容及依据>

---

## 11. 输出要求

默认中文。

要求：

- 简洁
- 工程化
- 可执行
- 不虚构
- 不扩大范围

允许：

- “待确认”
- “待实机验证”

禁止：

- 虚构 Hook
- 虚构测试结果
- 虚构仓库修改
- 虚构兼容性结论
