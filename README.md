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

- 🔌 **CDP 无侵入直连**：自动读取各目标应用用户数据目录下的 `DevToolsActivePort`（如 `%APPDATA%\Antigravity\`）动态建立调试会话，不修改任何 Antigravity 核心源码或安装包。
- 🎯 **多目标并行守护**：同时支持 **Antigravity**（v2.11，Electron 41）与 **Antigravity IDE**（v2.5.5，VS Code 1.107 内核，Electron 39）等多个已开启调试端口的实例，每个目标独立监控循环、互不影响；未启动的目标后台自动等待接入。
- 🔍 **穿透式 DOM / Shadow DOM 监测**：内置智能 TreeWalker 遍历与 Shadow Root 递归机制，精准定位多层组件中的确认元素（如 `Yes, allow this time`）。
- ⚡ **低资源开销与毫秒响应**：基于原始 CDP `Runtime.evaluate` 注入 MutationObserver 事件驱动监听（`awaitPromise` 挂起等待，兼容 Trusted Types CSP 页面），避免高 CPU 占用的跨进程暴力轮询，DOM 变动即时响应；等待器自动登记与清理，不在目标页面遗留观察器。
- 🧾 **审批追溯**：每次自动确认都会从审批卡片提取**请求正文**（被批准的命令/操作描述），生成结构化审计记录追加到 `logs/approvals.jsonl`（JSONL 格式，可长期追溯、可被其他工具消费），同时主界面"审批历史"列表实时滚动展示最近批准记录（时间、目标、内容预览，悬停查看完整信息，启动时自动回填最近 50 条）。
- ⏱️ **Claude 终端定时指令调度器**：针对第三方大模型套餐（如 Kimi k3 等）的 5 小时滚动速率限制与 7 天期限场景，提供本地 OS 级定时调度能力。智能嗅探并精准锁定运行 Claude 的 PowerShell / Windows Terminal 实例，支持**倒计时**与**定点时刻**双模式；支持启动后一键最小化至系统托盘静默守护，定时到达时自动唤醒终端注入指令并敲回车继续执行。
- 🎨 **现代精简深色 UI 与系统托盘**：采用 WPF 构建，内置状态指示灯呼吸动画、实时统计（确认次数、各目标端口与连接状态）、实时终端日志流、可折叠高级配置以及无冲突的原生 Win32 系统托盘后台托管。

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

### 2b.（可选）同时监控 Antigravity IDE
本工具同样支持 **Antigravity IDE**（`C:\Users\<你的用户名>\AppData\Local\Programs\Antigravity IDE\Antigravity IDE.exe`），规则与 Antigravity 完全一致（识别 `Yes, allow this time` 交互行 → 选中 → 发送 Enter）。两者可同时并行监控。

> ⚠️ **注意**：若 Antigravity IDE 已经在运行（且启动时未带调试参数），直接再次带参启动只会复用现有进程、参数不会生效。请**先完全退出 Antigravity IDE**（托盘图标右键退出），再以调试参数启动：

```powershell
& "C:\Users\<你的用户名>\AppData\Local\Programs\Antigravity IDE\Antigravity IDE.exe" --remote-debugging-port=9333
```

启动后，Antigravity IDE 会在 `%APPDATA%\Antigravity IDE\DevToolsActivePort` 中自动记录当前调试端口。工具端无需关心具体端口号——端口文件会被自动发现；目标未就绪时，对应监控循环会每 5 秒自动重试直至应用就绪，因此**先启动工具、后再打开应用**也可以正常接入。

> 💡 **更便捷的方式**：也可以直接使用工具界面"▸ 高级设置 → 监控目标"中每个应用行尾的 **"调试重启"** 按钮——它会先检测目标是否已在调试模式运行（是则提示无需操作），否则在你确认后自动关闭当前实例并以调试端口重新启动，监控循环随后自动接入。

> ℹ️ **背景**：Antigravity（v2.11）主进程内置 `appendSwitch('remote-debugging-port', '0')` 逻辑，默认自动开启 CDP 调试端口（随机端口，写入 `DevToolsActivePort`），因此对它**无需任何操作**；Antigravity IDE（v2.5.5）没有该逻辑，才需要快捷方式参数或"调试重启"按钮。

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
│ 确认次数: 12   目标: Antigravity:9226✓ · Antigravity IDE:等待中│
│              连接: 已连接   运行: 监控中                     │
│ ▸ 高级设置（交互文字 / 监控目标勾选 / 应用路径）              │
│ 审批历史                                                     │
│ 14:30:13  Antigravity IDE  Run terminal command: npm run ...│
├────────────────────────────────────────────────────────────┤
│ 实时日志                                         [清空日志] │
│ 14:08:12 [INFO]  [Antigravity]  已通过 CDP 连接（端口 9226） │
│ 14:08:15 [INFO]  [Antigravity]  检测到 'Yes, allow this time'│
│ 14:08:15 [INFO]  [Antigravity]  已自动发送 Enter 确认操作    │
└────────────────────────────────────────────────────────────┘
```

1. **开始监控**：点击 **`● 开始监控`** 按钮，工具将并行查找所有已勾选目标应用的调试端口并建立连接；未就绪的目标会在后台每 5 秒自动重试，应用一旦以调试模式启动即自动接入。
2. **自动确认**：当任一目标界面出现匹配指定文字的交互提示时，工具将在毫秒级检测到、自动选中该交互行并按 Enter 触发确认，同时“确认次数”实时累加（全部目标合计）。
3. **选择监控目标**：展开**“▸ 高级设置”**，在**“监控目标”**中勾选需要守护的应用（默认同时勾选 Antigravity 与 Antigravity IDE）；每个目标行尾的**“调试重启”**按钮可将该应用一键以调试模式重启（已在调试模式时会提示无需操作；关闭前会弹窗确认，优雅关闭超时会再询问是否强制结束）。
4. **自定义交互文字**：展开**“▸ 高级设置”**，可在**“交互文字”**中自定义输入想要自动确认的文本（不区分大小写，默认值为 `Yes, allow this time`）。点击“保存配置”即可永久保存到本地配置文件。
5. **审批历史追溯**：界面中部的**“审批历史”**列表实时展示每次自动确认的时间、目标应用与请求正文预览（即被批准的命令/操作内容），悬停可查看完整信息；启动时自动从审计文件回填最近 50 条。完整长期记录见 `logs/approvals.jsonl`（每行一条 JSON，含时间戳、目标、命中文本、请求正文、点击/Enter 是否成功）。
6. **停止监控**：点击 **`■ 停止`** 可随时安全退出监控守护。

---

## ⚙️ 配置文件说明

位于 `src/AntigravityAutomation/appsettings.json`：

```json
{
  "AutomationConfig": {
    "AppExecutablePath": "C:\\Users\\<用户名>\\AppData\\Local\\Programs\\antigravity\\Antigravity.exe",
    "YesAllowButtonText": "Yes, allow this time",
    "Targets": [
      {
        "Name": "Antigravity",
        "ExePath": "C:\\Users\\<用户名>\\AppData\\Local\\Programs\\antigravity\\Antigravity.exe",
        "UserDataDirectoryName": "Antigravity",
        "Enabled": true
      },
      {
        "Name": "Antigravity IDE",
        "ExePath": "C:\\Users\\<用户名>\\AppData\\Local\\Programs\\Antigravity IDE\\Antigravity IDE.exe",
        "UserDataDirectoryName": "Antigravity IDE",
        "Enabled": true
      }
    ]
  },
  "Serilog": {
    "MinimumLevel": "Debug"
  }
}
```

- `AppExecutablePath`：Antigravity IDE 安装路径（供元素探索等场景使用）。
- `YesAllowButtonText`：需要自动确认的交互行目标文本（匹配时忽略大小写，默认 `Yes, allow this time`，支持用户任意自定义）。
- `Targets`：监控目标列表。每个目标对应一个 Antigravity 系 Electron 应用，字段说明：
  - `Name`：显示名（用于日志前缀与界面勾选列表）；
  - `ExePath`：可执行文件路径（参考展示用，监控流程不启动应用）；
  - `UserDataDirectoryName`：该应用在 `%APPDATA%` 下的用户数据目录名——监控服务在此目录中查找 `DevToolsActivePort` 调试端口文件；
  - `Enabled`：是否纳入监控。所有启用目标并行监控、互不影响。
  - 缺失 `Targets` 节点时自动回退为内置默认双目标（上表两者均启用）。
- `Serilog`：日志记录级别与输出配置。

---

## 📁 目录结构

```
AntigravityAutomation/
├── src/
│   └── AntigravityAutomation/
│       ├── Models/                  # 数据契约与实体模型
│       │   ├── AppTargetProfile.cs      # 监控目标档案（名称/路径/用户数据目录/开关）
│       │   ├── AutomationConfig.cs      # 自动化配置（含 Targets 监控目标列表）
│       │   ├── AutomationStatistics.cs  # 统计快照（含多目标状态摘要）
│       │   ├── ApprovalRecord.cs        # 审批追溯记录（时间/目标/请求正文/点击与Enter状态）
│       │   └── LogEntry.cs
│       ├── Services/                # 核心自动化与 CDP 服务
│       │   ├── ElectronAutomationService.cs   # 多目标并行监控（每目标独立 CDP 会话）
│       │   ├── ApprovalAuditService.cs        # 审批审计（JSONL 持久化 + 历史读取）
│       │   └── LoggingService.cs
│       ├── ViewModels/              # MVVM 视图模型
│       │   ├── MainViewModel.cs
│       │   ├── MonitorTargetOption.cs     # 监控目标勾选项
│       │   └── ApprovalHistoryItem.cs     # 审批历史展示项
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