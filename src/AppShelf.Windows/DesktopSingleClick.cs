using System.Runtime.InteropServices;
using System.Windows.Threading;
using Accessibility;

namespace AppShelf.Windows;

public sealed class DesktopSingleClick : IDisposable
{
    private readonly string dataPath;
    private readonly Action<Guid> open;
    private readonly Dispatcher dispatcher;
    private readonly Action<InputHooks.MouseEvent> listener;
    private readonly StaWorker accessibility = new("AppShelf desktop accessibility");
    private readonly FileSystemWatcher? watcher;
    private readonly DispatcherTimer refreshTimer;
    private volatile Dictionary<string, Guid> shortcuts = new(StringComparer.CurrentCultureIgnoreCase);
    // Last left press, seen on the hook thread only.
    private Point press;
    private bool pressedOnDesktop;
    private Guid lastFolder;
    private DateTime lastClick;
    private volatile bool disposed;
    public bool Installed => !disposed && InputHooks.Shared.Installed;
    public int ShortcutCount => shortcuts.Values.Distinct().Count();
    /// <summary>Receives failures of background shortcut scans, which otherwise have no caller to report to.</summary>
    public Action<Exception>? Error { get; set; }

    public DesktopSingleClick(string dataPath, Action<Guid> open)
    {
        this.dataPath = dataPath; this.open = open;
        dispatcher = Dispatcher.CurrentDispatcher;
        listener = MouseMessage;
        InputHooks.Shared.Subscribe(listener);
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        refreshTimer.Tick += async (_, _) => { refreshTimer.Stop(); await RefreshAsync(); };
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (Directory.Exists(desktop))
        {
            watcher = new FileSystemWatcher(desktop, "*.lnk") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            watcher.Created += Changed; watcher.Changed += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
            watcher.EnableRaisingEvents = true;
        }
        _ = RefreshAsync();
    }
    private void Changed(object sender, FileSystemEventArgs e) => dispatcher.BeginInvoke(new Action(() =>
    { if (!disposed) { refreshTimer.Stop(); refreshTimer.Start(); } }));
    public async Task RefreshAsync()
    {
        try
        {
            var map = await ShellIntegration.RunSta(() =>
            {
                var result = new Dictionary<string, Guid>(StringComparer.CurrentCultureIgnoreCase);
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!Directory.Exists(desktop)) return result;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { Error?.Invoke(ex); }
    }
    private void MouseMessage(InputHooks.MouseEvent input)
    {
        // Runs on the hook thread: only cheap window checks here, the rest goes to the dispatcher.
        if (disposed || shortcuts.Count == 0 || input.Message is not (0x201 or 0x202)) return;
        var point = new Point { X = input.X, Y = input.Y };
        var window = WindowFromPoint(point);
        var root = GetAncestor(window, 2);
        var onDesktop = ClassName(window) == "SysListView32" && ClassName(root) is "Progman" or "WorkerW";
        if (input.Message == 0x201) { press = point; pressedOnDesktop = onDesktop; return; }
        // Dragging the folder icon around (or dropping something on it) ends with a release over the icon too;
        // only a press and release in place is a click.
        if (!onDesktop || !pressedOnDesktop || !IsClick(press.X, press.Y, point.X, point.Y, GetSystemMetrics(68), GetSystemMetrics(69))) return;
        pressedOnDesktop = false;
        dispatcher.BeginInvoke(new Action(() => _ = HandleClickAsync(point, root)));
    }

    /// <summary>A release within the system drag threshold of the press is a click, anything farther is a drag.</summary>
    public static bool IsClick(int pressX, int pressY, int releaseX, int releaseY, int dragWidth, int dragHeight) =>
        Math.Abs(releaseX - pressX) <= Math.Max(1, dragWidth / 2) && Math.Abs(releaseY - pressY) <= Math.Max(1, dragHeight / 2);
    private async Task HandleClickAsync(Point point, IntPtr desktop)
    {
        try
        {
            var name = await accessibility.Run(() => ItemName(point)).WaitAsync(TimeSpan.FromMilliseconds(750));
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
        if (disposed) return;
        disposed = true; InputHooks.Shared.Unsubscribe(listener);
        watcher?.Dispose(); refreshTimer.Stop(); accessibility.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(Point point, [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
}
