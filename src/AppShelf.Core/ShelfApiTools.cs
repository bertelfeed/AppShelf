using System.Text.Json.Nodes;

namespace AppShelf.Core;

/// <summary>
/// MCP tool definitions. They live next to the model so the server can list them even before
/// the resident AppShelf is started; the app itself implements them (AppShelf.App.ShelfApi).
/// </summary>
public static class ShelfApiTools
{
    public const string Instructions =
        "AppShelf groups Windows apps into folders that open as compact menus from desktop icons and taskbar buttons. " +
        "Typical request (often dictated): 'put Telegram and Discord into Chat, games into Games, clean up the desktop'. " +
        "Workflow: call get_desktop and get_folders first, then make the whole change with one organize call. " +
        "Refer to desktop items by the exact 'name' from get_desktop; apps that are not on the desktop can be found with find_installed_apps. " +
        "Dictated names often arrive transliterated ('Обсидиан' for Obsidian, 'Дискорд' for Discord): prefer the matching exact name from get_desktop; " +
        "organize also accepts an unambiguous sound-alike and reports it in recognized_names, otherwise it returns suggestions. " +
        "Folder names are free text in the user's language; nested folders use 'Parent/Child'. " +
        "Only set tidy_desktop when the user asks to clean or tidy the desktop: it moves the grouped shortcuts into AppShelf's archive " +
        "(reversible with restore_desktop); real files and folders are never moved. Use undo to revert the last change to folders. " +
        "Answer the user briefly with what was grouped.";

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject Object(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(required.Select(x => (JsonNode)x).ToArray()), ["additionalProperties"] = false
    };
    private static JsonObject Tool(string name, string description, JsonObject schema, bool readOnly, bool destructive = false) => new()
    {
        ["name"] = name, ["description"] = description, ["inputSchema"] = schema,
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = destructive, ["idempotentHint"] = readOnly, ["openWorldHint"] = false }
    };

    public static JsonArray List()
    {
        var item = Text("Desktop item name from get_desktop (e.g. 'Telegram'), a full path, or an installed app name from find_installed_apps.");
        var colors = new JsonArray(FolderFaceCatalog.Palette.Select(x => (JsonNode)L.CatalogLabel(x.Name, "en-US")).Append("Automatic").ToArray());
        var symbols = new JsonArray(FolderFaceCatalog.Symbols.Select(x => (JsonNode)L.CatalogLabel(x.Name, "en-US")).ToArray());
        return
        [
            Tool("get_desktop", "List everything on the user's desktop (own and all-users): name, kind (shortcut, web_link, program, file, folder, appshelf_folder), " +
                "target, and in which AppShelf folders it already is.", Object(new()), true),
            Tool("get_folders", "Show the AppShelf folder tree with the apps in each folder and whether the folder is on the desktop or the taskbar.", Object(new()), true),
            Tool("find_installed_apps", "Search installed Windows apps (Start menu) by part of the name, for apps that are not on the desktop.",
                Object(new() { ["query"] = Text("Part of the app name, e.g. 'telegram' or 'photos'.") }, "query"), true),
            Tool("organize", "Put apps into folders in one undoable step. Missing folders are created; an app already in another folder is moved. " +
                "Nothing changes if any item is not found; the reply then lists suggestions.",
                Object(new()
                {
                    ["groups"] = new JsonObject
                    {
                        ["type"] = "array", ["minItems"] = 1,
                        ["items"] = Object(new()
                        {
                            ["folder"] = Text("Folder name, or 'Parent/Child' for a nested folder."),
                            ["items"] = new JsonObject { ["type"] = "array", ["items"] = item, ["minItems"] = 1 }
                        }, "folder", "items")
                    },
                    ["tidy_desktop"] = Flag("Move the grouped desktop shortcuts off the desktop into AppShelf's archive. Only when the user asks to clean the desktop. Default false."),
                    ["place_on_desktop"] = Flag("Put an icon of each top-level folder on the desktop if it is not there yet. Default true.")
                }, "groups"), false),
            Tool("move_item", "Move an app or folder inside AppShelf.", Object(new()
            {
                ["item"] = Text("App or folder name, or 'Folder/App' when the name is not unique."),
                ["to_folder"] = Text("Target folder ('Parent/Child' allowed); empty string for the top level.")
            }, "item", "to_folder"), false),
            Tool("rename_item", "Rename an app or folder in AppShelf (not the program itself).", Object(new()
            {
                ["item"] = Text("App or folder name, or 'Folder/App'."), ["new_name"] = Text("New name.")
            }, "item", "new_name"), false),
            Tool("remove_item", "Remove an app or a whole folder from AppShelf. Programs and files on disk are not touched.", Object(new()
            {
                ["item"] = Text("App or folder name, or 'Folder/App'.")
            }, "item"), false, true),
            Tool("place_folder", "Show or hide a folder's icon on the desktop and its button on the taskbar.", Object(new()
            {
                ["folder"] = Text("Folder name or 'Parent/Child'."),
                ["desktop"] = Flag("true adds the desktop icon, false removes it; omit to keep."),
                ["taskbar"] = Flag("true adds the taskbar button, false removes it; omit to keep.")
            }, "folder"), false),
            Tool("set_folder_look", "Change how a folder icon looks: its apps, a symbol, or a picture, on a coloured plate.", Object(new()
            {
                ["folder"] = Text("Folder name or 'Parent/Child'."),
                ["look"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("apps", "symbol", "picture") },
                ["color"] = new JsonObject { ["type"] = "string", ["enum"] = colors, ["description"] = "Plate color; 'Automatic' keeps the translucent plate. Legacy Russian color names are also accepted." },
                ["opacity"] = new JsonObject { ["type"] = "integer", ["minimum"] = 5, ["maximum"] = 100, ["description"] = "Plate opacity in percent (default 40, see-through)." },
                ["symbol"] = new JsonObject { ["type"] = "string", ["enum"] = symbols, ["description"] = "Required for look=symbol. English and legacy Russian symbol names are accepted." },
                ["picture"] = Text("Path to a PNG, JPG, BMP or ICO file; required for look=picture.")
            }, "folder"), false),
            Tool("restore_desktop", "Put shortcuts that organize moved off the desktop back where they were.", Object(new()
            {
                ["items"] = new JsonObject { ["type"] = "array", ["items"] = Text("Shortcut name; omit the list to restore everything."), ["description"] = "Optional." }
            }), false),
            Tool("undo", "Undo the last change to AppShelf folders (does not move desktop shortcuts back; use restore_desktop for that).", Object(new()), false)
        ];
    }
}
