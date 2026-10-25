using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AppShelf.Windows;

/// <summary>
/// Owns the process-wide low-level mouse hook and the foreground WinEvent hook on a dedicated thread.
/// Windows silently removes a low-level hook whose thread answers too slowly, so the hooks never share
/// the UI thread. Listeners run on the hook thread and must only do quick checks before posting work.
/// </summary>
public sealed class InputHooks
{
    private static readonly Lazy<InputHooks> shared = new(() => new InputHooks());
    public static InputHooks Shared => shared.Value;

    public readonly record struct MouseEvent(int Message, int X, int Y);
    private const int ReinstallMessage = 0x8001; // WM_APP + 1
    private const int TimerMessage = 0x113;
    private readonly MouseProcedure mouseCallback;
    private readonly ForegroundProcedure foregroundCallback;
    private Action<MouseEvent>[] mouseListeners = [];
    private Action[] foregroundListeners = [];
    private readonly object gate = new();
    private IntPtr mouseHook, foregroundHook;
    private uint threadId;
    public bool Installed => mouseHook != IntPtr.Zero && foregroundHook != IntPtr.Zero;

    private InputHooks()
    {
        mouseCallback = MouseMessage;
        foregroundCallback = ForegroundChanged;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            try { InstallMouse(); InstallForeground(); }
            catch (Exception ex) { failure = ex; }
            ready.Set();
            if (failure is null) Run();
        }) { IsBackground = true, Name = "AppShelf input hooks" };
        thread.Start();
        ready.Wait();
        if (failure is not null) throw failure;
        // After sleep or a locked session the hook may have been dropped without notice.
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) Reinstall(); };
        SystemEvents.SessionSwitch += (_, e) => { if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect) Reinstall(); };
    }

    public void Subscribe(Action<MouseEvent> listener) { lock (gate) mouseListeners = [.. mouseListeners, listener]; }
    public void Unsubscribe(Action<MouseEvent> listener) { lock (gate) mouseListeners = mouseListeners.Where(x => x != listener).ToArray(); }
    public void Subscribe(Action listener) { lock (gate) foregroundListeners = [.. foregroundListeners, listener]; }
    public void Unsubscribe(Action listener) { lock (gate) foregroundListeners = foregroundListeners.Where(x => x != listener).ToArray(); }
    public void Reinstall() => PostThreadMessage(threadId, ReinstallMessage, IntPtr.Zero, IntPtr.Zero);

    private void Run()
    {
        // A periodic reinstall also covers drops that no system event announces.
        SetTimer(IntPtr.Zero, IntPtr.Zero, 5 * 60 * 1000, IntPtr.Zero);
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message is ReinstallMessage or TimerMessage && message.Window == IntPtr.Zero)
            {
                try { InstallMouse(); } catch (Win32Exception) { }
                continue;
            }
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private void InstallMouse()
    {
        var next = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
        if (next == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var previous = mouseHook;
        mouseHook = next;
        if (previous != IntPtr.Zero) UnhookWindowsHookEx(previous);
    }

    private void InstallForeground()
    {
        foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0);
        if (foregroundHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr MouseMessage(int code, IntPtr message, IntPtr information)
    {
        // Never consume input: the clicked application or desktop still receives the click.
        // Presses and releases of the left, right, middle and extra buttons.
        if (code >= 0 && message.ToInt64() is 0x201 or 0x202 or 0x204 or 0x205 or 0x207 or 0x208 or 0x20B or 0x20C)
        {
            var point = Marshal.PtrToStructure<MouseInfo>(information).Point;
            var data = new MouseEvent((int)message.ToInt64(), point.X, point.Y);
            foreach (var listener in mouseListeners)
                try { listener(data); } catch { }
        }
        return CallNextHookEx(mouseHook, code, message, information);
    }

    private void ForegroundChanged(IntPtr hook, uint eventType, IntPtr target, int objectId, int childId, uint thread, uint time)
    {
        foreach (var listener in foregroundListeners)
            try { listener(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInfo { public Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public IntPtr Window; public int Message; public IntPtr W, L; public uint Time; public Point Point; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr MouseProcedure(int code, IntPtr message, IntPtr information);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ForegroundProcedure(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, MouseProcedure callback, IntPtr module, uint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr information);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, ForegroundProcedure callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr window, IntPtr id, uint elapse, IntPtr callback);
}
