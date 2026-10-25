using System.Runtime.InteropServices;
using System.Windows.Threading;
using Accessibility;

namespace AppShelf.Windows;

public sealed class DesktopSingleClick : IDisposable
{
    private readonly string dataPath;
    private readonly Action<Guid> open;
    private readonly Dispatcher dispatcher;
    private readonly MouseProcedure callback;
    private readonly FileSystemWatcher watcher;
    private readonly DispatcherTimer refreshTimer;
    private Dictionary<string, Guid> shortcuts = new(StringComparer.CurrentCultureIgnoreCase);
    private IntPtr hook;
    private Guid lastFolder;
    private DateTime lastClick;
    private bool disposed;
    public bool Installed => hook != IntPtr.Zero;
    public int ShortcutCount => shortcuts.Values.Distinct().Count();

    public DesktopSingleClick(string dataPath, Action<Guid> open)
    {
        this.dataPath = dataPath; this.open = open;
        dispatcher = Dispatcher.CurrentDispatcher;
        callback = MouseMessage;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        refreshTimer.Tick += async (_, _) => { refreshTimer.Stop(); await RefreshAsync(); };
        watcher = new FileSystemWatcher(desktop, "*.lnk") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        watcher.Created += Changed; watcher.Changed += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
        watcher.EnableRaisingEvents = true;
        _ = RefreshAsync();
    }
    private void Changed(object sender, FileSystemEventArgs e) => dispatcher.BeginInvoke(new Action(() =>
    { if (!disposed) { refreshTimer.Stop(); refreshTimer.Start(); } }));
    public async Task RefreshAsync()
    {
        var map = await ShellIntegration.RunSta(() =>
        {
            var result = new Dictionary<string, Guid>(StringComparer.CurrentCultureIgnoreCase);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            foreach (var file in Directory.EnumerateFiles(desktop, "*.lnk"))
            {
                var link = ShellIntegration.ReadShortcut(file);
                if (link is null || !link.AppId.StartsWith("AppShelf.Folder.", StringComparison.Ordinal) ||
                    !Guid.TryParse(link.AppId["AppShelf.Folder.".Length..], out var id) ||
                    !ShellIntegration.IsFolderShortcut(link, id, dataPath)) continue;
                result[Path.GetFileNameWithoutExtension(file)] = id;
                result[Path.GetFileName(file)] = id;
            }
            return result;
        });
        if (!disposed) shortcuts = map;
    }
    private IntPtr MouseMessage(int code, IntPtr message, IntPtr information)
    {
        try
        {
            if (code >= 0 && message.ToInt64() == 0x202 && shortcuts.Count > 0)
            {
                var point = Marshal.PtrToStructure<MouseInfo>(information).Point;
                var window = WindowFromPoint(point);
                var root = GetAncestor(window, 2);
                if (ClassName(window) == "SysListView32" && ClassName(root) is "Progman" or "WorkerW")
                    dispatcher.BeginInvoke(new Action(() => _ = HandleClickAsync(point, root)));
            }
        }
        catch { }
        return CallNextHookEx(hook, code, message, information);
    }
    private async Task HandleClickAsync(Point point, IntPtr desktop)
    {
        try
        {
            var name = await ShellIntegration.RunSta(() => ItemName(point)).WaitAsync(TimeSpan.FromMilliseconds(750));
            if (disposed || name is null || !shortcuts.TryGetValue(name, out var id)) return;
            var now = DateTime.UtcNow;
            if (id == lastFolder && now - lastClick < TimeSpan.FromMilliseconds(GetDoubleClickTime() + 80)) return;
            lastFolder = id; lastClick = now;
            // Let Explorer finish selecting its own item before activating the folder.
            await Task.Delay(65);
            var foreground = GetForegroundWindow();
            if (!disposed && (foreground == desktop || ClassName(foreground) is "Progman" or "WorkerW")) open(id);
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException) { }
    }
    private static string? ItemName(Point point)
    {
        IAccessible? accessible = null;
        try
        {
            if (AccessibleObjectFromPoint(point, out accessible, out var child) < 0 || accessible is null) return null;
            var role = accessible.get_accRole(child);
            return role is int number && number == 34 ? accessible.get_accName(child) : null;
        }
        catch (COMException) { return null; }
        finally { if (accessible is not null && Marshal.IsComObject(accessible)) Marshal.FinalReleaseComObject(accessible); }
    }
    private static string ClassName(IntPtr window)
    {
        var buffer = new System.Text.StringBuilder(128);
        GetClassName(window, buffer, buffer.Capacity); return buffer.ToString();
    }
    public void Dispose()
    {
        disposed = true; watcher.Dispose(); refreshTimer.Stop();
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInfo { public Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr MouseProcedure(int code, IntPtr message, IntPtr information);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, MouseProcedure procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(Point point, [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
}
