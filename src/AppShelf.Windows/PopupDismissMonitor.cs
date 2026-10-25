using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AppShelf.Windows;

/// <summary>Dismisses a popup even when Windows did not give it keyboard focus.</summary>
public sealed class PopupDismissMonitor : IDisposable
{
    public enum Reason { OutsideClick, ForegroundChanged }
    private readonly IntPtr window;
    private readonly Dispatcher dispatcher;
    private readonly Action<Reason, IntPtr> dismiss;
    private readonly Action<InputHooks.MouseEvent> mouseListener;
    private readonly Action foregroundListener;
    private IntPtr lastForeground;
    private volatile bool disposed;

    public PopupDismissMonitor(IntPtr window, Action dismiss) : this(window, (_, _) => dismiss()) { }

    /// <param name="dismiss">Runs on the creating dispatcher with the reason and the window that took the click or focus.</param>
    public PopupDismissMonitor(IntPtr window, Action<Reason, IntPtr> dismiss)
    {
        this.window = window;
        this.dismiss = dismiss;
        dispatcher = Dispatcher.CurrentDispatcher;
        lastForeground = GetForegroundWindow();
        mouseListener = MouseMessage;
        foregroundListener = ForegroundChanged;
        InputHooks.Shared.Subscribe(mouseListener);
        InputHooks.Shared.Subscribe(foregroundListener);
    }

    private bool BelongsToPopup(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;
        return target == window || GetAncestor(target, 2) == window || GetAncestor(target, 3) == window;
    }

    private void MouseMessage(InputHooks.MouseEvent input)
    {
        if (disposed || input.Message == 0x202) return;
        var target = WindowFromPoint(new Point { X = input.X, Y = input.Y });
        if (BelongsToPopup(target)) return;
        dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (!disposed) dismiss(Reason.OutsideClick, target); }));
    }

    private void ForegroundChanged()
    {
        if (disposed) return;
        // Out-of-context events can arrive after a newer activation. Inspect the current
        // foreground and ignore events left over from opening the popup itself.
        dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (disposed) return;
            var active = GetForegroundWindow();
            if (active == lastForeground) return;
            lastForeground = active;
            if (!BelongsToPopup(active)) dismiss(Reason.ForegroundChanged, active);
        }));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        InputHooks.Shared.Unsubscribe(mouseListener);
        InputHooks.Shared.Unsubscribe(foregroundListener);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
