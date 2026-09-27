using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// 终端进程探测服务实现。
/// 使用 Windows API 枚举可见控制台窗口，并通过进程快照嗅探子进程（如 node.exe / claude.exe）以精准甄别 Claude 终端。
/// </summary>
public sealed class TerminalFinderService : ITerminalFinderService
{
    private static readonly HashSet<string> TargetProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell",
        "pwsh",
        "WindowsTerminal",
        "cmd"
    };

    public IReadOnlyList<TerminalProcessInfo> FindTerminalProcesses()
    {
        var result = new List<TerminalProcessInfo>();
        var parentChildMap = BuildProcessParentMap();

        // 枚举系统中所有顶级可见窗口
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid <= 0)
            {
                return true;
            }

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                var procName = proc.ProcessName;

                if (!TargetProcessNames.Contains(procName))
                {
                    return true;
                }

                // 获取窗口标题
                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString().Trim();

                // 排除没有标题或系统保留窗口
                if (string.IsNullOrWhiteSpace(title) && procName != "WindowsTerminal")
                {
                    return true;
                }

                // 嗅探是否运行 Claude：
                // 1. 标题含有 claude (不区分大小写)
                // 2. 子进程中包含 node.exe, claude.exe 等
                var isClaude = false;
                var detail = string.Empty;

                if (title.Contains("claude", StringComparison.OrdinalIgnoreCase))
                {
                    isClaude = true;
                    detail = "窗口标题匹配到 'claude'";
                }
                else if (parentChildMap.TryGetValue((int)pid, out var children))
                {
                    var claudeChild = children.FirstOrDefault(c =>
                        c.ProcessName.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                        c.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase));

                    if (claudeChild != null)
                    {
                        isClaude = true;
                        detail = $"检测到子进程: {claudeChild.ProcessName}.exe (PID:{claudeChild.ProcessId})";
                    }
                }

                result.Add(new TerminalProcessInfo
                {
                    ProcessId = (int)pid,
                    ProcessName = procName,
                    MainWindowTitle = string.IsNullOrWhiteSpace(title) ? procName : title,
                    MainWindowHandle = hwnd,
                    IsClaudeDetected = isClaude,
                    DetailDescription = detail
                });
            }
            catch
            {
                // 忽略没有权限访问的系统进程
            }

            return true;
        }, IntPtr.Zero);

        // 优先将检测到 Claude 的终端排在最前面，其次按 PID 排序
        return result
            .GroupBy(p => p.MainWindowHandle) // 按窗口句柄去重
            .Select(g => g.First())
            .OrderByDescending(p => p.IsClaudeDetected)
            .ThenByDescending(p => p.ProcessId)
            .ToList();
    }

    public void FlashWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return;
        }

        // 短暂置顶激活以供肉眼确认
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);

        // 闪烁窗口边框和任务栏图标 4 次
        var fInfo = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 4,
            dwTimeout = 0
        };
        FlashWindowEx(ref fInfo);
    }

    #region 进程树快照分析

    private record ProcessInfoSnapshot(int ProcessId, int ParentId, string ProcessName);

    /// <summary>
    /// 构建全系统进程的 父PID -> 子进程列表 映射表。
    /// 使用 Toolhelp32 快照，耗时极短 (<2ms)，安全且不需要管理员权限。
    /// </summary>
    private static Dictionary<int, List<ProcessInfoSnapshot>> BuildProcessParentMap()
    {
        var map = new Dictionary<int, List<ProcessInfoSnapshot>>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);

        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return map;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    var pid = (int)entry.th32ProcessID;
                    var parentId = (int)entry.th32ParentProcessID;
                    var name = entry.szExeFile;

                    if (!map.TryGetValue(parentId, out var list))
                    {
                        list = new List<ProcessInfoSnapshot>();
                        map[parentId] = list;
                    }

                    list.Add(new ProcessInfoSnapshot(pid, parentId, name));
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    #endregion

    #region Win32 P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    #endregion
}
