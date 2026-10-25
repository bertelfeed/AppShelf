using System.Windows;
using Microsoft.Win32;

namespace AppShelf.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public ShelfSession Session { get; private set; } = null!;
    public FolderPublisher Publisher { get; private set; } = null!;
    public TaskbarFolders TaskbarFolders { get; private set; } = null!;
    public DesktopSingleClick? DesktopClicks { get; private set; }
    public ShelfApi? Api { get; private set; }
    public ShelfRepair? Repair { get; private set; }
    private readonly CancellationTokenSource apiStop = new();
    private SingleInstance? instance;
    private TrayIcon? tray;
    private MainWindow? editor;
    private FolderPopup? popup;
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception);
            args.Handled = true;
        };
        try
        {
            var directory = Argument(e.Args, "--data-dir") ?? (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
                ? Path.Combine(AppContext.BaseDirectory, "data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppShelf"));
            instance = new SingleInstance(directory);
            var folderId = Argument(e.Args, "--folder");
            var surface = Argument(e.Args, "--surface") == "desktop" ? "desktop" : "taskbar";
            var command = e.Args.Contains("--quit") ? "quit" : folderId is not null ? "folder:" + folderId + ":" + surface : e.Args.Contains("--background") ? "background" : "editor";
            if (!instance.IsPrimary)
            {
                await instance.ForwardAsync(command);
                Shutdown(); return;
            }
            ShellIntegration.Initialize();
            Session = new ShelfSession(directory);
            // Shortcuts that their apps rewrote since the last run: refresh AppShelf's copies before icons are read.
            foreach (var item in ShelfTree.Walk(Session.Configuration.Items)) ShellIntegration.RefreshShortcutCopy(item);
            Theme.Apply(Session.Configuration.Appearance);
            Publisher = new FolderPublisher(Session);
            Repair = new ShelfRepair(Session);
            // Moved programs are found shortly after start, then every half hour and on every launch.
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                try { await Repair.CheckAllAsync(); } catch (Exception ex) { Log(ex); }
            });
            TaskbarFolders = new TaskbarFolders(Session);
            try { DesktopClicks = new DesktopSingleClick(Session.Store.DirectoryPath, id => ShowFolder(id, true, toggle: true)) { Error = Log }; }
            catch (Exception ex) { Log(ex); }
            Session.Changed += OnChanged;
            SystemEvents.UserPreferenceChanged += PreferenceChanged;
            tray = new TrayIcon(() => ShowEditor(), BuildTrayMenu);
            _ = instance.ListenAsync(message => Dispatcher.InvokeAsync(() => HandleCommand(message)));
            // Requests from the MCP server run on the UI thread like any edit in the editor.
            Api = new ShelfApi(Session, Publisher);
            _ = ShelfApiChannel.ListenAsync(directory, (tool, args) => Dispatcher.InvokeAsync(() => Api.HandleAsync(tool, args)).Task.Unwrap(), apiStop.Token);
            HandleCommand(command);
            if (Session.Store.RecoveryMessage is { } recovery) MessageBox.Show(recovery, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось открыть AppShelf", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }

    private static string? Argument(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void HandleCommand(string command)
    {
        if (command == "quit") Quit();
        else if (command.StartsWith("folder:") && command.Split(':') is var parts && Guid.TryParse(parts[1], out var id))
            ShowFolder(id, parts.Length > 2 && parts[2] == "desktop");
        else if (command != "background") ShowEditor();
    }

    public void ShowEditor(Guid? folder = null)
    {
        popup?.Hide();
        editor ??= new MainWindow();
        if (folder is not null) editor.OpenFolder(folder);
        MainWindow = editor;
        editor.Show();
        if (editor.WindowState == WindowState.Minimized) editor.WindowState = WindowState.Normal;
        editor.Activate();
    }

    /// <param name="toggle">
    /// A click on the folder's own button or icon closes an open menu. Taskbar buttons always toggle;
    /// a launched desktop shortcut (double-click) only activates, so it never closes what it opened.
    /// </param>
    public void ShowFolder(Guid id, bool fromDesktop = false, bool toggle = false)
    {
        if (ShelfTree.Find(Session.Configuration, id) is not { IsFolder: true })
        { MessageBox.Show("Эта папка была удалена. Создайте новый ярлык для существующей папки.", "AppShelf"); return; }
        toggle |= !fromDesktop;
        if (popup is { IsVisible: true } && popup.RootFolderId == id && popup.OpenedFromDesktop == fromDesktop)
        {
            if (toggle) popup.Hide(); else popup.Activate();
            return;
        }
        // The press on the button already closed the menu as an outside click; its release must not reopen it.
        if (toggle && popup is not null && popup.JustClosedBySurface(id, fromDesktop)) return;
        popup ??= new FolderPopup();
        if (popup.IsVisible) popup.Hide();
        popup.Owner = editor is { IsVisible: true } ? editor : null;
        popup.Open(id, fromDesktop);
    }
    /// <summary>Starts an app from a folder; a program that moved is found first.</summary>
    public async Task LaunchAsync(ShelfItem item, bool elevated = false)
    {
        var ready = Repair is null ? item : await Repair.PrepareAsync(item);
        ShellIntegration.Launch(ready, elevated);
    }

    public void HideFolder(Guid id) { if (popup?.RootFolderId == id) popup.Hide(); }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string text, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = text };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("Открыть AppShelf", () => ShowEditor());
        foreach (var folder in Session.Configuration.Items.Where(x => x.IsFolder).Take(15))
        { var id = folder.Id; Add(folder.Name, () => ShowFolder(id)); }
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Завершить работу", Quit);
        return menu;
    }

    private void OnChanged() => RefreshTheme();
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.InvokeAsync(RefreshTheme);
    private void RefreshTheme() { Theme.Apply(Session.Configuration.Appearance); editor?.RefreshBackdrop(); popup?.RefreshTheme(); }

    /// <summary>Re-extracts every application icon, e.g. after an app update changed its logo.</summary>
    public void RefreshIcons()
    {
        Session.Icons.Clear();
        Session.NotifyChanged();
    }

    public void Report(Exception error)
    {
        Log(error);
        MessageBox.Show(error.Message, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    public void Log(Exception error)
    { try { File.AppendAllText(Path.Combine(Session.Store.DirectoryPath, "errors.log"), $"{DateTimeOffset.Now:O} {error}\n"); } catch { } }

    public void Quit() { IsExiting = true; Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        apiStop.Cancel();
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        tray?.Dispose();
        TaskbarFolders?.Dispose();
        DesktopClicks?.Dispose();
        Publisher?.Dispose();
        Session?.Icons.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
