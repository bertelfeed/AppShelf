using System.Text.Json;

namespace AppShelf.Core;

public sealed class ConfigurationStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "shelf.json");
    public string BackupPath => FilePath + ".bak";
    public string? RecoveryMessage { get; private set; }
    private bool recoveredFromBackup;
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 128 };

    public ShelfConfiguration Load()
    {
        if (!File.Exists(FilePath)) return new();
        try { return Read(FilePath); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!File.Exists(BackupPath)) throw new InvalidDataException("Настройки повреждены. Исходный файл сохранён: " + FilePath, ex);
            var recovered = Read(BackupPath);
            // Preserve the damaged primary before the next successful save replaces it.
            File.Copy(FilePath, Path.Combine(DirectoryPath, $"shelf.corrupt-{DateTime.Now:yyyyMMdd-HHmmssfff}.json"));
            RecoveryMessage = "Настройки восстановлены из резервной копии. Повреждённый файл сохранён рядом.";
            recoveredFromBackup = true;
            return recovered;
        }
    }

    public static ShelfConfiguration Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Файл превышает 16 МБ.");
        var config = JsonSerializer.Deserialize<ShelfConfiguration>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Пустой файл настроек.");
        Migrate(config);
        ShelfTree.Validate(config);
        return config;
    }

    public static void Migrate(ShelfConfiguration config)
    {
        if (config.SchemaVersion != 1) return;
        // Validate before walking user-provided trees.
        config.SchemaVersion = 2;
        if (config.Items is null) throw new InvalidDataException("Некорректный список папок.");
        void Convert(IEnumerable<ShelfItem> items, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Превышена глубина вложенности.");
            foreach (var item in items)
            {
                if (item is null || item.Children is null) throw new InvalidDataException("Некорректный элемент.");
                item.Settings = new FolderSettings
                {
                    Appearance = config.Appearance,
                    Columns = config.Columns ?? 5,
                    CloseAfterLaunch = config.CloseAfterLaunch ?? true,
                    ShowLabels = false
                };
                Convert(item.Children, depth + 1);
            }
        }
        Convert(config.Items, 0);
        config.Columns = null; config.CloseAfterLaunch = null; config.ShowLabels = null;
    }

    public void Save(ShelfConfiguration config)
    {
        ShelfTree.Validate(config);
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, config, JsonOptions);
                stream.Flush(true);
            }
            // OneDrive, indexers and antivirus open the file for a moment after each change; retry briefly
            // instead of failing the user's edit.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, recoveredFromBackup ? null : BackupPath, true);
                    else File.Move(temporary, FilePath);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 8 && File.Exists(temporary))
                { Thread.Sleep(40 * attempt); }
            }
            recoveredFromBackup = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ShelfConfiguration Clone(ShelfConfiguration config) =>
        JsonSerializer.Deserialize<ShelfConfiguration>(JsonSerializer.Serialize(config, JsonOptions), JsonOptions)!;

    public static List<ShelfItem> CopyItems(IEnumerable<ShelfItem> items)
    {
        var copy = Clone(new ShelfConfiguration { Items = items.ToList() }).Items;
        foreach (var item in ShelfTree.Walk(copy)) item.Id = Guid.NewGuid();
        return copy;
    }
}
