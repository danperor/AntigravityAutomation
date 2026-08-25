// 文件用途：日志服务实现 LoggingService。
// 职责：
//   1. 使用 Serilog 将日志写入文件（logs/automation.log，按日滚动），输出模板包含
//      时间戳、级别、步骤名与人类可读消息，便于事后分析与调试。
//   2. 同时通过 System.Reactive.Subjects.Subject<LogEntry> 将每条日志以 LogEntry 形式
//      推送给界面订阅者（LogStream 返回 AsObservable 只读视图），实现界面实时日志显示。
//   3. LogError 在传入异常时，将异常完整信息交由 Serilog 的 {Exception} 输出，
//      并在面向界面的 LogEntry.Message 中附加简要异常类型与消息，便于用户快速识别错误。
//   4. 实现 IDisposable：释放时关闭 Serilog logger 并对 Subject 调用 OnCompleted，
//      避免界面订阅悬挂。
// 设计说明：本服务持有独立的 Serilog logger 实例（区别于 App.xaml.cs 中的全局兜底 logger），
//           专门用于业务自动化流程日志，文件名为 automation.log。Subject 的 OnNext 通过
//           lock 保护以保证多线程写入时的线程安全（Serilog logger 本身已线程安全）。

using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AntigravityAutomation.Models;
using Serilog;
using Serilog.Core;


namespace AntigravityAutomation.Services;

/// <summary>
/// 日志服务实现。同时向 Serilog 文件 Sink 与 System.Reactive 界面订阅流写入日志。
/// </summary>
public sealed class LoggingService : ILoggingService, IDisposable
{
    // Serilog 文件 logger 实例。线程安全，多线程并发写入无需额外同步。
    private readonly Logger _logger;

    // 面向界面订阅的日志事件流。OnNext 需通过 _subjectLock 保护以保证线程安全。
    private readonly Subject<LogEntry> _logSubject = new();

    // 保护 _logSubject.OnNext 的同步锁，避免多线程同时推送导致 Subject 状态损坏。
    private readonly object _subjectLock = new();

    // 标记是否已释放，防止重复 Dispose 导致 logger 重复关闭或 Subject 重复 OnCompleted。
    private bool _disposed;

    /// <summary>
    /// 构造函数。初始化 Serilog 文件 logger，配置按日滚动与包含步骤的输出模板。
    /// </summary>
    public LoggingService()
    {
        // 输出模板包含时间戳、级别、步骤名（来自 ForContext 注入的 Step 属性）与消息，
        // 末尾 {Exception} 由 Serilog 在传入异常时自动填充完整堆栈。
        const string outputTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Step:{Step}] {Message:lj}{NewLine}{Exception}";

        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Async(a => a.File(
                path: "logs/automation.log",
                rollingInterval: RollingInterval.Day,
                outputTemplate: outputTemplate,
                encoding: System.Text.Encoding.UTF8,
                shared: true))
            .CreateLogger();
    }

    /// <summary>
    /// 实时日志事件流的只读视图。界面订阅此流即可获得后续所有 LogEntry 推送，
    /// AsObservable 屏蔽了订阅者的 OnNext/OnCompleted 调用，保证流只能由本服务控制。
    /// </summary>
    public IObservable<LogEntry> LogStream => _logSubject.AsObservable();

    /// <summary>
    /// 记录一条 INFO 级别日志。同时写入 Serilog 文件与界面订阅流。
    /// </summary>
    /// <param name="message">人类可读的描述性消息，须标注"是什么"与"在做什么"。</param>
    /// <param name="step">当前自动化步骤名（如"启动应用"），可为空表示非步骤日志。</param>
    public void LogInfo(string message, string? step = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        // 使用 ForContext 注入 Step 属性，使输出模板中的 {Step} 占位符能取到步骤名。
        var contextualLogger = GetContextualLogger(step);
        contextualLogger.Information(message);

        PushLogEntry(LogEntry.Info(message, step));
    }

    /// <summary>
    /// 记录一条 WARN 级别日志。同时写入 Serilog 文件与界面订阅流。
    /// </summary>
    /// <param name="message">人类可读的描述性消息，说明告警对象与原因。</param>
    /// <param name="step">当前自动化步骤名，可为空。</param>
    public void LogWarning(string message, string? step = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var contextualLogger = GetContextualLogger(step);
        contextualLogger.Warning(message);

        PushLogEntry(LogEntry.Warn(message, step));
    }

    /// <summary>
    /// 记录一条 ERROR 级别日志。同时写入 Serilog 文件与界面订阅流。
    /// 若传入异常，Serilog 会通过 {Exception} 输出完整异常信息，
    /// 同时在面向界面的 LogEntry.Message 中附加简要异常类型与消息，便于用户快速识别。
    /// </summary>
    /// <param name="message">人类可读的描述性消息，说明错误对象与失败原因。</param>
    /// <param name="step">当前自动化步骤名，可为空。</param>
    /// <param name="ex">关联异常实例，可为空。</param>
    public void LogError(string message, string? step = null, Exception? ex = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var contextualLogger = GetContextualLogger(step);
        if (ex is not null)
        {
            // Serilog 在传入异常时会自动将其 ToString 填充到模板的 {Exception} 占位符。
            contextualLogger.Error(ex, message);
        }
        else
        {
            contextualLogger.Error(message);
        }

        // 面向界面的消息附加简要异常摘要，避免在界面展示完整堆栈造成阅读负担。
        var displayMessage = ex is not null
            ? $"{message} | 异常: {ex.GetType().Name}: {ex.Message}"
            : message;

        PushLogEntry(LogEntry.Error(displayMessage, step));
    }

    /// <summary>
    /// 根据步骤名创建带 Step 属性上下文的 logger。步骤为空时返回基础 logger。
    /// </summary>
    /// <param name="step">自动化步骤名，可为空。</param>
    /// <returns>带 Step 属性的 logger，或基础 logger。</returns>
    private ILogger GetContextualLogger(string? step)
    {
        return string.IsNullOrWhiteSpace(step)
            ? _logger.ForContext("Step", "无")
            : _logger.ForContext("Step", step);
    }

    /// <summary>
    /// 向界面订阅流推送一条 LogEntry。通过 lock 保护 Subject.OnNext 的线程安全。
    /// </summary>
    /// <param name="entry">待推送的日志条目。</param>
    private void PushLogEntry(LogEntry entry)
    {
        lock (_subjectLock)
        {
            // 在已释放后不再推送，避免对已 OnCompleted 的 Subject 调用 OnNext 抛异常。
            if (_disposed)
            {
                return;
            }

            _logSubject.OnNext(entry);
        }
    }

    /// <summary>
    /// 释放服务资源：对 Subject 调用 OnCompleted 通知界面订阅流结束，
    /// 并关闭 Serilog logger 刷新文件缓冲。幂等，多次调用安全。
    /// </summary>
    public void Dispose()
    {
        lock (_subjectLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _logSubject.OnCompleted();
                _logSubject.Dispose();
            }
            catch
            {
                // Subject 释放失败不应阻断后续 logger 关闭。
            }
        }

        try
        {
            _logger.Dispose();
        }
        catch
        {
            // logger 关闭失败忽略，避免 Dispose 抛异常影响调用方。
        }
    }
}