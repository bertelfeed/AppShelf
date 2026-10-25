using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppShelf.App;

public partial class FolderView : UserControl
{
    public Guid? FolderId { get; private set; }
    public event Action<Guid?>? Navigated;
    public event Action? ApplicationLaunched;
    public bool Compact { get; set; }
    private Point dragOrigin;
    private bool dragging;
    private ShelfSession Session => App.Current.Session;
    public FolderView() { InitializeComponent(); }
    private void OnLoaded(object sender, RoutedEventArgs e) { Session.Changed += Refresh; Refresh(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Session.Changed -= Refresh;

    public void Navigate(Guid? id)
    {
        if (id is { } value && ShelfTree.Find(Session.Configuration, value) is not { IsFolder: true }) id = null;
        FolderId = id;
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Clear();
        Refresh(); Navigated?.Invoke(id);
    }

    public void Refresh()
    {
        if (!IsInitialized || App.Current.Session is null) return;
        var config = Session.Configuration;
        if (FolderId is { } id && ShelfTree.Find(config, id) is not { IsFolder: true }) FolderId = null;
        var search = SearchBox.Text.Trim();
        IEnumerable<ShelfItem> items = search.Length == 0 ? ShelfTree.Container(config, FolderId)
            : ShelfTree.Walk(ShelfTree.Container(config, FolderId)).Where(x => x.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        var list = items.Select(x => new TileModel(x)).ToList();
        TileGrid.ItemsSource = list;
        EmptyPanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TileGrid.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = search.Length > 0 ? "Ничего не найдено" : FolderId is null ? "Соберите приложения в папки" : "Папка пока пуста";
        EmptyText.Text = search.Length > 0 ? "Попробуйте другое название." : "Добавьте приложения из Windows или перетащите сюда ярлыки. Папки можно вкладывать друг в друга.";
        EmptyAdd.Visibility = search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FolderTitle.Text = FolderId is { } f ? ShelfTree.Find(config, f)!.Name : "Мои папки";
        FolderTitle.FontSize = Compact ? 22 : 28;
        FolderSubtitle.Text = search.Length > 0 ? $"Результатов: {list.Count}" : Russian.Elements(list.Count);
        BackButton.IsEnabled = FolderId is not null;
        PinButton.Visibility = FolderId is null ? Visibility.Collapsed : Visibility.Visible;
        FolderSettingsButton.Visibility = PinButton.Visibility;
        BreadcrumbPanel.Children.Clear();
        AddCrumb("Все папки", null);
        foreach (var folder in ShelfTree.Breadcrumbs(config, FolderId))
        {
            BreadcrumbPanel.Children.Add(new TextBlock { Text = "›", Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
            AddCrumb(folder.Name, folder.Id);
        }
    }
    private void AddCrumb(string name, Guid? id)
    {
        var button = new Button { Content = name, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(7, 5, 7, 5), MaxWidth = 160, ToolTip = name };
        button.Click += (_, _) => Navigate(id); BreadcrumbPanel.Children.Add(button);
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Refresh(); }
    private void BackClick(object sender, RoutedEventArgs e) => Back();
    public void Back()
    {
        var path = ShelfTree.Breadcrumbs(Session.Configuration, FolderId);
        Navigate(path.Count > 1 ? path[^2].Id : null);
    }
    private void TileClick(object sender, RoutedEventArgs e)
    {
        if (dragging || ((Button)sender).DataContext is not TileModel model) return;
        if (model.Item.IsFolder) Navigate(model.Item.Id); else Launch(model.Item);
    }
    private void Launch(ShelfItem item, bool elevated = false)
    {
        try { ShellIntegration.Launch(item, elevated); ApplicationLaunched?.Invoke(); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { App.Current.Report(ex); }
    }
    private void AddClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "Приложения из Windows…", AddFromLibrary);
        AddMenu(menu, "Ярлыки и файлы…", AddFiles);
        AddMenu(menu, "Ссылка или команда…", AddCustom);
        menu.Items.Add(new Separator());
        AddMenu(menu, "Новая папка", NewFolder);
        OpenMenu(menu, (FrameworkElement)sender);
    }
    private void LibraryClick(object sender, RoutedEventArgs e) => AddFromLibrary();
    public void NewFolder()
    {
        var name = NameDialog.Ask(Window.GetWindow(this), "Новая папка", "Название", "Новая папка");
        if (name is null) return;
        var folder = ShelfItem.Folder(name);
        Change(config => ShelfTree.Container(config, FolderId).Add(folder));
        Navigate(folder.Id);
    }
    public void AddFromLibrary()
    {
        var dialog = new LibraryDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var added = dialog.SelectedApps;
        Change(config => ShelfTree.Container(config, FolderId).AddRange(ConfigurationStore.CopyItems(added)));
        StatusText.Text = $"Добавлено приложений: {added.Count}";
    }
    private void AddFiles()
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "Добавить приложения, ярлыки или файлы", Filter = "Приложения и ярлыки|*.exe;*.lnk;*.url;*.appref-ms|Все файлы|*.*" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) AddPaths(picker.FileNames, FolderId);
    }
    private void AddCustom()
    {
        var item = ShelfItem.Application("Новое приложение", "");
        var dialog = new EntryDialog(item) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) Change(config => ShelfTree.Container(config, FolderId).Add(dialog.Result));
    }
    private void PinClick(object sender, RoutedEventArgs e) => Pin();
    private void FolderSettingsClick(object sender, RoutedEventArgs e)
    { if (FolderId is { } id) new FolderSettingsDialog(id) { Owner = Window.GetWindow(this) }.ShowDialog(); }
    public void Pin()
    {
        if (FolderId is not { } id) return;
        try
        {
            var folder = ShelfTree.Find(Session.Configuration, id)!;
            var dialog = new PlacementDialog(folder) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
        }
        catch (Exception ex) { App.Current.Report(ex); }
    }
    private void MoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (FolderId is { } id)
        {
            AddMenu(menu, "Открыть с панели задач", () => App.Current.ShowFolder(id));
            AddMenu(menu, "Панель задач и рабочий стол…", Pin);
            AddMenu(menu, "Параметры папки…", () => new FolderSettingsDialog(id) { Owner = Window.GetWindow(this) }.ShowDialog());
            AddMenu(menu, "Переименовать…", () => Rename(id));
            AddMenu(menu, "Запустить всё в папке", () => LaunchAll(id));
            menu.Items.Add(new Separator());
        }
        AddMenu(menu, "Сортировать по названию", () => Change(config => ShelfTree.Container(config, FolderId).Sort((a, b) => { var kind = a.Kind.CompareTo(b.Kind); return kind != 0 ? kind : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name); })));
        AddMenu(menu, "Отменить последнее действие", () => { try { Session.Undo(); } catch (Exception ex) { App.Current.Report(ex); } }, Session.CanUndo);
        if (Compact) AddMenu(menu, "Открыть AppShelf", () => App.Current.ShowEditor(FolderId));
        else
        {
            menu.Items.Add(new Separator());
            AddMenu(menu, "Завершить работу AppShelf", () => App.Current.Quit());
        }
        OpenMenu(menu, (FrameworkElement)sender);
    }
    private void TileContext(object sender, MouseButtonEventArgs e)
    {
        if (((Button)sender).DataContext is not TileModel model) return;
        var item = model.Item;
        var menu = new ContextMenu();
        AddMenu(menu, item.IsFolder ? "Открыть папку" : "Открыть", () => { if (item.IsFolder) Navigate(item.Id); else Launch(item); });
        if (item.IsFolder)
        {
            AddMenu(menu, "Открыть компактное меню", () => App.Current.ShowFolder(item.Id));
            AddMenu(menu, "Параметры папки…", () => new FolderSettingsDialog(item.Id) { Owner = Window.GetWindow(this) }.ShowDialog());
            AddMenu(menu, "Панель задач и рабочий стол…", () => { Navigate(item.Id); Pin(); });
            AddMenu(menu, "Добавить на рабочий стол", async () =>
            {
                try { await App.Current.Publisher.AddDesktopAsync(item.Id); StatusText.Text = "Папка добавлена на рабочий стол"; }
                catch (Exception ex) { App.Current.Report(ex); }
            });
        }
        else AddMenu(menu, "Запустить от имени администратора", () => Launch(item, true));
        AddMenu(menu, "Переименовать…", () => Rename(item.Id));
        if (!item.IsFolder) AddMenu(menu, "Свойства…", () => Edit(item));
        AddMenu(menu, "Переместить…", () => MoveWithDialog(item));
        menu.Items.Add(new Separator());
        AddMenu(menu, "Убрать из AppShelf", () => Remove(item));
        OpenMenu(menu, (FrameworkElement)sender); e.Handled = true;
    }
    private void Rename(Guid id)
    {
        var item = ShelfTree.Find(Session.Configuration, id)!;
        var name = NameDialog.Ask(Window.GetWindow(this), "Переименовать", "Название", item.Name);
        if (name is not null) Change(config => ShelfTree.Find(config, id)!.Name = name);
    }
    private void Edit(ShelfItem item)
    {
        var dialog = new EntryDialog(item) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        Change(config => { var parent = ShelfTree.Parent(config, item.Id)!; parent[parent.FindIndex(x => x.Id == item.Id)] = dialog.Result; });
    }
    private void Remove(ShelfItem item)
    {
        if (item.IsFolder && item.Children.Count > 0 && MessageBox.Show(Window.GetWindow(this), "Убрать папку вместе с её содержимым из AppShelf? Приложения и файлы останутся на компьютере.", "Убрать папку «" + item.Name + "»", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Change(config => ShelfTree.Parent(config, item.Id)!.RemoveAll(x => x.Id == item.Id));
        StatusText.Text = "Элемент убран. Ctrl+Z — отменить";
    }
    private void MoveWithDialog(ShelfItem item)
    {
        var excluded = ShelfTree.Walk(new[] { item }).Select(x => x.Id).ToHashSet();
        var dialog = new MoveDialog(Session.Configuration, excluded) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) Change(config => ShelfTree.Move(config, item.Id, dialog.Destination));
    }
    private void LaunchAll(Guid id)
    {
        var apps = ShelfTree.Find(Session.Configuration, id)!.Children.Where(x => !x.IsFolder).ToList();
        if (apps.Count == 0) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Запустить приложений: {apps.Count}?", "Запустить всё", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        foreach (var item in apps) Launch(item);
    }
    private void DragStart(object sender, MouseButtonEventArgs e) { dragOrigin = e.GetPosition(this); }
    private void DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragging || ((Button)sender).DataContext is not TileModel model) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragging = true;
        try { DragDrop.DoDragDrop((Button)sender, new DataObject("AppShelf.Item", model.Item.Id.ToString()), DragDropEffects.Move); }
        finally { dragging = false; }
    }
    private static void DragEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("AppShelf.Item") ? DragDropEffects.Move : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void TileDragOver(object sender, DragEventArgs e) => DragEffect(e);
    private void BackgroundDragOver(object sender, DragEventArgs e) => DragEffect(e);
    private void TileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (((Button)sender).DataContext is not TileModel target) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { AddPaths((string[])e.Data.GetData(DataFormats.FileDrop), target.Item.IsFolder ? target.Item.Id : FolderId); return; }
        if (e.Data.GetData("AppShelf.Item") is not string text || !Guid.TryParse(text, out var source) || source == target.Item.Id) return;
        Change(config =>
        {
            var current = ShelfTree.Find(config, target.Item.Id)!;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ShelfTree.Move(config, source, FolderId, current.Id);
            else if (current.IsFolder) ShelfTree.Move(config, source, current.Id);
            else
            {
                var origin = ShelfTree.Find(config, source)!;
                if (origin.IsFolder) { ShelfTree.Move(config, source, FolderId, current.Id); return; }
                var parent = ShelfTree.Parent(config, current.Id)!;
                var folder = ShelfItem.Folder("Новая папка");
                parent.Insert(parent.IndexOf(current), folder);
                ShelfTree.Move(config, current.Id, folder.Id);
                ShelfTree.Move(config, source, folder.Id);
            }
        });
    }
    private void BackgroundDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) AddPaths((string[])e.Data.GetData(DataFormats.FileDrop), FolderId);
        else if (e.Data.GetData("AppShelf.Item") is string text && Guid.TryParse(text, out var id)) Change(config => ShelfTree.Move(config, id, FolderId));
    }
    private void AddPaths(string[] paths, Guid? folder)
    {
        var entries = paths.Where(x => File.Exists(x) || Directory.Exists(x)).Select(path => ShelfItem.Application(Path.GetFileNameWithoutExtension(path), path)).ToList();
        if (entries.Count > 0) Change(config => ShelfTree.Container(config, folder).AddRange(entries));
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox) { Session.Undo(); e.Handled = true; }
        if ((e.Key == Key.Back && Keyboard.FocusedElement is not TextBox) || (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)) { Back(); e.Handled = true; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        if (Keyboard.FocusedElement is Button { DataContext: TileModel model })
        {
            if (e.Key == Key.F2) { Rename(model.Item.Id); e.Handled = true; }
            if (e.Key == Key.Delete) { Remove(model.Item); e.Handled = true; }
        }
    }
    private void Change(Action<ShelfConfiguration> action) { try { Session.Change(action); } catch (Exception ex) { App.Current.Report(ex); } }
    internal static void AddMenu(ContextMenu menu, string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    internal static void OpenMenu(ContextMenu menu, FrameworkElement target)
    { menu.PlacementTarget = target; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
}
