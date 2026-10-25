using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AppShelf.App;

public sealed class TaskbarFolders : IDisposable
{
    private readonly Dictionary<Guid, FolderTaskbarWindow> windows = [];
    private readonly ShelfSession session;
    public TaskbarFolders(ShelfSession session)
    {
        this.session = session;
        session.Changed += Refresh;
        Refresh();
    }
    public void Refresh()
    {
        var groups = ShelfTree.Walk(session.Configuration.Items).Where(x => x.IsFolder && x.Settings.ShowOnTaskbar).ToList();
        var ids = groups.Select(x => x.Id).ToHashSet();
        foreach (var id in windows.Keys.Where(id => !ids.Contains(id)).ToList())
        { windows[id].Remove(); windows.Remove(id); }
        foreach (var group in groups)
        {
            if (!windows.TryGetValue(group.Id, out var window))
            {
                window = new FolderTaskbarWindow(group.Id);
                windows.Add(group.Id, window);
                window.Show();
                window.Start();
            }
            _ = window.RefreshAsync();
        }
    }
    public void Dispose()
    {
        session.Changed -= Refresh;
        foreach (var window in windows.Values) window.Remove();
        windows.Clear();
    }
}

public sealed class FolderTaskbarWindow : Window
{
    private readonly Guid folderId;
    private bool ready, removing;
    private DateTime lastActivation;
    private long revision;
    public FolderTaskbarWindow(Guid folderId)
    {
        this.folderId = folderId;
        var folder = ShelfTree.Find(App.Current.Session.Configuration, folderId)!;
        Title = folder.Name;
        ShowInTaskbar = true;
        ShowActivated = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Width = 1; Height = 1; Left = -32000; Top = -32000;
        Opacity = 0;
        WindowState = WindowState.Minimized;
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this);
            ShellIntegration.SetWindowAppId(source.Handle, folderId);
            source.AddHook(HandleMessage);
        };
        Activated += (_, _) => ActivateFolder();
        Closing += OnClosing;
    }
    public void Start()
    {
        TaskbarTabs.Add(new WindowInteropHelper(this).Handle);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ready = true));
    }
    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (ready && message == 0x112 && (w.ToInt64() & 0xFFF0) == 0xF020)
        {
            App.Current.HideFolder(folderId); handled = true; return IntPtr.Zero;
        }
        if (ready && message == 0x112 && (w.ToInt64() & 0xFFF0) is 0xF120 or 0xF030)
        {
            ActivateFolder(); handled = true; return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }
    private void ActivateFolder()
    {
        if (!ready || removing || DateTime.UtcNow - lastActivation < TimeSpan.FromMilliseconds(350)) return;
        lastActivation = DateTime.UtcNow;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!removing) App.Current.ShowFolder(folderId);
        }));
    }
    public async Task RefreshAsync()
    {
        var currentRevision = ++revision;
        try
        {
            var folder = ShelfTree.Find(App.Current.Session.Configuration, folderId);
            if (folder is null || removing) return;
            Title = folder.Name;
            var art = await FolderIconWriter.PrepareAsync(folder);
            if (removing || currentRevision != revision) return;
            using var stream = File.OpenRead(art.Path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Icon = decoder.Frames.OrderByDescending(x => x.PixelWidth).First();
            ShellIntegration.SetFolderWindowIdentity(new WindowInteropHelper(this).Handle, folder, App.Current.Session.Store.DirectoryPath, art.Path);
        }
        catch (Exception ex) { App.Current.Log(ex); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (removing || App.Current.IsExiting) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (removing) return;
            App.Current.Session.Change(config =>
            { if (ShelfTree.Find(config, folderId) is { } folder) folder.Settings.ShowOnTaskbar = false; });
        }));
    }
    public void Remove() { removing = true; Close(); }
}
