using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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
    public static void StylePopup(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var corner = 2;
        DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        var backdrop = 3; // Desktop Acrylic, follows the system transparency preference.
        DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
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
}
