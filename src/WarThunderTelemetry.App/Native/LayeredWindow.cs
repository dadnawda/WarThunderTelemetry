using System.Runtime.InteropServices;
using System.Text;

namespace WarThunderTelemetry.App.Native;

/// <summary>
/// 原生 Win32 分层窗口（<c>WS_EX_LAYERED</c>）的托管封装。
/// <para>
/// <b>为什么要走原生这条路：</b>
/// WinUI 3 的非打包窗口在浅色主题下，DWM 会给窗口铺一层白色宿主背景，
/// 它位于 XAML 内容<b>之下</b>，改 XAML 的 Background 完全够不着；
/// 即使补齐 <c>WS_EX_LAYERED</c> + <c>SetLayeredWindowAttributes</c> +
/// <c>DwmExtendFrameIntoClientArea</c>，仍会出现白底。
/// </para>
/// <para>
/// 而原生分层窗口用 <c>UpdateLayeredWindow</c> 逐像素提交 BGRA 位图，
/// 每个像素的 alpha 由我们自己决定 —— 透明是<b>操作系统层面保证</b>的，
/// 不存在中间宿主层，也就不会再有白底。
/// </para>
/// <para>
/// 窗口本身完全无边框无标题栏，所有可见内容（文字、背景板、描边）
/// 都由我们在离屏位图上画好后一次性提交。
/// </para>
/// </summary>
public sealed partial class LayeredWindow : IDisposable
{
    // ---- 窗口样式 ----

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    private const int GWL_EXSTYLE = -20;

    /// <summary>UpdateLayeredWindow 的 dwFlags：使用新的位置与尺寸。</summary>
    private const uint ULW_ALPHA = 0x00000002;

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static readonly nint HWND_TOPMOST = new(-1);
    private static readonly nint HWND_NOTOPMOST = new(-2);

    /// <summary>窗口类名（全局唯一，只需注册一次）。</summary>
    private const string WindowClassName = "WarThunderTelemetry.OverlayWindow";

    /// <summary>窗口类样式：允许接收双击消息（WM_LBUTTONDBLCLK）。</summary>
    private const uint CS_DBLCLKS = 0x0008;

    private static readonly Lock RegisterGate = new();
    private static bool _classRegistered;

    private nint _hwnd;
    private bool _clickThrough;
    private bool _disposed;

    /// <summary>窗口是否已创建。</summary>
    public bool IsCreated => _hwnd != nint.Zero;

    /// <summary>窗口句柄。</summary>
    public nint Handle => _hwnd;

    /// <summary>当前是否处于鼠标穿透状态。</summary>
    public bool IsClickThrough => _clickThrough;

    /// <summary>当前是否置顶。</summary>
    public bool IsTopMost { get; private set; } = true;

    /// <summary>
    /// 穿透态下仍保持可点击的边框宽度（像素）。0 表示完全穿透。
    /// <para>
    /// 这是「锁定后还能解锁」的逃生口：窗口锁定时往往整体穿透，
    /// 若不给边缘留一圈可点击区域，用户就再也点不到窗口，
    /// 只能被迫去设置页解锁。
    /// </para>
    /// </summary>
    public int HookRing { get; set; } = 8;

    /// <summary>
    /// 创建窗口。
    /// </summary>
    /// <param name="x">初始 X（屏幕坐标）。</param>
    /// <param name="y">初始 Y（屏幕坐标）。</param>
    /// <param name="width">初始宽度。</param>
    /// <param name="height">初始高度。</param>
    /// <param name="title">窗口标题（仅用于调试识别，不显示）。</param>
    public void Create(int x, int y, int width, int height, string title)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hwnd != nint.Zero)
        {
            return;
        }

        EnsureClassRegistered();

        var exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;

        // 传递 this 指针给 WndProc，用于分发消息。
        var handle = GCHandle.Alloc(this, GCHandleType.Normal);

        try
        {
            _hwnd = CreateWindowEx(
                exStyle,
                WindowClassName,
                title,
                WS_POPUP,
                x, y, width, height,
                nint.Zero,
                nint.Zero,
                GetModuleHandle(null),
                GCHandle.ToIntPtr(handle));
        }
        catch
        {
            handle.Free();
            throw;
        }

        if (_hwnd == nint.Zero)
        {
            handle.Free();
            throw new InvalidOperationException(
                $"创建原生叠加窗口失败，GetLastError={Marshal.GetLastWin32Error()}");
        }

        // 窗口创建后立即按目标位置尺寸摆放，并显示（不激活）。
        SetWindowPos(
            _hwnd, HWND_TOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        IsTopMost = true;
    }

    private static void EnsureClassRegistered()
    {
        lock (RegisterGate)
        {
            if (_classRegistered)
            {
                return;
            }

            var wc = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(),

                // CS_DBLCLKS 必须开：不开的话 Windows 根本不会投递 WM_LBUTTONDBLCLK，
                // 双击只会被拆成两次 WM_LBUTTONDOWN，双击切换锁定就永远不生效。
                style = CS_DBLCLKS,
                lpfnWndProc = _wndProcDelegate,
                hInstance = GetModuleHandle(null),
                lpszClassName = WindowClassName,
            };

            var atom = RegisterClassEx(ref wc);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                // 1410 = 类已存在，属正常（同进程二次注册）。
                if (err != 1410)
                {
                    throw new InvalidOperationException($"注册窗口类失败，GetLastError={err}");
                }
            }

            _classRegistered = true;
        }
    }

    // 委托必须保持强引用，否则会被 GC 回收导致回调崩溃。
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private static readonly WndProcDelegate _wndProcDelegate = StaticWndProc;

    private static nint StaticWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        LayeredWindow? instance = null;

        if (msg == WM_NCCREATE && lParam != nint.Zero)
        {
            var cs = Marshal.PtrToStructure<CREATESTRUCT>(lParam);
            if (cs.lpCreateParams != nint.Zero)
            {
                instance = GCHandle.FromIntPtr(cs.lpCreateParams).Target as LayeredWindow;
                if (instance is not null)
                {
                    instance._hwnd = hWnd;
                }
            }
        }
        else
        {
            instance = FromHandle(hWnd);
        }

        return instance?.WndProc(msg, wParam, lParam)
            ?? DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>已创建窗口的句柄 → 实例映射（供消息分发）。</summary>
    private static readonly Dictionary<nint, WeakReference<LayeredWindow>> Instances = [];

    private static LayeredWindow? FromHandle(nint hWnd)
    {
        lock (Instances)
        {
            return Instances.TryGetValue(hWnd, out var weak) && weak.TryGetTarget(out var target)
                ? target
                : null;
        }
    }

    private void RegisterInstance()
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        lock (Instances)
        {
            Instances[_hwnd] = new WeakReference<LayeredWindow>(this);
        }
    }

    private const uint WM_NCCREATE = 0x0081;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_SETCURSOR = 0x0020;

    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;

    /// <summary>
    /// 请求重绘时触发（供上层填充内容）。
    /// </summary>
    public event Action<LayeredWindow, PaintRequest>? Paint;

    /// <summary>鼠标按下时触发（参数为屏幕坐标）。</summary>
    public event Action<LayeredWindow, int, int>? PointerPressed;

    /// <summary>鼠标移动时触发（参数为屏幕坐标）。</summary>
    public event Action<LayeredWindow, int, int>? PointerMoved;

    /// <summary>鼠标抬起时触发（参数为屏幕坐标）。</summary>
    public event Action<LayeredWindow, int, int>? PointerReleased;

    /// <summary>双击时触发。</summary>
    public event Action<LayeredWindow>? DoubleClicked;

    private nint WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCCREATE:
                RegisterInstance();
                return 1;

            case WM_NCHITTEST:
                // 穿透态：整个窗口对鼠标透明，事件直接落到游戏里。
                if (_clickThrough)
                {
                    // 但**边缘一圈**例外：保留一条极窄的可点击带，
                    // 让用户在穿透状态下仍能双击解锁。
                    // 没有这个逃生口的话，「锁定 → 穿透 → 点不到 → 永远解不开」
                    // 就成了死循环，只能去设置页救。
                    if (HookRing > 0 && IsInHookRing(lParam))
                    {
                        return HTCLIENT;
                    }

                    return HTTRANSPARENT;
                }

                return HTCLIENT;

            case WM_LBUTTONDOWN:
            {
                var (sx, sy) = ScreenPoint(lParam);
                PointerPressed?.Invoke(this, sx, sy);
                return 0;
            }

            case WM_MOUSEMOVE:
            {
                var (sx, sy) = ScreenPoint(lParam);
                PointerMoved?.Invoke(this, sx, sy);
                return 0;
            }

            case WM_LBUTTONUP:
            {
                var (sx, sy) = ScreenPoint(lParam);
                PointerReleased?.Invoke(this, sx, sy);
                return 0;
            }

            case WM_LBUTTONDBLCLK:
                DoubleClicked?.Invoke(this);
                return 0;

            case WM_DESTROY:
                lock (Instances)
                {
                    Instances.Remove(_hwnd);
                }

                return 0;

            default:
                return DefWindowProc(_hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// 判断鼠标是否落在穿透态下仍保留的「解锁边带」上。
    /// <para>
    /// 传入的是 <c>WM_NCHITTEST</c> 的 lParam，其低 16 位为屏幕坐标 X、
    /// 高 16 位为屏幕坐标 Y（<b>不是</b>客户区坐标），
    /// 因此这里直接用屏幕坐标和窗口矩形比较。
    /// </para>
    /// </summary>
    private bool IsInHookRing(nint lParam)
    {
        if (_hwnd == nint.Zero || !GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        var screenX = unchecked((short)(long)lParam);
        var screenY = unchecked((short)((long)lParam >> 16));

        var inX = screenX >= rect.Left - HookRing && screenX <= rect.Right + HookRing;
        var inY = screenY >= rect.Top - HookRing && screenY <= rect.Bottom + HookRing;

        if (!inX || !inY)
        {
            return false;
        }

        // 必须落在「边缘一圈」内，而不是窗口中央 ——
        // 中央要继续穿透给游戏，只有边缘才拦下来。
        var nearLeft = screenX <= rect.Left + HookRing;
        var nearRight = screenX >= rect.Right - HookRing;
        var nearTop = screenY <= rect.Top + HookRing;
        var nearBottom = screenY >= rect.Bottom - HookRing;

        return nearLeft || nearRight || nearTop || nearBottom;
    }

    /// <summary>把消息里的客户区坐标转成屏幕坐标。</summary>
    private (int X, int Y) ScreenPoint(nint lParam)
    {
        // lParam 低 16 位是客户区 X，高 16 位是客户区 Y（均为有符号 16 位）。
        var x = unchecked((short)(long)lParam);
        var y = unchecked((short)((long)lParam >> 16));

        var pt = new POINT { X = x, Y = y };
        if (_hwnd != nint.Zero && ClientToScreen(_hwnd, ref pt))
        {
            return (pt.X, pt.Y);
        }

        return (x, y);
    }

    /// <summary>
    /// 提交一帧内容。
    /// <para>
    /// 必须传入<b>预乘 alpha</b> 的 32 位 BGRA 像素（<c>UpdateLayeredWindow</c> 的要求）。
    /// 像素数组长度应为 <c>width * height * 4</c>。
    /// </para>
    /// </summary>
    public void Present(int[] bgraPixels, int width, int height, int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bgraPixels);

        if (_hwnd == nint.Zero)
        {
            return;
        }

        var screenDc = GetDC(nint.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = nint.Zero;
        var oldBitmap = nint.Zero;

        try
        {
            var bmi = NativeShared.CreateTopDown32(width, height);

            bitmap = CreateDIBSection(memDc, ref bmi, 0, out var bits, nint.Zero, 0);
            if (bitmap == nint.Zero || bits == nint.Zero)
            {
                return;
            }

            // 拷贝像素到 DIB 段。
            Marshal.Copy(bgraPixels, 0, bits, Math.Min(bgraPixels.Length, width * height));

            oldBitmap = SelectObject(memDc, bitmap);

            var size = new SIZE { cx = width, cy = height };
            var srcPos = new POINT { X = 0, Y = 0 };
            var dstPos = new POINT { X = x, Y = y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            _ = UpdateLayeredWindow(
                _hwnd, screenDc, ref dstPos, ref size,
                memDc, ref srcPos, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != nint.Zero)
            {
                _ = SelectObject(memDc, oldBitmap);
            }

            if (bitmap != nint.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            _ = DeleteDC(memDc);
            _ = ReleaseDC(nint.Zero, screenDc);
        }
    }

    /// <summary>设置鼠标穿透。</summary>
    public void SetClickThrough(bool clickThrough)
    {
        if (_hwnd == nint.Zero || _clickThrough == clickThrough)
        {
            return;
        }

        _clickThrough = clickThrough;

        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;

        if (clickThrough)
        {
            exStyle |= WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }

        _ = SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
    }

    /// <summary>移动并调整尺寸。</summary>
    public void SetBounds(int x, int y, int width, int height)
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        SetWindowPos(
            _hwnd, IsTopMost ? HWND_TOPMOST : HWND_NOTOPMOST,
            x, y, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>设置置顶。</summary>
    public void SetTopMost(bool topMost)
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        IsTopMost = topMost;
        SetWindowPos(
            _hwnd, topMost ? HWND_TOPMOST : HWND_NOTOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>显示或隐藏窗口。</summary>
    public void SetVisible(bool visible)
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        ShowWindow(_hwnd, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }

    /// <summary>取窗口当前矩形。</summary>
    public (int X, int Y, int Width, int Height) GetBounds()
    {
        if (_hwnd == nint.Zero || !GetWindowRect(_hwnd, out var rect))
        {
            return (0, 0, 0, 0);
        }

        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != nint.Zero)
        {
            _ = DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    // ================= P/Invoke =================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate? lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCT
    {
        public nint lpCreateParams;
        public nint hInstance;
        public nint hMenu;
        public nint hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public nint lpszName;
        public nint lpszClass;
        public int dwExStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    // RegisterClassEx 的 WndClassEx 含委托字段，LibraryImport 源生成器不支持，
    // 因此这两个用传统 DllImport。
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateDIBSection(
        nint hdc, ref NativeShared.BitmapInfo pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(
        nint hWnd, nint hdcDst, ref POINT pptDst, ref SIZE psize,
        nint hdcSrc, ref POINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? lpModuleName);
}

/// <summary>一次绘制请求，承载画布尺寸。</summary>
/// <param name="Width">画布宽度（像素）。</param>
/// <param name="Height">画布高度（像素）。</param>
public readonly record struct PaintRequest(int Width, int Height);
