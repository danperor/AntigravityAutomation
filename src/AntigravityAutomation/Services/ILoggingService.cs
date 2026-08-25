// 文件用途：日志服务接口 ILoggingService。
// 定义面向业务代码与界面订阅的统一日志契约。业务代码通过 LogInfo/LogWarning/LogError 写入日志，
// 界面通过订阅 LogStream 实时接收 LogEntry 并渲染。所有日志消息须为人类可读的描述性文字。
// 接口与实现分离，便于使用 Serilog 或其他后端实现，且便于单元测试替换为内存记录实现。

using System;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// 日志服务契约。提供业务侧写入与界面侧订阅两条通道。
/// </summary>
public interface ILoggingService
{
    /// <summary>
    /// 记录一条 INFO 级别日志。
    /// </summary>
    /// <param name="message">人类可读的描述性消息，须标注"是什么"与"在做什么"。</param>
    /// <param name="step">当前自动化步骤名（如"启动应用"），可为空表示非步骤日志。</param>
    void LogInfo(string message, string? step = null);

    /// <summary>
    /// 记录一条 WARN 级别日志。
    /// </summary>
    /// <param name="message">人类可读的描述性消息，说明告警对象与原因。</param>
    /// <param name="step">当前自动化步骤名，可为空。</param>
    void LogWarning(string message, string? step = null);

    /// <summary>
    /// 记录一条 ERROR 级别日志。
    /// </summary>
    /// <param name="message">人类可读的描述性消息，说明错误对象与失败原因。</param>
    /// <param name="step">当前自动化步骤名，可为空。</param>
    /// <param name="ex">关联异常实例，可为空。实现应将其 ToString 追加到消息或单独字段。</param>
    void LogError(string message, string? step = null, Exception? ex = null);

    /// <summary>
    /// 实时日志事件流。界面订阅此流即可获得后续所有 LogEntry 推送。
    /// 须在服务释放时完成流的 OnCompleted 通知，避免界面订阅悬挂。
    /// </summary>
    IObservable<LogEntry> LogStream { get; }
}