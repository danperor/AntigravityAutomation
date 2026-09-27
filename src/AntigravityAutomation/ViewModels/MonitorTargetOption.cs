// 文件用途：监控目标勾选项视图模型 MonitorTargetOption。
// 包装一个 AppTargetProfile（监控目标档案），向界面"监控目标"勾选列表暴露
// 显示名 Name 与可双向绑定的启用开关 IsEnabled。IsEnabled 变更通过 ReactiveUI
// RaiseAndSetIfChanged 通知界面，启动监控时由 MainViewModel 回写到配置模型。
// 说明：Profile 本体保持只读引用（名称/路径/用户数据目录在运行期不变），
//       仅启用开关为可变状态，避免 Model 层依赖 ReactiveUI。

using System.Windows.Input;
using AntigravityAutomation.Models;
using ReactiveUI;

namespace AntigravityAutomation.ViewModels;

/// <summary>
/// 监控目标勾选项。承载目标档案引用与界面可编辑的启用开关。
/// </summary>
public sealed class MonitorTargetOption : ReactiveObject
{
    // 启用开关后备字段。
    private bool _isEnabled;

    /// <summary>
    /// 构造函数。以目标档案初始化显示名与初始启用状态。
    /// </summary>
    /// <param name="profile">监控目标档案。</param>
    public MonitorTargetOption(AppTargetProfile profile)
    {
        Profile = profile ?? throw new System.ArgumentNullException(nameof(profile));
        _isEnabled = profile.Enabled;
    }

    /// <summary>
    /// 关联的监控目标档案（名称、可执行文件路径、用户数据目录名）。只读。
    /// </summary>
    public AppTargetProfile Profile { get; }

    /// <summary>
    /// 目标显示名称（如 "Antigravity"、"Antigravity IDE"），绑定到 CheckBox 的 Content。
    /// </summary>
    public string Name => Profile.Name;

    /// <summary>
    /// 是否纳入本次监控。绑定到 CheckBox 的 IsChecked。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    /// <summary>
    /// "以调试模式重启该目标"命令。由 MainViewModel 在填充选项列表时统一赋值，
    /// 绑定到选项行尾的"调试重启"按钮（CommandParameter 传入本选项自身）。
    /// </summary>
    public ICommand? RestartCommand { get; set; }

    /// <summary>
    /// 生成带回写启用状态的目标档案副本，供构建 AutomationConfig 使用。
    /// </summary>
    /// <returns>Enabled 已按界面勾选更新的档案副本。</returns>
    public AppTargetProfile ToProfile()
    {
        var clone = Profile.Clone();
        clone.Enabled = IsEnabled;
        return clone;
    }
}
