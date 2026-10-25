using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShelf.Windows;

public static class FolderArtwork
{
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>
    /// Desktop and taskbar plate: translucent like Windows 11 Start folders, so it sits on any wallpaper.
    /// A faint dark rim keeps the edge visible on light backgrounds, a light one on dark backgrounds.
    /// </summary>
    private static readonly Brush ShellPlate = Frozen(new LinearGradientBrush(Color.FromArgb(0x7A, 236, 239, 244), Color.FromArgb(0x5C, 214, 219, 227), 90));
    private static readonly Pen ShellRim = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255))), 1));
    private static readonly Pen ShellShadow = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0))), 1));
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }

    private static readonly Pen ColorRim = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255))), 1));
    private static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static BitmapSource Render(IReadOnlyList<ImageSource?> apps, int pixels, Brush? background = null, Pen? border = null, FolderLook? look = null)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixels / 64d, pixels / 64d));
            if (look?.Image is { } picture) DrawPicture(dc, picture);
            else
            {
                var symbol = look?.Symbol is { Length: > 0 } glyph ? glyph : null;
                var plate = look?.Plate ?? (symbol is null ? null : FolderLook.SymbolPlate);
                DrawPlate(dc, background, border, plate);
                if (symbol is not null) DrawSymbol(dc, symbol, plate!.Value, pixels / 64d);
                else DrawApps(dc, apps, pixels);
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawPlate(DrawingContext dc, Brush? background, Pen? border, Color? color)
    {
        // Fill almost the entire icon box; the Shell sets the surrounding size/spacing.
        if (color is { } c)
        {
            var plate = new LinearGradientBrush(Lighten(c, .14), c, 90); plate.Freeze();
            dc.DrawRoundedRectangle(null, ShellShadow, new Rect(.5, .5, 63, 63), 12.5, 12.5);
            dc.DrawRoundedRectangle(plate, ColorRim, new Rect(1.5, 1.5, 61, 61), 11.5, 11.5);
        }
        else if (background is null)
        {
            dc.DrawRoundedRectangle(null, ShellShadow, new Rect(.5, .5, 63, 63), 12.5, 12.5);
            dc.DrawRoundedRectangle(ShellPlate, ShellRim, new Rect(1.5, 1.5, 61, 61), 11.5, 11.5);
        }
        else dc.DrawRoundedRectangle(background, border ?? new Pen(new SolidColorBrush(Color.FromArgb(100, 160, 166, 177)), .7),
            new Rect(1, 1, 62, 62), 12, 12);
    }

    /// <summary>The user's picture covers the whole rounded icon, cropped to fill like a wallpaper.</summary>
    private static void DrawPicture(DrawingContext dc, ImageSource picture)
    {
        var bounds = new Rect(1, 1, 62, 62);
        var scale = Math.Max(bounds.Width / Math.Max(1, picture.Width), bounds.Height / Math.Max(1, picture.Height));
        var size = new Size(picture.Width * scale, picture.Height * scale);
        dc.PushClip(new RectangleGeometry(bounds, 12, 12));
        dc.DrawImage(picture, new Rect(32 - size.Width / 2, 32 - size.Height / 2, size.Width, size.Height));
        dc.Pop();
        dc.DrawRoundedRectangle(null, ShellShadow, new Rect(.5, .5, 63, 63), 12.5, 12.5);
        dc.DrawRoundedRectangle(null, ColorRim, new Rect(1.5, 1.5, 61, 61), 11.5, 11.5);
    }

    private static void DrawSymbol(DrawingContext dc, string symbol, Color plate, double pixelsPerUnit)
    {
        var ink = Luminance(plate) > .6 ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        var text = new FormattedText(symbol, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(SymbolFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 32, Brushes.Black, Math.Max(.1, pixelsPerUnit));
        var geometry = text.BuildGeometry(new Point(0, 0));
        var box = geometry.Bounds;
        if (box.IsEmpty || box.Width <= 0) return;
        // Center the glyph's real outline, not its line box, and fit it into the middle half of the icon.
        var fit = Math.Min(30 / box.Width, 30 / box.Height);
        var transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-box.X - box.Width / 2, -box.Y - box.Height / 2));
        transform.Children.Add(new ScaleTransform(fit, fit));
        transform.Children.Add(new TranslateTransform(32, 32));
        geometry.Transform = transform;
        dc.DrawGeometry(new SolidColorBrush(ink), null, geometry);
    }

    private static void DrawApps(DrawingContext dc, IReadOnlyList<ImageSource?> apps, int pixels)
    {
        var count = Math.Min(apps.Count, pixels <= 24 ? 2 : 4);
        for (var i = 0; i < count; i++)
        {
            var rect = count switch
            {
                1 => new Rect(11, 11, 42, 42),
                2 => new Rect(6 + i * 20, 6 + i * 20, 32, 32),
                _ => new Rect(6 + i % 2 * 28, 6 + i / 2 * 28, 24, 24)
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
    }

    public static Color Lighten(Color color, double amount) => Color.FromArgb(color.A,
        (byte)(color.R + (255 - color.R) * amount), (byte)(color.G + (255 - color.G) * amount), (byte)(color.B + (255 - color.B) * amount));
    public static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;

    public static void WriteIcon(string path, IReadOnlyList<ImageSource?> apps, FolderLook? look = null)
    {
        var frames = new List<byte[]>();
        foreach (var size in Sizes)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(apps, size, look: look)));
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
