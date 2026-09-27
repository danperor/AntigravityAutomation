// 文件用途：审批追溯记录模型 ApprovalRecord。
// 职责：描述一次自动确认（批准）行为的完整审计信息——何时、在哪个目标应用、
//       命中了什么交互文本、被批准的请求正文（命令/操作描述）、点击与 Enter 是否成功。
// 说明：由 ElectronAutomationService 在每次确认时生成，经 IApprovalAuditService
//       以 JSONL 形式持久化到 logs/approvals.jsonl，并推送界面审批历史列表。

namespace AntigravityAutomation.Models;

/// <summary>
/// 一次自动确认行为的审计记录。不可变值对象，直接 JSON 序列化为单行 JSONL。
/// </summary>
/// <param name="Timestamp">确认发生的本地时间（含时区偏移）。</param>
/// <param name="TargetName">发生确认的目标应用显示名（如 Antigravity / Antigravity IDE）。</param>
/// <param name="MatchedText">实际命中的交互行文本。</param>
/// <param name="RequestContent">从审批卡片容器提取的请求正文（命令/操作描述）；
/// 提取失败时为空字符串。</param>
/// <param name="Clicked">是否成功点击选中交互行。</param>
/// <param name="EnterSent">是否成功发送 Enter 键（页面引用缺失时为 false）。</param>
public sealed record ApprovalRecord(
    DateTimeOffset Timestamp,
    string TargetName,
    string MatchedText,
    string RequestContent,
    bool Clicked,
    bool EnterSent);
