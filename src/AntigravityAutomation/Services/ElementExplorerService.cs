// 文件用途：界面元素探索服务实现 ElementExplorerService。
// 职责：
//   1. 启动指定 Antigravity IDE（Electron 应用），通过 Chrome DevTools Protocol（CDP）
//      连接其自动化端点，获取主窗口页面。
//   2. 枚举界面中所有按钮类可交互元素（标准 button、ARIA button、链接按钮、
//      Monaco 编辑器按钮、VS Code action-item），提取文字、角色、推荐 CSS 选择器、
//      边界框与可见性，返回 List<DiscoveredElement> 供界面展示与用户选择。
//   3. 全程通过注入的 ILoggingService 记录人类可读日志，标注"是什么"与"在做什么"。
//   4. StopAsync 幂等关闭 IBrowser、IPlaywright 与 Electron 进程。
// 设计说明：Microsoft.Playwright 1.49.0 已移除 Electron API（官方建议用 CDP 直连），
//           故本服务采用"启动 Electron 进程 + --remote-debugging-port + Chromium.ConnectOverCDPAsync"
//           方案，复刻原 Electron API 的底层行为。本服务独立实现启动逻辑，不与其他服务共享。
//           保存 IPlaywright、IBrowser 与 Process 字段供 StopAsync 释放。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AntigravityAutomation.Models;
using Microsoft.Playwright;

namespace AntigravityAutomation.Services;

/// <summary>
/// 界面元素探索服务实现。启动 Antigravity IDE 并枚举其按钮类可交互元素。
/// </summary>
public sealed class ElementExplorerService : IElementExplorerService
{
    // 日志服务，用于全程记录人类可读的探索过程日志。
    private readonly ILoggingService _loggingService;

    // Playwright 实例，StopAsync 时释放。
    private IPlaywright? _playwright;

    // 通过 CDP 连接得到的浏览器实例，StopAsync 时关闭。
    private IBrowser? _browser;

    // 启动的 Electron 进程，StopAsync 时结束。
    private Process? _electronProcess;

    // HttpClient 用于轮询 CDP 端点可用性。StopAsync 时释放。
    private HttpClient? _cdpProbeClient;

    // 同步锁，保护 StopAsync 与 ExploreButtonsAsync 并发时的资源状态。
    private readonly object _resourceLock = new();

    // 标记是否已停止，保证 StopAsync 幂等。
    private bool _stopped;

    /// <summary>
    /// 构造函数，注入日志服务。
    /// </summary>
    /// <param name="loggingService">日志服务，用于记录探索过程日志。</param>
    public ElementExplorerService(ILoggingService loggingService)
    {
        _loggingService = loggingService
            ?? throw new ArgumentNullException(nameof(loggingService));
    }

    /// <summary>
    /// 启动指定 Antigravity IDE 应用并枚举其界面中的按钮类可交互元素。
    /// </summary>
    /// <param name="appPath">Antigravity IDE 可执行文件绝对路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次探索发现的元素列表。若应用无可见按钮则返回空列表。</returns>
    public async Task<List<DiscoveredElement>> ExploreButtonsAsync(string appPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appPath))
        {
            throw new ArgumentException("Antigravity IDE 可执行文件路径不能为空", nameof(appPath));
        }

        if (!File.Exists(appPath))
        {
            throw new FileNotFoundException("找不到 Antigravity IDE 可执行文件", appPath);
        }

        const string step = "探索界面元素";
        var discoveredElements = new List<DiscoveredElement>();

        try
        {
            _loggingService.LogInfo($"准备启动 Antigravity IDE 进行界面元素探索，可执行文件路径：{appPath}", step);

            // 1. 启动 Electron 并通过 CDP 连接获取主窗口页面。
            var window = await LaunchElectronViaCdpAsync(appPath, cancellationToken, step);

            // 2. 等待窗口 DOM 加载完成，确保元素已渲染。
            cancellationToken.ThrowIfCancellationRequested();
            _loggingService.LogInfo("Antigravity IDE 主窗口已就绪，等待 DOM 内容加载完成以枚举按钮元素", step);
            await window.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            // 额外等待网络空闲，提升元素渲染稳定性。
            try
            {
                await window.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
                {
                    Timeout = 5000f
                });
            }
            catch (TimeoutException)
            {
                // 网络空闲等待超时不影响元素枚举，仅记录提示。
                _loggingService.LogWarning("等待网络空闲超时（5 秒），继续以当前 DOM 状态枚举按钮元素", step);
            }

            // 3. 枚举所有按钮类元素。使用复合选择器覆盖标准按钮、ARIA 按钮、链接按钮、
            //    Monaco 编辑器按钮与 VS Code action-item（Antigravity 基于 VS Code/Monaco 技术栈）。
            cancellationToken.ThrowIfCancellationRequested();
            _loggingService.LogInfo("开始枚举界面按钮元素，使用复合选择器覆盖 button、ARIA button、链接按钮、Monaco 按钮与 action-item", step);
            var buttonLocator = window.Locator(
                "button, [role='button'], a[role='button'], .monaco-button, .action-item");

            var elementLocators = await buttonLocator.AllAsync();
            _loggingService.LogInfo($"已定位到 {elementLocators.Count} 个候选按钮元素，开始逐个提取文字、角色、选择器与边界框信息", step);

            // 4. 逐个提取元素元信息。单个元素提取失败不中断整体枚举。
            var index = 1;
            foreach (var locator in elementLocators)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var element = await BuildDiscoveredElementAsync(locator, index);
                    discoveredElements.Add(element);
                    index++;
                }
                catch (Exception ex)
                {
                    // 单个元素信息提取失败时记录警告并跳过，继续处理后续元素。
                    _loggingService.LogWarning(
                        $"第 {index} 个按钮元素信息提取失败，已跳过该元素。失败原因：{ex.Message}",
                        step);
                    index++;
                }
            }

            _loggingService.LogInfo($"界面元素探索完成，共成功提取 {discoveredElements.Count} 个按钮元素的元信息", step);
            return discoveredElements;
        }
        catch (OperationCanceledException)
        {
            _loggingService.LogWarning("界面元素探索已被用户取消", step);
            throw;
        }
        catch (PlaywrightException ex)
        {
            _loggingService.LogError("界面元素探索过程中 Playwright 操作失败，探索中止", step, ex);
            throw;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("界面元素探索过程中发生未预期错误，探索中止", step, ex);
            throw;
        }
    }

    /// <summary>
    /// 停止当前探索会话并释放 Electron 进程与 Playwright 资源。幂等。
    /// </summary>
    /// <returns>表示异步停止操作的任务。</returns>
    public async Task StopAsync()
    {
        IPlaywright? playwrightToDispose;
        IBrowser? browserToClose;
        Process? processToKill;
        HttpClient? clientToDispose;

        lock (_resourceLock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;

            playwrightToDispose = _playwright;
            browserToClose = _browser;
            processToKill = _electronProcess;
            clientToDispose = _cdpProbeClient;

            _playwright = null;
            _browser = null;
            _electronProcess = null;
            _cdpProbeClient = null;
        }

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

        // 结束 Electron 进程。
        if (processToKill is not null)
        {
            try
            {
                if (!processToKill.HasExited)
                {
                    processToKill.Kill(entireProcessTree: true);
                    processToKill.WaitForExit(3000);
                }
                processToKill.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }

        // 释放 CDP 探测 HttpClient。
        clientToDispose?.Dispose();
    }

    /// <summary>
    /// 启动 Electron 进程并通过 CDP 连接，返回主窗口页面。
    /// 独立实现启动逻辑：选择空闲端口→启动进程→等待 CDP 端点→ConnectOverCDP→获取首窗口。
    /// </summary>
    /// <param name="appPath">Electron 可执行文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="step">日志步骤名。</param>
    /// <returns>主窗口页面。</returns>
    private async Task<IPage> LaunchElectronViaCdpAsync(string appPath, CancellationToken cancellationToken, string step)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 选择一个系统分配的空闲 TCP 端口，用于 Electron 远程调试端点。
        var debugPort = AllocateFreeTcpPort();
        _loggingService.LogInfo($"已为 Antigravity IDE 分配远程调试端口 {debugPort}，即将启动 Electron 进程", step);

        // 启动 Electron 进程，传入远程调试端口参数。
        var process = new Process();
        process.StartInfo.FileName = appPath;
        process.StartInfo.Arguments = $"--remote-debugging-port={debugPort}";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.CreateNoWindow = false;

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 Antigravity IDE 进程，可执行文件路径：{appPath}");
        }

        lock (_resourceLock)
        {
            _electronProcess = process;
        }

        _loggingService.LogInfo($"Antigravity IDE 进程已启动，进程 ID：{process.Id}，等待远程调试端点就绪", step);

        // 轮询等待 CDP 端点可用。
        var cdpProbeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        lock (_resourceLock)
        {
            _cdpProbeClient = cdpProbeClient;
        }

        var cdpVersionUrl = $"http://127.0.0.1:{debugPort}/json/version";
        await WaitForCdpEndpointAsync(cdpProbeClient, cdpVersionUrl, cancellationToken);
        _loggingService.LogInfo($"Antigravity IDE 远程调试端点已就绪（{cdpVersionUrl}），开始通过 CDP 建立自动化连接", step);

        cancellationToken.ThrowIfCancellationRequested();

        // 创建 Playwright 实例并通过 CDP 连接 Electron。
        var playwright = await Playwright.CreateAsync();
        lock (_resourceLock)
        {
            _playwright = playwright;
        }

        var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{debugPort}");
        lock (_resourceLock)
        {
            _browser = browser;
        }

        _loggingService.LogInfo("已通过 CDP 成功连接到 Antigravity IDE，正在获取主窗口页面", step);

        // 获取第一个浏览器上下文与页面。Electron 启动后可能尚未创建页面，需等待。
        var context = browser.Contexts.Count > 0
            ? browser.Contexts[0]
            : await browser.NewContextAsync();

        IPage window;
        if (context.Pages.Count > 0)
        {
            window = context.Pages[0];
        }
        else
        {
            _loggingService.LogInfo("Antigravity IDE 尚未创建窗口页面，等待首个窗口页面出现", step);
            window = await context.WaitForPageAsync();
        }

        _loggingService.LogInfo("已获取 Antigravity IDE 主窗口页面，准备枚举界面元素", step);
        return window;
    }

    /// <summary>
    /// 轮询等待 CDP 端点可用。最多等待 30 秒，每 500 毫秒探测一次。
    /// </summary>
    /// <param name="client">用于探测的 HttpClient。</param>
    /// <param name="cdpVersionUrl">CDP /json/version 端点 URL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task WaitForCdpEndpointAsync(HttpClient client, string cdpVersionUrl, CancellationToken cancellationToken)
    {
        const int maxAttempts = 60;
        const int delayMs = 500;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await client.GetAsync(cdpVersionUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 仅当用户真正取消时才抛出，避免中断轮询。
                throw;
            }
            catch
            {
                // 端点尚未就绪、连接被拒或 HttpClient 超时（TaskCanceledException），
                // 均属于正常启动等待过程，继续轮询。
            }

            await Task.Delay(delayMs, cancellationToken);
        }

        throw new TimeoutException($"等待 Antigravity IDE 远程调试端点就绪超时（{maxAttempts * delayMs / 1000} 秒），端点 URL：{cdpVersionUrl}");
    }

    /// <summary>
    /// 分配一个系统分配的空闲 TCP 端口。通过临时监听后立即释放获取。
    /// </summary>
    /// <returns>空闲端口号。</returns>
    private static int AllocateFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 从单个定位器构建 DiscoveredElement，提取文字、角色、推荐选择器、边界框与可见性。
    /// </summary>
    /// <param name="locator">元素定位器。</param>
    /// <param name="index">元素序号（从 1 开始）。</param>
    /// <returns>构建完成的 DiscoveredElement。</returns>
    private static async Task<DiscoveredElement> BuildDiscoveredElementAsync(ILocator locator, int index)
    {
        // 提取显示文字。
        var text = await locator.TextContentAsync();
        var cleanedText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        // 提取或推断角色。
        var role = await locator.GetAttributeAsync("role");
        if (string.IsNullOrWhiteSpace(role))
        {
            // 无 role 属性时根据标签名推断。
            var tagName = await SafeEvaluateTagNameAsync(locator);
            role = tagName switch
            {
                "button" => "button",
                "a" => "link",
                _ => tagName
            };
        }

        // 生成推荐 CSS 选择器。
        var selector = await GenerateRecommendedSelectorAsync(locator);

        // 提取边界框。BoundingBoxAsync 在元素不可见时返回 null。
        var boundingBox = await locator.BoundingBoxAsync();
        var boundingBoxString = boundingBox is not null
            ? $"{boundingBox.X:F0}, {boundingBox.Y:F0}, {boundingBox.Width:F0}, {boundingBox.Height:F0}"
            : null;

        // 提取可见性。
        var isVisible = await locator.IsVisibleAsync();

        return new DiscoveredElement
        {
            Index = index,
            Text = cleanedText,
            Role = role,
            Selector = selector,
            BoundingBox = boundingBoxString,
            IsVisible = isVisible
        };
    }

    /// <summary>
    /// 安全获取元素标签名（小写）。EvaluateAsync 失败时返回空字符串。
    /// </summary>
    /// <param name="locator">元素定位器。</param>
    /// <returns>小写标签名，失败时为空字符串。</returns>
    private static async Task<string> SafeEvaluateTagNameAsync(ILocator locator)
    {
        try
        {
            var tagName = await locator.EvaluateAsync<string>("el => (el.tagName || '').toLowerCase()");
            return tagName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 为元素生成推荐的稳定 CSS 选择器。优先级：
    /// data-testid > data-id > aria-label > id > tag.class > tag:has-text("text") > tag。
    /// </summary>
    /// <param name="locator">元素定位器。</param>
    /// <returns>推荐 CSS 选择器，无法生成时返回 null。</returns>
    private static async Task<string?> GenerateRecommendedSelectorAsync(ILocator locator)
    {
        // 1. 优先 data-testid（测试专用属性，最稳定）。
        var testId = await locator.GetAttributeAsync("data-testid");
        if (!string.IsNullOrWhiteSpace(testId))
        {
            return $"[data-testid=\"{testId.Trim()}\"]";
        }

        // 2. data-id（VS Code/Antigravity 常用数据属性）。
        var dataId = await locator.GetAttributeAsync("data-id");
        if (!string.IsNullOrWhiteSpace(dataId))
        {
            return $"[data-id=\"{dataId.Trim()}\"]";
        }

        // 3. aria-label（无障碍标签，语义稳定）。
        var ariaLabel = await locator.GetAttributeAsync("aria-label");
        if (!string.IsNullOrWhiteSpace(ariaLabel))
        {
            return $"[aria-label=\"{ariaLabel.Trim()}\"]";
        }

        // 4. id（页面内唯一，稳定）。
        var id = await locator.GetAttributeAsync("id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return $"#{id.Trim()}";
        }

        // 5. tag + 首个 class（兼顾可读性与稳定性）。
        var tagName = await SafeEvaluateTagNameAsync(locator);
        var classAttribute = await locator.GetAttributeAsync("class");
        if (!string.IsNullOrWhiteSpace(tagName) && !string.IsNullOrWhiteSpace(classAttribute))
        {
            var firstClass = classAttribute.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!string.IsNullOrWhiteSpace(firstClass))
            {
                return $"{tagName}.{firstClass}";
            }
        }

        // 6. tag + 文本（兜底，文本较短且不含引号时使用）。
        var text = await locator.TextContentAsync();
        if (!string.IsNullOrWhiteSpace(tagName) && !string.IsNullOrWhiteSpace(text))
        {
            var trimmedText = text.Trim();
            if (trimmedText.Length <= 30 && trimmedText.IndexOf('"') < 0)
            {
                return $"{tagName}:has-text(\"{trimmedText}\")";
            }
        }

        // 7. 最终兜底：仅返回标签名。
        return string.IsNullOrWhiteSpace(tagName) ? null : tagName;
    }
}