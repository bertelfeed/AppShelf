using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AppShelf.App;

public sealed class FolderPopup : Window
{
    private readonly Button title;
    private readonly WrapPanel tiles;
    private bool armed;
    private bool fromDesktop;
    private PopupDismissMonitor? dismissMonitor;
    private Guid currentId;
    private FolderSettings settings = new();
    public Guid RootFolderId { get; private set; }
    public bool OpenedFromDesktop => fromDesktop;
    public FolderPopup()
    {
        Title = "AppShelf — папка"; ShowInTaskbar = false; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new Grid { Margin = new Thickness(16, 12, 16, 14) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        title = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 8, 6, 14), FontSize = 18, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(ForegroundProperty, "TextBrush");
        title.Click += (_, _) => { if (currentId == RootFolderId) Hide(); else Back(); };
        root.Children.Add(title);
        tiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        KeyboardNavigation.SetDirectionalNavigation(tiles, KeyboardNavigationMode.Cycle);
        var scroll = new ScrollViewer { Content = tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        Content = root;
        SourceInitialized += (_, _) => WindowPlacement.StylePopup(this);
        Deactivated += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            if (armed && IsVisible && !IsActive && settings.CloseOnFocusLost && Mouse.Captured is null)
                Hide();
        }));
        IsVisibleChanged += (_, _) => { if (!IsVisible) { armed = false; dismissMonitor?.Dispose(); dismissMonitor = null; } };
        Closed += (_, _) => { dismissMonitor?.Dispose(); dismissMonitor = null; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            if (e.Key == Key.Back || e.SystemKey == Key.Left) { Back(); e.Handled = true; }
        };
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { Back(); e.Handled = true; } };
        App.Current.Session.Changed += () => { if (IsVisible) Render(); };
    }
    public void Open(Guid id, bool fromDesktop = false)
    {
        armed = false;
        this.fromDesktop = fromDesktop;
        RootFolderId = id;
        currentId = id;
        Render();
        WindowPlacement.FitAtCurrentPosition(this);
        new WindowInteropHelper(this).EnsureHandle();
        ShellIntegration.SetWindowAppId(new WindowInteropHelper(this).Handle, id);
        Show();
        WindowPlacement.NearPointer(this, fromDesktop);
        Activate();
        if (fromDesktop) WindowPlacement.ActivateFromDesktop(this);
        armed = true;
        RefreshDismissMonitor();
    }
    private void RefreshDismissMonitor()
    {
        dismissMonitor?.Dispose(); dismissMonitor = null;
        if (!armed || !IsVisible || !settings.CloseOnFocusLost) return;
        try { dismissMonitor = new PopupDismissMonitor(new WindowInteropHelper(this).Handle, () => Hide()); }
        catch (Exception ex) { App.Current.Log(ex); }
    }
    public void RefreshTheme() { if (IsVisible) Theme.ApplyTo(this, settings.Appearance); }
    private void Back()
    {
        if (currentId == RootFolderId) return;
        var path = ShelfTree.Breadcrumbs(App.Current.Session.Configuration, currentId);
        currentId = path.Count > 1 ? path[^2].Id : RootFolderId;
        Render();
        WindowPlacement.FitAtCurrentPosition(this);
    }
    private void Render()
    {
        var config = App.Current.Session.Configuration;
        if (ShelfTree.Find(config, RootFolderId) is not { IsFolder: true }) { Hide(); return; }
        var folder = ShelfTree.Find(config, currentId);
        if (folder is not { IsFolder: true }) folder = ShelfTree.Find(config, currentId = RootFolderId)!;
        settings = folder.Settings;
        Theme.ApplyTo(this, settings.Appearance);
        Title = folder.Name;
        title.Content = new TextBlock { Text = folder.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
        title.ToolTip = currentId == RootFolderId ? "Нажмите на название, чтобы закрыть папку · Esc" : "Нажмите на название, чтобы вернуться · Esc — закрыть";
        tiles.Children.Clear();
        var iconSize = settings.IconSizeFor(fromDesktop);
        var scale = (fromDesktop ? settings.DesktopIconScale : settings.TaskbarIconScale) / 100d;
        var tileWidth = iconSize + 28 * scale;
        var tileHeight = iconSize + 28 * scale + (settings.ShowLabels ? 26 : 0);
        var columns = Math.Min(settings.Columns, Math.Max(1, folder.Children.Count));
        Width = Math.Max(220, columns * tileWidth + 36);
        Height = Math.Max(94, Math.Min(6, Math.Ceiling(folder.Children.Count / (double)columns)) * tileHeight + 80);
        tiles.Width = columns * tileWidth;
        foreach (var item in folder.Children)
        {
            var model = new TileModel(item, settings.ShowLabels);
            var image = new Image { Width = iconSize, Height = iconSize };
            image.SetBinding(Image.SourceProperty, new Binding(nameof(TileModel.Icon)));
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(image);
            if (settings.ShowLabels)
            {
                var label = new TextBlock { Text = item.Name, FontSize = 11, TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, Margin = new Thickness(0, 6, 0, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); content.Children.Add(label);
            }
            var button = new Button { Width = tileWidth - 4, Height = tileHeight - 4, Margin = new Thickness(2),
                Content = content, ToolTip = item.Name, DataContext = model, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6) };
            System.Windows.Automation.AutomationProperties.SetName(button, item.Name);
            button.Click += (_, _) =>
            {
                if (item.IsFolder) { currentId = item.Id; Render(); WindowPlacement.FitAtCurrentPosition(this); return; }
                try
                {
                    ShellIntegration.Launch(item);
                    if (settings.CloseAfterLaunch) Hide();
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
                catch (Exception ex) { App.Current.Report(ex); }
            };
            tiles.Children.Add(button);
        }
        RefreshDismissMonitor();
    }
}
