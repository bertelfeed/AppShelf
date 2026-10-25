using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShelf.Windows;

public sealed class IconService : IDisposable
{
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Action> jobs = new(2048);
    private readonly string cachePath;
    public IconService(string directory)
    {
        cachePath = Path.Combine(directory, "icons-v2");
        Directory.CreateDirectory(cachePath);
        var worker = new Thread(() => { foreach (var action in jobs.GetConsumingEnumerable()) action(); })
            { IsBackground = true, Name = "AppShelf shell icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public Task<ImageSource?> GetAsync(string target) => cache.GetOrAdd(target, path =>
    {
        var result = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!jobs.TryAdd(() =>
        {
            try { result.TrySetResult(ReadIcon(path)); }
            catch { result.TrySetResult(null); }
        })) result.TrySetResult(null);
        return result.Task;
    });

    private ImageSource? ReadIcon(string target)
    {
        var expanded = Environment.ExpandEnvironmentVariables(target);
        var stamp = File.Exists(expanded) ? File.GetLastWriteTimeUtc(expanded).Ticks.ToString() : "";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target + "|" + stamp)));
        var disk = Path.Combine(cachePath, key + ".png");
        if (File.Exists(disk))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(disk); bitmap.EndInit(); bitmap.Freeze();
                return bitmap;
            }
            catch { }
        }
        if (new[] { ".png", ".jpg", ".jpeg", ".ico", ".bmp" }.Contains(Path.GetExtension(expanded).ToLowerInvariant()) && File.Exists(expanded))
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 256; bitmap.UriSource = new Uri(Path.GetFullPath(expanded)); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        var large = TryLargeImage(expanded);
        if (large is not null)
        {
            try { using var stream = File.Create(disk); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(large)); encoder.Save(stream); } catch (IOException) { }
            return large;
        }
        IntPtr pidl = IntPtr.Zero;
        try
        {
            var path = expanded;
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") path = ".html";
            uint attributes = 0;
            var info = new ShellFileInfo();
            uint flags = 0x100; // SHGFI_ICON, large icon, no link overlay
            IntPtr success;
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attributes) == 0 && pidl != IntPtr.Zero)
                success = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags | 0x8);
            else success = SHGetFileInfoPath(path, 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags | 0x10);
            if (success == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
                image.Freeze();
                try { using var stream = File.Create(disk); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream); } catch (IOException) { }
                return image;
            }
            finally { DestroyIcon(info.Icon); }
        }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }
    public void Dispose() => jobs.CompleteAdding();

    private static BitmapSource? TryLargeImage(string path)
    {
        IntPtr bitmap = IntPtr.Zero, dc = IntPtr.Zero;
        IImageFactory? factory = null;
        try
        {
            var iid = typeof(IImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) < 0) return null;
            if (factory.GetImage(new NativeSize { Width = 256, Height = 256 }, 0x105, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
            if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var source) == 0 || source.Width <= 0 || source.Height <= 0 || source.Width > 1024 || source.Height > 1024) return null;
            var info = new BitmapInfo { Size = 40, Width = source.Width, Height = -source.Height, Planes = 1, BitCount = 32 };
            var stride = source.Width * 4;
            var pixels = new byte[stride * source.Height];
            dc = CreateCompatibleDC(IntPtr.Zero);
            if (GetDIBits(dc, bitmap, 0, (uint)source.Height, pixels, ref info, 0) == 0) return null;
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var result = BitmapSource.Create(source.Width, source.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze(); return result;
        }
        catch (COMException) { return null; }
        finally
        {
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory is not null) Marshal.FinalReleaseComObject(factory);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
    }
    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageFactory { [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageFactory factory);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr obj, int count, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellFileInfo
    {
        public IntPtr Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint attributes, out uint outAttributes);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")] private static extern IntPtr SHGetFileInfoPath(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
