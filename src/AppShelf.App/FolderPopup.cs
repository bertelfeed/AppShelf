using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AppShelf.App;

/// <summary>Icon grid shared by the folder menu and the live preview in folder settings.</summary>
public static class FolderGrid
{
    public const int MaxRows = 6;
    /// <summary>Icons almost touch: only a narrow gap that leaves room for the hover highlight.</summary>
    public static (double Width, double Height) TileSize(FolderSettings settings, bool fromDesktop)
    {
        var icon = settings.IconSizeFor(fromDesktop);
        var width = Math.Round(icon + Math.Max(8, icon * .3));
        return (width, width + (settings.ShowLabels ? 24 : 0));
    }
    public static int Columns(FolderSettings settings, int count) => Math.Min(settings.Columns, Math.Max(1, count));

    public static Button Tile(ShelfItem item, FolderSettings settings, bool fromDesktop, double pixelsPerDip, bool dark)
    {
        var iconSize = settings.IconSizeFor(fromDesktop);
        var (tileWidth, tileHeight) = TileSize(settings, fromDesktop);
        // Nested folders are drawn at the exact device size so their four tiny previews stay sharp.
        var model = new TileModel(item, settings.ShowLabels, (int)Math.Ceiling(iconSize * pixelsPerDip), dark);
        var image = new Image { Width = iconSize, Height = iconSize, Opacity = model.IconOpacity };
        image.SetBinding(Image.SourceProperty, new Binding(nameof(TileModel.Icon)));
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(image);
        if (settings.ShowLabels)
        {
            var label = new TextBlock { Text = item.Name, FontSize = 11, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = tileWidth - 4, Margin = new Thickness(0, 4, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); content.Children.Add(label);
        }
        var button = new Button { Width = tileWidth - 2, Height = tileHeight - 2, Margin = new Thickness(1), MinWidth = 0, MinHeight = 0,
            Content = content, ToolTip = model.Missing ? item.Name + " — не найдено" : item.Name, DataContext = model, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(button, item.Name);
        return button;
    }
}

public sealed class FolderPopup : Window
{
    private readonly Grid root;
    private readonly TranslateTransform shift = new();
    private readonly Button title;
    private readonly ScrollViewer scroll;
    private readonly WrapPanel tiles;
    private bool armed;
    private bool fromDesktop;
    private bool backdrop;
    private PopupDismissMonitor? dismissMonitor;
    private Guid currentId;
    private FolderSettings settings = new();
    private DateTime openedAt, autoClosedAt;
    private Guid autoClosedId;
    private bool autoClosedFromDesktop;
    public Guid RootFolderId { get; private set; }
    public bool OpenedFromDesktop => fromDesktop;
    public FolderPopup()
    {
        Title = "AppShelf — папка"; ShowInTaskbar = false; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        // Icons are the menu; the name is a small quiet caption in the corner.
        root = new Grid { Margin = new Thickness(5, 3, 5, 5), RenderTransform = shift };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        title = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 0, 1),
            MinWidth = 0, MinHeight = 0, FontSize = 11 };
        title.SetResourceReference(ForegroundProperty, "MutedBrush");
        title.Click += (_, _) => { if (currentId == RootFolderId) Hide(); else Back(); };
        root.Children.Add(title);
        tiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        KeyboardNavigation.SetDirectionalNavigation(tiles, KeyboardNavigationMode.Cycle);
        scroll = new ScrollViewer { Content = tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        Content = root;
        SourceInitialized += (_, _) =>
        {
            backdrop = WindowPlacement.StylePopup(this, Theme.IsDarkFor(settings.Appearance));
            if (backdrop) Background = Brushes.Transparent;
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            if (armed && IsVisible && !IsActive && settings.CloseOnFocusLost && Mouse.Captured is null)
                AutoClose(WindowPlacement.ForegroundWindow());
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
        openedAt = DateTime.UtcNow;
        armed = true;
        RefreshDismissMonitor();
        AnimateIn();
    }

    /// <summary>
    /// True right after the menu closed because the user clicked the surface that opens it,
    /// so that click toggles the menu off instead of instantly reopening it.
    /// </summary>
    public bool JustClosedBySurface(Guid id, bool desktop) =>
        !IsVisible && id == autoClosedId && desktop == autoClosedFromDesktop && DateTime.UtcNow - autoClosedAt < TimeSpan.FromMilliseconds(600);

    private void AutoClose(IntPtr target)
    {
        if (!IsVisible) return;
        // During a double-click on the desktop shortcut the second click lands on the desktop;
        // it must not close the menu that the first click has just opened.
        if (fromDesktop && WindowPlacement.IsDesktop(target) &&
            DateTime.UtcNow - openedAt < TimeSpan.FromMilliseconds(WindowPlacement.DoubleClickTime + 150)) return;
        autoClosedAt = DateTime.UtcNow; autoClosedId = RootFolderId; autoClosedFromDesktop = fromDesktop;
        Hide();
    }
    private void AnimateIn()
    {
        root.BeginAnimation(OpacityProperty, null); shift.BeginAnimation(TranslateTransform.YProperty, null);
        if (!SystemParameters.ClientAreaAnimation) return;
        // A short slide from the surface that opened the menu, like Windows flyouts.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromDesktop ? -6 : 8, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
    }
    private void RefreshDismissMonitor()
    {
        dismissMonitor?.Dispose(); dismissMonitor = null;
        if (!armed || !IsVisible || !settings.CloseOnFocusLost) return;
        try { dismissMonitor = new PopupDismissMonitor(new WindowInteropHelper(this).Handle, (_, target) => AutoClose(target)); }
        catch (Exception ex) { App.Current.Log(ex); }
    }
    public void RefreshTheme() { if (IsVisible) ApplyTheme(); }
    private bool ApplyTheme()
    {
        var dark = Theme.ApplyTo(this, settings.Appearance);
        if (backdrop) WindowPlacement.SetDarkFrame(this, dark);
        return dark;
    }
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
        var dark = ApplyTheme();
        Title = folder.Name;
        title.Content = new TextBlock { Text = folder.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
        title.ToolTip = currentId == RootFolderId ? "Нажмите на название, чтобы закрыть папку · Esc" : "Нажмите на название, чтобы вернуться · Esc — закрыть";
        tiles.Children.Clear();
        var (tileWidth, tileHeight) = FolderGrid.TileSize(settings, fromDesktop);
        var columns = FolderGrid.Columns(settings, folder.Children.Count);
        tiles.Width = columns * tileWidth;
        scroll.MaxHeight = FolderGrid.MaxRows * tileHeight;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var item in folder.Children)
        {
            var button = FolderGrid.Tile(item, settings, fromDesktop, pixelsPerDip, dark);
            button.Click += async (_, _) =>
            {
                if (item.IsFolder) { currentId = item.Id; Render(); WindowPlacement.FitAtCurrentPosition(this); return; }
                try
                {
                    var close = settings.CloseAfterLaunch;
                    await App.Current.LaunchAsync(item);
                    if (close) Hide();
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
                catch (Exception ex) { App.Current.Report(ex); }
            };
            tiles.Children.Add(button);
        }
        // Size the window to its content: no empty margins around a short title or a few icons.
        // Computed from text metrics, because a theme switch has just discarded the visual tree.
        var text = new FormattedText(folder.Name, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, title.FontWeight, FontStretches.Normal), title.FontSize, Brushes.Black, pixelsPerDip);
        var titleWidth = Math.Min(520, text.WidthIncludingTrailingWhitespace) + title.Padding.Left + title.Padding.Right + 2;
        var titleHeight = Math.Ceiling(text.Height) + title.Padding.Top + title.Padding.Bottom + title.Margin.Bottom;
        var rows = Math.Ceiling(folder.Children.Count / (double)columns);
        var gridWidth = tiles.Width + (rows > FolderGrid.MaxRows ? SystemParameters.VerticalScrollBarWidth : 0);
        Width = Math.Ceiling(Math.Max(gridWidth, titleWidth) + root.Margin.Left + root.Margin.Right);
        Height = Math.Ceiling(titleHeight + Math.Min(FolderGrid.MaxRows, Math.Max(1, rows)) * tileHeight + root.Margin.Top + root.Margin.Bottom);
        RefreshDismissMonitor();
    }
}
