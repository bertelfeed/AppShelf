using System.Diagnostics;
using System.IO;
using AppShelf.Core;
using AppShelf.Windows;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int passed, failed;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--pipe-client", var directory, var command])
        {
            using var client = new SingleInstance(directory);
            client.ForwardAsync(command).GetAwaiter().GetResult();
            return client.IsPrimary ? 2 : 0;
        }
        var root = Path.Combine(Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "check-data")), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        static void Expect(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
        static void Reject(Action action)
        { try { action(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { return; } throw new Exception("Invalid operation was accepted"); }
        static ShelfConfiguration Example()
        {
            var config = new ShelfConfiguration();
            var outer = ShelfItem.Folder("Работа");
            var inner = ShelfItem.Folder("Инструменты");
            inner.Children.Add(ShelfItem.Application("Блокнот", Path.Combine(Environment.SystemDirectory, "notepad.exe")));
            outer.Children.Add(inner); outer.Children.Add(ShelfItem.Application("Проводник", "explorer.exe"));
            config.Items.Add(outer);
            return config;
        }
        Check("Nested folder traversal and breadcrumbs", () =>
        {
            var config = Example(); var inner = config.Items[0].Children[0];
            Expect(ShelfTree.Walk(config.Items).Count() == 4);
            Expect(ShelfTree.Breadcrumbs(config, inner.Id).Select(x => x.Name).SequenceEqual(new[] { "Работа", "Инструменты" }));
        });
        Check("Move folder, preserve ID and children", () =>
        {
            var config = Example(); var inner = config.Items[0].Children[0]; var id = inner.Id;
            ShelfTree.Move(config, inner.Id, null);
            Expect(config.Items.Count == 2 && config.Items[1].Id == id && config.Items[1].Children.Count == 1);
        });
        Check("Reject moving a folder into itself or descendants", () =>
        {
            var config = Example(); var outer = config.Items[0];
            Reject(() => ShelfTree.Move(config, outer.Id, outer.Id));
            Reject(() => ShelfTree.Move(config, outer.Id, outer.Children[0].Id));
            ShelfTree.Validate(config); Expect(config.Items.Count == 1);
        });
        Check("Reorder within a folder", () =>
        {
            var config = Example(); var outer = config.Items[0];
            var lastId = outer.Children[1].Id; ShelfTree.Move(config, lastId, outer.Id, outer.Children[0].Id);
            Expect(outer.Children[0].Id == lastId);
        });
        Check("Import remaps all IDs without changing source", () =>
        {
            var config = Example(); var old = ShelfTree.Walk(config.Items).Select(x => x.Id).ToHashSet();
            var copy = ConfigurationStore.CopyItems(config.Items);
            Expect(ShelfTree.Walk(copy).All(x => !old.Contains(x.Id)));
            Expect(config.Items[0].Name == copy[0].Name && old.Contains(config.Items[0].Id));
        });
        Check("Reject invalid schema, duplicate IDs and deep nesting", () =>
        {
            var config = Example(); config.SchemaVersion = 999; Reject(() => ShelfTree.Validate(config));
            config = Example(); config.Items.Add(config.Items[0]); Reject(() => ShelfTree.Validate(config));
            config = new(); var cursor = config.Items;
            for (var i = 0; i < 35; i++) { var folder = ShelfItem.Folder("x"); cursor.Add(folder); cursor = folder.Children; }
            Reject(() => ShelfTree.Validate(config));
        });
        Check("Atomic save, backup, recovery and corrupt-file preservation", () =>
        {
            var store = new ConfigurationStore(Path.Combine(root, Guid.NewGuid().ToString("N")));
            var config = Example(); store.Save(config);
            config.Items[0].Name = "Изменено"; store.Save(config);
            Expect(store.Load().Items[0].Name == "Изменено");
            File.WriteAllText(store.FilePath, "{interrupted");
            var recovered = store.Load();
            Expect(recovered.Items[0].Name == "Работа" && store.RecoveryMessage is not null);
            Expect(Directory.GetFiles(store.DirectoryPath, "shelf.corrupt-*.json").Length == 1);
            store.Save(recovered);
            Expect(store.Load().Items[0].Name == "Работа");
            Expect(ConfigurationStore.Read(store.BackupPath).Items[0].Name == "Работа");
        });
        Check("Invalid save leaves the last good configuration untouched", () =>
        {
            var store = new ConfigurationStore(Path.Combine(root, Guid.NewGuid().ToString("N")));
            var config = Example(); store.Save(config); config.Items[0].Settings.Columns = 99;
            Reject(() => store.Save(config)); Expect(store.Load().Items[0].Settings.Columns == 5);
        });
        Check("Single-instance routing delivers commands across processes", () =>
        {
            var path = Path.Combine(root, "pipe-" + Guid.NewGuid().ToString("N"));
            using var primary = new SingleInstance(path); Expect(primary.IsPrimary);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = primary.ListenAsync(message => received.TrySetResult(message));
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { "--pipe-client", path, "folder:test" }, UseShellExecute = false, CreateNoWindow = true
            })!;
            Expect(child.WaitForExit(8000), "Client timed out"); Expect(child.ExitCode == 0);
            Expect(received.Task.Wait(3000) && received.Task.Result == "folder:test");
        });
        Check("Windows shortcut preserves arguments and folder AppUserModelID", () =>
        {
            var folder = ShelfItem.Folder("Папка с пробелами");
            var path = ShellIntegration.CreateFolderShortcut(folder, root, root);
            Expect(File.Exists(path));
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(path);
            Expect(((string)link.Arguments).Contains(folder.Id.ToString("D")));
            Expect(((string)link.TargetPath).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            dynamic explorer = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic directory = explorer.NameSpace(root);
            dynamic item = directory.ParseName(Path.GetFileName(path));
            string appId = item.ExtendedProperty("System.AppUserModel.ID");
            Expect(appId == ShellIntegration.FolderAppId(folder.Id), appId);
            foreach (object obj in new object[] { item, directory, explorer }) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(obj);
        });
        Check("Shell icon extraction returns a frozen image", () =>
        {
            using var icons = new IconService(root);
            var icon = icons.GetAsync(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            Expect(icon.Wait(8000), "Icon timed out"); Expect(icon.Result is { IsFrozen: true });
        });
        Check("Resolve Win32 known-folder paths and preserve UWP identifiers", () =>
        {
            var path = ShellIntegration.ResolveAppTarget(@"{6D809377-6AF0-444B-8957-A3773F02200E}\7-Zip\7zFM.exe");
            Expect(Path.IsPathRooted(path) && path.EndsWith(@"7-Zip\7zFM.exe"));
            Expect(ShellIntegration.ResolveAppTarget("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") ==
                @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        });
        Check("Folder preview uses nested applications, deduplicates and preserves order", () =>
        {
            var folder = ShelfItem.Folder("Outer");
            var nested = ShelfItem.Folder("Nested");
            nested.Children.Add(ShelfItem.Application("One", "one.exe"));
            nested.Children.Add(ShelfItem.Application("Two", "two.exe"));
            folder.Children.Add(nested);
            folder.Children.Add(ShelfItem.Application("Duplicate", "ONE.EXE"));
            folder.Children.Add(ShelfItem.Application("Three", "three.exe"));
            folder.Children.Add(ShelfItem.Application("Four", "four.exe"));
            folder.Children.Add(ShelfItem.Application("Five", "five.exe"));
            Expect(FolderPreview.Applications(folder).Select(x => x.Name).SequenceEqual(new[] { "One", "Two", "Three", "Four" }));
        });
        Check("ICO contains all Shell sizes and real artwork in every frame", () =>
        {
            var apps = new ImageSource[] {
                new DrawingImage(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 64, 64)))),
                new DrawingImage(new GeometryDrawing(Brushes.DodgerBlue, null, new RectangleGeometry(new Rect(0, 0, 64, 64))))
            };
            var path = Path.Combine(root, "multi-size.ico");
            FolderArtwork.WriteIcon(path, apps);
            using var stream = File.OpenRead(path);
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Expect(decoder.Frames.Select(x => x.PixelWidth).Order().SequenceEqual(FolderArtwork.Sizes.Order()));
            foreach (var frame in decoder.Frames)
            {
                var image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                var red = 0; var blue = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                { if (pixels[i + 2] > 180 && pixels[i] < 80) red++; if (pixels[i] > 180 && pixels[i + 2] < 80) blue++; }
                Expect(red > 0 && blue > 0, "Missing content at size " + frame.PixelWidth);
            }
        });
        Check("Desktop shortcut keeps native icon, surface and identity; refresh preserves path", () =>
        {
            var folder = ShelfItem.Folder("Friendly name");
            var desktop = Path.Combine(root, "fake-desktop");
            var icon = Path.Combine(root, "multi-size.ico");
            var path = ShellIntegration.CreateFolderShortcut(folder, desktop, root, icon, "desktop", friendlyName: true);
            Expect(Path.GetFileName(path) == "Friendly name.lnk");
            var details = ShellIntegration.ReadShortcut(path)!;
            Expect(ShellIntegration.IsFolderShortcut(details, folder.Id, root));
            Expect(details.Arguments.Contains("--surface desktop"));
            Expect(details.IconPath == icon);
            folder.Name = "Renamed";
            var refreshed = ShellIntegration.CreateFolderShortcut(folder, desktop, root, icon, "desktop", path);
            Expect(refreshed == path && File.Exists(path));
        });
        Check("Publishing never overwrites another app's shortcut", () =>
        {
            var desktop = Path.Combine(root, "collision-desktop");
            Directory.CreateDirectory(desktop);
            var protectedPath = Path.Combine(desktop, "Same name.lnk");
            File.WriteAllText(protectedPath, "Unrelated content");
            var folder = ShelfItem.Folder("Same name");
            var path = ShellIntegration.CreateFolderShortcut(folder, desktop, root, friendlyName: true);
            Expect(path != protectedPath && File.ReadAllText(protectedPath) == "Unrelated content");
        });
        Check("Native taskbar pinning capability can be queried without prompting", () =>
        {
            using var pin = new NativeTaskbarPin();
            Console.WriteLine("INFO Taskbar API supported=" + pin.Supported + ", pinningAllowed=" + pin.Allowed);
        });
        Check("Schema 1 migration moves layout into independent folder settings", () =>
        {
            var path = Path.Combine(root, "legacy.json");
            File.WriteAllText(path, """{"SchemaVersion":1,"Appearance":2,"Columns":4,"CloseAfterLaunch":false,"ShowLabels":true,"Items":[{"Name":"One","Kind":0},{"Name":"Two","Kind":0}]}""");
            var config = ConfigurationStore.Read(path);
            Expect(config.SchemaVersion == 2 && config.Columns is null && config.ShowLabels is null);
            Expect(config.Items.All(x => x.Settings.Columns == 4 && x.Settings.Appearance == Appearance.Dark &&
                !x.Settings.CloseAfterLaunch && !x.Settings.ShowLabels));
            config.Items[0].Settings.Columns = 7;
            Expect(config.Items[1].Settings.Columns == 4);
        });
        Check("Folder settings and taskbar placement survive save and reload", () =>
        {
            var store = new ConfigurationStore(Path.Combine(root, "folder-settings"));
            var config = Example();
            config.Items[0].Settings.Columns = 3;
            config.Items[0].Settings.IconSize = 64;
            config.Items[0].Settings.ShowOnTaskbar = true;
            config.Items[0].Children[0].Settings.Appearance = Appearance.Light;
            store.Save(config);
            var loaded = store.Load();
            Expect(loaded.Items[0].Settings.Columns == 3 && loaded.Items[0].Settings.IconSize == 64 &&
                loaded.Items[0].Settings.ShowOnTaskbar);
            Expect(loaded.Items[0].Children[0].Settings.Appearance == Appearance.Light &&
                loaded.Items[0].Children[0].Settings.Columns == 5);
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(store.FilePath));
            Expect(!json.RootElement.TryGetProperty("Columns", out _));
        });
        Check("Desktop and taskbar scales persist independently for each folder", () =>
        {
            var store = new ConfigurationStore(Path.Combine(root, "scales"));
            var config = Example();
            var folder = config.Items[0];
            folder.Settings.DesktopIconScale = 150;
            folder.Settings.TaskbarIconScale = 50;
            store.Save(config);
            var loaded = store.Load();
            var settings = loaded.Items[0].Settings;
            Expect(settings.IconSizeFor(true) == 60 && settings.IconSizeFor(false) == 20);
            Expect(loaded.Items[0].Children[0].Settings.DesktopIconScale == 100 &&
                loaded.Items[0].Children[0].Settings.TaskbarIconScale == 80);
            foreach (var invalid in new[] { 0, 49, 201, int.MaxValue })
            {
                settings.DesktopIconScale = invalid; Reject(() => ShelfTree.Validate(loaded));
                settings.DesktopIconScale = 150;
                settings.TaskbarIconScale = invalid; Reject(() => ShelfTree.Validate(loaded));
                settings.TaskbarIconScale = 50;
            }
        });
        Check("Version 1.2 settings gain scale defaults without losing icon size or placement", () =>
        {
            var path = Path.Combine(root, "schema2-old.json");
            File.WriteAllText(path, """{"SchemaVersion":2,"Items":[{"Name":"One","Kind":0,"Settings":{"IconSize":64,"ShowOnTaskbar":true}}]}""");
            var loaded = ConfigurationStore.Read(path).Items[0].Settings;
            Expect(loaded.IconSize == 64 && loaded.ShowOnTaskbar && loaded.DesktopIconScale == 100 && loaded.TaskbarIconScale == 80);
        });
        RunWindowChecks(root, Check, Expect);
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void RunWindowChecks(string root, Action<string, Action> check, Action<bool, string> expect)
    {
        var application = new WindowTestApplication();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        AppShelf.App.Theme.Apply(Appearance.Dark);
        var session = new AppShelf.App.ShelfSession(Path.Combine(root, "window-profile"));
        typeof(AppShelf.App.App).GetProperty("Session")!.SetValue(application, session);
        var folder = ShelfItem.Folder("Native taskbar test");
        folder.Settings.Appearance = Appearance.Light;
        folder.Settings.ShowOnTaskbar = true;
        folder.Children.Add(ShelfItem.Application("Notepad", Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        session.Change(config => config.Items.Add(folder));
        AppShelf.App.TaskbarFolders? manager = null;
        check("A real minimized taskbar window is created without pinning APIs", () =>
        {
            manager = new AppShelf.App.TaskbarFolders(session);
            typeof(AppShelf.App.App).GetProperty("TaskbarFolders")!.SetValue(application, manager);
            PumpUntil(() => application.Windows.OfType<AppShelf.App.FolderTaskbarWindow>().Any(x => x.Icon is not null));
            var window = application.Windows.OfType<AppShelf.App.FolderTaskbarWindow>().Single();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            expect(IsWindowVisible(handle) && IsIconic(handle), "Taskbar window must be visible and minimized");
            expect(window.ShowInTaskbar && window.Title == folder.Name, "Incorrect taskbar identity");
        });
        check("Taskbar restore opens a minimal folder menu with only title and icons", () =>
        {
            var window = application.Windows.OfType<AppShelf.App.FolderTaskbarWindow>().Single();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            SendMessage(handle, 0x112, new IntPtr(0xF120), IntPtr.Zero);
            PumpUntil(() => application.Windows.OfType<AppShelf.App.FolderPopup>().Any(x => x.IsVisible));
            var popup = application.Windows.OfType<AppShelf.App.FolderPopup>().Single(x => x.IsVisible);
            var elements = Descendants(popup).ToList();
            expect(elements.OfType<System.Windows.Controls.TextBox>().Count() == 0, "Search field leaked into popup");
            expect(elements.OfType<System.Windows.Controls.Button>().Count() == folder.Children.Count + 1, "Editor actions leaked into popup");
            expect(elements.OfType<System.Windows.Controls.TextBlock>().Count() == 1, "Popup has extra labels or counters");
            expect(popup.ThemeMode == ThemeMode.Light && application.ThemeMode == ThemeMode.Dark, "Folder theme changed global editor theme");
        });
        check("Minimize-all closes the folder menu instead of opening it", () =>
        {
            var window = application.Windows.OfType<AppShelf.App.FolderTaskbarWindow>().Single();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            SendMessage(handle, 0x112, new IntPtr(0xF020), IntPtr.Zero);
            expect(!application.Windows.OfType<AppShelf.App.FolderPopup>().Any(x => x.IsVisible), "Minimize did not close the menu");
            SendMessage(handle, 0x112, new IntPtr(0xF020), IntPtr.Zero);
            expect(!application.Windows.OfType<AppShelf.App.FolderPopup>().Any(x => x.IsVisible), "Minimize opened the menu");
        });
        check("Menu uses independent surface scales, including after editing while open", () =>
        {
            var popup = application.Windows.OfType<AppShelf.App.FolderPopup>().Single();
            popup.Open(folder.Id, true);
            popup.UpdateLayout();
            expect(Descendants(popup).OfType<System.Windows.Controls.Image>().Single().Width == 40, "Wrong desktop default");
            popup.Hide(); popup.Open(folder.Id, false);
            popup.UpdateLayout();
            expect(Descendants(popup).OfType<System.Windows.Controls.Image>().Single().Width == 32, "Taskbar default should be smaller");
            session.Change(config =>
            {
                var current = ShelfTree.Find(config, folder.Id)!;
                current.Settings.DesktopIconScale = 150; current.Settings.TaskbarIconScale = 50;
            });
            popup.UpdateLayout();
            expect(Descendants(popup).OfType<System.Windows.Controls.Image>().Single().Width == 20, "Taskbar scale did not update");
            popup.Hide(); popup.Open(folder.Id, true);
            popup.UpdateLayout();
            expect(Descendants(popup).OfType<System.Windows.Controls.Image>().Single().Width == 60, "Desktop scale was changed by taskbar scale");
            popup.Hide();
        });
        check("Nested folders retain the launch surface and Escape closes the whole menu", () =>
        {
            var nested = ShelfItem.Folder("Nested scale check");
            nested.Settings.TaskbarIconScale = 200; nested.Settings.DesktopIconScale = 50;
            nested.Children.Add(ShelfItem.Application("Notepad", Path.Combine(Environment.SystemDirectory, "notepad.exe")));
            session.Change(config => ShelfTree.Find(config, folder.Id)!.Children.Add(nested));
            var popup = application.Windows.OfType<AppShelf.App.FolderPopup>().Single();
            popup.Open(folder.Id, false); popup.UpdateLayout();
            Descendants(popup).OfType<System.Windows.Controls.Button>().Single(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == nested.Name)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            popup.UpdateLayout();
            expect(popup.Title == nested.Name && Descendants(popup).OfType<System.Windows.Controls.Image>().Single().Width == 80,
                "Nested menu did not use its own taskbar scale");
            popup.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(popup), Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            expect(!popup.IsVisible, "Escape only went back instead of closing the menu");
        });
        check("Foreground monitor detects switching windows even if the popup never had focus", () =>
        {
            var inactive = new Window { Title = "Inactive popup check", Width = 160, Height = 100, ShowActivated = false, ShowInTaskbar = false };
            var other = new Window { Title = "Foreground check", Width = 160, Height = 100, ShowInTaskbar = false };
            inactive.Show();
            var dismissed = false;
            using var monitor = new PopupDismissMonitor(new System.Windows.Interop.WindowInteropHelper(inactive).Handle, () => dismissed = true);
            try
            {
                expect(!inactive.IsActive, "Test popup unexpectedly has focus");
                other.Show(); other.Activate();
                PumpUntil(() => dismissed);
            }
            finally { other.Close(); inactive.Close(); }
        });
        check("Title click closes the root folder without adding menu controls", () =>
        {
            var popup = application.Windows.OfType<AppShelf.App.FolderPopup>().Single();
            popup.Open(folder.Id);
            Descendants(popup).OfType<System.Windows.Controls.Button>().First().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            expect(!popup.IsVisible, "Title click did not close root folder");
        });
        check("Disabling taskbar placement removes its window and preserves other settings", () =>
        {
            session.Change(config => ShelfTree.Find(config, folder.Id)!.Settings.ShowOnTaskbar = false);
            expect(!application.Windows.OfType<AppShelf.App.FolderTaskbarWindow>().Any(), "Taskbar window was not removed");
            expect(ShelfTree.Find(session.Configuration, folder.Id)!.Settings.Appearance == Appearance.Light, "Folder settings changed");
        });
        check("Desktop single-click hook installs and can be disposed", () =>
        {
            using var clicks = new DesktopSingleClick(session.Store.DirectoryPath, _ => { });
            expect(clicks.Installed, "Windows rejected the single-click hook");
        });
        manager?.Dispose();
        foreach (var window in application.Windows.Cast<Window>().ToList()) window.Close();
        session.Icons.Dispose();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
        {
            var child = VisualTreeHelper.GetChild(item, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void PumpUntil(Func<bool> condition)
    {
        if (condition()) return;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!condition()) throw new Exception("Window check timed out");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    private sealed class WindowTestApplication : AppShelf.App.App
    {
        // Application queues startup from its constructor; never run production startup in a component test.
        protected override void OnStartup(StartupEventArgs e) { }
    }
}
