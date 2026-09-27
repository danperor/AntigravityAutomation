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

    public IReadOnlyList<TerminalProcessInfo> FindTerminalProcesses(TargetCliType? preferredTarget = null)
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

                // 嗅探是否运行 Claude 或 Kimi Code：
                TargetCliType? cliType = null;
                var cliName = string.Empty;
                var detail = string.Empty;

                // 1. 优先从窗口标题特征检测
                if (title.Contains("kimi", StringComparison.OrdinalIgnoreCase))
                {
                    cliType = TargetCliType.KimiCode;
                    cliName = "Kimi Code";
                    detail = "窗口标题包含 'kimi'";
                }
                else if (title.Contains("claude", StringComparison.OrdinalIgnoreCase))
                {
                    cliType = TargetCliType.Claude;
                    cliName = "Claude Code";
                    detail = "窗口标题包含 'claude'";
                }
                // 2. 从父子进程链快照嗅探
                else if (parentChildMap.TryGetValue((int)pid, out var children))
                {
                    var kimiChild = children.FirstOrDefault(c =>
                        c.ProcessName.Contains("kimi", StringComparison.OrdinalIgnoreCase));

                    if (kimiChild != null)
                    {
                        cliType = TargetCliType.KimiCode;
                        cliName = "Kimi Code";
                        detail = $"检测到 Kimi 子进程: {kimiChild.ProcessName}.exe (PID:{kimiChild.ProcessId})";
                    }
                    else
                    {
                        var claudeChild = children.FirstOrDefault(c =>
                            c.ProcessName.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                            c.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase));

                        if (claudeChild != null)
                        {
                            cliType = TargetCliType.Claude;
                            cliName = "Claude Code";
                            detail = $"检测到 Claude 子进程: {claudeChild.ProcessName}.exe (PID:{claudeChild.ProcessId})";
                        }
                    }
                }

                result.Add(new TerminalProcessInfo
                {
                    ProcessId = (int)pid,
                    ProcessName = procName,
                    MainWindowTitle = string.IsNullOrWhiteSpace(title) ? procName : title,
                    MainWindowHandle = hwnd,
                    DetectedCliType = cliType,
                    DetectedCliName = cliName,
                    DetailDescription = detail
                });
            }
            catch
            {
                // 忽略没有权限访问的系统进程
            }

            return true;
        }, IntPtr.Zero);

        // 去重
        var uniqueList = result
            .GroupBy(p => p.MainWindowHandle)
            .Select(g => g.First());

        // 依据当前偏好的 CLI 类型进行动态加权排序：目标工具优先排在第一位
        if (preferredTarget == TargetCliType.KimiCode)
        {
            return uniqueList
                .OrderByDescending(p => p.DetectedCliType == TargetCliType.KimiCode)
                .ThenByDescending(p => p.DetectedCliType == TargetCliType.Claude)
                .ThenByDescending(p => p.ProcessId)
                .ToList();
        }
        else if (preferredTarget == TargetCliType.Claude)
        {
            return uniqueList
                .OrderByDescending(p => p.DetectedCliType == TargetCliType.Claude)
                .ThenByDescending(p => p.DetectedCliType == TargetCliType.KimiCode)
                .ThenByDescending(p => p.ProcessId)
                .ToList();
        }

        return uniqueList
            .OrderByDescending(p => p.DetectedCliType.HasValue)
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
