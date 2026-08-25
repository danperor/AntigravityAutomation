// 文件用途：自动化流程配置模型 AutomationConfig。
// 描述一次 Antigravity IDE 自动化操作所需的全部可调参数，包括应用路径、目标按钮定位方式、
// 交互消息按钮文字、超时与启动延迟等。该模型由 appsettings.json 反序列化填充，
// 也可由 WPF 界面双向绑定后实时修改。所有公共属性采用 PascalCase 命名。

using System;

namespace AntigravityAutomation.Models;

/// <summary>
/// 自动化流程配置。承载 Antigravity IDE 自动化交互所需的参数。
/// </summary>
public sealed class AutomationConfig
{
    /// <summary>
    /// Antigravity IDE 可执行文件绝对路径。
    /// </summary>
    public string AppExecutablePath { get; set; } =
        @"C:\Users\peng\AppData\Local\Programs\antigravity\Antigravity.exe";

    /// <summary>
    /// 目标交互行文本（匹配时忽略大小写）。默认 "Yes, allow this time"。
    /// 当界面出现包含此文本的交互项时，将自动选中并按 Enter 确认。
    /// </summary>
    public string YesAllowButtonText { get; set; } = "Yes, allow this time";
}