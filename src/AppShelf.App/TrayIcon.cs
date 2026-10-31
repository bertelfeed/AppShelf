using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AppShelf.App;

public sealed class TrayIcon : IDisposable
{
    private readonly HwndSource source;
    private NotifyData data;
    private readonly Action open;
    private readonly Func<ContextMenu> menu;
    private readonly uint taskbarCreated;
    public TrayIcon(Action open, Func<ContextMenu> menu)
    {
        this.open = open; this.menu = menu;
        source = new HwndSource(new HwndSourceParameters("AppShelf.Tray") { Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(HandleMessage);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // The tray shows the small-icon size (16 px at 100 %); load that frame instead of shrinking the 32 px one.
        var small = GetSystemMetrics(49);
        var icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "AppShelf.ico"), 1, small, small, 0x10);
        data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = source.Handle, Id = 1, Flags = 7, Callback = 0x8001, Icon = icon, Tip = L.Get("AppShelfAppFolders") };
        Shell_NotifyIcon(0, ref data);
    }
    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == taskbarCreated) Shell_NotifyIcon(0, ref data);
        if (message == 0x8001)
        {
            if (lParam.ToInt32() == 0x202) open();
            if (lParam.ToInt32() == 0x205)
            {
                SetForegroundWindow(source.Handle);
                var context = menu();
                context.Closed += (_, _) => PostMessage(source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
                context.IsOpen = true;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }
    public void RefreshLanguage()
    {
        data.Tip = L.Get("AppShelfAppFolders");
        Shell_NotifyIcon(1, ref data);
    }
    public void Dispose() { Shell_NotifyIcon(2, ref data); if (data.Icon != IntPtr.Zero) DestroyIcon(data.Icon); source.Dispose(); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
}
