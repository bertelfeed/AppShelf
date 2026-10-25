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
    public static int PageSize(FolderSettings settings) => settings.Columns * settings.Rows;
    public static int Pages(FolderSettings settings, int count) => Math.Max(1, (int)Math.Ceiling(count / (double)PageSize(settings)));
    public const double DotsWidth = 12;

    /// <summary>Page dots down the right side, like the Start menu; shown only when there is more than one page.</summary>
    public static StackPanel Dots(int pages, int current, Action<int>? select)
    {
        var dots = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center, Width = DotsWidth };
        if (pages < 2) { dots.Width = 0; return dots; }
        for (var i = 0; i < pages; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = i == current ? 6 : 5, Height = i == current ? 6 : 5, Opacity = i == current ? 1 : .45,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, i == current ? "TextBrush" : "MutedBrush");
            var target = new Border { Width = DotsWidth, Height = 12, Background = Brushes.Transparent, Child = dot };
            System.Windows.Automation.AutomationProperties.SetName(target, L.F("Page0Of1", i + 1, pages));
            if (select is not null)
            {
                var index = i;
                target.Cursor = Cursors.Hand;
                target.MouseLeftButtonUp += (_, e) => { select(index); e.Handled = true; };
            }
            dots.Children.Add(target);
        }
        return dots;
    }

    /// <summary>
    /// The gap nearest to the pointer on the current page: 0 is before the first tile, count after the last.
    /// Below the last row means "at the end".
    /// </summary>
    public static int InsertionIndex(Point pointer, double tileWidth, double tileHeight, int columns, int count)
    {
        if (count == 0) return 0;
        var rows = (count + columns - 1) / columns;
        if (pointer.Y >= rows * tileHeight) return count;
        var row = Math.Clamp((int)Math.Floor(pointer.Y / tileHeight), 0, rows - 1);
        var column = Math.Clamp((int)Math.Round(pointer.X / tileWidth), 0, columns);
        return Math.Min(row * columns + column, count);
    }

    /// <summary>The tile under the pointer, or -1 between and outside tiles.</summary>
    public static int TileAt(Point pointer, double tileWidth, double tileHeight, int columns, int count)
    {
        if (pointer.X < 0 || pointer.Y < 0 || pointer.X >= columns * tileWidth) return -1;
        var index = (int)(pointer.Y / tileHeight) * columns + (int)(pointer.X / tileWidth);
        return index < count ? index : -1;
    }

    /// <summary>Over the middle of a tile a drop goes into that nested folder; near its edges it goes next to it.</summary>
    public static bool IsOverCenter(Point pointer, double tileWidth, double tileHeight)
    {
        var x = pointer.X % tileWidth; var y = pointer.Y % tileHeight;
        return x > tileWidth * .25 && x < tileWidth * .75 && y > tileHeight * .2 && y < tileHeight * .8;
    }

    /// <summary>Top-left of the insertion bar for a gap, in tile-grid coordinates.</summary>
    public static Point GapPosition(int index, double tileWidth, double tileHeight, int columns, int count)
    {
        if (count == 0) return new(0, 0);
        if (index < count) return new(index % columns * tileWidth, index / columns * tileHeight);
        var last = count - 1;
        return new((last % columns + 1) * tileWidth, last / columns * tileHeight);
    }

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
            Content = content, ToolTip = model.Missing ? item.Name + L.Get("NotFound") : item.Name, DataContext = model, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
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
    private readonly TranslateTransform pageShift = new();
    private readonly ContentControl dots = new() { Focusable = false };
    private int page, wheel;
    private DateTime lastFlip;
    public int Page => page;
    public int PageCount { get; private set; } = 1;
    // Drag and drop: a thin bar shows the gap where an app lands; over a nested folder the folder lights up.
    private const string ItemFormat = "AppShelf.Item";
    private const double BackArrowGap = 5;
    private readonly Canvas dropLayer = new() { IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Rectangle dropBar = new() { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Visibility = Visibility.Collapsed };
    private readonly System.Windows.Shapes.Rectangle dropFolder = new() { RadiusX = 6, RadiusY = 6, StrokeThickness = 1.5, Visibility = Visibility.Collapsed };
    private readonly System.Windows.Threading.DispatcherTimer edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private int edgeDirection;
    private List<ShelfItem> pageItems = [];
    private double tileWidth, tileHeight;
    private int gridColumns = 1, shownRows = 1;
    private Point pressPoint;
    private ShelfItem? pressed;
    private DateTime ignoreClicksUntil;
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
        Title = L.Get("AppShelfFolder"); ShowInTaskbar = false; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        // Icons are the menu; the name is a small quiet caption in the corner.
        root = new Grid { Margin = new Thickness(5, 3, 5, 5), RenderTransform = shift };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetRow(dots, 1); Grid.SetColumn(dots, 1); root.Children.Add(dots);
        title = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 0, 1),
            MinWidth = 0, MinHeight = 0, FontSize = 11 };
        title.SetResourceReference(ForegroundProperty, "MutedBrush");
        title.Click += (_, _) => { if (currentId == RootFolderId) Hide(); else Back(); };
        Grid.SetColumnSpan(title, 2);
        root.Children.Add(title);
        tiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, RenderTransform = pageShift };
        KeyboardNavigation.SetDirectionalNavigation(tiles, KeyboardNavigationMode.Cycle);
        scroll = new ScrollViewer { Content = tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        dropBar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, SystemColors.AccentColorBrushKey);
        dropFolder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, SystemColors.AccentColorBrushKey);
        dropFolder.Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        dropLayer.Children.Add(dropFolder); dropLayer.Children.Add(dropBar);
        Grid.SetRow(dropLayer, 1); Panel.SetZIndex(dropLayer, 1); root.Children.Add(dropLayer);
        Content = root;
        AllowDrop = true;
        DragEnter += (_, e) => DragUpdate(e);
        DragOver += (_, e) => DragUpdate(e);
        DragLeave += (_, _) => DragFinish();
        Drop += (_, e) => DropHere(e);
        edgeTimer.Tick += (_, _) => { if (edgeDirection != 0) ShowPage(page + edgeDirection); };
        SourceInitialized += (_, _) =>
        {
            backdrop = WindowPlacement.StylePopup(this, Theme.IsDarkFor(settings.Appearance));
            if (backdrop) Background = Brushes.Transparent;
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            // While a button is held the monitor decides on release, so a drag from the desktop can reach the menu.
            if (armed && IsVisible && !IsActive && settings.CloseOnFocusLost && Mouse.Captured is null && !WindowPlacement.MouseButtonDown())
                AutoClose(WindowPlacement.ForegroundWindow());
        }));
        IsVisibleChanged += (_, _) => { if (!IsVisible) { armed = false; dismissMonitor?.Dispose(); dismissMonitor = null; } };
        Closed += (_, _) => { dismissMonitor?.Dispose(); dismissMonitor = null; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            if (e.Key == Key.Back || e.SystemKey == Key.Left) { Back(); e.Handled = true; }
            if (e.Key is Key.PageDown or Key.PageUp or Key.Home or Key.End && PageCount > 1)
            {
                ShowPage(e.Key switch { Key.PageDown => page + 1, Key.PageUp => page - 1, Key.Home => 0, _ => PageCount - 1 });
                e.Handled = true;
            }
        };
        // One wheel notch turns one page; small touchpad deltas add up to a notch.
        PreviewMouseWheel += (_, e) =>
        {
            if (PageCount < 2) return;
            e.Handled = true;
            if (Math.Sign(e.Delta) != Math.Sign(wheel)) wheel = 0;
            wheel += e.Delta;
            if (Math.Abs(wheel) < 120 || DateTime.UtcNow - lastFlip < TimeSpan.FromMilliseconds(180)) return;
            ShowPage(page + (wheel < 0 ? 1 : -1));
            wheel = 0;
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
        page = 0;
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
            DateTime.UtcNow - openedAt < TimeSpan.FromMilliseconds(WindowPlacement.DoubleClickTime + 300)) return;
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
        page = 0;
        Render();
        WindowPlacement.FitAtCurrentPosition(this);
    }

    private enum DragKind { None, Files, Item }
    private static DragKind KindOf(IDataObject data) =>
        data.GetDataPresent(ItemFormat) ? DragKind.Item : data.GetDataPresent(DataFormats.FileDrop) ? DragKind.Files : DragKind.None;
    private static Guid? DraggedId(IDataObject data) => data.GetData(ItemFormat) is string text && Guid.TryParse(text, out var id) ? id : null;

    /// <summary>Where the drag would land: a gap on this page, or inside the nested folder under the pointer.</summary>
    private (int Index, ShelfItem? Into) DropTarget(Point pointer, Guid? dragged)
    {
        var tile = FolderGrid.TileAt(pointer, tileWidth, tileHeight, gridColumns, pageItems.Count);
        if (tile >= 0 && pageItems[tile] is { IsFolder: true } folder && folder.Id != dragged && FolderGrid.IsOverCenter(pointer, tileWidth, tileHeight))
            return (tile, folder);
        return (FolderGrid.InsertionIndex(pointer, tileWidth, tileHeight, gridColumns, pageItems.Count), null);
    }

    private void DragUpdate(DragEventArgs e)
    {
        e.Handled = true;
        var kind = KindOf(e.Data);
        if (kind == DragKind.None || !IsVisible) { e.Effects = DragDropEffects.None; DragFinish(); return; }
        // A drag from the desktop began with a press outside the menu; the menu stays open for the drop.
        dismissMonitor?.CancelPendingClose();
        // Never Move for files: Explorer would then delete the desktop shortcut. AppShelf keeps its own copy.
        e.Effects = kind == DragKind.Files ? DragDropEffects.Copy : DragDropEffects.Move;
        // Hovering a page dot opens that page; resting at the top or bottom edge turns pages, as in the Start menu.
        if (dots.Content is StackPanel pageDots && PageCount > 1)
        {
            var onDots = e.GetPosition(pageDots);
            if (onDots.X >= 0 && onDots.X <= pageDots.ActualWidth && onDots.Y >= 0 && onDots.Y < pageDots.ActualHeight)
            { ShowPage((int)(onDots.Y / 12)); HideDropMarks(); return; }
        }
        var pointer = e.GetPosition(tiles);
        var bottom = shownRows * tileHeight;
        var direction = PageCount < 2 ? 0 : pointer.Y < 10 && page > 0 ? -1 : pointer.Y > bottom - 10 && page < PageCount - 1 ? 1 : 0;
        if (direction != edgeDirection) { edgeDirection = direction; edgeTimer.Stop(); if (direction != 0) edgeTimer.Start(); }
        var (index, into) = DropTarget(pointer, DraggedId(e.Data));
        var origin = tiles.TranslatePoint(new Point(0, 0), dropLayer);
        if (into is not null)
        {
            dropBar.Visibility = Visibility.Collapsed;
            dropFolder.Width = tileWidth - 2; dropFolder.Height = tileHeight - 2;
            Canvas.SetLeft(dropFolder, origin.X + index % gridColumns * tileWidth + 1);
            Canvas.SetTop(dropFolder, origin.Y + index / gridColumns * tileHeight + 1);
            dropFolder.Visibility = Visibility.Visible;
            return;
        }
        dropFolder.Visibility = Visibility.Collapsed;
        var gap = FolderGrid.GapPosition(index, tileWidth, tileHeight, gridColumns, pageItems.Count);
        dropBar.Height = tileHeight * .7;
        Canvas.SetLeft(dropBar, origin.X + gap.X - dropBar.Width / 2);
        Canvas.SetTop(dropBar, origin.Y + gap.Y + tileHeight * .15);
        dropBar.Visibility = Visibility.Visible;
    }

    private void HideDropMarks() { dropBar.Visibility = Visibility.Collapsed; dropFolder.Visibility = Visibility.Collapsed; }
    private void DragFinish() { HideDropMarks(); edgeDirection = 0; edgeTimer.Stop(); }

    private void DropHere(DragEventArgs e)
    {
        e.Handled = true;
        var kind = KindOf(e.Data);
        var (index, into) = DropTarget(e.GetPosition(tiles), DraggedId(e.Data));
        DragFinish();
        dismissMonitor?.CancelPendingClose();
        var position = page * FolderGrid.PageSize(settings) + index;
        try
        {
            if (kind == DragKind.Files && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            { e.Effects = DragDropEffects.Copy; DropPaths(paths, position, into?.Id); }
            else if (kind == DragKind.Item && DraggedId(e.Data) is { } id)
            { e.Effects = DragDropEffects.Move; DropItem(id, position, into?.Id); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { App.Current.Report(ex); }
        // Take the focus back from Explorer, so the next click outside closes the menu as usual.
        if (IsVisible) { Activate(); WindowPlacement.ActivateFromDesktop(this); }
        WindowPlacement.FitAtCurrentPosition(this);
    }

    /// <summary>
    /// Adds dropped files at a position of the open folder (or at the end of a nested folder).
    /// An app that is already in that folder moves to the new place instead of appearing twice.
    /// </summary>
    public int DropPaths(IEnumerable<string> paths, int position, Guid? into = null)
    {
        var data = App.Current.Session.Store.DirectoryPath;
        var items = paths.Where(ShortcutRepair.Exists).Select(path => ShelfImport.FromPath(data, path)).OfType<ShelfItem>().ToList();
        if (items.Count == 0) return 0;
        var unused = new List<string>();
        App.Current.Session.Change(config =>
        {
            var container = ShelfTree.Find(config, into ?? currentId)?.Children ?? throw new InvalidOperationException(L.Get("TheFolderWasDeleted"));
            var at = into is null ? Math.Clamp(position, 0, container.Count) : container.Count;
            foreach (var item in items)
            {
                var existing = container.FirstOrDefault(x => !x.IsFolder && string.Equals(x.Origin, item.Origin, StringComparison.OrdinalIgnoreCase) && x.Origin.Length > 0);
                if (existing is null) { container.Insert(at++, item); continue; }
                if (item.Target != item.Origin) unused.Add(item.Target);
                var old = container.IndexOf(existing);
                container.RemoveAt(old); if (old < at) at--;
                container.Insert(at++, existing);
            }
        });
        foreach (var copy in unused) try { File.Delete(copy); } catch (IOException) { }
        return items.Count;
    }

    /// <summary>Moves an app or folder of this menu to a position, or into a nested folder.</summary>
    public void DropItem(Guid id, int position, Guid? into = null)
    {
        App.Current.Session.Change(config =>
        {
            if (into is { } folder) { if (folder != id) ShelfTree.Move(config, id, folder); return; }
            var container = ShelfTree.Find(config, currentId)?.Children ?? throw new InvalidOperationException(L.Get("TheFolderWasDeleted"));
            var before = position >= 0 && position < container.Count ? container[position].Id : (Guid?)null;
            if (before != id) ShelfTree.Move(config, id, currentId, before);
        });
    }

    /// <summary>Turns to another page with a short vertical slide in the direction of travel, as in the Start menu.</summary>
    public void ShowPage(int target)
    {
        target = Math.Clamp(target, 0, PageCount - 1);
        if (target == page) return;
        var direction = Math.Sign(target - page);
        page = target; lastFlip = DateTime.UtcNow;
        Render();
        pageShift.BeginAnimation(TranslateTransform.YProperty, null); tiles.BeginAnimation(OpacityProperty, null);
        if (!SystemParameters.ClientAreaAnimation) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        pageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(direction * 24, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        tiles.BeginAnimation(OpacityProperty, new DoubleAnimation(.2, 1, TimeSpan.FromMilliseconds(150)));
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
        var name = new TextBlock { Text = folder.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center };
        var nested = currentId != RootFolderId;
        if (nested)
        {
            // Inside a nested folder a small arrow makes it obvious that the caption leads back.
            var arrow = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, BackArrowGap, 0) };
            var caption = new StackPanel { Orientation = Orientation.Horizontal };
            caption.Children.Add(arrow); caption.Children.Add(name);
            title.Content = caption;
        }
        else title.Content = name;
        System.Windows.Automation.AutomationProperties.SetName(title, nested ? L.Get("Back") + folder.Name : folder.Name);
        title.ToolTip = currentId == RootFolderId ? L.Get("CloseTitleHelp") : L.Get("BackTitleHelp");
        tiles.Children.Clear();
        (tileWidth, tileHeight) = FolderGrid.TileSize(settings, fromDesktop);
        var columns = FolderGrid.Columns(settings, folder.Children.Count);
        tiles.Width = columns * tileWidth;
        // A page never grows past the row limit; the window keeps the size of a full page while paging.
        PageCount = FolderGrid.Pages(settings, folder.Children.Count);
        page = Math.Clamp(page, 0, PageCount - 1);
        var shown = PageCount > 1 ? settings.Rows : Math.Max(1, (int)Math.Ceiling(folder.Children.Count / (double)columns));
        scroll.MaxHeight = shown * tileHeight;
        dots.Content = FolderGrid.Dots(PageCount, page, ShowPage);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        pageItems = folder.Children.Skip(page * FolderGrid.PageSize(settings)).Take(FolderGrid.PageSize(settings)).ToList();
        gridColumns = columns; shownRows = shown;
        foreach (var item in pageItems)
        {
            var button = FolderGrid.Tile(item, settings, fromDesktop, pixelsPerDip, dark);
            // Dragging a tile moves it to another place or into a nested folder.
            button.PreviewMouseLeftButtonDown += (_, e) => { pressPoint = e.GetPosition(this); pressed = item; };
            button.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || pressed != item) return;
                var now = e.GetPosition(this);
                if (Math.Abs(now.X - pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(now.Y - pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = null;
                try { DragDrop.DoDragDrop(button, new DataObject(ItemFormat, item.Id.ToString()), DragDropEffects.Move); }
                finally { ignoreClicksUntil = DateTime.UtcNow.AddMilliseconds(250); DragFinish(); }
            };
            button.Click += async (_, _) =>
            {
                if (DateTime.UtcNow < ignoreClicksUntil) return;
                if (item.IsFolder) { currentId = item.Id; page = 0; Render(); WindowPlacement.FitAtCurrentPosition(this); return; }
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
        var titleWidth = Math.Min(520, text.WidthIncludingTrailingWhitespace) + title.Padding.Left + title.Padding.Right + 2 + (nested ? 10 + BackArrowGap : 0);
        var titleHeight = Math.Ceiling(text.Height) + title.Padding.Top + title.Padding.Bottom + title.Margin.Bottom;
        var dotsWidth = PageCount > 1 ? FolderGrid.DotsWidth : 0;
        Width = Math.Ceiling(Math.Max(tiles.Width + dotsWidth, titleWidth) + root.Margin.Left + root.Margin.Right);
        Height = Math.Ceiling(titleHeight + shown * tileHeight + root.Margin.Top + root.Margin.Bottom);
        RefreshDismissMonitor();
    }
}
