using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Security.Cryptography;

namespace AppShelf.App;

public static class FolderIconWriter
{
    public sealed record Artwork(string Path, BitmapSource Preview);
    public static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".ico"];

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
        var look = await LookAsync(folder.Settings);
        var image = FolderArtwork.Render(previews, 256, look: look);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream(); encoder.Save(png); var bytes = png.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        var path = System.IO.Path.Combine(App.Current.Session.Store.DirectoryPath, "folder-icons", $"{folder.Id:N}-{hash}.ico");
        if (!File.Exists(path)) FolderArtwork.WriteIcon(path, previews, look);
        return new(path, image);
    }

    /// <summary>The folder's custom look, or null for the standard app preview on the standard plate.</summary>
    public static async Task<FolderLook?> LookAsync(FolderSettings settings)
    {
        var plate = FolderLook.ParseColor(settings.FaceColor);
        if (settings.Face == FolderFace.Image && PicturePath(settings.FaceImage) is { } picture &&
            await App.Current.Session.Icons.GetAsync(picture) is { } image)
            return new(plate, null, image);
        if (settings.Face == FolderFace.Symbol && settings.FaceSymbol.Length > 0) return new(plate, settings.FaceSymbol);
        return plate is null ? null : new(plate);
    }

    /// <summary>Stored pictures are relative to the profile, so a portable copy keeps them.</summary>
    public static string? PicturePath(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var path = System.IO.Path.IsPathRooted(stored) ? stored : System.IO.Path.Combine(App.Current.Session.Store.DirectoryPath, stored);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Copies a chosen picture into the profile and returns the value to store in FaceImage.</summary>
    public static string ImportPicture(Guid folderId, string source)
    {
        var info = new FileInfo(source);
        var extension = info.Extension.ToLowerInvariant();
        if (!PictureExtensions.Contains(extension)) throw new InvalidOperationException("Поддерживаются PNG, JPG, BMP и ICO.");
        if (info.Length > 20 * 1024 * 1024) throw new InvalidOperationException("Картинка больше 20 МБ.");
        var bytes = File.ReadAllBytes(source);
        var name = $"{folderId:N}-{Convert.ToHexString(SHA256.HashData(bytes))[..16]}{extension}";
        var directory = System.IO.Path.Combine(App.Current.Session.Store.DirectoryPath, "folder-art");
        Directory.CreateDirectory(directory);
        var target = System.IO.Path.Combine(directory, name);
        if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
        return System.IO.Path.Combine("folder-art", name);
    }
}
