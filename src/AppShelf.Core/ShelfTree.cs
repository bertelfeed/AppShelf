namespace AppShelf.Core;

public static class ShelfTree
{
    public static IEnumerable<ShelfItem> Walk(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Walk(item.Children)) yield return child;
        }
    }

    public static ShelfItem? Find(ShelfConfiguration config, Guid id) => Walk(config.Items).FirstOrDefault(x => x.Id == id);
    public static List<ShelfItem> Container(ShelfConfiguration config, Guid? folderId) =>
        folderId is null ? config.Items : (Find(config, folderId.Value) is { IsFolder: true } folder
            ? folder.Children : throw new InvalidOperationException("Папка не найдена."));

    public static List<ShelfItem>? Parent(ShelfConfiguration config, Guid id)
    {
        if (config.Items.Any(x => x.Id == id)) return config.Items;
        return Walk(config.Items).Where(x => x.IsFolder).Select(x => x.Children).FirstOrDefault(x => x.Any(y => y.Id == id));
    }

    public static List<ShelfItem> Breadcrumbs(ShelfConfiguration config, Guid? id)
    {
        var path = new List<ShelfItem>();
        if (id is null) return path;
        bool Search(IEnumerable<ShelfItem> items)
        {
            foreach (var item in items.Where(x => x.IsFolder))
            {
                path.Add(item);
                if (item.Id == id || Search(item.Children)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
        Search(config.Items);
        return path;
    }

    public static void Move(ShelfConfiguration config, Guid sourceId, Guid? destinationId, Guid? beforeId = null)
    {
        var source = Find(config, sourceId) ?? throw new InvalidOperationException("Элемент не найден.");
        if (sourceId == destinationId || (destinationId is { } d && Walk(source.Children).Any(x => x.Id == d)))
            throw new InvalidOperationException("Нельзя переместить папку в саму себя или в её вложенную папку.");
        var destination = Container(config, destinationId);
        if (beforeId == sourceId) return;
        Parent(config, sourceId)!.Remove(source);
        var index = beforeId is { } b ? destination.FindIndex(x => x.Id == b) : -1;
        if (index < 0) destination.Add(source); else destination.Insert(index, source);
    }

    public static void Validate(ShelfConfiguration config)
    {
        if (config.SchemaVersion != 2) throw new InvalidDataException("Эта версия формата пока не поддерживается.");
        if (config.Items is null || !Enum.IsDefined(config.Appearance) || config.Language is null)
            throw new InvalidDataException("Некорректные настройки.");
        var ids = new HashSet<Guid>();
        void Check(IEnumerable<ShelfItem> items, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Превышена глубина вложенности (32).");
            foreach (var item in items)
            {
                if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || ids.Count > 20000)
                    throw new InvalidDataException("Некорректные или повторяющиеся идентификаторы.");
                if (!Enum.IsDefined(item.Kind) || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200 ||
                    item.Children is null || item.Target is null || item.Arguments is null || item.IconPath is null || item.WorkingDirectory is null)
                    throw new InvalidDataException("Некорректный элемент.");
                if (!item.IsFolder && (item.Children.Count > 0 || string.IsNullOrWhiteSpace(item.Target)))
                    throw new InvalidDataException("У приложения должен быть адрес запуска и не может быть вложенных элементов.");
                if (item.Settings is null || item.Settings.Columns is < 3 or > 8 ||
                    item.Settings.IconSize is < 24 or > 80 || !Enum.IsDefined(item.Settings.Appearance) ||
                    item.Settings.DesktopIconScale is < 50 or > 200 || item.Settings.TaskbarIconScale is < 50 or > 200 ||
                    !Enum.IsDefined(item.Settings.Face) || item.Settings.FaceSymbol is not { Length: <= 8 } ||
                    item.Settings.FaceImage is not { Length: <= 400 } || !IsColor(item.Settings.FaceColor))
                    throw new InvalidDataException("Некорректные параметры папки.");
                Check(item.Children, depth + 1);
            }
        }
        Check(config.Items, 0);
    }

    /// <summary>Empty (automatic) or #RRGGBB.</summary>
    public static bool IsColor(string? value) =>
        value is { Length: 0 } || (value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit));
}
