using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AppShelf.App;

/// <summary>
/// Desktop and folder operations behind the MCP tools (see ShelfApiTools). Runs on the UI thread;
/// every change goes through ShelfSession, so it is saved atomically, can be undone and refreshes
/// desktop shortcuts and taskbar buttons exactly like an edit in the editor.
/// </summary>
public sealed class ShelfApi(ShelfSession session, FolderPublisher? publisher)
{
    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] ShortcutExtensions = [".lnk", ".url", ".appref-ms"];
    private static readonly StringComparison Names = StringComparison.CurrentCultureIgnoreCase;
    private List<ShelfItem>? installed;
    private DateTime installedAt;

    /// <summary>Desktop folders to read, the user's own first. Tests point them at temporary folders.</summary>
    public List<string> DesktopDirectories { get; } =
        [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)];
    private string UserDesktop => DesktopDirectories[0];
    private string Data => session.Store.DirectoryPath;
    private string ArchiveManifest => Path.Combine(Data, "desktop-archive", "manifest.json");

    private sealed class ApiException(string message) : Exception(message);
    private sealed record DesktopEntry(string Name, string Path, string Kind, string Target, bool Shared, Guid? Folder) { public bool Hidden { get; init; } }
    private sealed record ArchivedShortcut(string Name, string Original, string Archived, DateTimeOffset At);

    public async Task<ShelfApiChannel.Reply> HandleAsync(string tool, JsonObject args)
    {
        try
        {
            object result = tool switch
            {
                "get_desktop" => await DesktopReportAsync(),
                "get_folders" => await FoldersReportAsync(),
                "find_installed_apps" => await FindAppsAsync(Text(args, "query")),
                "organize" => await OrganizeAsync(args),
                "move_item" => MoveItem(Text(args, "item"), Text(args, "to_folder", required: false) ?? ""),
                "rename_item" => RenameItem(Text(args, "item"), Text(args, "new_name")),
                "remove_item" => RemoveItem(Text(args, "item")),
                "place_folder" => await PlaceFolderAsync(args),
                "set_folder_look" => SetLook(args),
                "restore_desktop" => RestoreDesktop(args),
                "undo" => Undo(),
                _ => throw new ApiException("Неизвестный инструмент: " + tool)
            };
            return new(true, JsonSerializer.Serialize(result, Output));
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException or ArgumentException or FormatException)
        { return new(false, ex.Message); }
    }

    private static string Text(JsonObject args, string name) =>
        Text(args, name, true) ?? throw new ApiException($"Не указан параметр {name}.");
    private static string? Text(JsonObject args, string name, bool required) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : required ? throw new ApiException($"Не указан параметр {name}.") : null;
    private static bool? Flag(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    // ---------- reading ----------

    private Task<List<DesktopEntry>> DesktopAsync()
    {
        var directories = DesktopDirectories.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var data = Data;
        return ShellIntegration.RunSta(() =>
        {
            var result = new List<DesktopEntry>();
            for (var d = 0; d < directories.Count; d++)
            {
                if (!Directory.Exists(directories[d])) continue;
                foreach (var path in Directory.EnumerateFileSystemEntries(directories[d]))
                    if (Describe(path, d > 0, data) is { } entry && !entry.Hidden) result.Add(entry);
            }
            return result;
        });
    }

    /// <summary>Reads one desktop item; null when it cannot be read. Must run on an STA thread (shortcuts are read via COM).</summary>
    private static DesktopEntry? Describe(string path, bool shared, string data)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isFolder = attributes.HasFlag(FileAttributes.Directory);
            var extension = isFolder ? "" : Path.GetExtension(path).ToLowerInvariant();
            var kind = isFolder ? "folder" : extension switch { ".lnk" or ".appref-ms" => "shortcut", ".url" => "web_link", ".exe" => "program", _ => "file" };
            var target = ""; Guid? folder = null;
            if (extension == ".lnk" && ShellIntegration.ReadShortcut(path) is { } link)
            {
                target = link.Target.Length > 0 ? link.Target : link.AppId;
                if (link.AppId.StartsWith("AppShelf.Folder.", StringComparison.Ordinal) && Guid.TryParse(link.AppId["AppShelf.Folder.".Length..], out var id) &&
                    ShellIntegration.IsFolderShortcut(link, id, data)) { folder = id; kind = "appshelf_folder"; }
            }
            // Explorer hides the extension of shortcuts, so they are named as the user sees them.
            var name = ShortcutExtensions.Contains(extension) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
            return new(name, path, kind, target, shared, folder) { Hidden = attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A full path to an existing item that is not on the desktop itself, e.g. a shortcut in a desktop subfolder.</summary>
    private Task<DesktopEntry?> OutsideAsync(string reference)
    {
        if (!Path.IsPathFullyQualified(reference) || !(File.Exists(reference) || Directory.Exists(reference))) return Task.FromResult<DesktopEntry?>(null);
        var data = Data;
        return ShellIntegration.RunSta(() => Describe(Path.GetFullPath(reference), false, data));
    }
    private bool OnDesktop(DesktopEntry entry) => DesktopDirectories.Any(d => SamePath(Path.GetDirectoryName(entry.Path) ?? "", d.TrimEnd('\\')));

    private async Task<List<ShelfItem>> InstalledAsync()
    {
        if (installed is null || DateTime.UtcNow - installedAt > TimeSpan.FromMinutes(2))
        { installed = await ShellIntegration.InstalledApplicationsAsync(); installedAt = DateTime.UtcNow; }
        return installed;
    }

    private static string FolderPath(ShelfConfiguration config, Guid id) => string.Join("/", ShelfTree.Breadcrumbs(config, id).Select(x => x.Name));
    private static bool SamePath(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool IsFrom(ShelfItem item, string path) => !item.IsFolder && (SamePath(item.Origin, path) || SamePath(item.Target, path));
    /// <summary>
    /// The same app can be in AppShelf from the Start menu and on the desktop as a shortcut with other paths;
    /// an app with the same name counts as the same app, so organizing moves it instead of adding a twin.
    /// </summary>
    private static bool IsSame(ShelfItem item, string path, string name) => IsFrom(item, path) || (!item.IsFolder && item.Name.Equals(name, Names));
    private static string Location(ShelfConfiguration config, ShelfItem item) =>
        ShelfTree.Parent(config, item.Id) is { } parent && ShelfTree.Walk(config.Items).FirstOrDefault(x => x.IsFolder && x.Children == parent) is { } folder
            ? FolderPath(config, folder.Id) : "";

    private async Task<object> DesktopReportAsync()
    {
        var entries = await DesktopAsync();
        var config = session.Configuration;
        var apps = ShelfTree.Walk(config.Items).Where(x => !x.IsFolder).ToList();
        return new
        {
            desktop = UserDesktop,
            items = entries.Select(e => new
            {
                name = e.Name, kind = e.Kind, path = e.Path,
                target = e.Target.Length > 0 ? e.Target : null,
                all_users = e.Shared ? true : (bool?)null,
                appshelf_folder = e.Folder is { } id && ShelfTree.Find(config, id) is not null ? FolderPath(config, id) : null,
                in_folders = apps.Where(a => IsSame(a, e.Path, e.Name)).Select(a => Location(config, a)).Distinct().ToArray() is { Length: > 0 } found ? found : null
            })
        };
    }

    private async Task<object> FoldersReportAsync()
    {
        var onDesktop = (await DesktopAsync()).Where(x => x.Folder is not null).Select(x => x.Folder!.Value).ToHashSet();
        var config = session.Configuration;
        object Node(ShelfItem folder, string path) => new
        {
            name = folder.Name, path,
            on_desktop = onDesktop.Contains(folder.Id), on_taskbar = folder.Settings.ShowOnTaskbar,
            look = folder.Settings.Face switch { FolderFace.Symbol => "symbol", FolderFace.Image => "picture", _ => "apps" },
            apps = folder.Children.Where(x => !x.IsFolder).Select(x => x.Name).ToArray(),
            folders = folder.Children.Where(x => x.IsFolder).Select(x => Node(x, path + "/" + x.Name)).ToArray()
        };
        return new
        {
            folders = config.Items.Where(x => x.IsFolder).Select(x => Node(x, x.Name)).ToArray(),
            apps_outside_folders = config.Items.Where(x => !x.IsFolder).Select(x => x.Name).ToArray()
        };
    }

    private async Task<object> FindAppsAsync(string query)
    {
        var apps = await InstalledAsync();
        var found = apps.Where(x => x.Name.Contains(query, Names)).Take(30).Select(x => new { name = x.Name, target = x.Target }).ToArray();
        return new { query, found };
    }

    // ---------- organizing ----------

    private async Task<object> OrganizeAsync(JsonObject args)
    {
        if (args["groups"] is not JsonArray { Count: > 0 } groups) throw new ApiException("Нужен непустой список groups.");
        var tidy = Flag(args, "tidy_desktop") ?? false;
        var place = Flag(args, "place_on_desktop") ?? true;
        var entries = await DesktopAsync();
        var plan = new List<(string Folder, List<(string Name, DesktopEntry? Entry, ShelfItem? App)> Items)>();
        var missing = new List<object>();
        var recognized = new List<string>();
        foreach (var group in groups.OfType<JsonObject>())
        {
            var folder = Text(group, "folder");
            if (group["items"] is not JsonArray { Count: > 0 } references) throw new ApiException($"Для папки «{folder}» не указаны items.");
            var items = new List<(string, DesktopEntry?, ShelfItem?)>();
            foreach (var reference in references.Select(x => x?.GetValue<string>()?.Trim() ?? ""))
            {
                var entry = FindEntry(entries, reference) ?? await OutsideAsync(reference);
                if (entry?.Folder is not null) throw new ApiException($"«{reference}» — это значок папки AppShelf, а не приложение.");
                var app = entry is null ? FindApp(await InstalledAsync(), reference) : null;
                if (entry is null && app is null) missing.Add(new { item = reference, suggestions = await SuggestAsync(reference, entries) });
                else
                {
                    items.Add((reference, entry, app));
                    var found = entry?.Name ?? app!.Name;
                    if (!found.Equals(reference, Names) && !SamePath(entry?.Path ?? "", reference)) recognized.Add($"{reference} → {found}");
                }
            }
            plan.Add((folder, items));
        }
        if (missing.Count > 0)
            throw new ApiException("Ничего не изменено: не найдены элементы. " + JsonSerializer.Serialize(missing, Output));

        var created = new List<string>(); var added = new List<string>(); var moved = new List<string>(); var already = new List<string>();
        var targets = new List<Guid>();
        session.Change(config =>
        {
            foreach (var (folderPath, items) in plan)
            {
                var folder = FolderByPath(config, folderPath, created);
                targets.Add(folder.Id);
                var label = FolderPath(config, folder.Id);
                foreach (var (name, entry, app) in items)
                {
                    var all = ShelfTree.Walk(config.Items).ToList();
                    var existing = entry is not null
                        ? all.FirstOrDefault(x => IsFrom(x, entry.Path)) ?? all.FirstOrDefault(x => IsSame(x, entry.Path, entry.Name))
                        : all.FirstOrDefault(x => !x.IsFolder && SamePath(x.Target, app!.Target)) ?? all.FirstOrDefault(x => !x.IsFolder && x.Name.Equals(app!.Name, Names));
                    if (existing is not null && folder.Children.Contains(existing)) already.Add($"{existing.Name} — уже в «{label}»");
                    else if (existing is not null) { ShelfTree.Move(config, existing.Id, folder.Id); moved.Add($"{existing.Name} → {label}"); }
                    else
                    {
                        var item = entry is not null ? Materialize(entry) : ConfigurationStore.CopyItems([app!])[0];
                        folder.Children.Add(item); added.Add($"{item.Name} → {label}");
                    }
                }
            }
        });

        var archived = new List<string>(); var kept = new List<string>();
        if (tidy)
            foreach (var entry in plan.SelectMany(x => x.Items).Select(x => x.Entry).OfType<DesktopEntry>().DistinctBy(x => x.Path.ToUpperInvariant()))
            {
                if (!OnDesktop(entry)) kept.Add($"{entry.Name} — лежит не на рабочем столе, его место не меняется");
                else if (entry.Shared) kept.Add($"{entry.Name} — общий ярлык всех пользователей, убрать его можно только с правами администратора");
                else if (entry.Kind is not ("shortcut" or "web_link")) kept.Add($"{entry.Name} — это не ярлык, файлы и папки не перемещаются");
                else { Archive(entry); archived.Add(entry.Name); }
            }

        var placed = new List<string>();
        if (place && publisher is not null)
        {
            var onDesktop = entries.Where(x => x.Folder is not null).Select(x => x.Folder!.Value).ToHashSet();
            foreach (var id in targets.Distinct().Where(id => session.Configuration.Items.Any(x => x.Id == id) && !onDesktop.Contains(id)))
            { await publisher.AddDesktopAsync(id); placed.Add(FolderPath(session.Configuration, id)); }
        }
        return new
        {
            recognized_names = recognized, created_folders = created, added, moved, already_there = already,
            moved_off_desktop = archived, left_on_desktop = kept, folder_icons_added_to_desktop = placed,
            undo = "undo отменит изменения папок; restore_desktop вернёт ярлыки на рабочий стол"
        };
    }

    private static DesktopEntry? FindEntry(List<DesktopEntry> entries, string reference) =>
        entries.FirstOrDefault(e => SamePath(e.Path, reference))
        ?? entries.FirstOrDefault(e => e.Name.Equals(reference, Names))
        ?? entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(reference, Names))
        ?? Unique(entries.Where(e => Skeleton(e.Name) == Skeleton(reference)));
    private static ShelfItem? FindApp(List<ShelfItem> apps, string reference) =>
        apps.FirstOrDefault(x => x.Name.Equals(reference, Names)) ?? Unique(apps.Where(x => Skeleton(x.Name) == Skeleton(reference)));
    private static T? Unique<T>(IEnumerable<T> items) where T : class
    {
        using var enumerator = items.GetEnumerator();
        if (!enumerator.MoveNext()) return null;
        var first = enumerator.Current;
        return enumerator.MoveNext() ? null : first;
    }

    /// <summary>
    /// Dictation writes app names as heard: «Обсидиан», «Дискорд». Names are compared by a Latin
    /// sound-alike skeleton, so «Дискорд» and Discord both become "diskord".
    /// </summary>
    public static string Skeleton(string text)
    {
        var latin = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
            latin.Append(c switch
            {
                'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' or 'ё' or 'э' => "e", 'ж' => "zh", 'з' => "z",
                'и' or 'й' or 'ы' => "i", 'к' => "k", 'л' => "l", 'м' => "m", 'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r",
                'с' => "s", 'т' => "t", 'у' => "u", 'ф' => "f", 'х' => "h", 'ц' => "ts", 'ч' => "ch", 'ш' or 'щ' => "sh",
                'ъ' or 'ь' => "", 'ю' => "iu", 'я' => "ia",
                _ when char.IsAsciiLetterOrDigit(c) => c.ToString(),
                _ => ""
            });
        return latin.ToString().Replace("ph", "f").Replace("ck", "k").Replace("x", "ks").Replace("dzh", "j").Replace("zh", "j")
            .Replace('c', 'k').Replace('q', 'k').Replace('w', 'v').Replace('y', 'i');
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1]; current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }

    private async Task<string[]> SuggestAsync(string reference, List<DesktopEntry> entries)
    {
        var sound = Skeleton(reference);
        var tolerance = Math.Max(1, (int)Math.Round(sound.Length / 3d));
        bool Similar(string name)
        {
            if (name.Contains(reference, Names) || reference.Contains(name, Names)) return true;
            var other = Skeleton(name);
            if (sound.Length == 0 || other.Length == 0) return false;
            if (other.Contains(sound) || (other.Length >= 3 && sound.Contains(other)) || Distance(other, sound) <= tolerance) return true;
            // «Юбисофт» against "Ubisoft Connect": compare with each word of the name.
            return name.Split(' ', '-', '_', '.').Select(Skeleton).Any(word => word.Length >= 3 && Distance(word, sound) <= tolerance);
        }
        var desktop = entries.Where(e => e.Folder is null && Similar(e.Name)).Select(e => e.Name);
        var apps = (await InstalledAsync()).Where(x => Similar(x.Name)).Select(x => x.Name);
        return desktop.Concat(apps).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(6).ToArray();
    }

    /// <summary>
    /// Shortcuts are copied into the profile so the app still starts after the desktop shortcut is tidied away
    /// or deleted by hand; programs, files and folders are referenced where they are.
    /// </summary>
    private ShelfItem Materialize(DesktopEntry entry)
    {
        var item = ShelfItem.Application(entry.Name, entry.Path);
        item.Origin = entry.Path;
        var extension = Path.GetExtension(entry.Path).ToLowerInvariant();
        if (entry.Kind != "folder" && ShortcutExtensions.Contains(extension))
        {
            var directory = Path.Combine(Data, "launchers");
            Directory.CreateDirectory(directory);
            var safe = string.Concat(entry.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var copy = Path.Combine(directory, $"{(safe.Length > 80 ? safe[..80] : safe)}-{item.Id.ToString("N")[..8]}{extension}");
            File.Copy(entry.Path, copy, true);
            item.Target = copy;
        }
        return item;
    }

    private static ShelfItem FolderByPath(ShelfConfiguration config, string path, List<string>? created)
    {
        var parts = path.Split('/', '\\').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (parts.Length == 0) throw new ApiException("Не указано название папки.");
        if (parts.Length == 1)
        {
            var matches = ShelfTree.Walk(config.Items).Where(x => x.IsFolder && x.Name.Equals(parts[0], Names)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
                throw new ApiException($"Папок «{parts[0]}» несколько: {string.Join(", ", matches.Select(x => FolderPath(config, x.Id)))}. Укажите путь через «/».");
        }
        var container = config.Items;
        ShelfItem? current = null;
        foreach (var part in parts)
        {
            var next = container.FirstOrDefault(x => x.IsFolder && x.Name.Equals(part, Names));
            if (next is null)
            {
                if (created is null) throw new ApiException($"Папка «{path}» не найдена.");
                if (part.Length > 200) throw new ApiException("Слишком длинное название папки.");
                next = ShelfItem.Folder(part); container.Add(next);
                created.Add(current is null ? part : FolderPath(config, current.Id) + "/" + part);
            }
            current = next; container = next.Children;
        }
        return current!;
    }

    private static ShelfItem ItemByPath(ShelfConfiguration config, string reference)
    {
        var parts = reference.Split('/', '\\').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (parts.Length == 0) throw new ApiException("Не указан элемент.");
        IEnumerable<ShelfItem> scope = parts.Length > 1 ? FolderByPath(config, string.Join("/", parts[..^1]), null).Children : ShelfTree.Walk(config.Items);
        var matches = scope.Where(x => x.Name.Equals(parts[^1], Names)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ApiException($"«{reference}» нет в AppShelf."),
            _ => throw new ApiException($"«{reference}» встречается несколько раз: {string.Join(", ", matches.Select(x => (Location(config, x) is { Length: > 0 } at ? at + "/" : "") + x.Name))}. Укажите путь через «/».")
        };
    }

    private object MoveItem(string reference, string destination)
    {
        string? result = null;
        session.Change(config =>
        {
            var item = ItemByPath(config, reference);
            Guid? folder = destination.Trim('/', ' ').Length == 0 ? null : FolderByPath(config, destination, []).Id;
            ShelfTree.Move(config, item.Id, folder);
            result = $"{item.Name} → {(folder is { } id ? FolderPath(config, id) : "верхний уровень")}";
        });
        return new { moved = result };
    }

    private object RenameItem(string reference, string name)
    {
        if (name.Length is 0 or > 200) throw new ApiException("Новое название должно быть от 1 до 200 символов.");
        string? old = null;
        session.Change(config => { var item = ItemByPath(config, reference); old = item.Name; item.Name = name; });
        return new { renamed = $"{old} → {name}" };
    }

    private object RemoveItem(string reference)
    {
        string? removed = null; var count = 0;
        session.Change(config =>
        {
            var item = ItemByPath(config, reference);
            removed = item.Name; count = ShelfTree.Walk([item]).Count(x => !x.IsFolder);
            ShelfTree.Parent(config, item.Id)!.RemoveAll(x => x.Id == item.Id);
        });
        return new { removed, apps_inside = count, note = "Программы и файлы на диске не тронуты; undo вернёт элемент." };
    }

    private async Task<object> PlaceFolderAsync(JsonObject args)
    {
        var folder = FolderByPath(session.Configuration, Text(args, "folder"), null);
        var desktop = Flag(args, "desktop"); var taskbar = Flag(args, "taskbar");
        if (desktop is null && taskbar is null) throw new ApiException("Укажите desktop и/или taskbar.");
        if (desktop is { } show && publisher is not null)
        { if (show) await publisher.AddDesktopAsync(folder.Id); else await publisher.RemoveDesktopAsync(folder.Id); }
        if (taskbar is { } pin)
            session.Change(config => ShelfTree.Find(config, folder.Id)!.Settings.ShowOnTaskbar = pin);
        return new { folder = FolderPath(session.Configuration, folder.Id), desktop, taskbar };
    }

    private object SetLook(JsonObject args)
    {
        var folder = FolderByPath(session.Configuration, Text(args, "folder"), null);
        var look = Text(args, "look", required: false);
        var colorName = Text(args, "color", required: false);
        var symbolName = Text(args, "symbol", required: false);
        var picture = Text(args, "picture", required: false);
        string? color = colorName switch
        {
            null => null,
            "Авто" or "auto" or "" => "",
            _ when ShelfTree.IsColor(colorName) => colorName.ToUpperInvariant(),
            _ => FolderFaceCatalog.Palette.FirstOrDefault(x => x.Name.Equals(colorName, Names)).Hex
                 ?? throw new ApiException("Неизвестный цвет: " + colorName + ". Есть: " + string.Join(", ", FolderFaceCatalog.Palette.Select(x => x.Name)))
        };
        FolderFace? face = look switch { null => null, "apps" => FolderFace.Preview, "symbol" => FolderFace.Symbol, "picture" => FolderFace.Image, _ => throw new ApiException("look: apps, symbol или picture.") };
        string? glyph = symbolName is null ? null : FolderFaceCatalog.Symbols.FirstOrDefault(x => x.Name.Equals(symbolName, Names) || x.Glyph == symbolName).Glyph
            ?? throw new ApiException("Неизвестный символ: " + symbolName + ". Есть: " + string.Join(", ", FolderFaceCatalog.Symbols.Select(x => x.Name)));
        if (face == FolderFace.Symbol && glyph is null && folder.Settings.FaceSymbol.Length == 0) throw new ApiException("Для look=symbol укажите symbol.");
        string? stored = null;
        if (face == FolderFace.Image)
        {
            if (picture is null || !File.Exists(picture)) throw new ApiException("Для look=picture укажите путь к существующей картинке.");
            stored = FolderIconWriter.ImportPicture(folder.Id, picture);
        }
        session.Change(config =>
        {
            var settings = ShelfTree.Find(config, folder.Id)!.Settings;
            if (face is { } f) settings.Face = f;
            if (color is not null) settings.FaceColor = color;
            if (glyph is not null) { settings.FaceSymbol = glyph; if (face is null) settings.Face = FolderFace.Symbol; }
            if (stored is not null) settings.FaceImage = stored;
        });
        var result = ShelfTree.Find(session.Configuration, folder.Id)!.Settings;
        return new
        {
            folder = FolderPath(session.Configuration, folder.Id),
            look = result.Face switch { FolderFace.Symbol => "symbol", FolderFace.Image => "picture", _ => "apps" },
            color = FolderFaceCatalog.Palette.FirstOrDefault(x => x.Hex == result.FaceColor).Name ?? (result.FaceColor.Length == 0 ? "Авто" : result.FaceColor),
            symbol = FolderFaceCatalog.Symbols.FirstOrDefault(x => x.Glyph == result.FaceSymbol).Name
        };
    }

    // ---------- desktop archive ----------

    private List<ArchivedShortcut> LoadArchive()
    {
        if (!File.Exists(ArchiveManifest)) return [];
        return JsonSerializer.Deserialize<List<ArchivedShortcut>>(File.ReadAllText(ArchiveManifest)) ?? [];
    }
    private void SaveArchive(List<ArchivedShortcut> archive)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ArchiveManifest)!);
        var temporary = ArchiveManifest + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(archive, Output));
        File.Move(temporary, ArchiveManifest, true);
    }

    /// <summary>Moves a desktop shortcut into the profile's archive; restore_desktop puts it back.</summary>
    private void Archive(DesktopEntry entry)
    {
        var directory = Path.Combine(Data, "desktop-archive", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Path.GetFileName(entry.Path));
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(entry.Path)} ({i}){Path.GetExtension(entry.Path)}");
        File.Move(entry.Path, target);
        var archive = LoadArchive();
        archive.Add(new(entry.Name, entry.Path, target, DateTimeOffset.Now));
        SaveArchive(archive);
        ShellIntegration.NotifyShortcut(entry.Path, 4);
    }

    private object RestoreDesktop(JsonObject args)
    {
        var names = (args["items"] as JsonArray)?.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0).ToList();
        var archive = LoadArchive();
        var restored = new List<string>(); var skipped = new List<string>();
        foreach (var shortcut in archive.ToList())
        {
            if (names is { Count: > 0 } && !names.Any(x => x.Equals(shortcut.Name, Names))) continue;
            if (!File.Exists(shortcut.Archived)) { skipped.Add($"{shortcut.Name} — копии в архиве нет"); archive.Remove(shortcut); continue; }
            if (File.Exists(shortcut.Original)) { skipped.Add($"{shortcut.Name} — на рабочем столе уже есть файл с таким именем"); continue; }
            File.Move(shortcut.Archived, shortcut.Original);
            ShellIntegration.NotifyShortcut(shortcut.Original, 2);
            archive.Remove(shortcut); restored.Add(shortcut.Name);
        }
        SaveArchive(archive);
        return new { restored, skipped, still_archived = archive.Select(x => x.Name).ToArray() };
    }

    private object Undo()
    {
        if (!session.CanUndo) throw new ApiException("Отменять нечего.");
        session.Undo();
        return new { undone = true, note = "Ярлыки, убранные с рабочего стола, возвращает restore_desktop." };
    }
}
