// 文件用途：自动化流程配置模型 AutomationConfig。
// 描述一次 Antigravity IDE 自动化操作所需的全部可调参数，包括应用路径、目标按钮定位方式、
// 交互消息按钮文字、超时与启动延迟等。该模型由 appsettings.json 反序列化填充，
// 也可由 WPF 界面双向绑定后实时修改。所有公共属性采用 PascalCase 命名。

using System;

namespace AntigravityAutomation.Models;

/// <summary>
/// 自动化流程配置。承载启动 Antigravity IDE 并完成"找按钮→点击目标→处理 yes 消息→点击 submit"
/// 全流程所需的参数。属性均可在界面配置或通过 appsettings.json 预置。
/// </summary>
public sealed class AutomationConfig
{
    /// <summary>
    /// Antigravity IDE 可执行文件绝对路径。默认指向当前用户本机安装位置。
    /// </summary>
    public string AppExecutablePath { get; set; } =
        @"C:\Users\peng\AppData\Local\Programs\antigravity\Antigravity.exe";

    /// <summary>
    /// 目标按钮的显示文字。匹配时忽略大小写与首尾空白。
    /// 当该值为空时进入"探索模式"：仅枚举界面按钮而不执行点击。
    /// </summary>
    public string? TargetButtonText { get; set; }

    /// <summary>
    /// 目标按钮的 CSS 选择器。优先级高于 TargetButtonText；为空时按文字匹配定位。
    /// </summary>
    public string? TargetButtonSelector { get; set; }

    /// <summary>
    /// "yes, allow this time" 交互消息按钮的显示文字。匹配时忽略大小写。
    /// 用于在点击目标按钮后处理 IDE 弹出的授权/确认交互消息。
    /// </summary>
    public string YesAllowButtonText { get; set; } = "yes, allow this time";

    /// <summary>
    /// submit 提交按钮的显示文字。匹配时忽略大小写。
    /// 用于在处理完 yes 消息后点击最终提交按钮完成操作。
    /// </summary>
    public string SubmitButtonText { get; set; } = "submit";

    /// <summary>
    /// 单步操作超时秒数。应用于等待元素可见、可点击等场景。默认 30 秒。
    /// </summary>
    public int OperationTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 应用启动后等待秒数。用于等待 Electron 应用完成初始化窗口渲染后再开始自动化。默认 5 秒。
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 5;

    /// <summary>
    /// 将 OperationTimeoutSeconds 转换为 TimeSpan，便于 Playwright API 直接使用。
    /// </summary>
    /// <returns>操作超时对应的 TimeSpan。</returns>
    public TimeSpan GetOperationTimeout() => TimeSpan.FromSeconds(OperationTimeoutSeconds);

    /// <summary>
    /// 将 StartupDelaySeconds 转换为 TimeSpan，便于异步等待直接使用。
    /// </summary>
    /// <returns>启动延迟对应的 TimeSpan。</returns>
    public TimeSpan GetStartupDelay() => TimeSpan.FromSeconds(StartupDelaySeconds);
}