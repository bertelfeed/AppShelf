using System.Diagnostics;
using System.Windows.Threading;

namespace AppShelf.App;

public sealed class FolderPublisher : IDisposable
{
    private readonly ShelfSession session;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool syncing, pending;
    private readonly Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
    public FolderPublisher(ShelfSession session, bool autoRefresh = true)
    {
        this.session = session;
        if (autoRefresh) session.Changed += RequestRefresh;
        timer.Tick += async (_, _) => { timer.Stop(); await RefreshAsync(); };
        if (autoRefresh) RequestRefresh();
    }
    public void RequestRefresh() { pending = true; timer.Stop(); timer.Start(); }
    private string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string Programs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AppShelf");
    private string Pinned => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
    public async Task<string?> DesktopShortcutAsync(Guid id) =>
        (await LinksAsync([Desktop])).FirstOrDefault(link => ShellIntegration.IsFolderShortcut(link, id, session.Store.DirectoryPath))?.Path;
    public async Task<string> AddDesktopAsync(Guid id)
    {
        var folder = ShelfTree.Find(session.Configuration, id) ?? throw new InvalidOperationException("Папка удалена.");
        var art = await FolderIconWriter.PrepareAsync(folder);
        var existing = await DesktopShortcutAsync(id);
        return await ShellIntegration.RunSta(() => ShellIntegration.CreateFolderShortcut(folder, Desktop, session.Store.DirectoryPath,
            art.Path, "desktop", existing, true));
    }
    public async Task RemoveDesktopAsync(Guid id)
    {
        // Only remove a matching AppShelf shortcut for this exact profile.
        var path = await DesktopShortcutAsync(id);
        if (path is null) return;
        await ShellIntegration.RunSta(() =>
        {
            if (ShellIntegration.IsFolderShortcut(ShellIntegration.ReadShortcut(path), id, session.Store.DirectoryPath))
            { File.Delete(path); ShellIntegration.NotifyShortcut(path, 4); }
            return true;
        });
    }
    public async Task<string> EnsureTaskbarEntryAsync(Guid id)
    {
        var folder = ShelfTree.Find(session.Configuration, id) ?? throw new InvalidOperationException("Папка удалена.");
        var art = await FolderIconWriter.PrepareAsync(folder);
        var existing = (await LinksAsync([Programs])).FirstOrDefault(link => ShellIntegration.IsFolderShortcut(link, id, session.Store.DirectoryPath))?.Path;
        return await ShellIntegration.RunSta(() => ShellIntegration.CreateFolderShortcut(folder, Programs, session.Store.DirectoryPath,
            art.Path, "taskbar", existing, true));
    }
    public async Task PinTaskbarAsync(Guid id)
    {
        session.Change(config =>
        { if (ShelfTree.Find(config, id) is { IsFolder: true } folder) folder.Settings.ShowOnTaskbar = true; });
        await Task.CompletedTask;
    }
    private Task<List<ShellIntegration.ShortcutDetails>> LinksAsync(IEnumerable<string> directories) => ShellIntegration.RunSta(() =>
    {
        var result = new List<ShellIntegration.ShortcutDetails>();
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.lnk"))
                {
                    var link = ShellIntegration.ReadShortcut(path);
                    if (link is not null && link.AppId.StartsWith("AppShelf.Folder.", StringComparison.Ordinal)) result.Add(link);
                }
            }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    });
    public async Task RefreshAsync()
    {
        if (syncing) { pending = true; return; }
        syncing = true; pending = false;
        try
        {
            var config = ConfigurationStore.Clone(session.Configuration);
            var links = await LinksAsync([Desktop, Programs, Pinned, Path.Combine(session.Store.DirectoryPath, "shortcuts")]);
            foreach (var folder in ShelfTree.Walk(config.Items).Where(x => x.IsFolder))
            {
                var owned = links.Where(link => ShellIntegration.IsFolderShortcut(link, folder.Id, session.Store.DirectoryPath)).ToList();
                if (owned.Count == 0) continue;
                var art = await FolderIconWriter.PrepareAsync(folder);
                foreach (var link in owned)
                {
                    var signature = folder.Name + "|" + art.Path;
                    if (signatures.GetValueOrDefault(link.Path) == signature) continue;
                    try
                    {
                        var surface = link.Arguments.Contains("--surface desktop", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetDirectoryName(link.Path), Desktop, StringComparison.OrdinalIgnoreCase) ? "desktop" : "taskbar";
                        await ShellIntegration.RunSta(() => ShellIntegration.CreateFolderShortcut(folder, Path.GetDirectoryName(link.Path)!,
                            session.Store.DirectoryPath, art.Path, surface, link.Path));
                        signatures[link.Path] = signature;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                    { Log(ex); }
                }
            }
        }
        catch (Exception ex) { Log(ex); }
        finally { syncing = false; if (pending) RequestRefresh(); }
    }
    private void Log(Exception ex)
    { try { File.AppendAllText(Path.Combine(session.Store.DirectoryPath, "errors.log"), $"{DateTimeOffset.Now:O} Shortcut refresh: {ex.Message}\n"); } catch { } }
    public void Dispose() { timer.Stop(); session.Changed -= RequestRefresh; }
}
