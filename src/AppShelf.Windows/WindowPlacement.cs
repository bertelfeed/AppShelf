using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AppShelf.Windows;

public static class WindowPlacement
{
    public static void FitAtCurrentPosition(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;
        var scale = Math.Max(1, GetDpiForWindow(hwnd) / 96d);
        var width = Math.Min((int)(window.Width * scale), info.Work.Right - info.Work.Left - 24);
        var height = Math.Min((int)(window.Height * scale), info.Work.Bottom - info.Work.Top - 24);
        var x = Math.Clamp(rect.Left, info.Work.Left + 12, Math.Max(info.Work.Left + 12, info.Work.Right - width - 12));
        var y = Math.Clamp(rect.Top, info.Work.Top + 12, Math.Max(info.Work.Top + 12, info.Work.Bottom - height - 12));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x4 | 0x10);
    }
    public static void ActivateFromDesktop(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try { SetForegroundWindow(hwnd); SetFocus(hwnd); }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
    }
    /// <summary>Rounded corners and acrylic. Returns false when the content has to stay opaque.</summary>
    public static bool StylePopup(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var corner = 2;
        DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        return EnableBackdrop(window, 3, dark); // Acrylic, follows the system transparency preference.
    }

    /// <summary>
    /// Shows a DWM system backdrop (2 Mica, 3 Acrylic) through the window. WPF paints an opaque
    /// surface by default, so the composition background is cleared and the frame is extended.
    /// </summary>
    public static bool EnableBackdrop(Window window, int type, bool dark)
    {
        if (Environment.OSVersion.Version.Build < 22621 || SystemParameters.HighContrast) return false;
        var handle = new WindowInteropHelper(window).Handle;
        if (HwndSource.FromHwnd(handle) is not { CompositionTarget: { } target }) return false;
        SetDarkFrame(window, dark);
        if (DwmSetWindowAttribute(handle, 38, ref type, sizeof(int)) != 0) return false;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(handle, ref margins) != 0) return false;
        target.BackgroundColor = Colors.Transparent;
        return true;
    }

    /// <summary>Tints the backdrop for the window's own theme rather than the system one.</summary>
    public static void SetDarkFrame(Window window, bool dark)
    {
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref value, sizeof(int));
    }

    public static IntPtr ForegroundWindow() => GetForegroundWindow();
    public static int DoubleClickTime => (int)GetDoubleClickTime();

    /// <summary>The desktop icon view or its Progman/WorkerW host.</summary>
    public static bool IsDesktop(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        return ClassName(window) is "SysListView32" or "SHELLDLL_DefView" or "Progman" or "WorkerW"
            && ClassName(GetAncestor(window, 2)) is "Progman" or "WorkerW";
    }
    private static string ClassName(IntPtr window)
    {
        var buffer = new System.Text.StringBuilder(128);
        GetClassName(window, buffer, buffer.Capacity); return buffer.ToString();
    }

    public static void NearPointer(Window window, bool fromDesktop = false)
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        var scale = GetDpiForWindow(hwnd) / 96d;
        if (scale <= 0) scale = 1;
        var width = (int)(window.Width * scale);
        var height = (int)(window.Height * scale);
        var margin = (int)(12 * scale);
        var work = info.Work;
        width = Math.Min(width, work.Right - work.Left - 2 * margin);
        height = Math.Min(height, work.Bottom - work.Top - 2 * margin);
        var x = Math.Clamp(cursor.X - width / 2, work.Left + margin, Math.Max(work.Left + margin, work.Right - width - margin));
        // Prefer the taskbar edge of the monitor; place adjacent to the pointer for side taskbars.
        var y = work.Bottom - height - margin;
        if (fromDesktop)
        {
            y = cursor.Y + (int)(24 * scale);
            if (y + height > work.Bottom - margin) y = cursor.Y - height - (int)(24 * scale);
            y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - height - margin));
        }
        else if (cursor.Y < work.Top + 50 * scale) y = work.Top + margin;
        if (!fromDesktop && (cursor.X < work.Left || cursor.X > work.Right))
            y = Math.Clamp(cursor.Y - height / 2, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - height - margin));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x4 | 0x10);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
}
