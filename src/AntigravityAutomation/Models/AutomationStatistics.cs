// 文件用途：自动化运行统计信息模型 AutomationStatistics。
// 描述 ElectronAutomationService 在运行过程中向界面推送的实时统计快照，
// 包含本次运行已确认 "Yes, allow this time" 交互项的总次数、首个已连接目标的 CDP 调试端口、
// 整体连接状态，以及各监控目标的逐条状态摘要文本（多目标并行监控时供界面一览）。
// 界面 MainViewModel 订阅 StatisticsChanged 事件后，
// 通过 Dispatcher 将这些值同步到绑定属性，供状态栏与控制面板实时显示。
// 采用 sealed record 不可变值类型语义，保证事件推送过程中状态快照不会被误改。

namespace AntigravityAutomation.Models;

/// <summary>
/// 自动化运行统计快照。由 ElectronAutomationService 在确认次数变化、连接建立、
/// 断线重连、停止等关键节点推送，界面据此更新状态栏与控制面板的统计数字。
/// </summary>
/// <param name="ConfirmationCount">本次运行全部监控目标累计确认 'Yes, allow this time' 的总次数。</param>
/// <param name="CdpPort">首个已连接目标的 CDP 调试端口号；全部未连接时为 0。
/// 多目标场景下各目标端口详见 <paramref name="TargetSummary"/>。</param>
/// <param name="IsConnected">是否已有至少一个目标通过 CDP 连接。</param>
/// <param name="TargetSummary">各监控目标的逐条状态摘要（如 "Antigravity:9226✓ · Antigravity IDE:等待中"）；
/// 无目标或未运行时为 string.Empty。</param>
public sealed record AutomationStatistics(
    int ConfirmationCount,
    int CdpPort,
    bool IsConnected,
    string TargetSummary = "");
