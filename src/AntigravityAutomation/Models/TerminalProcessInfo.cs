using System;

namespace AntigravityAutomation.Models;

/// <summary>
/// 运行中的终端进程信息模型。
/// 用于精准识别并呈现当前系统中运行的 PowerShell / Windows Terminal 窗口。
/// </summary>
public sealed class TerminalProcessInfo
{
    /// <summary>
    /// 进程 ID。
    /// </summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// 进程名称（如 powershell, pwsh, WindowsTerminal）。
    /// </summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// 主窗口标题。
    /// </summary>
    public string MainWindowTitle { get; init; } = string.Empty;

    /// <summary>
    /// 主窗口句柄 (HWND)。
    /// </summary>
    public IntPtr MainWindowHandle { get; init; }

    /// <summary>
    /// 是否在子进程或进程链中检测到 Claude (如 node.exe, claude.exe 或命令行匹配)。
    /// </summary>
    public bool IsClaudeDetected { get; init; }

    /// <summary>
    /// 探测到的详细标签描述（如子进程名称、标题摘要等）。
    /// </summary>
    public string DetailDescription { get; init; } = string.Empty;

    /// <summary>
    /// 下拉框友好展示文本。
    /// </summary>
    public string DisplayText
    {
        get
        {
            var badge = IsClaudeDetected ? " ★[已识别为Claude]" : "";
            var title = string.IsNullOrWhiteSpace(MainWindowTitle) ? "(无标题窗口)" : MainWindowTitle;
            if (title.Length > 45)
            {
                title = title.Substring(0, 42) + "...";
            }
            return $"[PID:{ProcessId}] {ProcessName} - {title}{badge}";
        }
    }
}
