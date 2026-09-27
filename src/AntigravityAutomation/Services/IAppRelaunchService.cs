// 文件用途：应用调试重启服务接口 IAppRelaunchService。
// 定义"检测目标应用运行/调试状态 → 关闭运行实例 → 以 --remote-debugging-port 重新启动 →
// 等待调试端口就绪"的能力契约，供界面"调试重启"按钮使用。
// 接口与实现分离，便于替换实现或编写不依赖真实进程的单元测试。

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AntigravityAutomation.Models;

namespace AntigravityAutomation.Services;

/// <summary>
/// 应用调试重启服务契约。负责检测目标应用调试状态并以调试模式重启它。
/// </summary>
public interface IAppRelaunchService
{
    /// <summary>
    /// 查找当前正在运行的目标应用全部进程（主进程与子进程）。
    /// 通过可执行文件绝对路径精确匹配，避免误伤同名程序。
    /// </summary>
    /// <param name="profile">监控目标档案。</param>
    /// <returns>运行中的进程数组；未运行为空数组。</returns>
    Process[] FindRunningProcesses(AppTargetProfile profile);

    /// <summary>
    /// 探测目标应用的 CDP 调试端口当前是否存活。
    /// 读取其用户数据目录下的 DevToolsActivePort 文件并 HTTP 探测 /json/version，
    /// 双重验证避免采信上次运行残留的陈旧端口文件。
    /// </summary>
    /// <param name="profile">监控目标档案。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存活的端口号；无调试端口或不可达时返回 null。</returns>
    Task<int?> GetLiveDebugPortAsync(AppTargetProfile profile, CancellationToken cancellationToken);

    /// <summary>
    /// 关闭给定进程集合。先对每个进程尝试优雅关闭（CloseMainWindow），
    /// 等待固定超时（15 秒）；仍有存活时按 forceKill 决定是否强制结束整棵进程树。
    /// </summary>
    /// <param name="processes">待关闭的进程集合。</param>
    /// <param name="forceKill">true 时优雅关闭超时后强制 Kill 整棵进程树（可能丢失未保存内容）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示全部进程已退出；false 表示仍有存活（调用方可询问用户后重试 forceKill）。</returns>
    Task<bool> CloseInstancesAsync(Process[] processes, bool forceKill, CancellationToken cancellationToken);

    /// <summary>
    /// 以调试模式启动目标应用（不带 --user-data-dir，使用默认用户数据目录），
    /// 并等待其 CDP 调试端口就绪。
    /// </summary>
    /// <param name="profile">监控目标档案（使用其 ExePath 与 DebugPort）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>就绪的调试端口号；超时未就绪返回 null（应用可能仍在启动中）。</returns>
    Task<int?> LaunchWithDebugPortAsync(AppTargetProfile profile, CancellationToken cancellationToken);
}
