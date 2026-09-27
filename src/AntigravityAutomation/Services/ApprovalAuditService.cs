// 文件用途：审批审计服务实现 ApprovalAuditService。
// 职责：将审批记录以 JSONL（每行一条 JSON）追加到 logs/approvals.jsonl，
//       并提供最近记录读取（供界面启动时回填审批历史）。
// 说明：
//   1. 与 Serilog 文本日志互补：文本日志面向人阅读，本文件面向长期追溯与程序消费。
//   2. 追加写入经锁串行化；IO 失败仅记日志、不上抛，绝不影响监控主流程。
//   3. 读取采用共享读模式，与正在进行的追加写入兼容。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// JSONL 文件型审批审计服务。文件固定为应用基目录下 logs/approvals.jsonl。
/// </summary>
public sealed class ApprovalAuditService : IApprovalAuditService
{
    // 审计文件所在目录名（与 Serilog 文本日志同目录）。
    private const string LogDirectoryName = "logs";

    // 审计文件名（JSONL：每行一条完整 JSON 记录）。
    private const string AuditFileName = "approvals.jsonl";

    // 序列化选项：关闭转义美化，保持单行紧凑；中文等非 ASCII 字符原样输出便于阅读。
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 日志服务，用于记录审计写入/读取失败（不阻断主流程）。
    private readonly ILoggingService _loggingService;

    // 追加写入串行化锁（读取不加锁，采用共享读模式容忍并发）。
    private readonly object _appendLock = new();

    // 审计文件完整路径。
    private readonly string _auditFilePath;

    /// <summary>
    /// 构造函数，注入日志服务并解析审计文件路径。
    /// </summary>
    /// <param name="loggingService">日志服务。</param>
    public ApprovalAuditService(ILoggingService loggingService)
    {
        _loggingService = loggingService
            ?? throw new ArgumentNullException(nameof(loggingService));
        _auditFilePath = Path.Combine(
            AppContext.BaseDirectory, LogDirectoryName, AuditFileName);
    }

    /// <inheritdoc />
    public void Append(ApprovalRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, SerializerOptions);
            lock (_appendLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_auditFilePath)!);
                File.AppendAllText(_auditFilePath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            // 审计写入失败不影响监控主流程，仅记 WARN。
            _loggingService.LogWarning($"审批审计记录写入失败：{ex.Message}", "审批追溯");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ApprovalRecord> ReadRecent(int count)
    {
        var records = new List<ApprovalRecord>();
        try
        {
            if (!File.Exists(_auditFilePath))
            {
                return records;
            }

            // 共享读模式打开，允许与并发追加共存。
            using var stream = new FileStream(
                _auditFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            // 全量解析后取尾部 count 条。审批频率极低（每条对应一次真实确认），
            // 文件体积增长缓慢，全量读在可预见生命周期内无性能问题。
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var record = JsonSerializer.Deserialize<ApprovalRecord>(line, SerializerOptions);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // 跳过损坏行（如进程中断写了一半），不影响其余记录。
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"审批审计历史读取失败：{ex.Message}", "审批追溯");
            return new List<ApprovalRecord>();
        }

        return records.Count <= count
            ? records
            : records.Skip(records.Count - count).ToList();
    }
}
