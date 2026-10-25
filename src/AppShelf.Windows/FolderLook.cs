using System.Windows.Media;

namespace AppShelf.Windows;

/// <summary>Custom look of a folder icon: a coloured plate, a symbol on it, or a picture instead of the app previews.</summary>
public sealed record FolderLook(Color? Plate = null, string? Symbol = null, ImageSource? Image = null)
{
    /// <summary>Plate behind a symbol when the user kept the automatic colour.</summary>
    public static readonly Color SymbolPlate = Color.FromRgb(0x00, 0x67, 0xC0);

    public static readonly (string Name, string Hex)[] Palette =
    [
        ("Синий", "#0067C0"), ("Голубой", "#0099BC"), ("Бирюзовый", "#00B294"), ("Зелёный", "#10893E"),
        ("Жёлтый", "#FFB900"), ("Оранжевый", "#F7630C"), ("Красный", "#E81123"), ("Малиновый", "#E3008C"),
        ("Фиолетовый", "#8764B8"), ("Серый", "#69797E"), ("Графит", "#3B3B3B"), ("Белый", "#F3F3F3")
    ];

    /// <summary>Segoe Fluent Icons glyphs offered for folders. WPF draws emoji in one colour, so symbols replace them.</summary>
    public static readonly (string Glyph, string Name)[] Symbols =
    [
        ("", "Игры"), ("", "Код"), ("", "ИИ"), ("", "Сообщения"), ("", "Почта"), ("", "Интернет"),
        ("", "Музыка"), ("", "Видео"), ("", "Фото"), ("", "Рисование"), ("", "Работа"), ("", "Инструменты"),
        ("", "Настройки"), ("", "Документы"), ("", "Папка"), ("", "Магазин"), ("", "Облако"), ("", "Идеи"),
        ("", "Люди"), ("", "Дом"), ("", "Избранное"), ("", "Сердце"), ("", "Учёба"), ("", "Защита")
    ];

    public static Color? ParseColor(string? hex) =>
        string.IsNullOrEmpty(hex) ? null : (Color)ColorConverter.ConvertFromString(hex);
}
