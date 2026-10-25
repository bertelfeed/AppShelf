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

public sealed class SettingsDialog : ShelfDialog
{
    public SettingsDialog() : base("Параметры AppShelf", 520)
    {
        var config = App.Current.Session.Configuration;
        Heading("Параметры приложения", "AppShelf · 0.3.0");
        Body.Children.Add(new TextBlock { Text = "Тема редактора", Margin = new Thickness(0, 8, 0, 8) });
        var appearance = new ComboBox { ItemsSource = new[] { "Как в Windows", "Светлая", "Тёмная" }, SelectedIndex = (int)config.Appearance };
        Body.Children.Add(appearance);
        Body.Children.Add(new TextBlock { Text = "Язык (скоро)", Margin = new Thickness(0, 22, 0, 8) });
        Body.Children.Add(new ComboBox { ItemsSource = new[] { "Русский" }, SelectedIndex = 0, IsEnabled = false, ToolTip = "Переключение языка пока недоступно" });
        var startup = new CheckBox { Content = "Запускать AppShelf при входе в Windows", IsChecked = ShellIntegration.RunAtStartup, Margin = new Thickness(0, 24, 0, 0) };
        Body.Children.Add(startup);
        var hint = new TextBlock { Text = "Кнопки папок на панели задач и одиночный клик на рабочем столе работают, пока AppShelf запущен в фоне. Крестик скрывает редактор; выход доступен в меню значка у часов.", TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 22, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(hint);
        Body.Children.Add(new TextBlock { Text = "Иконки приложений", Margin = new Thickness(0, 24, 0, 8) });
        var icons = new Button { Content = "Обновить иконки", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        icons.Click += (_, _) => { App.Current.RefreshIcons(); icons.Content = "Обновление запущено"; icons.IsEnabled = false; };
        Body.Children.Add(icons);
        var iconsHint = new TextBlock { Text = "Если приложение обновилось, а в папках осталась старая иконка. Иконки также обновляются сами раз в неделю.", TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        iconsHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(iconsHint);
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
        // Live preview of the menu, drawn by the same grid code as the real folder menu.
        var surfaces = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var previewDesktop = new RadioButton { Content = "Рабочий стол", GroupName = "PreviewSurface" + folderId, IsChecked = true, Margin = new Thickness(0, 0, 18, 0) };
        var previewTaskbar = new RadioButton { Content = "Панель задач", GroupName = "PreviewSurface" + folderId };
        surfaces.Children.Add(previewDesktop); surfaces.Children.Add(previewTaskbar);
        Body.Children.Add(surfaces);
        var previewTitle = new TextBlock { Text = folder.Name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(5, 1, 5, 2) };
        previewTitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var previewTiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var previewContent = new StackPanel(); previewContent.Children.Add(previewTitle); previewContent.Children.Add(previewTiles);
        var preview = new Border { Child = previewContent, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(5, 3, 5, 5),
            HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false, Focusable = false };
        System.Windows.Automation.AutomationProperties.SetName(preview, "Предпросмотр меню папки");
        Body.Children.Add(preview);
        var empty = new TextBlock { Text = "Папка пуста — добавьте приложения, чтобы увидеть меню.", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        // Folder icon look: desktop shortcut, taskbar button and the tile of this folder inside other folders.
        Action updateFace = () => { }; // assigned once every control below exists
        var face = settings.Face; var faceColor = settings.FaceColor; var faceSymbol = settings.FaceSymbol; var facePicture = settings.FaceImage;
        Body.Children.Add(new TextBlock { Text = "Значок папки", Margin = new Thickness(0, 22, 0, 8) });
        var faceRow = new Grid();
        faceRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        faceRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var faceImage = new Image { Width = 64, Height = 64 };
        RenderOptions.SetBitmapScalingMode(faceImage, BitmapScalingMode.HighQuality);
        var faceBox = new Border { Width = 92, Height = 92, CornerRadius = new CornerRadius(8), Child = faceImage, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0), Background = new LinearGradientBrush(Color.FromRgb(0x2E, 0x3E, 0x58), Color.FromRgb(0x8A, 0x9B, 0xB3), 45) };
        System.Windows.Automation.AutomationProperties.SetName(faceBox, "Предпросмотр значка папки");
        faceRow.Children.Add(faceBox);
        var faceControls = new StackPanel(); Grid.SetColumn(faceControls, 1); faceRow.Children.Add(faceControls);
        Body.Children.Add(faceRow);
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var kindButtons = new Dictionary<FolderFace, RadioButton>();
        foreach (var (text, value) in new[] { ("Приложения", FolderFace.Preview), ("Символ", FolderFace.Symbol), ("Картинка", FolderFace.Image) })
        {
            var radio = new RadioButton { Content = text, GroupName = "Face" + folderId, IsChecked = face == value, Margin = new Thickness(0, 0, 14, 0), MinWidth = 0 };
            radio.Checked += (_, _) => { face = value; if (face == FolderFace.Symbol && faceSymbol.Length == 0) faceSymbol = FolderLook.Symbols[0].Glyph; updateFace(); };
            kindButtons[value] = radio; kinds.Children.Add(radio);
        }
        faceControls.Children.Add(kinds);
        Button Choice(object content, string tip, Action select)
        {
            var button = new Button { Content = content, ToolTip = tip, Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4) };
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            button.Click += (_, _) => { select(); updateFace(); };
            return button;
        }
        var swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var swatchButtons = new List<(Button Button, string Hex)>();
        var automatic = new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x55, 236, 239, 244)) };
        automatic.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
        swatchButtons.Add((Choice(automatic, "Автоматически", () => faceColor = ""), ""));
        foreach (var (name, hex) in FolderLook.Palette)
            swatchButtons.Add((Choice(new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(FolderLook.ParseColor(hex)!.Value) }, name, () => faceColor = hex), hex));
        foreach (var (button, _) in swatchButtons) swatches.Children.Add(button);
        faceControls.Children.Add(swatches);
        var symbols = new WrapPanel();
        var symbolButtons = new List<(Button Button, string Glyph)>();
        foreach (var (glyph, name) in FolderLook.Symbols)
        {
            var text = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var button = Choice(text, name, () => faceSymbol = glyph);
            symbolButtons.Add((button, glyph)); symbols.Children.Add(button);
        }
        faceControls.Children.Add(symbols);
        var picturePanel = new StackPanel();
        var pick = new Button { Content = "Выбрать картинку…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        var pictureName = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        pictureName.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        pick.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "Картинка для значка папки", Filter = "Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.ico" };
            if (picker.ShowDialog(this) != true) return;
            facePicture = picker.FileName; face = FolderFace.Image; kindButtons[FolderFace.Image].IsChecked = true; updateFace();
        };
        picturePanel.Children.Add(pick); picturePanel.Children.Add(pictureName);
        faceControls.Children.Add(picturePanel);
        var faceHint = new TextBlock { Text = "Виден на ярлыке рабочего стола, кнопке панели задач и внутри других папок. Для цветного смайлика выберите его картинкой.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        faceHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Body.Children.Add(faceHint);

        Body.Children.Add(new TextBlock { Text = "Тема меню", Margin = new Thickness(0, 18, 0, 8) });
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
        FolderSettings Draft(FolderSettings current) => new()
        {
            Appearance = (Appearance)appearance.SelectedIndex,
            Columns = (int)columns.SelectedItem, IconSize = current.IconSize,
            DesktopIconScale = (int)Math.Round(desktopScale.Value), TaskbarIconScale = (int)Math.Round(taskbarScale.Value),
            ShowLabels = labels.IsChecked == true, CloseAfterLaunch = close.IsChecked == true,
            CloseOnFocusLost = outside.IsChecked == true, ShowOnTaskbar = current.ShowOnTaskbar,
            Face = face, FaceColor = faceColor, FaceSymbol = face == FolderFace.Symbol ? faceSymbol : current.FaceSymbol, FaceImage = facePicture
        };
        async void UpdateFace()
        {
            if (!IsLoaded) return;
            symbols.Visibility = face == FolderFace.Symbol ? Visibility.Visible : Visibility.Collapsed;
            picturePanel.Visibility = face == FolderFace.Image ? Visibility.Visible : Visibility.Collapsed;
            pictureName.Text = FolderIconWriter.PicturePath(facePicture) is null ? "Картинка не выбрана" : Path.GetFileName(facePicture);
            Brush selected = (Brush)FindResource(SystemColors.AccentColorBrushKey);
            foreach (var (button, hex) in swatchButtons) { button.BorderBrush = hex == faceColor ? selected : Brushes.Transparent; button.BorderThickness = new Thickness(2); }
            foreach (var (button, glyph) in symbolButtons) { button.BorderBrush = glyph == faceSymbol ? selected : Brushes.Transparent; button.BorderThickness = new Thickness(2); }
            try
            {
                var previews = new List<ImageSource?>();
                foreach (var child in FolderPreview.Applications(folder))
                    previews.Add(await App.Current.Session.Icons.GetAsync(string.IsNullOrWhiteSpace(child.IconPath) ? child.Target : child.IconPath));
                var look = await FolderIconWriter.LookAsync(Draft(settings));
                faceImage.Source = FolderArtwork.Render(previews, (int)Math.Ceiling(64 * VisualTreeHelper.GetDpi(this).PixelsPerDip), look: look);
            }
            catch (Exception ex) { App.Current.Log(ex); }
        }
        updateFace = UpdateFace;
        Loaded += (_, _) => UpdateFace();
        void UpdatePreview()
        {
            if (!IsLoaded) return;
            var draft = Draft(settings);
            var desktop = previewDesktop.IsChecked == true;
            var dark = Theme.IsDarkFor(draft.Appearance);
            preview.Resources["TextBrush"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
            preview.Resources["MutedBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0xB8, 0xB8, 0xB8) : Color.FromRgb(0x61, 0x61, 0x61));
            preview.Background = new SolidColorBrush(dark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xF3, 0xF3, 0xF3));
            preview.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x33, 255, 255, 255) : Color.FromArgb(0x1F, 0, 0, 0));
            var items = folder.Children.Take(draft.Columns * 2).ToList();
            var (tileWidth, _) = FolderGrid.TileSize(draft, desktop);
            previewTiles.Width = FolderGrid.Columns(draft, items.Count) * tileWidth;
            previewTiles.Children.Clear();
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var item in items) previewTiles.Children.Add(FolderGrid.Tile(item, draft, desktop, pixelsPerDip, dark));
            previewContent.Children.Remove(empty);
            if (items.Count == 0) previewContent.Children.Add(empty);
        }
        appearance.SelectionChanged += (_, _) => UpdatePreview();
        columns.SelectionChanged += (_, _) => UpdatePreview();
        labels.Click += (_, _) => UpdatePreview();
        previewDesktop.Checked += (_, _) => UpdatePreview();
        previewTaskbar.Checked += (_, _) => UpdatePreview();
        // Moving a slider shows the surface it belongs to.
        desktopScale.ValueChanged += (_, _) => { if (previewDesktop.IsChecked != true) previewDesktop.IsChecked = true; else UpdatePreview(); };
        taskbarScale.ValueChanged += (_, _) => { if (previewTaskbar.IsChecked != true) previewTaskbar.IsChecked = true; else UpdatePreview(); };
        Loaded += (_, _) => UpdatePreview();
        Accept.Click += (_, _) =>
        {
            try
            {
                // The picture is copied into the profile only on save; without a picture the app preview stays.
                if (face == FolderFace.Image && Path.IsPathRooted(facePicture) && File.Exists(facePicture))
                    facePicture = FolderIconWriter.ImportPicture(folderId, facePicture);
                if (face == FolderFace.Image && FolderIconWriter.PicturePath(facePicture) is null) face = FolderFace.Preview;
                App.Current.Session.Change(config =>
                {
                    var current = ShelfTree.Find(config, folderId) ?? throw new InvalidOperationException("Папка удалена.");
                    current.Settings = Draft(current.Settings);
                });
                DialogResult = true;
            }
            catch (Exception ex) { App.Current.Report(ex); }
        };
    }
}
