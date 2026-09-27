using System;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// Claude 终端定时任务调度服务接口。
/// </summary>
public interface IClaudeSchedulerService
{
    /// <summary>
    /// 是否正处于定时调度运行中。
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 当前生效的任务配置。
    /// </summary>
    ClaudeScheduleConfig? ActiveConfig { get; }

    /// <summary>
    /// 剩余倒计时。
    /// </summary>
    TimeSpan RemainingTime { get; }

    /// <summary>
    /// 目标触发绝对时间。
    /// </summary>
    DateTime TargetTime { get; }

    /// <summary>
    /// 倒计时每秒触发事件（用于 UI 和系统托盘刷新）。
    /// </summary>
    event EventHandler<TimeSpan>? CountdownTick;

    /// <summary>
    /// 调度状态改变事件。
    /// </summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 定时任务执行完毕事件 (bool success, string message)。
    /// </summary>
    event EventHandler<(bool Success, string Message)>? ExecutionCompleted;

    /// <summary>
    /// 启动定时任务。
    /// </summary>
    /// <param name="config">调度配置。</param>
    void StartSchedule(ClaudeScheduleConfig config);

    /// <summary>
    /// 取消/中止当前运行中的定时任务。
    /// </summary>
    void CancelSchedule();
}
