using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AntigravityAutomation.Services;

/// <summary>
/// 终端输入注入服务实现。
/// 通过 Win32 线程输入挂载突破前台激活限制，使用剪贴板+按键模拟对 Claude 终端进行文本及回车注入。
/// </summary>
public sealed class TerminalInjectionService : ITerminalInjectionService
{
    private readonly ILoggingService _loggingService;

    public TerminalInjectionService(ILoggingService loggingService)
    {
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
    }

    public async Task<bool> InjectCommandAsync(IntPtr hwnd, int processId, string commandText, bool pressEnter)
    {
        const string step = "Claude指令注入";

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            _loggingService.LogError($"目标窗口句柄无效或目标窗口已关闭 (HWND: {hwnd}, PID: {processId})", step);
            return false;
        }

        try
        {
            _loggingService.LogInfo($"正在激活目标终端窗口 (PID: {processId}, HWND: {hwnd})...", step);

            // 1. 强力唤醒并置顶目标窗口
            ForceForegroundWindow(hwnd);
            await Task.Delay(250); // 给终端足够时间渲染并获取光标输入焦点

            // 2. 注入指令文本（如果有内容）
            if (!string.IsNullOrEmpty(commandText))
            {
                _loggingService.LogInfo($"正在注入指令文本: \"{commandText}\"...", step);

                // 在 WPF 调度器主线程上操作剪贴板，带重试机制
                var clipboardSet = false;
                for (var i = 0; i < 3; i++)
                {
                    try
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            System.Windows.Clipboard.SetText(commandText);
                        });
                        clipboardSet = true;
                        break;
                    }
                    catch
                    {
                        await Task.Delay(50);
                    }
                }

                if (clipboardSet)
                {
                    // 模拟 Ctrl + V 粘贴
                    SimulateCtrlV();
                    await Task.Delay(150);
                }
                else
                {
                    _loggingService.LogError("写入系统剪贴板失败，无法注入文本", step);
                    return false;
                }
            }

            // 3. 模拟按下回车确认
            if (pressEnter)
            {
                _loggingService.LogInfo("已发送 Enter (回车) 键确认执行", step);
                SimulateEnter();
            }

            _loggingService.LogInfo($"✓ 成功向 Claude 终端 (PID: {processId}) 注入指令！", step);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"向目标终端注入指令时发生错误: {ex.Message}", step, ex);
            return false;
        }
    }

    /// <summary>
    /// 强行突破 Windows 前台锁定机制（通过挂载线程输入队列方式实现可靠置顶）。
    /// </summary>
    private static void ForceForegroundWindow(IntPtr hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);

        var foregroundWindow = GetForegroundWindow();
        var foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(hwnd, out _);

        if (foregroundThreadId != targetThreadId)
        {
            AttachThreadInput(currentThreadId, targetThreadId, true);
            AttachThreadInput(foregroundThreadId, targetThreadId, true);

            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);

            AttachThreadInput(foregroundThreadId, targetThreadId, false);
            AttachThreadInput(currentThreadId, targetThreadId, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
    }

    private static void SimulateCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void SimulateEnter()
    {
        keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    #region Win32 P/Invoke

    private const int SW_RESTORE = 9;
    private const byte VK_RETURN = 0x0D;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    #endregion
}
