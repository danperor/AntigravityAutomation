// 文件用途：审批历史界面展示项 ApprovalHistoryItem。
// 职责：将 ApprovalRecord 审计记录转换为界面列表可直接绑定的展示形态
//       （时间文本、目标名、内容预览、异常备注、完整 ToolTip），
//       避免在 XAML 中引入值转换器。

using System;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.ViewModels;

/// <summary>
/// 审批历史列表的单条展示项。不可变值对象。
/// </summary>
/// <param name="TimeText">时间文本（当天为 HH:mm:ss，跨天为 MM-dd HH:mm）。</param>
/// <param name="TargetName">目标应用显示名。</param>
/// <param name="ContentPreview">请求正文预览（未能提取时为占位提示）。</param>
/// <param name="StatusNote">异常备注（如"Enter 未送达"），正常时为空字符串。</param>
/// <param name="FullToolTip">悬停展示的完整信息（多行）。</param>
public sealed record ApprovalHistoryItem(
    string TimeText,
    string TargetName,
    string ContentPreview,
    string StatusNote,
    string FullToolTip)
{
    /// <summary>
    /// 由审计记录构建展示项。
    /// </summary>
    /// <param name="record">审批审计记录。</param>
    /// <returns>界面展示项。</returns>
    public static ApprovalHistoryItem FromRecord(ApprovalRecord record)
    {
        var local = record.Timestamp.ToLocalTime();
        var timeText = local.Date == DateTime.Today
            ? local.ToString("HH:mm:ss")
            : local.ToString("MM-dd HH:mm");

        var hasContent = !string.IsNullOrWhiteSpace(record.RequestContent);
        var preview = hasContent ? record.RequestContent : "（未能提取请求正文）";

        // 异常备注：点击失败或 Enter 未送达时给出醒目标记，便于事后排查。
        var statusNote = record switch
        {
            { EnterSent: false } => "Enter 未送达",
            { Clicked: false } => "点击失败",
            _ => string.Empty
        };

        var fullToolTip =
            $"时间：{local:yyyy-MM-dd HH:mm:ss}\n" +
            $"目标：{record.TargetName}\n" +
            $"命中：{record.MatchedText}\n" +
            $"点击：{(record.Clicked ? "成功" : "失败")}，Enter：{(record.EnterSent ? "已送达" : "未送达")}\n" +
            $"请求正文：{(hasContent ? record.RequestContent : "（未能提取）")}";

        return new ApprovalHistoryItem(timeText, record.TargetName, preview, statusNote, fullToolTip);
    }
}
