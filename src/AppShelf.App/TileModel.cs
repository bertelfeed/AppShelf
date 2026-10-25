using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private ImageSource? icon;
    public ImageSource? Icon { get => icon; private set { icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
    public TileModel(ShelfItem item, bool showLabels = true) { Item = item; ShowLabel = showLabels; Icon = FolderImage([], item.IsFolder); _ = LoadAsync(); }
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
                Icon = FolderImage(previews, true);
            }
        }
        catch { /* A missing icon must never prevent access to an application. */ }
    }
    public static ImageSource FolderImage(IReadOnlyList<ImageSource?> previews, bool folder)
    {
        if (folder) return FolderArtwork.Render(previews, 128, (Brush)Application.Current.Resources["FolderBrush"]);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var fill = folder ? (Brush)Application.Current.Resources["FolderBrush"] : new SolidColorBrush(Color.FromRgb(0, 103, 192));
            dc.DrawRoundedRectangle(fill, null, new Rect(2, 2, 60, 60), 12, 12);
            if (folder)
            {
                for (var i = 0; i < 4; i++)
                {
                    var rect = new Rect(11 + i % 2 * 25, 11 + i / 2 * 25, 18, 18);
                    if (i < previews.Count && previews[i] is { } image) dc.DrawImage(image, rect);
                    else if (previews.Count == 0) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)), null, rect, 4, 4);
                }
            }
            else dc.DrawRoundedRectangle(Brushes.White, null, new Rect(20, 20, 24, 24), 5, 5);
        }
        var bitmap = new RenderTargetBitmap(128, 128, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
