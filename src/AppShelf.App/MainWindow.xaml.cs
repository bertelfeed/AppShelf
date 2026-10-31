using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace AppShelf.App;

public sealed class FolderNode
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public List<FolderNode> Children { get; init; } = [];
    public static List<FolderNode> From(IEnumerable<ShelfItem> items) => items.Where(x => x.IsFolder).Select(x => new FolderNode { Id = x.Id, Name = x.Name, Children = From(x.Children) }).ToList();
}

public partial class MainWindow : Window
{
    private bool refreshingTree;
    private bool mica;
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => { mica = WindowPlacement.EnableBackdrop(this, 2, Theme.IsDark); RefreshBackdrop(); };
        App.Current.Session.Changed += RefreshTree;
        Browser.Navigated += id => HomeButton.SetResourceReference(BackgroundProperty, id is null ? "SelectedBrush" : "CanvasBrush");
        RefreshTree();
        HomeButton.SetResourceReference(BackgroundProperty, "SelectedBrush");
        if (App.Current.Updates is { } updates) updates.Changed += RefreshUpdate;
        App.Current.Session.Changed += RefreshUpdate;
        RefreshUpdate();
    }
    public void OpenFolder(Guid? id) => Browser.Navigate(id);
    /// <summary>Keeps the Mica tint and the translucent editor colours in step with the editor theme.</summary>
    public void RefreshBackdrop()
    {
        if (!mica) return;
        WindowPlacement.SetDarkFrame(this, Theme.IsDark);
        Theme.ApplyBackdropTo(this);
    }
    private void RefreshTree()
    {
        refreshingTree = true;
        FolderTree.ItemsSource = FolderNode.From(App.Current.Session.Configuration.Items);
        refreshingTree = false;
    }
    /// <summary>A quiet notice above Backups while an update is out; nothing pops up.</summary>
    private void RefreshUpdate()
    {
        var updates = App.Current.Updates;
        UpdateButton.Visibility = updates is { Available: true } ? Visibility.Visible : Visibility.Collapsed;
        if (updates is null) return;
        UpdateText.Text = updates.Label; UpdateButton.ToolTip = updates.Help;
        System.Windows.Automation.AutomationProperties.SetName(UpdateButton, updates.Label);
    }
    private void UpdateClick(object sender, RoutedEventArgs e) => App.Current.Updates?.Open();
    private void HomeClick(object sender, RoutedEventArgs e) => Browser.Navigate(null);
    private void NewFolderClick(object sender, RoutedEventArgs e) => Browser.NewFolder();
    private void FolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (!refreshingTree && e.NewValue is FolderNode node) Browser.Navigate(node.Id); }
    private void SettingsClick(object sender, RoutedEventArgs e) => new SettingsDialog { Owner = this }.ShowDialog();
    private void SupportClick(object sender, RoutedEventArgs e) => App.Current.OpenSupportPage();
    private void BackupClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        FolderView.AddMenu(menu, L.Get("ExportAllFolders"), Export);
        FolderView.AddMenu(menu, L.Get("ImportFolders"), Import);
        FolderView.AddMenu(menu, L.Get("OpenSettingsDirectory"), () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{App.Current.Session.Store.DirectoryPath}\"") { UseShellExecute = true }));
        FolderView.OpenMenu(menu, (FrameworkElement)sender);
    }
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = L.Get("ExportFileFilter"), FileName = "AppShelf-" + DateTime.Now.ToString("yyyy-MM-dd") + ".appshelf.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(App.Current.Session.Configuration, ConfigurationStore.JsonOptions)); }
        catch (Exception ex) { App.Current.Report(ex); }
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = L.Get("ImportFileFilter") };
        if (dialog.ShowDialog(this) != true) return;
        try { App.Current.Session.Import(dialog.FileName); }
        catch (Exception ex) { App.Current.Report(ex); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.Current.IsExiting) return;
        e.Cancel = true; Hide();
    }
}
