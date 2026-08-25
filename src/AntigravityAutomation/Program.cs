// 文件用途：应用程序入口点 Program 类。
// 职责：
//   1. 拦截 Playwright CLI 子命令（如 install、codegen），转发给 Playwright 生成的
//      Microsoft.Playwright.Program.Main，用于安装浏览器二进制或打开元素拾取工具。
//   2. 否则按常规 WPF 流程启动 App（App.xaml 定义的应用实例）。
// 说明：csproj 中通过 <StartupObject> 显式指定本类为入口点，避免与 WPF 自动生成的
//       App.Main 以及 Playwright 生成的 Microsoft.Playwright.Program.Main 产生多入口歧义。

using System;
using System.Linq;
using Microsoft.Playwright;

namespace AntigravityAutomation;

/// <summary>
/// 应用程序主入口点。负责区分 Playwright CLI 调用与正常 WPF 桌面启动两种场景。
/// </summary>
public static class Program
{
    /// <summary>
    /// 应用程序主入口。WPF 需要 STAThread 以使用单线程单元模型。
    /// </summary>
    /// <param name="args">命令行参数。首参数为 "playwright" 时进入 Playwright CLI 转发流程。</param>
    /// <returns>进程退出码：0 表示正常退出，非 0 表示 Playwright CLI 返回的退出码。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // 当通过 "dotnet run -- playwright install" 等方式调用时，
        // 将子命令转发给 Playwright 生成的入口，完成浏览器二进制安装或 codegen 等操作。
        // Microsoft.Playwright.Program.Main 由 Playwright NuGet 包在编译期生成，
        // 返回 int 退出码。
        if (args.Length > 0 && args[0] == "playwright")
        {
            var playwrightArgs = args.Skip(1).ToArray();
            return Microsoft.Playwright.Program.Main(playwrightArgs);
        }

        // 常规 WPF 启动：创建应用实例、加载 XAML 资源并进入消息循环。
        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }
}
