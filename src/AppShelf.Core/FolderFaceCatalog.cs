namespace AppShelf.Core;

/// <summary>Colours and symbols offered for folder icons; shared by the settings dialog and the MCP tools.</summary>
public static class FolderFaceCatalog
{
    public static readonly (string Name, string Hex)[] Palette =
    [
        ("Синий", "#0067C0"), ("Голубой", "#0099BC"), ("Бирюзовый", "#00B294"), ("Зелёный", "#10893E"),
        ("Жёлтый", "#FFB900"), ("Оранжевый", "#F7630C"), ("Красный", "#E81123"), ("Малиновый", "#E3008C"),
        ("Фиолетовый", "#8764B8"), ("Серый", "#69797E"), ("Графит", "#3B3B3B"), ("Белый", "#F3F3F3")
    ];

    /// <summary>Segoe Fluent Icons glyphs. WPF draws emoji in one colour, so symbols replace them.</summary>
    public static readonly (string Glyph, string Name)[] Symbols =
    [
        ("", "Игры"), ("", "Код"), ("", "ИИ"), ("", "Сообщения"), ("", "Почта"), ("", "Интернет"),
        ("", "Музыка"), ("", "Видео"), ("", "Фото"), ("", "Рисование"), ("", "Работа"), ("", "Инструменты"),
        ("", "Настройки"), ("", "Документы"), ("", "Папка"), ("", "Магазин"), ("", "Облако"), ("", "Идеи"),
        ("", "Люди"), ("", "Дом"), ("", "Избранное"), ("", "Сердце"), ("", "Учёба"), ("", "Защита")
    ];
}
