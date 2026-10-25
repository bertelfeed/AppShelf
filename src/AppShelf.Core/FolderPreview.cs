namespace AppShelf.Core;

public static class FolderPreview
{
    // Show actual applications even when the first children are nested folders.
    public static IReadOnlyList<ShelfItem> Applications(ShelfItem folder) =>
        ShelfTree.Walk(folder.Children).Where(x => !x.IsFolder)
            .DistinctBy(x => (x.Target.ToUpperInvariant(), x.IconPath.ToUpperInvariant()))
            .Take(4).ToList();
}
