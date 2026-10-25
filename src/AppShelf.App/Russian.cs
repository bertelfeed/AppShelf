namespace AppShelf.App;
public static class Russian
{
    public static string Elements(int count)
    {
        var word = count % 100 is >= 11 and <= 14 ? "элементов"
            : (count % 10) switch { 1 => "элемент", 2 or 3 or 4 => "элемента", _ => "элементов" };
        return $"{count} {word}";
    }
}
