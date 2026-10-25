using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Security.Cryptography;

namespace AppShelf.App;

public static class FolderIconWriter
{
    public sealed record Artwork(string Path, BitmapSource Preview);
    public static async Task<Artwork> PrepareAsync(ShelfItem folder)
    {
        var previews = new List<ImageSource?>();
        var tasks = FolderPreview.Applications(folder).Select(child => App.Current.Session.Icons.GetAsync(
            string.IsNullOrWhiteSpace(child.IconPath) ? child.Target : child.IconPath)).ToList();
        foreach (var task in tasks)
        {
            try { previews.Add(await task.WaitAsync(TimeSpan.FromSeconds(5))); }
            catch (TimeoutException)
            {
                previews.Add(null);
                _ = task.ContinueWith(_ => App.Current.Dispatcher.InvokeAsync(() => App.Current.Publisher?.RequestRefresh()), TaskScheduler.Default);
            }
        }
        var image = FolderArtwork.Render(previews, 256);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream(); encoder.Save(png); var bytes = png.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        var path = System.IO.Path.Combine(App.Current.Session.Store.DirectoryPath, "folder-icons", $"{folder.Id:N}-{hash}.ico");
        if (!File.Exists(path)) FolderArtwork.WriteIcon(path, previews);
        return new(path, image);
    }
}
