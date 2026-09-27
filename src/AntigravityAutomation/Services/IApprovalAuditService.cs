// 文件用途：审批审计服务契约 IApprovalAuditService。
// 职责：定义审批追溯记录的持久化追加与历史读取能力，
//       供 ElectronAutomationService 写入、MainViewModel 启动时回填历史列表。

using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// 审批审计服务。将每次自动确认行为以 JSONL（每行一条 JSON）形式持久化，
/// 并支持读取最近的记录供界面展示。实现须保证线程安全且写入失败不影响监控主流程。
/// </summary>
public interface IApprovalAuditService
{
    /// <summary>
    /// 追加一条审批记录到审计文件（logs/approvals.jsonl）。线程安全；
    /// 内部自行处理 IO 异常（记录到日志服务），不向调用方抛出。
    /// </summary>
    /// <param name="record">待追加的审批记录。</param>
    void Append(ApprovalRecord record);

    /// <summary>
    /// 读取审计文件中最近的若干条记录（按时间升序返回，最旧在前）。
    /// 文件不存在或读取失败时返回空列表。
    /// </summary>
    /// <param name="count">最多返回的记录条数。</param>
    /// <returns>最近的审批记录列表（时间升序）。</returns>
    IReadOnlyList<ApprovalRecord> ReadRecent(int count);
}
