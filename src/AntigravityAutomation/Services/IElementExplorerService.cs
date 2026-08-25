// 文件用途：界面元素探索服务接口 IElementExplorerService。
// 定义启动 Antigravity IDE 并枚举其界面可交互元素（主要是按钮）的契约，
// 供"探索模式"使用：当用户尚未确定目标按钮时，先调用本服务列出候选按钮及其定位信息，
// 再由用户在界面选择目标。接口与实现分离，便于替换后端或编写单元测试。

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// 界面元素探索服务契约。负责启动应用并枚举其可交互元素供用户选择。
/// </summary>
public interface IElementExplorerService
{
    /// <summary>
    /// 启动指定 Antigravity IDE 应用并枚举其界面中的按钮等可交互元素。
    /// 返回的列表按界面出现顺序排列，每项包含文字、角色、推荐选择器、边界框与可见性。
    /// 支持通过 cancellationToken 提前取消探索并释放资源。
    /// </summary>
    /// <param name="appPath">Antigravity IDE 可执行文件绝对路径。</param>
    /// <param name="cancellationToken">取消令牌，用于界面"停止探索"按钮中断流程。</param>
    /// <returns>本次探索发现的元素列表。若应用无可见按钮则返回空列表。</returns>
    Task<List<DiscoveredElement>> ExploreButtonsAsync(string appPath, CancellationToken cancellationToken);

    /// <summary>
    /// 停止当前探索会话并释放 Electron 进程与 Playwright 资源。
    /// 须为幂等操作：对已停止的服务再次调用不应抛出异常。
    /// </summary>
    /// <returns>表示异步停止操作的任务。</returns>
    Task StopAsync();
}