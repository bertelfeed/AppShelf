using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShelf.App;

public sealed class TileModel : INotifyPropertyChanged
{
    public ShelfItem Item { get; }
    public string Name => Item.Name;
    public string Description => Item.IsFolder ? Russian.Elements(Item.Children.Count) + " · папка" : Item.Target;
    public double IconSize => Item.IsFolder ? 58 : 40;
    public bool ShowLabel { get; }
    private readonly int pixels;
    private readonly bool? menuDark;
    private ImageSource? icon;
    public ImageSource? Icon { get => icon; private set { icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }

    /// <param name="pixels">Device size of folder artwork; the editor uses 128.</param>
    /// <param name="menuDark">Theme of the folder menu showing the tile; null draws the editor's opaque folder plate.</param>
    public TileModel(ShelfItem item, bool showLabels = true, int pixels = 128, bool? menuDark = null)
    {
        Item = item; ShowLabel = showLabels; this.pixels = Math.Clamp(pixels, 16, 512); this.menuDark = menuDark;
        Icon = item.IsFolder ? FolderImage([]) : Placeholder;
        _ = LoadAsync();
    }
    private async Task LoadAsync()
    {
        try
        {
            var icons = App.Current.Session.Icons;
            if (!Item.IsFolder)
                Icon = await icons.GetAsync(string.IsNullOrWhiteSpace(Item.IconPath) ? Item.Target : Item.IconPath) ?? Icon;
            else
            {
                var previews = new List<ImageSource?>();
                foreach (var child in FolderPreview.Applications(Item))
                    previews.Add(await icons.GetAsync(string.IsNullOrWhiteSpace(child.IconPath) ? child.Target : child.IconPath));
                Icon = FolderImage(previews, await FolderIconWriter.LookAsync(Item.Settings));
            }
        }
        catch { /* A missing icon must never prevent access to an application. */ }
    }
    private ImageSource FolderImage(IReadOnlyList<ImageSource?> previews, FolderLook? look = null)
    {
        if (menuDark is not { } dark) return FolderArtwork.Render(previews, pixels, (Brush)Application.Current.Resources["FolderBrush"], look: look);
        var (fill, border) = Theme.FolderPlate(dark);
        return FolderArtwork.Render(previews, pixels, fill, border, look);
    }

    /// <summary>Shown until the real application icon has been extracted.</summary>
    private static ImageSource Placeholder { get; } = CreatePlaceholder();
    private static ImageSource CreatePlaceholder()
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 103, 192)), null, new Rect(2, 2, 60, 60), 12, 12);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(20, 20, 24, 24), 5, 5);
        }
        var bitmap = new RenderTargetBitmap(128, 128, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
