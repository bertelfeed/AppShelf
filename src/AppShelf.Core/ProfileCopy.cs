using System.Text.Json.Nodes;

namespace AppShelf.Core;

/// <summary>Copies a profile into an empty destination, keeping the source intact.</summary>
public static class ProfileCopy
{
    public static void Copy(string from, string to)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        if (Inside(from, to) || Inside(to, from)) throw new IOException(L.Get("ProfilePathsOverlap"));
        if (File.Exists(to) || (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()))
            throw new IOException(L.Get("ProfileDestinationNotEmpty"));
        if (new DirectoryInfo(from).LinkTarget is not null)
            throw new IOException(L.Get("ProfileLinksUnsupported"));
        var config = ConfigurationStore.Read(Path.Combine(from, "shelf.json"));
        var parent = Path.GetDirectoryName(to) ?? throw new IOException(L.Get("ProfileDestinationNotEmpty"));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(to)}.import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            CopyFiles(from, staging, root: true);
            string Moved(string path) => Inside(from, path) ? to + Path.GetFullPath(path)[from.Length..] : path;
            foreach (var item in ShelfTree.Walk(config.Items))
            {
                item.Target = Moved(item.Target);
                item.IconPath = Moved(item.IconPath);
                item.WorkingDirectory = Moved(item.WorkingDirectory);
                item.Origin = Moved(item.Origin);
                item.Settings.FaceImage = Moved(item.Settings.FaceImage);
            }
            var archive = Path.Combine(staging, "desktop-archive", "manifest.json");
            if (File.Exists(archive))
            {
                if (JsonNode.Parse(File.ReadAllText(archive)) is not JsonArray entries)
                    throw new InvalidDataException(L.Get("InvalidDesktopArchive"));
                foreach (var entry in entries)
                {
                    if (entry is not JsonObject) throw new InvalidDataException(L.Get("InvalidDesktopArchive"));
                    foreach (var key in new[] { "Archived", "Original" })
                        if (entry?[key] is JsonValue value && value.TryGetValue<string>(out var path)) entry[key] = Moved(path);
                        else throw new InvalidDataException(L.Get("InvalidDesktopArchive"));
                }
                File.WriteAllText(archive, entries.ToJsonString(ConfigurationStore.JsonOptions));
            }
            // Store final destination paths even though the copy is not published until everything succeeds.
            new ConfigurationStore(staging).Save(config);
            if (Directory.Exists(to)) Directory.Delete(to, recursive: false); // Fails safely if another process filled it.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Move(staging, to); break; }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 7 &&
                    Directory.Exists(staging) && !Directory.Exists(to) && !File.Exists(to))
                { Thread.Sleep(40 * (attempt + 1)); }
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static bool Inside(string root, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyFiles(string from, string to, bool root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            var name = Path.GetFileName(entry);
            if (root && (name.StartsWith("shelf.", StringComparison.OrdinalIgnoreCase) || name.Equals("temp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("desktop-shortcuts.json", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(name, @"^icons(-v\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) continue;
            var attributes = File.GetAttributes(entry);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(entry) : new FileInfo(entry);
            // Cloud placeholders may also be reparse points; reject actual links, not OneDrive files.
            if (info.LinkTarget is not null) throw new IOException(L.Get("ProfileLinksUnsupported"));
            var target = Path.Combine(to, name);
            if ((attributes & FileAttributes.Directory) != 0)
            { Directory.CreateDirectory(target); CopyFiles(entry, target, root: false); }
            else File.Copy(entry, target, overwrite: false);
        }
    }
}
