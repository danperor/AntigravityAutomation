// 文件用途：监控目标应用档案模型 AppTargetProfile。
// 描述一个可被自动确认工具监控的 Antigravity 系 Electron 应用（如 Antigravity / Antigravity IDE），
// 包含显示名称、可执行文件路径、%APPDATA% 下的用户数据目录名（用于发现 DevToolsActivePort
// 调试端口文件）以及是否启用监控。该模型由 appsettings.json 的 AutomationConfig:Targets
// 数组反序列化填充，也可由界面"监控目标"勾选列表实时修改。
// 背景：Antigravity（v2.11，Electron 41）与 Antigravity IDE（v2.5.5，VS Code 1.107 内核，
// Electron 39）均为 Google 出品的 VS Code 系 Electron 应用，二者用户数据目录分别为
// %APPDATA%\Antigravity 与 %APPDATA%\Antigravity IDE，开启 --remote-debugging-port 启动后
// 各自在目录下写入 DevToolsActivePort 文件，本工具据此分别建立 CDP 连接并行监控。

using System.Collections.Generic;

namespace AntigravityAutomation.Models;

/// <summary>
/// 监控目标应用档案。承载一个 Antigravity 系 Electron 应用的定位与开关信息。
/// </summary>
public sealed class AppTargetProfile
{
    /// <summary>
    /// 目标显示名称（如 "Antigravity"、"Antigravity IDE"）。用于日志前缀与界面勾选列表展示。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 目标应用可执行文件绝对路径。当前监控流程不启动应用（仅读取调试端口文件），
    /// 此路径用于界面展示与元素探索等场景参考。
    /// </summary>
    public string ExePath { get; set; } = string.Empty;

    /// <summary>
    /// 目标应用在 %APPDATA% 下的用户数据目录名（如 "Antigravity"、"Antigravity IDE"）。
    /// 应用以 --remote-debugging-port 启动后，Chromium 会在该目录写入 DevToolsActivePort
    /// 文件，监控服务读取其首行获得 CDP 调试端口。
    /// </summary>
    public string UserDataDirectoryName { get; set; } = string.Empty;

    /// <summary>
    /// 是否纳入监控。true 时监控服务为该目标启动独立监控循环；false 时跳过。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 以调试模式重启该目标时使用的 CDP 端口号（默认 9333）。
    /// 仅用于"调试重启"功能；监控流程始终从 DevToolsActivePort 文件读取实际端口，
    /// 不依赖此值。
    /// </summary>
    public int DebugPort { get; set; } = 9333;

    /// <summary>
    /// 创建内置默认监控目标列表：Antigravity 与 Antigravity IDE 双目标均启用。
    /// 配置文件缺失 Targets 节点时回退使用此默认值。
    /// </summary>
    /// <returns>默认监控目标列表（每次调用返回新实例，调用方可自由修改）。</returns>
    public static List<AppTargetProfile> CreateDefaults()
    {
        return new List<AppTargetProfile>
        {
            new AppTargetProfile
            {
                Name = "Antigravity",
                ExePath = @"C:\Users\peng\AppData\Local\Programs\antigravity\Antigravity.exe",
                UserDataDirectoryName = "Antigravity",
                Enabled = true
            },
            new AppTargetProfile
            {
                Name = "Antigravity IDE",
                ExePath = @"C:\Users\peng\AppData\Local\Programs\Antigravity IDE\Antigravity IDE.exe",
                UserDataDirectoryName = "Antigravity IDE",
                Enabled = true
            }
        };
    }

    /// <summary>
    /// 创建当前档案的浅副本（各属性值复制到新实例），
    /// 用于界面编辑时避免直接修改共享配置实例。
    /// </summary>
    /// <returns>属性值相同的新实例。</returns>
    public AppTargetProfile Clone()
    {
        return new AppTargetProfile
        {
            Name = Name,
            ExePath = ExePath,
            UserDataDirectoryName = UserDataDirectoryName,
            Enabled = Enabled,
            DebugPort = DebugPort
        };
    }
}
