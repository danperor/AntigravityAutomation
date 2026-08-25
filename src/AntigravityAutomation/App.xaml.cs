// 文件用途：WPF 应用程序代码后台。
// 职责：
//   1. OnStartup：构建 ServiceCollection 注册三个后台服务（按命名约定），
//      初始化 Serilog 全局 logger，构建 ServiceProvider 解析 MainViewModel，
//      创建 MainWindow 注入 ViewModel 并显示。
//   2. OnExit：释放 ServiceProvider 与 LoggingService，关闭并刷新 Serilog logger。
// 说明：服务实现类（LoggingService / ElementExplorerService / ElectronAutomationService）
//       由另一工程师并行创建，本文件按命名约定引用。若其尚未存在，编译将失败，属预期情况。

using System;
using System.IO;
using System.Windows;
using AntigravityAutomation.Services;
using AntigravityAutomation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AntigravityAutomation;

/// <summary>
/// WPF 应用程序类。对应 App.xaml 的代码后台。
/// </summary>
public partial class App : Application
{
    // 依赖注入容器，OnExit 时释放。
    private ServiceProvider? _serviceProvider;

    // 日志服务引用，OnExit 时显式 Dispose 以刷新并关闭日志 Sink。
    private ILoggingService? _loggingService;

    /// <summary>
    /// 应用启动事件处理。构建 DI 容器、初始化 Serilog、创建并显示主窗口。
    /// </summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="e">启动事件参数。</param>
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 初始化 Serilog 全局 logger：按日期滚动写入 logs 目录，便于事后分析调试。
        InitializeSerilog();

        // 构建依赖注入容器并注册服务。
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        _serviceProvider = serviceCollection.BuildServiceProvider();

        // 解析日志服务（单例），OnExit 时显式释放。
        _loggingService = _serviceProvider.GetRequiredService<ILoggingService>();

        // 解析主视图模型（其构造函数注入三个服务）。
        var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();

        // 记入启动日志，便于确认界面层装配完成。
        _loggingService.LogInfo("Antigravity IDE 自动化工具启动完成，界面层已装配就绪");

        // 创建主窗口注入 ViewModel 并显示。
        var mainWindow = new MainWindow(mainViewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    /// <summary>
    /// 应用退出事件处理。释放容器与日志服务，关闭 Serilog logger。
    /// </summary>
    /// <param name="sender">事件发送者。</param>
    /// <param name="e">退出事件参数。</param>
    private void OnExit(object sender, ExitEventArgs e)
    {
        _loggingService?.LogInfo("Antigravity IDE 自动化工具退出，开始释放资源");

        // 释放日志服务（若其实现 IDisposable，会刷新并关闭日志 Sink）。
        if (_loggingService is IDisposable disposableLoggingService)
        {
            disposableLoggingService.Dispose();
        }

        // 释放 DI 容器及其管理的所有单例服务。
        _serviceProvider?.Dispose();

        // 关闭并刷新 Serilog 全局 logger。
        Log.CloseAndFlush();
    }

    /// <summary>
    /// 配置依赖注入容器：注册三个后台服务（按命名约定）与 MainViewModel。
    /// 日志服务注册为单例，确保界面订阅与业务写入共享同一实例。
    /// </summary>
    /// <param name="services">服务集合。</param>
    private static void ConfigureServices(IServiceCollection services)
    {
        // 日志服务：单例，界面订阅 LogStream 与业务写入须共享同一实例。
        services.AddSingleton<ILoggingService, LoggingService>();

        // 元素探索服务：作用域或单例均可，此处注册为单例避免重复创建底层 Playwright 资源。
        services.AddSingleton<IElementExplorerService, ElementExplorerService>();

        // Electron 自动化服务：单例，保留 StatusChanged 事件订阅的稳定性。
        services.AddSingleton<IElectronAutomationService, ElectronAutomationService>();

        // 主视图模型：单例，整个应用生命周期共享一个 ViewModel 实例。
        services.AddSingleton<MainViewModel>();
    }

    /// <summary>
    /// 初始化 Serilog 全局 logger。配置按日期滚动的文件 Sink，输出到 logs 目录。
    /// 若 LoggingService 内部已自行初始化 Serilog，此处配置作为全局兜底。
    /// </summary>
    private static void InitializeSerilog()
    {
        try
        {
            // 确保 logs 目录存在，避免首次启动时文件 Sink 写入失败。
            Directory.CreateDirectory("logs");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Async(a => a.File(
                    path: "logs/antigravity-.log",
                    rollingInterval: RollingInterval.Day,
                    outputTemplate:
                        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
                .CreateLogger();

            Log.Information("Serilog 全局 logger 初始化完成，日志输出目录：logs");
        }
        catch (Exception ex)
        {
            // 日志初始化失败不应阻止应用启动，仅输出到调试通道。
            System.Diagnostics.Debug.WriteLine($"Serilog 初始化失败：{ex}");
        }
    }
}
