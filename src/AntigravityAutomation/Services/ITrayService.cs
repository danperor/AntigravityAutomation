using System;
using System.Windows;

namespace AntigravityAutomation.Services;

/// <summary>
/// 系统托盘服务接口。
/// </summary>
public interface ITrayService : IDisposable
{
    /// <summary>
    /// 初始化托盘图标（绑定主窗口句柄与事件回调）。
    /// </summary>
    /// <param name="mainWindow">WPF 主窗口实例。</param>
    /// <param name="openClaudeSchedulerAction">打开 Claude 定时器窗口的回调。</param>
    void Initialize(Window mainWindow, Action openClaudeSchedulerAction);

    /// <summary>
    /// 更新托盘悬停提示文字 (Tooltip)。
    /// </summary>
    /// <param name="tipText">提示文本（不超过 120 字符）。</param>
    void UpdateTooltip(string tipText);

    /// <summary>
    /// 弹出托盘系统通知气泡 (Balloon / Toast)。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="message">内容。</param>
    void ShowNotification(string title, string message);

    /// <summary>
    /// 将主窗口最小化并隐藏至托盘。
    /// </summary>
    void MinimizeToTray();

    /// <summary>
    /// 从托盘还原显示主窗口。
    /// </summary>
    void RestoreFromTray();
}
