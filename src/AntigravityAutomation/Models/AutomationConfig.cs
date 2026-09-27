// 文件用途：自动化流程配置模型 AutomationConfig。
// 描述一次 Antigravity 系应用自动化操作所需的全部可调参数，包括应用路径、监控目标列表、
// 交互消息按钮文字等。该模型由 appsettings.json 反序列化填充，
// 也可由 WPF 界面双向绑定后实时修改。所有公共属性采用 PascalCase 命名。

using System;
using System.Collections.Generic;

namespace AntigravityAutomation.Models;

/// <summary>
/// 自动化流程配置。承载 Antigravity 系 Electron 应用自动化交互所需的参数。
/// </summary>
public sealed class AutomationConfig
{
    /// <summary>
    /// Antigravity IDE 可执行文件绝对路径。
    /// 保留用途：界面元素探索服务启动临时实例时使用；监控流程本身不启动应用，
    /// 仅通过 <see cref="Targets"/> 中各目标的用户数据目录发现 CDP 调试端口。
    /// </summary>
    public string AppExecutablePath { get; set; } =
        @"C:\Users\peng\AppData\Local\Programs\antigravity\Antigravity.exe";

    /// <summary>
    /// 目标交互行文本（匹配时忽略大小写）。默认 "Yes, allow this time"。
    /// 当界面出现包含此文本的交互项时，将自动选中并按 Enter 确认。
    /// </summary>
    public string YesAllowButtonText { get; set; } = "Yes, allow this time";

    /// <summary>
    /// 监控目标列表。每个目标对应一个 Antigravity 系 Electron 应用实例，
    /// 监控服务为每个 <see cref="AppTargetProfile.Enabled"/> 为 true 的目标
    /// 启动独立的 CDP 连接与监控循环，并行守护。
    /// 默认为内置双目标（Antigravity 与 Antigravity IDE，均启用）。
    /// </summary>
    public List<AppTargetProfile> Targets { get; set; } = AppTargetProfile.CreateDefaults();
}
