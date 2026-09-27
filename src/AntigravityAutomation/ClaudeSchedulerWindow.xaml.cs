using System.Windows;
using AntigravityAutomation.ViewModels;

namespace AntigravityAutomation;

/// <summary>
/// Claude 终端定时指令调度器子窗口的代码后台。
/// </summary>
public partial class ClaudeSchedulerWindow : Window
{
    private readonly ClaudeSchedulerViewModel _viewModel;

    public ClaudeSchedulerWindow(ClaudeSchedulerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.RequestCloseWindow = () =>
        {
            Dispatcher.Invoke(Close);
        };
    }

    private void Preset5Hours_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetPresetCountdown(5, 0, 0);
    }

    private void Preset1Hour_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetPresetCountdown(1, 0, 0);
    }

    private void Preset30Mins_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetPresetCountdown(0, 30, 0);
    }

    private void Preset10Secs_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetPresetCountdown(0, 0, 10);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
