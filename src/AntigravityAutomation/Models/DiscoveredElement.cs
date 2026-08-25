// 文件用途：界面元素探索结果模型 DiscoveredElement。
// 描述在 Antigravity IDE 界面中枚举到的一个可交互元素（主要是按钮）的元信息，
// 供 IElementExplorerService 返回并交由界面展示与选择。所有公共属性采用 PascalCase 命名。

namespace AntigravityAutomation.Models;

/// <summary>
/// 界面探索发现的元素描述。承载单个可交互元素的关键定位与展示信息。
/// </summary>
public sealed class DiscoveredElement
{
    /// <summary>
    /// 元素在本次探索结果列表中的序号（从 1 开始），便于界面展示与用户选择。
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 元素的显示文字（innerText 或 value）。可能为空（如纯图标按钮）。
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// 元素的可访问角色（role），如 "button"、"link"、"menuitem"。
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// 推荐用于定位该元素的 CSS 选择器。优先使用稳定属性，避免脆弱的 nth-child 结构。
    /// </summary>
    public string? Selector { get; set; }

    /// <summary>
    /// 元素边界框的字符串表示，格式 "x, y, width, height"，便于界面展示与调试。
    /// </summary>
    public string? BoundingBox { get; set; }

    /// <summary>
    /// 元素当前是否可见（未折叠、display 不为 none、且有非零尺寸）。
    /// </summary>
    public bool IsVisible { get; set; }
}