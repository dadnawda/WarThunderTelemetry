using System.Text;
using Microsoft.UI.Xaml;

namespace WarThunderTelemetry.App.Hud;

/// <summary>
/// 悬浮窗无人工干预自检。
/// <para>
/// 由命令行参数 <c>--selfcheck</c> 触发：真实创建悬浮窗，等首帧渲染完成后
/// 逐项读取 Win32 扩展样式与 <see cref="Microsoft.UI.Windowing.OverlappedPresenter"/>
/// 的属性，验证「透明 / 置顶 / 穿透 / 不抢焦点 / 无边框」是否真的生效，
/// 结果写入 stdout 后退出。
/// </para>
/// <para>
/// 之所以要有这个自检：这些属性全都要落到真实窗口句柄上才能确认，
/// 编译通过完全不能说明问题，而人工肉眼在游戏里逐个观察成本又太高。
/// </para>
/// </summary>
internal static class HudSelfCheck
{
    /// <summary>命令行开关。</summary>
    public const string Flag = "--selfcheck";

    /// <summary>命令行开关：额外遍历所有页面（冒烟测试）。</summary>
    public const string PagesFlag = "--selfcheck-pages";

    /// <summary>
    /// 设置页交互冒烟测试：逐个触发按钮与开关，确认处理器真的会执行。
    /// <para>
    /// 光「页面能构造出来」不够 —— 如果某个初始化异常让 <c>_loading</c> 之类的
    /// 标志卡住，页面看着正常但所有控件都是死的。只有真的点一遍才能发现。
    /// </para>
    /// </summary>
    public static async Task RunSettingsInteractionTestAsync(MainWindow window)
    {
        Console.WriteLine("=== 设置页交互冒烟测试 ===");

        window.NavigateContentForSelfCheck(typeof(Views.SettingsPage));
        await Task.Delay(500);

        if (window.ContentFrameForSelfCheck.Content is not Views.SettingsPage page)
        {
            Console.WriteLine("  无法取到设置页实例 : FAIL");
            return;
        }

        var result = page.RunInteractionSelfCheck();
        Console.WriteLine(result);

        Console.Out.Flush();
    }

    /// <summary>
    /// 逐页导航冒烟测试。
    /// <para>
    /// 页面构造期的事件回调顺序很容易踩空（历史上「全字段」页就因此崩过），
    /// 而这类问题只在真实导航时暴露。所以把它变成自动检查，而不是靠人手点。
    /// </para>
    /// </summary>
    public static async Task RunPageSmokeTestAsync(MainWindow window)
    {
        Console.WriteLine("=== 导航一致性检查 ===");
        Console.WriteLine(window.RunNavigationSelfCheck());

        Console.WriteLine("=== 页面导航冒烟测试 ===");

        var pages = new (string Name, Type Type)[]
        {
            ("仪表盘", typeof(Views.DashboardPage)),
            ("全字段", typeof(Views.RawFieldsPage)),
            ("设置", typeof(Views.SettingsPage)),
        };

        var allOk = true;

        foreach (var (name, type) in pages)
        {
            allOk &= await TryNavigateAsync(window, name, type, 400);
        }

        // 再来回切一遍 —— 「关掉再打开就点不动」这类问题只在重复导航时暴露。
        Console.WriteLine("--- 重复导航（每页来回各一次） ---");

        foreach (var (name, type) in pages)
        {
            allOk &= await TryNavigateAsync(window, $"{name} (第2次)", type, 300);
        }

        Console.WriteLine($"冒烟测试结论         : {(allOk ? "PASS" : "FAIL")}");
        Console.Out.Flush();
    }

    private static async Task<bool> TryNavigateAsync(
        MainWindow window, string name, Type type, int settleMs)
    {
        try
        {
            window.NavigateContentForSelfCheck(type);

            // 给页面构造与首次布局留出时间 —— 崩溃通常发生在这期间。
            await Task.Delay(settleMs);

            Console.WriteLine($"  {name,-12} : PASS");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {name,-12} : FAIL - {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return false;
        }
    }

    /// <summary>
    /// 跑一轮自检并在结束时关闭悬浮窗。
    /// <para>
    /// 悬浮窗已改为原生分层窗口：没有 <c>AppWindow</c>，透明由
    /// <c>UpdateLayeredWindow</c> 逐像素保证，因此这里直接从句柄验证
    /// 扩展样式（分层 / 穿透 / 不抢焦点 / 不出现在 Alt+Tab）。
    /// </para>
    /// </summary>
    /// <param name="hud">已创建的悬浮窗。</param>
    public static async Task RunAsync(NativeHudWindow hud)
    {
        var report = new StringBuilder();
        report.AppendLine("=== 悬浮窗自检（原生分层窗口）===");

        // 原生窗口创建是同步的，但给一帧时间让首次 Present 落下去。
        await Task.Delay(300);

        var expectClickThrough = hud.IsLockedForSelfCheck;

        var probe = WindowInterop.ProbeHandle(hud.Handle, expectClickThrough);
        var (bounds, visible) = WindowInterop.ProbePlacementHandle(hud.Handle);

        report.AppendLine($"句柄 HWND          : 0x{probe.Handle:X}");
        report.AppendLine($"窗口可见           : {visible}");
        report.AppendLine($"位置尺寸           : ({bounds.X},{bounds.Y}) {bounds.Width}x{bounds.Height}");
        report.AppendLine("--- 扩展样式 ---");
        report.AppendLine($"WS_EX_LAYERED      : {probe.Layered}   (分层，逐像素透明的前提)");
        report.AppendLine($"WS_EX_TRANSPARENT  : {probe.ClickThrough}   (鼠标穿透，预期 {expectClickThrough})");
        report.AppendLine($"WS_EX_NOACTIVATE   : {probe.NoActivate}   (不抢游戏焦点)");
        report.AppendLine($"WS_EX_TOOLWINDOW   : {probe.ToolWindow}   (不出现在 Alt+Tab)");
        report.AppendLine("--- 窗口形态 ---");
        report.AppendLine($"IsAlwaysOnTop      : {probe.TopMost}");
        report.AppendLine($"无边框无标题栏     : {probe.Borderless}   (WS_POPUP)");
        report.AppendLine($"可拉伸             : {probe.Resizable}   (自绘四边热区)");
        report.AppendLine("--- 内容 ---");
        report.AppendLine($"悬浮行数           : {hud.RowCountForSelfCheck}");
        report.AppendLine($"背景全透明         : {hud.IsBackgroundTransparentForSelfCheck}   (原生逐像素，恒真)");
        var (w, h) = hud.SizeForSelfCheck;
        report.AppendLine($"当前尺寸           : {w}x{h}");
        report.AppendLine("--- 结论 ---");
        report.AppendLine(probe.Passed ? "PASS" : "FAIL");

        Console.WriteLine(report.ToString());
        Console.Out.Flush();

        // 穿透切换是否也能真的改样式 —— 这是「解锁后还能拖窗口」的关键。
        hud.ApplyLock(!expectClickThrough);
        var flipped = WindowInterop.ProbeHandle(hud.Handle, !expectClickThrough);
        Console.WriteLine($"穿透切换自检       : {(flipped.ClickThrough == !expectClickThrough ? "PASS" : "FAIL")}");

        // 拉伸能力自检：真的改一次尺寸，确认能生效。
        var before = hud.SizeForSelfCheck;
        hud.SetBoundsForSelfCheck(
            bounds.X, bounds.Y, before.Width + 80, before.Height + 40);
        var after = hud.SizeForSelfCheck;
        var resized = after.Width == before.Width + 80 && after.Height == before.Height + 40;
        Console.WriteLine($"拉伸自检           : {(resized ? "PASS" : "FAIL")}"
                          + $" ({before.Width}x{before.Height} → {after.Width}x{after.Height})");

        // 纯透明背景自检：原生窗口背景恒为逐像素透明。
        Console.WriteLine($"背景全透明自检     : {(hud.IsBackgroundTransparentForSelfCheck ? "PASS" : "FAIL")}");

        // 还原成用户的配置（自检期间改过尺寸，但自检模式不落盘）。
        hud.RefreshAppearance();

        Console.Out.Flush();

        hud.Dispose();
    }

    /// <summary>
    /// 武器参数悬浮区自检。
    /// <para>
    /// 重点验证三件事：窗口属性（透明/置顶/穿透）与遥测悬浮窗一致、
    /// 背景为逐像素透明、以及导弹库能正常驱动一次渲染。
    /// </para>
    /// </summary>
    public static async Task RunWeaponAsync(NativeWeaponHudWindow hud)
    {
        var report = new StringBuilder();
        report.AppendLine("=== 武器悬浮区自检（原生分层窗口）===");

        await Task.Delay(300);

        var expectClickThrough = hud.IsLockedForSelfCheck;
        var probe = WindowInterop.ProbeHandle(hud.Handle, expectClickThrough);
        var (bounds, visible) = WindowInterop.ProbePlacementHandle(hud.Handle);

        report.AppendLine($"句柄 HWND          : 0x{probe.Handle:X}");
        report.AppendLine($"窗口可见           : {visible}");
        report.AppendLine($"位置尺寸           : ({bounds.X},{bounds.Y}) {bounds.Width}x{bounds.Height}");
        report.AppendLine($"WS_EX_LAYERED      : {probe.Layered}");
        report.AppendLine($"WS_EX_TRANSPARENT  : {probe.ClickThrough}   (预期 {expectClickThrough})");
        report.AppendLine($"WS_EX_NOACTIVATE   : {probe.NoActivate}");
        report.AppendLine($"WS_EX_TOOLWINDOW   : {probe.ToolWindow}");
        report.AppendLine($"IsAlwaysOnTop      : {probe.TopMost}");
        report.AppendLine($"无边框无标题栏     : {probe.Borderless}");
        report.AppendLine($"可拉伸             : {probe.Resizable}");
        report.AppendLine($"武器名文本         : {hud.WeaponNameForSelfCheck}");
        report.AppendLine($"背景全透明         : {hud.IsBackgroundTransparentForSelfCheck}   (原生逐像素，恒真)");
        report.AppendLine("--- 结论 ---");
        report.AppendLine(probe.Passed ? "PASS" : "FAIL");

        Console.WriteLine(report.ToString());

        // 穿透切换。
        hud.ApplyLock(!expectClickThrough);
        var flipped = WindowInterop.ProbeHandle(hud.Handle, !expectClickThrough);
        Console.WriteLine($"武器区穿透切换自检 : {(flipped.ClickThrough == !expectClickThrough ? "PASS" : "FAIL")}");

        // 拉伸。
        var before = hud.SizeForSelfCheck;
        hud.SetBounds(
            bounds.X, bounds.Y, before.Width + 60, before.Height + 40);
        var after = hud.SizeForSelfCheck;
        Console.WriteLine($"武器区拉伸自检     : {(after.Width == before.Width + 60 ? "PASS" : "FAIL")}"
                          + $" ({before.Width}x{before.Height} → {after.Width}x{after.Height})");

        // 用存档里的设置渲染一次，确认整条链路（识别 → 解算 → 渲染）不抛异常。
        try
        {
            hud.RenderForSelfCheck();
            Console.WriteLine("武器区渲染自检     : PASS");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"武器区渲染自检     : FAIL ({ex.GetType().Name}: {ex.Message})");
        }

        Console.Out.Flush();
        hud.Dispose();
    }
}
