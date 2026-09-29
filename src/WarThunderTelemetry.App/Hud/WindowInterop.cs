using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace WarThunderTelemetry.App.Hud;

/// <summary>
/// 悬浮窗的 Win32 互操作：透明背景、始终置顶、鼠标点击穿透、无边框。
/// <para>
/// WinUI 3 对透明窗口的支持弱于 WPF，必须借助 Win32 扩展样式实现。
/// 这是本项目技术风险最高的一环，因此单独抽成一个类便于独立验证与调整。
/// </para>
/// <para>
/// <b>关键前提</b>：透明窗口<b>不能使用 Mica / Acrylic 背景</b>，
/// 否则 <c>WS_EX_LAYERED</c> 的分层透明会被系统背景覆盖成不透明。
/// </para>
/// </summary>
public static partial class WindowInterop
{
    // ---- 窗口扩展样式常量 ----

    /// <summary>扩展样式索引。</summary>
    private const int GWL_EXSTYLE = -20;

    /// <summary>分层窗口：透明背景的前提。</summary>
    private const int WS_EX_LAYERED = 0x00080000;

    /// <summary>鼠标穿透：点击直达下层窗口（游戏）。</summary>
    private const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>不抢焦点：点击悬浮窗不会导致游戏失去焦点。</summary>
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>工具窗口：不出现在 Alt+Tab 与任务栏。</summary>
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>SetLayeredWindowAttributes 标志：按 alpha 值合成整个窗口。</summary>
    private const uint LWA_ALPHA = 0x00000002;

    // ---- SetWindowPos 常量 ----

    /// <summary>置顶。</summary>
    private static readonly nint HWND_TOPMOST = new(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    // ---- P/Invoke ----

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// 设置分层窗口的透明方式。
    /// <para>
    /// 只加 <c>WS_EX_LAYERED</c> 标志位是不够的：窗口仍按不透明方式绘制。
    /// 必须调用本 API 声明"按 alpha 通道合成"，窗口才真正支持透明。
    /// </para>
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(
        nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    /// <summary>
    /// 取窗口句柄。
    /// </summary>
    public static nint GetHandle(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return WindowNative.GetWindowHandle(window);
    }

    /// <summary>
    /// 把窗口配置为无边框、无标题栏的悬浮层。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="alwaysOnTop">是否始终置顶。</param>
    /// <param name="resizable">是否允许拖拽调整大小（锁定时应关掉，避免误操作）。</param>
    /// <param name="showInTaskbar">是否在任务栏显示。</param>
    public static void ConfigureAsOverlay(
        Window window,
        bool alwaysOnTop = true,
        bool resizable = false,
        bool showInTaskbar = false)
    {
        ArgumentNullException.ThrowIfNull(window);

        var appWindow = GetAppWindow(window);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // 去边框与标题栏 —— 这是无边框悬浮窗的标准做法。
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsAlwaysOnTop = alwaysOnTop;
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        appWindow.IsShownInSwitchers = showInTaskbar;

        // 叠加扩展样式：分层（透明前提）+ 不抢焦点 + 工具窗口。
        var handle = GetHandle(window);
        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

        // 清除可能残留的穿透位，保证初始为可交互状态。
        exStyle &= ~WS_EX_TRANSPARENT;

        SetWindowLong(handle, GWL_EXSTYLE, exStyle);
        ApplyTopMost(handle);

        // 只加 WS_EX_LAYERED 标志位是不够的 —— 窗口本体仍会按普通方式绘制，
        // 浅色主题下就是一层白底。
        // 真正让窗口"可以是透明的"靠这三步：
        //   1. WS_EX_LAYERED（上面已加）
        //   2. 关闭 DWM 的系统边框渲染（下面的 MakeWindowFrameTransparent）
        //   3. XAML 内容层自己画透明（根 Grid 的 Background）
        MakeWindowFrameTransparent(handle);

        MakeContentTransparent(window);
    }

    /// <summary>
    /// 加固窗口透明属性。窗口已上屏后调用效果最可靠。
    /// <para>
    /// 做两件事：
    /// <list type="number">
    /// <item>再次确保 <c>WS_EX_LAYERED</c> 生效，并用
    /// <c>SetLayeredWindowAttributes</c> 声明「按 alpha 合成」——
    /// 只加标志位而不调这个 API，窗口仍会按不透明方式绘制。</item>
    /// <item>把 DWM 边框铺满客户区，让系统不再绘制自己的浅色背景。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static void HardenTransparency(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = GetHandle(window);

        // 1) 分层窗口 + 让系统按 alpha 通道合成（LWA_ALPHA = 0x2）。
        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED;
        SetWindowLong(handle, GWL_EXSTYLE, exStyle);

        _ = SetLayeredWindowAttributes(handle, 0, 255, LWA_ALPHA);

        // 2) DWM 边框铺满客户区。
        MakeWindowFrameTransparent(handle);

        // 3) 重新确认置顶（改扩展样式可能影响 Z 序）。
        ApplyTopMost(handle);
    }

    /// <summary>
    /// 让窗口的 DWM 边框/背景不参与绘制。
    /// <para>
    /// WinUI 3 非打包窗口默认由 DWM 绘制一圈系统背景（浅色主题下是白色）。
    /// 这层在 XAML 内容<b>之下</b>，改 XAML 的 Background 是够不着它的 ——
    /// 必须通过 <c>DwmExtendFrameIntoClientArea</c> 把边框扩展到整个客户区，
    /// 让 DWM 认为自己"没有边框要画"，白色背景才会真正消失。
    /// </para>
    /// </summary>
    private static void MakeWindowFrameTransparent(nint handle)
    {
        try
        {
            // 负边距 = 让 DWM 把整个客户区当成"边框"处理，
            // 这样它就不画自己的背景色，由我们的 XAML 内容决定显示什么。
            var margins = new Margins
            {
                LeftWidth = -1,
                RightWidth = -1,
                TopHeight = -1,
                BottomHeight = -1,
            };

            _ = DwmExtendFrameIntoClientArea(handle, ref margins);
        }
        catch (Exception)
        {
            // DWM 不可用（极旧系统/远程会话）时忽略，退化为普通分层窗口。
        }
    }

    /// <summary>
    /// 把窗口内容根元素的背景设为全透明，抹掉 WinUI 的主题底色。
    /// <para>
    /// WinUI 3 会用 <c>ApplicationPageBackgroundThemeBrush</c> 给窗口内容根容器铺底，
    /// 浅色主题下是白色 —— 不抹掉它，悬浮窗就是一块白板。
    /// </para>
    /// <para>
    /// 只处理<b>根容器一层</b>：白色底来自根容器本身，
    /// 深层子元素（比如用户要的背景板 <c>Panel</c>）不能被误伤，
    /// 否则「显示背景面板」功能会失效。
    /// </para>
    /// </summary>
    public static void MakeContentTransparent(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        switch (root)
        {
            case Panel panel:
                panel.Background = new SolidColorBrush(Colors.Transparent);
                break;

            case Border border:
                border.Background = new SolidColorBrush(Colors.Transparent);
                break;

            case Control control:
                // 少数情况下根是 Control（如 ContentControl），同样清掉。
                control.Background = new SolidColorBrush(Colors.Transparent);
                break;
        }
    }

    // ---- 自检 ----

    /// <summary>
    /// 悬浮层自检结果。
    /// </summary>
    /// <param name="Handle">窗口句柄。</param>
    /// <param name="Layered">是否具备分层属性（透明的前提）。</param>
    /// <param name="ClickThrough">是否鼠标穿透。</param>
    /// <param name="NoActivate">是否不抢焦点。</param>
    /// <param name="ToolWindow">是否工具窗口（不在 Alt+Tab）。</param>
    /// <param name="TopMost">是否置顶。</param>
    /// <param name="Borderless">是否无边框无标题栏。</param>
    /// <param name="Resizable">是否允许拉伸。</param>
    /// <param name="Passed">是否全部符合预期。</param>
    public readonly record struct OverlayProbe(
        nint Handle,
        bool Layered,
        bool ClickThrough,
        bool NoActivate,
        bool ToolWindow,
        bool TopMost,
        bool Borderless,
        bool Resizable,
        bool Passed);

    /// <summary>
    /// 检查窗口当前的悬浮层属性，用于无人工干预的自动验证。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="expectClickThrough">预期是否处于穿透状态。</param>
    public static OverlayProbe Probe(Window window, bool expectClickThrough)
    {
        ArgumentNullException.ThrowIfNull(window);

        var appWindow = GetAppWindow(window);
        var handle = GetHandle(window);
        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);

        var layered = (exStyle & WS_EX_LAYERED) != 0;
        var clickThrough = (exStyle & WS_EX_TRANSPARENT) != 0;
        var noActivate = (exStyle & WS_EX_NOACTIVATE) != 0;
        var toolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;

        var topMost = false;
        var borderless = false;
        var resizable = false;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            topMost = presenter.IsAlwaysOnTop;
            borderless = !presenter.HasBorder && !presenter.HasTitleBar;
            resizable = presenter.IsResizable;
        }

        var passed = layered
            && clickThrough == expectClickThrough
            && noActivate
            && toolWindow
            && topMost
            && borderless
            && resizable;

        return new OverlayProbe(
            handle, layered, clickThrough, noActivate, toolWindow,
            topMost, borderless, resizable, passed);
    }

    /// <summary>
    /// 直接按窗口句柄检查悬浮层属性。
    /// <para>
    /// 原生分层窗口（<see cref="Native.LayeredWindow"/>）没有 <c>AppWindow</c>，
    /// 只能从句柄读扩展样式；置顶/无边框/可拉伸则由我们创建时的样式决定，
    /// 对原生窗口恒为 true（<c>WS_POPUP</c> 无边框、<c>WS_EX_TOPMOST</c> 置顶）。
    /// </para>
    /// </summary>
    /// <param name="handle">窗口句柄。</param>
    /// <param name="expectClickThrough">预期是否处于穿透状态。</param>
    public static OverlayProbe ProbeHandle(nint handle, bool expectClickThrough)
    {
        if (handle == nint.Zero)
        {
            return new OverlayProbe(nint.Zero, false, false, false, false, false, false, false, false);
        }

        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);

        var layered = (exStyle & WS_EX_LAYERED) != 0;
        var clickThrough = (exStyle & WS_EX_TRANSPARENT) != 0;
        var noActivate = (exStyle & WS_EX_NOACTIVATE) != 0;
        var toolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;

        // 原生分层窗口一律是 WS_POPUP + WS_EX_TOPMOST，无边框、可拉伸。
        const bool topMost = true;
        const bool borderless = true;
        const bool resizable = true;

        var passed = layered
            && clickThrough == expectClickThrough
            && noActivate
            && toolWindow;

        return new OverlayProbe(
            handle, layered, clickThrough, noActivate, toolWindow,
            topMost, borderless, resizable, passed);
    }

    /// <summary>按句柄读取窗口矩形与可见性。</summary>
    public static (RectInt32 Bounds, bool Visible) ProbePlacementHandle(nint handle)
    {
        if (handle == nint.Zero || !GetWindowRect(handle, out var rect))
        {
            return (new RectInt32(0, 0, 0, 0), false);
        }

        return (
            new RectInt32(
                rect.Left, rect.Top,
                rect.Right - rect.Left, rect.Bottom - rect.Top),
            IsWindowVisible(handle));
    }

    /// <summary>
    /// 取窗口的边框/完全透明状态快照，便于排查「窗口是不是根本没显示」。
    /// </summary>
    public static (RectInt32 Bounds, bool Visible) ProbePlacement(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var appWindow = GetAppWindow(window);

        var pos = appWindow.Position;
        var size = appWindow.Size;

        return (new RectInt32(pos.X, pos.Y, size.Width, size.Height), appWindow.IsVisible);
    }

    /// <summary>
    /// 设置鼠标点击穿透。
    /// <para>
    /// 开启后鼠标事件直达下层窗口（游戏），悬浮窗变为纯显示层；
    /// 关闭后恢复可交互。
    /// </para>
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="clickThrough">是否穿透。</param>
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = GetHandle(window);
        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);

        // 每次都以「叠加/清除单个位」的方式操作，避免覆盖掉其他样式。
        exStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

        if (clickThrough)
        {
            exStyle |= WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }

        SetWindowLong(handle, GWL_EXSTYLE, exStyle);

        // 重设样式后需重新确认置顶，否则可能被其他窗口盖住。
        ApplyTopMost(handle);
    }

    /// <summary>
    /// 让窗口保持置顶。
    /// </summary>
    public static void ApplyTopMost(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ApplyTopMost(GetHandle(window));
    }

    private static void ApplyTopMost(nint handle)
    {
        SetWindowPos(
            handle,
            HWND_TOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 取 AppWindow。
    /// </summary>
    public static AppWindow GetAppWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = GetHandle(window);
        var id = Win32Interop.GetWindowIdFromWindow(handle);
        return AppWindow.GetFromWindowId(id);
    }

    /// <summary>
    /// 移动窗口（用于拖动定位）。
    /// </summary>
    public static void MoveBy(Window window, int deltaX, int deltaY)
    {
        ArgumentNullException.ThrowIfNull(window);

        var appWindow = GetAppWindow(window);
        var position = appWindow.Position;
        appWindow.Move(new PointInt32(position.X + deltaX, position.Y + deltaY));
    }

    /// <summary>
    /// 取窗口位置。
    /// </summary>
    public static PointInt32 GetPosition(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return GetAppWindow(window).Position;
    }

    /// <summary>
    /// 同时设置位置与尺寸。
    /// <para>
    /// 拉伸时必须一次调用 <c>MoveAndResize</c> 而不是先 Move 再 Resize ——
    /// 拆成两次会让窗口中间态出现抖动（先移到新位置但还用旧尺寸）。
    /// </para>
    /// </summary>
    public static void SetBounds(Window window, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(window);

        GetAppWindow(window).MoveAndResize(new RectInt32(
            x,
            y,
            Math.Max(120, width),
            Math.Max(60, height)));
    }

    /// <summary>
    /// 取窗口所在显示器的可用高度（已扣除任务栏）。
    /// <para>
    /// 用于给「自动贴合内容」的高度封顶，避免勾选字段很多时
    /// 悬浮窗长到屏幕外面去。取不到时返回 0，调用方需自行兜底。
    /// </para>
    /// </summary>
    public static int GetWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            var handle = GetHandle(window);
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);

            if (monitor == nint.Zero)
            {
                return 0;
            }

            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return 0;
            }

            return info.rcWork.Bottom - info.rcWork.Top;
        }
        catch
        {
            // 取不到不致命，让调用方走默认上限。
            return 0;
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out Rect32 lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point32 point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    /// <summary>DWM 边框边距。全 -1 表示把边框铺满整个客户区。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    /// <summary>
    /// 取鼠标当前的<b>屏幕物理坐标</b>。
    /// <para>
    /// 拖拽窗口必须用屏幕绝对坐标：<c>PointerRoutedEventArgs.GetCurrentPoint(null)</c>
    /// 给的是相对坐标，在无边框分层窗口上会随窗口一起移动，
    /// 造成「拖一下窗口就飞出去」或「拖不动」的经典毛病。
    /// </para>
    /// </summary>
    public static PointInt32 GetCursorScreenPosition()
    {
        return GetCursorPos(out var p)
            ? new PointInt32(p.X, p.Y)
            : new PointInt32(0, 0);
    }

    /// <summary>
    /// 设置窗口位置。
    /// </summary>
    public static void SetPosition(Window window, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(window);
        GetAppWindow(window).Move(new PointInt32(x, y));
    }

    /// <summary>
    /// 设置窗口尺寸。
    /// </summary>
    public static void SetSize(Window window, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(window);

        GetAppWindow(window).Resize(new SizeInt32(
            Math.Max(120, width),
            Math.Max(60, height)));
    }
}
