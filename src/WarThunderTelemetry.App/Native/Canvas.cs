using System.Runtime.InteropServices;

namespace WarThunderTelemetry.App.Native;

/// <summary>
/// 一块可绘制的 ARGB 画布。
/// <para>
/// 用 GDI 的 <c>DrawTextW</c> 在 32 位 DIB 上直接画字，好处是：
/// <list type="bullet">
/// <item>中文渲染开箱即用，不需要额外字体包；</item>
/// <item>可以直接控制每个像素的 alpha —— 这正是分层窗口需要的；</item>
/// <item>不依赖 System.Drawing.Common（.NET 10 上它是独立包，还得额外引入）。</item>
/// </list>
/// </para>
/// <para>
/// 像素格式为 <b>BGRA</b>（Windows 的 32 位 DIB 原生顺序），
/// 提交给 <see cref="LayeredWindow.Present"/> 前会自动做预乘 alpha。
/// </para>
/// </summary>
public sealed partial class Canvas : IDisposable
{
    private nint _memDc;
    private nint _bitmap;
    private nint _oldBitmap;
    private nint _bits;
    private bool _disposed;

    /// <summary>
    /// 覆盖度掩码：每个像素一个字节，记录「这里被画过多少」。
    /// <para>
    /// <b>为什么必须有这个：</b>GDI 写 32 位 DIB 时不写 alpha 通道，
    /// 原先是靠「RGB 是否非 0」反推覆盖度 —— 但<b>纯黑 (0,0,0) 的 RGB 全是 0</b>，
    /// 会被误判成「没画过」，于是黑字完全不可见。
    /// 用户把文字设成黑色是最常见的需求（亮色场景下最清晰），
    /// 所以必须改成显式记录绘制覆盖度，而不是猜。
    /// </para>
    /// <para>
    /// 掩码在构造时清零，每次 <see cref="Clear"/> 也清零；
    /// 所有绘制原语负责往里写覆盖度。
    /// </para>
    /// </summary>
    private readonly byte[] _coverage;

    /// <summary>构造指定尺寸的画布。</summary>
    public Canvas(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _coverage = new byte[Width * Height];

        var screenDc = GetDC(nint.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screenDc);
            if (_memDc == nint.Zero)
            {
                throw new InvalidOperationException("创建内存 DC 失败。");
            }

            var bmi = NativeShared.CreateTopDown32(Width, Height);

            _bitmap = CreateDIBSection(_memDc, ref bmi, 0, out _bits, nint.Zero, 0);
            if (_bitmap == nint.Zero || _bits == nint.Zero)
            {
                throw new InvalidOperationException("创建 DIB 段失败。");
            }

            _oldBitmap = SelectObject(_memDc, _bitmap);

            // GDI 文字默认不抗锯齿，必须开启 ClearType/灰度抗锯齿才好看。
            _ = SetBkMode(_memDc, TRANSPARENT);
        }
        finally
        {
            _ = ReleaseDC(nint.Zero, screenDc);
        }
    }

    /// <summary>画布宽度。</summary>
    public int Width { get; }

    /// <summary>画布高度。</summary>
    public int Height { get; }

    /// <summary>底层 DC 句柄（供直接调用 GDI）。</summary>
    public nint Dc => _memDc;

    /// <summary>
    /// 把整块画布填成指定颜色（含 alpha）。传 alpha=0 即完全清空。
    /// </summary>
    public void Clear(byte r, byte g, byte b, byte a)
    {
        var count = Width * Height;
        var buffer = new byte[count * 4];

        // 掩码同步清空 —— 否则上一帧的覆盖度会残留，旧内容"粘"在新画布上。
        Array.Clear(_coverage, 0, _coverage.Length);

        if (a != 0)
        {
            // 预乘：分层窗口要求颜色分量已乘以 alpha。
            var pr = (byte)(r * a / 255);
            var pg = (byte)(g * a / 255);
            var pb = (byte)(b * a / 255);

            for (var i = 0; i < count; i++)
            {
                var o = i * 4;
                buffer[o] = pb;
                buffer[o + 1] = pg;
                buffer[o + 2] = pr;
                buffer[o + 3] = a;
            }
        }

        Marshal.Copy(buffer, 0, _bits, buffer.Length);
    }

    /// <summary>
    /// 填充一个矩形区域（含 alpha）。
    /// </summary>
    public void FillRect(int x, int y, int w, int h, byte r, byte g, byte b, byte a)
    {
        if (a == 0 || w <= 0 || h <= 0)
        {
            return;
        }

        x = Math.Max(0, x);
        y = Math.Max(0, y);
        var right = Math.Min(Width, x + w);
        var bottom = Math.Min(Height, y + h);

        if (right <= x || bottom <= y)
        {
            return;
        }

        var pr = (byte)(r * a / 255);
        var pg = (byte)(g * a / 255);
        var pb = (byte)(b * a / 255);

        // 逐行写：DIB 每行 4 字节对齐，32 位下天然对齐，可直接按行拷贝。
        var rowLength = (right - x) * 4;
        var row = new byte[rowLength];
        for (var i = 0; i < rowLength; i += 4)
        {
            row[i] = pb;
            row[i + 1] = pg;
            row[i + 2] = pr;
            row[i + 3] = a;
        }

        for (var yy = y; yy < bottom; yy++)
        {
            var offset = (yy * Width + x) * 4;
            Marshal.Copy(row, 0, _bits + offset, rowLength);

            // 记录覆盖度：矩形填充是实心的，整行都算「画过」。
            var maskOffset = yy * Width + x;
            for (var xx = x; xx < right; xx++)
            {
                _coverage[maskOffset + (xx - x)] = a;
            }
        }
    }

    /// <summary>
    /// 画一圈矩形边框（中空，含 alpha）。
    /// <para>
    /// 悬浮窗默认是纯文字、没有背景板，玩家看不到窗口的边界，
    /// 拉伸时不知道边缘在哪、也容易误触。画一圈极细的边框可以
    /// 明确「可操作范围」，又不会像背景板那样遮住游戏画面。
    /// </para>
    /// </summary>
    /// <param name="x">左上角 X。</param>
    /// <param name="y">左上角 Y。</param>
    /// <param name="w">外框宽度。</param>
    /// <param name="h">外框高度。</param>
    /// <param name="thickness">边框粗细（像素）。</param>
    /// <param name="r">边框色 R。</param>
    /// <param name="g">边框色 G。</param>
    /// <param name="b">边框色 B。</param>
    /// <param name="a">边框色 A。</param>
    public void DrawBorder(
        int x, int y, int w, int h,
        int thickness, byte r, byte g, byte b, byte a)
    {
        if (a == 0 || w <= 0 || h <= 0 || thickness <= 0)
        {
            return;
        }

        thickness = Math.Min(thickness, Math.Min(w, h));

        // 上下两条横边。
        FillRect(x, y, w, thickness, r, g, b, a);
        FillRect(x, y + h - thickness, w, thickness, r, g, b, a);

        // 左右两条竖边（去掉已由横边覆盖的四个角）。
        FillRect(x, y + thickness, thickness, h - (thickness * 2), r, g, b, a);
        FillRect(x + w - thickness, y + thickness, thickness, h - (thickness * 2), r, g, b, a);
    }

    /// <summary>
    /// 画一圈矩形边框，按给定不透明度换算 alpha。
    /// </summary>
    public void DrawBorder(
        int x, int y, int w, int h,
        int thickness, byte r, byte g, byte b, double opacity)
    {
        var a = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        DrawBorder(x, y, w, h, thickness, r, g, b, a);
    }

    /// <summary>
    /// 画一段文字。
    /// </summary>
    /// <param name="text">文本内容。</param>
    /// <param name="x">左上角 X。</param>
    /// <param name="y">左上角 Y。</param>
    /// <param name="fontSize">字号（像素高）。</param>
    /// <param name="bold">是否加粗。</param>
    /// <param name="r">文字色 R。</param>
    /// <param name="g">文字色 G。</param>
    /// <param name="b">文字色 B。</param>
    /// <param name="a">文字色 A。</param>
    /// <param name="outline">是否画描边。</param>
    /// <param name="fontFamily">字体名。</param>
    /// <param name="rightAlign">是否在 <paramref name="maxWidth"/> 内右对齐。</param>
    /// <param name="maxWidth">右对齐时的可用宽度；0 表示不限制。</param>
    public void DrawText(
        string text,
        int x,
        int y,
        int fontSize,
        bool bold,
        byte r,
        byte g,
        byte b,
        byte a,
        bool outline = false,
        string fontFamily = "Microsoft YaHei UI",
        bool rightAlign = false,
        int maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text) || a == 0)
        {
            return;
        }

        var font = CreateFontW(
            -fontSize, 0, 0, 0,
            bold ? FW_BOLD : FW_NORMAL,
            0, 0, 0,
            DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS,
            ANTIALIASED_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE,
            fontFamily);

        if (font == nint.Zero)
        {
            return;
        }

        var oldFont = SelectObject(_memDc, font);

        try
        {
            var flags = DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX;
            if (rightAlign && maxWidth > 0)
            {
                flags |= DT_RIGHT;
            }

            var rect = new RECT
            {
                Left = x,
                Top = y,
                Right = rightAlign && maxWidth > 0 ? x + maxWidth : x + EstimateWidth(text, fontSize) + 8,
                Bottom = y + (int)(fontSize * 1.6),
            };

            // 描边要往四方向各偏 1px，所以实际影响范围比 rect 大一圈 → 快照取扩展后的区域。
            const int pad = 2;
            var sx = Math.Max(0, rect.Left - pad);
            var sy = Math.Max(0, rect.Top - pad);
            var ex = Math.Min(Width, rect.Right + pad);
            var ey = Math.Min(Height, rect.Bottom + pad);

            if (ex <= sx || ey <= sy)
            {
                return;
            }

            // 画之前先给这块区域拍个快照。
            // 画完后逐像素比对「有没有被改变」来判定覆盖度 ——
            // 这样纯黑文字也能正确识别（黑字的 RGB 全是 0，
            // 靠"非 0 即内容"的老办法会把黑字整体判成透明，字就没了）。
            var snapW = ex - sx;
            var snapH = ey - sy;
            var snapshot = new byte[snapW * snapH * 4];
            for (var yy = 0; yy < snapH; yy++)
            {
                Marshal.Copy(
                    _bits + ((sy + yy) * Width + sx) * 4,
                    snapshot, yy * snapW * 4, snapW * 4);
            }

            // 描边：先在四方向偏移画一遍，再画本色文字。
            if (outline)
            {
                var (orb, og, ob, _) = OutlineColor(r, g, b, a);
                _ = SetTextColor(_memDc, Rgb(orb, og, ob));

                foreach (var (dx, dy) in OutlineOffsets)
                {
                    var outlined = new RECT
                    {
                        Left = rect.Left + dx,
                        Top = rect.Top + dy,
                        Right = rect.Right + dx,
                        Bottom = rect.Bottom + dy,
                    };
                    _ = DrawTextW(_memDc, text, text.Length, ref outlined, flags);
                }
            }

            _ = SetTextColor(_memDc, Rgb(r, g, b));
            _ = DrawTextW(_memDc, text, text.Length, ref rect, flags);

            // 比对快照：凡是变动过的像素，其覆盖度视为该文字色的 alpha。
            // 抗锯齿边缘的中间像素也"变动过"，同样会被标成不透明，
            // 边缘会略硬一点，但换来的是黑字可用 —— 这个取舍是值得的。
            var current = new byte[snapW * 4];
            for (var yy = 0; yy < snapH; yy++)
            {
                var rowOffset = ((sy + yy) * Width + sx) * 4;
                Marshal.Copy(_bits + rowOffset, current, 0, current.Length);

                var snapRow = yy * snapW * 4;
                var maskRow = (sy + yy) * Width + sx;

                for (var xx = 0; xx < snapW; xx++)
                {
                    var i = xx * 4;
                    var changed = current[i] != snapshot[snapRow + i] ||
                                  current[i + 1] != snapshot[snapRow + i + 1] ||
                                  current[i + 2] != snapshot[snapRow + i + 2];

                    if (changed)
                    {
                        _coverage[maskRow + xx] = a;
                    }
                }
            }
        }
        finally
        {
            _ = SelectObject(_memDc, oldFont);
            _ = DeleteObject(font);
        }
    }

    private static readonly (int Dx, int Dy)[] OutlineOffsets =
    [
        (-1, 0), (1, 0), (0, -1), (0, 1),
    ];

    private static (byte R, byte G, byte B, byte A) OutlineColor(byte r, byte g, byte b, byte a)
    {
        // 按文字亮度决定描边色：亮字配深描边，暗字配浅描边。
        var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        return luminance > 0.5
            ? ((byte)0, (byte)0, (byte)0, a)
            : ((byte)255, (byte)255, (byte)255, a);
    }

    /// <summary>
    /// 估算一段文字的大致像素宽度（用于右对齐与宽度自适配）。
    /// <para>
    /// 用 GDI 实测比估算准，但实测要建 DC 与字体，过于频繁会拖慢刷新；
    /// 这里按「中文字符约占 1 个字高、西文约占 0.55 个字高」估算，
    /// 对本用途（数字与短标签）足够。
    /// </para>
    /// </summary>
    public static int EstimateWidth(string text, int fontSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        double width = 0;
        foreach (var ch in text)
        {
            // 判断是否宽字符（CJK 及全角符号）。
            width += ch > 0x2E7F ? fontSize : fontSize * 0.56;
        }

        return (int)Math.Ceiling(width);
    }

    /// <summary>
    /// 依据覆盖度掩码把 alpha 通道补齐。
    /// <para>
    /// <b>关键步骤：</b>GDI 的 <c>DrawTextW</c> 写 32 位 DIB 时<b>不写 alpha 通道</b>
    /// （alpha 保持为 0）。分层窗口按 alpha 合成，alpha=0 就等于完全透明 ——
    /// 字会看不见。所以每次绘制后必须按 <see cref="_coverage"/> 补写 alpha。
    /// </para>
    /// <para>
    /// 这里对颜色分量做预处理（乘 alpha），因为
    /// <c>UpdateLayeredWindow</c> 要求 <c>AC_SRC_ALPHA</c> 模式下颜色是预乘的。
    /// </para>
    /// </summary>
    public void UpdateAlpha()
    {
        var count = Width * Height;
        var buffer = new byte[count * 4];
        Marshal.Copy(_bits, buffer, 0, buffer.Length);

        for (var i = 0; i < count; i++)
        {
            var a = _coverage[i];
            var o = i * 4;

            if (a == 0)
            {
                // 没画过：全透明。连 RGB 一起清掉，
                // 避免黑色像素的残留 RGB 影响后续合成。
                buffer[o] = 0;
                buffer[o + 1] = 0;
                buffer[o + 2] = 0;
                buffer[o + 3] = 0;
                continue;
            }

            if (a == 255)
            {
                buffer[o + 3] = 255;
                continue;
            }

            // 半透明：颜色预乘 alpha。
            buffer[o] = (byte)(buffer[o] * a / 255);
            buffer[o + 1] = (byte)(buffer[o + 1] * a / 255);
            buffer[o + 2] = (byte)(buffer[o + 2] * a / 255);
            buffer[o + 3] = a;
        }

        Marshal.Copy(buffer, 0, _bits, buffer.Length);
    }

    /// <summary>把画布内容导出为分层窗口可用的像素数组。</summary>
    public int[] ToBgraArray()
    {
        var count = Width * Height;
        var bytes = new byte[count * 4];
        Marshal.Copy(_bits, bytes, 0, bytes.Length);

        // 转成 int[] 时按小端合成 BGRA。
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var o = i * 4;
            result[i] = bytes[o]
                | (bytes[o + 1] << 8)
                | (bytes[o + 2] << 16)
                | (bytes[o + 3] << 24);
        }

        return result;
    }

    /// <summary>
    /// 把当前画布内容一次性提交到分层窗口。
    /// <para>
    /// 内部会先调 <see cref="UpdateAlpha"/> 补齐 alpha 通道 ——
    /// 忘了这步的话，GDI 画的字会因为 alpha=0 而完全看不见。
    /// </para>
    /// </summary>
    /// <param name="window">目标分层窗口。</param>
    /// <param name="x">窗口在屏幕上的 X。</param>
    /// <param name="y">窗口在屏幕上的 Y。</param>
    public void Present(LayeredWindow window, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(window);

        UpdateAlpha();
        window.Present(ToBgraArray(), Width, Height, x, y);
    }

    private static uint Rgb(byte r, byte g, byte b) =>
        (uint)(r | (g << 8) | (b << 16));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_oldBitmap != nint.Zero && _memDc != nint.Zero)
        {
            _ = SelectObject(_memDc, _oldBitmap);
        }

        if (_bitmap != nint.Zero)
        {
            _ = DeleteObject(_bitmap);
        }

        if (_memDc != nint.Zero)
        {
            _ = DeleteDC(_memDc);
        }

        _memDc = nint.Zero;
        _bitmap = nint.Zero;
        _bits = nint.Zero;
    }

    // ================= P/Invoke =================

    private const int TRANSPARENT = 1;
    private const int FW_NORMAL = 400;
    private const int FW_BOLD = 700;
    private const int DEFAULT_CHARSET = 1;
    private const int OUT_DEFAULT_PRECIS = 0;
    private const int CLIP_DEFAULT_PRECIS = 0;
    private const int ANTIALIASED_QUALITY = 4;
    private const int DEFAULT_PITCH = 0;
    private const int FF_DONTCARE = 0;

    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_RIGHT = 0x00000002;
    private const uint DT_NOPREFIX = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DrawTextW(nint hdc, string lpchText, int cchText, ref RECT lprc, uint format);
}
