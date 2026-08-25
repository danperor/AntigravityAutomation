// 文件用途：Electron 自动化服务实现 ElectronAutomationService。
// 职责：
//   1. 通过 CDP 连接到已运行的 Antigravity IDE（Electron 应用），获取主窗口页面。
//   2. 持续监控主窗口 DOM 中是否出现 "Yes, allow this time" 交互项，
//      一旦检测到立即发送 Enter 键确认，然后继续等待，形成长期守护循环。
//   3. 全程通过注入的 ILoggingService 记录人类可读日志，并通过 StatusChanged 事件
//      向界面推送状态变化，便于界面状态栏实时更新。
//   4. 支持通过 CancellationToken 提前取消监控循环，取消后释放 Playwright 资源。
//   5. StopAsync 幂等关闭 IBrowser 与 IPlaywright。
// 设计说明：
//   * Antigravity IDE 由用户事先启动并开启 CDP 远程调试，端口号写入
//     %APPDATA%\Antigravity\DevToolsActivePort 文件第一行。本服务读取该文件获取端口，
//     不再自行启动 Electron 进程，避免与用户已运行的实例冲突。
//   * Microsoft.Playwright 1.49.0 已移除 Electron API（官方建议用 CDP 直连），
//     故本服务采用 Chromium.ConnectOverCDPAsync 方案。
//   * 监控采用事件驱动式：page.WaitForFunctionAsync 在浏览器端执行 JS 等待条件成立，
//     内部由 RAF（requestAnimationFrame）优化，DOM 变化后即时返回，
//     相比 C# 跨进程定时轮询大幅降低延迟与 CPU 开销。
//   * DOM 搜索使用 page.EvaluateAsync 执行 JS TreeWalker 遍历所有文本节点，
//     并递归进入 shadowRoot，以应对 Antigravity IDE 可能使用 Web 组件 shadow DOM 的情况。
//   * 当 CDP 连接断开（Antigravity 重启等）时，TryReconnectAsync 重新发现端口并重建连接，
//     监控循环切换到新 page 引用继续运行，不中断守护。
//   * 本服务独立实现监控逻辑，不与 ElementExplorerService 共享代码。
// 容错策略：
//   * CDP 端口文件缺失或连接失败：记 ERROR 日志并向上抛出，终止监控。
//   * 监控循环中 WaitForFunctionAsync 抛 PlaywrightException：尝试断线重连，
//     重连失败才向上抛出；重连成功则切换到新 page 继续监控。
//   * 监控循环中其他异常：记 WARN 日志，不中断循环，继续下一轮等待。
//   * 取消令牌触发：正常退出循环并记 INFO 日志。

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AntigravityAutomation.Models;
using Microsoft.Playwright;

namespace AntigravityAutomation.Services;

/// <summary>
/// Electron 自动化服务实现。通过 CDP 连接已运行的 Antigravity IDE，
/// 持续监控 "Yes, allow this time" 交互项并自动按 Enter 确认。
/// </summary>
public sealed class ElectronAutomationService : IElectronAutomationService
{
    // DevToolsActivePort 文件所在目录名（位于 %APPDATA% 下）。
    private const string DevToolsPortDirectoryName = "Antigravity";

    // DevToolsActivePort 文件名。
    private const string DevToolsPortFileName = "DevToolsActivePort";

    // WaitForFunctionAsync 单次等待超时：在浏览器端等待 "allow this time" 出现的最长时长。
    // 超时后记一次"仍在监控中"心跳日志并继续等待，避免长时间无输出造成"假死"观感。
    private static readonly TimeSpan WaitForFunctionTimeout = TimeSpan.FromSeconds(60);

    // 等待异常后的重试间隔：非 Playwright 异常时短暂退避再继续等待，避免异常时紧密循环。
    private static readonly TimeSpan ReconnectRetryDelay = TimeSpan.FromSeconds(2);

    // 检测到交互项并按 Enter 后的冷却等待，避免对同一弹窗重复触发。
    private static readonly TimeSpan ConfirmCooldown = TimeSpan.FromSeconds(3);

    // 日志服务，用于全程记录人类可读的监控流程日志。
    private readonly ILoggingService _loggingService;

    // Playwright 实例，StopAsync 时释放；TryReconnectAsync 重连时也会先释放旧实例。
    private IPlaywright? _playwright;

    // 通过 CDP 连接得到的浏览器实例，StopAsync 时关闭；TryReconnectAsync 重连时替换。
    private IBrowser? _browser;

    // 当前监控的页面引用。重连成功后更新为新连接的第一个页面，
    // 监控循环通过此字段在断线后切换到新 page 继续运行。
    private IPage? _currentPage;

    // 同步锁，保护 StopAsync、RunAutomationAsync 与 TryReconnectAsync 并发时的资源状态。
    private readonly object _resourceLock = new();

    // 标记是否已停止，保证 StopAsync 幂等。
    private bool _stopped;

    // 本次运行已确认 'Yes, allow this time' 的次数。由 MonitorAllowThisTimeLoopAsync 累加，
    // 通过 StatisticsChanged 事件推送给界面绑定。RunAutomationAsync 开始时重置为 0。
    private int _confirmationCount;

    // 当前连接的 Antigravity CDP 调试端口号；未连接时为 0。
    // 由 DiscoverCdpPort 设置，TryReconnectAsync 重连时更新，StopAsync 时清零。
    private int _cdpPort;

    // 是否已通过 CDP 连接到 Antigravity IDE。连接成功置 true，断线/停止置 false。
    private bool _isConnected;

    /// <summary>
    /// 构造函数，注入日志服务。
    /// </summary>
    /// <param name="loggingService">日志服务，用于记录监控流程日志。</param>
    public ElectronAutomationService(ILoggingService loggingService)
    {
        _loggingService = loggingService
            ?? throw new ArgumentNullException(nameof(loggingService));
    }

    /// <summary>
    /// 状态变化通知。参数为人类可读的状态描述，界面可订阅以实时更新状态栏。
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 运行统计变化通知。在确认次数增加、CDP 端口发现、连接建立/断开、停止等关键节点触发，
    /// 携带当前 <see cref="AutomationStatistics"/> 快照，界面据此更新控制面板统计信息。
    /// </summary>
    public event EventHandler<AutomationStatistics>? StatisticsChanged;

    /// <summary>
    /// 按给定配置启动持续监控循环：
    /// 发现 CDP 端口→连接 Antigravity IDE→轮询 DOM 搜索 "Yes, allow this time"→
    /// 检测到则按 Enter 确认→继续轮询，直至取消令牌触发。
    /// </summary>
    /// <param name="config">自动化配置（保留接口契约，本实现仅用于日志参考，不再启动应用）。</param>
    /// <param name="cancellationToken">取消令牌，用于界面"停止"按钮中断监控循环。</param>
    public async Task RunAutomationAsync(AutomationConfig config, CancellationToken cancellationToken)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        const string stepConnect = "连接CDP";
        const string stepMonitor = "监控循环";

        // 重置本次运行统计：确认次数归零，连接状态初始为未连接。
        // 在锁内修改以与 StopAsync 并发安全；锁外触发事件避免锁内回调死锁。
        lock (_resourceLock)
        {
            _confirmationCount = 0;
            _cdpPort = 0;
            _isConnected = false;
        }
        RaiseStatisticsChanged();

        try
        {
            // ===== 步骤 1：发现 CDP 端口 =====
            RaiseStatusChanged("正在发现 Antigravity CDP 调试端口");
            _loggingService.LogInfo(
                "正在从 DevToolsActivePort 文件读取 Antigravity CDP 调试端口...",
                stepConnect);

            var cdpPort = DiscoverCdpPort();
            _cdpPort = cdpPort;
            RaiseStatisticsChanged();
            _loggingService.LogInfo(
                $"已发现 CDP 调试端口：{cdpPort}",
                stepConnect);

            // ===== 步骤 2：通过 CDP 连接 Antigravity IDE =====
            cancellationToken.ThrowIfCancellationRequested();

            var playwright = await Playwright.CreateAsync();
            lock (_resourceLock)
            {
                _playwright = playwright;
            }

            var cdpEndpoint = $"http://127.0.0.1:{cdpPort}";
            _loggingService.LogInfo(
                $"正在通过 CDP 端点 {cdpEndpoint} 连接 Antigravity IDE...",
                stepConnect);

            IBrowser browser;
            try
            {
                browser = await playwright.Chromium.ConnectOverCDPAsync(cdpEndpoint);
            }
            catch (Exception ex)
            {
                _loggingService.LogError(
                    $"通过 CDP 端点 {cdpEndpoint} 连接 Antigravity IDE 失败，" +
                    "请确认 Antigravity IDE 已启动并开启了远程调试。",
                    stepConnect, ex);
                throw;
            }

            lock (_resourceLock)
            {
                _browser = browser;
                _isConnected = true;
            }
            RaiseStatisticsChanged();

            // 获取第一个浏览器上下文与页面。Antigravity IDE 已运行，通常存在至少一个页面。
            if (browser.Contexts.Count == 0)
            {
                throw new InvalidOperationException(
                    "已通过 CDP 连接到 Antigravity IDE，但未发现任何浏览器上下文，" +
                    "无法获取主窗口页面。请确认 Antigravity IDE 主窗口已打开。");
            }

            var context = browser.Contexts[0];
            if (context.Pages.Count == 0)
            {
                throw new InvalidOperationException(
                    "已通过 CDP 连接到 Antigravity IDE，但当前上下文未发现任何页面，" +
                    "无法获取主窗口页面。请确认 Antigravity IDE 主窗口已打开。");
            }

            // 多页面支持说明：当前实现仅监控第一个页面（WaitForFunctionAsync 为 page 级别 API）。
            // 若存在多个页面，记录警告提示其余页面不被监控，便于运维定位"为何某弹窗未被自动确认"。
            if (context.Pages.Count > 1)
            {
                _loggingService.LogWarning(
                    $"当前上下文发现 {context.Pages.Count} 个页面，将选择第一个页面进行监控，" +
                    "其余页面不监控。如需多页面并行监控，需对每个页面分别启动 WaitForFunctionAsync。",
                    stepConnect);
            }

            var page = context.Pages[0];
            _currentPage = page;
            // 获取页面标题用于日志展示。页面可能正在导航/刷新，TitleAsync 可能失败，
            // 此处用 try-catch 包裹，失败时不中断流程，直接进入监控循环。
            string pageTitle;
            try
            {
                pageTitle = await page.TitleAsync();
            }
            catch (PlaywrightException ex)
            {
                pageTitle = "未知（页面可能正在导航/刷新）";
                _loggingService.LogWarning(
                    $"获取页面标题失败（页面可能正在导航），将以占位标题继续。异常信息：{ex.Message}",
                    stepConnect);
            }
            _loggingService.LogInfo(
                $"已通过 CDP 连接到 Antigravity IDE，页面标题：{pageTitle}（共 {context.Pages.Count} 个页面）",
                stepConnect);

            var targetText = string.IsNullOrWhiteSpace(config.YesAllowButtonText)
                ? "Yes, allow this time"
                : config.YesAllowButtonText.Trim();

            // ===== 步骤 3：进入持续监控循环 =====
            cancellationToken.ThrowIfCancellationRequested();
            RaiseStatusChanged($"监控中（匹配文本：{targetText}）");
            _loggingService.LogInfo(
                $"开始持续监控包含 '{targetText}' 的交互项（忽略大小写），检测到将自动按 Enter 确认",
                stepMonitor);

            await MonitorAllowThisTimeLoopAsync(page, targetText, cancellationToken, stepMonitor);
        }
        catch (OperationCanceledException)
        {
            _loggingService.LogInfo("监控已停止", stepMonitor);
            throw;
        }
        catch (PlaywrightException ex)
        {
            _loggingService.LogError("监控因 Playwright 操作失败而中止", stepMonitor, ex);
            throw;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("监控因未预期错误而中止", stepMonitor, ex);
            throw;
        }
        finally
        {
            // 无论监控正常停止、失败或取消，都释放 Playwright 资源。
            await StopAsync();
        }
    }

    /// <summary>
    /// 停止当前正在执行的监控循环并释放资源。幂等。
    /// </summary>
    /// <returns>表示异步停止操作的任务。</returns>
    public async Task StopAsync()
    {
        IPlaywright? playwrightToDispose;
        IBrowser? browserToClose;

        lock (_resourceLock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;

            playwrightToDispose = _playwright;
            browserToClose = _browser;

            _playwright = null;
            _browser = null;
            _currentPage = null;
            _isConnected = false;
            _cdpPort = 0;
        }
        RaiseStatisticsChanged();

        _loggingService.LogInfo("开始释放 Playwright 自动化资源", "释放资源");

        // 关闭 CDP 连接的浏览器实例。
        if (browserToClose is not null)
        {
            try
            {
                await browserToClose.CloseAsync();
            }
            catch
            {
                // 关闭失败忽略，继续释放其余资源。
            }
        }

        // 释放 Playwright 实例。
        if (playwrightToDispose is not null)
        {
            try
            {
                playwrightToDispose.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }

        _loggingService.LogInfo("Playwright 自动化资源已释放完毕", "释放资源");
    }

    /// <summary>
    /// 触发 StatusChanged 事件，向界面推送人类可读的状态描述。
    /// </summary>
    /// <param name="status">状态描述。</param>
    private void RaiseStatusChanged(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>
    /// 触发 StatisticsChanged 事件，向界面推送当前统计快照。
    /// 在确认次数变化、CDP 端口发现、连接建立/断开、停止等关键节点调用。
    /// </summary>
    private void RaiseStatisticsChanged()
    {
        StatisticsChanged?.Invoke(this, new AutomationStatistics(_confirmationCount, _cdpPort, _isConnected));
    }

    /// <summary>
    /// 从 %APPDATA%\Antigravity\DevToolsActivePort 文件第一行读取 Antigravity IDE
    /// 当前使用的 CDP 远程调试端口号。
    /// </summary>
    /// <returns>CDP 调试端口号。</returns>
    /// <exception cref="FileNotFoundException">DevToolsActivePort 文件不存在。</exception>
    /// <exception cref="InvalidOperationException">文件内容无法解析为有效端口号。</exception>
    private int DiscoverCdpPort()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appDataPath))
        {
            throw new InvalidOperationException(
                "无法获取当前用户 %APPDATA% 目录路径，环境变量 APPDATA 未设置。");
        }

        var portFilePath = Path.Combine(appDataPath, DevToolsPortDirectoryName, DevToolsPortFileName);
        if (!File.Exists(portFilePath))
        {
            throw new FileNotFoundException(
                $"未找到 Antigravity CDP 调试端口文件：{portFilePath}。" +
                "请确认 Antigravity IDE 已启动并开启了远程调试。",
                portFilePath);
        }

        // 读取第一行端口号。DevToolsActivePort 文件首行即 Chrome/Chromium 选定的调试端口。
        string firstLine;
        try
        {
            using var reader = new StreamReader(portFilePath);
            firstLine = reader.ReadLine() ?? string.Empty;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"读取 Antigravity CDP 调试端口文件失败：{portFilePath}", ex);
        }

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            throw new InvalidOperationException(
                $"Antigravity CDP 调试端口文件内容为空：{portFilePath}");
        }

        if (!int.TryParse(firstLine.Trim(), out var port) || port <= 0 || port > 65535)
        {
            throw new InvalidOperationException(
                $"Antigravity CDP 调试端口文件首行内容无法解析为有效端口号：" +
                $"\"{firstLine}\"（文件路径：{portFilePath}）");
        }

        return port;
    }

    /// <summary>
    /// 持续监控循环：用 page.WaitForFunctionAsync 在浏览器端事件驱动式等待 "allow this time" 文本出现，
    /// 检测到则点击交互行并按 Enter 确认。相比 C# 跨进程定时轮询，WaitForFunctionAsync 内部由 RAF 优化，
    /// DOM 变化后即时返回，延迟与 CPU 开销更低。
    /// 循环内异常分类处理：
    ///   * TimeoutException：单次等待超时，记心跳日志后继续等待，不中断循环。
    ///   * PlaywrightException：可能 CDP 连接断开，调用 TryReconnectAsync 重连，成功则切换到新 page 继续；
    ///     重连失败才向上抛出。
    ///   * 其他异常：记 WARN 后短暂退避继续等待，不中断循环。
    ///   * 取消令牌触发：正常退出循环并记 INFO 日志（含本次确认次数统计）。
    /// </summary>
    /// <param name="page">Antigravity IDE 主窗口页面；重连成功后会被更新为新 page 引用。</param>
    /// <param name="targetText">待匹配的交互项文本（不区分大小写）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="step">日志步骤名。</param>
    private async Task MonitorAllowThisTimeLoopAsync(
        IPage page,
        string targetText,
        CancellationToken cancellationToken,
        string step)
    {
        var idleLogCounter = 0; // 空闲计数器，每 WaitForFunctionTimeout 记一次"仍在监控中"心跳。

        while (!cancellationToken.IsCancellationRequested)
        {
            bool detected = false;
            try
            {
                // WaitForFunctionAsync 在浏览器端等待 targetText 文本出现。
                // 最多等 WaitForFunctionTimeout，未出现则抛 TimeoutException。
                // JS 用 TreeWalker 遍历 body 下所有文本节点进行大小写不敏感匹配。
                await page.WaitForFunctionAsync(
                    "(target) => {" +
                        "var lower = (target || '').toLowerCase();" +
                        "var w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null);" +
                        "while (w.nextNode()) {" +
                            "var t = w.currentNode.textContent;" +
                            "if (t && t.toLowerCase().includes(lower)) return true;" +
                        "}" +
                        "return false;" +
                    "}",
                    targetText,
                    new PageWaitForFunctionOptions
                    {
                        Timeout = (float)WaitForFunctionTimeout.TotalMilliseconds
                    });
                detected = true;
            }
            catch (TimeoutException)
            {
                // 等待超时未出现，记一次"仍在监控中"心跳日志，继续等待。
                idleLogCounter++;
                _loggingService.LogInfo(
                    $"仍在监控中 - 已等待 {idleLogCounter * (int)WaitForFunctionTimeout.TotalSeconds} 秒，" +
                    $"尚未检测到包含 '{targetText}' 的交互项",
                    step);
                RaiseStatusChanged($"监控中 - 等待 '{targetText}' 出现");
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PlaywrightException ex)
            {
                // 可能是 CDP 连接断开（Antigravity 重启、崩溃等），尝试断线重连。
                _loggingService.LogWarning(
                    $"等待 '{targetText}' 时 Playwright 异常，可能 CDP 连接断开。异常信息：{ex.Message}",
                    step);
                var reconnected = await TryReconnectAsync(cancellationToken, step);
                if (!reconnected)
                {
                    // 重连失败，向上抛出由 RunAutomationAsync 统一处理。
                    throw;
                }
                // 重连成功，切换到新 page 引用继续监控。
                page = _currentPage!;
                continue;
            }
            catch (Exception ex)
            {
                // 其他非预期异常不中断监控循环，记 WARN 后短暂退避继续等待。
                _loggingService.LogWarning(
                    $"等待 '{targetText}' 时发生非预期异常，将退避后继续等待。异常信息：{ex.Message}",
                    step);
                try
                {
                    await Task.Delay(ReconnectRetryDelay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                continue;
            }

            if (detected)
            {
                _confirmationCount++;
                idleLogCounter = 0; // 检测到交互项，重置空闲计数器。
                _loggingService.LogInfo(
                    $"✓ [第{_confirmationCount}次确认] 检测到包含 '{targetText}' 的交互行出现",
                    step);
                RaiseStatisticsChanged();

                try
                {
                    // 调用 FindAndClickAllowThisTimeAsync 点击交互行并获取详细 DOM 诊断信息
                    var (found, clicked, diagInfo) = await FindAndClickAllowThisTimeAsync(page, targetText);

                    if (!string.IsNullOrWhiteSpace(diagInfo))
                    {
                        _loggingService.LogInfo(diagInfo, step);
                    }

                    if (clicked)
                    {
                        _loggingService.LogInfo("  已点击选中交互行", step);
                    }
                    else
                    {
                        _loggingService.LogInfo(
                            "  未能点击交互行容器，将直接按 Enter 尝试确认",
                            step);
                    }

                    await page.Keyboard.PressAsync("Enter");
                    _loggingService.LogInfo("  已发送 Enter 键确认", step);
                    RaiseStatusChanged($"已确认 {_confirmationCount} 次");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 确认操作异常不中断循环，下一轮仍可重试。
                    _loggingService.LogWarning(
                        $"  确认操作异常，将在下一轮重试。异常信息：{ex.Message}",
                        step);
                }

                // 冷却等待，避免对同一弹窗重复触发 Enter。
                try
                {
                    await Task.Delay(ConfirmCooldown, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        // 循环正常退出（取消令牌触发），输出本次运行确认次数统计。
        _loggingService.LogInfo($"监控已停止，本次运行共确认 {_confirmationCount} 次", step);
    }

    /// <summary>
    /// 尝试重新连接 Antigravity CDP。当监控循环中 WaitForFunctionAsync 抛 PlaywrightException
    /// （通常因 Antigravity 重启、崩溃导致 CDP 连接断开）时调用。
    /// 流程：关闭旧 Playwright/Browser 资源 → 重新发现 CDP 端口（Antigravity 可能换了端口）→
    /// 重建连接 → 选取第一个页面（多页面时记警告）→ 更新 _currentPage。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="step">日志步骤名。</param>
    /// <returns>true 表示重连成功且 _currentPage 已更新；false 表示重连失败（已记 ERROR 日志）。</returns>
    private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken, string step)
    {
        _loggingService.LogInfo("正在尝试重新连接 Antigravity CDP...", step);

        // 先关闭旧连接：锁内取出引用并清空字段，锁外执行关闭/释放（避免锁内 await）。
        IPlaywright? oldPlaywright;
        IBrowser? oldBrowser;
        lock (_resourceLock)
        {
            oldPlaywright = _playwright;
            oldBrowser = _browser;
            _playwright = null;
            _browser = null;
            _currentPage = null;
            _isConnected = false;
        }
        RaiseStatisticsChanged();
        if (oldBrowser is not null)
        {
            try
            {
                await oldBrowser.CloseAsync();
            }
            catch
            {
                // 旧连接关闭失败忽略，继续重连。
            }
        }
        if (oldPlaywright is not null)
        {
            try
            {
                oldPlaywright.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 重新读取端口（Antigravity 可能重启，端口可能变了）。
            var port = DiscoverCdpPort();
            _cdpPort = port;
            RaiseStatisticsChanged();
            _loggingService.LogInfo($"重新发现 CDP 调试端口：{port}", step);

            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");

            lock (_resourceLock)
            {
                _playwright = playwright;
                _browser = browser;
                _isConnected = true;
            }
            RaiseStatisticsChanged();

            if (browser.Contexts.Count == 0)
            {
                throw new InvalidOperationException(
                    "重新连接后未发现任何浏览器上下文，无法获取主窗口页面。");
            }

            var context = browser.Contexts[0];
            if (context.Pages.Count == 0)
            {
                // 上下文暂无页面，等待新页面出现。
                _loggingService.LogInfo("重新连接后上下文暂无页面，等待新页面出现...", step);
                _currentPage = await context.WaitForPageAsync();
            }
            else
            {
                // 多页面时记录警告，与初次连接行为一致。
                if (context.Pages.Count > 1)
                {
                    _loggingService.LogWarning(
                        $"重新连接后发现 {context.Pages.Count} 个页面，将选择第一个页面进行监控，" +
                        "其余页面不监控。",
                        step);
                }
                _currentPage = context.Pages[0];
            }

            // 获取页面标题用于日志展示，失败则用占位标题。
            string title;
            try
            {
                title = await _currentPage.TitleAsync();
            }
            catch (PlaywrightException ex)
            {
                title = "未知（页面可能正在导航/刷新）";
                _loggingService.LogWarning(
                    $"重连后获取页面标题失败，将以占位标题继续。异常信息：{ex.Message}",
                    step);
            }
            _loggingService.LogInfo($"重新连接成功，当前页面：{title}", step);
            RaiseStatusChanged("已重新连接 Antigravity CDP");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"重新连接失败：{ex.Message}", step, ex);
            return false;
        }
    }

    /// <summary>
    /// 在指定页面 DOM 中搜索 targetText 文本节点（忽略大小写），找到后提取其 DOM 路径、
    /// 标签、Class、可见性、尺寸及外层容器 HTML 等诊断信息，并尝试点击选中。
    /// 使用 TreeWalker 遍历所有文本节点，并递归进入 shadowRoot 覆盖 Web 组件场景。
    /// </summary>
    /// <param name="page">待搜索的页面。</param>
    /// <param name="targetText">待匹配的交互文本（不区分大小写）。</param>
    /// <returns>(found: 是否找到文本, clicked: 是否成功点击交互行, diagInfo: 详细诊断文本)。</returns>
    private async Task<(bool found, bool clicked, string diagInfo)> FindAndClickAllowThisTimeAsync(IPage page, string targetText)
    {
        var jsonResult = await page.EvaluateAsync<string>(
            "(target) => {" +
            "var lower = (target || '').toLowerCase();" +
            "var r = {" +
                "found: false," +
                "clicked: false," +
                "matchedText: ''," +
                "tag: ''," +
                "className: ''," +
                "id: ''," +
                "domPath: ''," +
                "isVisible: false," +
                "rectWidth: 0," +
                "rectHeight: 0," +
                "clickableTag: ''," +
                "clickableClass: ''," +
                "outerHtmlSnippet: ''" +
            "};" +
            "function getPath(el) {" +
                "var path = [];" +
                "var curr = el;" +
                "while (curr && curr !== document.body && curr !== document.documentElement) {" +
                    "var tag = curr.tagName ? curr.tagName.toLowerCase() : '';" +
                    "var cls = (curr.className && typeof curr.className === 'string')" +
                        "? '.' + curr.className.trim().split(/\\s+/).slice(0, 2).join('.')" +
                        ": '';" +
                    "path.unshift(tag + cls);" +
                    "curr = curr.parentElement;" +
                "}" +
                "return path.join(' > ');" +
            "}" +
            "function checkVisible(el) {" +
                "if (!el) return false;" +
                "var style = window.getComputedStyle(el);" +
                "if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;" +
                "var rect = el.getBoundingClientRect();" +
                "return rect.width > 0 && rect.height > 0;" +
            "}" +
            "function search(root) {" +
                "if (!root) return;" +
                "var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);" +
                "while (w.nextNode()) {" +
                    "var t = w.currentNode.textContent;" +
                    "if (t && t.toLowerCase().includes(lower)) {" +
                        "var el = w.currentNode.parentElement;" +
                        "if (!el) continue;" +
                        "var isVis = checkVisible(el);" +
                        "var rect = el.getBoundingClientRect();" +
                        "r.found = true;" +
                        "r.matchedText = t.trim().substring(0, 100);" +
                        "r.tag = el.tagName || '';" +
                        "r.className = (typeof el.className === 'string' ? el.className : '') || '';" +
                        "r.id = el.id || '';" +
                        "r.domPath = getPath(el);" +
                        "r.isVisible = isVis;" +
                        "r.rectWidth = Math.round(rect.width);" +
                        "r.rectHeight = Math.round(rect.height);" +
                        "var clickable = el;" +
                        "var sel = '.monaco-list-row,.action-item,.quick-input-list-entry,.quick-input-row,.list-row,button,[role=button],.quick-input-list .monaco-list-row';" +
                        "while (clickable && clickable !== document.body) {" +
                            "if (clickable.matches && clickable.matches(sel)) { break; }" +
                            "clickable = clickable.parentElement;" +
                        "}" +
                        "if (!clickable || clickable === document.body) { clickable = el; }" +
                        "r.clickableTag = clickable.tagName || '';" +
                        "r.clickableClass = (typeof clickable.className === 'string' ? clickable.className : '') || '';" +
                        "r.outerHtmlSnippet = clickable.outerHTML ? clickable.outerHTML.substring(0, 240) : '';" +
                        "try {" +
                            "clickable.click();" +
                            "r.clicked = true;" +
                        "} catch (e) {" +
                            "r.clicked = false;" +
                        "}" +
                        "return;" +
                    "}" +
                "}" +
            "}" +
            "search(document.body);" +
            "if (!r.found) {" +
                "document.querySelectorAll('*').forEach(function(el) {" +
                    "if (el.shadowRoot && !r.found) search(el.shadowRoot);" +
                "});" +
            "}" +
            "return JSON.stringify(r);" +
        "}", targetText);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonResult);
            var root = doc.RootElement;
            var found = root.GetProperty("found").GetBoolean();
            var clicked = root.GetProperty("clicked").GetBoolean();
            var matchedText = root.GetProperty("matchedText").GetString() ?? "";
            var tag = root.GetProperty("tag").GetString() ?? "";
            var className = root.GetProperty("className").GetString() ?? "";
            var id = root.GetProperty("id").GetString() ?? "";
            var domPath = root.GetProperty("domPath").GetString() ?? "";
            var isVisible = root.GetProperty("isVisible").GetBoolean();
            var rectW = root.GetProperty("rectWidth").GetInt32();
            var rectH = root.GetProperty("rectHeight").GetInt32();
            var clickableTag = root.GetProperty("clickableTag").GetString() ?? "";
            var clickableClass = root.GetProperty("clickableClass").GetString() ?? "";
            var outerHtml = root.GetProperty("outerHtmlSnippet").GetString() ?? "";

            var diag = $"【DOM定位分析】\n" +
                       $"  • 命中文字：\"{matchedText}\"\n" +
                       $"  • 所在元素：<{tag}> (class='{className}', id='{id}')\n" +
                       $"  • 可见状态：{(isVisible ? $"可见 (尺寸: {rectW}x{rectH})" : "不可见/隐藏")}\n" +
                       $"  • DOM路径：{domPath}\n" +
                       $"  • 触发点击容器：<{clickableTag} class='{clickableClass}'> (点击状态: {(clicked ? "成功" : "失败")})\n" +
                       $"  • 容器HTML片段：{outerHtml}";

            return (found, clicked, diag);
        }
        catch
        {
            var found = jsonResult.Contains("\"found\":true");
            var clicked = jsonResult.Contains("\"clicked\":true");
            return (found, clicked, $"原始分析结果: {jsonResult}");
        }
    }
}
