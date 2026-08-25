// 文件用途：日志条目模型 LogEntry。
// 描述一条面向用户展示的实时日志记录，包含时间戳、级别、人类可读消息与所属自动化步骤名。
// 该模型由 ILoggingService 产生并通过 LogStream 推送给界面订阅者实时显示。
// 所有公共属性采用 PascalCase 命名，消息内容须为人类可读的描述性文字。

using System;

namespace AntigravityAutomation.Models;

/// <summary>
/// 实时日志条目。承载单条日志的全部展示信息，供界面订阅 ILoggingService.LogStream 后渲染。
/// </summary>
public sealed class LogEntry
{
    /// <summary>
    /// 日志产生时间戳。
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 日志级别，如 "INFO"、"WARN"、"ERROR"。使用大写字符串以便界面按级别着色。
    /// </summary>
    public string Level { get; set; } = "INFO";

    /// <summary>
    /// 人类可读的描述性消息。须清楚标注"是什么对象"与"在做什么操作"，
    /// 例如"已启动 Antigravity IDE，等待 5 秒完成窗口初始化"。
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 当前自动化步骤名，如 "启动应用"、"查找按钮"、"点击yes"、"点击submit"。
    /// 可为空，表示该日志不属于特定步骤（如初始化或收尾日志）。
    /// </summary>
    public string? Step { get; set; }

    /// <summary>
    /// 生成一条 INFO 级别的 LogEntry 快捷工厂方法。
    /// </summary>
    /// <param name="message">人类可读的描述性消息。</param>
    /// <param name="step">所属自动化步骤名，可为空。</param>
    /// <returns>构造完成的 LogEntry 实例。</returns>
    public static LogEntry Info(string message, string? step = null) =>
        new() { Level = "INFO", Message = message, Step = step };

    /// <summary>
    /// 生成一条 WARN 级别的 LogEntry 快捷工厂方法。
    /// </summary>
    /// <param name="message">人类可读的描述性消息。</param>
    /// <param name="step">所属自动化步骤名，可为空。</param>
    /// <returns>构造完成的 LogEntry 实例。</returns>
    public static LogEntry Warn(string message, string? step = null) =>
        new() { Level = "WARN", Message = message, Step = step };

    /// <summary>
    /// 生成一条 ERROR 级别的 LogEntry 快捷工厂方法。
    /// </summary>
    /// <param name="message">人类可读的描述性消息。</param>
    /// <param name="step">所属自动化步骤名，可为空。</param>
    /// <returns>构造完成的 LogEntry 实例。</returns>
    public static LogEntry Error(string message, string? step = null) =>
        new() { Level = "ERROR", Message = message, Step = step };
}