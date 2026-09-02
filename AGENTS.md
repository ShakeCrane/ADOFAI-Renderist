# AGENTS.md

本文件是 **ADOFAI-Renderist** 仓库的唯一 Agent 指令文件，主要供 **DeepSeek Harness** 使用。

## 1. 项目定位

ADOFAI-Renderist 是一个面向 **A Dance of Fire and Ice** 的 **Unity Mod Manager（UMM）模组项目**，目标是实现 **非实时渲染导出插件**。

长期方向：

- 截图序列导出
- 非实时逐帧渲染
- autoplay / replay 驱动
- 外部音频与视频编码

仅支持 **UMM**。

禁止：

- BepInEx
- MelonLoader
- 其他 Mod Loader
- 多 Loader 抽象层

---

## 2. 当前基线

```text
ADOFAI v3.2.0
Unity 6000.3.10f1
UMM 0.32.5
Harmony 2.3.6.0
TargetFramework net48
```

项目规则：

- 命名空间：`ADOFAI.Renderist`
- DLL：`ADOFAI.Renderist.dll`
- Mod ID：`ADOFAI.Renderist`
- License：Apache-2.0
- README 默认不修改
- 不使用 `CLAUDE.md`
- 不提交游戏 / Unity / UMM / 第三方 Mod DLL
- `references/` 仅占位
- `build/local.props` 仅本地使用
- `dist/` 忽略
- 不默认引入 `Assembly-CSharp.dll`

发布包仅包含：

```text
ADOFAI.Renderist.zip
├── Info.json
├── ADOFAI.Renderist.dll
└── LICENSE
```

---

## 3. Agent 职责

- Web ChatGPT：负责规划、审查、生成 DeepSeek Harness 提示词
- DeepSeek Harness：负责本地分析、实现、构建、验证
- 不得虚报未执行操作
- 不得创建或维护 `CLAUDE.md`

---

## 4. 默认工作流

除非用户明确要求实现，否则默认先分析并输出计划，不修改文件。

计划至少包含：

1. 需求理解
2. 当前状态
3. 修改范围
4. 技术路线
5. 验证方案
6. 风险 / 待确认项

用户确认后再实现。

---

## 5. README 与版本

README 由用户维护，未经明确要求不得创建、修改或格式化。

版本变更必须同步：

- `Info.json`
- `.csproj`
- 代码版本文本
- 发布包

优先使用：

```text
scripts/set-version.ps1
```

未经用户确认，不得擅自升级版本号。

---

## 6. 构建与引用

开发环境：

- Visual Studio 优先
- 可使用 MSBuild / dotnet
- TargetFramework 保持 `net48`

本地引用通过：

```text
references/
build/local.props
scripts/prepare-references.ps1
scripts/clean-references.ps1
```

管理。

禁止提交：

- `Assembly-CSharp.dll`
- Unity DLL
- UMM / Harmony DLL
- 第三方 Mod DLL
- 游戏反编译源码

如需引入 `Assembly-CSharp.dll`，必须先说明用途、来源、风险和防误提交方案。

---

## 7. UMM / Harmony

UMM 结构围绕：

- `Info.json`
- `ModEntry`
- `Load / OnToggle`
- GUI / 设置 / 日志
- Harmony 初始化与释放

入口类保持精简。

Harmony Patch 前必须确认：

- 目标类型
- 方法与签名
- Patch 类型
- 风险

优先 Prefix / Postfix。

避免：

- Transpiler
- 未验证 API
- 凭空假设 Hook

---

## 8. 渲染导出

阶段：

1. 实时录屏：仅验证
2. 截图序列
3. 非实时逐帧：目标
4. 音频：后期
5. 视频编码：外部工具
6. autoplay / replay 驱动

必须关注：

- 时间控制
- 帧推进
- UI / 相机
- 特效 / 后处理
- 音频同步
- 性能与磁盘
- 中断恢复
- 输出安全

未经验证，不得声称非实时渲染方案已可行。

---

## 9. replay / autoplay

优先兼容：

- ADOFAI autoplay
- 现有 replay Mod

不要重写完整 replay 系统。

涉及 replay / autoplay 时必须验证：

- 状态检测
- 时间控制兼容
- 暂停 / 恢复 / 中断
- 与 UMM 的稳定性

---

## 10. 文件与验证

不得修改或覆盖用户关卡。

导出必须使用独立目录。

实现后应尽可能执行：

- 构建
- 打包
- verify 脚本
- `git status`
- `git diff --check`

运行时功能需提供人工验证步骤。

未实际启动游戏时，只能说：

```text
代码层面验证已完成，游戏内验证需要用户执行。
```

不得虚报实机验证。

---

## 11. 实现报告

完成后报告：

- 修改文件
- 修改目的
- 是否改 README
- 是否保持 UMM-only
- 是否提交 DLL
- 是否同步版本
- 已执行验证
- 未执行验证
- 风险 / 待确认项

---

## 12. 输出风格

默认中文。

要求：

- 简洁
- 工程化
- 可执行
- 先结论后细节
- 明确风险与待确认项