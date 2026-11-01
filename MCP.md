# AppShelf MCP setup

[Русское описание возможностей](#что-умеет-сервер)

`AppShelf.Mcp.exe` is an optional local MCP server shipped beside `AppShelf.exe`.
It speaks JSON-RPC over stdio and forwards edits to the running AppShelf profile.
There is no embedded chatbot, subscription or AI API key in AppShelf.
Folder artwork tools advertise English color and symbol names (for example,
`Blue`, `Games`, `Automatic`) and also accept the original Russian aliases.
App messages follow the selected profile language.

## Claude Desktop

Add an entry to your Claude Desktop MCP configuration, replacing the example
location with the actual directory where you extracted AppShelf:

```json
{
  "mcpServers": {
    "appshelf": {
      "command": "C:\\Apps\\AppShelf\\AppShelf.Mcp.exe"
    }
  }
}
```

With the Microsoft Store version, use `"command": "appshelf.exe"` with
`"args": ["--mcp"]`. This app execution alias keeps working after Store updates;
`appshelf.exe --mcp` runs the same server on its stdio. The portable copy accepts
`AppShelf.exe --mcp` too.

Merge this entry with existing servers; do not replace an existing configuration.
Restart the client after changing its configuration.

## Claude Code

```text
claude mcp add appshelf --scope user -- "C:\Apps\AppShelf\AppShelf.Mcp.exe"
```

For another compatible client, configure the same executable as a local stdio
server. To use a separate profile, pass `--data-dir` and its absolute path as two
arguments. The server starts AppShelf in the background if that profile is not
running. Close connected server processes before replacing the portable binaries.

## Tools

| Tool | Purpose |
| --- | --- |
| `get_desktop` | List desktop items, shortcut targets and existing AppShelf membership. |
| `get_folders` | Read the folder tree and desktop/taskbar placement. |
| `find_installed_apps` | Find installed apps by name. |
| `organize` | Create or reuse folders and move apps into them in one collection edit. |
| `move_item`, `rename_item`, `remove_item` | Edit the collection; originals on disk are preserved. |
| `place_folder` | Add or remove a folder's desktop icon or running taskbar button. |
| `set_folder_look` | Set preview/symbol/image artwork, plate colour and opacity. |
| `restore_desktop` | Restore archived desktop shortcuts. |
| `undo` | Undo the last collection edit. |

## Privacy and desktop changes

The connected MCP client receives **desktop file names and paths**, shortcut
information and the AppShelf collection. Treat those as potentially personal data;
choose your client and its model/provider accordingly. The server does not read
file contents or send data directly to an external AI service.

Imported shortcuts are copied to `data/launchers`. Desktop tidying happens only
when `tidy_desktop` is requested. It moves grouped shortcuts and internet links
into `data/desktop-archive`; documents, programs and directories stay in place.
Use `restore_desktop` to reverse the shortcut move. `undo` reverts the collection
edit and is distinct from restoring desktop files.

## Что умеет сервер

Можно попросить выбранный клиент разложить приложения по папкам, в том числе по
диктовке. Сначала он читает рабочий стол и существующие группы, затем вызывает
`organize`. Названия вроде «Обсидиан» и «Дискорд» распознаются при однозначном
совпадении; если есть неоднозначность, возвращаются варианты.

Пример: «Telegram и Discord — в Общение, инструменты разработки — в Работу».
Уборку рабочего стола нужно просить отдельно: в архив переносятся только ярлыки.
Самостоятельные документы, программы и папки не перемещаются. Названия и пути
получает подключённый MCP-клиент; учитывайте его настройки приватности.
