using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WarThunderTelemetry.App.Services;
using WarThunderTelemetry.App.Views;
using WarThunderTelemetry.Core;
using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Alerts;
using WarThunderTelemetry.Core.Models;
using Windows.Graphics;
using Windows.UI;

namespace WarThunderTelemetry.App;

/// <summary>
/// 主窗口：仪表盘 / 全字段 / 设置 三个页面，顶部常驻状态条。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _refreshTimer;
    private TelemetryStatus? _pending;

    /// <summary>
    /// XAML 里首个 <c>NavigationViewItem</c> 带 <c>IsSelected="True"</c>，
    /// 因此 <c>SelectionChanged</c> 会在 <c>InitializeComponent()</c> 期间触发，
    /// 此时 <c>ContentFrame</c> 还可能为 null。用它挡掉初始化期的回调。
    /// </summary>
    private bool _ready;

    /// <summary>构造。</summary>
    public MainWindow()
    {
        InitializeComponent();

        // 应用保存的窗口位置与尺寸。
        RestoreBounds();

        ContentFrame.Navigate(typeof(DashboardPage));

        // 导航元素与首帧就绪后，才允许 SelectionChanged 真正翻页。
        _ready = true;

        // 数据到达频率远高于 UI 需要，这里按 ~12Hz 合并刷新，
        // 避免数据风暴拖垮界面。
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(80);
        _refreshTimer.Tick += OnRefreshTick;

        if (App.Telemetry is { } telemetry)
        {
            telemetry.Updated += OnTelemetryUpdated;
        }

        _refreshTimer.Start();

        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void RestoreBounds()
    {
        var bounds = App.Settings?.Current.MainWindowBounds;
        if (bounds is null)
        {
            return;
        }

        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    private void OnTelemetryUpdated(object? sender, TelemetryStatus status)
    {
        // 不在采集线程直接刷 UI，只记录最新状态由定时器统一渲染。
        _pending = status;
    }

    private void OnRefreshTick(DispatcherQueueTimer sender, object args)
    {
        var status = _pending;
        if (status is null)
        {
            return;
        }

        _pending = null;
        UpdateStatusBar(status);

        // 把最新状态转给当前页面。
        if (ContentFrame.Content is IStatusAware page)
        {
            page.OnStatusUpdated(status);
        }
    }

    private void UpdateStatusBar(TelemetryStatus status)
    {
        var connected = status.Snapshot.IsConnected;

        StatusDot.Fill = new SolidColorBrush(connected ? Color.FromArgb(255, 26, 127, 55) : Color.FromArgb(255, 152, 160, 171));
        StatusText.Text = connected ? $"已连接 · 127.0.0.1:{App.Settings?.Current.Port ?? 8111}" : "未连接（等待游戏启动）";

        // 数据源发生过自动回退时，把原因挂出来 —— 否则用户会疑惑
        //「我明明勾了模拟源，怎么在读真实数据」或反过来。
        var fallback = Services.AppSettingsStore.LastSourceFallbackReason;
        StatusText.Text += fallback is null ? string.Empty : $" · {fallback}";

        // 载具信息：区分精确匹配与兜底
        if (status.VehicleProfile is { } profile)
        {
            var suffix = status.IsExactVehicleMatch ? string.Empty : "（通用阈值）";
            VehicleText.Text = $"载具：{profile.DisplayName}{suffix}";
        }
        else
        {
            VehicleText.Text = "载具：—";
        }

        // 告警：显示最严重的一条
        if (status.Alerts.Count > 0)
        {
            var top = status.Alerts[0];
            AlertText.Text = $"⚠ {top.Title}：{top.Detail}";
            AlertText.Foreground = new SolidColorBrush(ToColor(top.Severity));
        }
        else
        {
            AlertText.Text = connected ? "各项参数正常" : string.Empty;
            AlertText.Foreground = new SolidColorBrush(Color.FromArgb(255, 91, 99, 110));
        }
    }

    /// <summary>把告警级别映射为主题色。</summary>
    public static Color ToColor(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => Color.FromArgb(255, 207, 34, 46),
        AlertSeverity.Warning => Color.FromArgb(255, 188, 76, 0),
        AlertSeverity.Caution => Color.FromArgb(255, 154, 103, 0),
        _ => Color.FromArgb(255, 91, 99, 110),
    };

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || ContentFrame is null)
        {
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var pageType = item.Tag switch
        {
            "raw" => typeof(RawFieldsPage),
            "settings" => typeof(SettingsPage),
            "dashboard" => typeof(DashboardPage),

            // 认不出的项（含 Tag 为 null 的情况）一律忽略，绝不兜底成某个页面。
            //
            // 这一点很关键：早先这里写的是 `_ => typeof(DashboardPage)`，
            // 结果 NavigationView 内置的 Settings 项（Tag 为 null）被当成仪表盘，
            // 而内容区已经在仪表盘上时 Navigate 会被跳过 ——
            // 选中态停在内置设置项、内容区不动，界面看起来就「卡死进不去」。
            // 宁可什么都不做，也不要让选中态和内容区脱节。
            _ => null,
        };

        if (pageType is null)
        {
            return;
        }

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    /// <summary>
    /// 供自检逐页导航用。
    /// <para>
    /// 刻意不走 <c>NavigationView.SelectedItem</c> —— 那是用户交互路径，
    /// 自检要测的是「页面本身能不能构造出来」。
    /// </para>
    /// </summary>
    internal void NavigateContentForSelfCheck(Type pageType) =>
        ContentFrame.Navigate(pageType);

    /// <summary>供自检取当前页面实例。</summary>
    internal Frame ContentFrameForSelfCheck => ContentFrame;

    /// <summary>
    /// 导航一致性自检。
    /// <para>
    /// 检查三件事：
    /// ① 内置 Settings 项必须关闭（它的 Tag 为 null，会造成选中态与内容区脱节）；
    /// ② 每个菜单项的 Tag 都能映射到一个真实页面；
    /// ③ 逐个选中菜单项后，内容区确实切到了对应页面。
    /// </para>
    /// <para>
    /// 历史上「点设置进去就出不来」正是第 ①②条同时出问题导致的，
    /// 所以把这条规则固化下来，避免以后再犯。
    /// </para>
    /// </summary>
    internal string RunNavigationSelfCheck()
    {
        var report = new System.Text.StringBuilder();
        var failures = 0;

        // ① 内置 Settings 项必须关掉
        var settingsItem = Nav.SettingsItem as NavigationViewItem;
        var builtInOff = settingsItem is null || !Nav.IsSettingsVisible;
        report.AppendLine($"  {"内置Settings项已关闭",-24} : {(builtInOff ? "PASS" : "FAIL - 仍开启，会导致导航卡死")}");
        if (!builtInOff)
        {
            failures++;
        }

        // ②③ 逐个菜单项：Tag 能否映射、选中后内容区是否真的切换
        foreach (var menuItem in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            var tag = menuItem.Tag as string ?? "(null)";

            var mapped = tag switch
            {
                "raw" => typeof(RawFieldsPage),
                "settings" => typeof(SettingsPage),
                "dashboard" => typeof(DashboardPage),
                _ => null,
            };

            if (mapped is null)
            {
                failures++;
                report.AppendLine($"  {"菜单项 " + tag,-24} : FAIL - Tag 无法映射到页面");
                continue;
            }

            // 选中它，确认真的切过去了
            Nav.SelectedItem = menuItem;

            var actual = ContentFrame.CurrentSourcePageType;
            var ok = actual == mapped;

            report.AppendLine($"  {"菜单项 " + tag,-24} : {(ok ? "PASS" : $"FAIL - 期望 {mapped.Name}，实际 {actual?.Name ?? "null"}")}");
            if (!ok)
            {
                failures++;
            }
        }

        report.AppendLine($"导航一致性结论       : {(failures == 0 ? "PASS" : $"FAIL ({failures} 项)")}");

        // 复位到仪表盘，别把界面留在奇怪的状态。
        if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault() is { } first)
        {
            Nav.SelectedItem = first;
        }

        return report.ToString();
    }

    private void OnToggleHudClick(object sender, RoutedEventArgs e)
    {
        if (App.HudWindow is null)
        {
            App.ShowHud();
            HudButton.Content = "关闭悬浮窗";
        }
        else
        {
            App.CloseHud();
            HudButton.Content = "显示悬浮窗";
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveBounds();
        _refreshTimer.Stop();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        SaveBounds();

        if (App.Telemetry is { } telemetry)
        {
            telemetry.Updated -= OnTelemetryUpdated;
        }

        // 悬浮窗随主窗口一起关闭，避免残留无主窗口。
        App.CloseHud();
    }

    private void SaveBounds()
    {
        if (App.Settings is not { } store)
        {
            return;
        }

        var position = AppWindow.Position;
        var size = AppWindow.Size;

        store.Save(store.Current with
        {
            MainWindowBounds = new WindowBounds
            {
                X = position.X,
                Y = position.Y,
                Width = size.Width,
                Height = size.Height,
            },
        });
    }
}

/// <summary>
/// 能接收遥测状态更新的页面。
/// </summary>
public interface IStatusAware
{
    /// <summary>状态更新回调（已在 UI 线程）。</summary>
    void OnStatusUpdated(TelemetryStatus status);
}
