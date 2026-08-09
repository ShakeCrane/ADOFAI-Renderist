---

## 1. 项目定位

ADOFAI-Renderist 是一个面向 **A Dance of Fire and Ice** 的 **Unity Mod Manager（UMM）模组项目**，目标是实现 **非实时渲染导出插件**。

长期方向：

* 截图序列导出
* 非实时逐帧渲染
* 可复现导出流程
* autoplay / replay 驱动导出
* 外部音频与视频编码流程

仅支持 **UMM**。

禁止引入：

* BepInEx
* MelonLoader
* 其他 Mod Loader
* 多 Loader 抽象层

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

* 命名空间：`ADOFAI.Renderist`
* DLL：`ADOFAI.Renderist.dll`
* Mod ID：`ADOFAI.Renderist`
* License：Apache-2.0
* README 默认不修改
* 不使用 `CLAUDE.md`
* 不提交游戏 / Unity / UMM DLL
* `references/` 仅占位
* `build/local.props` 仅本地使用
* `dist/` 忽略
* 不默认引入 `Assembly-CSharp.dll`
* 不默认写 Patch 或渲染逻辑

发布包结构：

```text
ADOFAI.Renderist.zip
├── Info.json
├── ADOFAI.Renderist.dll
└── LICENSE
```

发布包禁止包含本地 DLL、第三方 DLL、引用目录、调试文件或缓存文件。

---

## 3. Agent 职责

* Web ChatGPT：负责规划、审查、生成 Codex 提示词
* Codex：负责本地分析、规划、实现、构建、验证
* 不得虚报未执行的操作
* 不得创建或维护 `CLAUDE.md`

---

## 4. 默认工作流

默认先进入 **计划模式**。

计划模式允许：

* 检查仓库状态
* 检查脚本、引用、版本
* 分析技术路线
* 输出计划、风险、验证步骤

计划模式禁止：

* 创建文件
* 修改文件
* 删除文件
* 格式化文件

计划必须包含：

1. 需求理解
2. 当前状态
3. 修改范围
4. 技术路线
5. 验证方案
6. 风险与待确认项

只有用户明确批准后，Codex 才能进入实现。

---

## 5. README 规则

README 由用户维护。

除非用户明确要求，否则不得：

* 创建 README
* 修改 README
* 重写 README
* 格式化 README

如确实需要修改，必须先标注：

```text
需要用户确认：是否允许修改 README。
```

---

## 6. 版本规则

版本变更必须同步：

* `Info.json`
* `.csproj`
* 代码版本文本
* 打包输出

优先使用：

```text
scripts/set-version.ps1
```

版本变更后必须验证：

* 构建输出
* UMM 显示 / 日志
* 发布包
* verify 脚本

未经用户确认，不得擅自升级版本号。

---

## 7. 构建与脚本

开发环境：

* Visual Studio 优先
* 可使用 MSBuild / dotnet
* TargetFramework 保持 `net48`

修改 TargetFramework 前必须验证 UMM / Harmony / Unity 兼容性。

优先复用现有脚本：

```text
scripts/prepare-references.ps1
scripts/clean-references.ps1
scripts/set-version.ps1
scripts/package-release.ps1
scripts/verify-release-package.ps1
```

Windows PowerShell 可使用：

```text
powershell.exe -ExecutionPolicy Bypass
```

---

## 8. DLL 引用规则

源码仓库不得提交任何专有或本地 DLL。

禁止提交：

* `Assembly-CSharp.dll`
* Unity DLL
* UMM DLL
* Harmony DLL
* 第三方 Mod DLL
* 从游戏反编译出的专有源码

允许提交：

* 引用配置模板
* 检测脚本
* 占位目录
* 说明文件

本地引用通过 `references/`、`build/local.props` 和引用脚本管理。

如未来需要引入 `Assembly-CSharp.dll`，必须先说明原因、来源、风险和防误提交方案。

---

## 9. UMM 与 Harmony 规则

UMM 结构应围绕：

* `Info.json`
* `ModEntry`
* `Load / OnToggle`
* GUI / 设置
* 日志
* Harmony 初始化与释放

入口类保持精简，业务逻辑拆分模块。

Harmony Patch 必须谨慎使用。

创建 Patch 前必须确认：

* 目标类型
* 目标方法
* 方法签名
* Patch 类型
* 风险与降级方案

优先使用 Prefix / Postfix。

避免：

* Transpiler
* 未验证 API
* 硬编码旧版类名 / 方法名

禁止凭空假设 Hook 存在。

---

## 10. 渲染导出边界

非实时渲染导出分阶段推进：

1. 实时录屏：仅用于验证
2. 截图序列：核心方向之一
3. 非实时逐帧：目标方向
4. 音频：后期处理
5. 视频编码：外部工具处理
6. replay / autoplay：作为驱动来源

涉及渲染导出时必须分析：

* 时间控制
* 帧推进
* 截图时机
* UI 隐藏
* 相机状态
* 特效 / 后处理
* 音频同步
* replay / autoplay 稳定性
* 性能与磁盘占用
* 中断恢复
* 输出路径安全

未经验证，不得声称完整非实时渲染可行。

---

## 11. replay / autoplay

优先兼容：

* ADOFAI autoplay
* 现有 replay Mod

不要早期重写完整 replay 系统。

涉及 replay / autoplay 时必须确认：

* 状态检测方式
* 时间控制兼容性
* 暂停 / 恢复 / 中断行为
* UI 隐藏需求
* 与 UMM 共存稳定性

不得硬编码第三方 replay Mod 内部实现，除非已确认版本和失败处理。

---

## 12. 文件安全

不得修改或覆盖用户关卡文件。

导出内容必须进入独立输出目录。

必须处理：

* 路径合法性
* 覆盖风险
* 权限问题
* 磁盘空间
* 中断恢复
* 错误日志

---

## 13. 验证要求

实现后应尽可能执行：

* 构建
* 打包
* verify 脚本
* `git status`
* `git diff --check`

涉及运行时行为时，必须提供人工验证步骤：

* 启动 ADOFAI
* 打开 UMM
* 检查 Renderist 是否加载
* 检查启用 / 禁用
* 检查日志
* 测试相关快捷键或功能

除非实际运行游戏并观察结果，否则不得声称已完成实机验证。

可以说：

```text
代码层面验证已完成，游戏内验证需要用户执行。
```

---

## 14. 实现报告

实现完成后必须报告：

* 修改文件
* 修改目的
* 是否修改 README
* 是否创建 CLAUDE.md
* 是否保持 UMM-only
* 是否提交 DLL
* 是否同步版本
* 执行的验证
* 未执行的验证
* 风险与待确认项
* 建议的实机验证步骤

---

## 15. 信息边界

允许：

* “需要确认”
* “待验证”
* “需要实机验证”

禁止虚构：

* 已确认 Hook 存在
* 已确认 replay 兼容
* 已运行游戏验证
* 已修改仓库
* 已打包成功

除非这些操作确实已执行并有结果支持。

---

## 16. 输出风格

默认中文输出。

要求：

* 简洁
* 工程化
* 可执行
* 先结论后细节
* 明确风险和待确认项

根据用户需求输出：

* 计划
* 实现报告
* 审查意见
* 修复提示词