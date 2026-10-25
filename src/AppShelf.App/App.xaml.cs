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
            Theme.Apply(Session.Configuration.Appearance);
            Publisher = new FolderPublisher(Session);
            TaskbarFolders = new TaskbarFolders(Session);
            try { DesktopClicks = new DesktopSingleClick(Session.Store.DirectoryPath, id => ShowFolder(id, true)); }
            catch (Exception ex) { Log(ex); }
            Session.Changed += OnChanged;
            SystemEvents.UserPreferenceChanged += PreferenceChanged;
            tray = new TrayIcon(() => ShowEditor(), BuildTrayMenu);
            _ = instance.ListenAsync(message => Dispatcher.InvokeAsync(() => HandleCommand(message)));
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

    public void ShowFolder(Guid id, bool fromDesktop = false)
    {
        if (ShelfTree.Find(Session.Configuration, id) is not { IsFolder: true })
        { MessageBox.Show("Эта папка была удалена. Создайте новый ярлык для существующей папки.", "AppShelf"); return; }
        if (popup is { IsVisible: true } && popup.RootFolderId == id && popup.OpenedFromDesktop == fromDesktop)
        {
            if (fromDesktop) popup.Activate(); else popup.Hide();
            return;
        }
        popup ??= new FolderPopup();
        if (popup.IsVisible) popup.Hide();
        popup.Owner = editor is { IsVisible: true } ? editor : null;
        popup.Open(id, fromDesktop);
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

    private void OnChanged() { Theme.Apply(Session.Configuration.Appearance); popup?.RefreshTheme(); }
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.InvokeAsync(() =>
    { Theme.Apply(Session.Configuration.Appearance); popup?.RefreshTheme(); });

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
