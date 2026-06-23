# CLAUDE.md

此文件为 Claude Code 在处理 **ADOFAI-Renderist** 仓库时提供仓库级别的工作指令。

## 项目定位

ADOFAI-Renderist 是一个面向 **A Dance of Fire and Ice** 的 Unity Mod Manager 模组项目。

本项目的长期目标是为 ADOFAI 构建一个 **非实时渲染导出插件**。该插件最终应能够在受控流程下导出稳定、可复现的帧、图像序列或渲染素材。

项目设计时应考虑未来兼容：

* 当前最新正式版本的 ADOFAI；
* ADOFAI 内置的自动播放 / 自动演示功能；
* 其他模组提供的回放功能；
* 后续可能加入的手动录制、外部编码、视频合成等工作流。

本项目 **仅使用 Unity Mod Manager**。

不要引入或规划支持：

* BepInEx；
* MelonLoader；
* 其他模组加载器；
* 多加载器抽象层。

除非用户明确改变项目方向，否则始终保持项目仅支持 UMM。

## Agent 职责

`CLAUDE.md` 是 Claude Code 使用的主要仓库指令文件。

预期工作流程：

* Web ChatGPT 负责项目规划、审查，以及生成 Claude Code 计划模式提示词。
* Claude Code 在本地仓库中工作。
* Claude Code 可以根据用户明确请求和本文件进行分析、规划、实现、重构、构建和验证。
* Claude Code 不得在未实际检查或修改本地仓库、未运行当前环境可用验证步骤的情况下声称任务完成。

本仓库仅使用 `CLAUDE.md` 作为 Agent 指令文件。

除非用户明确要求，否则不要创建、修改或维护 `AGENTS.md`。

## 默认工作流程

除非用户明确要求实现，否则 Claude Code 必须从 **计划模式（plan mode）** 开始。

计划模式意味着：

* 分析当前目录；
* 检查仓库结构；
* 检查必要项目文件是否存在；
* 如果存在构建配置，则检查构建配置；
* 如果存在引用 DLL 配置，则检查引用配置；
* 必要时检查本地 ADOFAI DLL 是否可用；
* 找出缺失信息；
* 提出具体计划；
* 列出需要创建或修改的文件；
* 列出验证步骤；
* 列出风险和需要人工确认的事项；
* **不要** 创建、修改、删除、移动或格式化任何文件。

处于计划模式时，先输出可审查的计划。等待用户批准实现后再进行修改。

如果用户明确要求实现、修复、创建文件或修改仓库，则按照请求范围执行，但仍应保持修改最小且目标明确。

## README 策略

`README.md` 不属于 Claude Code 默认修改范围。

除非用户明确要求，否则 Claude Code 不得：

* 创建 `README.md`；
* 修改 `README.md`；
* 重写 `README.md`；
* 重新格式化 `README.md`；
* 在计划修改文件列表中包含 README 修改。

Claude Code 可以检查 `README.md` 是否存在以理解仓库，但不得主动提出 README 修改，除非用户明确要求。

## 仓库命名与 C# 命名

仓库名称：

```text
ADOFAI-Renderist
```

默认 C# 根命名空间：

```text
ADOFAI.Renderist
```

默认程序集 / DLL 名称应遵循相同命名，除非用户另有要求：

```text
ADOFAI.Renderist.dll
```

项目许可证：

```text
Apache-2.0
```

除非用户明确要求，否则不要修改许可证。

## 目标游戏版本

目标游戏版本为 **当前最新正式版 ADOFAI**。

不要假设旧版 ADOFAI 模组代码、旧反编译符号、旧回放模组或旧 Unity 行为仍然有效。

涉及以下内容的任何工作，都必须基于用户当前本地 ADOFAI 安装进行验证：

* Unity 版本；
* Mono / IL2CPP 状态；
* `Assembly-CSharp.dll`；
* Unity `Managed` DLL 结构；
* ADOFAI 内部类名；
* 方法签名；
* 字段名称；
* 游戏状态类；
* 自动播放行为；
* 回放兼容性；
* Harmony Patch 目标。

如果无法找到本地 ADOFAI 安装或缺少必要 DLL，停止并报告缺失需求。不要为专有游戏 API 编造 stub。

## 构建环境

首选开发环境为 **Visual Studio**。

第一个实际构建目标应是一个适用于 Unity Mod Manager 模组的最小可构建 C# Class Library。

优先使用简单项目结构和简单构建配置。除非用户明确要求，不要引入复杂构建系统、自定义生成器或大型框架抽象。

可接受的构建相关工具：

* Visual Studio；
* MSBuild；
* 与项目格式兼容时使用 `dotnet build`；
* 用于本地引用准备和打包的 PowerShell 辅助脚本。

优先使用清晰、可维护的 `.csproj` 配置，而不是隐藏的 IDE 专属设置。

## 引用 DLL 策略

不要提交专有游戏程序集或本地二进制引用。

源码仓库只应提交：

* 引用配置模板；
* 检测脚本；
* 占位目录；
* 说明文本文件；
* 用于定位或验证本地引用的构建脚本。

每个开发者必须使用自己的本地 ADOFAI 安装提供所需游戏 DLL。

不要提交：

* `Assembly-CSharp.dll`；
* ADOFAI 游戏 DLL；
* 从游戏复制出的 Unity DLL；
* 从游戏复制出的 `UnityEngine*.dll`；
* Unity Mod Manager 二进制文件，除非明确批准；
* 第三方模组 DLL，除非其许可证允许再分发且用户明确批准；
* 从 ADOFAI DLL 反编译生成的专有游戏源码。

推荐本地引用策略：

1. 在仓库中保留占位目录。
2. 使用 `build/local.props.example` 作为提交的模板。
3. 在本地生成或复制 `build/local.props`。
4. 让 `build/local.props` 指向用户本地 ADOFAI 安装路径。
5. 通过 MSBuild 属性让项目文件引用本地 DLL。
6. 所有实际游戏 DLL 均保持 Git 忽略。

ADOFAI Managed 目录通常为：

```text
<ADOFAI install dir>/A Dance of Fire and Ice_Data/Managed
```

该路径必须在本地验证。不要在提交的项目文件中硬编码某个用户机器路径。

如果辅助脚本将 DLL 复制到本地 `references/` 目录，这些 DLL 必须保持 Git 忽略。

对于可再分发的开源依赖（例如 Harmony），优先使用 NuGet 包，除非 UMM 运行兼容性需要其他方式。

引用 ADOFAI 或 Unity DLL 进行编译时：

* 将其视为仅编译期引用；
* 不要复制到模组发布包；
* 不要包含在源码压缩包中；
* 不要从第三方来源下载。

## 推荐本地引用文件结构

需要时优先使用：

```text
build/
  local.props.example
  local.props          # 仅本地存在，由 Git 忽略

references/
  ADOFAI/
    .gitkeep
  Unity/
    .gitkeep
  UMM/
    .gitkeep

scripts/
  prepare-references.ps1
  clean-references.ps1
```

`build/local.props.example` 只能包含示例属性。除示例用途外，不应包含私人机器路径。

`build/local.props` 应被 Git 忽略。

引用准备脚本可以：

* 请求输入本地 ADOFAI 安装路径；
* 尽可能检测 Steam 安装路径；
* 检查必要 DLL 是否存在；
* 生成 `build/local.props`；
* 可选地复制本地 DLL 到被忽略的引用目录。

引用准备脚本不得：

* 下载 ADOFAI DLL；
* 从第三方下载 Unity 游戏 DLL；
* 提交 DLL；
* 修改无关文件。

## Git 与二进制文件规则

保持仓库以源码为核心。

不要提交本地构建产物或专有二进制文件。

谨慎使用宽泛的 `*.dll` 忽略规则。它可以防止误提交专有 DLL，但发布包可能也包含项目自身编译出的模组 DLL。如果使用宽泛 DLL 忽略规则，必须确保发布流程明确且安全。

## UMM 模组约束

项目必须保持基于 Unity Mod Manager。

创建或修改 UMM 相关代码时，优先采用标准 UMM 模式：

* `Info.json`；
* UMM 入口类；
* 加载方法；
* 启用 / 禁用处理；
* 设置加载 / 保存；
* 可选 UMM GUI；
* UMM 日志；
* Harmony 初始化和取消 Patch。

保持 UMM 入口代码精简。不要将大量功能逻辑直接放入入口类。

优先将职责拆分到 `ADOFAI.Renderist` 下清晰的命名空间和类中。

## Harmony Patch 规则

谨慎使用 Harmony。

创建或修改任何 Harmony Patch 前，确认：

* 目标类型；
* 目标方法；
* 预期方法签名；
* Patch 类型：Prefix、Postfix、Transpiler 或 Finalizer；
* 为什么需要该 Patch；
* 目标方法不存在时的失败行为；
* 是否可以安全禁用或取消 Patch。

尽可能优先使用 Prefix 或 Postfix。

除非有明确验证过的需求且没有更简单 Hook 点，否则避免使用 Transpiler。

不要假设 ADOFAI 内部方法名或签名稳定。实现前必须基于当前本地游戏 DLL 验证。

## 非实时渲染导出方向

项目最终方向是非实时渲染导出。

分析或实现渲染相关功能时，必须明确区分：

* 实时屏幕录制；
* 游戏内截图导出；
* 图像序列导出；
* 受控帧推进；
* 非实时渲染；
* 音频提取或复用；
* 外部视频编码；
* 基于 ffmpeg 的合成；
* 基于回放的渲染；
* 基于自动播放的渲染。

不要假设完整非实时渲染可以立即实现。

任何涉及时间控制、帧捕获、回放、自动播放、相机状态、隐藏 UI、后处理、音频同步或事件播放的实现，在基于当前最新 ADOFAI 验证前都应视为高风险。

## 回放与自动播放兼容性

项目设计应兼容：

* ADOFAI 内置自动播放 / 自动演示行为；
* 其他模组提供的回放功能。

不要假设任何回放模组提供稳定 API。

涉及回放兼容时，应检查本地环境中实际可用的回放模组源码、二进制、日志或运行行为。

不要在没有版本检查和失败处理的情况下硬编码第三方回放模组兼容。

不要破坏游戏原始自动播放流程。

## 文件安全

不要覆盖用户创建的 ADOFAI 谱面文件。

未来任何导出或生成内容都应进入专用输出目录。

处理文件时考虑：

* 路径有效性；
* 非法文件名字符；
* 权限；
* 磁盘空间；
* 导出中断恢复；
* 避免意外覆盖；
* 清晰日志和用户可见错误信息。

## 验证

请求实现时，在报告完成前运行可用验证步骤。

可能的验证步骤：

* Visual Studio 构建；
* 适用时运行 `dotnet build`；
* 脚本语法检查；
* 打包脚本 dry run；
* `git status`；
* `git diff --check`。

如果需要游戏运行测试但 Claude Code 无法执行，必须明确说明限制，并提供用户手动测试步骤。

除非实际运行游戏并观察结果，否则不要声称已验证 ADOFAI 运行时行为。

## 输出要求

报告计划或结果时，应具体且简洁。

计划应包含：

1. 当前仓库状态；
2. 假设；
3. 建议创建或修改的文件；
4. 引用 DLL 需求；
5. 构建方式；
6. 风险；
7. 需要人工确认的事项；
8. 验证步骤。

实现总结应包含：

1. 修改的文件；
2. 修改原因；
3. 已执行验证；
4. 未执行验证；
5. 剩余风险或 TODO。

始终将不确定事项标记为等待确认。