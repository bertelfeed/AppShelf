namespace AppShelf.App;

public sealed class ShelfSession
{
    public ConfigurationStore Store { get; }
    public ShelfConfiguration Configuration { get; private set; }
    public IconService Icons { get; }
    public event Action? Changed;
    private readonly List<ShelfConfiguration> undo = [];
    public bool CanUndo => undo.Count > 0;

    public ShelfSession(string directory)
    {
        Store = new(directory);
        Configuration = Store.Load();
        Icons = new(directory);
    }

    public void Change(Action<ShelfConfiguration> change)
    {
        var next = ConfigurationStore.Clone(Configuration);
        change(next);
        Store.Save(next);
        undo.Add(Configuration);
        if (undo.Count > 30) undo.RemoveAt(0);
        Configuration = next;
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var previous = undo[^1];
        Store.Save(previous);
        undo.RemoveAt(undo.Count - 1);
        Configuration = previous;
        Changed?.Invoke();
    }

    public void Import(string path)
    {
        var imported = ConfigurationStore.Read(path);
        Change(config => config.Items.AddRange(ConfigurationStore.CopyItems(imported.Items)));
    }
}
