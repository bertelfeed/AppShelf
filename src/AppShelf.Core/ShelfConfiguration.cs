using System.Text.Json.Serialization;

namespace AppShelf.Core;

public enum ItemKind { Folder, Application }
public enum Appearance { System, Light, Dark }

public sealed class ShelfConfiguration
{
    public int SchemaVersion { get; set; } = 2;
    public Appearance Appearance { get; set; }
    public string Language { get; set; } = "ru-RU";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Columns { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CloseAfterLaunch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowLabels { get; set; }
    public List<ShelfItem> Items { get; set; } = [];
}

public sealed class ShelfItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ItemKind Kind { get; set; }
    public string Name { get; set; } = "Новая папка";
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string IconPath { get; set; } = "";
    public bool RunAsAdministrator { get; set; }
    public List<ShelfItem> Children { get; set; } = [];
    public FolderSettings Settings { get; set; } = new();
    [JsonIgnore] public bool IsFolder => Kind == ItemKind.Folder;

    public static ShelfItem Folder(string name) => new() { Kind = ItemKind.Folder, Name = name };
    public static ShelfItem Application(string name, string target) => new()
        { Kind = ItemKind.Application, Name = name, Target = target };
}

public sealed class FolderSettings
{
    public Appearance Appearance { get; set; } = Appearance.System;
    public int Columns { get; set; } = 5;
    public int IconSize { get; set; } = 40;
    public int DesktopIconScale { get; set; } = 100;
    public int TaskbarIconScale { get; set; } = 80;
    public bool ShowLabels { get; set; }
    public bool CloseAfterLaunch { get; set; } = true;
    public bool CloseOnFocusLost { get; set; } = true;
    public bool ShowOnTaskbar { get; set; }
    public double IconSizeFor(bool fromDesktop) => IconSize * (fromDesktop ? DesktopIconScale : TaskbarIconScale) / 100d;
}
