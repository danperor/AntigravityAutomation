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
    /// 检测到的 AI CLI 工具类型（Claude 或 Kimi Code），未检测到则为 null。
    /// </summary>
    public TargetCliType? DetectedCliType { get; init; }

    /// <summary>
    /// 检测到的 CLI 工具友好名称（如 "Claude Code", "Kimi Code"）。
    /// </summary>
    public string DetectedCliName { get; init; } = string.Empty;

    /// <summary>
    /// 是否在子进程或窗口标题中检测到 Claude。
    /// </summary>
    public bool IsClaudeDetected => DetectedCliType == TargetCliType.Claude;

    /// <summary>
    /// 是否在子进程或窗口标题中检测到 Kimi Code。
    /// </summary>
    public bool IsKimiDetected => DetectedCliType == TargetCliType.KimiCode;

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
            var badge = DetectedCliType switch
            {
                TargetCliType.Claude => " ★[已识别为 Claude]",
                TargetCliType.KimiCode => " ★[已识别为 Kimi Code]",
                _ => ""
            };

            var title = string.IsNullOrWhiteSpace(MainWindowTitle) ? "(无标题窗口)" : MainWindowTitle;
            if (title.Length > 45)
            {
                title = title.Substring(0, 42) + "...";
            }
            return $"[PID:{ProcessId}] {ProcessName} - {title}{badge}";
        }
    }
}
