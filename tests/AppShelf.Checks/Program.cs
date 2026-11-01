using System.Diagnostics;
using System.IO;
using AppShelf.Core;
using AppShelf.Windows;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Localization = AppShelf.Core.Localization;

internal static class Program
{
    private static int passed, failed, skipped;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--pipe-client", var directory, var command])
        {
            using var client = new SingleInstance(directory);
            client.ForwardAsync(command).GetAwaiter().GetResult();
            return client.IsPrimary ? 2 : 0;
        }
        Localization.SetLanguage("ru-RU"); // Existing Russian assertions must not depend on the runner's OS language.
        var root = Path.Combine(Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "check-data")), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(string name, Action test)
        {
            if (Environment.GetEnvironmentVariable("APPSHELF_SKIP_FOREGROUND_CHECKS") == "1" &&
                name is "Foreground monitor detects switching windows even if the popup never had focus" or
                    "A click on the folder's own button closes the menu instead of reopening it")
            {
                skipped++; Console.WriteLine("SKIP " + name + " (requires an interactive foreground desktop)"); return;
            }
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
            // Read back through the shell link and its property store, as the taskbar does. Script hosts and Explorer's
            // namespace are not available on every machine (hosted CI runners have no Explorer session).
            var link = ShellIntegration.ReadShortcut(path);
            Expect(link is not null, "The shortcut cannot be read: " + path);
            Expect(link!.Arguments.Contains(folder.Id.ToString("D")), "Arguments: " + link.Arguments);
            Expect(link.Target.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase), "Target: " + link.Target);
            Expect(link.AppId == ShellIntegration.FolderAppId(folder.Id), "AppUserModelID: " + link.AppId);
        });
        Check("Shell icon extraction returns a frozen image", () =>
        {
            using var icons = new IconService(root);
            var icon = icons.GetAsync(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            Expect(icon.Wait(8000), "Icon timed out"); Expect(icon.Result is { IsFrozen: true });
        });
        Check("A folder shows what is inside it, as on the desktop", () =>
        {
            using var icons = new IconService(Path.Combine(root, "folder-thumb-cache"));
            var withPicture = Path.Combine(root, "folder-thumb", "Pictures"); Directory.CreateDirectory(withPicture);
            var red = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 64 * 64).SelectMany(x => x).ToArray(), 256);
            using (var stream = File.Create(Path.Combine(withPicture, "red.png"))) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(red)); encoder.Save(stream); }
            var empty = Path.Combine(root, "folder-thumb", "Empty"); Directory.CreateDirectory(empty);
            int Reddish(string folder)
            {
                var task = icons.GetAsync(folder); Expect(task.Wait(15000) && task.Result is BitmapSource, "Folder picture timed out");
                var image = new FormatConvertedBitmap((BitmapSource)task.Result!, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                var count = 0; for (var i = 0; i < pixels.Length; i += 4) if (pixels[i + 2] > 200 && pixels[i + 1] < 60 && pixels[i] < 60 && pixels[i + 3] > 200) count++;
                return count;
            }
            using var advanced = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var iconsOnly = advanced?.GetValue("IconsOnly") is int value && value == 1;
            var inside = Reddish(withPicture);
            Expect(Reddish(empty) == 0, "An empty folder must not have content in its picture");
            Expect(iconsOnly || inside > 50, $"The folder picture does not show its contents ({inside} red pixels)");
        });
        Check("Refreshing icons reads them again instead of reusing the cache", () =>
        {
            using var icons = new IconService(Path.Combine(root, "icon-refresh"));
            var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var first = icons.GetAsync(notepad);
            Expect(first.Wait(8000) && first.Result is not null, "Icon timed out");
            Expect(ReferenceEquals(icons.GetAsync(notepad), first), "A fresh icon was not reused from memory");
            icons.Clear();
            var second = icons.GetAsync(notepad);
            Expect(!ReferenceEquals(second, first), "Refresh reused the old icon");
            Expect(second.Wait(8000) && second.Result is { IsFrozen: true }, "Refreshed icon was not read again");
        });
        Check("A small icon inside the Shell's large frame is cut out to fill the tile", () =>
        {
            static byte[] Canvas(uint fill) { var pixels = new byte[256 * 256 * 4]; for (var i = 0; i < pixels.Length; i += 4) BitConverter.GetBytes(fill).CopyTo(pixels, i); return pixels; }
            static void Paint(byte[] pixels, int x0, int y0, int size, uint color)
            { for (var y = y0; y < y0 + size; y++) for (var x = x0; x < x0 + size; x++) BitConverter.GetBytes(color).CopyTo(pixels, (y * 256 + x) * 4); }
            // Plain frame with a grey border and a 48 px icon in the middle, as Explorer draws Steam shortcuts.
            var framed = Canvas(0xFFFBFBFB);
            for (var i = 0; i < 256; i++) foreach (var (x, y) in new[] { (0, i), (1, i), (254, i), (255, i), (i, 0), (i, 1), (i, 254), (i, 255) })
                BitConverter.GetBytes(0xFFC8C8C8u).CopyTo(framed, (y * 256 + x) * 4);
            Paint(framed, 104, 104, 48, 0xFFB03020);
            Expect(IconService.SmallIconBounds(framed, 256, 256) is { Width: 48, Height: 48, X: 104, Y: 104 }, "Framed small icon was not found");
            var padded = Canvas(0); Paint(padded, 112, 112, 32, 0xFF2080F0);
            Expect(IconService.SmallIconBounds(padded, 256, 256) is { Width: 32, X: 112 }, "Transparent padding was not cropped");
            var fullBleed = Canvas(0xFF0D0D0D); Paint(fullBleed, 40, 40, 176, 0xFFFFFFFF);
            Expect(IconService.SmallIconBounds(fullBleed, 256, 256) is null, "A large icon on a solid background was cropped");
            var smallLogo = Canvas(0xFF0D0D0D); Paint(smallLogo, 112, 112, 32, 0xFFFFFFFF);
            Expect(IconService.SmallIconBounds(smallLogo, 256, 256) is null, "A full-bleed icon with a small logo was cropped");
            var large = Canvas(0); Paint(large, 16, 16, 224, 0xFF10A040);
            Expect(IconService.SmallIconBounds(large, 256, 256) is null, "A normal large icon was cropped");
        });
        Check("A shortcut copy follows its original when the app rewrites it", () =>
        {
            var original = Path.Combine(root, "original.url"); var copy = Path.Combine(root, "copy.url");
            File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://old.example/\r\n"); File.Copy(original, copy);
            var item = ShelfItem.Application("Game", copy); item.Origin = original;
            Expect(!ShellIntegration.RefreshShortcutCopy(item), "An unchanged original was copied again");
            File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://new.example/\r\n");
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddMinutes(1));
            Expect(ShellIntegration.RefreshShortcutCopy(item) && File.ReadAllText(copy).Contains("new.example"), "The copy was not refreshed");
            File.Delete(original);
            Expect(!ShellIntegration.RefreshShortcutCopy(item) && File.Exists(copy), "A deleted original must keep the copy");
        });
        Check("A program that moved to a new version folder is found, another app's file is not", () =>
        {
            Expect(ShortcutRepair.AreVersions("version-e380c8edc8f6477c", "version-145f189a6a974303") && ShortcutRepair.AreVersions("app-1.0.9001", "app-1.0.9100") &&
                ShortcutRepair.AreVersions("IntelliJ IDEA 2023.1", "IntelliJ IDEA 2024.2") && ShortcutRepair.AreVersions("1.2.3", "1.3.0"), "Version folders not recognized");
            Expect(!ShortcutRepair.AreVersions("GameA", "GameB") && !ShortcutRepair.AreVersions("Squad", "Squad2") &&
                !ShortcutRepair.AreVersions("Deep Rock Galactic", "Dead Cells") && !ShortcutRepair.AreVersions("same-1", "same-1"), "Different apps taken for versions");
            var versions = Path.Combine(root, "Roblox", "Versions");
            foreach (var (folder, age) in new[] { ("version-1a2b3c", -2), ("version-9f8e7d", -1), ("Other", 0) })
            {
                Directory.CreateDirectory(Path.Combine(versions, folder));
                var exe = Path.Combine(versions, folder, "RobloxPlayerBeta.exe"); File.WriteAllText(exe, "");
                File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddDays(age));
            }
            var missing = Path.Combine(versions, "version-e380c8", "RobloxPlayerBeta.exe");
            Expect(ShortcutRepair.FindMoved(missing) == Path.Combine(versions, "version-9f8e7d", "RobloxPlayerBeta.exe"), "Newest version was not picked: " + ShortcutRepair.FindMoved(missing));
            Expect(ShortcutRepair.FindMoved(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NoSuchApp", "x.exe")) is null,
                "Search must not scan a broad root like Program Files");
        });
        Check("A press outside closes the menu on release, a drag into the menu keeps it open", () =>
        {
            var now = DateTime.UtcNow; var desktop = new IntPtr(42);
            var click = new DismissGate(); click.Arm(PopupDismissMonitor.Reason.OutsideClick, desktop);
            Expect(click.Release(false, out _, out var at) && at == desktop, "A plain click outside did not close the menu");
            var drag = new DismissGate(); drag.Arm(PopupDismissMonitor.Reason.OutsideClick, desktop);
            Expect(!drag.ForegroundLost(desktop, true, now, out _, out _), "Explorer taking focus during the press closed the menu");
            drag.Cancel(now);
            Expect(!drag.Release(true, out _, out _), "Dropping into the menu closed it");
            Expect(!drag.ForegroundLost(desktop, false, now.AddMilliseconds(100), out _, out _), "A late focus event right after the drop closed the menu");
            Expect(drag.ForegroundLost(desktop, false, now.AddSeconds(1), out _, out _), "Switching windows later must close the menu again");
            var held = new DismissGate();
            Expect(!held.ForegroundLost(desktop, true, now, out _, out _) && held.Release(false, out var why, out _) && why == PopupDismissMonitor.Reason.ForegroundChanged,
                "Focus lost with a button held must close on an outside release");
            Expect(!new DismissGate().Release(false, out _, out _), "A release without a press outside must not close");
        });
        Check("Dragging a folder icon on the desktop does not open it, a click in place does", () =>
        {
            Expect(DesktopSingleClick.IsClick(100, 100, 101, 99, 4, 4), "A click with a pixel of jitter must still open the folder");
            Expect(!DesktopSingleClick.IsClick(100, 100, 140, 100, 4, 4) && !DesktopSingleClick.IsClick(100, 100, 100, 104, 4, 4),
                "Releasing after dragging the icon opened the folder");
        });
        Check("The drop gap and the insertion bar follow the pointer", () =>
        {
            // 3 columns of 50×50 tiles, 7 apps on the page: rows of 3, 3, 1.
            int Gap(double x, double y) => AppShelf.App.FolderGrid.InsertionIndex(new Point(x, y), 50, 50, 3, 7);
            Expect(Gap(5, 10) == 0 && Gap(30, 10) == 1 && Gap(140, 10) == 3 && Gap(70, 60) == 4 && Gap(10, 120) == 6 && Gap(40, 120) == 7 && Gap(10, 400) == 7,
                "Wrong gaps: " + string.Join(",", Gap(5, 10), Gap(30, 10), Gap(140, 10), Gap(70, 60), Gap(10, 120), Gap(40, 120), Gap(10, 400)));
            Expect(AppShelf.App.FolderGrid.InsertionIndex(new Point(80, 80), 50, 50, 3, 0) == 0, "Empty folder must drop at 0");
            Expect(AppShelf.App.FolderGrid.TileAt(new Point(75, 60), 50, 50, 3, 7) == 4 && AppShelf.App.FolderGrid.TileAt(new Point(75, 120), 50, 50, 3, 7) == -1 &&
                AppShelf.App.FolderGrid.TileAt(new Point(160, 10), 50, 50, 3, 7) == -1, "Wrong tile under the pointer");
            Expect(AppShelf.App.FolderGrid.IsOverCenter(new Point(75, 75), 50, 50) && !AppShelf.App.FolderGrid.IsOverCenter(new Point(52, 75), 50, 50), "Folder centre is wrong");
            Expect(AppShelf.App.FolderGrid.GapPosition(4, 50, 50, 3, 7) == new Point(50, 50) && AppShelf.App.FolderGrid.GapPosition(7, 50, 50, 3, 7) == new Point(50, 100) &&
                AppShelf.App.FolderGrid.GapPosition(6, 50, 50, 3, 6) == new Point(150, 50), "Insertion bar is placed wrongly");
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
        Check("Folder icon colour, symbol and picture replace the app preview", () =>
        {
            static Color Pixel(BitmapSource image, int x, int y)
            {
                var pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
            }
            var red = new DrawingImage(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
            var green = Color.FromRgb(0x10, 0x89, 0x3E);
            var plated = FolderArtwork.Render([red], 64, look: new FolderLook(green, Opacity: 1));
            var edge = Pixel(plated, 32, 60);
            Expect(edge.A > 240 && Math.Abs(edge.G - green.G) < 30 && edge.R < 80, "Plate colour was not used");
            // A coloured plate is see-through at the chosen opacity, like the standard plate.
            var glass = Pixel(FolderArtwork.Render([red], 64, look: new FolderLook(green, Opacity: .3)), 32, 60);
            Expect(glass.A is > 60 and < 100, "Plate opacity was not applied: alpha " + glass.A);
            var standard = Pixel(FolderArtwork.Render([red], 64), 32, 60).A;
            Expect(Math.Abs(Pixel(FolderArtwork.Render([red], 64, look: new FolderLook(Opacity: FolderLook.DefaultOpacity)), 32, 60).A - standard) <= 2 &&
                Pixel(FolderArtwork.Render([red], 64, look: new FolderLook(Opacity: .1)), 32, 60).A < standard, "Opacity of the standard plate is wrong");
            Expect(Pixel(plated, 32, 32).R > 200, "Apps were not drawn on the coloured plate");
            var symbol = FolderArtwork.Render([red], 64, look: new FolderLook(null, FolderLook.Symbols[0].Glyph));
            var white = 0; var pixels = new byte[64 * 64 * 4]; symbol.CopyPixels(pixels, 64 * 4, 0);
            for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] > 220 && pixels[i + 1] > 220 && pixels[i + 2] > 220) white++;
            Expect(white > 40, "Symbol glyph is missing");
            // Default symbol plate: Windows blue at the default 40 % (pixels are premultiplied).
            Expect(Pixel(symbol, 32, 60) is { A: > 80 and < 120, B: > 60, R: < 20 }, "Symbol has no default see-through blue plate: " + Pixel(symbol, 32, 60));
            var picture = FolderArtwork.Render([], 64, look: new FolderLook(null, null, red));
            Expect(Pixel(picture, 32, 32) is { R: > 240, G: < 20 } && Pixel(picture, 6, 32).R > 240, "Picture does not fill the icon");
            Expect(Pixel(picture, 0, 0).A < 40, "Picture ignores the rounded corners");
        });
        Check("Folder look settings default for older files and reject invalid colours", () =>
        {
            var legacy = System.Text.Json.JsonSerializer.Deserialize<FolderSettings>("{\"Columns\":4}")!;
            Expect(legacy.Face == FolderFace.Preview && legacy.FaceColor == "" && legacy.FaceSymbol == "" && legacy.FaceImage == "" && legacy.Rows == 4 && legacy.FaceOpacity == 40);
            foreach (var opacity in new[] { 4, 101 }) { var bad = Example(); bad.Items[0].Settings.FaceOpacity = opacity; Reject(() => ShelfTree.Validate(bad)); }
            var config = Example();
            foreach (var rows in new[] { 0, 9 }) { config.Items[0].Settings.Rows = rows; Reject(() => ShelfTree.Validate(config)); }
            config.Items[0].Settings.Rows = 1; ShelfTree.Validate(config);
            config.Items[0].Settings.FaceColor = "#0067C0"; config.Items[0].Settings.Face = FolderFace.Symbol; config.Items[0].Settings.FaceSymbol = "";
            ShelfTree.Validate(config);
            foreach (var invalid in new[] { "red", "#12345G", "#12345", "#0067C0FF" })
            { config.Items[0].Settings.FaceColor = invalid; Reject(() => ShelfTree.Validate(config)); }
            config.Items[0].Settings.FaceColor = ""; config.Items[0].Settings.Face = (FolderFace)42; Reject(() => ShelfTree.Validate(config));
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
        Check("A copied profile points its shortcuts and pictures at the new folder and leaves the original intact", () =>
        {
            var from = Path.Combine(root, "profile-from"); var to = Path.Combine(root, "profile-to");
            Directory.CreateDirectory(Path.Combine(from, "shortcuts")); Directory.CreateDirectory(Path.Combine(from, "icons-v3"));
            File.WriteAllText(Path.Combine(from, "shortcuts", "Tor.lnk"), "copy");
            File.WriteAllText(Path.Combine(from, "icons-v3", "cache.png"), "cache");
            var config = new ShelfConfiguration();
            var folder = ShelfItem.Folder("Work"); folder.Settings.FaceImage = Path.Combine(from, "folder-art", "a.png");
            var app = ShelfItem.Application("Tor", Path.Combine(from, "shortcuts", "Tor.lnk")); app.Origin = @"C:\Users\Someone\Desktop\Tor.lnk";
            app.WorkingDirectory = from;
            var outside = ShelfItem.Application("Notepad", @"C:\Windows\notepad.exe");
            folder.Children.AddRange([app, outside]); config.Items.Add(folder);
            new ConfigurationStore(from).Save(config);
            var before = File.ReadAllText(Path.Combine(from, "shelf.json"));
            ProfileCopy.Copy(from, to);
            var copied = new ConfigurationStore(to).Load().Items[0];
            Expect(copied.Children[0].Target == Path.Combine(to, "shortcuts", "Tor.lnk") && File.ReadAllText(copied.Children[0].Target) == "copy", "Shortcut copy was not moved");
            Expect(copied.Children[0].Origin == app.Origin && copied.Children[1].Target == outside.Target, "Paths outside the profile changed");
            Expect(copied.Settings.FaceImage == Path.Combine(to, "folder-art", "a.png"), "Picture path was not moved");
            Expect(copied.Children[0].WorkingDirectory == to, "A working directory equal to the profile root was not moved");
            Expect(!Directory.Exists(Path.Combine(to, "icons-v3")), "Icon cache was copied");
            Expect(File.ReadAllText(Path.Combine(from, "shelf.json")) == before, "Original profile changed");
        });
        Check("Profile import rejects overlapping and occupied destinations without changing existing files", () =>
        {
            var from = Path.Combine(root, "protected-source");
            new ConfigurationStore(from).Save(Example());
            var before = File.ReadAllText(Path.Combine(from, "shelf.json"));
            foreach (var to in new[] { from, Path.Combine(from, "nested"), root })
            {
                var rejected = false;
                try { ProfileCopy.Copy(from, to); } catch (IOException) { rejected = true; }
                Expect(rejected, "Overlapping destination was accepted: " + to);
            }
            var occupied = Path.Combine(root, "occupied-destination"); Directory.CreateDirectory(occupied);
            File.WriteAllText(Path.Combine(occupied, "keep.txt"), "do not replace");
            var refused = false;
            try { ProfileCopy.Copy(from, occupied); } catch (IOException) { refused = true; }
            Expect(refused && File.ReadAllText(Path.Combine(occupied, "keep.txt")) == "do not replace" && Directory.GetFiles(occupied).Length == 1);
            Expect(File.ReadAllText(Path.Combine(from, "shelf.json")) == before && !Directory.Exists(Path.Combine(from, "nested")));
        });
        Check("A failed profile import leaves no partial destination or staging copy", () =>
        {
            var from = Path.Combine(root, "failed-import-source"); var to = Path.Combine(root, "failed-import-target");
            new ConfigurationStore(from).Save(Example());
            Directory.CreateDirectory(Path.Combine(from, "desktop-archive")); Directory.CreateDirectory(to);
            File.WriteAllText(Path.Combine(from, "asset.png"), "copied before the failure");
            foreach (var invalid in new[] { "{invalid json", "null", "{}", "[null]" })
            {
                File.WriteAllText(Path.Combine(from, "desktop-archive", "manifest.json"), invalid);
                var rejected = false;
                try { ProfileCopy.Copy(from, to); } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { rejected = true; }
                Expect(rejected && !Directory.EnumerateFileSystemEntries(to).Any(), "The failed copy left a partial profile");
            }
            Expect(!Directory.GetDirectories(root, ".failed-import-target.import-*").Any(), "An abandoned staging copy remained");
            Expect(File.ReadAllText(Path.Combine(from, "asset.png")) == "copied before the failure", "The source changed");
        });
        Check("Store links target one validated product and remain unavailable before setup", () =>
        {
            foreach (var invalid in new[] { "", "AppShelf", "https://example.com", "9AAAAAAAAAAA&other=1" })
                Expect(AppLinks.StorePageFor(invalid) is null, "An invalid Store target was accepted");
            Expect(AppLinks.StorePageFor(null) is null);
            Expect(AppLinks.StorePageFor("9aaaaaaaaaaa")!.AbsoluteUri == "ms-windows-store://pdp/?ProductId=9AAAAAAAAAAA");
            Expect(AppLinks.StorePageFor("9AAAAAAAAAAA", web: true)!.AbsoluteUri == "https://apps.microsoft.com/detail/9AAAAAAAAAAA");
            Expect(Uri.TryCreate(AppLinks.GitHub, UriKind.Absolute, out var gitHub) && gitHub.Scheme == "https" && gitHub.Host == "github.com", "Repository link is missing from build metadata");
        });
        Check("Feedback links: Store rating needs a valid product ID and bug reports open the issue template picker", () =>
        {
            foreach (var invalid in new[] { null, "", "AppShelf", "https://example.com", "9AAAAAAAAAAA&other=1", "8AAAAAAAAAAA", "9AAAAAAAAAA" })
                Expect(AppLinks.StoreReviewFor(invalid) is null, "An invalid Store rating target was accepted");
            Expect(AppLinks.StoreReviewFor(" 9aaaaaaaaaaa ")!.AbsoluteUri == "ms-windows-store://review/?ProductId=9AAAAAAAAAAA");
            Expect(AppLinks.MicrosoftStoreReview?.AbsoluteUri == AppLinks.StoreReviewFor(AppLinks.StoreProductId)?.AbsoluteUri, "The rating link ignores the configured product");
            foreach (var invalid in new[] { null, "", "   ", "github.com/bertelfeed/AppShelf", "http://github.com/bertelfeed/AppShelf" })
                Expect(AppLinks.NewIssueFor(invalid) is null, "A bug report link was built without an https repository");
            foreach (var repository in new[] { "https://github.com/bertelfeed/AppShelf", "https://github.com/bertelfeed/AppShelf/" })
                Expect(AppLinks.NewIssueFor(repository)!.AbsoluteUri == "https://github.com/bertelfeed/AppShelf/issues/new/choose", "Wrong bug report link for " + repository);
            Expect(AppLinks.NewIssue!.AbsoluteUri == AppLinks.GitHub.TrimEnd('/') + "/issues/new/choose", "The bug report link ignores the repository metadata");
        });
        Check("Update notice: only a newer published GitHub release counts", () =>
        {
            Expect(ReleaseFeed.ApiFor("https://github.com/bertelfeed/AppShelf/")!.AbsoluteUri == "https://api.github.com/repos/bertelfeed/AppShelf/releases/latest");
            Expect(ReleaseFeed.PageFor("https://github.com/bertelfeed/AppShelf")!.AbsoluteUri == "https://github.com/bertelfeed/AppShelf/releases/latest");
            foreach (var invalid in new[] { "", "github.com/bertelfeed/AppShelf", "http://github.com/bertelfeed/AppShelf", "https://example.com/a/b", "https://github.com/bertelfeed" })
                Expect(ReleaseFeed.ApiFor(invalid) is null, "An update source was built from " + invalid);
            Expect(ReleaseFeed.VersionIn("{\"tag_name\":\"v1.2.3\",\"draft\":false,\"prerelease\":false}") == new Version(1, 2, 3));
            Expect(ReleaseFeed.VersionIn("{\"tag_name\":\"1.0.0\"}") == new Version(1, 0, 0));
            foreach (var json in new[] { "{\"tag_name\":\"v1.2.3\",\"prerelease\":true}", "{\"tag_name\":\"v1.2.3\",\"draft\":true}",
                "{\"tag_name\":\"latest\"}", "{\"tag_name\":\"v2\"}", "{}", "[]", "not json", "{\"message\":\"Not Found\"}" })
                Expect(ReleaseFeed.VersionIn(json) is null, "Not a release version: " + json);
            Expect(ReleaseFeed.IsNewer(new Version(1, 0, 0), new Version(0, 6, 0, 0)));
            Expect(!ReleaseFeed.IsNewer(new Version(0, 6, 0), new Version(0, 6, 0, 0)), "The assembly's fourth version part made the same release look newer");
            Expect(!ReleaseFeed.IsNewer(new Version(0, 5, 9), new Version(0, 6, 0, 0)));
            Expect(new ShelfConfiguration().CheckForUpdates && System.Text.Json.JsonSerializer.Deserialize<ShelfConfiguration>("{\"Items\":[]}")!.CheckForUpdates,
                "Update checks must be on for new and older profiles");
        });
        Check("The Store update query answers without crashing outside the Store package", () =>
        {
            // Only the Store package gets updates; here the call must go through the ABI and simply find none or fail cleanly.
            try { Expect(!StoreUpdates.Available(), "An unpackaged process reported a Store update"); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or TimeoutException) { }
        });
        Check("A pinned folder's relaunch command without --data-dir belongs only to the default profile", () =>
        {
            var id = Guid.NewGuid();
            var pinned = new ShellIntegration.ShortcutDetails(Path.Combine(root, "pinned.lnk"), Environment.ProcessPath!, $"--folder {id:D}", "", ShellIntegration.FolderAppId(id));
            Expect(ShellIntegration.IsFolderShortcut(pinned, id, AppLocations.DefaultDataPath()), "The default profile did not recognize its pinned folder");
            Expect(!ShellIntegration.IsFolderShortcut(pinned, id, Path.Combine(root, "other-profile")), "Another profile claimed a pinned folder of the default profile");
        });
        Check("An unpackaged process keeps its own executable for shortcuts and its classic profile", () =>
        {
            Expect(!AppLocations.IsPackaged && ShellIntegration.Launcher == Environment.ProcessPath, "Launcher changed outside the package");
            Expect(PackageStartup.LaunchedAtSignIn() == false, "Startup task activation reported outside the package");
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
        Check("English and Russian resources have matching keys and valid format placeholders", () =>
        {
            var english = Localization.Strings("en-US"); var russian = Localization.Strings("ru-RU");
            Expect(english.Count >= 200 && english.Keys.Order().SequenceEqual(russian.Keys.Order()));
            foreach (var key in english.Keys)
            {
                Expect(!string.IsNullOrWhiteSpace(english[key]) && !string.IsNullOrWhiteSpace(russian[key]), "Empty message: " + key);
                var enFormat = System.Text.CompositeFormat.Parse(english[key]);
                var ruFormat = System.Text.CompositeFormat.Parse(russian[key]);
                Expect(enFormat.MinimumArgumentCount == ruFormat.MinimumArgumentCount, "Different placeholders: " + key);
                Expect(key == "RussianLanguageName" || !System.Text.RegularExpressions.Regex.IsMatch(english[key], "[А-Яа-яЁё]"), "Untranslated English resource: " + key);
            }
        });
        Check("Switching languages changes messages and plural forms without renaming user content", () =>
        {
            var config = Example(); var names = ShelfTree.Walk(config.Items).Select(item => item.Name).ToArray();
            try
            {
                Localization.SetLanguage("en-US");
                Expect(Localization.Get("MyFolders") == "My folders" && Localization.Elements(1) == "1 item" && Localization.Elements(21) == "21 items");
                Expect(Localization.CatalogLabel("Игры") == "Games");
                Localization.SetLanguage("ru-RU");
                Expect(Localization.Get("MyFolders") == "Мои папки" && Localization.Elements(1) == "1 элемент" &&
                    Localization.Elements(2) == "2 элемента" && Localization.Elements(11) == "11 элементов" && Localization.Elements(21) == "21 элемент");
                Expect(ShelfTree.Walk(config.Items).Select(item => item.Name).SequenceEqual(names));
            }
            finally { Localization.SetLanguage("ru-RU"); }
        });
        Check("Language selection persists with folder identities and legacy Russian preferences", () =>
        {
            var config = Example(); var id = config.Items[0].Id;
            var store = new ConfigurationStore(Path.Combine(root, "language"));
            config.Language = "en-US"; store.Save(config);
            Expect(store.Load().Language == "en-US" && store.Load().Items[0].Id == id);
            config.Language = "ru-RU"; store.Save(config);
            Expect(store.Load().Language == "ru-RU" && store.Load().Items[0].Name == "Работа");
        });
        RunWindowChecks(root, Check, Expect);
        Console.WriteLine($"RESULT {passed} passed, {failed} failed, {skipped} skipped");
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
        check("Taskbar hover preview shows the folder's apps and offers launch buttons", () =>
        {
            var window = application.Windows.OfType<AppShelf.App.FolderTaskbarWindow>().Single();
            PumpUntil(() => window.TaskbarItemInfo?.ThumbButtonInfos.Count == 1);
            expect(window.TaskbarItemInfo!.ThumbButtonInfos[0].Description == "Notepad" && window.TaskbarItemInfo.ThumbButtonInfos[0].ImageSource is not null,
                "Launch button lacks the app name or icon");
            var thumbnail = window.RenderThumbnail(200, 120);
            var pixels = new byte[200 * 120 * 4]; thumbnail.CopyPixels(pixels, 200 * 4, 0);
            var drawn = 0; for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] > 0) drawn++;
            expect(thumbnail.PixelWidth == 200 && thumbnail.PixelHeight == 120 && drawn > 400, "Thumbnail is empty or exceeds the requested size");
            expect(pixels[3] == 0, "Thumbnail background should stay transparent");
        });
        check("A chosen folder picture is copied into the profile and used for the icon", () =>
        {
            var source = Path.Combine(root, "face-source.png");
            var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 16).SelectMany(x => x).ToArray(), 16);
            using (var stream = File.Create(source)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
            var stored = AppShelf.App.FolderIconWriter.ImportPicture(folder.Id, source);
            File.Delete(source);
            expect(!Path.IsPathRooted(stored) && File.Exists(Path.Combine(session.Store.DirectoryPath, stored)), "Picture was not copied into the profile");
            var settings = new FolderSettings { Face = FolderFace.Image, FaceImage = stored };
            var look = AppShelf.App.FolderIconWriter.LookAsync(settings);
            PumpUntil(() => look.IsCompleted);
            expect(look.Result?.Image is not null, "Copied picture was not loaded");
            var rejected = false;
            try { AppShelf.App.FolderIconWriter.ImportPicture(folder.Id, Path.Combine(Environment.SystemDirectory, "notepad.exe")); }
            catch (InvalidOperationException) { rejected = true; }
            expect(rejected, "A non-image file was accepted as a folder picture");
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
            expect(Descendants(popup).OfType<System.Windows.Controls.TextBlock>().Any(x => x.Text == "") &&
                System.Windows.Automation.AutomationProperties.GetName(Descendants(popup).OfType<System.Windows.Controls.Button>().First()).StartsWith("Назад"),
                "A nested folder shows no back arrow");
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
        check("More apps than columns × rows are paged with the wheel, keys and dots", () =>
        {
            var paged = ShelfItem.Folder("Paging check"); paged.Settings.Columns = 3; paged.Settings.Rows = 2;
            for (var i = 0; i < 7; i++) paged.Children.Add(ShelfItem.Application("App " + i, Path.Combine(Environment.SystemDirectory, "notepad.exe")));
            session.Change(config => config.Items.Add(paged));
            var popup = application.Windows.OfType<AppShelf.App.FolderPopup>().Single();
            try
            {
                popup.Open(paged.Id); popup.UpdateLayout();
                int Tiles() { popup.UpdateLayout(); return Descendants(popup).OfType<System.Windows.Controls.Image>().Count(); }
                expect(popup.PageCount == 2 && popup.Page == 0 && Tiles() == 6, $"First page is wrong: {popup.PageCount} pages, {Tiles()} tiles");
                expect(Descendants(popup).OfType<System.Windows.Shapes.Ellipse>().Count() == 2, "Page dots are missing");
                var fullHeight = popup.Height;
                popup.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                expect(popup.Page == 1 && Tiles() == 1 && popup.Height == fullHeight, "Wheel did not turn the page or the window jumped");
                popup.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(popup),
                    Environment.TickCount, System.Windows.Input.Key.PageUp) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                expect(popup.Page == 0 && Tiles() == 6, "PageUp did not go back");
                var dot = Descendants(popup).OfType<System.Windows.Controls.Border>()
                    .Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Страница 2 из 2");
                dot.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                expect(popup.Page == 1, "Clicking a page dot did not turn the page");
                popup.Hide(); popup.Open(paged.Id);
                expect(popup.Page == 0, "Reopening must start on the first page");
            }
            finally { popup.Hide(); session.Change(config => config.Items.RemoveAll(x => x.Id == paged.Id)); }
        });
        check("Shortcuts dropped into the menu land at the chosen place; tiles can be reordered and moved into folders", () =>
        {
            var target = ShelfItem.Folder("Drop check");
            var a = ShelfItem.Application("A", Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            var b = ShelfItem.Application("B", Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            var inner = ShelfItem.Folder("Inner");
            target.Children.AddRange([a, b, inner]);
            session.Change(config => config.Items.Add(target));
            var dropped = Path.Combine(root, "drop"); Directory.CreateDirectory(dropped);
            var site = Path.Combine(dropped, "Site.url"); File.WriteAllText(site, "[InternetShortcut]\r\nURL=https://example.com/\r\n");
            var popup = application.Windows.OfType<AppShelf.App.FolderPopup>().Single();
            try
            {
                popup.Open(target.Id);
                string Names() => string.Join(",", ShelfTree.Find(session.Configuration, target.Id)!.Children.Select(x => x.Name));
                expect(popup.DropPaths([site], 1) == 1 && Names() == "A,Site,B,Inner", "Dropped shortcut is not at the gap: " + Names());
                var added = ShelfTree.Find(session.Configuration, target.Id)!.Children[1];
                expect(added.Origin == site && added.Target.StartsWith(session.Store.DirectoryPath) && File.Exists(site), "The desktop shortcut was not copied or was removed");
                popup.DropPaths([site], 4);
                expect(Names() == "A,B,Inner,Site", "Dropping the same shortcut again added a twin: " + Names());
                popup.DropItem(a.Id, 3);
                expect(Names() == "B,Inner,A,Site", "Reordering by drag is wrong: " + Names());
                popup.DropItem(b.Id, 0, inner.Id);
                expect(Names() == "Inner,A,Site" && ShelfTree.Find(session.Configuration, inner.Id)!.Children.Single().Name == "B", "Drop onto a folder did not move into it");
                popup.DropItem(inner.Id, 0, inner.Id);
                expect(Names() == "Inner,A,Site", "A folder was moved into itself");
            }
            finally { popup.Hide(); session.Change(config => config.Items.RemoveAll(x => x.Id == target.Id)); }
        });
        check("Title click closes the root folder without adding menu controls", () =>
        {
            var popup = application.Windows.OfType<AppShelf.App.FolderPopup>().Single();
            popup.Open(folder.Id);
            Descendants(popup).OfType<System.Windows.Controls.Button>().First().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            expect(!popup.IsVisible, "Title click did not close root folder");
        });
        check("A click on the folder's own button closes the menu instead of reopening it", () =>
        {
            application.ShowFolder(folder.Id);
            var popup = application.Windows.OfType<AppShelf.App.FolderPopup>().Single();
            PumpUntil(() => popup.IsVisible);
            if (Environment.OSVersion.Version.Build >= 22621 && !SystemParameters.HighContrast)
                expect(popup.Background is SolidColorBrush { Color.A: 0 }, "Menu paints over its acrylic backdrop");
            var other = new Window { Title = "Toggle check", Width = 160, Height = 100, ShowInTaskbar = false };
            try
            {
                // The press on the taskbar button dismisses the menu; its release then asks to open it again.
                other.Show(); other.Activate();
                PumpUntil(() => !popup.IsVisible);
                application.ShowFolder(folder.Id);
                expect(!popup.IsVisible, "The same click reopened the menu");
                System.Threading.Thread.Sleep(700);
                application.ShowFolder(folder.Id);
                expect(popup.IsVisible, "A later click did not open the menu");
            }
            finally { other.Close(); popup.Hide(); }
        });
        check("Folder settings preview follows the slider of each surface without saving", () =>
        {
            var before = ShelfTree.Find(session.Configuration, folder.Id)!.Settings;
            var dialog = new AppShelf.App.FolderSettingsDialog(folder.Id);
            try
            {
                dialog.Show();
                PumpUntil(() => dialog.IsLoaded);
                var all = Descendants(dialog).OfType<System.Windows.Controls.Slider>().ToList();
                System.Windows.Controls.Slider Named(string name) => all.Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == name);
                var sliders = new[] { Named("Масштаб иконок · рабочий стол"), Named("Масштаб иконок · панель задач") };
                expect(Named("Непрозрачность подложки").Value == before.FaceOpacity, "Plate opacity slider is missing or wrong");
                System.Windows.Controls.Image Preview() { dialog.UpdateLayout(); return Descendants(dialog).OfType<System.Windows.Controls.Image>().First(); }
                expect(Preview().Width == before.IconSizeFor(true), "Preview does not start on the desktop surface");
                sliders[1].Value = 200;
                expect(Preview().Width == before.IconSize * 2, "Preview ignored the taskbar slider");
                sliders[0].Value = 50;
                expect(Preview().Width == before.IconSize / 2, "Preview ignored the desktop slider");
            }
            finally { dialog.Close(); }
            var after = ShelfTree.Find(session.Configuration, folder.Id)!.Settings;
            expect(after.DesktopIconScale == before.DesktopIconScale && after.TaskbarIconScale == before.TaskbarIconScale, "Preview saved settings");
        });
        check("Desktop organizing tools group, tidy, restore and undo without touching real files", () =>
        {
            var desktop = Path.Combine(root, "api-desktop");
            Directory.CreateDirectory(Path.Combine(desktop, "Проекты"));
            File.WriteAllText(Path.Combine(desktop, "Сайт.url"), "[InternetShortcut]\r\nURL=https://example.com/\r\n");
            File.WriteAllText(Path.Combine(desktop, "notes.txt"), "keep me");
            var api = new AppShelf.App.ShelfApi(session, null);
            api.DesktopDirectories.Clear(); api.DesktopDirectories.Add(desktop);
            AppShelfApiReply Call(string tool, string json = "{}")
            {
                var task = api.HandleAsync(tool, System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject());
                PumpUntil(() => task.IsCompleted);
                return new(task.Result.Ok, task.Result.Text);
            }
            var listing = Call("get_desktop");
            expect(listing.Ok && listing.Text.Contains("\"Сайт\"") && listing.Text.Contains("web_link") && listing.Text.Contains("\"Проекты\""), "Desktop listing is incomplete: " + listing.Text);
            var before = ShelfTree.Walk(session.Configuration.Items).Count();
            var missing = Call("organize", "{\"groups\":[{\"folder\":\"Сайты\",\"items\":[\"Сайт\",\"Несуществующее приложение 7f3\"]}]}");
            expect(!missing.Ok && missing.Text.Contains("Несуществующее приложение 7f3") && ShelfTree.Walk(session.Configuration.Items).Count() == before,
                "A failed organize changed folders: " + missing.Text);
            var organized = Call("organize", "{\"groups\":[{\"folder\":\"Сайты\",\"items\":[\"Сайт\",\"notes.txt\"]}],\"tidy_desktop\":true}");
            expect(organized.Ok, organized.Text);
            var sites = ShelfTree.Walk(session.Configuration.Items).Single(x => x.IsFolder && x.Name == "Сайты");
            var link = sites.Children.Single(x => x.Name == "Сайт");
            expect(sites.Children.Count == 2 && File.Exists(link.Target) && link.Target.StartsWith(session.Store.DirectoryPath, StringComparison.OrdinalIgnoreCase),
                "Shortcut was not copied into the profile");
            expect(!File.Exists(Path.Combine(desktop, "Сайт.url")) && File.Exists(Path.Combine(desktop, "notes.txt")), "Tidying moved the wrong items");
            var moved = Call("organize", "{\"groups\":[{\"folder\":\"Документы/Текст\",\"items\":[\"notes.txt\"]}]}");
            var text = ShelfTree.Walk(session.Configuration.Items).Single(x => x.IsFolder && x.Name == "Текст");
            expect(moved.Ok && text.Children.Single().Name == "notes.txt" && sites.Children.Count == 2 &&
                ShelfTree.Walk(session.Configuration.Items).Single(x => x.IsFolder && x.Name == "Сайты").Children.Count == 1, "Organize duplicated instead of moving: " + moved.Text);
            var look = Call("set_folder_look", "{\"folder\":\"Сайты\",\"look\":\"symbol\",\"symbol\":\"Интернет\",\"color\":\"Зелёный\",\"opacity\":25}");
            var settings = ShelfTree.Walk(session.Configuration.Items).Single(x => x.IsFolder && x.Name == "Сайты").Settings;
            expect(look.Ok && settings.Face == FolderFace.Symbol && settings.FaceColor == "#10893E" && settings.FaceSymbol == "" && settings.FaceOpacity == 25,
                "Folder look was not applied: " + look.Text);
            expect(!Call("set_folder_look", "{\"folder\":\"Сайты\",\"opacity\":1}").Ok, "Opacity outside 5–100 was accepted");
            var restored = Call("restore_desktop");
            expect(restored.Ok && File.Exists(Path.Combine(desktop, "Сайт.url")), "Shortcut was not restored: " + restored.Text);
            expect(Call("rename_item", "{\"item\":\"Сайты/Сайт\",\"new_name\":\"Пример\"}").Ok &&
                ShelfTree.Walk(session.Configuration.Items).Any(x => x.Name == "Пример"), "Rename failed");
            expect(Call("undo").Ok && ShelfTree.Walk(session.Configuration.Items).Any(x => x.Name == "Сайт"), "Undo did not revert the rename");
            expect(Call("remove_item", "{\"item\":\"Документы\"}").Ok && !ShelfTree.Walk(session.Configuration.Items).Any(x => x.Name == "Текст"), "Remove failed");
            expect(File.Exists(Path.Combine(desktop, "notes.txt")), "Removing from AppShelf deleted a real file");
            expect(!Call("no_such_tool").Ok, "Unknown tool was accepted");
            // Dictation: «Дискорд» is Discord; a near miss is not applied but suggested.
            expect(AppShelf.App.ShelfApi.Skeleton("Дискорд") == AppShelf.App.ShelfApi.Skeleton("Discord") &&
                AppShelf.App.ShelfApi.Skeleton("Обсидиан") == AppShelf.App.ShelfApi.Skeleton("Obsidian") &&
                AppShelf.App.ShelfApi.Skeleton("Телеграм") == AppShelf.App.ShelfApi.Skeleton("Telegram"), "Sound-alike names do not match");
            File.WriteAllText(Path.Combine(desktop, "Discord.url"), "[InternetShortcut]\r\nURL=https://discord.com/\r\n");
            var nearMiss = Call("organize", "{\"groups\":[{\"folder\":\"Общение\",\"items\":[\"Дискорт\"]}],\"place_on_desktop\":false}");
            expect(!nearMiss.Ok && nearMiss.Text.Contains("Discord") && !ShelfTree.Walk(session.Configuration.Items).Any(x => x.Name == "Общение"),
                "A near miss was applied or not suggested: " + nearMiss.Text);
            var dictated = Call("organize", "{\"groups\":[{\"folder\":\"Общение\",\"items\":[\"Дискорд\"]}],\"place_on_desktop\":false}");
            expect(dictated.Ok && dictated.Text.Contains("Дискорд → Discord") &&
                ShelfTree.Walk(session.Configuration.Items).Single(x => x.IsFolder && x.Name == "Общение").Children.Single().Name == "Discord", "Dictated name was not recognized: " + dictated.Text);
            // The same app from the Start menu and as a desktop shortcut has different paths but one name.
            session.Change(config => ShelfTree.Walk(config.Items).Single(x => x.IsFolder && x.Name == "Сайты").Children.Add(ShelfItem.Application("Близнец", @"shell:AppsFolder\Twin.App")));
            File.WriteAllText(Path.Combine(desktop, "Близнец.url"), "[InternetShortcut]\r\nURL=https://example.org/\r\n");
            var twin = Call("organize", "{\"groups\":[{\"folder\":\"Другое\",\"items\":[\"Близнец\"]}],\"place_on_desktop\":false}");
            expect(twin.Ok && ShelfTree.Walk(session.Configuration.Items).Count(x => x.Name == "Близнец") == 1 &&
                ShelfTree.Walk(session.Configuration.Items).Single(x => x.IsFolder && x.Name == "Другое").Children.Single().Name == "Близнец", "A same-named app was duplicated: " + twin.Text);
            // A shortcut in a desktop subfolder is taken by its full path and stays where it is even with tidy_desktop.
            var nested = Path.Combine(desktop, "Проекты", "Игра.url");
            File.WriteAllText(nested, "[InternetShortcut]\r\nURL=steam://rungameid/1/\r\n");
            var byPath = Call("organize", System.Text.Json.JsonSerializer.Serialize(new { groups = new[] { new { folder = "Game", items = new[] { nested } } }, tidy_desktop = true, place_on_desktop = false }));
            expect(byPath.Ok && File.Exists(nested) &&
                ShelfTree.Walk(session.Configuration.Items).Single(x => x.IsFolder && x.Name == "Game").Children.Single().Name == "Игра", "A full path outside the desktop was not taken: " + byPath.Text);
            var folders = Call("get_folders");
            expect(folders.Ok && folders.Text.Contains("\"Сайты\"") && folders.Text.Contains("\"symbol\""), "Folder tree is incomplete: " + folders.Text);
        });
        check("MCP server speaks JSON-RPC over stdio and forwards tool calls to the running app", () =>
        {
            var api = new AppShelf.App.ShelfApi(session, null);
            using var stop = new System.Threading.CancellationTokenSource();
            _ = ShelfApiChannel.ListenAsync(session.Store.DirectoryPath, (tool, args) =>
                application.Dispatcher.InvokeAsync(() => api.HandleAsync(tool, args)).Task.Unwrap(), stop.Token);
            var server = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\AppShelf.Mcp\bin\Release\net10.0\AppShelf.Mcp.exe"));
            expect(File.Exists(server), "MCP server was not built: " + server);
            var start = new ProcessStartInfo(server) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardInputEncoding = new System.Text.UTF8Encoding(false) };
            start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(session.Store.DirectoryPath);
            using var process = Process.Start(start)!;
            try
            {
                System.Text.Json.Nodes.JsonObject Ask(string json)
                {
                    process.StandardInput.WriteLine(json); process.StandardInput.Flush();
                    var line = process.StandardOutput.ReadLineAsync();
                    PumpUntil(() => line.IsCompleted);
                    return System.Text.Json.Nodes.JsonNode.Parse(line.Result!)!.AsObject();
                }
                var init = Ask("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"checks\",\"version\":\"1\"}}}");
                expect(init["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "appshelf" && init["result"]?["capabilities"]?["tools"] is not null, "Bad initialize result");
                process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                var tools = Ask("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}")["result"]!["tools"]!.AsArray();
                expect(tools.Any(x => x!["name"]!.GetValue<string>() == "organize") && tools.All(x => x!["inputSchema"]?["type"]?.GetValue<string>() == "object"), "Tool list is wrong");
                var call = Ask("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_folders\",\"arguments\":{}}}")["result"]!;
                expect(call["isError"]?.GetValue<bool>() == false && call["content"]![0]!["text"]!.GetValue<string>().Contains("Сайты"), "Tool call was not forwarded");
                var unknown = Ask("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"resources/list\"}");
                expect(unknown["error"]?["code"]?.GetValue<int>() == -32601, "Unknown method must be a JSON-RPC error");
            }
            finally { process.StandardInput.Close(); if (!process.WaitForExit(3000)) process.Kill(); stop.Cancel(); }
        });
        check("Broken shortcuts repair themselves; what cannot be found is faded and explained", () =>
        {
            var versions = Path.Combine(root, "repair", "Launcher", "Versions");
            Directory.CreateDirectory(Path.Combine(versions, "version-77aa11"));
            var current = Path.Combine(versions, "version-77aa11", "Player.exe"); File.WriteAllText(current, "");
            var launchers = Path.Combine(session.Store.DirectoryPath, "launchers"); Directory.CreateDirectory(launchers);
            var copy = Path.Combine(launchers, "Player-test.lnk");
            ShellIntegration.CreateShortcut(copy, Path.Combine(versions, "version-0b0b0b", "Player.exe"), Path.Combine(versions, "version-0b0b0b"));
            var userLink = Path.Combine(root, "repair", "User Player.lnk");
            ShellIntegration.CreateShortcut(userLink, Path.Combine(versions, "version-0c0c0c", "Player.exe"));
            var player = ShelfItem.Application("Player", copy);
            var foreign = ShelfItem.Application("Player (user)", userLink);
            var store = ShelfItem.Application("Близнец Апп", Path.Combine(root, "repair", "Gone", "twin.exe"));
            var lost = ShelfItem.Application("Пропавшее", Path.Combine(root, "repair", "Gone", "lost.exe"));
            var holder = ShelfItem.Folder("Ремонт"); holder.Children.AddRange([player, foreign, store, lost]);
            session.Change(config => config.Items.Add(holder));
            var repair = new AppShelf.App.ShelfRepair(session, () => Task.FromResult(new List<ShelfItem> { ShelfItem.Application("Близнец Апп", @"shell:AppsFolder\Twin.App") }), schedule: false);
            typeof(AppShelf.App.App).GetProperty("Repair")!.SetValue(application, repair);
            var staleIcon = session.Icons.GetAsync(copy);
            var run = repair.CheckAllAsync(); PumpUntil(() => run.IsCompleted);
            expect(run.Result == 3, "Expected three repairs, got " + run.Result);
            expect(!ReferenceEquals(session.Icons.GetAsync(copy), staleIcon), "The icon of the broken shortcut stayed in memory");
            expect(ShellIntegration.ReadShortcut(copy)!.Target == current && ShellIntegration.ReadShortcut(copy)!.WorkingDirectory == Path.GetDirectoryName(current),
                "AppShelf's copy does not point at the new version");
            var fixedForeign = ShelfTree.Find(session.Configuration, foreign.Id)!;
            expect(ShellIntegration.ReadShortcut(userLink)!.Target.Contains("version-0c0c0c") && fixedForeign.Target.StartsWith(launchers) &&
                ShellIntegration.ReadShortcut(fixedForeign.Target)!.Target == current && fixedForeign.Origin == userLink, "The user's own shortcut was changed or not copied");
            expect(ShelfTree.Find(session.Configuration, store.Id)!.Target == @"shell:AppsFolder\Twin.App", "Start menu app was not used");
            expect(repair.IsMissing(lost.Id) && !repair.IsMissing(player.Id), "Missing marks are wrong");
            var tile = new AppShelf.App.TileModel(ShelfTree.Find(session.Configuration, lost.Id)!);
            expect(tile.Missing && tile.IconOpacity < 1 && tile.Description.Contains("Не найдено"), "Missing app is not shown as missing");
            expect(File.ReadAllText(Path.Combine(session.Store.DirectoryPath, "repairs.log")).Contains("Player:"), "Repair was not logged");
            var launch = repair.PrepareAsync(ShelfTree.Find(session.Configuration, lost.Id)!); PumpUntil(() => launch.IsCompleted);
            expect(launch.IsFaulted && launch.Exception!.InnerException!.Message.Contains("Пропавшее"), "Launching a missing app gives no clear message");
            var again = repair.CheckAllAsync(); PumpUntil(() => again.IsCompleted);
            expect(again.Result == 0, "Repaired apps were repaired again");
            typeof(AppShelf.App.App).GetProperty("Repair")!.SetValue(application, null);
            session.Change(config => config.Items.RemoveAll(x => x.Id == holder.Id));
        });
        check("A folder shortcut zeroed by a sync conflict is replaced by a working one", () =>
        {
            var desktop = Path.Combine(root, "zeroed-desktop"); Directory.CreateDirectory(desktop);
            var damaged = ShelfItem.Folder("Zeroed check");
            damaged.Children.Add(ShelfItem.Application("Notepad", Path.Combine(Environment.SystemDirectory, "notepad.exe")));
            session.Change(config => config.Items.Add(damaged));
            var publisher = new AppShelf.App.FolderPublisher(session, false) { Desktop = desktop };
            try
            {
                var created = publisher.AddDesktopAsync(damaged.Id); PumpUntil(() => created.IsCompleted);
                var path = created.Result;
                // Ownership must survive a process restart before synchronization damages the file.
                publisher.Dispose(); publisher = new AppShelf.App.FolderPublisher(session, false) { Desktop = desktop };
                File.WriteAllBytes(path, new byte[2847]);
                expect(ShellIntegration.IsDamagedShortcut(path) && !ShellIntegration.IsDamagedShortcut(Path.Combine(desktop, "missing.lnk")), "Damage detection is wrong");
                var run = publisher.RepairDamagedAsync(); PumpUntil(() => run.IsCompleted);
                expect(run.Result.SequenceEqual(["Zeroed check"]), "The damaged shortcut was not repaired");
                expect(!ShellIntegration.IsDamagedShortcut(path) && ShellIntegration.IsFolderShortcut(ShellIntegration.ReadShortcut(path), damaged.Id, session.Store.DirectoryPath),
                    "The new shortcut does not open the folder");
                expect(Directory.GetFiles(Path.Combine(session.Store.DirectoryPath, "damaged-shortcuts"), "*Zeroed check.lnk").Length == 1, "The damaged file was not kept aside");
                var again = publisher.RepairDamagedAsync(); PumpUntil(() => again.IsCompleted);
                expect(again.Result.Count == 0, "A healthy shortcut was replaced");
            }
            finally { publisher.Dispose(); session.Change(config => config.Items.RemoveAll(x => x.Id == damaged.Id)); }
        });
        check("Damage repair leaves an unregistered same-name desktop file untouched", () =>
        {
            var desktop = Path.Combine(root, "foreign-zeroed-desktop"); Directory.CreateDirectory(desktop);
            var group = ShelfItem.Folder("Someone else's shortcut"); session.Change(config => config.Items.Add(group));
            var path = Path.Combine(desktop, ShellIntegration.FolderShortcutName(group) + ".lnk");
            var bytes = new byte[123]; File.WriteAllBytes(path, bytes);
            using var publisher = new AppShelf.App.FolderPublisher(session, false) { Desktop = desktop };
            try
            {
                var task = publisher.RepairDamagedAsync(); PumpUntil(() => task.IsCompleted);
                expect(task.Result.Count == 0 && File.ReadAllBytes(path).SequenceEqual(bytes), "A foreign file was adopted by name");
            }
            finally { session.Change(config => config.Items.RemoveAll(x => x.Id == group.Id)); }
        });
        check("Removing desktop placement clears duplicate owned shortcuts and cannot resurrect a damaged icon", () =>
        {
            var desktop = Path.Combine(root, "remove-owned-desktop"); Directory.CreateDirectory(desktop);
            var group = ShelfItem.Folder("Remove check"); session.Change(config => config.Items.Add(group));
            using var publisher = new AppShelf.App.FolderPublisher(session, false) { Desktop = desktop };
            try
            {
                var add = publisher.AddDesktopAsync(group.Id); PumpUntil(() => add.IsCompleted);
                var path = add.Result;
                ShellIntegration.CreateFolderShortcut(group, desktop, session.Store.DirectoryPath, existingPath: Path.Combine(desktop, "Duplicate.lnk"));
                var remove = publisher.RemoveDesktopAsync(group.Id); PumpUntil(() => remove.IsCompleted);
                expect(!Directory.GetFiles(desktop, "*.lnk").Any(), "Duplicate folder placement was left behind");
                File.WriteAllBytes(path, new byte[91]);
                var repair = publisher.RepairDamagedAsync(); PumpUntil(() => repair.IsCompleted);
                expect(repair.Result.Count == 0 && new FileInfo(path).Length == 91, "Removed placement was resurrected");
                expect(!Directory.GetFiles(desktop, ".AppShelf-*.tmp").Any(), "Shortcut drafts were left behind");
            }
            finally { session.Change(config => config.Items.RemoveAll(x => x.Id == group.Id)); }
        });
        check("Desktop shortcuts restore from the imported archive without touching the original profile", () =>
        {
            var from = Path.Combine(root, "archive-import-source"); var to = Path.Combine(root, "archive-import-target");
            var desktop = Path.Combine(root, "archive-import-desktop"); Directory.CreateDirectory(desktop);
            var archived = Path.Combine(from, "desktop-archive", "batch", "Example.url"); Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
            File.WriteAllText(archived, "[InternetShortcut]\r\nURL=https://example.com/\r\n");
            var original = Path.Combine(desktop, "Example.url");
            var manifest = Path.Combine(from, "desktop-archive", "manifest.json");
            File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(new[] { new { Name = "Example", Original = original, Archived = archived, At = DateTimeOffset.UtcNow } }));
            new ConfigurationStore(from).Save(new ShelfConfiguration());
            ProfileCopy.Copy(from, to);
            var imported = new AppShelf.App.ShelfSession(to);
            try
            {
                var task = new AppShelf.App.ShelfApi(imported, null).HandleAsync("restore_desktop", new System.Text.Json.Nodes.JsonObject());
                PumpUntil(() => task.IsCompleted);
                expect(task.Result.Ok && File.Exists(original), "The imported archive could not restore a shortcut");
                expect(File.Exists(archived) && File.ReadAllText(manifest).Contains(from.Replace("\\", "\\\\")), "Restoring used or modified the old archive");
                expect(!File.Exists(Path.Combine(to, "desktop-archive", "batch", "Example.url")), "The new archive was not used");
            }
            finally { imported.Icons.Dispose(); }
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
        check("Language selector saves English and updates existing dynamic UI resources immediately", () =>
        {
            session.Changed += application.ApplyLanguage;
            Window? host = null;
            try
            {
                session.Change(config => config.Language = "ru-RU");
                var sidebar = new System.Windows.Controls.TextBlock();
                sidebar.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "Text.MyFolders");
                host = new Window { Content = sidebar, Width = 200, Height = 80, ShowInTaskbar = false, ShowActivated = false };
                host.Show();
                expect(sidebar.Text == "Мои папки", "Initial Russian UI missing");
                var settings = new AppShelf.App.SettingsDialog { Owner = host };
                settings.Loaded += (_, _) =>
                {
                    var controls = Descendants(settings).ToList();
                    var selector = controls.OfType<System.Windows.Controls.ComboBox>().Single(box =>
                        System.Windows.Automation.AutomationProperties.GetName(box) == "Язык");
                    expect(selector.IsEnabled && selector.Items.Count == 2, "Language selector is still a placeholder");
                    selector.SelectedIndex = 0;
                    controls.OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == "Сохранить")
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                };
                expect(settings.ShowDialog() == true, "Language settings did not save");
                expect(session.Store.Load().Language == "en-US", "English selection did not persist");
                PumpUntil(() => sidebar.Text == "My folders");
                var dialog = new AppShelf.App.FolderSettingsDialog(folder.Id);
                try
                {
                    dialog.Show(); dialog.UpdateLayout();
                    expect(dialog.Title == "Folder settings", "Folder dialog title was not translated");
                    var labels = Descendants(dialog).OfType<System.Windows.Controls.TextBlock>().Select(block => block.Text).ToList();
                    expect(labels.Contains("Desktop") && labels.Contains("Taskbar") && labels.Contains("Rows per page"), "English folder controls missing");
                    expect(!labels.Any(label => System.Text.RegularExpressions.Regex.IsMatch(label, "[А-Яа-яЁё]")), "Russian text remained in the English folder dialog");
                }
                finally { dialog.Close(); }
                session.Change(config => config.Language = "ru-RU");
                PumpUntil(() => sidebar.Text == "Мои папки");
            }
            finally
            {
                session.Changed -= application.ApplyLanguage;
                host?.Close();
                Localization.SetLanguage("ru-RU");
            }
        });
        check("English dialogs translate settings, app properties, destinations and artwork labels", () =>
        {
            try
            {
                session.Change(config => config.Language = "en-US"); application.ApplyLanguage();
                var windows = new Window[]
                {
                    new AppShelf.App.SettingsDialog(),
                    new AppShelf.App.EntryDialog(ShelfItem.Application("Calculator", "calc.exe")),
                    new AppShelf.App.MoveDialog(new ShelfConfiguration { Items = [ShelfItem.Folder("Work")] }, []),
                    new AppShelf.App.FolderSettingsDialog(folder.Id)
                };
                foreach (var window in windows)
                {
                    try
                    {
                        window.Show(); window.UpdateLayout();
                        var labels = Descendants(window).OfType<FrameworkElement>().SelectMany(element => new[]
                        {
                            (element as System.Windows.Controls.TextBlock)?.Text,
                            (element as System.Windows.Controls.ContentControl)?.Content as string,
                            element.ToolTip as string,
                            System.Windows.Automation.AutomationProperties.GetName(element)
                        }).Append(window.Title).Where(text => text is not null && text != "Русский");
                        expect(!labels.Any(text => System.Text.RegularExpressions.Regex.IsMatch(text!, "[А-Яа-яЁё]")),
                            "Untranslated text in " + window.Title);
                    }
                    finally { window.Close(); }
                }
                foreach (var name in FolderFaceCatalog.Palette.Select(x => x.Name).Concat(FolderFaceCatalog.Symbols.Select(x => x.Name)))
                    expect(!System.Text.RegularExpressions.Regex.IsMatch(Localization.CatalogLabel(name), "[А-Яа-яЁё]"), "Untranslated artwork label: " + name);
            }
            finally { session.Change(config => config.Language = "ru-RU"); application.ApplyLanguage(); }
        });
        check("Settings put GitHub and Store buttons before support in both languages", () =>
        {
            try
            {
                foreach (var language in new[] { "en-US", "ru-RU" })
                {
                    session.Change(config => config.Language = language); application.ApplyLanguage();
                    var dialog = new AppShelf.App.SettingsDialog();
                    try
                    {
                        dialog.Show(); dialog.UpdateLayout();
                        var buttons = Descendants(dialog).OfType<System.Windows.Controls.Button>().ToList();
                        string Label(System.Windows.Controls.Button button) => System.Windows.Automation.AutomationProperties.GetName(button);
                        var gitHub = buttons.Single(button => Label(button) == Localization.Get("GitHub"));
                        var store = buttons.Single(button => Label(button) == Localization.Get("MicrosoftStore"));
                        var support = buttons.Single(button => Label(button) == Localization.Get("SupportOnBoosty"));
                        expect(buttons.IndexOf(gitHub) < buttons.IndexOf(store) && buttons.IndexOf(store) < buttons.IndexOf(support), "External links are not above support");
                        expect(gitHub.IsEnabled && store.IsEnabled == (AppLinks.MicrosoftStore is not null), "Store button does not follow product readiness");
                        expect(store.ToolTip as string == Localization.Get(store.IsEnabled ? "OpenInMicrosoftStore" : "MicrosoftStoreNotPublished"), "Store availability hint is missing");
                        expect(System.Windows.Controls.ToolTipService.GetShowOnDisabled(store), "The unavailable Store hint cannot be shown");
                        var report = buttons.Single(button => Label(button) == Localization.Get("ReportBug"));
                        var rate = buttons.SingleOrDefault(button => Label(button) == Localization.Get("RateInMicrosoftStore"));
                        expect(report.Content as string == Localization.Get("ReportBug") && report.ToolTip as string == AppLinks.NewIssue!.AbsoluteUri, "Bug report button is wrong");
                        expect((rate is null) == (AppLinks.MicrosoftStoreReview is null), "Rating button does not follow product readiness");
                        expect(buttons.IndexOf(store) < buttons.IndexOf(report) && buttons.IndexOf(report) < buttons.IndexOf(support)
                            && (rate is null || buttons.IndexOf(report) < buttons.IndexOf(rate) && buttons.IndexOf(rate) < buttons.IndexOf(support)), "Feedback buttons are out of place");
                        var storeImage = (System.Windows.Controls.Image)store.Content;
                        var badge = (BitmapImage)storeImage.Source;
                        expect(badge.UriSource.AbsoluteUri.Contains($"microsoft-store-{(language == "ru-RU" ? "ru" : "en")}.png"), "Store badge does not follow the interface language");
                        expect(badge.PixelWidth >= 644 && gitHub.ActualHeight == store.ActualHeight, "Branded artwork failed to load or badge heights differ");
                    }
                    finally { dialog.Close(); }
                }
            }
            finally { session.Change(config => config.Language = "ru-RU"); application.ApplyLanguage(); }
        });
        check("English API errors and artwork names work with legacy Russian aliases", () =>
        {
            var apiSession = new AppShelf.App.ShelfSession(Path.Combine(root, "english-api"));
            apiSession.Change(config => config.Items.Add(ShelfItem.Folder("Work")));
            var api = new AppShelf.App.ShelfApi(apiSession, null);
            ShelfApiChannel.Reply Call(string tool, string json = "{}")
            {
                var task = api.HandleAsync(tool, System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject());
                PumpUntil(() => task.IsCompleted); return task.Result;
            }
            try
            {
                Localization.SetLanguage("en-US");
                expect(Call("unknown").Text == "Unknown tool: unknown", "Unknown tool error is not English");
                expect(Call("move_item").Text == "Missing parameter: item.", "Parameter error is not English");
                expect(Call("place_folder", "{\"folder\":\"Missing\",\"taskbar\":false}").Text == "Folder “Missing” was not found.", "Folder error is not English");
                var english = Call("set_folder_look", "{\"folder\":\"Work\",\"look\":\"symbol\",\"symbol\":\"Games\",\"color\":\"Blue\"}");
                expect(english.Ok && english.Text.Contains("Games") && english.Text.Contains("Blue"), "English artwork names were not applied: " + english.Text);
                var russian = Call("set_folder_look", "{\"folder\":\"Work\",\"symbol\":\"Игры\",\"color\":\"Синий\"}");
                expect(russian.Ok && russian.Text == english.Text, "Legacy Russian artwork names changed behavior");
                expect(Call("set_folder_look", "{\"folder\":\"Work\",\"color\":\"Automatic\"}").Ok, "Automatic color was not accepted");
                var schema = ShelfApiTools.List().Single(tool => tool!["name"]!.GetValue<string>() == "set_folder_look")!;
                var colors = schema["inputSchema"]!["properties"]!["color"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
                expect(colors.Contains("Blue") && colors.Contains("Automatic") && !colors.Contains("Синий"), "MCP still advertises Russian-only colors");
                Localization.SetLanguage("ru-RU");
                expect(Call("unknown").Text == "Неизвестный инструмент: unknown", "Russian API messages were lost");
                expect(Call("set_folder_look", "{\"folder\":\"Work\",\"symbol\":\"Games\",\"color\":\"Blue\"}").Ok, "English names depend on UI language");
            }
            finally { apiSession.Icons.Dispose(); Localization.SetLanguage("ru-RU"); }
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
    private sealed record AppShelfApiReply(bool Ok, string Text);
    private sealed class WindowTestApplication : AppShelf.App.App
    {
        // Application queues startup from its constructor; never run production startup in a component test.
        protected override void OnStartup(StartupEventArgs e) { }
    }
}
