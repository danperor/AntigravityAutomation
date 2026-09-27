// 文件用途：Electron 自动化服务实现 ElectronAutomationService。
// 职责：
//   1. 通过 CDP 并行连接一个或多个已运行的 Antigravity 系 Electron 应用
//      （Antigravity / Antigravity IDE 等），分别获取其主窗口页面。
//   2. 为每个监控目标运行独立的持续监控循环：轮询主窗口 DOM 中是否出现
//      "Yes, allow this time" 交互项，一旦检测到立即点击选中该交互行并发送
//      Enter 键确认，然后继续等待，形成长期守护循环。
//   3. 全程通过注入的 ILoggingService 记录人类可读日志（日志步骤名为目标应用名，
//      便于区分多目标交错日志），并通过 StatusChanged / StatisticsChanged 事件
//      向界面推送状态变化与聚合统计（确认总数、各目标连接摘要）。
//   4. 支持通过 CancellationToken 提前取消全部监控循环，取消后释放全部 Playwright 资源。
//   5. StopAsync 幂等关闭所有目标的 IBrowser 与 IPlaywright。
// 设计说明：
//   * 各目标应用由用户事先启动并开启 CDP 远程调试（--remote-debugging-port），
//     Chromium 会将端口号写入目标用户数据目录下的 DevToolsActivePort 文件第一行
//     （如 %APPDATA%\Antigravity\DevToolsActivePort、
//     %APPDATA%\Antigravity IDE\DevToolsActivePort）。本服务读取该文件获取端口，
//     不再自行启动 Electron 进程，避免与用户已运行的实例冲突。
//   * 多目标并行：RunAutomationAsync 为每个启用的 AppTargetProfile 创建一个
//     TargetSession 并启动独立监控任务（Task.WhenAll 聚合）。任一目标掉线、重启
//     或未启动均不影响其他目标的监控。
//   * 目标未就绪（未启动或未开调试端口）时，对应监控循环进入后台等待：
//     每 AppAvailabilityRetryDelay 重新探测一次端口文件，就绪后自动接入监控，
//     因此工具可以先启动监控、后再打开任一目标应用。
//   * Microsoft.Playwright 1.49.0 已移除 Electron API（官方建议用 CDP 直连），
//     故本服务采用 Chromium.ConnectOverCDPAsync 方案。
//   * DOM 检测采用【事件驱动】而非轮询：通过原始 CDP Runtime.evaluate 向页面一次性注入
//     MutationObserver 观察器，DOM 变化命中目标文本时 resolve 一个 Promise，
//     C# 侧以 Runtime.evaluate(awaitPromise: true) 挂起等待该 Promise——
//     命中即返回（毫秒级），等待期间零 CPU 开销。
//     不使用 Playwright 的 WaitForFunctionAsync/带参 EvaluateAsync 的原因：
//     Antigravity IDE（VS Code 内核）工作台页面启用了 Trusted Types CSP
//     （require-trusted-types-for 'script'），Playwright 高层 API 需要在页面内
//     eval 字符串构造函数，会被 Trusted Types 拦截（EvalError）；
//     而原始 CDP Runtime.evaluate 由调试器通道直接求值整段脚本（含其中的
//     MutationObserver 与函数字面量），不经过"字符串→代码"的页面内汇点，
//     不受该限制（实证：同一页面 TitleAsync 可正常返回，WaitForFunctionAsync 报 EvalError）。
//   * 心跳与清理：每次等待以 HeartbeatInterval（60 秒）为界输出"仍在监控中"心跳并
//     重装等待器；断线/停止前对页面执行"取消全部等待器"脚本，避免在用户的 IDE
//     页面里遗留 MutationObserver。
//   * DOM 搜索 JS 使用 TreeWalker 遍历所有文本节点，并递归进入 shadowRoot，
//     以应对 Antigravity 系应用可能使用 Web 组件 shadow DOM 的情况；
//     智能过滤聊天历史/代码块中的静态文本，仅匹配可见的可交互容器。
//   * 页面导航/刷新期间的 "Execution context destroyed" 类错误视为瞬时状态，
//     本轮按"未检测到"处理，不误判断线重连。
//   * 当某目标 CDP 连接真正断开（应用重启、崩溃等）时，仅该目标的监控循环回到
//     "等待接入"阶段重新发现端口并重建连接，其他目标不受影响。
//   * 本服务独立实现监控逻辑，不与 ElementExplorerService 共享代码。
// 容错策略：
//   * 目标 CDP 端口文件缺失或连接失败：记 INFO/WARN 日志并每 5 秒自动重试，
//     不终止监控（等待用户启动应用或开启调试端口）。
//   * 监控循环中 CDP 调用抛 PlaywrightException：判定连接断开，释放本目标资源后
//     回到"等待接入"阶段自动重连（导航类瞬时错误除外，见上）。
//   * 监控循环中其他异常：记 WARN 日志，不中断循环，继续下一轮等待。
//   * 取消令牌触发：所有目标监控循环正常退出并记 INFO 日志。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AntigravityAutomation.Models;
using Microsoft.Playwright;

namespace AntigravityAutomation.Services;

/// <summary>
/// Electron 自动化服务实现。通过 CDP 并行连接多个已运行的 Antigravity 系应用，
/// 持续监控 "Yes, allow this time" 交互项并自动按 Enter 确认。
/// </summary>
public sealed class ElectronAutomationService : IElectronAutomationService
{
    // DevToolsActivePort 文件名（位于各目标用户数据目录下，由 Chromium 写入）。
    private const string DevToolsPortFileName = "DevToolsActivePort";

    // 事件驱动等待的心跳边界：单次 Runtime.evaluate(awaitPromise) 挂起等待的最长时长。
    // 超时后取消页面内等待器、记一次"仍在监控中"心跳并重新安装，避免长时间无输出造成"假死"观感。
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    // 页面导航/刷新等瞬时错误（执行上下文销毁）或等待器安装被拒后的重试间隔。
    private static readonly TimeSpan NavigationRetryDelay = TimeSpan.FromMilliseconds(500);

    // 等待异常后的重试间隔：非 Playwright 异常时短暂退避再继续等待，避免异常时紧密循环。
    private static readonly TimeSpan ReconnectRetryDelay = TimeSpan.FromSeconds(2);

    // 检测到交互项并按 Enter 后的冷却等待，避免对同一弹窗重复触发。
    private static readonly TimeSpan ConfirmCooldown = TimeSpan.FromSeconds(3);

    // 目标应用未就绪（未启动/未开启调试端口）时的轮询重试间隔。
    private static readonly TimeSpan AppAvailabilityRetryDelay = TimeSpan.FromSeconds(5);

    // 等待目标应用就绪时的心跳日志间隔轮数：每 12 轮（约 60 秒）记一次"仍在等待"日志。
    private const int AvailabilityHeartbeatIntervalRounds = 12;

    // 检测目标文本的 JS 函数体（参数 target 为待匹配文本，返回 bool）。
    // 通过 CDP Runtime.evaluate 以 (function(target){...})(jsonArg) 形式整体求值。
    // 仅匹配可见且处于按钮/交互行容器中的选项，智能过滤掉聊天历史/代码块中的普通文本。
    private const string DetectTargetTextBodyJs =
        "var lower = (target || '').toLowerCase();" +
        "function checkNode(node) {" +
            "if (!node || !node.textContent) return false;" +
            "var rawText = node.textContent.trim();" +
            "if (!rawText || !rawText.toLowerCase().includes(lower)) return false;" +
            "var el = node.parentElement;" +
            "if (!el) return false;" +
            "var isInsideCodeOrChat = el.closest('pre, code, .whitespace-pre-wrap, .rendered-markdown, .prose, .chat-markdown');" +
            "var isInsideButton = el.closest('button, [role=\"button\"], [role=\"option\"], [role=\"menuitem\"], .monaco-button, .monaco-list-row, .action-item, .quick-input-list-entry, .interactive-item');" +
            "if (isInsideCodeOrChat && !isInsideButton) return false;" +
            "if (rawText.length > 80 && !isInsideButton) return false;" +
            "var style = window.getComputedStyle(el);" +
            "if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;" +
            "var rect = el.getBoundingClientRect();" +
            "if (rect.width <= 0 || rect.height <= 0) return false;" +
            "var clickable = isInsideButton || el.closest('button, [role=\"button\"], [role=\"option\"], .monaco-list-row, .action-item, .quick-input-list-entry, .list-row');" +
            "if (!clickable && style.cursor !== 'pointer' && !el.getAttribute('onclick')) return false;" +
            "return true;" +
        "}" +
        "function search(root) {" +
            "if (!root) return false;" +
            "var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);" +
            "while (w.nextNode()) {" +
                "if (checkNode(w.currentNode)) return true;" +
            "}" +
            "return false;" +
        "}" +
        "if (search(document.body)) return true;" +
        "var foundInShadow = false;" +
        "document.querySelectorAll('*').forEach(function(el) {" +
            "if (el.shadowRoot && !foundInShadow) {" +
                "if (search(el.shadowRoot)) foundInShadow = true;" +
            "}" +
        "});" +
        "return foundInShadow;";

    // 查找并点击目标交互行的 JS 函数体（参数 target 为待匹配文本）。
    // 提取 DOM 路径、标签、Class、可见性、尺寸及外层容器 HTML 等诊断信息，
    // 并在点击前从审批卡片祖先容器提取请求正文（contextText，供审批追溯），
    // 点击选中最接近的可点击容器，返回 JSON 字符串结果。
    private const string FindAndClickBodyJs =
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
            "outerHtmlSnippet: ''," +
            "contextText: ''" +
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
        // 审批追溯：从命中元素向上找"最小有效审批卡片"——第一个文本量显著超过按钮文本、
        // 且剔除按钮行后仍剩实质内容的祖先容器（仅含按钮的容器继续向上找，
        // 避免请求正文在按钮行上一级时漏提）。按钮行的最终剔除在 C# 侧二次完成。
        // 必须在点击前调用——点击/确认后卡片可能从 DOM 消失。
        "function extractContext(el, btnText) {" +
            "var btnLower = (btnText || '').toLowerCase();" +
            "var skips = ['yes, allow this time','allow for this session','always allow','allow always','allow','deny','cancel','yes','no'];" +
            "function isButtonLine(t) {" +
                "var l = t.toLowerCase();" +
                "for (var i = 0; i < skips.length; i++) { l = l.split(skips[i]).join(' '); }" +
                "l = l.split(btnLower).join(' ');" +
                "return l.replace(/\\s+/g, '') === '';" +
            "}" +
            "var curr = el;" +
            "var minLen = (btnText || '').length + 15;" +
            "for (var depth = 0; curr && depth < 8; depth++) {" +
                "var raw = curr.innerText || curr.textContent || '';" +
                "var oneLine = raw.replace(/\\s+/g, ' ').trim();" +
                "if (oneLine.length > minLen) {" +
                    "var lines = raw.split('\\n');" +
                    "var hasContent = false;" +
                    "for (var j = 0; j < lines.length; j++) {" +
                        "var t = lines[j].replace(/\\s+/g, ' ').trim();" +
                        "if (t && !isButtonLine(t)) { hasContent = true; break; }" +
                    "}" +
                    "if (hasContent) return raw.replace(/\\r/g, '').substring(0, 600);" +
                "}" +
                "curr = curr.parentElement;" +
            "}" +
            "return '';" +
        "}" +
        "function checkNode(node) {" +
            "if (!node || !node.textContent) return null;" +
            "var rawText = node.textContent.trim();" +
            "if (!rawText || !rawText.toLowerCase().includes(lower)) return null;" +
            "var el = node.parentElement;" +
            "if (!el) return null;" +
            "var isInsideCodeOrChat = el.closest('pre, code, .whitespace-pre-wrap, .rendered-markdown, .prose, .chat-markdown');" +
            "var isInsideButton = el.closest('button, [role=\"button\"], [role=\"option\"], [role=\"menuitem\"], .monaco-button, .monaco-list-row, .action-item, .quick-input-list-entry, .interactive-item');" +
            "if (isInsideCodeOrChat && !isInsideButton) return null;" +
            "if (rawText.length > 80 && !isInsideButton) return null;" +
            "var style = window.getComputedStyle(el);" +
            "if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return null;" +
            "var rect = el.getBoundingClientRect();" +
            "if (rect.width <= 0 || rect.height <= 0) return null;" +
            "var clickable = isInsideButton || el.closest('button, [role=\"button\"], [role=\"option\"], .monaco-list-row, .action-item, .quick-input-list-entry, .list-row');" +
            "if (!clickable && style.cursor !== 'pointer' && !el.getAttribute('onclick')) return null;" +
            "return { el: el, rawText: rawText, rect: rect, clickable: clickable || el };" +
        "}" +
        "function search(root) {" +
            "if (!root) return false;" +
            "var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);" +
            "while (w.nextNode()) {" +
                "var res = checkNode(w.currentNode);" +
                "if (res) {" +
                    "r.found = true;" +
                    "r.matchedText = res.rawText.substring(0, 100);" +
                    "r.tag = res.el.tagName || '';" +
                    "r.className = (typeof res.el.className === 'string' ? res.el.className : '') || '';" +
                    "r.id = res.el.id || '';" +
                    "r.domPath = getPath(res.el);" +
                    "r.isVisible = true;" +
                    "r.rectWidth = Math.round(res.rect.width);" +
                    "r.rectHeight = Math.round(res.rect.height);" +
                    "var c = res.clickable;" +
                    "r.clickableTag = c.tagName || '';" +
                    "r.clickableClass = (typeof c.className === 'string' ? c.className : '') || '';" +
                    "r.outerHtmlSnippet = c.outerHTML ? c.outerHTML.substring(0, 240) : '';" +
                    // 审批追溯：先提取请求正文（点击后卡片可能消失），再执行点击。
                    "try {" +
                        "r.contextText = extractContext(res.el, res.rawText);" +
                    "} catch (e) {" +
                        "r.contextText = '';" +
                    "}" +
                    "try {" +
                        "c.click();" +
                        "r.clicked = true;" +
                    "} catch (e) {" +
                        "r.clicked = false;" +
                    "}" +
                    "return true;" +
                "}" +
            "}" +
            "return false;" +
        "}" +
        "if (!search(document.body)) {" +
            "document.querySelectorAll('*').forEach(function(el) {" +
                "if (el.shadowRoot && !r.found) search(el.shadowRoot);" +
            "});" +
        "}" +
        "return JSON.stringify(r);";

    // 事件驱动等待器的 JS 函数体（参数 target 为待匹配文本，waiterId 为本次等待标识）。
    // 语义与 Playwright WaitForFunctionAsync 一致，但以 MutationObserver 实现：
    // 立即命中返回 true；否则安装观察器并返回 Promise，DOM 变化命中时 resolve(true)。
    // 等待器登记在 window.__agWaiters[waiterId]，供超时取消脚本调用 cancel() 中止
    // （resolve(false) 并断开观察器），保证不在页面遗留观察器。
    // 整段由 CDP Runtime.evaluate 直接求值，不走页面内 eval，兼容 Trusted Types CSP。
    private const string WaitForTargetTextBodyJs =
        "window.__agWaiters = window.__agWaiters || {};" +
        "if (window.__agWaiters[waiterId]) return false;" +
        "function matches() {" + DetectTargetTextBodyJs + "}" +
        "if (matches()) return true;" +
        "return new Promise(function(resolve) {" +
            "var observer;" +
            "var done = false;" +
            "function finish(value) {" +
                "if (done) return;" +
                "done = true;" +
                "try { if (observer) observer.disconnect(); } catch (e) {}" +
                "try { delete window.__agWaiters[waiterId]; } catch (e) {}" +
                "resolve(value);" +
            "}" +
            "var root = document.body || document.documentElement;" +
            "if (!root) { finish(false); return; }" +
            "observer = new MutationObserver(function() {" +
                "try { if (matches()) finish(true); } catch (e) {}" +
            "});" +
            "observer.observe(root, { childList: true, subtree: true, characterData: true });" +
            "window.__agWaiters[waiterId] = { cancel: function() { finish(false); } };" +
        "});";

    // 取消指定等待器的 JS 函数体（参数 waiterId）。用于心跳超时后中止页面内等待器。
    private const string CancelWaiterBodyJs =
        "var w = (window.__agWaiters || {})[waiterId];" +
        "if (w && w.cancel) { w.cancel(); return true; }" +
        "return false;";

    // 取消页面内全部等待器的 JS 表达式（无参数）。用于断线/停止前清理页面遗留观察器。
    private const string CancelAllWaitersExpressionJs =
        "(function(){" +
            "var ws = window.__agWaiters || {};" +
            "var n = 0;" +
            "Object.keys(ws).forEach(function(k) { try { ws[k].cancel(); n++; } catch (e) {} });" +
            "return n;" +
        "})();";

    // 日志服务，用于全程记录人类可读的监控流程日志。
    private readonly ILoggingService _loggingService;

    // 审批审计服务，用于将每次确认行为（含请求正文）持久化到 logs/approvals.jsonl。
    private readonly IApprovalAuditService _approvalAuditService;

    // 当前运行的全部监控目标会话。仅在 _resourceLock 内增删改；
    // 各会话的 Playwright 资源字段由对应监控任务写入、由 StopAsync 统一释放。
    private readonly List<TargetSession> _sessions = new();

    // 同步锁，保护 _sessions 及会话状态字段的并发访问
    // （监控任务、StopAsync、RaiseStatisticsChanged 可能并发执行）。
    private readonly object _resourceLock = new();

    // 标记是否已停止，保证 StopAsync 幂等。
    // RunAutomationAsync 开始时复位为 false，确保停止后可以再次启动
    // （此前版本缺少复位，导致第二次运行后 StopAsync 提前返回、资源无法释放）。
    private bool _stopped;

    /// <summary>
    /// 构造函数，注入日志服务与审批审计服务。
    /// </summary>
    /// <param name="loggingService">日志服务，用于记录监控流程日志。</param>
    /// <param name="approvalAuditService">审批审计服务，用于持久化每次确认行为的追溯记录。</param>
    public ElectronAutomationService(
        ILoggingService loggingService,
        IApprovalAuditService approvalAuditService)
    {
        _loggingService = loggingService
            ?? throw new ArgumentNullException(nameof(loggingService));
        _approvalAuditService = approvalAuditService
            ?? throw new ArgumentNullException(nameof(approvalAuditService));
    }

    /// <summary>
    /// 状态变化通知。参数为人类可读的状态描述（带目标名前缀），界面可订阅以实时更新状态栏。
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 运行统计变化通知。在确认次数增加、任一目标连接建立/断开、停止等关键节点触发，
    /// 携带当前 <see cref="AutomationStatistics"/> 快照（聚合全部目标的确认总数与状态摘要），
    /// 界面据此更新控制面板统计信息。
    /// </summary>
    public event EventHandler<AutomationStatistics>? StatisticsChanged;

    /// <summary>
    /// 审批追溯通知。每次自动确认完成后触发，携带含请求正文的
    /// <see cref="ApprovalRecord"/> 审计快照（记录已先行持久化到审计文件）。
    /// </summary>
    public event EventHandler<ApprovalRecord>? ApprovalRecorded;

    /// <summary>
    /// 按给定配置启动多目标并行监控：
    /// 为每个启用的监控目标创建会话与独立监控任务（发现 CDP 端口→连接→
    /// 轮询 DOM 检测目标文本→点击交互行并按 Enter 确认→继续等待），
    /// 全部任务并行运行直至取消令牌触发。
    /// </summary>
    /// <param name="config">自动化配置，指定监控目标列表与匹配文本。</param>
    /// <param name="cancellationToken">取消令牌，用于界面"停止"按钮中断监控循环。</param>
    public async Task RunAutomationAsync(AutomationConfig config, CancellationToken cancellationToken)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        const string stepMonitor = "监控循环";

        var targetText = string.IsNullOrWhiteSpace(config.YesAllowButtonText)
            ? "Yes, allow this time"
            : config.YesAllowButtonText.Trim();

        var targets = ResolveTargets(config);

        // 重置本次运行状态：清空旧会话、按目标列表创建新会话。
        // 同时复位 _stopped，确保上一轮停止后本轮 StopAsync 仍可正常释放资源。
        lock (_resourceLock)
        {
            _sessions.Clear();
            foreach (var target in targets)
            {
                _sessions.Add(new TargetSession { Profile = target });
            }
            _stopped = false;
        }
        RaiseStatisticsChanged();

        if (targets.Count == 0)
        {
            _loggingService.LogWarning(
                "没有启用任何监控目标，本次监控未启动。请在高级设置的监控目标中勾选至少一个应用。",
                stepMonitor);
            RaiseStatusChanged("未启动：没有启用的监控目标");
            return;
        }

        var targetNames = string.Join("、", targets.Select(t => t.Name));
        RaiseStatusChanged($"正在连接监控目标（共 {targets.Count} 个）");
        _loggingService.LogInfo(
            $"开始并行监控 {targets.Count} 个目标应用：{targetNames}。" +
            $"匹配文本：'{targetText}'（忽略大小写），检测到将自动选中交互行并按 Enter 确认",
            stepMonitor);

        // 为每个目标会话启动独立监控任务，并行运行。
        TargetSession[] sessionsSnapshot;
        lock (_resourceLock)
        {
            sessionsSnapshot = _sessions.ToArray();
        }
        var monitorTasks = sessionsSnapshot
            .Select(session => RunTargetMonitorAsync(session, targetText, cancellationToken))
            .ToList();

        try
        {
            await Task.WhenAll(monitorTasks);
        }
        catch (OperationCanceledException)
        {
            _loggingService.LogInfo("监控已停止", stepMonitor);
            throw;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("监控因未预期错误而中止", stepMonitor, ex);
            throw;
        }
        finally
        {
            // 无论监控正常停止、失败或取消，都释放全部目标的 Playwright 资源。
            await StopAsync();
        }
    }

    /// <summary>
    /// 停止当前正在执行的全部监控循环并释放所有目标的资源。幂等。
    /// </summary>
    /// <returns>表示异步停止操作的任务。</returns>
    public async Task StopAsync()
    {
        TargetSession[] sessionsSnapshot;

        lock (_resourceLock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            sessionsSnapshot = _sessions.ToArray();
        }

        _loggingService.LogInfo("开始释放 Playwright 自动化资源", "释放资源");

        // 逐个释放各目标的 CDP 连接与 Playwright 实例。
        foreach (var session in sessionsSnapshot)
        {
            await DisconnectSessionAsync(session, raiseStatistics: false);
        }
        RaiseStatisticsChanged();

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
    /// 触发 StatisticsChanged 事件，向界面推送当前聚合统计快照。
    /// 聚合规则：确认次数为全部目标之和；CdpPort 取首个已连接目标端口；
    /// IsConnected 表示是否至少一个目标已连接；TargetSummary 为逐目标状态摘要。
    /// </summary>
    private void RaiseStatisticsChanged()
    {
        AutomationStatistics snapshot;
        lock (_resourceLock)
        {
            var totalConfirmations = 0;
            var firstConnectedPort = 0;
            var anyConnected = false;
            var summaryBuilder = new StringBuilder();

            foreach (var session in _sessions)
            {
                totalConfirmations += session.ConfirmationCount;
                if (session.IsConnected)
                {
                    anyConnected = true;
                    if (firstConnectedPort == 0)
                    {
                        firstConnectedPort = session.CdpPort;
                    }
                }

                if (summaryBuilder.Length > 0)
                {
                    summaryBuilder.Append(" · ");
                }
                summaryBuilder.Append(session.IsConnected
                    ? $"{session.Profile.Name}:{session.CdpPort}✓"
                    : $"{session.Profile.Name}:等待中");
            }

            snapshot = new AutomationStatistics(
                totalConfirmations,
                firstConnectedPort,
                anyConnected,
                summaryBuilder.ToString());
        }

        StatisticsChanged?.Invoke(this, snapshot);
    }

    /// <summary>
    /// 解析本次运行的监控目标列表。
    /// 配置未提供 Targets（旧版配置）时回退为内置默认双目标；
    /// 仅保留 Enabled 且用户数据目录名非空的目标；按用户数据目录名去重（同名仅保留首个）。
    /// </summary>
    /// <param name="config">自动化配置。</param>
    /// <returns>本次运行的监控目标列表（克隆副本，修改不影响配置实例）。</returns>
    private List<AppTargetProfile> ResolveTargets(AutomationConfig config)
    {
        var configured = config.Targets;
        var source = (configured is null || configured.Count == 0)
            ? AppTargetProfile.CreateDefaults()
            : configured;

        var seenDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AppTargetProfile>();

        foreach (var target in source)
        {
            if (target is null || !target.Enabled)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(target.UserDataDirectoryName))
            {
                _loggingService.LogWarning(
                    $"监控目标 '{target?.Name}' 未配置用户数据目录名，已跳过。",
                    "监控循环");
                continue;
            }
            if (!seenDirectoryNames.Add(target.UserDataDirectoryName))
            {
                _loggingService.LogWarning(
                    $"监控目标 '{target.Name}' 的用户数据目录 '{target.UserDataDirectoryName}' " +
                    "与前面的目标重复，已跳过。",
                    "监控循环");
                continue;
            }

            result.Add(target.Clone());
        }

        return result;
    }

    /// <summary>
    /// 单个监控目标的守护循环：
    /// 等待目标应用调试端口就绪 → 建立 CDP 连接 → 轮询检测目标文本出现 →
    /// 点击交互行并按 Enter 确认 → 继续等待。目标掉线时自动回到"等待接入"阶段重连，
    /// 全程不中断其他目标的监控，直至取消令牌触发或服务停止。
    /// </summary>
    /// <param name="session">目标会话（承载连接状态与该目标确认计数）。</param>
    /// <param name="targetText">待匹配的交互项文本（不区分大小写）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RunTargetMonitorAsync(
        TargetSession session,
        string targetText,
        CancellationToken cancellationToken)
    {
        // 日志步骤名直接使用目标应用名，多目标日志交错时一眼可辨归属。
        var step = session.Profile.Name;

        while (!cancellationToken.IsCancellationRequested && !IsStopped)
        {
            // ===== 阶段 1：确保本目标已通过 CDP 连接（未就绪则后台等待并重试） =====
            if (!IsSessionConnected(session))
            {
                await EnsureConnectedAsync(session, cancellationToken);
                continue;
            }

            // ===== 阶段 2：事件驱动式等待目标交互项出现 =====
            // 向页面注入 MutationObserver 等待器并以 CDP Runtime.evaluate(awaitPromise)
            // 挂起等待：DOM 变化命中即返回（毫秒级），等待期间零 CPU 开销。
            bool detected;
            try
            {
                detected = await WaitForTargetTextAsync(session, targetText, cancellationToken, step);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PlaywrightException ex)
            {
                // CDP 连接断开（目标应用重启、崩溃等）：释放本目标资源，
                // 回到"等待接入"阶段自动重连，不影响其他目标。
                _loggingService.LogWarning(
                    $"等待 '{targetText}' 时 Playwright 异常，判定 CDP 连接已断开，将自动重新接入。" +
                    $"异常信息：{ex.Message}",
                    step);
                await DisconnectSessionAsync(session);
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

            if (!detected)
            {
                // 服务已停止（未触发取消）：退出循环。
                break;
            }

            // ===== 阶段 3：检测到交互项，点击选中并按 Enter 确认 =====
            var totalConfirmations = IncrementConfirmationCount(session);
            _loggingService.LogInfo(
                $"✓ [全局第{totalConfirmations}次确认 / 本目标第{GetSessionConfirmationCount(session)}次] " +
                $"检测到包含 '{targetText}' 的交互行出现",
                step);
            RaiseStatisticsChanged();

            try
            {
                // 通过 CDP 执行查找+点击脚本，并获取详细 DOM 诊断信息与审批请求正文
                var (found, clicked, diagInfo, requestContext) =
                    await FindAndClickAllowThisTimeAsync(session, targetText);

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

                IPage? page;
                lock (_resourceLock)
                {
                    page = session.Page;
                }
                var enterSent = false;
                if (page is not null)
                {
                    await page.Keyboard.PressAsync("Enter");
                    enterSent = true;
                    _loggingService.LogInfo("  已发送 Enter 键确认", step);
                }
                else
                {
                    _loggingService.LogWarning("  页面引用缺失，无法发送 Enter 键", step);
                }

                // 审批追溯：持久化本次确认行为（含请求正文）并通知界面审批历史。
                // 审计服务内部自行消化 IO 异常；事件订阅方异常由外层 catch 兜底，
                // 均不影响监控主流程。
                var approvalRecord = new ApprovalRecord(
                    DateTimeOffset.Now,
                    session.Profile.Name,
                    targetText,
                    requestContext,
                    clicked,
                    enterSent);
                _approvalAuditService.Append(approvalRecord);
                ApprovalRecorded?.Invoke(this, approvalRecord);

                RaiseStatusChanged($"[{step}] 已确认 {GetSessionConfirmationCount(session)} 次");
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

        // 循环正常退出（取消令牌触发或服务停止），输出本目标确认次数统计。
        _loggingService.LogInfo(
            $"监控已停止，本次运行该目标共确认 {GetSessionConfirmationCount(session)} 次",
            step);
    }

    /// <summary>
    /// 事件驱动式等待目标交互项出现（TT 兼容版 WaitForFunction）：
    /// 通过原始 CDP Runtime.evaluate 向页面注入 MutationObserver 等待器并
    /// awaitPromise 挂起等待，DOM 变化命中目标文本即返回；每 HeartbeatInterval
    /// 无命中则取消旧等待器、记心跳日志并重装，保证等待期间零 CPU 且不留观察器泄漏。
    /// 导航类瞬时错误稍候重试；真正的 CDP 断线以 PlaywrightException 上抛给调用方。
    /// </summary>
    /// <param name="session">目标会话（须已连接）。</param>
    /// <param name="targetText">待匹配的交互项文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="step">日志步骤名（目标应用名）。</param>
    /// <returns>true 表示检测到目标文本；false 表示服务已停止（未触发取消但需退出）。</returns>
    private async Task<bool> WaitForTargetTextAsync(
        TargetSession session,
        string targetText,
        CancellationToken cancellationToken,
        string step)
    {
        var monitorStartedAt = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested && !IsStopped)
        {
            // 安装页面内等待器并挂起等待其 Promise 完成（命中/被取消/断线）。
            var waiterId = Guid.NewGuid().ToString("N");
            var waitTask = EvaluateViaCdpAsync(
                session,
                BuildWaiterExpression(targetText, waiterId),
                awaitPromise: true);

            // 等待命中或心跳超时（awaitPromise 的 SendAsync 不含协议超时，心跳完全由本侧控制）。
            var completed = await Task.WhenAny(waitTask, Task.Delay(HeartbeatInterval, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            if (completed != waitTask)
            {
                // 心跳超时：放弃本次等待任务（观察其潜在异常避免 UnobservedTaskException），
                // 取消页面内等待器（best-effort），记心跳日志后重新安装。
                ObserveFault(waitTask);
                await CancelWaiterAsync(session, waiterId);
                _loggingService.LogInfo(
                    $"仍在监控中 - 已等待 {(int)(DateTime.UtcNow - monitorStartedAt).TotalSeconds} 秒，" +
                    $"尚未检测到包含 '{targetText}' 的交互项",
                    step);
                RaiseStatusChanged($"[{step}] 监控中 - 等待 '{targetText}' 出现");
                continue;
            }

            JsonElement? result;
            try
            {
                result = await waitTask;
            }
            catch (PlaywrightException ex) when (IsTransientNavigationError(ex))
            {
                // 页面导航/刷新导致执行上下文销毁：稍候重试安装，不做断线重连。
                await Task.Delay(NavigationRetryDelay, cancellationToken);
                continue;
            }

            if (result is null)
            {
                // 页面侧脚本异常（如导航间隙）：稍候重试。
                await Task.Delay(NavigationRetryDelay, cancellationToken);
                continue;
            }

            var detected = result.Value.TryGetProperty("result", out var resultElement)
                && resultElement.TryGetProperty("value", out var valueElement)
                && valueElement.ValueKind == JsonValueKind.True;

            if (detected)
            {
                return true;
            }

            // false：重复安装守卫或页面无根节点等边缘情况，稍候重试。
            await Task.Delay(NavigationRetryDelay, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false; // 服务已停止（未取消）：返回 false 让调用方退出循环。
    }

    /// <summary>
    /// 取消页面内指定等待器（best-effort，任何失败均吞没）。
    /// 取消会让等待 Promise 以 false 结束并断开 MutationObserver。
    /// </summary>
    /// <param name="session">目标会话。</param>
    /// <param name="waiterId">等待器标识。</param>
    private async Task CancelWaiterAsync(TargetSession session, string waiterId)
    {
        try
        {
            await EvaluateViaCdpAsync(session, BuildCancelWaiterExpression(waiterId), awaitPromise: false);
        }
        catch
        {
            // best-effort：连接已断开等场景下忽略。
        }
    }

    /// <summary>
    /// 取消页面内全部等待器（best-effort，最多等待 2 秒）。
    /// 在断开 CDP 连接前调用，避免在用户的 IDE 页面中遗留 MutationObserver。
    /// </summary>
    /// <param name="session">目标会话。</param>
    private async Task CancelAllWaitersAsync(TargetSession session)
    {
        ICDPSession? cdp;
        lock (_resourceLock)
        {
            cdp = session.Cdp;
        }
        if (cdp is null)
        {
            return;
        }

        try
        {
            var sendTask = cdp.SendAsync("Runtime.evaluate", new Dictionary<string, object>
            {
                ["expression"] = CancelAllWaitersExpressionJs,
                ["returnByValue"] = true
            });
            // 最多等待 2 秒，断线场景下不拖累停止/重连流程。
            await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(2)));
            ObserveFault(sendTask);
        }
        catch
        {
            // best-effort：忽略。
        }
    }

    /// <summary>
    /// 观察被放弃任务的异常，避免触发 UnobservedTaskException。
    /// </summary>
    /// <param name="task">不再 await 的任务。</param>
    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 组装事件驱动等待器的完整调用表达式：
    /// (function(target, waiterId){ ...body... })("json文本", "json等待器标识")。
    /// </summary>
    /// <param name="targetText">待匹配的交互文本。</param>
    /// <param name="waiterId">本次等待器标识。</param>
    /// <returns>完整调用表达式字符串。</returns>
    private static string BuildWaiterExpression(string targetText, string waiterId)
    {
        return "(function(target, waiterId){" + WaitForTargetTextBodyJs + "})(" +
               JsonSerializer.Serialize(targetText) + "," + JsonSerializer.Serialize(waiterId) + ");";
    }

    /// <summary>
    /// 组装取消指定等待器的完整调用表达式。
    /// </summary>
    /// <param name="waiterId">等待器标识。</param>
    /// <returns>完整调用表达式字符串。</returns>
    private static string BuildCancelWaiterExpression(string waiterId)
    {
        return "(function(waiterId){" + CancelWaiterBodyJs + "})(" +
               JsonSerializer.Serialize(waiterId) + ");";
    }

    /// <summary>
    /// 判断 PlaywrightException 是否为页面导航/刷新导致的瞬时错误
    /// （执行上下文销毁、框架分离等）。此类错误在页面加载完成后自然恢复，
    /// 不应触发断线重连。
    /// </summary>
    /// <param name="ex">Playwright 异常。</param>
    /// <returns>true 表示为导航类瞬时错误。</returns>
    private static bool IsTransientNavigationError(PlaywrightException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot find object", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Inspected target navigated", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Frame was detached", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 确保指定目标已通过 CDP 连接。若目标调试端口文件不存在（应用未启动或未开启
    /// 远程调试）或连接失败，则每 <see cref="AppAvailabilityRetryDelay"/> 自动重试，
    /// 直至连接成功、取消令牌触发或服务停止。
    /// </summary>
    /// <param name="session">目标会话，连接成功后其资源字段与连接状态被填充。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task EnsureConnectedAsync(TargetSession session, CancellationToken cancellationToken)
    {
        var step = session.Profile.Name;
        var waitRounds = 0; // 等待轮数，用于首条提示与周期性心跳日志。

        while (!cancellationToken.IsCancellationRequested && !IsStopped)
        {
            // ----- 步骤 1：发现 CDP 端口 -----
            int cdpPort;
            try
            {
                cdpPort = DiscoverCdpPort(session.Profile);
            }
            catch (Exception)
            {
                // 端口文件缺失或内容无效：目标应用未就绪，进入后台等待。
                waitRounds++;
                if (waitRounds == 1)
                {
                    _loggingService.LogInfo(
                        "未检测到调试端口（应用未启动，或未以 --remote-debugging-port 开启远程调试），" +
                        $"将每 {(int)AppAvailabilityRetryDelay.TotalSeconds} 秒自动重试直至就绪",
                        step);
                    RaiseStatusChanged($"[{step}] 等待应用调试端口就绪...");
                }
                else if (waitRounds % AvailabilityHeartbeatIntervalRounds == 0)
                {
                    _loggingService.LogInfo(
                        $"仍在等待应用调试端口就绪 - 已等待约 " +
                        $"{waitRounds * (int)AppAvailabilityRetryDelay.TotalSeconds} 秒",
                        step);
                }

                await Task.Delay(AppAvailabilityRetryDelay, cancellationToken);
                continue;
            }

            // ----- 步骤 2：通过 CDP 连接目标应用 -----
            IPlaywright? playwright = null;
            IBrowser? browser = null;
            try
            {
                var cdpEndpoint = $"http://127.0.0.1:{cdpPort}";

                playwright = await Playwright.CreateAsync();
                browser = await playwright.Chromium.ConnectOverCDPAsync(cdpEndpoint);

                // 获取第一个浏览器上下文与页面。目标应用已运行，通常存在至少一个页面。
                if (browser.Contexts.Count == 0)
                {
                    throw new InvalidOperationException(
                        "已通过 CDP 连接，但未发现任何浏览器上下文，无法获取主窗口页面。");
                }

                var context = browser.Contexts[0];
                if (context.Pages.Count == 0)
                {
                    throw new InvalidOperationException(
                        "已通过 CDP 连接，但当前上下文未发现任何页面（主窗口可能尚未打开）。");
                }

                // 多页面支持说明：当前实现仅监控第一个页面（DOM 轮询为 page 级别 API）。
                if (context.Pages.Count > 1)
                {
                    _loggingService.LogWarning(
                        $"当前上下文发现 {context.Pages.Count} 个页面，将选择第一个页面进行监控，其余页面不监控。",
                        step);
                }

                var page = context.Pages[0];

                // 创建直达该页面的原始 CDP 会话，用于执行 DOM 检测/点击脚本。
                // 原始 Runtime.evaluate 由调试器通道直接求值，不受页面 Trusted Types CSP 限制
                // （Playwright 的 WaitForFunctionAsync/带参 EvaluateAsync 在页面内 eval 字符串，
                // 会被 VS Code 内核工作台的 require-trusted-types-for 'script' 拦截）。
                var cdpSession = await context.NewCDPSessionAsync(page);

                // 获取页面标题用于日志展示。页面可能正在导航/刷新，TitleAsync 可能失败，
                // 失败时使用占位标题，不中断接入流程。
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
                        step);
                }

                // 连接成功：资源所有权移交会话。
                lock (_resourceLock)
                {
                    session.Playwright = playwright;
                    session.Browser = browser;
                    session.Page = page;
                    session.Cdp = cdpSession;
                    session.CdpPort = cdpPort;
                    session.IsConnected = true;
                    playwright = null;
                    browser = null;
                }
                RaiseStatisticsChanged();

                _loggingService.LogInfo(
                    $"已通过 CDP 连接（端口 {cdpPort}），页面标题：{pageTitle}（共 {context.Pages.Count} 个页面）",
                    step);
                RaiseStatusChanged($"[{step}] 已连接（端口 {cdpPort}），监控中");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 连接失败（目标应用可能正在重启、端口刚切换等），退避后重试。
                _loggingService.LogWarning(
                    $"通过 CDP 端口 {cdpPort} 连接失败，{(int)AppAvailabilityRetryDelay.TotalSeconds} 秒后重试。" +
                    $"异常信息：{ex.Message}",
                    step);
                await Task.Delay(AppAvailabilityRetryDelay, cancellationToken);
            }
            finally
            {
                // 连接流程失败时释放本轮创建的半成品资源（成功时已置 null，不会重复释放）。
                if (browser is not null)
                {
                    try
                    {
                        await browser.CloseAsync();
                    }
                    catch
                    {
                        // 关闭失败忽略。
                    }
                }
                if (playwright is not null)
                {
                    try
                    {
                        playwright.Dispose();
                    }
                    catch
                    {
                        // 忽略。
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 释放指定目标会话的 CDP 连接与 Playwright 资源，并将连接状态置为未连接。幂等。
    /// </summary>
    /// <param name="session">待释放的目标会话。</param>
    /// <param name="raiseStatistics">是否在释放后触发一次统计快照推送（StopAsync 批量释放时传 false，统一最后推送）。</param>
    /// <returns>表示异步释放操作的任务。</returns>
    private async Task DisconnectSessionAsync(TargetSession session, bool raiseStatistics = true)
    {
        // 断开前先取消页面内全部等待器，避免在目标应用页面中遗留 MutationObserver。
        await CancelAllWaitersAsync(session);

        IPlaywright? playwrightToDispose;
        IBrowser? browserToClose;

        lock (_resourceLock)
        {
            playwrightToDispose = session.Playwright;
            browserToClose = session.Browser;

            session.Playwright = null;
            session.Browser = null;
            session.Page = null;
            session.Cdp = null;
            session.IsConnected = false;
            session.CdpPort = 0;
        }

        if (raiseStatistics)
        {
            RaiseStatisticsChanged();
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
    }

    /// <summary>
    /// 将 JS 函数体与目标文本参数拼装为可在页面直接求值的调用表达式：
    /// (function(target){ ...body... })("json编码的文本")。参数以 JSON 字面量内嵌，
    /// 避免 Playwright 带参求值时在页面内 eval 构造函数（会被 Trusted Types 拦截）。
    /// </summary>
    /// <param name="functionBodyJs">JS 函数体（可使用 target 参数）。</param>
    /// <param name="targetText">待匹配的交互文本。</param>
    /// <returns>完整调用表达式字符串。</returns>
    private static string BuildCallExpression(string functionBodyJs, string targetText)
    {
        return "(function(target){" + functionBodyJs + "})(" +
               JsonSerializer.Serialize(targetText) + ");";
    }

    /// <summary>
    /// 通过原始 CDP Runtime.evaluate 执行 JS 表达式并读取字符串结果。
    /// 页面 JS 侧抛异常（exceptionDetails）时返回 null。
    /// </summary>
    /// <param name="session">目标会话（须已连接，持有 CDP 会话）。</param>
    /// <param name="expression">完整 JS 调用表达式。</param>
    /// <returns>表达式求值结果字符串；无结果或异常时为 null。</returns>
    /// <exception cref="PlaywrightException">CDP 会话不可用或协议层失败。</exception>
    private async Task<string?> EvaluateStringViaCdpAsync(TargetSession session, string expression)
    {
        var result = await EvaluateViaCdpAsync(session, expression);
        if (result is null)
        {
            return null;
        }

        return result.Value.TryGetProperty("result", out var resultElement)
            && resultElement.TryGetProperty("value", out var valueElement)
            && valueElement.ValueKind == JsonValueKind.String
                ? valueElement.GetString()
                : null;
    }

    /// <summary>
    /// 通过目标会话的原始 CDP 通道执行 Runtime.evaluate（returnByValue），
    /// 返回协议响应根 JsonElement。页面 JS 侧异常（exceptionDetails）时返回 null。
    /// </summary>
    /// <param name="session">目标会话（须已连接，持有 CDP 会话）。</param>
    /// <param name="expression">完整 JS 表达式。</param>
    /// <param name="awaitPromise">是否以 awaitPromise 模式挂起等待表达式返回的 Promise 完成
    /// （用于事件驱动等待器；此时 SendAsync 本身无协议超时，由调用方自行控制超时与取消）。</param>
    /// <returns>Runtime.evaluate 响应根元素；会话不可用或页面侧异常时为 null。</returns>
    /// <exception cref="PlaywrightException">CDP 会话不可用（上层按断线处理）。</exception>
    private async Task<JsonElement?> EvaluateViaCdpAsync(TargetSession session, string expression, bool awaitPromise = false)
    {
        ICDPSession? cdp;
        lock (_resourceLock)
        {
            cdp = session.Cdp;
        }
        if (cdp is null)
        {
            throw new PlaywrightException("CDP 会话不可用（目标未连接或已断开）");
        }

        var args = new Dictionary<string, object>
        {
            ["expression"] = expression,
            ["returnByValue"] = true
        };
        if (awaitPromise)
        {
            args["awaitPromise"] = true;
        }

        var response = await cdp.SendAsync("Runtime.evaluate", args);

        if (response is null)
        {
            return null;
        }

        // 页面 JS 侧抛异常：按无结果处理（导航间隙等瞬时场景下一轮自然恢复）。
        if (response.Value.TryGetProperty("exceptionDetails", out _))
        {
            return null;
        }

        return response.Value;
    }

    /// <summary>
    /// 增加指定目标的确认计数并返回全部目标的累计确认总数。
    /// </summary>
    /// <param name="session">发生确认的目标会话。</param>
    /// <returns>全部目标的累计确认总数。</returns>
    private int IncrementConfirmationCount(TargetSession session)
    {
        lock (_resourceLock)
        {
            session.ConfirmationCount++;
            var total = 0;
            foreach (var item in _sessions)
            {
                total += item.ConfirmationCount;
            }
            return total;
        }
    }

    /// <summary>
    /// 读取指定目标会话的确认计数（线程安全）。
    /// </summary>
    /// <param name="session">目标会话。</param>
    /// <returns>该目标本次运行的确认次数。</returns>
    private int GetSessionConfirmationCount(TargetSession session)
    {
        lock (_resourceLock)
        {
            return session.ConfirmationCount;
        }
    }

    /// <summary>
    /// 读取会话连接状态（线程安全）。
    /// </summary>
    /// <param name="session">目标会话。</param>
    /// <returns>true 表示该目标已通过 CDP 连接。</returns>
    private bool IsSessionConnected(TargetSession session)
    {
        lock (_resourceLock)
        {
            return session.IsConnected;
        }
    }

    /// <summary>
    /// 读取服务停止标志（线程安全）。监控循环据此在服务停止后及时退出。
    /// </summary>
    private bool IsStopped
    {
        get
        {
            lock (_resourceLock)
            {
                return _stopped;
            }
        }
    }

    /// <summary>
    /// 从目标应用用户数据目录下的 DevToolsActivePort 文件第一行读取其
    /// 当前使用的 CDP 远程调试端口号。
    /// </summary>
    /// <param name="profile">监控目标档案，提供用户数据目录名与显示名。</param>
    /// <returns>CDP 调试端口号。</returns>
    /// <exception cref="InvalidOperationException">无法获取 %APPDATA% 路径或文件内容无法解析。</exception>
    /// <exception cref="FileNotFoundException">DevToolsActivePort 文件不存在。</exception>
    private int DiscoverCdpPort(AppTargetProfile profile)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appDataPath))
        {
            throw new InvalidOperationException(
                "无法获取当前用户 %APPDATA% 目录路径，环境变量 APPDATA 未设置。");
        }

        var portFilePath = Path.Combine(appDataPath, profile.UserDataDirectoryName, DevToolsPortFileName);
        if (!File.Exists(portFilePath))
        {
            throw new FileNotFoundException(
                $"未找到 {profile.Name} 的 CDP 调试端口文件：{portFilePath}。" +
                "请确认应用已启动并以 --remote-debugging-port 开启远程调试。",
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
                $"读取 {profile.Name} 的 CDP 调试端口文件失败：{portFilePath}", ex);
        }

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            throw new InvalidOperationException(
                $"{profile.Name} 的 CDP 调试端口文件内容为空：{portFilePath}");
        }

        if (!int.TryParse(firstLine.Trim(), out var port) || port <= 0 || port > 65535)
        {
            throw new InvalidOperationException(
                $"{profile.Name} 的 CDP 调试端口文件首行内容无法解析为有效端口号：" +
                $"\"{firstLine}\"（文件路径：{portFilePath}）");
        }

        return port;
    }

    /// <summary>
    /// 在目标页面 DOM 中搜索 targetText 文本节点（忽略大小写），找到后提取其 DOM 路径、
    /// 标签、Class、可见性、尺寸及外层容器 HTML 等诊断信息，并尝试点击选中。
    /// 点击前先从审批卡片祖先容器提取请求正文（供审批追溯）。
    /// 通过原始 CDP Runtime.evaluate 执行（兼容启用了 Trusted Types CSP 的页面），
    /// 使用 TreeWalker 遍历所有文本节点，并递归进入 shadowRoot 覆盖 Web 组件场景。
    /// </summary>
    /// <param name="session">目标会话（须已连接，持有 CDP 会话）。</param>
    /// <param name="targetText">待匹配的交互文本（不区分大小写）。</param>
    /// <returns>(found: 是否找到文本, clicked: 是否成功点击交互行, diagInfo: 详细诊断文本,
    /// requestContext: 清洗后的请求正文，提取失败为空字符串)。</returns>
    private async Task<(bool found, bool clicked, string diagInfo, string requestContext)> FindAndClickAllowThisTimeAsync(
        TargetSession session, string targetText)
    {
        var jsonResult = await EvaluateStringViaCdpAsync(
            session, BuildCallExpression(FindAndClickBodyJs, targetText));

        if (string.IsNullOrWhiteSpace(jsonResult))
        {
            return (false, false, "页面侧脚本未返回结果（页面可能正在导航/刷新）", string.Empty);
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonResult);
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
            var rawContext = root.TryGetProperty("contextText", out var ctxElem)
                ? ctxElem.GetString() ?? ""
                : "";
            var requestContext = CleanRequestContext(rawContext, matchedText, targetText);

            var diag = $"【DOM定位分析】\n" +
                       $"  • 命中文字：\"{matchedText}\"\n" +
                       $"  • 请求正文：{(string.IsNullOrWhiteSpace(requestContext) ? "（未能提取）" : requestContext)}\n" +
                       $"  • 所在元素：<{tag}> (class='{className}', id='{id}')\n" +
                       $"  • 可见状态：{(isVisible ? $"可见 (尺寸: {rectW}x{rectH})" : "不可见/隐藏")}\n" +
                       $"  • DOM路径：{domPath}\n" +
                       $"  • 触发点击容器：<{clickableTag} class='{clickableClass}'> (点击状态: {(clicked ? "成功" : "失败")})\n" +
                       $"  • 容器HTML片段：{outerHtml}";

            return (found, clicked, diag, requestContext);
        }
        catch
        {
            var found = jsonResult.Contains("\"found\":true");
            var clicked = jsonResult.Contains("\"clicked\":true");
            return (found, clicked, $"原始分析结果: {jsonResult}", string.Empty);
        }
    }

    // 已知的审批按钮文案（小写，长词优先）。清洗请求正文时按行剔除这些纯按钮行，
    // 避免 "Yes, allow this time / Always allow / No" 等操作按钮混入审计内容。
    private static readonly string[] KnownButtonPhrases =
    {
        "yes, allow this time",
        "allow for this session",
        "always allow",
        "allow always",
        "allow",
        "deny",
        "cancel",
        "yes",
        "no",
    };

    /// <summary>
    /// 清洗 JS 侧提取的审批卡片原始文本：按行剔除空行与纯按钮文案行
    /// （含命中文本与配置的目标文本本身），剩余行以 " | " 连接为单行预览并截断。
    /// </summary>
    /// <param name="rawContext">JS 提取的审批卡片原始文本（含换行）。</param>
    /// <param name="matchedText">实际命中的交互行文本。</param>
    /// <param name="targetText">配置的目标文本。</param>
    /// <returns>清洗后的请求正文；无有效内容时返回空字符串。</returns>
    private static string CleanRequestContext(string rawContext, string matchedText, string targetText)
    {
        if (string.IsNullOrWhiteSpace(rawContext))
        {
            return string.Empty;
        }

        var kept = new List<string>();
        foreach (var rawLine in rawContext.Split('\n'))
        {
            var line = System.Text.RegularExpressions.Regex.Replace(rawLine, @"\s+", " ").Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // 剔除"只剩按钮文案"的行：把全部已知按钮短语及命中/目标文本
            // 从行中移除后若不再有实质内容，则视为按钮行。
            var residue = line.ToLowerInvariant();
            foreach (var phrase in KnownButtonPhrases)
            {
                residue = residue.Replace(phrase, " ");
            }
            if (!string.IsNullOrWhiteSpace(matchedText))
            {
                residue = residue.Replace(matchedText.ToLowerInvariant(), " ");
            }
            if (!string.IsNullOrWhiteSpace(targetText))
            {
                residue = residue.Replace(targetText.ToLowerInvariant(), " ");
            }
            if (residue.Replace(" ", string.Empty).Length == 0)
            {
                continue;
            }

            kept.Add(line);
        }

        var joined = string.Join(" | ", kept).Trim();
        const int maxLength = 300;
        return joined.Length > maxLength ? joined.Substring(0, maxLength) : joined;
    }

    /// <summary>
    /// 单个监控目标的会话状态。承载该目标的档案、Playwright 资源引用、
    /// 连接状态、CDP 端口与确认计数。
    /// 所有字段的读写均在 ElectronAutomationService._resourceLock 内进行；
    /// IPage/ICDPSession 引用取出后可跨锁使用（Playwright 代理对象线程安全，
    /// 连接被并发关闭时进行中的调用会抛 PlaywrightException，由监控循环捕获后重连）。
    /// </summary>
    private sealed class TargetSession
    {
        /// <summary>监控目标档案（名称、用户数据目录名等）。</summary>
        public required AppTargetProfile Profile { get; init; }

        /// <summary>Playwright 实例，断开/停止时释放。</summary>
        public IPlaywright? Playwright;

        /// <summary>通过 CDP 连接得到的浏览器实例，断开/停止时关闭。</summary>
        public IBrowser? Browser;

        /// <summary>当前监控的页面引用，重连后更新。</summary>
        public IPage? Page;

        /// <summary>直达当前页面的原始 CDP 会话，用于执行 DOM 检测/点击脚本。
        /// 使用原始 Runtime.evaluate 以兼容启用 Trusted Types CSP 的页面。</summary>
        public ICDPSession? Cdp;

        /// <summary>当前连接的 CDP 调试端口号；未连接时为 0。</summary>
        public int CdpPort;

        /// <summary>是否已通过 CDP 连接到目标应用。</summary>
        public bool IsConnected;

        /// <summary>本次运行该目标已确认 'Yes, allow this time' 的次数。</summary>
        public int ConfirmationCount;
    }
}
