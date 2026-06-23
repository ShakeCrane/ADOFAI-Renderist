## 1. 项目定位

ADOFAI-Renderist 是一个面向 **A Dance of Fire and Ice** 的 **UMM 模组项目**，目标是实现 **非实时渲染导出插件**。

长期方向：

* 截图序列导出
* 非实时逐帧渲染
* 可复现渲染流程
* replay / autoplay 驱动导出
* 外部音频与视频编码流程

仅支持 **Unity Mod Manager（UMM）**，禁止引入其他 Loader。

---

## 2. 当前基线

环境：

```text
ADOFAI v3.1.2
Unity 6000.3.10f1
UMM 0.32.5
Harmony 2.3.6.0
TargetFramework net48
```

核心规则：

* 命名空间：`ADOFAI.Renderist`
* DLL：`ADOFAI.Renderist.dll`
* Mod ID：`ADOFAI.Renderist`
* License：Apache-2.0
* README 不自动修改
* 不使用 `AGENTS.md`
* 不提交任何游戏 / Unity / UMM DLL
* `references/`、`build/local.props` 仅本地使用
* `dist/` 忽略
* 不默认引入 `Assembly-CSharp.dll`
* 不默认写 Patch 或渲染逻辑

发布包：

```text
ADOFAI.Renderist.zip
├── Info.json
├── ADOFAI.Renderist.dll
└── LICENSE
```

禁止包含任何第三方或本地依赖文件。

---

## 3. Agent 职责

* 本文件为唯一 Agent 指令
* Web ChatGPT 负责规划
* Claude Code 负责实现与验证
* 不得虚报已执行操作

不使用 `AGENTS.md`。

---

## 4. 工作流程

默认进入 **计划模式**：

允许：

* 检查仓库、脚本、引用、版本
* 输出计划、风险、验证步骤

禁止：

* 修改文件
* 创建文件

计划必须包含：

1. 需求理解
2. 当前状态
3. 修改范围
4. 技术路线
5. 验证方案
6. 风险

需用户批准后才能实现。

---

## 5. README 策略

默认不修改 README。

如需修改，必须标注：

```text
需要用户确认：是否允许修改 README。
```

---

## 6. 版本规则

同步以下位置：

* `Info.json`
* `.csproj`
* 代码版本文本
* 发布包

使用：

```text
scripts/set-version.ps1
```

验证：

* 构建输出
* UMM 日志
* 发布包
* verify 脚本

---

## 7. 构建环境

* Visual Studio 优先
* 支持 MSBuild / dotnet

目标框架：

```text
net48
```

修改框架需验证兼容性（UMM / Harmony / Unity）。

---

## 8. DLL 引用规则

不提交任何专有 DLL。

允许：

* 引用模板
* 脚本
* 占位目录

禁止：

* `Assembly-CSharp.dll`
* Unity / UMM / Harmony DLL
* 第三方 Mod DLL

本地引用通过脚本管理：

```text
scripts/prepare-references.ps1
scripts/clean-references.ps1
```

---

## 9. UMM 结构

核心：

* `Info.json`
* `ModEntry`
* `Load / OnToggle`
* GUI / 设置 / 日志

入口类保持精简，逻辑拆分模块。

---

## 10. Harmony 规则

谨慎使用 Patch：

必须确认：

* 目标方法
* 签名
* Patch 类型
* 风险

优先：

* Prefix / Postfix

避免：

* Transpiler
* 未验证 API

禁止凭空假设 Hook。

---

## 11. 渲染导出边界

分阶段推进：

1. 实时录屏（仅验证）
2. 截图序列
3. 非实时渲染（目标）
4. 音频（后期）
5. 视频编码（外部工具）
6. replay / autoplay 驱动

必须考虑：

* 时间控制
* 帧推进
* UI / 相机
* 音频同步
* 性能与磁盘

---

## 12. replay / autoplay

优先兼容：

* ADOFAI autoplay
* 现有 replay Mod

不要重写 replay 系统。

必须验证：

* 状态检测
* 时间控制兼容
* 稳定性

---

## 13. 文件安全

* 不修改用户关卡
* 输出到独立目录

必须处理：

* 路径合法性
* 覆盖风险
* 错误日志

---

## 14. 脚本体系

优先复用现有脚本：

* 引用准备
* 部署
* 清缓存
* 打包
* 验证

关键脚本：

```text
scripts/package-release.ps1
scripts/verify-release-package.ps1
scripts/set-version.ps1
```

PowerShell 可使用：

```text
powershell.exe -ExecutionPolicy Bypass
```

---

## 15. 验证要求

执行：

* 构建
* 打包
* verify 脚本
* git 检查

提供人工验证步骤：

* 启动游戏
* 检查 UMM
* 检查日志

可说：

```text
代码层面验证已完成，游戏内验证需要用户执行。
```

不可虚报运行验证。

---

## 16. 实现报告

必须包含：

* 修改文件
* 修改目的
* 版本同步
* 验证情况
* 风险

---

## 17. 信息边界

允许：

* “需要确认”
* “待验证”

禁止虚构：

* Hook 已存在
* 已运行验证
* 已修改仓库

---

## 18. 输出风格

* 中文
* 简洁
* 工程化
* 可执行

根据需求输出：

* 计划 / 实现 / 审查