using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
    /// <summary>Windows allows at most seven buttons under a taskbar thumbnail.</summary>
    public const int MaxLaunchButtons = 7;
    private const int MaxThumbnailIcons = 12;
    private readonly Guid folderId;
    private bool ready, removing;
    private DateTime lastActivation;
    private long revision;
    private IReadOnlyList<ImageSource?> thumbnailIcons = [];
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
            TaskbarThumbnail.Enable(source.Handle);
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
        if (message == TaskbarThumbnail.RequestThumbnail)
        {
            var (width, height) = TaskbarThumbnail.RequestedSize(l);
            try { TaskbarThumbnail.Set(hwnd, RenderThumbnail(width, height)); }
            catch (Exception ex) { App.Current.Log(ex); }
            handled = true; return IntPtr.Zero;
        }
        if (message == TaskbarThumbnail.RequestLivePreview) { handled = true; return IntPtr.Zero; }
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
            var handle = new WindowInteropHelper(this).Handle;
            // Windows rejects relaunch commands longer than MAX_PATH (deep profile paths); the button still works without it.
            try { ShellIntegration.SetFolderWindowIdentity(handle, folder, App.Current.Session.Store.DirectoryPath, art.Path); }
            catch (System.Runtime.InteropServices.COMException ex) { App.Current.Log(ex); }
            await RefreshPreviewAsync(folder, currentRevision);
            TaskbarThumbnail.Invalidate(handle);
        }
        catch (Exception ex) { App.Current.Log(ex); }
    }

    /// <summary>Icons for the hover thumbnail and launch buttons for the first applications.</summary>
    private async Task RefreshPreviewAsync(ShelfItem folder, long currentRevision)
    {
        var icons = App.Current.Session.Icons;
        Task<ImageSource?> IconOf(ShelfItem item) => icons.GetAsync(string.IsNullOrWhiteSpace(item.IconPath) ? item.Target : item.IconPath);
        var tiles = new List<ImageSource?>();
        foreach (var child in folder.Children.Take(MaxThumbnailIcons))
        {
            if (!child.IsFolder) { tiles.Add(await IconOf(child)); continue; }
            var previews = new List<ImageSource?>();
            foreach (var app in FolderPreview.Applications(child)) previews.Add(await IconOf(app));
            tiles.Add(FolderArtwork.Render(previews, 128, look: await FolderIconWriter.LookAsync(child.Settings)));
        }
        var launchers = new System.Windows.Shell.ThumbButtonInfoCollection();
        foreach (var app in ShelfTree.Walk(folder.Children).Where(x => !x.IsFolder).Take(MaxLaunchButtons))
        {
            var button = new System.Windows.Shell.ThumbButtonInfo { ImageSource = await IconOf(app), Description = app.Name, DismissWhenClicked = true };
            var target = app;
            button.Click += (_, _) =>
            {
                try { ShellIntegration.Launch(target); }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
                catch (Exception ex) { App.Current.Report(ex); }
            };
            launchers.Add(button);
        }
        if (removing || currentRevision != revision) return;
        thumbnailIcons = tiles;
        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        TaskbarItemInfo.ThumbButtonInfos = launchers;
    }

    /// <summary>The folder's icons laid out to fill the size DWM offers, on a transparent background.</summary>
    public BitmapSource RenderThumbnail(int width, int height)
    {
        width = Math.Clamp(width, 16, 1024); height = Math.Clamp(height, 16, 1024);
        var tiles = thumbnailIcons;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            if (tiles.Count == 0)
            {
                var size = Math.Min(width, height) * .6;
                if (Icon is { } icon) dc.DrawImage(icon, new System.Windows.Rect((width - size) / 2, (height - size) / 2, size, size));
            }
            else
            {
                // Pick the column count that gives the largest cells, capped so small icons are not blown up.
                var (columns, cell) = Enumerable.Range(1, Math.Min(tiles.Count, 6))
                    .Select(c => (c, Math.Min(96d, Math.Min(width / (double)c, height / Math.Ceiling(tiles.Count / (double)c)))))
                    .MaxBy(x => x.Item2);
                var rows = (int)Math.Ceiling(tiles.Count / (double)columns);
                var icon = cell * .72;
                var left = (width - columns * cell) / 2; var top = (height - rows * cell) / 2;
                for (var i = 0; i < tiles.Count; i++)
                    if (tiles[i] is { } image)
                        dc.DrawImage(image, new System.Windows.Rect(left + i % columns * cell + (cell - icon) / 2, top + i / columns * cell + (cell - icon) / 2, icon, icon));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing); bitmap.Freeze();
        return bitmap;
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
