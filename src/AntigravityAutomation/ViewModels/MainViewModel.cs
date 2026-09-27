// 文件用途：主视图模型 MainViewModel。
// 职责：
//   1. 承载界面双向绑定的全部配置属性（应用路径、目标按钮定位、超时等）与状态属性
//      （当前状态、运行标志、探索结果列表、实时日志列表）。
//   2. 暴露四个 ReactiveCommand：探索界面按钮、执行自动化、停止、保存配置，
//      并通过 IsRunning 标志联动命令可用性（运行时禁用探索与执行，启用停止）。
//   3. 订阅 ILoggingService.LogStream，将日志条目通过 Dispatcher 转发到 UI 线程的 LogEntries。
//   4. 订阅 IElectronAutomationService.StatusChanged，将状态描述同步到 CurrentStatus。
// 说明：项目未引用 ReactiveUI.Fody，因此响应式属性采用手写 RaiseAndSetIfChanged 模式，
//       命令采用 ReactiveCommand.CreateFromTask / Create 结合 WhenAnyValue 实现 CanExecute。

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AntigravityAutomation.Models;
using AntigravityAutomation.Services;
using ReactiveUI;

namespace AntigravityAutomation.ViewModels;

/// <summary>
/// 主视图模型。聚合后台服务，向界面提供配置绑定、命令与实时日志/状态/审批历史流。
/// </summary>
public sealed class MainViewModel : ReactiveObject
{
    // 默认应用路径：与 AutomationConfig 保持一致的本机安装位置。
    private const string DefaultAppPath =
        @"C:\Users\peng\AppData\Local\Programs\antigravity\Antigravity.exe";

    // 配置文件名：与程序输出目录中的 appsettings.json 对应。
    private const string ConfigFileName = "appsettings.json";

    // 界面日志列表最大保留条数。超过此阈值时移除最旧的条目，避免长时间运行后
    // ObservableCollection 积累过多日志导致 WPF 界面渲染卡顿。
    private const int MaxLogEntries = 500;

    // 审批历史列表最大保留条数（界面展示用；审计文件中的完整记录不受影响）。
    private const int MaxApprovalHistoryItems = 200;

    // 启动时从审计文件回填的审批历史条数。
    private const int ApprovalHistoryLoadCount = 50;

    // 后台服务引用。
    private readonly ILoggingService _loggingService;
    private readonly IElectronAutomationService _electronAutomationService;
    private readonly IElementExplorerService _elementExplorerService;
    private readonly IAppRelaunchService _appRelaunchService;
    private readonly IApprovalAuditService _approvalAuditService;

    // 取消令牌源：供 StopCommand 取消正在执行的探索或自动化流程。
    private CancellationTokenSource _cancellationTokenSource = new();

    // 日志流订阅句柄，释放时取消订阅避免悬挂。
    private readonly IDisposable _logSubscription;

    // ───────────────────────── 配置属性 ─────────────────────────

    private string _appExecutablePath = DefaultAppPath;
    /// <summary>
    /// Antigravity IDE 可执行文件绝对路径。界面"浏览"按钮可修改。
    /// </summary>
    public string AppExecutablePath
    {
        get => _appExecutablePath;
        set => this.RaiseAndSetIfChanged(ref _appExecutablePath, value);
    }

    private string _yesAllowButtonText = "Yes, allow this time";
    /// <summary>
    /// 目标交互行文本（不区分大小写）。当界面出现包含此文本时自动发送 Enter 确认。
    /// </summary>
    public string YesAllowButtonText
    {
        get => _yesAllowButtonText;
        set => this.RaiseAndSetIfChanged(ref _yesAllowButtonText, value);
    }

    // ───────────────────────── 状态属性 ─────────────────────────

    private string _currentStatus = "就绪";
    /// <summary>
    /// 当前自动化状态描述，绑定到界面状态栏。
    /// </summary>
    public string CurrentStatus
    {
        get => _currentStatus;
        set => this.RaiseAndSetIfChanged(ref _currentStatus, value);
    }

    private bool _isRunning;
    /// <summary>
    /// 是否正在执行自动化或探索流程。控制命令可用性与停止按钮。
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => this.RaiseAndSetIfChanged(ref _isRunning, value);
    }

    private int _confirmationCount;
    /// <summary>
    /// 本次运行已确认 'Yes, allow this time' 交互项的次数。
    /// 由 ElectronAutomationService.StatisticsChanged 事件推送更新，绑定到控制面板统计区。
    /// </summary>
    public int ConfirmationCount
    {
        get => _confirmationCount;
        set => this.RaiseAndSetIfChanged(ref _confirmationCount, value);
    }

    private int _cdpPort;
    /// <summary>
    /// 当前连接的 Antigravity CDP 调试端口号；未连接时为 0。
    /// 由 ElectronAutomationService.StatisticsChanged 事件推送更新。
    /// </summary>
    public int CdpPort
    {
        get => _cdpPort;
        set => this.RaiseAndSetIfChanged(ref _cdpPort, value);
    }

    private bool _isConnected;
    /// <summary>
    /// 是否已通过 CDP 连接到 Antigravity IDE。
    /// 由 ElectronAutomationService.StatisticsChanged 事件推送更新，绑定到连接状态指示。
    /// </summary>
    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    private string _targetSummary = string.Empty;
    /// <summary>
    /// 各监控目标的逐条状态摘要（如 "Antigravity:9226✓ · Antigravity IDE:等待中"）。
    /// 由 ElectronAutomationService.StatisticsChanged 事件推送更新，绑定到控制面板统计区。
    /// </summary>
    public string TargetSummary
    {
        get => _targetSummary;
        set => this.RaiseAndSetIfChanged(ref _targetSummary, value);
    }

    /// <summary>
    /// 监控目标勾选列表（Antigravity / Antigravity IDE 等），绑定到高级设置的勾选区域。
    /// 启动监控与保存配置时据此构建 AutomationConfig.Targets。
    /// </summary>
    public ObservableCollection<MonitorTargetOption> TargetOptions { get; } = new();

    /// <summary>
    /// 探索发现的界面元素列表，绑定到探索结果 DataGrid。
    /// </summary>
    public ObservableCollection<DiscoveredElement> DiscoveredElements { get; } = new();

    /// <summary>
    /// 实时日志条目列表，绑定到日志 ListBox。
    /// </summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    /// <summary>
    /// 审批历史列表（最新在前），绑定到审批历史 ListBox。
    /// 启动时从审计文件回填最近记录，运行中由 ApprovalRecorded 事件实时追加。
    /// </summary>
    public ObservableCollection<ApprovalHistoryItem> ApprovalHistory { get; } = new();

    // ───────────────────────── 命令 ─────────────────────────

    /// <summary>
    /// 探索界面按钮命令。启动应用并枚举所有可交互按钮填入 DiscoveredElements。
    /// </summary>
    public ReactiveCommand<Unit, Unit> ExploreCommand { get; }

    /// <summary>
    /// 执行自动化命令。按界面配置构建 AutomationConfig 并调用自动化服务运行全流程。
    /// </summary>
    public ReactiveCommand<Unit, Unit> RunAutomationCommand { get; }

    /// <summary>
    /// 停止命令。取消当前流程并调用两个服务的 StopAsync。
    /// </summary>
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    /// <summary>
    /// 保存配置命令。将当前界面配置序列化到 appsettings.json。
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveConfigCommand { get; }

    /// <summary>
    /// 以调试模式重启监控目标命令。参数为目标勾选项（MonitorTargetOption）。
    /// 共享同一命令实例，逐选项通过 CommandParameter 传入。
    /// </summary>
    public ReactiveCommand<MonitorTargetOption, Unit> RestartTargetCommand { get; }

    /// <summary>
    /// 构造函数。注入后台服务，初始化响应式属性与命令，订阅日志/状态/统计/审批事件流。
    /// </summary>
    /// <param name="loggingService">日志服务，提供业务写入与界面订阅通道。</param>
    /// <param name="electronAutomationService">Electron 自动化服务，执行完整自动化流程。</param>
    /// <param name="elementExplorerService">元素探索服务，枚举界面按钮供用户选择。</param>
    /// <param name="appRelaunchService">应用调试重启服务，负责"调试重启"按钮的进程机制。</param>
    /// <param name="approvalAuditService">审批审计服务，启动时回填审批历史。</param>
    public MainViewModel(
        ILoggingService loggingService,
        IElectronAutomationService electronAutomationService,
        IElementExplorerService elementExplorerService,
        IAppRelaunchService appRelaunchService,
        IApprovalAuditService approvalAuditService)
    {
        _loggingService = loggingService
            ?? throw new ArgumentNullException(nameof(loggingService));
        _electronAutomationService = electronAutomationService
            ?? throw new ArgumentNullException(nameof(electronAutomationService));
        _elementExplorerService = elementExplorerService
            ?? throw new ArgumentNullException(nameof(elementExplorerService));
        _appRelaunchService = appRelaunchService
            ?? throw new ArgumentNullException(nameof(appRelaunchService));
        _approvalAuditService = approvalAuditService
            ?? throw new ArgumentNullException(nameof(approvalAuditService));

        // 命令可用性：未运行时才允许探索与执行；运行时才允许停止；保存配置始终可用。
        var canRunWhenIdle = this.WhenAnyValue(x => x.IsRunning).Select(running => !running);
        var canStopWhenRunning = this.WhenAnyValue(x => x.IsRunning).Select(running => running);

        ExploreCommand = ReactiveCommand.CreateFromTask(ExploreButtonsAsync, canRunWhenIdle);
        RunAutomationCommand = ReactiveCommand.CreateFromTask(RunAutomationAsync, canRunWhenIdle);
        StopCommand = ReactiveCommand.CreateFromTask(StopAsync, canStopWhenRunning);
        SaveConfigCommand = ReactiveCommand.Create(SaveConfig);
        // 调试重启与监控互不冲突（监控循环会自动重连新实例），允许任意时刻执行。
        RestartTargetCommand = ReactiveCommand.CreateFromTask<MonitorTargetOption>(RestartTargetWithDebugAsync);

        // 订阅日志流：通过 Dispatcher 转发到 UI 线程的 LogEntries 集合。
        _logSubscription = _loggingService.LogStream.Subscribe(OnLogEntryReceived);

        // 订阅自动化服务状态变化：同步到界面状态栏。
        _electronAutomationService.StatusChanged += OnAutomationStatusChanged;

        // 订阅自动化服务统计变化：同步确认次数/CDP端口/连接状态到界面绑定属性。
        _electronAutomationService.StatisticsChanged += OnAutomationStatisticsChanged;

        // 订阅审批追溯事件：每次自动确认后实时追加到审批历史列表。
        _electronAutomationService.ApprovalRecorded += OnApprovalRecorded;

        // 启动时回填审批历史：读取审计文件最近若干条（时间升序），倒置为最新在前展示。
        // 构造函数在 UI 线程执行，无需 Dispatcher 封送。
        foreach (var record in _approvalAuditService.ReadRecent(ApprovalHistoryLoadCount).Reverse())
        {
            ApprovalHistory.Add(ApprovalHistoryItem.FromRecord(record));
        }

        // 尝试从 appsettings.json 加载持久化配置
        LoadConfig();

        // 配置文件缺失或未包含监控目标时，回退为内置默认双目标（Antigravity 与 Antigravity IDE）。
        if (TargetOptions.Count == 0)
        {
            PopulateTargetOptions(AppTargetProfile.CreateDefaults());
        }
    }

    // ───────────────────────── 命令实现 ─────────────────────────

    /// <summary>
    /// 探索界面按钮。启动应用并枚举所有可交互按钮，结果填入 DiscoveredElements。
    /// </summary>
    private async Task ExploreButtonsAsync()
    {
        IsRunning = true;
        CurrentStatus = "正在探索界面按钮";
        DiscoveredElements.Clear();
        RecreateCancellationTokenSource();

        var appPath = AppExecutablePath;
        _loggingService.LogInfo($"开始探索界面按钮，目标应用路径：{appPath}", "探索界面");

        try
        {
            // 注意：不使用 ConfigureAwait(false)，WPF 桌面应用需要 await 后回到 UI 线程，
            // 以便安全地设置 ReactiveUI 响应属性（IsRunning/CurrentStatus 等）。
            var discovered = await _elementExplorerService
                .ExploreButtonsAsync(appPath, _cancellationTokenSource.Token);

            // 通过 Dispatcher 将结果逐条加入 UI 集合（探索可能在后台线程完成）。
            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var element in discovered)
                {
                    DiscoveredElements.Add(element);
                }
            });

            _loggingService.LogInfo(
                $"探索完成，共发现 {discovered.Count} 个可交互按钮元素", "探索界面");
            CurrentStatus = $"探索完成，发现 {discovered.Count} 个元素";
        }
        catch (OperationCanceledException)
        {
            _loggingService.LogWarning("探索界面按钮操作已被用户取消", "探索界面");
            CurrentStatus = "探索已取消";
        }
        catch (Exception ex)
        {
            _loggingService.LogError("探索界面按钮时发生错误", "探索界面", ex);
            CurrentStatus = "探索失败";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// 执行自动化流程。从界面属性构建 AutomationConfig 并调用自动化服务运行。
    /// </summary>
    private async Task RunAutomationAsync()
    {
        IsRunning = true;
        CurrentStatus = "正在连接 Antigravity CDP...";
        RecreateCancellationTokenSource();

        var config = BuildConfigFromUi();
        var targetText = string.IsNullOrWhiteSpace(config.YesAllowButtonText)
            ? "Yes, allow this time"
            : config.YesAllowButtonText.Trim();

        _loggingService.LogInfo(
            $"开始持续监控包含 '{targetText}' 的交互项，将自动按 Enter 确认...",
            "执行自动化");

        try
        {
            await _electronAutomationService
                .RunAutomationAsync(config, _cancellationTokenSource.Token);

            _loggingService.LogInfo("监控循环已停止", "执行自动化");
            CurrentStatus = "监控已停止";
        }
        catch (OperationCanceledException)
        {
            _loggingService.LogWarning("监控已停止", "执行自动化");
            CurrentStatus = "监控已停止";
        }
        catch (Exception ex)
        {
            _loggingService.LogError("监控发生错误", "执行自动化", ex);
            CurrentStatus = "监控失败";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// 停止当前正在执行的探索或自动化流程。
    /// </summary>
    private async Task StopAsync()
    {
        _loggingService.LogInfo("用户请求停止当前操作，正在取消并释放资源", "停止操作");
        CurrentStatus = "正在停止";

        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 令牌已释放，忽略。
        }

        try
        {
            await _electronAutomationService.StopAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogError("停止 Electron 自动化服务时发生错误", "停止操作", ex);
        }

        try
        {
            await _elementExplorerService.StopAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogError("停止元素探索服务时发生错误", "停止操作", ex);
        }

        IsRunning = false;
        CurrentStatus = "已停止";
        _loggingService.LogInfo("停止操作完成，资源已释放", "停止操作");
    }

    /// <summary>
    /// 以调试模式重启指定监控目标。
    /// 流程：检测运行状态与调试端口存活 → 已在调试模式则提示无需重启 →
    /// 运行中未开调试则经用户确认后优雅关闭（超时再询问是否强制结束）→
    /// 以 --remote-debugging-port 重新启动并等待端口就绪。
    /// 监控循环对端口文件采用 5 秒轮询，新实例就绪后会自动接入，无需手动重开监控。
    /// </summary>
    /// <param name="option">目标勾选项（携带目标档案）。</param>
    private async Task RestartTargetWithDebugAsync(MonitorTargetOption option)
    {
        if (option is null)
        {
            return;
        }

        const string step = "调试重启";
        var profile = option.Profile;

        try
        {
            _loggingService.LogInfo($"开始处理 {profile.Name} 的调试模式重启请求", step);
            CurrentStatus = $"[{profile.Name}] 正在检测运行状态...";

            var processes = _appRelaunchService.FindRunningProcesses(profile);
            var livePort = await _appRelaunchService.GetLiveDebugPortAsync(profile, CancellationToken.None);

            // 情形 1：已在调试模式运行，无需任何操作。
            if (processes.Length > 0 && livePort is not null)
            {
                _loggingService.LogInfo($"{profile.Name} 已在调试模式运行（端口 {livePort}），无需重启", step);
                CurrentStatus = $"[{profile.Name}] 已在调试模式（端口 {livePort}）";
                MessageBox.Show(
                    $"{profile.Name} 已在调试模式运行（端口 {livePort}），无需重启。",
                    "调试重启",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 情形 2：运行中但未开调试端口。VS Code 系应用为单实例架构，
            // 直接带参启动只会复用现有进程，必须先退出再重启。
            if (processes.Length > 0)
            {
                var confirm = MessageBox.Show(
                    $"将关闭并重启 {profile.Name}，以 --remote-debugging-port={profile.DebugPort} 开启调试端口。\n\n" +
                    "未保存的更改可能丢失，是否继续？",
                    "以调试模式重启",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    _loggingService.LogInfo($"用户取消了 {profile.Name} 的调试模式重启", step);
                    CurrentStatus = "已取消重启";
                    return;
                }

                CurrentStatus = $"[{profile.Name}] 正在关闭当前实例...";
                _loggingService.LogInfo($"正在优雅关闭 {profile.Name}（共 {processes.Length} 个进程）...", step);

                var closed = await _appRelaunchService.CloseInstancesAsync(processes, forceKill: false, CancellationToken.None);
                if (!closed)
                {
                    var force = MessageBox.Show(
                        $"{profile.Name} 未能在 15 秒内正常退出（可能存在未保存内容）。\n\n是否强制结束进程？",
                        "以调试模式重启",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (force != MessageBoxResult.Yes)
                    {
                        _loggingService.LogWarning($"{profile.Name} 优雅关闭超时，用户放弃强制结束，重启取消", step);
                        CurrentStatus = "已取消重启";
                        return;
                    }

                    closed = await _appRelaunchService.CloseInstancesAsync(processes, forceKill: true, CancellationToken.None);
                    if (!closed)
                    {
                        _loggingService.LogError($"{profile.Name} 强制结束后仍有进程残留，重启中止", step);
                        MessageBox.Show(
                            $"{profile.Name} 无法完全退出，请手动关闭后重试。",
                            "以调试模式重启",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        CurrentStatus = "重启失败";
                        return;
                    }
                }

                _loggingService.LogInfo($"{profile.Name} 已完全退出，准备以调试模式重启", step);
            }
            else
            {
                // 情形 3：未运行，直接以调试模式启动。
                _loggingService.LogInfo($"{profile.Name} 当前未运行，直接以调试模式启动", step);
            }

            CurrentStatus = $"[{profile.Name}] 正在以调试模式启动...";
            var port = await _appRelaunchService.LaunchWithDebugPortAsync(profile, CancellationToken.None);

            if (port is not null)
            {
                _loggingService.LogInfo(
                    IsRunning
                        ? $"{profile.Name} 调试端口 {port} 就绪，监控将在数秒内自动接入"
                        : $"{profile.Name} 调试端口 {port} 就绪，点击\"开始监控\"后将自动接入",
                    step);
                CurrentStatus = $"[{profile.Name}] 调试端口 {port} 就绪";
            }
            else
            {
                // 服务内部已记 WARN 日志。
                CurrentStatus = $"[{profile.Name}] 已启动，端口探测超时";
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"{profile.Name} 调试模式重启失败", step, ex);
            CurrentStatus = "重启失败";
        }
    }

    /// <summary>
    /// 将当前界面配置序列化保存到 appsettings.json。
    /// </summary>
    private void SaveConfig()
    {
        try
        {
            var config = BuildConfigFromUi();
            var configPayload = new
            {
                AutomationConfig = new
                {
                    AppExecutablePath = config.AppExecutablePath,
                    YesAllowButtonText = config.YesAllowButtonText,
                    // 监控目标列表：逐项序列化显示名、路径、用户数据目录名与启用开关。
                    Targets = config.Targets.Select(t => new
                    {
                        Name = t.Name,
                        ExePath = t.ExePath,
                        UserDataDirectoryName = t.UserDataDirectoryName,
                        Enabled = t.Enabled
                    }).ToArray()
                },
                // 保留原 Serilog 配置段，避免覆盖日志设置。
                Serilog = new
                {
                    Using = new[] { "Serilog.Sinks.File", "Serilog.Sinks.Async" },
                    MinimumLevel = "Debug",
                    WriteTo = new[]
                    {
                        new
                        {
                            Name = "Async",
                            Args = new
                            {
                                configure = new[]
                                {
                                    new
                                    {
                                        Name = "File",
                                        Args = new
                                        {
                                            path = "logs/antigravity-.log",
                                            rollingInterval = "Day",
                                            outputTemplate =
                                                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] " +
                                                "[{Level:u3}] {Message:lj}{NewLine}{Exception}"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var json = JsonSerializer.Serialize(configPayload, jsonOptions);
            File.WriteAllText(ConfigFileName, json);

            _loggingService.LogInfo($"配置已保存到文件：{ConfigFileName}", "保存配置");
            CurrentStatus = "配置已保存";
        }
        catch (Exception ex)
        {
            _loggingService.LogError("保存配置文件失败", "保存配置", ex);
            CurrentStatus = "保存配置失败";
        }
    }

    // ───────────────────────── 辅助方法 ─────────────────────────

    /// <summary>
    /// 从 appsettings.json 加载持久化配置并回填到 ViewModel 属性。
    /// </summary>
    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFileName))
            {
                return;
            }

            var json = File.ReadAllText(ConfigFileName);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("AutomationConfig", out var autoConfig))
            {
                if (autoConfig.TryGetProperty("AppExecutablePath", out var pathElem) &&
                    pathElem.GetString() is { } path && !string.IsNullOrWhiteSpace(path))
                {
                    _appExecutablePath = path;
                }
                if (autoConfig.TryGetProperty("YesAllowButtonText", out var textElem) &&
                    textElem.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    _yesAllowButtonText = text;
                }

                // 解析监控目标列表。任一目标缺少名称或用户数据目录名时跳过该项；
                // 全部无效时保持 TargetOptions 为空，由构造函数回退默认双目标。
                if (autoConfig.TryGetProperty("Targets", out var targetsElem) &&
                    targetsElem.ValueKind == JsonValueKind.Array)
                {
                    var profiles = new List<AppTargetProfile>();
                    foreach (var targetElem in targetsElem.EnumerateArray())
                    {
                        var name = targetElem.TryGetProperty("Name", out var nameElem)
                            ? nameElem.GetString() ?? string.Empty
                            : string.Empty;
                        var dirName = targetElem.TryGetProperty("UserDataDirectoryName", out var dirElem)
                            ? dirElem.GetString() ?? string.Empty
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dirName))
                        {
                            continue;
                        }

                        profiles.Add(new AppTargetProfile
                        {
                            Name = name,
                            UserDataDirectoryName = dirName,
                            ExePath = targetElem.TryGetProperty("ExePath", out var exeElem)
                                ? exeElem.GetString() ?? string.Empty
                                : string.Empty,
                            Enabled = !targetElem.TryGetProperty("Enabled", out var enabledElem)
                                || enabledElem.GetBoolean()
                        });
                    }

                    if (profiles.Count > 0)
                    {
                        PopulateTargetOptions(profiles);
                    }
                }
            }
        }
        catch
        {
            // 配置文件读取失败时保留默认值
        }
    }

    /// <summary>
    /// 用给定目标档案列表重建 TargetOptions 勾选集合，
    /// 并为每个选项挂上共享的调试重启命令。
    /// </summary>
    /// <param name="profiles">监控目标档案列表。</param>
    private void PopulateTargetOptions(IEnumerable<AppTargetProfile> profiles)
    {
        TargetOptions.Clear();
        foreach (var profile in profiles)
        {
            var option = new MonitorTargetOption(profile)
            {
                RestartCommand = RestartTargetCommand
            };
            TargetOptions.Add(option);
        }
    }

    /// <summary>
    /// 从界面属性构建 AutomationConfig 实例。空字符串统一回退为默认提示词。
    /// </summary>
    /// <returns>填充界面当前值的 AutomationConfig 实例。</returns>
    private AutomationConfig BuildConfigFromUi()
    {
        return new AutomationConfig
        {
            AppExecutablePath = AppExecutablePath,
            YesAllowButtonText = string.IsNullOrWhiteSpace(YesAllowButtonText)
                ? "Yes, allow this time"
                : YesAllowButtonText.Trim(),
            // 监控目标：按界面勾选状态回写 Enabled，构建档案副本列表。
            Targets = TargetOptions.Select(option => option.ToProfile()).ToList()
        };
    }

    /// <summary>
    /// 重建 CancellationTokenSource。在每次启动新操作前调用，确保旧令牌已取消时仍可发起新操作。
    /// </summary>
    private void RecreateCancellationTokenSource()
    {
        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
        }

        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    /// <summary>
    /// 日志流订阅回调。通过 Dispatcher 将 LogEntry 加入 UI 集合，确保跨线程安全。
    /// 加入后执行限流：当 LogEntries 超过 <see cref="MaxLogEntries"/> 时从头部移除最旧条目，
    /// 避免长时间运行后界面绑定集合过大导致渲染卡顿。
    /// </summary>
    /// <param name="logEntry">服务推送的日志条目。</param>
    private void OnLogEntryReceived(LogEntry logEntry)
    {
        if (logEntry is null)
        {
            return;
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            LogEntries.Add(logEntry);

            // 限流：超过最大条数时移除最旧的，保持界面流畅。
            while (LogEntries.Count > MaxLogEntries)
            {
                LogEntries.RemoveAt(0);
            }
        });
    }

    /// <summary>
    /// 自动化服务状态变化回调。通过 Dispatcher 同步状态到 UI 线程属性。
    /// </summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="status">新的状态描述。</param>
    private void OnAutomationStatusChanged(object? sender, string status)
    {
        Application.Current?.Dispatcher.Invoke(() => CurrentStatus = status);
    }

    /// <summary>
    /// 自动化服务统计变化回调。通过 Dispatcher 将统计快照同步到 UI 线程的绑定属性，
    /// 供控制面板的"确认次数"、"CDP 端口"、"连接状态"实时显示。
    /// </summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="statistics">统计快照，包含确认次数、CDP 端口、连接状态。</param>
    private void OnAutomationStatisticsChanged(object? sender, AutomationStatistics statistics)
    {
        if (statistics is null)
        {
            return;
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            ConfirmationCount = statistics.ConfirmationCount;
            CdpPort = statistics.CdpPort;
            IsConnected = statistics.IsConnected;
            TargetSummary = statistics.TargetSummary ?? string.Empty;
        });
    }

    /// <summary>
    /// 审批追溯回调。通过 Dispatcher 将新记录插入审批历史列表顶部（最新在前），
    /// 并限制列表长度避免长期运行积累过多。
    /// </summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="record">本次确认行为的审计记录（含请求正文）。</param>
    private void OnApprovalRecorded(object? sender, ApprovalRecord record)
    {
        if (record is null)
        {
            return;
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            ApprovalHistory.Insert(0, ApprovalHistoryItem.FromRecord(record));

            // 限流：超过最大条数时从尾部移除最旧的，保持界面流畅。
            while (ApprovalHistory.Count > MaxApprovalHistoryItems)
            {
                ApprovalHistory.RemoveAt(ApprovalHistory.Count - 1);
            }
        });
    }
}