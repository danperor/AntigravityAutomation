using System;
using System.Threading.Tasks;

namespace AntigravityAutomation.Services;

/// <summary>
/// 终端输入注入服务接口。
/// 负责激活目标终端窗口，并注入指令文本及模拟按下回车。
/// </summary>
public interface ITerminalInjectionService
{
    /// <summary>
    /// 将指定指令注入目标终端窗口，并按回车确认。
    /// </summary>
    /// <param name="hwnd">目标终端窗口句柄。</param>
    /// <param name="processId">目标进程 ID。</param>
    /// <param name="commandText">要输入的指令文本。</param>
    /// <param name="pressEnter">是否输入回车键。</param>
    /// <returns>注入是否成功。</returns>
    Task<bool> InjectCommandAsync(IntPtr hwnd, int processId, string commandText, bool pressEnter);
}
