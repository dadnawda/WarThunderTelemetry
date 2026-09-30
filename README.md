# 战雷遥测悬浮辅助 · WarThunderTelemetry

读取《战争雷霆》游戏本机 `127.0.0.1:8111` 的遥测数据，用**始终置顶、背景透明**的悬浮窗，把飞行参数和导弹发射参数直接叠在游戏画面上。

> **不修改游戏文件、不注入游戏进程、不上传任何数据。**
> 全程只读取游戏自己开放的本地接口，程序本体是自绘的置顶窗口。
> 载具阈值联网查询为可选功能（默认行为见设置页）。

---

## 下载

最新版本见 [Releases](https://github.com/dadnawda/WarThunderTelemetry/releases)：

| 文件 | 说明 |
|---|---|
| `WarThunderTelemetry-Setup-*.exe` | **推荐**。安装包，双击出中文安装向导，自动建快捷方式，带卸载器 |
| `WarThunderTelemetry-win-x64.zip` | 免安装版，解压即用 |

两者功能完全一致。已内置 .NET 10 运行时与 Windows App SDK，**目标电脑无需安装任何东西**。

### 系统要求

- 64 位 Windows 10 版本 1809 (17763) 或更高 / Windows 11

### 安装后注意

1. **游戏必须用「窗口化」或「无边框窗口」模式**。独占全屏下任何叠加层都无法显示（Windows 显示机制决定，非本程序问题）
2. 首次运行若弹出蓝色的「Windows 已保护你的电脑」：点「更多信息」→「仍要运行」（程序未购买代码签名证书，属正常现象）
3. Windows 11 的「智能应用控制」(Smart App Control) 可能拦截未签名程序，处理方法见压缩包内 `使用说明.txt` 第四节

---

## 功能

### 遥测悬浮窗

高度 / 速度 / 垂直速度 / 过载 / 马赫数 / 油量 / 转速等 39 项字段可自由勾选；字号、颜色、背景透明度、紧凑行距、一键配色预设全部可调。

### 武器发射参数悬浮区

本机离线解算发射包线，给出**最小射程 / 不可逃逸区 / 有效射程 / 最大射程**与命中时间、末段速度，并按当前本机状态给出「可发射 / 太远 / 太近」判断；本机过载超限时标红。

内置 **76 种空空导弹实测参数**（PL-12 / PL-15、AIM-120 全系、R-77、R-27ER、霹雳 8/9、天燕 90 等）。

### 悬浮窗操作

整块可拖动，四边四角可拉伸（鼠标移到边缘会高亮），双击锁定后鼠标穿透到游戏，边缘一圈保留解锁逃生口。两个悬浮窗各自独立配置位置、字号、配色。

---

## 推演是怎么算的

不是「标称射程 × 修正系数」的粗略估算，而是**质点飞行模型推演**：

1. 逐帧积分导弹的速度-时间与距离-时间曲线
   - 动力段：推力恒定（数据表海平面基准值），质量从发射质量线性衰减到燃尽空重
   - 阻力：`a = k · (ρ/ρ₀) · v²`，空气密度按 `ρ/ρ₀ = exp(-h/8500)` 随高度衰减
2. **锚定**：用数据表里游戏实测的「最大飞行距离」反推每枚弹的有效阻力系数，
   使模拟总距离精确落在锚点上 —— 曲线形状来自物理，绝对量来自数据
3. 在曲线上解出射程族与命中时间（二分求根）

缺失字段一律留空并走保守估算，界面标注「含估算值」，**不编造数字**。

---

## 数据来源

导弹参数来自社区数据挖掘表《WT导弹与设备性能表 2.44.0.23》
（B站 [@库撒的幽灵](https://space.bilibili.com) [@苍之古叶](https://space.bilibili.com) 整理），
为本仓库 `tools/extract-missiles.py` 从原始 Excel 提取、归一化后嵌入
（`src/WarThunderTelemetry.Core/Weapons/missiles.json`）。

**收录纪律**：核心飞行参数（推力 / 燃烧 / 极速 / 最大距离 / 滞空 / 增速 / 阻力系数）
至少占 3 项才收录；源表空列（如 AIM-7M、PL-2）**直接剔除而非猜测**。

---

## 构建

必须用 Visual Studio 的 MSBuild（`dotnet build` 会失败，WinUI 3 的 PRI 生成任务依赖 VS 目录下的程序集）。

```bat
_build-run.cmd        :: 构建（日志 build-log.txt）
_test-run.cmd         :: 测试（日志 test-log.txt）
_selfcheck-run.cmd    :: 悬浮窗属性自检
_selfcheck-pages.cmd  :: 页面导航与设置页交互自检
_publish-run.cmd      :: Release 自包含发布 -> publish\WarThunderTelemetry\
_installer-run.cmd    :: 用 Inno Setup 打安装包 -> publish\WarThunderTelemetry-Setup-<版本>.exe
```

发版流程：改 `installer\setup.iss` 顶部的 `AppVersion` → 跑 `_publish-run.cmd` → 跑 `_installer-run.cmd`。

**构建前必须关掉正在运行的 EXE**，否则自建 DLL 被锁会报 MSB3027 / MSB3021。

---

## 项目结构

```
src/WarThunderTelemetry.Core/     遥测解析、派生量、发射包线解算、导弹库
src/WarThunderTelemetry.App/      WinUI 3 外壳 + 原生 Win32 分层悬浮窗
src/WarThunderTelemetry.Tests/    xUnit 测试
tools/                            数据采集与图标生成脚本
installer/                        Inno Setup 脚本、中文语言包、随包说明
docs/                             规划与使用文档
```

悬浮窗刻意**不用 WinUI 渲染**：WinUI 3 非打包窗口在浅色主题下 DWM 会铺白底，
四层加固仍消不掉，故改用原生分层窗口 + GDI 自绘。

---

## 免责声明

本项目为个人开发的学习与辅助工具，与 Gaijin Entertainment 无关。
导弹参数来自社区整理的数据挖掘结果，仅用于估算，不代表游戏内精确实现。
请勿用于任何违反游戏用户协议的行为。
