// 文件用途：应用调试重启服务实现 AppRelaunchService。
// 职责：
//   1. FindRunningProcesses：按可执行文件绝对路径精确查找目标应用的全部运行中进程。
//   2. GetLiveDebugPortAsync：读取目标用户数据目录下的 DevToolsActivePort 文件，
//      并 HTTP 探测 /json/version 验证端口真实存活（避免采信上次运行残留的陈旧文件）。
//   3. CloseInstancesAsync：先优雅关闭（CloseMainWindow），超时后可选强制 Kill 整棵进程树。
//   4. LaunchWithDebugPortAsync：以 --remote-debugging-port=<DebugPort> 启动目标应用
//      （不带 --user-data-dir，使用默认用户数据目录），删除可能残留的旧端口文件后
//      轮询等待新端口就绪。
// 设计说明：
//   * 本服务只做进程与端口机制，不含任何 UI；确认对话框由 MainViewModel 负责。
//   * Antigravity IDE（v2.5.5，VS Code 1.107 内核）不会自行开启 CDP 调试端口，
//     必须带 --remote-debugging-port 启动；Antigravity（v2.11）主进程内置
//     appendSwitch('remote-debugging-port','0') 逻辑会默认开启，无需本功能。
//   * VS Code 系应用为单实例架构：若目标已在无调试模式运行，直接带参启动只会
//     复用现有进程、参数不生效，因此必须先退出再重启。
// 容错策略：
//   * MainModule 访问被拒/进程中途退出等竞态：捕获异常并跳过该进程。
//   * 端口文件删除失败：忽略（以 HTTP 存活探测为准，陈旧文件不会导致误判）。
//   * 启动后端口未在 20 秒内就绪：返回 null，由调用方提示用户（监控循环仍会
//     每 5 秒自动重试，应用稍后就绪也可自动接入）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// 应用调试重启服务实现。检测目标应用调试状态并以调试模式重启它。
/// </summary>
public sealed class AppRelaunchService : IAppRelaunchService
{
    // DevToolsActivePort 文件名（位于各目标用户数据目录下，由 Chromium 写入）。
    private const string DevToolsPortFileName = "DevToolsActivePort";

    // 优雅关闭等待：CloseMainWindow 后等待全部进程退出的最长时长。
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(15);

    // 强制结束后等待进程退出的最长时长。
    private static readonly TimeSpan ForceKillExitTimeout = TimeSpan.FromSeconds(5);

    // 启动后等待调试端口就绪的最长时长。
    private static readonly TimeSpan DebugPortReadyTimeout = TimeSpan.FromSeconds(20);

    // 端口存活探测的轮询间隔。
    private static readonly TimeSpan PortProbeInterval = TimeSpan.FromMilliseconds(500);

    // 单次 HTTP 探测超时（/json/version）。
    private static readonly TimeSpan HttpProbeTimeout = TimeSpan.FromSeconds(2);

    // 日志服务，用于全程记录人类可读的重启流程日志。
    private readonly ILoggingService _loggingService;

    /// <summary>
    /// 构造函数，注入日志服务。
    /// </summary>
    /// <param name="loggingService">日志服务，用于记录重启流程日志。</param>
    public AppRelaunchService(ILoggingService loggingService)
    {
        _loggingService = loggingService
            ?? throw new ArgumentNullException(nameof(loggingService));
    }

    /// <inheritdoc />
    public Process[] FindRunningProcesses(AppTargetProfile profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        var result = new List<Process>();
        var processName = Path.GetFileNameWithoutExtension(profile.ExePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return Array.Empty<Process>();
        }

        // 按进程名粗筛，再按主模块完整路径精确匹配（防止误伤同名程序）。
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var modulePath = process.MainModule?.FileName;
                if (string.Equals(modulePath, profile.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                // MainModule 访问被拒或进程已退出：跳过并释放句柄。
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // 忽略。
                }
            }
        }

        return result.ToArray();
    }

    /// <inheritdoc />
    public async Task<int?> GetLiveDebugPortAsync(AppTargetProfile profile, CancellationToken cancellationToken)
    {
        var portFilePath = GetPortFilePath(profile);
        if (!File.Exists(portFilePath))
        {
            return null;
        }

        int port;
        try
        {
            using var reader = new StreamReader(portFilePath);
            var firstLine = await reader.ReadLineAsync(cancellationToken);
            if (!int.TryParse(firstLine?.Trim(), out port) || port <= 0 || port > 65535)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        // HTTP 探测验证端口真实存活（端口文件可能是上次运行残留的陈旧文件）。
        return await ProbeCdpAliveAsync(port, cancellationToken) ? port : null;
    }

    /// <inheritdoc />
    public async Task<bool> CloseInstancesAsync(Process[] processes, bool forceKill, CancellationToken cancellationToken)
    {
        if (processes is null || processes.Length == 0)
        {
            return true;
        }

        const string step = "调试重启";

        // 阶段 1：对每个进程尝试优雅关闭。Electron 主进程持有主窗口，
        // CloseMainWindow 会触发正常退出流程（应用可保存状态）；子进程随主进程退出。
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
                // 无窗口进程或进程已退出，忽略。
            }
        }

        // 阶段 2：等待全部进程退出。
        if (await WaitAllExitedAsync(processes, GracefulExitTimeout, cancellationToken))
        {
            return true;
        }

        if (!forceKill)
        {
            _loggingService.LogWarning(
                $"优雅关闭超时（{(int)GracefulExitTimeout.TotalSeconds} 秒），仍有进程未退出",
                step);
            return false;
        }

        // 阶段 3：强制结束整棵进程树（调用方已获用户确认）。
        _loggingService.LogWarning("正在强制结束残留进程（可能丢失未保存内容）", step);
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程已退出或拒绝访问，忽略。
            }
        }

        return await WaitAllExitedAsync(processes, ForceKillExitTimeout, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int?> LaunchWithDebugPortAsync(AppTargetProfile profile, CancellationToken cancellationToken)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }
        if (!File.Exists(profile.ExePath))
        {
            throw new FileNotFoundException("找不到目标应用可执行文件", profile.ExePath);
        }

        var step = "调试重启";
        var portFilePath = GetPortFilePath(profile);

        // 删除可能残留的旧端口文件，确保后续等待读到的是本次启动写入的新值。
        try
        {
            if (File.Exists(portFilePath))
            {
                File.Delete(portFilePath);
            }
        }
        catch
        {
            // 删除失败不影响流程：端口存活以 HTTP 探测为准。
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = profile.ExePath,
            Arguments = $"--remote-debugging-port={profile.DebugPort}",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(profile.ExePath) ?? string.Empty
        };

        _loggingService.LogInfo(
            $"正在以调试模式启动 {profile.Name}（--remote-debugging-port={profile.DebugPort}）",
            step);

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启动 {profile.Name} 失败：{ex.Message}", ex);
        }
        started?.Dispose();

        // 轮询等待调试端口文件出现且端口存活。
        var deadline = DateTime.UtcNow + DebugPortReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var livePort = await GetLiveDebugPortAsync(profile, cancellationToken);
            if (livePort is not null)
            {
                _loggingService.LogInfo($"{profile.Name} 调试端口已就绪：{livePort}", step);
                return livePort;
            }

            await Task.Delay(PortProbeInterval, cancellationToken);
        }

        _loggingService.LogWarning(
            $"{profile.Name} 已启动，但调试端口在 {(int)DebugPortReadyTimeout.TotalSeconds} 秒内未就绪。" +
            "监控循环仍会每 5 秒自动重试，应用稍后就绪也可自动接入。",
            step);
        return null;
    }

    /// <summary>
    /// 等待给定进程集合全部退出，超时返回 false。
    /// </summary>
    /// <param name="processes">进程集合。</param>
    /// <param name="timeout">最长等待时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示全部退出。</returns>
    private static async Task<bool> WaitAllExitedAsync(
        IReadOnlyList<Process> processes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allExited = true;
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        allExited = false;
                        break;
                    }
                }
                catch
                {
                    // 进程句柄失效视为已退出。
                }
            }

            if (allExited)
            {
                return true;
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// HTTP 探测指定端口的 CDP /json/version 端点是否可达。
    /// </summary>
    /// <param name="port">CDP 端口号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示端口存活。</returns>
    private static async Task<bool> ProbeCdpAliveAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = HttpProbeTimeout };
            using var response = await client.GetAsync(
                $"http://127.0.0.1:{port}/json/version",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 连接被拒、超时等均视为端口未就绪。
            return false;
        }
    }

    /// <summary>
    /// 获取目标应用用户数据目录下 DevToolsActivePort 文件的完整路径。
    /// </summary>
    /// <param name="profile">监控目标档案。</param>
    /// <returns>端口文件完整路径。</returns>
    private static string GetPortFilePath(AppTargetProfile profile)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, profile.UserDataDirectoryName, DevToolsPortFileName);
    }
}
