using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AppShelf.Windows;

/// <summary>
/// Decides when an outside click closes the menu. A press outside only arms the close; the release
/// decides. That keeps the menu open while a shortcut is dragged from the desktop and dropped into it.
/// </summary>
public sealed class DismissGate
{
    private bool armed;
    private PopupDismissMonitor.Reason reason;
    private IntPtr target;
    private DateTime quietUntil;

    /// <summary>A press outside the menu (or focus lost while a button is held): close on release.</summary>
    public void Arm(PopupDismissMonitor.Reason why, IntPtr window) { armed = true; reason = why; target = window; }

    /// <summary>A drag entered the menu or ended in it: the pending close is cancelled for a moment.</summary>
    public void Cancel(DateTime now) { armed = false; quietUntil = now.AddMilliseconds(400); }

    /// <summary>Focus moved elsewhere. Closes at once unless a button is held or a drop has just happened.</summary>
    public bool ForegroundLost(IntPtr window, bool buttonDown, DateTime now, out PopupDismissMonitor.Reason why, out IntPtr at)
    {
        why = PopupDismissMonitor.Reason.ForegroundChanged; at = window;
        if (now < quietUntil) return false;
        if (buttonDown) { if (!armed) Arm(why, window); return false; }
        return true;
    }

    /// <summary>A button was released: closes when the press was outside and the release is not inside the menu.</summary>
    public bool Release(bool insideMenu, out PopupDismissMonitor.Reason why, out IntPtr at)
    {
        why = reason; at = target;
        if (!armed) return false;
        armed = false;
        return !insideMenu;
    }
}

/// <summary>Dismisses a popup even when Windows did not give it keyboard focus.</summary>
public sealed class PopupDismissMonitor : IDisposable
{
    public enum Reason { OutsideClick, ForegroundChanged }
    private readonly IntPtr window;
    private readonly Dispatcher dispatcher;
    private readonly Action<Reason, IntPtr> dismiss;
    private readonly Action<InputHooks.MouseEvent> mouseListener;
    private readonly Action foregroundListener;
    private readonly DismissGate gate = new();
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

    /// <summary>A drag from outside reached the menu; it must stay open for the drop.</summary>
    public void CancelPendingClose()
    {
        gate.Cancel(DateTime.UtcNow);
        lastForeground = GetForegroundWindow();
    }

    private bool BelongsToPopup(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;
        return target == window || GetAncestor(target, 2) == window || GetAncestor(target, 3) == window;
    }

    private void MouseMessage(InputHooks.MouseEvent input)
    {
        if (disposed) return;
        var target = WindowFromPoint(new Point { X = input.X, Y = input.Y });
        var inside = BelongsToPopup(target);
        var press = input.Message is 0x201 or 0x204 or 0x207 or 0x20B;
        dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (disposed) return;
            if (press) { if (!inside) gate.Arm(Reason.OutsideClick, target); }
            else if (gate.Release(inside, out var reason, out var at)) dismiss(reason, at);
        }));
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
            if (BelongsToPopup(active)) return;
            if (gate.ForegroundLost(active, ButtonDown(), DateTime.UtcNow, out var reason, out var at)) dismiss(reason, at);
        }));
    }

    private static bool ButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

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
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
