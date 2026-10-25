using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppShelf.App;

public class ShelfDialog : Window
{
    protected StackPanel Body { get; } = new() { Margin = new Thickness(28, 24, 28, 24) };
    protected StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 16, 24, 16) };
    protected Button Accept { get; } = new() { Content = "Сохранить", MinWidth = 110, Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
    public ShelfDialog(string title, double width = 480)
    {
        Title = title; Width = width; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 80;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; FontFamily = new FontFamily("Segoe UI Variable Text"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; grid.Children.Add(scroll);
        var footer = new Border { Child = Actions, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "SubtleBrush"); footer.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Grid.SetRow(footer, 1); grid.Children.Add(footer);
        var cancel = new Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 7, 16, 7), IsCancel = true };
        Actions.Children.Add(cancel); Actions.Children.Add(Accept); Content = grid;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    protected void Heading(string text, string? description = null)
    {
        Body.Children.Add(new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        if (description is not null)
        {
            var block = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18), LineHeight = 20 };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(block);
        }
    }
    protected TextBox Field(string label, string value, bool multiline = false)
    {
        Body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 6) });
        var box = new TextBox { Text = value, Padding = new Thickness(10, 7, 10, 7), AcceptsReturn = multiline };
        Body.Children.Add(box); return box;
    }
}

public sealed class NameDialog : ShelfDialog
{
    private readonly TextBox name;
    public string Value => name.Text.Trim();
    private NameDialog(string title, string label, string value) : base(title)
    {
        Heading(title); name = Field(label, value); name.MaxLength = 200;
        Accept.IsEnabled = !string.IsNullOrWhiteSpace(value);
        name.TextChanged += (_, _) => Accept.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        Accept.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
    public static string? Ask(Window? owner, string title, string label, string value)
    { var dialog = new NameDialog(title, label, value) { Owner = owner }; return dialog.ShowDialog() == true ? dialog.Value : null; }
}

public sealed class EntryDialog : ShelfDialog
{
    public ShelfItem Result { get; private set; }
    public EntryDialog(ShelfItem entry) : base("Свойства приложения", 540)
    {
        Result = ConfigurationStore.Clone(new ShelfConfiguration { Items = [entry] }).Items[0];
        Heading("Свойства приложения");
        var name = Field("Название", entry.Name); name.MaxLength = 200;
        var target = Field("Приложение, файл или ссылка", entry.Target);
        var browse = new Button { Content = "Выбрать файл…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        browse.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "Приложения и ярлыки|*.exe;*.lnk;*.url|Все файлы|*.*" }; if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName; };
        Body.Children.Add(browse);
        var arguments = Field("Аргументы запуска", entry.Arguments);
        var working = Field("Рабочая папка (необязательно)", entry.WorkingDirectory);
        var icon = Field("Своя иконка (необязательно)", entry.IconPath);
        var iconBrowse = new Button { Content = "Выбрать иконку…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        iconBrowse.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "Иконки и изображения|*.ico;*.png;*.jpg;*.exe|Все файлы|*.*" }; if (dialog.ShowDialog(this) == true) icon.Text = dialog.FileName; };
        Body.Children.Add(iconBrowse);
        var admin = new CheckBox { Content = "Запускать от имени администратора", IsChecked = entry.RunAsAdministrator, Margin = new Thickness(0, 18, 0, 0) };
        Body.Children.Add(admin);
        void Validate() => Accept.IsEnabled = !string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(target.Text);
        name.TextChanged += (_, _) => Validate(); target.TextChanged += (_, _) => Validate(); Validate();
        Accept.Click += (_, _) =>
        {
            Result.Name = name.Text.Trim(); Result.Target = target.Text.Trim(); Result.Arguments = arguments.Text;
            Result.WorkingDirectory = working.Text.Trim(); Result.IconPath = icon.Text.Trim(); Result.RunAsAdministrator = admin.IsChecked == true;
            DialogResult = true;
        };
    }
}

public sealed class LibraryOption(ShelfItem item) : INotifyPropertyChanged
{
    public ShelfItem Item { get; } = item;
    public string Name => Item.Name;
    private bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LibraryDialog : ShelfDialog
{
    private readonly TextBox search;
    private readonly ListBox list;
    private readonly TextBlock status;
    private List<LibraryOption> options = [];
    public List<ShelfItem> SelectedApps => options.Where(x => x.Selected).Select(x => x.Item).ToList();
    public LibraryDialog() : base("Добавить приложения", 580)
    {
        Height = Math.Min(700, SystemParameters.WorkArea.Height - 80); SizeToContent = SizeToContent.Manual; ResizeMode = ResizeMode.CanResize;
        Heading("Добавить приложения", "Выберите приложения, которые хотите видеть в этой папке.");
        search = Field("Поиск в установленных приложениях", ""); search.TextChanged += (_, _) => Filter();
        list = new ListBox { Height = 320, BorderThickness = new Thickness(0), Margin = new Thickness(0, 12, 0, 0), Background = Brushes.Transparent };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        var template = new DataTemplate(typeof(LibraryOption));
        var checkbox = new FrameworkElementFactory(typeof(CheckBox));
        checkbox.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(LibraryOption.Selected)) { Mode = BindingMode.TwoWay });
        checkbox.SetValue(CheckBox.PaddingProperty, new Thickness(4, 8, 4, 8));
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(Image.WidthProperty, 28d); image.SetValue(Image.HeightProperty, 28d); image.SetValue(Image.MarginProperty, new Thickness(0, 0, 12, 0));
        image.AddHandler(LoadedEvent, new RoutedEventHandler(async (sender, _) =>
        {
            var view = (Image)sender; if (view.DataContext is not LibraryOption option) return;
            var icon = await App.Current.Session.Icons.GetAsync(option.Item.Target);
            if (ReferenceEquals(view.DataContext, option)) view.Source = icon;
        }));
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(LibraryOption.Name))); label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(image); row.AppendChild(label); checkbox.AppendChild(row); template.VisualTree = checkbox; list.ItemTemplate = template;
        Body.Children.Add(list);
        status = new TextBlock { Text = "Загрузка приложений Windows…", Margin = new Thickness(0, 10, 0, 0) }; status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(status);
        var browse = new Button { Content = "Выбрать файлы…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Multiselect = true, Filter = "Приложения и ярлыки|*.exe;*.lnk;*.url|Все файлы|*.*" };
            if (picker.ShowDialog(this) != true) return;
            foreach (var path in picker.FileNames)
            {
                var option = new LibraryOption(ShelfItem.Application(Path.GetFileNameWithoutExtension(path), path)) { Selected = true };
                option.PropertyChanged += (_, _) => SelectionChanged(); options.Add(option);
            }
            Filter(); SelectionChanged();
        };
        Body.Children.Add(browse);
        Accept.Content = "Добавить"; Accept.IsEnabled = false; Accept.IsDefault = false;
        Accept.Click += (_, _) => DialogResult = true;
        Loaded += async (_, _) =>
        {
            search.Focus();
            try
            {
                var apps = await ShellIntegration.InstalledApplicationsAsync();
                foreach (var item in apps) { var option = new LibraryOption(item); option.PropertyChanged += (_, _) => SelectionChanged(); options.Add(option); }
                Filter(); SelectionChanged();
            }
            catch (Exception ex) { status.Text = "Не удалось получить список: " + ex.Message + ". Можно выбрать файлы вручную."; }
        };
    }
    private void Filter() => list.ItemsSource = options.Where(x => x.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).OrderBy(x => x.Name).ToList();
    private void SelectionChanged()
    {
        var count = options.Count(x => x.Selected); status.Text = count == 0 ? $"Найдено приложений: {options.Count}" : $"Выбрано: {count}";
        Accept.IsEnabled = count > 0; Accept.Content = count == 0 ? "Добавить" : $"Добавить ({count})";
    }
}

public sealed class MoveDialog : ShelfDialog
{
    private sealed record DestinationOption(Guid? Id, string Name) { public override string ToString() => Name; }
    public Guid? Destination { get; private set; }
    public MoveDialog(ShelfConfiguration config, HashSet<Guid> excluded) : base("Переместить в папку")
    {
        Heading("Куда переместить?");
        var choices = new List<DestinationOption> { new(null, "Все папки") };
        void Add(IEnumerable<ShelfItem> items, int depth)
        {
            foreach (var folder in items.Where(x => x.IsFolder && !excluded.Contains(x.Id)))
            { choices.Add(new(folder.Id, new string(' ', depth * 3) + "▸ " + folder.Name)); Add(folder.Children, depth + 1); }
        }
        Add(config.Items, 0);
        var list = new ListBox { ItemsSource = choices, SelectedIndex = 0, MaxHeight = 340 }; Body.Children.Add(list);
        Accept.Content = "Переместить"; Accept.Click += (_, _) => { Destination = ((DestinationOption)list.SelectedItem).Id; DialogResult = true; };
    }
}

public sealed class PinDialog : ShelfDialog
{
    public PinDialog(string folderName, string shortcutPath) : base("Закрепить папку", 500)
    {
        Heading("«" + folderName + "» на панели задач", "Ярлык папки готов. Закрепление выполняется через меню Windows.");
        foreach (var text in new[] { "1. Нажмите «Показать ярлык».", "2. Нажмите правой кнопкой по выделенному ярлыку.", "3. Выберите «Закрепить на панели задач». При необходимости откройте «Показать дополнительные параметры»." })
            Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), LineHeight = 21 });
        var desktop = new Button { Content = "Создать ярлык на рабочем столе", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0) };
        desktop.Click += (_, _) =>
        {
            try
            {
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.GetFileName(shortcutPath));
                File.Copy(shortcutPath, file, true); desktop.Content = "Ярлык создан"; desktop.IsEnabled = false;
            }
            catch (Exception ex) { App.Current.Report(ex); }
        };
        Body.Children.Add(desktop);
        Accept.Content = "Показать ярлык";
        Accept.Click += (_, _) => { ShellIntegration.RevealFile(shortcutPath); DialogResult = true; };
    }
}

public sealed class SettingsDialog : ShelfDialog
{
    public SettingsDialog() : base("Параметры AppShelf", 520)
    {
        var config = App.Current.Session.Configuration;
        Heading("Параметры приложения", "AppShelf · 0.2.0");
        Body.Children.Add(new TextBlock { Text = "Тема редактора", Margin = new Thickness(0, 8, 0, 8) });
        var appearance = new ComboBox { ItemsSource = new[] { "Как в Windows", "Светлая", "Тёмная" }, SelectedIndex = (int)config.Appearance };
        Body.Children.Add(appearance);
        Body.Children.Add(new TextBlock { Text = "Язык (скоро)", Margin = new Thickness(0, 22, 0, 8) });
        Body.Children.Add(new ComboBox { ItemsSource = new[] { "Русский" }, SelectedIndex = 0, IsEnabled = false, ToolTip = "Переключение языка пока недоступно" });
        var startup = new CheckBox { Content = "Запускать AppShelf при входе в Windows", IsChecked = ShellIntegration.RunAtStartup, Margin = new Thickness(0, 24, 0, 0) };
        Body.Children.Add(startup);
        var hint = new TextBlock { Text = "Кнопки папок на панели задач и одиночный клик на рабочем столе работают, пока AppShelf запущен в фоне. Крестик скрывает редактор; выход доступен в меню значка у часов.", TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 22, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(hint);
        Accept.Click += (_, _) =>
        {
            try
            {
                if ((startup.IsChecked == true) != ShellIntegration.RunAtStartup)
                    ShellIntegration.SetRunAtStartup(startup.IsChecked == true, App.Current.Session.Store.DirectoryPath);
                App.Current.Session.Change(next => next.Appearance = (Appearance)appearance.SelectedIndex);
                DialogResult = true;
            }
            catch (Exception ex) { App.Current.Report(ex); }
        };
    }
}

public sealed class FolderSettingsDialog : ShelfDialog
{
    public FolderSettingsDialog(Guid folderId) : base("Параметры папки", 520)
    {
        var folder = ShelfTree.Find(App.Current.Session.Configuration, folderId) ?? throw new InvalidOperationException("Папка удалена.");
        var settings = folder.Settings;
        Heading(folder.Name, "Настройки применяются только к этой папке.");
        Body.Children.Add(new TextBlock { Text = "Тема меню", Margin = new Thickness(0, 8, 0, 8) });
        var appearance = new ComboBox { ItemsSource = new[] { "Как в Windows", "Светлая", "Тёмная" }, SelectedIndex = (int)settings.Appearance };
        Body.Children.Add(appearance);
        Body.Children.Add(new TextBlock { Text = "Столбцов", Margin = new Thickness(0, 18, 0, 8) });
        var columns = new ComboBox { ItemsSource = Enumerable.Range(3, 6).ToArray(), SelectedItem = settings.Columns }; Body.Children.Add(columns);
        Slider AddScale(string label, int value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 18, 0, 8) };
            var percent = new TextBlock { Text = value + "%", HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(percent, Dock.Right); row.Children.Add(percent);
            row.Children.Add(new TextBlock { Text = label }); Body.Children.Add(row);
            var slider = new Slider { Minimum = 50, Maximum = 200, Value = value,
                TickFrequency = 10, IsSnapToTickEnabled = true, SmallChange = 10, LargeChange = 20 };
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            slider.ValueChanged += (_, _) => percent.Text = $"{slider.Value:0}%";
            Body.Children.Add(slider); return slider;
        }
        var desktopScale = AddScale("Масштаб иконок · рабочий стол", settings.DesktopIconScale);
        var taskbarScale = AddScale("Масштаб иконок · панель задач", settings.TaskbarIconScale);
        var scaleHint = new TextBlock { Text = "Размер иконок внутри открытой папки. Настройки для двух мест независимы.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        scaleHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(scaleHint);
        var labels = new CheckBox { Content = "Подписи под иконками", IsChecked = settings.ShowLabels, Margin = new Thickness(0, 22, 0, 0) }; Body.Children.Add(labels);
        var close = new CheckBox { Content = "Закрывать после запуска приложения", IsChecked = settings.CloseAfterLaunch, Margin = new Thickness(0, 16, 0, 0) }; Body.Children.Add(close);
        var outside = new CheckBox { Content = "Закрывать по клику снаружи и при переключении окна", IsChecked = settings.CloseOnFocusLost, Margin = new Thickness(0, 16, 0, 0) }; Body.Children.Add(outside);
        Accept.Click += (_, _) =>
        {
            try
            {
                App.Current.Session.Change(config =>
                {
                    var current = ShelfTree.Find(config, folderId) ?? throw new InvalidOperationException("Папка удалена.");
                    current.Settings = new FolderSettings
                    {
                        Appearance = (Appearance)appearance.SelectedIndex,
                        Columns = (int)columns.SelectedItem, IconSize = current.Settings.IconSize,
                        DesktopIconScale = (int)Math.Round(desktopScale.Value), TaskbarIconScale = (int)Math.Round(taskbarScale.Value),
                        ShowLabels = labels.IsChecked == true, CloseAfterLaunch = close.IsChecked == true,
                        CloseOnFocusLost = outside.IsChecked == true, ShowOnTaskbar = current.Settings.ShowOnTaskbar
                    };
                });
                DialogResult = true;
            }
            catch (Exception ex) { App.Current.Report(ex); }
        };
    }
}
