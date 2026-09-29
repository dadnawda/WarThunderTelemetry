using System.Runtime.InteropServices;

namespace WarThunderTelemetry.App.Native;

/// <summary>
/// 原生互操作共用的结构体与常量。
/// <para>
/// 集中放一处，避免在多个类里重复定义导致字段访问不一致
/// （比如 <c>BITMAPINFO</c> 嵌了 header，字段必须经由 <c>bmiHeader</c> 访问）。
/// </para>
/// </summary>
internal static class NativeShared
{
    /// <summary>32 位 DIB 的位图信息头。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    /// <summary>位图信息（32 位真彩下不需要调色板）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public int bmiColors;
    }

    /// <summary>构造一个自上而下的 32 位 BGRA 位图信息。</summary>
    internal static BitmapInfo CreateTopDown32(int width, int height) => new()
    {
        bmiHeader = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height,   // 负数 = 自上而下，与我们的像素顺序一致
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,    // BI_RGB
        },
    };
}
