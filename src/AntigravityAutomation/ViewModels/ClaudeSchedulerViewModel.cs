using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Windows;
using AntigravityAutomation.Models;
using AntigravityAutomation.Services;
using ReactiveUI;

namespace AntigravityAutomation.ViewModels;

/// <summary>
/// Claude 终端定时调度器子窗口 ViewModel。
/// </summary>
public sealed class ClaudeSchedulerViewModel : ReactiveObject
{
    private readonly ITerminalFinderService _terminalFinder;
    private readonly IClaudeSchedulerService _schedulerService;
    private readonly ITrayService _trayService;

    private TerminalProcessInfo? _selectedTerminal;
    private TargetCliType _selectedCliType = TargetCliType.Claude;
    private bool _isCountdownMode = true;
    private bool _isSpecificTimeMode;
    private int _countdownHours = 5;
    private int _countdownMinutes = 0;
    private int _countdownSeconds = 0;
    private DateTime _specificDate = DateTime.Today;
    private int _specificHour = (DateTime.Now.Hour + 5) % 24;
    private int _specificMinute = DateTime.Now.Minute;
    private string _commandText = "继续";
    private bool _autoPressEnter = true;
    private bool _minimizeToTray = true;
    private bool _isRunning;
    private string _remainingTimeString = "05:00:00";
    private string _statusMessage = "就绪，等待设置定时任务";

    public ObservableCollection<TerminalProcessInfo> TerminalProcesses { get; } = new();

    /// <summary>
    /// 当前选中的目标 AI CLI 工具（Claude 或 Kimi Code）。
    /// </summary>
    public TargetCliType SelectedCliType
    {
        get => _selectedCliType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCliType, value);
            this.RaisePropertyChanged(nameof(IsClaudeTarget));
            this.RaisePropertyChanged(nameof(IsKimiTarget));
            RefreshTerminals();
        }
    }

    public bool IsClaudeTarget
    {
        get => _selectedCliType == TargetCliType.Claude;
        set
        {
            if (value && _selectedCliType != TargetCliType.Claude)
            {
                SelectedCliType = TargetCliType.Claude;
            }
        }
    }

    public bool IsKimiTarget
    {
        get => _selectedCliType == TargetCliType.KimiCode;
        set
        {
            if (value && _selectedCliType != TargetCliType.KimiCode)
            {
                SelectedCliType = TargetCliType.KimiCode;
            }
        }
    }

    public TerminalProcessInfo? SelectedTerminal
    {
        get => _selectedTerminal;
        set => this.RaiseAndSetIfChanged(ref _selectedTerminal, value);
    }

    public bool IsCountdownMode
    {
        get => _isCountdownMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCountdownMode, value);
            if (value)
            {
                IsSpecificTimeMode = false;
            }
        }
    }

    public bool IsSpecificTimeMode
    {
        get => _isSpecificTimeMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSpecificTimeMode, value);
            if (value)
            {
                IsCountdownMode = false;
            }
        }
    }

    public int CountdownHours
    {
        get => _countdownHours;
        set => this.RaiseAndSetIfChanged(ref _countdownHours, Math.Max(0, value));
    }

    public int CountdownMinutes
    {
        get => _countdownMinutes;
        set => this.RaiseAndSetIfChanged(ref _countdownMinutes, Math.Clamp(value, 0, 59));
    }

    public int CountdownSeconds
    {
        get => _countdownSeconds;
        set => this.RaiseAndSetIfChanged(ref _countdownSeconds, Math.Clamp(value, 0, 59));
    }

    public DateTime SpecificDate
    {
        get => _specificDate;
        set => this.RaiseAndSetIfChanged(ref _specificDate, value);
    }

    public int SpecificHour
    {
        get => _specificHour;
        set => this.RaiseAndSetIfChanged(ref _specificHour, Math.Clamp(value, 0, 23));
    }

    public int SpecificMinute
    {
        get => _specificMinute;
        set => this.RaiseAndSetIfChanged(ref _specificMinute, Math.Clamp(value, 0, 59));
    }

    public string CommandText
    {
        get => _commandText;
        set => this.RaiseAndSetIfChanged(ref _commandText, value);
    }

    public bool AutoPressEnter
    {
        get => _autoPressEnter;
        set => this.RaiseAndSetIfChanged(ref _autoPressEnter, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => this.RaiseAndSetIfChanged(ref _minimizeToTray, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => this.RaiseAndSetIfChanged(ref _isRunning, value);
    }

    public string RemainingTimeString
    {
        get => _remainingTimeString;
        private set => this.RaiseAndSetIfChanged(ref _remainingTimeString, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshTerminalsCommand { get; }
    public ReactiveCommand<Unit, Unit> FlashTargetCommand { get; }
    public ReactiveCommand<Unit, Unit> StartScheduleCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelScheduleCommand { get; }

    public Action? RequestCloseWindow { get; set; }

    public ClaudeSchedulerViewModel(
        ITerminalFinderService terminalFinder,
        IClaudeSchedulerService schedulerService,
        ITrayService trayService)
    {
        _terminalFinder = terminalFinder ?? throw new ArgumentNullException(nameof(terminalFinder));
        _schedulerService = schedulerService ?? throw new ArgumentNullException(nameof(schedulerService));
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));

        // 订阅调度器状态
        _schedulerService.CountdownTick += (s, remaining) =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RemainingTimeString = $"{remaining:hh\\:mm\\:ss}";
            });
        };

        _schedulerService.StatusChanged += (s, status) =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                StatusMessage = status;
                IsRunning = _schedulerService.IsRunning;
            });
        };

        _schedulerService.ExecutionCompleted += (s, e) =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsRunning = false;
                StatusMessage = e.Success ? "已成功执行" : "执行失败";
            });
        };

        RefreshTerminalsCommand = ReactiveCommand.Create(RefreshTerminals);

        FlashTargetCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedTerminal != null)
            {
                _terminalFinder.FlashWindow(SelectedTerminal.MainWindowHandle);
            }
        });

        StartScheduleCommand = ReactiveCommand.Create(StartSchedule);
        CancelScheduleCommand = ReactiveCommand.Create(CancelSchedule);

        // 初始刷新终端列表
        RefreshTerminals();
    }

    public void RefreshTerminals()
    {
        TerminalProcesses.Clear();
        var list = _terminalFinder.FindTerminalProcesses(SelectedCliType);
        foreach (var item in list)
        {
            TerminalProcesses.Add(item);
        }

        // 默认选中第一个（优先级最高的是当前选择的目标工具）
        SelectedTerminal = TerminalProcesses.FirstOrDefault();
    }

    public void SetPresetCountdown(int hours, int minutes, int seconds = 0)
    {
        IsCountdownMode = true;
        CountdownHours = hours;
        CountdownMinutes = minutes;
        CountdownSeconds = seconds;
        RemainingTimeString = $"{new TimeSpan(hours, minutes, seconds):hh\\:mm\\:ss}";
    }

    private void StartSchedule()
    {
        if (SelectedTerminal == null)
        {
            System.Windows.MessageBox.Show(
                "请先在下拉列表中选择一个目标终端窗口！",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var config = new ClaudeScheduleConfig
        {
            TargetProcessId = SelectedTerminal.ProcessId,
            TargetHwnd = SelectedTerminal.MainWindowHandle,
            TargetDisplayName = SelectedTerminal.DisplayText,
            TargetCli = SelectedCliType,
            TimingMode = IsCountdownMode ? ScheduleTimingMode.Countdown : ScheduleTimingMode.SpecificTime,
            CountdownHours = CountdownHours,
            CountdownMinutes = CountdownMinutes,
            CountdownSeconds = CountdownSeconds,
            SpecificDateTime = new DateTime(
                SpecificDate.Year, SpecificDate.Month, SpecificDate.Day,
                SpecificHour, SpecificMinute, 0),
            CommandText = CommandText,
            AutoPressEnter = AutoPressEnter,
            MinimizeToTray = MinimizeToTray
        };

        _schedulerService.StartSchedule(config);
        IsRunning = true;

        if (MinimizeToTray)
        {
            RequestCloseWindow?.Invoke();
            _trayService.MinimizeToTray();
        }
    }

    private void CancelSchedule()
    {
        _schedulerService.CancelSchedule();
        IsRunning = false;
        StatusMessage = "任务已取消";
    }
}
