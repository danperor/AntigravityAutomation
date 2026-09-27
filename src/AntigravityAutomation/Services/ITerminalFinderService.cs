using System;
using System.Collections.Generic;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// 终端进程探测服务接口。
/// 负责枚举并智能识别系统中运行的 PowerShell / Windows Terminal 实例。
/// </summary>
public interface ITerminalFinderService
{
    /// <summary>
    /// 枚举当前系统中所有运行中的终端窗口，并标记是否具备 Claude 特征。
    /// </summary>
    /// <returns>终端进程信息列表，检测到 Claude 的排在最前面。</returns>
    IReadOnlyList<TerminalProcessInfo> FindTerminalProcesses();

    /// <summary>
    /// 激活并闪烁指定窗口，供用户肉眼核验窗口对应关系。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    void FlashWindow(IntPtr hwnd);
}
