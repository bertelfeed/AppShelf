using System.Collections;
using System.Globalization;
using System.Resources;

namespace AppShelf.Core;

/// <summary>Shared messages for the UI and Shell errors. Stored user names are never translated.</summary>
public static class Localization
{
    private static readonly ResourceManager resources = new("AppShelf.Core.Resources.Strings", typeof(Localization).Assembly);
    private static CultureInfo culture = CultureInfo.GetCultureInfo(DefaultLanguage);
    public static string DefaultLanguage => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ru" ? "ru-RU" : "en-US";
    public static string Language => culture.Name;
    public static CultureInfo Culture => culture;
    public static IReadOnlyList<string> SupportedLanguages { get; } = Array.AsReadOnly(new[] { "en-US", "ru-RU" });
    public static string NormalizeLanguage(string? language) => language?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true ? "ru-RU" : "en-US";

    public static bool SetLanguage(string language)
    {
        var next = CultureInfo.GetCultureInfo(NormalizeLanguage(language));
        var changed = next.Name != culture.Name;
        culture = next;
        CultureInfo.DefaultThreadCurrentUICulture = next;
        CultureInfo.CurrentUICulture = next;
        return changed;
    }

    public static string Get(string key, string? language = null) => resources.GetString(key,
        language is null ? culture : CultureInfo.GetCultureInfo(NormalizeLanguage(language)))
        ?? throw new InvalidOperationException("Missing localization resource: " + key);
    public static string F(string key, params object?[] arguments) => string.Format(culture, Get(key), arguments);

    public static IReadOnlyDictionary<string, string> Strings(string? language = null)
    {
        var set = resources.GetResourceSet(CultureInfo.GetCultureInfo(NormalizeLanguage(language ?? Language)), true, true)!;
        return set.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
    }

    public static string Elements(int count)
    {
        var form = culture.TwoLetterISOLanguageName != "ru" ? (count == 1 ? "ItemsOne" : "ItemsMany")
            : count % 100 is >= 11 and <= 14 ? "ItemsMany"
            : (count % 10) switch { 1 => "ItemsOne", 2 or 3 or 4 => "ItemsFew", _ => "ItemsMany" };
        return F(form, count);
    }

    // These are canonical names in the existing artwork/MCP catalog, not user content.
    public static string CatalogLabel(string name, string? language = null)
    {
        var key = name switch
        {
            "Белый" => "White", "Бирюзовый" => "Teal", "Видео" => "Video",
            "Голубой" => "Cyan", "Графит" => "Graphite", "Документы" => "Documents",
            "Дом" => "Home", "Жёлтый" => "Yellow", "Защита" => "Security",
            "Зелёный" => "Green", "Игры" => "Games", "Идеи" => "Ideas",
            "Избранное" => "Favorites", "ИИ" => "AI", "Инструменты" => "Tools",
            "Интернет" => "Internet", "Код" => "Code", "Красный" => "Red",
            "Люди" => "People", "Магазин" => "Store", "Малиновый" => "Magenta",
            "Музыка" => "Music", "Настройки" => "Settings", "Облако" => "Cloud",
            "Оранжевый" => "Orange", "Папка" => "Folder", "Почта" => "Mail",
            "Работа" => "Work", "Рисование" => "Drawing", "Сердце" => "Heart",
            "Серый" => "Gray", "Синий" => "Blue", "Сообщения" => "Messages",
            "Учёба" => "Study", "Фиолетовый" => "Purple", "Фото" => "Photos",
            _ => null
        };
        return key is null ? name : Get(key, language);
    }

    /// <summary>Accept both English and legacy Russian catalog names, independently of the selected UI language.</summary>
    public static bool CatalogMatches(string canonical, string name) =>
        string.Equals(canonical, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CatalogLabel(canonical, "en-US"), name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CatalogLabel(canonical, "ru-RU"), name, StringComparison.OrdinalIgnoreCase);
}
