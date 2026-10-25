using AppShelf.Core;

namespace AppShelf.Windows;

/// <summary>
/// Finds programs that moved. Many apps install every update into a new version folder
/// (Roblox\Versions\version-…, Discord\app-1.0.…) and leave old shortcuts pointing at a deleted one.
/// </summary>
public static class ShortcutRepair
{
    /// <summary>
    /// The local program or file the item finally starts, or null when Windows resolves it itself
    /// (web links, Steam links, Store apps, advertised installer shortcuts).
    /// </summary>
    public static string? LocalTarget(ShelfItem item)
    {
        var target = Environment.ExpandEnvironmentVariables(item.Target);
        if (target.Length == 0 || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || target.Contains("://") || !Path.IsPathRooted(target)) return null;
        var extension = Path.GetExtension(target).ToLowerInvariant();
        if (extension is ".url" or ".appref-ms") return null;
        if (extension != ".lnk" || !File.Exists(target)) return target;
        return ShellIntegration.ReadShortcut(target) is { Target.Length: > 0 } link ? Environment.ExpandEnvironmentVariables(link.Target) : null;
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// The same file in a sibling version folder, newest first. Only searches below the nearest existing
    /// folder of the app, never a broad root such as Program Files, so another app's file of the same name is not picked.
    /// </summary>
    public static string? FindMoved(string missing)
    {
        try
        {
            var full = Path.GetFullPath(missing);
            var anchor = Path.GetDirectoryName(Path.GetDirectoryName(full));
            var depth = 2;
            while (anchor is not null && !Directory.Exists(anchor) && depth < 4) { anchor = Path.GetDirectoryName(anchor); depth++; }
            if (anchor is null || !Directory.Exists(anchor) || IsBroad(anchor)) return null;
            // missing = anchor\<version>\<rest…>; candidates keep <rest…> and differ only in <version>.
            var parts = Path.GetRelativePath(anchor, full).Split(Path.DirectorySeparatorChar);
            if (parts.Length < 2) return null;
            var tail = string.Join(Path.DirectorySeparatorChar, parts[1..]);
            return Directory.EnumerateDirectories(anchor)
                .Where(version => AreVersions(parts[0], Path.GetFileName(version)))
                .Select(version => Path.Combine(version, tail))
                .Where(candidate => File.Exists(candidate) && !string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// "version-e380c8…" and "version-145f18…", "app-1.0.9001" and "app-1.0.9100": a shared prefix and digits
    /// in what differs. Two different games in steamapps\common ("GameA", "GameB") are not versions of each other.
    /// </summary>
    public static bool AreVersions(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && char.ToLowerInvariant(a[prefix]) == char.ToLowerInvariant(b[prefix])) prefix++;
        // Do not split inside a version number: "app-1.0.9001" vs "app-1.0.9100" share "app-1.0.9".
        while (prefix > 0 && (char.IsAsciiHexDigit(a[prefix - 1]) || a[prefix - 1] == '.')) prefix--;
        var restA = a[prefix..]; var restB = b[prefix..];
        if (restA.Length == 0 || restB.Length == 0 || !restA.Any(char.IsDigit) || !restB.Any(char.IsDigit)) return false;
        static bool VersionLike(string text) => text.All(c => char.IsAsciiHexDigit(c) || c is '.' or '-' or '_');
        return (prefix >= 2 || (VersionLike(a) && VersionLike(b))) && VersionLike(restA) && VersionLike(restB);
    }

    private static bool IsBroad(string folder)
    {
        static string Normal(string path) => Path.GetFullPath(path).TrimEnd('\\');
        var path = Normal(folder);
        if (Path.GetPathRoot(path)?.TrimEnd('\\') == path) return true;
        var broad = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments
        }.Select(Environment.GetFolderPath).Where(x => x.Length > 0).Select(Normal);
        return broad.Contains(path, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(path, Normal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")), StringComparison.OrdinalIgnoreCase);
    }
}
