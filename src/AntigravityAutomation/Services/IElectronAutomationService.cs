// 文件用途：Electron 自动化服务接口 IElectronAutomationService。
// 定义使用 Playwright Electron 模式启动 Antigravity IDE 并完成
// "找按钮行→点击目标按钮→处理 yes, allow this time 交互消息→点击 submit"全流程的契约。
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
    /// 按给定配置执行完整自动化流程：启动应用→查找目标按钮→点击→处理 yes 消息→点击 submit。
    /// 支持通过 cancellationToken 提前取消，取消后应尽快释放 Electron 进程与 Playwright 资源。
    /// </summary>
    /// <param name="config">自动化配置，指定应用路径、目标按钮定位方式、超时等。</param>
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