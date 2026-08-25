# AntigravityAutomation

> ⚡ **Antigravity AI 编程助手自动化权限确认与守护工具**

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](https://microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## 📖 项目由来

在日常使用 **Google Antigravity** 等新一代 Agentic AI 编程工具时，AI 经常会自主规划多步任务并提出终端命令执行、文件创建/修改、代码重构等操作。为了保障安全，IDE 界面会频繁弹出交互权限提示（例如：`Yes, allow this time`）。

在面对长任务自主执行（如全项目重构、长流程自动化调试、端到端测试运行等）场景时：
- **痛点**：开发者必须时刻守在屏幕前等待并手动点击确认，导致本应全自动化的工作流频繁被打断，无法做到真正的“无人值守”与“放手交付”。
- **目标**：**AntigravityAutomation** 由此诞生。它通过 Chrome DevTools Protocol (CDP) 协议直连 Antigravity 的 Electron 主进程，以非侵入方式全自动监控确认提示并触发确认，让开发者真正解放双手，畅享丝滑无阻的 AI 自主编程体验。

---

## ✨ 核心特性

- 🔌 **CDP 无侵入直连**：自动读取 `%APPDATA%\Antigravity\DevToolsActivePort` 端口动态建立调试会话，不修改任何 Antigravity 核心源码或安装包。
- 🔍 **穿透式 DOM / Shadow DOM 监测**：内置智能 TreeWalker 遍历与 Shadow Root 递归机制，精准定位多层组件中的确认元素（如 `Yes, allow this time`）。
- ⚡ **低资源开销与毫秒响应**：基于 Playwright 底层 `WaitForFunction` 事件驱动监听，避免高 CPU 占用的跨进程暴力轮询，DOM 变动即时响应。
- 🔄 **断线自动重连**：当 Antigravity IDE 重启或窗口刷新时，后台监控循环自动探测新端口并重建连接，保障长任务稳定性。
- 🎨 **现代精简深色 UI**：采用 WPF 构建，内置状态指示灯呼吸动画、实时统计（确认次数、端口状态、连接指示）、实时终端日志流与可折叠高级配置。

---

## 🛠️ 技术栈

| 模块 | 技术选型 | 说明 |
| :--- | :--- | :--- |
| **运行时** | .NET 8.0 (Windows 10/11) | 高性能跨平台运行时 |
| **UI 框架** | WPF (Windows Presentation Foundation) | 现代化深色主题客户端界面 |
| **自动化核心** | Microsoft.Playwright (CDP) | 通过 Chromium DevTools Protocol 连接 Electron |
| **响应式架构** | ReactiveUI / CommunityToolkit.Mvvm | MVVM 双向数据绑定与命令响应 |
| **日志组件** | Serilog (Async + File Sink) | 结构化滚动日志与 UI 实时输出 |

---

## 🚀 快速上手

### 1. 环境准备
- **操作系统**：Windows 10 (1809+) 或 Windows 11
- **运行时环境**：[.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（如果直接运行编译好的可执行文件）或 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（如果需要源码编译）
- **Antigravity IDE**：已安装并可正常运行

### 2. 启动 Antigravity 并开启调试端口
确保 Antigravity 在启动时开启了远程调试端口（CDP）。可以通过以下任一方式启动：

**方式 A：通过命令行或快捷方式启动**
```powershell
# 启动时添加远程调试参数
& "C:\Users\<你的用户名>\AppData\Local\Programs\antigravity\Antigravity.exe" --remote-debugging-port=9222
```

**方式 B：修改快捷方式目标**
在 Antigravity 桌面快捷方式右键 -> **属性** -> **目标**，在末尾追加参数：
` --remote-debugging-port=9222`

启动后，Antigravity 会在 `%APPDATA%\Antigravity\DevToolsActivePort` 中自动记录当前调试端口。

---

### 3. 运行本工具

#### 选项 1：源码编译运行
```powershell
# 克隆仓库
git clone https://github.com/danperor/AntigravityAutomation.git
cd AntigravityAutomation

# 还原并运行
dotnet run --project src/AntigravityAutomation/AntigravityAutomation.csproj
```

#### 选项 2：使用 Visual Studio 打开
打开根目录下的 `AntigravityAutomation.sln`，设置 `AntigravityAutomation` 为启动项，按 `F5` 直接调试运行。

---

### 4. 界面操作说明

```
┌────────────────────────────────────────────────────────────┐
│ [● 绿色呼吸灯]  Antigravity 自动确认工具         状态：监控中 │
├────────────────────────────────────────────────────────────┤
│ [ ● 开始监控 ]    [ ■ 停止 ]                                │
│ 确认次数: 12      CDP 端口: 9222    连接: 已连接   运行: 监控中│
│ ▸ 高级设置                                                 │
├────────────────────────────────────────────────────────────┤
│ 实时日志                                         [清空日志] │
│ 14:08:12 [INFO]  [连接CDP]  成功连接到 CDP 端口 9222       │
│ 14:08:15 [INFO]  [监控循环] 检测到 'Yes, allow this time'   │
│ 14:08:15 [INFO]  [自动确认] 已自动发送 Enter 确认操作       │
└────────────────────────────────────────────────────────────┘
```

1. **开始监控**：点击 **`● 开始监控`** 按钮，工具将自动查找已运行的 Antigravity 调试端口并建立连接。
2. **自动确认**：当 Antigravity 中提出操作需要确认时，工具将在毫秒级检测到并自动触发确认，同时界面上的“确认次数”累加。
3. **停止监控**：点击 **`■ 停止`** 可随时安全退出监控守护。
4. **高级设置**：展开“高级设置”可配置 Antigravity 可执行文件路径，并支持一键保存至配置文件。

---

## ⚙️ 配置文件说明

位于 `src/AntigravityAutomation/appsettings.json`：

```json
{
  "AutomationConfig": {
    "AppExecutablePath": "C:\\Users\\<用户名>\\AppData\\Local\\Programs\\antigravity\\Antigravity.exe",
    "YesAllowButtonText": "yes, allow this time",
    "SubmitButtonText": "submit",
    "OperationTimeoutSeconds": 30,
    "StartupDelaySeconds": 5
  },
  "Serilog": {
    "MinimumLevel": "Debug"
  }
}
```

- `YesAllowButtonText`：需要匹配的权限确认关键词（不区分大小写）。
- `OperationTimeoutSeconds`：操作单次等待超时阈值（秒）。
- `StartupDelaySeconds`：启动缓冲延时。

---

## 📁 目录结构

```
AntigravityAutomation/
├── src/
│   └── AntigravityAutomation/
│       ├── Models/                  # 数据契约与实体模型
│       │   ├── AutomationConfig.cs
│       │   ├── AutomationStatistics.cs
│       │   └── LogEntry.cs
│       ├── Services/                # 核心自动化与 CDP 服务
│       │   ├── ElectronAutomationService.cs
│       │   └── LoggingService.cs
│       ├── ViewModels/              # MVVM 视图模型
│       │   └── MainViewModel.cs
│       ├── MainWindow.xaml          # 主窗口界面定义
│       ├── MainWindow.xaml.cs       # 窗口代码后台
│       ├── App.xaml / Program.cs    # 应用程序入口与 DI 注入
│       └── appsettings.json         # 配置文件
├── AntigravityAutomation.sln        # Visual Studio 解决方案
├── .gitignore                       # Git 忽略配置
├── LICENSE                          # 开源许可证 (MIT)
└── README.md                        # 项目说明文档
```

---

## ⚠️ 免责声明 (Disclaimer)

- 本工具仅作为开发辅助与生产力提升工具使用。
- 开启自动确认意味着工具将自动允许 Agent 提出的命令与操作执行。请确保在可信的项目与环境中使用，避免由于 Agent 执行不受信任或具破坏性的指令而产生意外影响。

---

## 📄 开源许可证

本项目基于 [MIT 许可证](LICENSE) 开源。欢迎提交 Issue 与 Pull Request！