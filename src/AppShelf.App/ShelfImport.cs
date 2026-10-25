namespace AppShelf.App;

/// <summary>Turns a file the user brought in (dragged, or named to the MCP tools) into a folder item.</summary>
public static class ShelfImport
{
    private static readonly string[] ShortcutExtensions = [".lnk", ".url", ".appref-ms"];

    /// <summary>
    /// Shortcuts are copied into the profile so the app still starts after the original is deleted or tidied
    /// away; programs, files and folders are referenced where they are. Returns null for AppShelf's own folder icons.
    /// </summary>
    public static ShelfItem? FromPath(string dataPath, string path, string? name = null)
    {
        var isFolder = Directory.Exists(path);
        var extension = isFolder ? "" : Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".lnk" && ShellIntegration.ReadShortcut(path) is { } link && link.AppId.StartsWith("AppShelf.Folder.", StringComparison.Ordinal))
            return null;
        name ??= isFolder || !ShortcutExtensions.Contains(extension) && extension != ".exe" ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var item = ShelfItem.Application(name.Length > 200 ? name[..200] : name, path);
        item.Origin = path;
        if (!isFolder && ShortcutExtensions.Contains(extension))
        {
            var directory = Path.Combine(dataPath, "launchers");
            Directory.CreateDirectory(directory);
            var safe = string.Concat(item.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var copy = Path.Combine(directory, $"{(safe.Length > 80 ? safe[..80] : safe)}-{item.Id.ToString("N")[..8]}{extension}");
            File.Copy(path, copy, true);
            item.Target = copy;
        }
        return item;
    }
}
