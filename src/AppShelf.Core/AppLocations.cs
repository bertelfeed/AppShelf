using System.Runtime.InteropServices;

namespace AppShelf.Core;

/// <summary>
/// Where AppShelf keeps its profile and which program other processes start. Three layouts exist:
/// a portable copy (portable.flag next to the program), the Microsoft Store package and a plain installation.
/// </summary>
public static class AppLocations
{
    public const string AppAlias = "appshelf.exe";
    public const string McpAlias = "appshelf-mcp.exe";

    /// <summary>Package family name when running from the MSIX package; null otherwise.</summary>
    public static string? PackageFamilyName { get; } = ReadPackageFamilyName();
    public static bool IsPackaged => PackageFamilyName is not null;

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DefaultDataPath()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))) return Path.Combine(AppContext.BaseDirectory, "data");
        // A package's writes to LocalAppData\AppShelf would be redirected into a private copy that Explorer cannot see,
        // and the desktop shows folder icons stored in the profile. LocalState is the package's own real folder.
        return IsPackaged ? Path.Combine(LocalAppData, "Packages", PackageFamilyName!, "LocalState") : Path.Combine(LocalAppData, "AppShelf");
    }

    /// <summary>Profile of a plain installation, which the Store version takes over on its first start.</summary>
    public static string LegacyDataPath => Path.Combine(LocalAppData, "AppShelf");

    /// <summary>
    /// Program written into shortcuts and started by other processes. The Store installs every update into a new folder,
    /// so the package is started through its app execution alias, which keeps one path.
    /// </summary>
    public static string Launcher(string alias, string fileName) =>
        IsPackaged ? Path.Combine(LocalAppData, "Microsoft", "WindowsApps", alias) : Path.Combine(AppContext.BaseDirectory, fileName);

    private static string? ReadPackageFamilyName()
    {
        try
        {
            var length = 0;
            // APPMODEL_ERROR_NO_PACKAGE (15700) for an ordinary process; ERROR_INSUFFICIENT_BUFFER (122) reports the length.
            if (GetCurrentPackageFamilyName(ref length, null) != 122) return null;
            var buffer = new char[length];
            return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? new string(buffer, 0, length - 1) : null;
        }
        catch (EntryPointNotFoundException) { return null; }
        catch (DllNotFoundException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, [Out] char[]? name);
}
