using System;
using System.Threading;
using System.Threading.Tasks;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// Claude 终端定时任务调度服务实现。
/// 支持倒计时模式与定点时刻模式，使用精准计时循环更新倒计时状态并执行注入。
/// </summary>
public sealed class ClaudeSchedulerService : IClaudeSchedulerService, IDisposable
{
    private readonly ITerminalInjectionService _injectionService;
    private readonly ILoggingService _loggingService;

    private CancellationTokenSource? _cts;
    private Task? _schedulerTask;
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public ClaudeScheduleConfig? ActiveConfig { get; private set; }
    public TimeSpan RemainingTime { get; private set; }
    public DateTime TargetTime { get; private set; }

    public event EventHandler<TimeSpan>? CountdownTick;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<(bool Success, string Message)>? ExecutionCompleted;

    public ClaudeSchedulerService(
        ITerminalInjectionService injectionService,
        ILoggingService loggingService)
    {
        _injectionService = injectionService ?? throw new ArgumentNullException(nameof(injectionService));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
    }

    public void StartSchedule(ClaudeScheduleConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        CancelSchedule(); // 先取消已有任务

        lock (_lock)
        {
            ActiveConfig = config;
            TargetTime = config.CalculateTargetTime();

            // 若定点时间已过，自动推迟到次日同一时间
            if (config.TimingMode == ScheduleTimingMode.SpecificTime && TargetTime <= DateTime.Now)
            {
                TargetTime = TargetTime.AddDays(1);
            }

            RemainingTime = TargetTime - DateTime.Now;
            if (RemainingTime < TimeSpan.Zero)
            {
                RemainingTime = TimeSpan.Zero;
            }

            IsRunning = true;
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            _schedulerTask = Task.Run(() => RunSchedulerLoopAsync(token), token);
        }

        var toolName = config.TargetCliDisplayName;
        var step = $"{toolName}定时调度";
        var modeDesc = config.TimingMode == ScheduleTimingMode.Countdown ? "倒计时模式" : "定点模式";
        var msg = $"已启动 {toolName} 终端定时任务 [{modeDesc}]，预计于 {TargetTime:yyyy-MM-dd HH:mm:ss} 触发执行 (目标PID: {config.TargetProcessId})";

        _loggingService.LogInfo(msg, step);
        StatusChanged?.Invoke(this, $"定时中: 目标时间 {TargetTime:HH:mm:ss}");
    }

    public void CancelSchedule()
    {
        lock (_lock)
        {
            if (!IsRunning)
            {
                return;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            IsRunning = false;
            RemainingTime = TimeSpan.Zero;
        }

        var toolName = ActiveConfig?.TargetCliDisplayName ?? "AI";
        _loggingService.LogInfo($"已手动取消 {toolName} 终端定时任务", $"{toolName}定时调度");
        StatusChanged?.Invoke(this, "已取消");
    }

    private async Task RunSchedulerLoopAsync(CancellationToken token)
    {
        var toolName = ActiveConfig?.TargetCliDisplayName ?? "AI";
        var step = $"{toolName}定时调度";

        try
        {
            while (!token.IsCancellationRequested)
            {
                var remaining = TargetTime - DateTime.Now;

                if (remaining <= TimeSpan.Zero)
                {
                    RemainingTime = TimeSpan.Zero;
                    CountdownTick?.Invoke(this, TimeSpan.Zero);
                    break;
                }

                RemainingTime = remaining;
                CountdownTick?.Invoke(this, remaining);

                // 每 1 秒轮询一次
                await Task.Delay(1000, token);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            // 到达预定时间点，触发终端命令注入
            _loggingService.LogInfo($"定时时间到达 ({DateTime.Now:HH:mm:ss})，开始向目标终端执行指令注入...", step);
            StatusChanged?.Invoke(this, "正在执行注入...");

            var cfg = ActiveConfig;
            if (cfg == null)
            {
                ExecutionCompleted?.Invoke(this, (false, "找不到任务配置"));
                return;
            }

            var success = await _injectionService.InjectCommandAsync(
                cfg.TargetHwnd,
                cfg.TargetProcessId,
                cfg.CommandText,
                cfg.AutoPressEnter);

            var finishMsg = success
                ? $"已在 {DateTime.Now:HH:mm:ss} 成功向 {cfg.TargetCliDisplayName} 终端 (PID: {cfg.TargetProcessId}) 发送指令并确认！"
                : $"向 {cfg.TargetCliDisplayName} 终端 (PID: {cfg.TargetProcessId}) 注入指令失败，请检查终端窗口是否已关闭。";

            ExecutionCompleted?.Invoke(this, (success, finishMsg));
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Claude 终端定时调度执行异常: {ex.Message}", step, ex);
            ExecutionCompleted?.Invoke(this, (false, $"执行异常: {ex.Message}"));
        }
        finally
        {
            lock (_lock)
            {
                IsRunning = false;
            }
            StatusChanged?.Invoke(this, "执行结束");
        }
    }

    public void Dispose()
    {
        CancelSchedule();
    }
}
