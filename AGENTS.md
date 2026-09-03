# AGENTS.md

本文件是 **ADOFAI-Renderist** 仓库的唯一 Coding Agent 指令文件，主要供 **DeepSeek Harness（DSH）** 使用。

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
- 维护 `PROJECT_UNDERSTANDING.md`

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
- 按任务需要维护 `CHANGELOG.md` / `DEVLOG.md`

### PROJECT\_UNDERSTANDING.md

`PROJECT_UNDERSTANDING.md` 是跨会话项目状态来源。

规则：

- 仅 Web ChatGPT 维护
- DSH 只能读取
- DSH 禁止创建、修改、重写或格式化该文件

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

---

## 4. README 与版本

README 由用户维护。

除非用户明确要求：

- 不创建 README
- 不修改 README
- 不格式化 README

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

工作报告至少包含：

1. 修改内容
2. 修改文件
3. 构建 / 测试结果
4. 未完成验证
5. 风险 / 待确认项
6. 是否修改版本号
7. 是否修改 README
8. 是否修改 PROJECT\_UNDERSTANDING.md

最后一项正常情况下必须为：

```text
PROJECT_UNDERSTANDING.md 未修改。
```

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
