using System.Windows.Media;
using AppShelf.Core;

namespace AppShelf.Windows;

/// <summary>Custom look of a folder icon: a coloured plate, a symbol on it, or a picture instead of the app previews.</summary>
public sealed record FolderLook(Color? Plate = null, string? Symbol = null, ImageSource? Image = null, double Opacity = FolderLook.DefaultOpacity)
{
    /// <summary>The standard plate's opacity; coloured plates start from it too.</summary>
    public const double DefaultOpacity = .4;
    /// <summary>Plate behind a symbol when the user kept the automatic colour.</summary>
    public static readonly Color SymbolPlate = Color.FromRgb(0x00, 0x67, 0xC0);

    public static (string Name, string Hex)[] Palette => FolderFaceCatalog.Palette;
    public static (string Glyph, string Name)[] Symbols => FolderFaceCatalog.Symbols;

    public static Color? ParseColor(string? hex) =>
        string.IsNullOrEmpty(hex) ? null : (Color)ColorConverter.ConvertFromString(hex);
}
