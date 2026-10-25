using System.Windows.Threading;

namespace AppShelf.App;

/// <summary>
/// Keeps apps in folders startable without anyone's help. A program that disappeared is looked for
/// in a sibling version folder (apps that install each update next to the old one), then in the Start
/// menu under the same name; what cannot be found is marked as missing. Only AppShelf's own copies of
/// shortcuts are rewritten, never the user's files; every repair is written to repairs.log.
/// </summary>
public sealed class ShelfRepair
{
    private readonly ShelfSession session;
    private readonly Func<Task<List<ShelfItem>>> installedApps;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private HashSet<Guid> missing = [];
    // A launch waits for a running background check instead of skipping its own repair.
    private readonly SemaphoreSlim gate = new(1, 1);
    public IReadOnlySet<Guid> Missing => missing;
    public bool IsMissing(Guid id) => missing.Contains(id);

    public ShelfRepair(ShelfSession session, Func<Task<List<ShelfItem>>>? installedApps = null, bool schedule = true)
    {
        this.session = session;
        this.installedApps = installedApps ?? ShellIntegration.InstalledApplicationsAsync;
        if (!schedule) return;
        timer.Tick += async (_, _) => await CheckAllAsync();
        timer.Start();
    }

    private sealed record Finding(Guid Id, string Name, string Missing, string? Moved);
    private string Launchers => Path.Combine(session.Store.DirectoryPath, "launchers");

    /// <summary>Checks every app; returns how many were repaired.</summary>
    public Task<int> CheckAllAsync() => RepairAsync(ShelfTree.Walk(session.Configuration.Items).Where(x => !x.IsFolder).ToList());

    /// <summary>Before a launch: a missing program is repaired at once, so the click still starts the app.</summary>
    public async Task<ShelfItem> PrepareAsync(ShelfItem item)
    {
        if (ShortcutRepair.LocalTarget(item) is not { } target || ShortcutRepair.Exists(target)) return item;
        await RepairAsync([item]);
        var current = ShelfTree.Find(session.Configuration, item.Id) ?? item;
        if (ShortcutRepair.LocalTarget(current) is { } still && !ShortcutRepair.Exists(still))
            throw new InvalidOperationException($"«{item.Name}» не найдено: {still}\nПрограмму удалили или переместили. Укажите её в AppShelf: правый щелчок → «Свойства…».");
        return current;
    }

    private async Task<int> RepairAsync(List<ShelfItem> items)
    {
        await gate.WaitAsync();
        try
        {
            // Shortcut reading and folder search run on a background STA thread; the UI thread only applies the result.
            var findings = await ShellIntegration.RunSta(() => items.Select(item =>
            {
                ShellIntegration.RefreshShortcutCopy(item);
                return ShortcutRepair.LocalTarget(item) is { } target && !ShortcutRepair.Exists(target)
                    ? new Finding(item.Id, item.Name, target, ShortcutRepair.FindMoved(target)) : null;
            }).OfType<Finding>().ToList());
            List<ShelfItem>? start = null;
            var fixes = new List<(Guid Id, Func<ShelfItem, string> Apply)>();
            var unresolved = new HashSet<Guid>();
            foreach (var finding in findings)
            {
                if (ShelfTree.Find(session.Configuration, finding.Id) is not { } item) continue;
                if (finding.Moved is { } moved)
                {
                    try { fixes.Add((item.Id, Retarget(item, finding.Missing, moved))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                    { unresolved.Add(item.Id); App.Current.Log(ex); }
                    continue;
                }
                start ??= await StartMenuAsync();
                var sound = ShelfApi.Skeleton(item.Name);
                var app = start.FirstOrDefault(x => x.Name.Equals(item.Name, StringComparison.CurrentCultureIgnoreCase))
                    ?? (sound.Length > 0 && start.Count(x => ShelfApi.Skeleton(x.Name) == sound) == 1 ? start.First(x => ShelfApi.Skeleton(x.Name) == sound) : null);
                if (app is null || (ShortcutRepair.LocalTarget(app) is { } local && !ShortcutRepair.Exists(local))) { unresolved.Add(item.Id); continue; }
                fixes.Add((item.Id, current =>
                {
                    current.Target = app.Target; current.Arguments = ""; current.WorkingDirectory = ""; current.IconPath = "";
                    return $"меню «Пуск»: {app.Name}";
                }));
            }
            // Marks are updated before the change, so the redraw it triggers already shows them.
            var checkedIds = items.Select(x => x.Id).ToHashSet();
            var next = missing.Where(id => !checkedIds.Contains(id)).Concat(unresolved).ToHashSet();
            var marksChanged = !next.SetEquals(missing);
            missing = next;
            // A shortcut fixed in place keeps its path, so its old icon must not come back from memory.
            foreach (var (id, _) in fixes)
                if (ShelfTree.Find(session.Configuration, id) is { } item) session.Icons.Forget(item.Target);
            var log = new List<string>();
            if (fixes.Count > 0)
                session.Change(config =>
                {
                    foreach (var (id, apply) in fixes)
                        if (ShelfTree.Find(config, id) is { } current)
                        { var before = current.Target; log.Add($"{current.Name}: {before} → {apply(current)}"); }
                });
            if (log.Count > 0)
                try { File.AppendAllLines(Path.Combine(session.Store.DirectoryPath, "repairs.log"), log.Select(x => $"{DateTimeOffset.Now:O} {x}")); } catch (IOException) { }
            if (marksChanged && fixes.Count == 0) session.NotifyChanged();
            return fixes.Count;
        }
        finally { gate.Release(); }
    }

    private async Task<List<ShelfItem>> StartMenuAsync()
    {
        try { return await installedApps(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { return []; }
    }

    /// <summary>
    /// Points the item at the program's new place. A shortcut is fixed in AppShelf's own copy;
    /// a shortcut that belongs to the user is first copied, so their file stays as it was.
    /// </summary>
    private Func<ShelfItem, string> Retarget(ShelfItem item, string missingTarget, string moved)
    {
        var oldFolder = Path.GetDirectoryName(missingTarget) ?? "";
        var newFolder = Path.GetDirectoryName(moved) ?? "";
        string Follow(string path) => path.Length > 0 && path.StartsWith(oldFolder, StringComparison.OrdinalIgnoreCase)
            ? newFolder + path[oldFolder.Length..] : path;
        var linkPath = Environment.ExpandEnvironmentVariables(item.Target);
        if (!linkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return current =>
            {
                current.Target = moved;
                current.WorkingDirectory = Follow(current.WorkingDirectory);
                current.IconPath = Follow(current.IconPath);
                return moved;
            };
        var copy = linkPath;
        if (!linkPath.StartsWith(Launchers + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Launchers);
            copy = Path.Combine(Launchers, $"{Path.GetFileNameWithoutExtension(linkPath)}-{item.Id.ToString("N")[..8]}.lnk");
            File.Copy(linkPath, copy, true);
        }
        var link = ShellIntegration.ReadShortcut(copy);
        var directory = link is null || link.WorkingDirectory.Length == 0 || !Directory.Exists(link.WorkingDirectory) ? newFolder : Follow(link.WorkingDirectory);
        var icon = link is null || link.IconPath.Length == 0 || !File.Exists(Environment.ExpandEnvironmentVariables(link.IconPath)) ? moved : Follow(link.IconPath);
        ShellIntegration.RetargetShortcut(copy, moved, directory, icon, icon == moved ? 0 : link?.IconIndex ?? 0);
        return current =>
        {
            if (current.Origin.Length == 0 && !string.Equals(copy, linkPath, StringComparison.OrdinalIgnoreCase)) current.Origin = linkPath;
            current.Target = copy;
            return moved;
        };
    }
}
