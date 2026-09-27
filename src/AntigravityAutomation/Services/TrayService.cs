using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace AntigravityAutomation.Services;

/// <summary>
/// 基于原生 Win32 Shell_NotifyIcon 的系统托盘服务实现。
/// 零第三方依赖、零命名空间冲突，支持悬停提示动态更新、气泡通知与深色右键菜单。
/// </summary>
public sealed class TrayService : ITrayService
{
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 1024;
    private const uint TRAY_ICON_ID = 1001;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private readonly IClaudeSchedulerService _schedulerService;
    private Window? _mainWindow;
    private Action? _openClaudeSchedulerAction;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _isIconAdded;
    private ContextMenu? _trayContextMenu;

    public TrayService(IClaudeSchedulerService schedulerService)
    {
        _schedulerService = schedulerService ?? throw new ArgumentNullException(nameof(schedulerService));

        // 订阅调度器每秒倒计时更新，同步托盘 Tooltip
        _schedulerService.CountdownTick += OnSchedulerCountdownTick;
        _schedulerService.ExecutionCompleted += OnSchedulerExecutionCompleted;
    }

    public void Initialize(Window mainWindow, Action openClaudeSchedulerAction)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _openClaudeSchedulerAction = openClaudeSchedulerAction;

        _hwnd = new WindowInteropHelper(_mainWindow).EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        // 提取应用程序默认图标（直接使用 Win32 API，无需 System.Drawing.Common 依赖）
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            _hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);
        }
        if (_hIcon == IntPtr.Zero)
        {
            _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        }

        // 构建深色主题托盘右键菜单
        BuildTrayContextMenu();

        // 注册系统托盘图标
        AddNotifyIcon("Antigravity 自动确认工具 - 就绪");
    }

    public void UpdateTooltip(string tipText)
    {
        if (!_isIconAdded || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyData();
        data.uFlags = NIF_TIP;
        data.szTip = tipText.Length > 120 ? tipText.Substring(0, 117) + "..." : tipText;

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void ShowNotification(string title, string message)
    {
        if (!_isIconAdded || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title.Length > 60 ? title.Substring(0, 57) + "..." : title;
        data.szInfo = message.Length > 250 ? message.Substring(0, 247) + "..." : message;
        data.dwInfoFlags = NIIF_INFO;

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void MinimizeToTray()
    {
        if (_mainWindow == null)
        {
            return;
        }

        _mainWindow.WindowState = WindowState.Minimized;
        _mainWindow.Hide();

        if (_schedulerService.IsRunning)
        {
            var remain = _schedulerService.RemainingTime;
            ShowNotification("已转入后台运行", $"Claude 定时任务正在执行中 (剩余: {remain:hh\\:mm\\:ss})");
        }
    }

    public void RestoreFromTray()
    {
        if (_mainWindow == null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void AddNotifyIcon(string initialTip)
    {
        var data = CreateNotifyData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _hIcon;
        data.szTip = initialTip;

        _isIconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void RemoveNotifyIcon()
    {
        if (!_isIconAdded || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _isIconAdded = false;
    }

    private NOTIFYICONDATA CreateNotifyData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TRAY_ICON_ID,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WM_TRAYICON)
        {
            var eventType = (int)lParam;
            if (eventType == WM_LBUTTONDBLCLK || eventType == WM_LBUTTONUP)
            {
                RestoreFromTray();
                handled = true;
            }
            else if (eventType == WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_trayContextMenu == null)
        {
            return;
        }

        SetForegroundWindow(_hwnd);
        _trayContextMenu.Placement = PlacementMode.MousePoint;
        _trayContextMenu.IsOpen = true;
    }

    private void BuildTrayContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D2D3F")),
            Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E0E0E0")),
            BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3F3F55")),
            BorderThickness = new Thickness(1),
            FontSize = 13
        };

        var itemRestore = new MenuItem { Header = "🖥️ 显示主控制台" };
        itemRestore.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(itemRestore);

        var itemClaude = new MenuItem { Header = "⏱️ 打开 Claude 定时调度器" };
        itemClaude.Click += (_, _) =>
        {
            RestoreFromTray();
            _openClaudeSchedulerAction?.Invoke();
        };
        menu.Items.Add(itemClaude);

        var itemCancel = new MenuItem { Header = "⏹️ 取消当前定时任务" };
        itemCancel.Click += (_, _) =>
        {
            _schedulerService.CancelSchedule();
            UpdateTooltip("Antigravity 自动确认工具 - 就绪");
            ShowNotification("已取消定时", "Claude 终端定时任务已中止。");
        };
        menu.Items.Add(itemCancel);

        menu.Items.Add(new Separator());

        var itemExit = new MenuItem { Header = "❌ 退出程序" };
        itemExit.Click += (_, _) =>
        {
            System.Windows.Application.Current.Shutdown();
        };
        menu.Items.Add(itemExit);

        _trayContextMenu = menu;
    }

    private void OnSchedulerCountdownTick(object? sender, TimeSpan remaining)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var tip = _schedulerService.IsRunning
                ? $"Claude 定时中: 剩余 {remaining:hh\\:mm\\:ss} (目标: PID {_schedulerService.ActiveConfig?.TargetProcessId})"
                : "Antigravity 自动确认工具 - 就绪";
            UpdateTooltip(tip);
        });
    }

    private void OnSchedulerExecutionCompleted(object? sender, (bool Success, string Message) e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var title = e.Success ? "Claude 指令注入成功" : "Claude 指令注入失败";
            ShowNotification(title, e.Message);
            UpdateTooltip("Antigravity 自动确认工具 - 就绪");
        });
    }

    public void Dispose()
    {
        _schedulerService.CountdownTick -= OnSchedulerCountdownTick;
        _schedulerService.ExecutionCompleted -= OnSchedulerExecutionCompleted;
        RemoveNotifyIcon();
        _hwndSource?.RemoveHook(WndProc);
    }

    #region Win32 P/Invoke

    private const int IDI_APPLICATION = 32512;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    #endregion
}
