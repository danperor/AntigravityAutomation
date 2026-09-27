// 文件用途：Electron 自动化服务接口 IElectronAutomationService。
// 定义通过 CDP 并行连接一个或多个 Antigravity 系 Electron 应用（Antigravity / Antigravity IDE 等），
// 持续监控 "Yes, allow this time" 交互项并自动选中交互行、按 Enter 确认的守护契约。
// 接口与实现分离，便于替换为不同后端或编写不依赖真实 Electron 的单元测试。

using System;
using System.Threading;
using System.Threading.Tasks;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// Electron 自动化服务契约。负责启动并控制 Antigravity IDE 完成目标自动化流程。
/// </summary>
public interface IElectronAutomationService
{
    /// <summary>
    /// 状态变化通知。参数为人类可读的状态描述（如"正在启动应用"、"已点击目标按钮"）。
    /// 界面可订阅此事件实时更新状态栏。
    /// </summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 运行统计变化通知。在确认次数增加、CDP 端口发现、连接建立/断开、停止等关键节点触发，
    /// 携带当前 <see cref="AutomationStatistics"/> 快照。界面可订阅此事件实时更新
    /// 控制面板的"确认次数"、"CDP 端口"、"连接状态"等统计信息。
    /// </summary>
    event EventHandler<AutomationStatistics>? StatisticsChanged;

    /// <summary>
    /// 审批追溯通知。每次自动确认（点击交互行并发送 Enter）完成后触发，
    /// 携带包含请求正文（命令/操作描述）的 <see cref="ApprovalRecord"/> 审计快照。
    /// 界面可订阅此事件实时更新审批历史列表；记录已由服务先行持久化到审计文件。
    /// </summary>
    event EventHandler<ApprovalRecord>? ApprovalRecorded;

    /// <summary>
    /// 按给定配置执行完整自动化流程：为每个启用的监控目标建立 CDP 连接并并行守护，
    /// 检测到目标交互项时自动选中交互行并按 Enter 确认。
    /// 支持通过 cancellationToken 提前取消，取消后应尽快释放全部 Playwright 资源。
    /// </summary>
    /// <param name="config">自动化配置，指定监控目标列表与匹配文本等。</param>
    /// <param name="cancellationToken">取消令牌，用于界面"停止"按钮中断流程。</param>
    /// <returns>表示异步操作的任务。任务结果为流程是否成功完成。</returns>
    Task RunAutomationAsync(AutomationConfig config, CancellationToken cancellationToken);

    /// <summary>
    /// 停止当前正在执行的自动化流程并释放 Electron 进程与 Playwright 资源。
    /// 须为幂等操作：对已停止的服务再次调用不应抛出异常。
    /// </summary>
    /// <returns>表示异步停止操作的任务。</returns>
    Task StopAsync();
}