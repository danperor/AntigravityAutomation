using System;

namespace AntigravityAutomation.Models;

/// <summary>
/// 定时执行方式枚举。
/// </summary>
public enum ScheduleTimingMode
{
    /// <summary>
    /// 倒计时模式（如 5小时00分钟后执行）。
    /// </summary>
    Countdown,

    /// <summary>
    /// 定点具体时刻（如 2026-09-27 04:30:00 准时执行）。
    /// </summary>
    SpecificTime
}

/// <summary>
/// Claude 终端定时任务配置模型。
/// </summary>
public sealed class ClaudeScheduleConfig
{
    /// <summary>
    /// 目标终端进程 ID。
    /// </summary>
    public int TargetProcessId { get; set; }

    /// <summary>
    /// 目标终端窗口句柄。
    /// </summary>
    public IntPtr TargetHwnd { get; set; }

    /// <summary>
    /// 目标终端显示名称。
    /// </summary>
    public string TargetDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 监控的目标 AI CLI 工具类型（Claude 或 Kimi Code）。
    /// </summary>
    public TargetCliType TargetCli { get; set; } = TargetCliType.Claude;

    /// <summary>
    /// 目标 CLI 工具友好名称。
    /// </summary>
    public string TargetCliDisplayName => TargetCli == TargetCliType.KimiCode ? "Kimi Code" : "Claude";

    /// <summary>
    /// 定时方式。
    /// </summary>
    public ScheduleTimingMode TimingMode { get; set; } = ScheduleTimingMode.Countdown;

    /// <summary>
    /// 倒计时：小时。默认 5 小时（匹配 5 小时速率限制）。
    /// </summary>
    public int CountdownHours { get; set; } = 5;

    /// <summary>
    /// 倒计时：分钟。
    /// </summary>
    public int CountdownMinutes { get; set; } = 0;

    /// <summary>
    /// 倒计时：秒。
    /// </summary>
    public int CountdownSeconds { get; set; } = 0;

    /// <summary>
    /// 定点时刻目标时间（绝对时间）。
    /// </summary>
    public DateTime SpecificDateTime { get; set; } = DateTime.Now.AddHours(5);

    /// <summary>
    /// 要注入发送的命令内容（如：继续、/retry 或自定义 Prompt）。
    /// </summary>
    public string CommandText { get; set; } = "继续";

    /// <summary>
    /// 注入完成后是否自动按下回车键 (Enter)。默认 true。
    /// </summary>
    public bool AutoPressEnter { get; set; } = true;

    /// <summary>
    /// 启动定时后是否自动最小化至系统托盘。默认 true。
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// 计算该配置的目标触发时间。
    /// </summary>
    public DateTime CalculateTargetTime()
    {
        if (TimingMode == ScheduleTimingMode.SpecificTime)
        {
            return SpecificDateTime;
        }

        var duration = new TimeSpan(CountdownHours, CountdownMinutes, CountdownSeconds);
        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.FromSeconds(5); // 兜底避免负数或零
        }
        return DateTime.Now.Add(duration);
    }
}
