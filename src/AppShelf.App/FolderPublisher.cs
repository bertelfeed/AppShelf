using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace AppShelf.App;

public sealed class FolderPublisher : IDisposable
{
    private readonly ShelfSession session;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool syncing, pending;
    private readonly Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
    private sealed record PublishedShortcut(Guid FolderId, string Path);
    private string PublishedPath => Path.Combine(session.Store.DirectoryPath, "desktop-shortcuts.json");
    public FolderPublisher(ShelfSession session, bool autoRefresh = true)
    {
        this.session = session;
        if (autoRefresh) session.Changed += RequestRefresh;
        timer.Tick += async (_, _) => { timer.Stop(); await RefreshAsync(); };
        if (autoRefresh) RequestRefresh();
    }
    public void RequestRefresh() { pending = true; timer.Stop(); timer.Start(); }
    /// <summary>The user's desktop; checks point it at a temporary folder.</summary>
    public string Desktop { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string Programs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AppShelf");
    private string Pinned => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
    public async Task<string?> DesktopShortcutAsync(Guid id) =>
        (await LinksAsync([Desktop])).FirstOrDefault(link => ShellIntegration.IsFolderShortcut(link, id, session.Store.DirectoryPath))?.Path;
    public async Task<string> AddDesktopAsync(Guid id)
    {
        var folder = ShelfTree.Find(session.Configuration, id) ?? throw new InvalidOperationException(L.Get("TheFolderWasDeleted"));
        var art = await FolderIconWriter.PrepareAsync(folder);
        var existing = await DesktopShortcutAsync(id);
        var path = await ShellIntegration.RunSta(() => ShellIntegration.CreateFolderShortcut(folder, Desktop, session.Store.DirectoryPath,
            art.Path, "desktop", existing, true));
        RememberDesktop(folder.Id, path);
        return path;
    }
    public async Task RemoveDesktopAsync(Guid id)
    {
        // Only remove a matching AppShelf shortcut for this exact profile.
        var links = (await LinksAsync([Desktop])).Where(link => ShellIntegration.IsFolderShortcut(link, id, session.Store.DirectoryPath)).ToList();
        var known = ReadPublished();
        if (known.RemoveAll(link => link.FolderId == id) > 0) SavePublished(known);
        await ShellIntegration.RunSta(() =>
        {
            foreach (var link in links)
                if (ShellIntegration.IsFolderShortcut(ShellIntegration.ReadShortcut(link.Path), id, session.Store.DirectoryPath))
                { File.Delete(link.Path); ShellIntegration.NotifyShortcut(link.Path, 4); }
            return true;
        });
    }
    public async Task<string> EnsureTaskbarEntryAsync(Guid id)
    {
        var folder = ShelfTree.Find(session.Configuration, id) ?? throw new InvalidOperationException(L.Get("TheFolderWasDeleted"));
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
            // Adopt only shortcuts whose intact Shell identity proves they belong to this profile.
            foreach (var link in await LinksAsync([Desktop]))
                if (Guid.TryParse(link.AppId["AppShelf.Folder.".Length..], out var id) &&
                    ShelfTree.Find(session.Configuration, id) is { IsFolder: true } &&
                    ShellIntegration.IsFolderShortcut(link, id, session.Store.DirectoryPath)) RememberDesktop(id, link.Path);
            await RepairDamagedAsync();
            var config = ConfigurationStore.Clone(session.Configuration);
            var links = await LinksAsync([Desktop, Programs, Pinned, Path.Combine(session.Store.DirectoryPath, "shortcuts")]);
            foreach (var folder in ShelfTree.Walk(config.Items).Where(x => x.IsFolder))
            {
                var owned = links.Where(link => ShellIntegration.IsFolderShortcut(link, folder.Id, session.Store.DirectoryPath)).ToList();
                if (owned.Count == 0) continue;
                var art = await FolderIconWriter.PrepareAsync(folder);
                foreach (var link in owned)
                {
                    var signature = folder.Name + "|" + art.Path + "|" + L.Language;
                    if (signatures.GetValueOrDefault(link.Path) == signature) continue;
                    try
                    {
                        var surface = link.Arguments.Contains("--surface desktop", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetDirectoryName(link.Path), Desktop, StringComparison.OrdinalIgnoreCase) ? "desktop" : "taskbar";
                        var path = RenameOnDesktop(link.Path, folder);
                        await ShellIntegration.RunSta(() => ShellIntegration.CreateFolderShortcut(folder, Path.GetDirectoryName(path)!,
                            session.Store.DirectoryPath, art.Path, surface, path));
                        signatures[path] = signature;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                    { Log(ex); }
                }
            }
        }
        catch (Exception ex) { Log(ex); }
        finally { syncing = false; if (pending) RequestRefresh(); }
    }
    /// <summary>
    /// Rebuilds a damaged shortcut only when its exact path was previously published or verified by this profile.
    /// A matching folder name alone never establishes ownership of a desktop file.
    /// Returns the names of the repaired folders.
    /// </summary>
    public async Task<List<string>> RepairDamagedAsync()
    {
        var repaired = new List<string>();
        foreach (var published in ReadPublished())
        {
            var folder = ShelfTree.Find(session.Configuration, published.FolderId);
            var path = published.Path;
            if (folder is not { IsFolder: true } || !string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(Desktop).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (!ShellIntegration.IsDamagedShortcut(path)) continue;
            string? archived = null;
            try
            {
                var art = await FolderIconWriter.PrepareAsync(folder);
                // Recheck after awaiting artwork: Explorer or synchronization may have fixed the file meanwhile.
                if (!ShellIntegration.IsDamagedShortcut(path)) continue;
                var keep = Path.Combine(session.Store.DirectoryPath, "damaged-shortcuts");
                Directory.CreateDirectory(keep);
                archived = Path.Combine(keep, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{Path.GetFileName(path)}");
                File.Move(path, archived);
                await ShellIntegration.RunSta(() => ShellIntegration.CreateFolderShortcut(folder, Desktop, session.Store.DirectoryPath,
                    art.Path, "desktop", path, true));
                RememberDesktop(folder.Id, path);
                repaired.Add(folder.Name);
                File.AppendAllText(Path.Combine(session.Store.DirectoryPath, "repairs.log"),
                    $"{DateTimeOffset.Now:O} {folder.Name}: damaged desktop shortcut replaced\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // A failed replacement must not make the original desktop file disappear.
                if (archived is not null && File.Exists(archived) && !File.Exists(path))
                    try { File.Move(archived, path); } catch (IOException restoreError) { Log(restoreError); }
                Log(ex);
            }
        }
        return repaired;
    }

    private List<PublishedShortcut> ReadPublished()
    {
        try
        {
            return File.Exists(PublishedPath)
                ? (JsonSerializer.Deserialize<List<PublishedShortcut>>(File.ReadAllText(PublishedPath)) ?? [])
                    .Where(link => link is not null && link.FolderId != Guid.Empty && !string.IsNullOrWhiteSpace(link.Path) && Path.IsPathFullyQualified(link.Path)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Log(ex); return []; }
    }
    private void SavePublished(List<PublishedShortcut> links)
    {
        Directory.CreateDirectory(session.Store.DirectoryPath);
        var temporary = PublishedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(links)); File.Move(temporary, PublishedPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void RememberDesktop(Guid id, string path)
    {
        path = Path.GetFullPath(path);
        var links = ReadPublished();
        if (links.Any(link => link.FolderId == id && string.Equals(link.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        links.RemoveAll(link => string.Equals(link.Path, path, StringComparison.OrdinalIgnoreCase));
        links.Add(new(id, path)); SavePublished(links);
    }

    /// <summary>
    /// Explorer labels a desktop shortcut by its file name, so a renamed folder renames its desktop shortcut too.
    /// A name taken by another file keeps the old shortcut name.
    /// </summary>
    private string RenameOnDesktop(string path, ShelfItem folder)
    {
        if (!string.Equals(Path.GetDirectoryName(path), Desktop, StringComparison.OrdinalIgnoreCase)) return path;
        var wanted = Path.Combine(Desktop, ShellIntegration.FolderShortcutName(folder) + ".lnk");
        if (string.Equals(path, wanted, StringComparison.Ordinal)) return path;
        if (File.Exists(wanted) && !string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase)) return path;
        var known = ReadPublished();
        if (known.RemoveAll(link => string.Equals(link.Path, path, StringComparison.OrdinalIgnoreCase)) > 0) SavePublished(known);
        File.Move(path, wanted);
        RememberDesktop(folder.Id, wanted);
        ShellIntegration.NotifyShortcut(path, 4); ShellIntegration.NotifyShortcut(wanted, 2);
        signatures.Remove(path);
        return wanted;
    }
    private void Log(Exception ex)
    { try { File.AppendAllText(Path.Combine(session.Store.DirectoryPath, "errors.log"), $"{DateTimeOffset.Now:O} Shortcut refresh: {ex.Message}\n"); } catch { } }
    public void Dispose() { timer.Stop(); session.Changed -= RequestRefresh; }
}
