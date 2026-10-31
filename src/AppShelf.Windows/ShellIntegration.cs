using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using AppShelf.Core;
using Microsoft.Win32;

namespace AppShelf.Windows;

public static class ShellIntegration
{
    public const string ApplicationId = "AppShelf.Desktop";
    public static string FolderAppId(Guid id) => "AppShelf.Folder." + id.ToString("N");

    /// <summary>A packaged process keeps the package's own AppUserModelID, so its editor groups with its Start entry.</summary>
    public static void Initialize(string? appId = null) { if (!AppLocations.IsPackaged) SetCurrentProcessExplicitAppUserModelID(appId ?? ApplicationId); }

    /// <summary>Program that folder shortcuts and pinned folder buttons start.</summary>
    public static string? Launcher => AppLocations.IsPackaged ? AppLocations.Launcher(AppLocations.AppAlias, "AppShelf.exe") : Environment.ProcessPath;

    private static bool IsLauncher(string target) =>
        string.Equals(target, Launcher, StringComparison.OrdinalIgnoreCase) || string.Equals(target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
        // Written by an earlier package version, whose folder an update removed; the refresh rewrites it to the alias.
        (AppLocations.IsPackaged && target.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) &&
         Path.GetFileName(target).Equals("AppShelf.exe", StringComparison.OrdinalIgnoreCase));

    public static void Launch(ShelfItem item, bool elevated = false)
    {
        RefreshShortcutCopy(item);
        var target = Environment.ExpandEnvironmentVariables(item.Target);
        if (target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { target }, UseShellExecute = true });
            return;
        }
        var info = new ProcessStartInfo(target)
        {
            Arguments = item.Arguments,
            UseShellExecute = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(item.WorkingDirectory) ? "" : Environment.ExpandEnvironmentVariables(item.WorkingDirectory)
        };
        if (elevated || item.RunAsAdministrator) info.Verb = "runas";
        Process.Start(info);
    }

    /// <summary>
    /// Items taken from the desktop start from AppShelf's own copy of the shortcut. Apps such as
    /// launchers rewrite their shortcut on update, so a newer original replaces the copy.
    /// Returns true when the copy was refreshed.
    /// </summary>
    public static bool RefreshShortcutCopy(ShelfItem item)
    {
        if (item.IsFolder || item.Origin.Length == 0 || string.Equals(item.Origin, item.Target, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(item.Origin), Path.GetExtension(item.Target), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (!File.Exists(item.Origin) || (File.Exists(item.Target) && File.GetLastWriteTimeUtc(item.Origin) <= File.GetLastWriteTimeUtc(item.Target))) return false;
            File.Copy(item.Origin, item.Target, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>File name (without .lnk) of a folder's friendly shortcut; Explorer shows it as the icon label.</summary>
    public static string FolderShortcutName(ShelfItem folder)
    {
        var safeName = string.Concat(folder.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safeName.Length == 0) safeName = L.Get("Folder");
        return safeName.Length > 90 ? safeName[..90] : safeName;
    }

    public static string CreateFolderShortcut(ShelfItem folder, string directory, string dataPath, string? iconPath = null,
        string surface = "taskbar", string? existingPath = null, bool friendlyName = false)
    {
        Directory.CreateDirectory(directory);
        var safeName = FolderShortcutName(folder);
        var shortcutPath = existingPath ?? Path.Combine(directory, friendlyName ? safeName + ".lnk" : $"{safeName} — {folder.Id.ToString("N")[..6]}.lnk");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(shortcutPath)), Path.GetFullPath(directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.Get("TheShortcutMustBeInsideTheSelectedDirectory"));
        if (File.Exists(shortcutPath) && !IsFolderShortcut(ReadShortcut(shortcutPath), folder.Id, dataPath))
        {
            if (existingPath is not null) throw new InvalidOperationException(L.Get("ThisShortcutBelongsToAnotherApplication"));
            shortcutPath = Path.Combine(directory, $"{safeName} — {folder.Id:N}.lnk");
            if (File.Exists(shortcutPath) && !IsFolderShortcut(ReadShortcut(shortcutPath), folder.Id, dataPath))
                throw new InvalidOperationException(L.Get("CouldNotFindAnAvailableShortcutName"));
        }
        var existed = File.Exists(shortcutPath);
        var executable = Launcher ?? throw new InvalidOperationException(L.Get("CouldNotDetermineTheApplicationPath"));
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.Get("LaunchAppShelfExeDirectlyToUseShortcuts"));
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(executable);
            link.SetArguments($"--folder {folder.Id:D} --surface {surface} --data-dir \"{dataPath.TrimEnd('\\')}\"");
            // The package folder changes with every update; the alias needs no working directory.
            link.SetWorkingDirectory(AppLocations.IsPackaged ? "" : AppContext.BaseDirectory);
            link.SetDescription(L.Get("OpenFolder2") + folder.Name + L.Get("InAppShelf"));
            link.SetIconLocation(iconPath ?? Environment.ProcessPath ?? executable, 0);
            var store = (IPropertyStore)link;
            SetProperty(store, AppIdKey, FolderAppId(folder.Id));
            store.Commit();
            SaveReplacing(link, shortcutPath, folder.Id, dataPath);
        }
        finally { Marshal.FinalReleaseComObject(link); }
        NotifyShortcut(shortcutPath, existed ? 0x2000u : 2u);
        return shortcutPath;
    }

    /// <summary>
    /// Writes a hidden draft on the destination volume, checks it, then swaps it in with one rename.
    /// Rewriting a shortcut in place on a OneDrive desktop once left every folder shortcut filled with zeros.
    /// A sibling draft also works when the desktop and the profile live on different drives.
    /// </summary>
    private static void SaveReplacing(IShellLinkW link, string shortcutPath, Guid folderId, string dataPath)
    {
        // File.Move across volumes copies and deletes; only a sibling draft guarantees a rename.
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(shortcutPath))!, $".AppShelf-{Guid.NewGuid():N}.tmp");
        try
        {
            ((IPersistFile)link).Save(temporary, true);
            File.SetAttributes(temporary, FileAttributes.Hidden | FileAttributes.Temporary);
            if (IsDamagedShortcut(temporary) || !IsFolderShortcut(ReadLink(temporary), folderId, dataPath))
                throw new IOException(L.F("CouldNotWriteShortcut", shortcutPath));
            for (var attempt = 0; ; attempt++)
            {
                var existed = File.Exists(shortcutPath);
                if (existed && !IsFolderShortcut(ReadShortcut(shortcutPath), folderId, dataPath))
                    throw new InvalidOperationException(L.Get("ThisShortcutBelongsToAnotherApplication"));
                try { File.Move(temporary, shortcutPath, existed); break; }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4 && File.Exists(temporary))
                { Thread.Sleep(40 * (attempt + 1)); }
            }
            File.SetAttributes(shortcutPath, FileAttributes.Normal);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// A file named .lnk without the Shell Link header (4C 00 00 00), e.g. zeroed by a sync conflict.
    /// Such a file opens nothing and shows a blank icon.
    /// </summary>
    public static bool IsDamagedShortcut(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) < 4 || header[0] != 0x4C || header[1] != 0 || header[2] != 0 || header[3] != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public sealed record ShortcutDetails(string Path, string Target, string Arguments, string IconPath, string AppId, string WorkingDirectory = "", int IconIndex = 0);

    /// <summary>Points an existing shortcut at another program, keeping its arguments, AppID and other properties.</summary>
    public static void RetargetShortcut(string path, string target, string workingDirectory, string iconPath, int iconIndex)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 2); // STGM_READWRITE
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetIconLocation(iconPath, iconIndex);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    public static void CreateShortcut(string path, string target, string workingDirectory = "")
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    public static ShortcutDetails? ReadShortcut(string path)
    {
        return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ReadLink(path) : null;
    }
    private static ShortcutDetails? ReadLink(string path)
    {
        if (!File.Exists(path)) return null;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(32768); var args = new StringBuilder(32768); var icon = new StringBuilder(32768); var directory = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            link.GetArguments(args, args.Capacity); link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            link.GetWorkingDirectory(directory, directory.Capacity);
            var key = AppIdKey;
            ((IPropertyStore)link).GetValue(ref key, out var value);
            string appId;
            try { appId = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
            return new(path, target.ToString(), args.ToString(), icon.ToString(), appId, directory.ToString(), iconIndex);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { return null; }
        finally { Marshal.FinalReleaseComObject(link); }
    }
    public static bool IsFolderShortcut(ShortcutDetails? shortcut, Guid folderId, string dataPath)
    {
        if (shortcut is null || shortcut.AppId != FolderAppId(folderId) || !IsLauncher(shortcut.Target)) return false;
        var args = SplitArguments(shortcut.Arguments);
        string? Argument(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (!Guid.TryParse(Argument("--folder"), out var id) || id != folderId) return false;
        // Without --data-dir the shortcut opens the default profile (a pinned folder's relaunch command omits it).
        var directory = Argument("--data-dir");
        try { return directory is null ? IsDefaultProfile(dataPath) : string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(dataPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    private static bool IsDefaultProfile(string dataPath) =>
        string.Equals(Path.GetFullPath(dataPath).TrimEnd('\\'), Path.GetFullPath(AppLocations.DefaultDataPath()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static string[] SplitArguments(string command)
    {
        var buffer = CommandLineToArgvW("AppShelf.exe " + command, out var count);
        if (buffer == IntPtr.Zero) return [];
        try { return Enumerable.Range(1, Math.Max(0, count - 1)).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(buffer); }
    }
    public static void NotifyShortcut(string path, uint change = 0x2000) => SHChangeNotify(change, 0x2005, path, IntPtr.Zero);

    public static void SetWindowAppId(IntPtr window, Guid folderId)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        try { SetProperty(store, AppIdKey, FolderAppId(folderId)); }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    public static void SetFolderWindowIdentity(IntPtr window, ShelfItem folder, string dataPath, string iconPath)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        try
        {
            // A manually pinned window relaunches the folder, rather than its pinning dialog. Windows rejects a command
            // longer than MAX_PATH, and without it also the folder's name on its button; so the command leaves out
            // the defaults (taskbar surface, default profile). A still longer one keeps the button, unnamed and unpinnable.
            var command = $"\"{Launcher}\" --folder {folder.Id:D}";
            if (!IsDefaultProfile(dataPath)) command += $" --data-dir \"{dataPath.TrimEnd('\\')}\"";
            try
            {
                SetProperty(store, new PropertyKey(AppIdKey.FormatId, 2), command);
                SetProperty(store, new PropertyKey(AppIdKey.FormatId, 3), iconPath + ",0");
                SetProperty(store, new PropertyKey(AppIdKey.FormatId, 4), folder.Name);
            }
            catch (COMException) { }
            // Set regardless: without its own AppID the folder's window would join one shared AppShelf button.
            SetProperty(store, AppIdKey, FolderAppId(folder.Id));
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    public static void RevealFile(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public static bool RunAtStartup
    {
        get
        {
            if (AppLocations.IsPackaged)
            {
                try { return PackageStartup.IsEnabled(PackageStartup.Read()); }
                catch (Exception ex) when (ex is COMException or TimeoutException) { return false; }
            }
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("AppShelf") is not null; }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public static void SetRunAtStartup(bool enabled, string dataPath)
    {
        if (AppLocations.IsPackaged)
        {
            // Windows starts the package without arguments; App recognizes the startup task and stays in the background.
            var state = PackageStartup.Set(enabled);
            if (enabled && state == PackageStartup.State.DisabledByUser)
                throw new InvalidOperationException(L.Get("StartupDisabledInSettings"));
            if (PackageStartup.IsEnabled(state) != enabled)
                throw new InvalidOperationException(L.Get("StartupManagedByWindows"));
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("AppShelf", $"\"{Environment.ProcessPath}\" --background --data-dir \"{dataPath.TrimEnd('\\')}\"");
        else key.DeleteValue("AppShelf", false);
    }

    public static Task<bool> ReadRunAtStartupAsync() => AppLocations.IsPackaged
        ? Task.Run(() => PackageStartup.IsEnabled(PackageStartup.Read())) : Task.FromResult(RunAtStartup);

    public static async Task SetRunAtStartupAsync(bool enabled, string dataPath)
    {
        if (AppLocations.IsPackaged) await Task.Run(() => SetRunAtStartup(enabled, dataPath));
        else SetRunAtStartup(enabled, dataPath);
    }

    public static Task<List<ShelfItem>> InstalledApplicationsAsync() => RunSta(() =>
    {
        var result = new List<ShelfItem>();
        object? shellObject = null, folderObject = null, itemsObject = null;
        try
        {
            shellObject = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic shell = shellObject!;
            folderObject = shell.NameSpace("shell:AppsFolder");
            if (folderObject is null) return result;
            dynamic folder = folderObject;
            itemsObject = folder.Items();
            dynamic items = itemsObject;
            for (var i = 0; i < (int)items.Count; i++)
            {
                object? itemObject = null;
                try
                {
                    itemObject = items.Item(i);
                    dynamic item = itemObject;
                    string name = item.Name;
                    string target = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target)) continue;
                    target = ResolveAppTarget(target);
                    result.Add(ShelfItem.Application(name, target));
                }
                catch (COMException) { }
                finally { if (itemObject is not null) Marshal.FinalReleaseComObject(itemObject); }
            }
        }
        finally
        {
            foreach (var obj in new[] { itemsObject, folderObject, shellObject })
                if (obj is not null) Marshal.FinalReleaseComObject(obj);
        }
        return result.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).OrderBy(x => x.Name).ToList();
    });

    public static string ResolveAppTarget(string target)
    {
        target = Environment.ExpandEnvironmentVariables(target);
        if (Path.IsPathRooted(target)) return target;
        if (target.Length > 38 && target[38] == '\\' && Guid.TryParse(target[..38], out var folderId) &&
            SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pointer) == 0)
        {
            try
            {
                var folder = Marshal.PtrToStringUni(pointer);
                if (folder is not null) return Path.Combine(folder, target[39..]);
            }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        return "shell:AppsFolder\\" + target;
    }

    public static Task<T> RunSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static readonly PropertyKey AppIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static void SetProperty(IPropertyStore store, PropertyKey key, string value)
    {
        var variant = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref variant); }
        finally { Marshal.FreeCoTaskMem(variant.Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)] public struct PropertyKey(Guid formatId, uint id) { public Guid FormatId = formatId; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint change, uint flags, string path, IntPtr other);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
