# AppShelf 0.6.1 — development preview

Windows 11 folders for your desktop and taskbar, with nested menus and optional
MCP integration. This is a preview on the way to the first stable 1.0 release.

## New in 0.6.1

- Update notice in the editor and tray menu when a new version is out; Settings can turn it off.
- Settings link to the new website, GitHub and Report a bug.
- The MCP server also starts as `AppShelf.exe --mcp` (see MCP.md).
- The folder settings dialog fits longer translations.
- Checks run on hosted CI runners without an interactive shell.

## Features

- Full English and Russian interfaces with immediate, persistent language switching.
- Native Fluent editor with Mica, compact acrylic menus and light/dark themes.
- Nested folders, paging, drag and drop, and independent desktop/taskbar scales.
- Folder artwork from its applications, a Windows symbol or your own picture.
- Separate taskbar buttons with content thumbnails and quick-launch actions.
- Single-click desktop menus, outside-click dismissal and Escape to close.
- Copied launchers, automatic repair of moved applications, atomic saves and undo.
- Optional local MCP server for organizing applications through your chosen client.

## Run

Extract the entire ZIP to a permanent writable folder and run `AppShelf.exe`.
The .NET runtime is included. Windows 11 x64 is required. Choose English or
Russian in Settings → Language; restarting is not required.

Taskbar folder buttons and desktop single-click require AppShelf to keep running
in the notification area. Closing the editor hides it; exiting the background app
removes its running taskbar buttons. Enable app autostart if desired.

## Upgrade

Exit AppShelf and connected MCP server processes. Back up the entire `data`
directory, replace application files, and preserve `data`. Do not move the portable
folder without recreating shortcuts that refer to its old location.

There is no installer, code-signing certificate or automatic updater in this preview.
See the README for verification limits and the path to version 1.0.
