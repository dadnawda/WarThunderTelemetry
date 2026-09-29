using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WarThunderTelemetry.App;

/// <summary>
/// WinUI 3 没有 WPF 的 <c>SetResourceReference</c>，这里补一个等价扩展。
/// <para>
/// 用处：用代码动态创建控件时，仍能绑定到 <c>ThemeResource</c>
/// （例如 <c>WtCardBackgroundBrush</c>），主题切换或资源替换时自动跟随，
/// 而不是硬编码颜色值。
/// </para>
/// <para>
/// 实现原理：直接查 <see cref="FrameworkElement.Resources"/> 与
/// <see cref="Application.Current"/> 的资源字典，取到后赋给依赖属性。
/// </para>
/// </summary>
public static class ResourceExtensions
{
    /// <summary>
    /// 在应用资源字典中查找指定键的资源；找不到返回 <c>null</c>。
    /// </summary>
    public static object? FindResource(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue(key, out var value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// 把依赖属性的值设为资源键对应的对象。
    /// <para>
    /// 资源缺失时静默跳过 —— 保留控件的默认外观，比抛异常好。
    /// </para>
    /// </summary>
    /// <param name="target">目标对象。</param>
    /// <param name="property">依赖属性。</param>
    /// <param name="resourceKey">资源键。</param>
    public static void SetResourceReference(
        this DependencyObject target,
        DependencyProperty property,
        string resourceKey)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);

        if (FindResource(resourceKey) is not { } value)
        {
            return;
        }

        target.SetValue(property, value);
    }

    /// <summary>
    /// 取画笔资源；找不到时返回 <c>null</c>。
    /// </summary>
    public static Brush? FindBrush(string key) => FindResource(key) as Brush;
}
