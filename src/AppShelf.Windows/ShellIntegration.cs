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

    public static void Initialize(string? appId = null) => SetCurrentProcessExplicitAppUserModelID(appId ?? ApplicationId);

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

    public static string CreateFolderShortcut(ShelfItem folder, string directory, string dataPath, string? iconPath = null,
        string surface = "taskbar", string? existingPath = null, bool friendlyName = false)
    {
        Directory.CreateDirectory(directory);
        var safeName = string.Concat(folder.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safeName.Length == 0) safeName = "Папка";
        if (safeName.Length > 90) safeName = safeName[..90];
        var shortcutPath = existingPath ?? Path.Combine(directory, friendlyName ? safeName + ".lnk" : $"{safeName} — {folder.Id.ToString("N")[..6]}.lnk");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(shortcutPath)), Path.GetFullPath(directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ярлык должен находиться в выбранной папке.");
        if (File.Exists(shortcutPath) && !IsFolderShortcut(ReadShortcut(shortcutPath), folder.Id, dataPath))
        {
            if (existingPath is not null) throw new InvalidOperationException("Этот ярлык принадлежит другому приложению.");
            shortcutPath = Path.Combine(directory, $"{safeName} — {folder.Id:N}.lnk");
            if (File.Exists(shortcutPath) && !IsFolderShortcut(ReadShortcut(shortcutPath), folder.Id, dataPath))
                throw new InvalidOperationException("Не удалось выбрать свободное имя ярлыка.");
        }
        var existed = File.Exists(shortcutPath);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для ярлыков запустите AppShelf.exe напрямую.");
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(executable);
            link.SetArguments($"--folder {folder.Id:D} --surface {surface} --data-dir \"{dataPath.TrimEnd('\\')}\"");
            link.SetWorkingDirectory(AppContext.BaseDirectory);
            link.SetDescription("Открыть папку «" + folder.Name + "» в AppShelf");
            link.SetIconLocation(iconPath ?? executable, 0);
            var store = (IPropertyStore)link;
            SetProperty(store, AppIdKey, FolderAppId(folder.Id));
            store.Commit();
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
        NotifyShortcut(shortcutPath, existed ? 0x2000u : 2u);
        return shortcutPath;
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
        if (!File.Exists(path) || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
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
        if (shortcut is null || shortcut.AppId != FolderAppId(folderId) ||
            !string.Equals(shortcut.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
        var args = SplitArguments(shortcut.Arguments);
        string? Argument(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (!Guid.TryParse(Argument("--folder"), out var id) || id != folderId) return false;
        var directory = Argument("--data-dir");
        try { return directory is not null && string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(dataPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
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
            // A manually pinned window relaunches the folder, rather than its pinning dialog.
            SetProperty(store, new PropertyKey(AppIdKey.FormatId, 2),
                $"\"{Environment.ProcessPath}\" --folder {folder.Id:D} --surface taskbar --data-dir \"{dataPath.TrimEnd('\\')}\"");
            SetProperty(store, new PropertyKey(AppIdKey.FormatId, 3), iconPath + ",0");
            SetProperty(store, new PropertyKey(AppIdKey.FormatId, 4), folder.Name);
            SetProperty(store, AppIdKey, FolderAppId(folder.Id));
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    public static void RevealFile(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public static bool RunAtStartup
    {
        get
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("AppShelf") is not null; }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public static void SetRunAtStartup(bool enabled, string dataPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("AppShelf", $"\"{Environment.ProcessPath}\" --background --data-dir \"{dataPath.TrimEnd('\\')}\"");
        else key.DeleteValue("AppShelf", false);
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
