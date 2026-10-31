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
    protected Button Accept { get; } = new() { Content = L.Get("Save"), MinWidth = 110, Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
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
        var cancel = new Button { Content = L.Get("Cancel"), MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 7, 16, 7), IsCancel = true };
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
    public EntryDialog(ShelfItem entry) : base(L.Get("AppProperties"), 540)
    {
        Result = ConfigurationStore.Clone(new ShelfConfiguration { Items = [entry] }).Items[0];
        Heading(L.Get("AppProperties"));
        var name = Field(L.Get("Name"), entry.Name); name.MaxLength = 200;
        var target = Field(L.Get("ApplicationFileOrLink"), entry.Target);
        var browse = new Button { Content = L.Get("ChooseFile"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        browse.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = L.Get("ApplicationFileFilter") }; if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName; };
        Body.Children.Add(browse);
        var arguments = Field(L.Get("LaunchArguments"), entry.Arguments);
        var working = Field(L.Get("WorkingDirectoryOptional"), entry.WorkingDirectory);
        var icon = Field(L.Get("CustomIconOptional"), entry.IconPath);
        var iconBrowse = new Button { Content = L.Get("ChooseIcon"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        iconBrowse.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = L.Get("IconFileFilter") }; if (dialog.ShowDialog(this) == true) icon.Text = dialog.FileName; };
        Body.Children.Add(iconBrowse);
        var admin = new CheckBox { Content = L.Get("RunAsAdministrator"), IsChecked = entry.RunAsAdministrator, Margin = new Thickness(0, 18, 0, 0) };
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
    public LibraryDialog() : base(L.Get("AddApps"), 580)
    {
        Height = Math.Min(700, SystemParameters.WorkArea.Height - 80); SizeToContent = SizeToContent.Manual; ResizeMode = ResizeMode.CanResize;
        Heading(L.Get("AddApps"), L.Get("ChooseTheAppsToIncludeInThisFolder"));
        search = Field(L.Get("SearchInstalledApps"), ""); search.TextChanged += (_, _) => Filter();
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
        status = new TextBlock { Text = L.Get("LoadingWindowsApps"), Margin = new Thickness(0, 10, 0, 0) }; status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(status);
        var browse = new Button { Content = L.Get("ChooseFiles"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Multiselect = true, Filter = L.Get("ApplicationFileFilter") };
            if (picker.ShowDialog(this) != true) return;
            foreach (var path in picker.FileNames)
            {
                var option = new LibraryOption(ShelfItem.Application(Path.GetFileNameWithoutExtension(path), path)) { Selected = true };
                option.PropertyChanged += (_, _) => SelectionChanged(); options.Add(option);
            }
            Filter(); SelectionChanged();
        };
        Body.Children.Add(browse);
        Accept.Content = L.Get("Add"); Accept.IsEnabled = false; Accept.IsDefault = false;
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
            catch (Exception ex) { status.Text = L.Get("CouldNotLoadTheList") + ex.Message + L.Get("YouCanSelectFilesManually"); }
        };
    }
    private void Filter() => list.ItemsSource = options.Where(x => x.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).OrderBy(x => x.Name).ToList();
    private void SelectionChanged()
    {
        var count = options.Count(x => x.Selected); status.Text = count == 0 ? L.F("AppsFound0", options.Count) : L.F("Selected0", count);
        Accept.IsEnabled = count > 0; Accept.Content = count == 0 ? L.Get("Add") : L.F("Add0", count);
    }
}

public sealed class MoveDialog : ShelfDialog
{
    private sealed record DestinationOption(Guid? Id, string Name) { public override string ToString() => Name; }
    public Guid? Destination { get; private set; }
    public MoveDialog(ShelfConfiguration config, HashSet<Guid> excluded) : base(L.Get("MoveToFolder"))
    {
        Heading(L.Get("WhereWouldYouLikeToMoveIt"));
        var choices = new List<DestinationOption> { new(null, L.Get("AllFolders")) };
        void Add(IEnumerable<ShelfItem> items, int depth)
        {
            foreach (var folder in items.Where(x => x.IsFolder && !excluded.Contains(x.Id)))
            { choices.Add(new(folder.Id, new string(' ', depth * 3) + "▸ " + folder.Name)); Add(folder.Children, depth + 1); }
        }
        Add(config.Items, 0);
        var list = new ListBox { ItemsSource = choices, SelectedIndex = 0, MaxHeight = 340 }; Body.Children.Add(list);
        Accept.Content = L.Get("Move"); Accept.Click += (_, _) => { Destination = ((DestinationOption)list.SelectedItem).Id; DialogResult = true; };
    }
}

public sealed class SettingsDialog : ShelfDialog
{
    public SettingsDialog() : base(L.Get("AppShelfSettings"), 520)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AppShelf;component/BrandLinks.xaml") });
        var config = App.Current.Session.Configuration;
        Heading(L.Get("AppSettings"), "AppShelf · " + typeof(App).Assembly.GetName().Version?.ToString(3));
        Body.Children.Add(new TextBlock { Text = L.Get("EditorTheme"), Margin = new Thickness(0, 8, 0, 8) });
        var appearance = new ComboBox { ItemsSource = new[] { L.Get("FollowWindows"), L.Get("Light"), L.Get("Dark") }, SelectedIndex = (int)config.Appearance };
        Body.Children.Add(appearance);
        Body.Children.Add(new TextBlock { Text = L.Get("Language"), Margin = new Thickness(0, 22, 0, 8) });
        var language = new ComboBox { ItemsSource = new[] { "English", "Русский" },
            SelectedIndex = L.NormalizeLanguage(config.Language) == "ru-RU" ? 1 : 0,
            ToolTip = L.Get("ChooseTheInterfaceLanguage") };
        System.Windows.Automation.AutomationProperties.SetName(language, L.Get("Language"));
        Body.Children.Add(language);
        bool? initialStartup = AppLocations.IsPackaged ? null : ShellIntegration.RunAtStartup;
        var startup = new CheckBox { Content = L.Get("StartAppShelfWhenISignInToWindows"), IsChecked = initialStartup == true,
            IsEnabled = initialStartup is not null, Margin = new Thickness(0, 24, 0, 0) };
        Body.Children.Add(startup);
        if (AppLocations.IsPackaged) Loaded += async (_, _) =>
        {
            try
            {
                var enabled = await ShellIntegration.ReadRunAtStartupAsync();
                if (!IsVisible) return;
                initialStartup = enabled; startup.IsChecked = enabled; startup.IsEnabled = true;
            }
            catch (Exception ex) { if (IsVisible) App.Current.Report(ex); }
        };
        var hint = new TextBlock { Text = L.Get("BackgroundOperationHelp"), TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 22, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(hint);
        var updates = new CheckBox { Content = L.Get("CheckForUpdates"), IsChecked = config.CheckForUpdates, Margin = new Thickness(0, 20, 0, 0) };
        Body.Children.Add(updates);
        Body.Children.Add(new TextBlock { Text = L.Get("AppIcons"), Margin = new Thickness(0, 24, 0, 8) });
        var icons = new Button { Content = L.Get("RefreshIcons"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        icons.Click += (_, _) => { App.Current.RefreshIcons(); icons.Content = L.Get("RefreshStarted"); icons.IsEnabled = false; };
        Body.Children.Add(icons);
        var iconsHint = new TextBlock { Text = L.Get("RefreshIconsHelp"), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        iconsHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(iconsHint);
        var links = new WrapPanel { Margin = new Thickness(0, 24, 0, 0) };
        static Image Badge(string asset, double width, double height) => new()
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/AppShelf;component/Assets/Brand/" + asset)),
            Width = width, Height = height, Stretch = Stretch.Uniform, IsHitTestVisible = false
        };
        Button Link(string label, FrameworkElement badge, Action open)
        {
            var button = new Button { Content = badge, Style = (Style)FindResource("BrandLinkButton"), Margin = new Thickness(0, 0, 12, 8) };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => open(); links.Children.Add(button); return button;
        }
        var gitHubBadge = new Border { Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(92, 92, 92)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4.5),
            Child = Badge("github-lockup-white.png", 118, 27) };
        Link(L.Get("GitHub"), gitHubBadge, App.Current.OpenGitHubPage).ToolTip = AppLinks.GitHub;
        var badgeLanguage = L.NormalizeLanguage(config.Language) == "ru-RU" ? "ru" : "en";
        var store = Link(L.Get("MicrosoftStore"), Badge($"microsoft-store-{badgeLanguage}.png", 161, 44), App.Current.OpenStorePage);
        store.IsEnabled = AppLinks.MicrosoftStore is not null;
        store.ToolTip = L.Get(store.IsEnabled ? "OpenInMicrosoftStore" : "MicrosoftStoreNotPublished");
        ToolTipService.SetShowOnDisabled(store, true);
        Body.Children.Add(links);
        // Feedback actions appear only when their target exists: no repository, no report; no Store ID, no rating.
        var feedback = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Feedback(string label, Uri? target, Action open)
        {
            if (target is null) return;
            var button = new Button { Content = label, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 8), ToolTip = target.AbsoluteUri };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => open(); feedback.Children.Add(button);
        }
        Feedback(L.Get("ReportBug"), AppLinks.NewIssue, App.Current.OpenNewIssuePage);
        Feedback(L.Get("RateInMicrosoftStore"), AppLinks.MicrosoftStoreReview, App.Current.OpenStoreReview);
        if (feedback.Children.Count > 0) Body.Children.Add(feedback);
        // A quiet way to support the author: only here and in the tray menu, never a reminder.
        var supportHint = new TextBlock { Text = L.Get("SupportHelp"), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 26, 0, 8), FontSize = 12 };
        supportHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(supportHint);
        var supportContent = new StackPanel { Orientation = Orientation.Horizontal };
        var heart = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) };
        heart.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.AccentColorBrushKey);
        supportContent.Children.Add(heart); supportContent.Children.Add(new TextBlock { Text = L.Get("SupportProject") });
        var support = new Button { Content = supportContent, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6), ToolTip = AppLinks.Support };
        System.Windows.Automation.AutomationProperties.SetName(support, L.Get("SupportOnBoosty"));
        support.Click += (_, _) => App.Current.OpenSupportPage();
        Body.Children.Add(support);
        Accept.Click += async (_, _) =>
        {
            if (!Accept.IsEnabled) return;
            Accept.IsEnabled = false;
            try
            {
                if (initialStartup is { } previous && (startup.IsChecked == true) != previous)
                    await ShellIntegration.SetRunAtStartupAsync(startup.IsChecked == true, App.Current.Session.Store.DirectoryPath);
                if (!IsVisible) return;
                App.Current.Session.Change(next =>
                {
                    next.Appearance = (Appearance)appearance.SelectedIndex;
                    next.Language = language.SelectedIndex == 1 ? "ru-RU" : "en-US";
                    next.CheckForUpdates = updates.IsChecked == true;
                });
                DialogResult = true;
            }
            catch (Exception ex) { App.Current.Report(ex); }
            finally { if (IsVisible) Accept.IsEnabled = true; }
        };
    }
}

public sealed class FolderSettingsDialog : ShelfDialog
{
    private const double FacePreviewSize = 96;
    public FolderSettingsDialog(Guid folderId) : base(L.Get("FolderSettings"), 560)
    {
        var folder = ShelfTree.Find(App.Current.Session.Configuration, folderId) ?? throw new InvalidOperationException(L.Get("TheFolderWasDeleted"));
        var settings = folder.Settings;
        Heading(folder.Name, L.Get("TheseSettingsApplyOnlyToThisFolder"));
        // Live preview of the menu, drawn by the same grid code as the real folder menu.
        var surfaces = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var previewDesktop = new RadioButton { Content = L.Get("Desktop"), GroupName = "PreviewSurface" + folderId, IsChecked = true, Margin = new Thickness(0, 0, 18, 0) };
        var previewTaskbar = new RadioButton { Content = L.Get("Taskbar"), GroupName = "PreviewSurface" + folderId };
        surfaces.Children.Add(previewDesktop); surfaces.Children.Add(previewTaskbar);
        Body.Children.Add(surfaces);
        var previewTitle = new TextBlock { Text = folder.Name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(5, 1, 5, 2) };
        previewTitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var previewTiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var previewDots = new ContentControl { Focusable = false };
        var previewGrid = new DockPanel(); DockPanel.SetDock(previewDots, Dock.Right);
        previewGrid.Children.Add(previewDots); previewGrid.Children.Add(previewTiles);
        var previewContent = new StackPanel(); previewContent.Children.Add(previewTitle); previewContent.Children.Add(previewGrid);
        var preview = new Border { Child = previewContent, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(5, 3, 5, 5),
            HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false, Focusable = false };
        System.Windows.Automation.AutomationProperties.SetName(preview, L.Get("FolderMenuPreview"));
        Body.Children.Add(preview);
        var empty = new TextBlock { Text = L.Get("ThisFolderIsEmptyAddAppsToPreviewItsMenu"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        // Folder icon look: desktop shortcut, taskbar button and the tile of this folder inside other folders.
        Action updateFace = () => { }; // assigned once every control below exists
        var face = settings.Face; var faceColor = settings.FaceColor; var faceSymbol = settings.FaceSymbol; var facePicture = settings.FaceImage;
        Body.Children.Add(new TextBlock { Text = L.Get("FolderIcon"), Margin = new Thickness(0, 22, 0, 8) });
        var faceRow = new Grid();
        faceRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        faceRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        // Large enough to judge colours and small app icons; drawn 1:1 with screen pixels, never rescaled.
        var faceImage = new Image { Width = FacePreviewSize, Height = FacePreviewSize, UseLayoutRounding = true, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(faceImage, BitmapScalingMode.HighQuality);
        var faceBox = new Border { Width = FacePreviewSize + 28, Height = FacePreviewSize + 28, CornerRadius = new CornerRadius(8), UseLayoutRounding = true, Child = faceImage, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0), Background = new LinearGradientBrush(Color.FromRgb(0x2E, 0x3E, 0x58), Color.FromRgb(0x8A, 0x9B, 0xB3), 45) };
        System.Windows.Automation.AutomationProperties.SetName(faceBox, L.Get("FolderIconPreview"));
        faceRow.Children.Add(faceBox);
        var faceControls = new StackPanel(); Grid.SetColumn(faceControls, 1); faceRow.Children.Add(faceControls);
        Body.Children.Add(faceRow);
        // Wraps rather than clips when a translation is longer (Russian "Картинка" did not fit at 520 px).
        var kinds = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var kindButtons = new Dictionary<FolderFace, RadioButton>();
        foreach (var (text, value) in new[] { (L.Get("Apps"), FolderFace.Preview), (L.Get("Symbol"), FolderFace.Symbol), (L.Get("Image"), FolderFace.Image) })
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
        swatchButtons.Add((Choice(automatic, L.Get("Automatic"), () => faceColor = ""), ""));
        foreach (var (name, hex) in FolderLook.Palette)
            swatchButtons.Add((Choice(new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(FolderLook.ParseColor(hex)!.Value) }, L.CatalogLabel(name), () => faceColor = hex), hex));
        foreach (var (button, _) in swatchButtons) swatches.Children.Add(button);
        faceControls.Children.Add(swatches);
        // Plate opacity: a colour stays see-through like the standard Windows 11 plate unless the user wants it solid.
        var opacityRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var opacityValue = new TextBlock { Text = settings.FaceOpacity + "%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(opacityValue, Dock.Right); opacityRow.Children.Add(opacityValue);
        var opacityLabel = new TextBlock { Text = L.Get("Opacity"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(opacityLabel, Dock.Left); opacityRow.Children.Add(opacityLabel);
        var opacity = new Slider { Minimum = 5, Maximum = 100, Value = settings.FaceOpacity, TickFrequency = 5, IsSnapToTickEnabled = true,
            SmallChange = 5, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(opacity, L.Get("BackgroundOpacity"));
        opacity.ValueChanged += (_, _) => { opacityValue.Text = $"{opacity.Value:0}%"; updateFace(); };
        opacityRow.Children.Add(opacity);
        faceControls.Children.Add(opacityRow);
        var symbols = new WrapPanel();
        var symbolButtons = new List<(Button Button, string Glyph)>();
        foreach (var (glyph, name) in FolderLook.Symbols)
        {
            var text = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var button = Choice(text, L.CatalogLabel(name), () => faceSymbol = glyph);
            symbolButtons.Add((button, glyph)); symbols.Children.Add(button);
        }
        faceControls.Children.Add(symbols);
        var picturePanel = new StackPanel();
        var pick = new Button { Content = L.Get("ChooseImage"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        var pictureName = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        pictureName.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        pick.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = L.Get("FolderIconImage"), Filter = L.Get("ImageFileFilter") };
            if (picker.ShowDialog(this) != true) return;
            facePicture = picker.FileName; face = FolderFace.Image; kindButtons[FolderFace.Image].IsChecked = true; updateFace();
        };
        picturePanel.Children.Add(pick); picturePanel.Children.Add(pictureName);
        faceControls.Children.Add(picturePanel);
        var faceHint = new TextBlock { Text = L.Get("FolderIconHelp"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        faceHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Body.Children.Add(faceHint);

        Body.Children.Add(new TextBlock { Text = L.Get("MenuTheme"), Margin = new Thickness(0, 18, 0, 8) });
        var appearance = new ComboBox { ItemsSource = new[] { L.Get("FollowWindows"), L.Get("Light"), L.Get("Dark") }, SelectedIndex = (int)settings.Appearance };
        Body.Children.Add(appearance);
        // Grid size: more apps than columns × rows are paged like Start menu folders.
        var size = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        size.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        size.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
        size.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ComboBox SizeBox(string label, int column, IEnumerable<int> range, int value)
        {
            var panel = new StackPanel(); Grid.SetColumn(panel, column);
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
            var box = new ComboBox { ItemsSource = range.ToArray(), SelectedItem = value };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            panel.Children.Add(box); size.Children.Add(panel); return box;
        }
        var columns = SizeBox(L.Get("Columns"), 0, Enumerable.Range(3, 6), settings.Columns);
        var rows = SizeBox(L.Get("RowsPerPage"), 2, Enumerable.Range(1, 8), settings.Rows);
        Body.Children.Add(size);
        var pagingHint = new TextBlock { Text = L.Get("PagingHelp"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        pagingHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(pagingHint);
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
        var desktopScale = AddScale(L.Get("IconScaleDesktop"), settings.DesktopIconScale);
        var taskbarScale = AddScale(L.Get("IconScaleTaskbar"), settings.TaskbarIconScale);
        var scaleHint = new TextBlock { Text = L.Get("SurfaceScaleHelp"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        scaleHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(scaleHint);
        var labels = new CheckBox { Content = L.Get("ShowIconLabels"), IsChecked = settings.ShowLabels, Margin = new Thickness(0, 22, 0, 0) }; Body.Children.Add(labels);
        var close = new CheckBox { Content = L.Get("CloseAfterLaunchingAnApp"), IsChecked = settings.CloseAfterLaunch, Margin = new Thickness(0, 16, 0, 0) }; Body.Children.Add(close);
        var outside = new CheckBox { Content = L.Get("CloseOnOutsideClickOrWindowSwitch"), IsChecked = settings.CloseOnFocusLost, Margin = new Thickness(0, 16, 0, 0) }; Body.Children.Add(outside);
        FolderSettings Draft(FolderSettings current) => new()
        {
            Appearance = (Appearance)appearance.SelectedIndex,
            Columns = (int)columns.SelectedItem, Rows = (int)rows.SelectedItem, IconSize = current.IconSize,
            DesktopIconScale = (int)Math.Round(desktopScale.Value), TaskbarIconScale = (int)Math.Round(taskbarScale.Value),
            ShowLabels = labels.IsChecked == true, CloseAfterLaunch = close.IsChecked == true,
            CloseOnFocusLost = outside.IsChecked == true, ShowOnTaskbar = current.ShowOnTaskbar,
            Face = face, FaceColor = faceColor, FaceOpacity = (int)Math.Round(opacity.Value), FaceSymbol = face == FolderFace.Symbol ? faceSymbol : current.FaceSymbol, FaceImage = facePicture
        };
        async void UpdateFace()
        {
            if (!IsLoaded) return;
            symbols.Visibility = face == FolderFace.Symbol ? Visibility.Visible : Visibility.Collapsed;
            opacityRow.Visibility = face == FolderFace.Image ? Visibility.Collapsed : Visibility.Visible;
            picturePanel.Visibility = face == FolderFace.Image ? Visibility.Visible : Visibility.Collapsed;
            pictureName.Text = FolderIconWriter.PicturePath(facePicture) is null ? L.Get("NoImageSelected") : Path.GetFileName(facePicture);
            Brush selected = (Brush)FindResource(SystemColors.AccentColorBrushKey);
            foreach (var (button, hex) in swatchButtons) { button.BorderBrush = hex == faceColor ? selected : Brushes.Transparent; button.BorderThickness = new Thickness(2); }
            foreach (var (button, glyph) in symbolButtons) { button.BorderBrush = glyph == faceSymbol ? selected : Brushes.Transparent; button.BorderThickness = new Thickness(2); }
            try
            {
                var previews = new List<ImageSource?>();
                foreach (var child in FolderPreview.Applications(folder))
                    previews.Add(await App.Current.Session.Icons.GetAsync(string.IsNullOrWhiteSpace(child.IconPath) ? child.Target : child.IconPath));
                var look = await FolderIconWriter.LookAsync(Draft(settings));
                faceImage.Source = FolderArtwork.Render(previews, (int)Math.Round(FacePreviewSize * VisualTreeHelper.GetDpi(this).PixelsPerDip), look: look);
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
            // The first page exactly as the menu shows it, with the page dots on the right when there is more.
            var items = folder.Children.Take(FolderGrid.PageSize(draft)).ToList();
            var (tileWidth, _) = FolderGrid.TileSize(draft, desktop);
            previewTiles.Width = FolderGrid.Columns(draft, folder.Children.Count) * tileWidth;
            previewTiles.Children.Clear();
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var item in items) previewTiles.Children.Add(FolderGrid.Tile(item, draft, desktop, pixelsPerDip, dark));
            previewDots.Content = FolderGrid.Dots(FolderGrid.Pages(draft, folder.Children.Count), 0, null);
            previewContent.Children.Remove(empty);
            if (items.Count == 0) previewContent.Children.Add(empty);
        }
        appearance.SelectionChanged += (_, _) => UpdatePreview();
        columns.SelectionChanged += (_, _) => UpdatePreview();
        rows.SelectionChanged += (_, _) => UpdatePreview();
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
                    var current = ShelfTree.Find(config, folderId) ?? throw new InvalidOperationException(L.Get("TheFolderWasDeleted"));
                    current.Settings = Draft(current.Settings);
                });
                DialogResult = true;
            }
            catch (Exception ex) { App.Current.Report(ex); }
        };
    }
}
