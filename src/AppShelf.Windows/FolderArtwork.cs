using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShelf.Windows;

public static class FolderArtwork
{
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    public static BitmapSource Render(IReadOnlyList<ImageSource?> apps, int pixels, Brush? background = null)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixels / 64d, pixels / 64d));
            // Fill almost the entire icon box; the Shell sets the surrounding size/spacing.
            dc.DrawRoundedRectangle(background ?? new SolidColorBrush(Color.FromRgb(54, 59, 67)),
                new Pen(new SolidColorBrush(Color.FromArgb(100, 160, 166, 177)), .7),
                new Rect(1, 1, 62, 62), 12, 12);
            var count = Math.Min(apps.Count, pixels <= 24 ? 2 : 4);
            for (var i = 0; i < count; i++)
            {
                var rect = count switch
                {
                    1 => new Rect(11, 11, 42, 42),
                    2 => new Rect(6 + i * 20, 6 + i * 20, 32, 32),
                    _ => new Rect(7 + i % 2 * 27, 7 + i / 2 * 27, 23, 23)
                };
                if (apps[i] is { } image) dc.DrawImage(image, rect);
                else dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(116, 159, 216)), null, rect, 4, 4);
            }
            if (count == 0)
            {
                // Only truly empty folders use a generic folder glyph.
                var geometry = Geometry.Parse("M14,23 L14,18 Q14,15 18,15 L28,15 L33,21 L47,21 Q50,21 50,25 L50,46 Q50,49 47,49 L18,49 Q14,49 14,45 Z");
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(190, 198, 210)), 2.5), geometry);
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    public static void WriteIcon(string path, IReadOnlyList<ImageSource?> apps)
    {
        var frames = new List<byte[]>();
        foreach (var size in Sizes)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(apps, size)));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            frames.Add(buffer.ToArray());
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Count);
                var offset = 6 + 16 * frames.Count;
                for (var i = 0; i < frames.Count; i++)
                {
                    writer.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i]));
                    writer.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i]));
                    writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(frames[i].Length); writer.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var frame in frames) writer.Write(frame);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
