using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace AppShelf.Windows;

/// <summary>
/// Custom hover thumbnail for a taskbar button. The folder window is an invisible 1×1 window,
/// so its live thumbnail would be empty; DWM instead asks for a bitmap that we draw ourselves.
/// </summary>
public static class TaskbarThumbnail
{
    public const int RequestThumbnail = 0x0323;   // WM_DWMSENDICONICTHUMBNAIL
    public const int RequestLivePreview = 0x0326; // WM_DWMSENDICONICLIVEPREVIEWBITMAP

    public static void Enable(IntPtr window)
    {
        var on = 1;
        DwmSetWindowAttribute(window, 7, ref on, sizeof(int));  // DWMWA_FORCE_ICONIC_REPRESENTATION
        DwmSetWindowAttribute(window, 10, ref on, sizeof(int)); // DWMWA_HAS_ICONIC_BITMAP
        DwmSetWindowAttribute(window, 11, ref on, sizeof(int)); // DWMWA_DISALLOW_PEEK: nothing to peek at
    }

    /// <summary>Maximum thumbnail size that DWM sent with the request.</summary>
    public static (int Width, int Height) RequestedSize(IntPtr lParam) =>
        ((int)((lParam.ToInt64() >> 16) & 0xFFFF), (int)(lParam.ToInt64() & 0xFFFF));

    public static void Set(IntPtr window, BitmapSource image)
    {
        var width = image.PixelWidth; var height = image.PixelHeight;
        var header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) return;
        try
        {
            // Pbgra32 is the premultiplied top-down layout DWM expects.
            image.CopyPixels(System.Windows.Int32Rect.Empty, bits, width * height * 4, width * 4);
            DwmSetIconicThumbnail(window, bitmap, 0);
        }
        finally { DeleteObject(bitmap); }
    }

    /// <summary>Asks DWM to request a new thumbnail, e.g. after the folder's icons changed.</summary>
    public static void Invalidate(IntPtr window) => DwmInvalidateIconicBitmaps(window);

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr bitmap, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
