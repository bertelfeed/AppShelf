using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AppShelf.Windows;

/// <summary>Dismisses a popup even when Windows did not give it keyboard focus.</summary>
public sealed class PopupDismissMonitor : IDisposable
{
    private readonly IntPtr window;
    private readonly Dispatcher dispatcher;
    private readonly Action dismiss;
    private readonly MouseProcedure mouseCallback;
    private readonly ForegroundProcedure foregroundCallback;
    private IntPtr mouseHook, foregroundHook;
    private IntPtr lastForeground;
    private bool disposed;

    public PopupDismissMonitor(IntPtr window, Action dismiss)
    {
        this.window = window;
        this.dismiss = dismiss;
        dispatcher = Dispatcher.CurrentDispatcher;
        mouseCallback = MouseMessage;
        foregroundCallback = ForegroundChanged;
        mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0);
        if (foregroundHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error);
        }
        lastForeground = GetForegroundWindow();
    }

    private bool BelongsToPopup(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;
        return target == window || GetAncestor(target, 2) == window || GetAncestor(target, 3) == window;
    }

    private void QueueDismiss() => dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    {
        if (!disposed) dismiss();
    }));

    private IntPtr MouseMessage(int code, IntPtr message, IntPtr information)
    {
        // Never consume input: the clicked application or desktop still receives the click.
        if (code >= 0 && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            var point = Marshal.PtrToStructure<MouseInfo>(information).Point;
            if (!BelongsToPopup(WindowFromPoint(point))) QueueDismiss();
        }
        return CallNextHookEx(mouseHook, code, message, information);
    }

    private void ForegroundChanged(IntPtr hook, uint eventType, IntPtr target, int objectId, int childId, uint thread, uint time)
    {
        // Out-of-context events can arrive after a newer activation. Inspect the current
        // foreground and ignore events left over from opening the popup itself.
        dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (disposed) return;
            var active = GetForegroundWindow();
            if (active == lastForeground) return;
            lastForeground = active;
            if (!BelongsToPopup(active)) dismiss();
        }));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInfo { public Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr MouseProcedure(int code, IntPtr message, IntPtr information);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ForegroundProcedure(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, MouseProcedure callback, IntPtr module, uint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr information);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, ForegroundProcedure callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
