// 文件用途：主窗口代码后台。
// 职责：
//   1. 接收 MainViewModel 并设置 DataContext（由 App.xaml.cs 通过 DI 注入）。
//   2. 实现日志 ListBox 自动滚动到底：订阅 LogEntries.CollectionChanged，新增项时滚动到底。
//   3. "浏览"按钮的 OpenFileDialog 处理：选择 .exe 文件并回填应用路径。
//   4. "清空日志"按钮点击事件：清空 ViewModel.LogEntries 集合，便于用户清理历史日志。
// 说明：保留无参构造函数供 XAML 设计器与单元测试使用；运行时由 DI 容器调用带参构造函数。
//      已移除探索结果 DataGrid 双击事件（探索功能已从精简界面中移除）。

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AntigravityAutomation.Services;
using AntigravityAutomation.ViewModels;
using Microsoft.Win32;

namespace AntigravityAutomation;

/// <summary>
/// 应用程序主窗口。对应 MainWindow.xaml 的代码后台。
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private readonly ITrayService? _trayService;
    private readonly ClaudeSchedulerViewModel? _claudeSchedulerViewModel;
    private readonly IClaudeSchedulerService? _claudeSchedulerService;
    private ClaudeSchedulerWindow? _claudeWindow;

    /// <summary>
    /// 无参构造函数。供 XAML 设计器与单元测试使用；运行时优先使用带 ViewModel 的构造函数。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 兼容旧版单一 ViewModel 的构造函数。
    /// </summary>
    public MainWindow(MainViewModel viewModel) : this()
    {
        InitializeViewModel(viewModel);
    }

    /// <summary>
    /// 完整依赖注入构造函数。
    /// </summary>
    public MainWindow(
        MainViewModel viewModel,
        ITrayService trayService,
        ClaudeSchedulerViewModel claudeSchedulerViewModel,
        IClaudeSchedulerService claudeSchedulerService) : this()
    {
        _trayService = trayService;
        _claudeSchedulerViewModel = claudeSchedulerViewModel;
        _claudeSchedulerService = claudeSchedulerService;
        InitializeViewModel(viewModel);

        Loaded += (_, _) =>
        {
            _trayService?.Initialize(this, OpenClaudeSchedulerWindow);
        };
    }

    /// <summary>
    /// 初始化视图模型绑定。设置 DataContext 并订阅日志集合变化以实现自动滚动。
    /// </summary>
    /// <param name="viewModel">主视图模型实例。</param>
    private void InitializeViewModel(MainViewModel viewModel)
    {
        _viewModel = viewModel
            ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        // 订阅日志集合变化：新增日志时滚动 ListBox 到底，确保最新日志可见。
        _viewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;
    }

    /// <summary>
    /// 日志集合变化回调。当新增日志条目时，将日志 ListBox 滚动到底部。
    /// </summary>
    /// <param name="sender">事件发送者（LogEntries 集合）。</param>
    /// <param name="e">集合变化事件参数。</param>
    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        // 仅当有新增项时滚动到底；使用 Dispatcher 异步执行避免在数据绑定过程中产生冲突。
        if (e.NewItems is null || e.NewItems.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(ScrollLogListBoxToEnd));
    }

    /// <summary>
    /// 将日志 ListBox 滚动到底部，使最新日志条目可见。
    /// </summary>
    private void ScrollLogListBoxToEnd()
    {
        if (LogListBox.Items.Count == 0)
        {
            return;
        }

        var lastItem = LogListBox.Items[LogListBox.Items.Count - 1];
        LogListBox.ScrollIntoView(lastItem);
    }

    /// <summary>
    /// "浏览"按钮点击事件。弹出 OpenFileDialog 选择 Antigravity 可执行文件，
    /// 选择成功后回填到 ViewModel 的 AppExecutablePath 属性。
    /// </summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="e">路由事件参数。</param>
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Antigravity IDE 可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            InitialDirectory = System.IO.Path.GetDirectoryName(_viewModel?.AppExecutablePath)
                ?? string.Empty,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (_viewModel is null)
        {
            return;
        }

        _viewModel.AppExecutablePath = dialog.FileName;
    }

    /// <summary>
    /// "清空日志"按钮点击事件。清空 ViewModel.LogEntries 集合中所有日志条目，
    /// 便于用户在长时间运行后清理历史日志、聚焦最新输出。
    /// </summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="e">路由事件参数。</param>
    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.LogEntries.Clear();
    }

    /// <summary>
    /// 打开 Claude 终端定时指令调度器子窗口。
    /// </summary>
    public void OpenClaudeSchedulerWindow()
    {
        if (_claudeSchedulerViewModel == null)
        {
            return;
        }

        if (_claudeWindow == null || !_claudeWindow.IsLoaded)
        {
            _claudeSchedulerViewModel.RefreshTerminals();
            _claudeWindow = new ClaudeSchedulerWindow(_claudeSchedulerViewModel)
            {
                Owner = this
            };
            _claudeWindow.Closed += (_, _) => _claudeWindow = null;
            _claudeWindow.Show();
        }
        else
        {
            _claudeWindow.WindowState = WindowState.Normal;
            _claudeWindow.Activate();
        }
    }

    private void OpenClaudeSchedulerButton_Click(object sender, RoutedEventArgs e)
    {
        OpenClaudeSchedulerWindow();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_claudeSchedulerService != null && _claudeSchedulerService.IsRunning)
        {
            var result = MessageBox.Show(
                "Claude 终端定时任务正在运行中。\n\n• 点击【是 (Yes)】：最小化到右下角系统托盘继续守护运行\n• 点击【否 (No)】：直接退出程序并终止定时任务",
                "定时任务运行中",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                _trayService?.MinimizeToTray();
                return;
            }
            else if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _claudeSchedulerService != null && _claudeSchedulerService.IsRunning)
        {
            _trayService?.MinimizeToTray();
        }
    }
}
