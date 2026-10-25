<p align="center"><img src="docs/images/appshelf.svg" width="76" alt="AppShelf"></p>
<h1 align="center">AppShelf</h1>
<p align="center">Your Windows apps. A little more organized.</p>
<p align="center">
  <a href="https://github.com/bertelfeed/AppShelf/actions/workflows/build.yml"><img src="https://github.com/bertelfeed/AppShelf/actions/workflows/build.yml/badge.svg" alt="Windows build"></a>
  <img src="https://img.shields.io/badge/preview-0.6.0-0078D4" alt="Preview 0.6.0">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-2ea44f" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/Windows-11-0078D4" alt="Windows 11">
</p>
<p align="center"><a href="README.ru.md">Русский</a> · <a href="MCP.md">MCP setup</a> · <a href="CONTRIBUTING.md">Build & contribute</a></p>

![AppShelf editor with a demonstration collection](docs/images/editor.png)

Put your applications into folders that open from the **desktop or taskbar**.
Keep the menu small: a title, your app icons, and nested folders. Manage everything
else in the Fluent editor.

**0.6.0 is a development preview. The first stable release is planned as 1.0.**
The interface is available in **English and Russian**. Change it in
**Settings → Language**; the change applies immediately and is remembered.
New profiles follow the Windows language, using English for other languages.

## Made for Windows

- **English and Russian**, including dialogs, menus, tooltips and app messages.
- **Nested folders**, with compact acrylic menus and a Mica editor.
- **Desktop and taskbar placement**, independently for each folder.
- **Your own layout:** rows, columns, paging, labels, themes and separate icon scales
  for desktop and taskbar menus.
- **Recognizable folder icons:** application previews, a symbol or your own image,
  with a selectable background and opacity.
- **Drag and drop** into menus, reorder apps, or drop onto a nested folder.
- **Taskbar previews** with the folder's contents and quick-launch buttons.
- **Resilient launchers:** imported shortcuts are copied into the profile; moved
  applications can be repaired after updates.
- **Optional MCP integration:** organize apps through Claude or another MCP client,
  without adding a chatbot to the application.

![Work folder menu above the real Windows 11 taskbar](docs/images/taskbar.png)

The three preview icons on the right are AppShelf folder buttons on the Windows
taskbar. Each shows the apps inside its folder.

![Work folder icon and its open menu on the Windows 11 desktop](docs/images/desktop.png)

The same folder on the desktop. These are real Windows screenshots using a
demonstration collection.

## Try the preview

Requires **Windows 11 x64**. Portable builds include .NET; no separate runtime or
Visual Studio installation is needed to run them.

Development ZIPs and their SHA-256 checksums are available in the
[Windows build artifacts](https://github.com/bertelfeed/AppShelf/actions/workflows/build.yml).
GitHub may require sign-in to download workflow artifacts. There is no stable
release yet.

1. Extract the entire `AppShelf` folder to a permanent, writable location.
2. Open `AppShelf.exe` and create a folder.
3. Add applications or drag shortcuts into it.
4. Open **Place** to show it on the desktop, taskbar, or both.
5. Use the folder's gear button for its appearance and layout.

Desktop single-click and folder taskbar buttons work while AppShelf is running in
its notification area. Closing the editor hides it. Fully exiting AppShelf removes
its running taskbar buttons; they return when the app starts again. Autostart is
optional. These are running application buttons, not Explorer pins independent of
AppShelf. The system Start menu is not modified.

Click outside a menu or press **Esc** to close it. Use the nested folder title,
Backspace or Alt+Left to go back. More apps are paged with the wheel, navigation
keys, or the vertical page dots.

## Make it yours

![Per-folder appearance and layout settings](docs/images/folder-settings.png)

Each folder keeps its own options. Desktop and taskbar menu scales range from
50–200%; Windows itself chooses the size of the folder's icon on those surfaces.
Your chosen picture is copied into the profile, so moving its original does not
break the folder icon.

## Optional AI organization

`AppShelf.Mcp.exe` exposes local tools to your chosen MCP client. It can list apps,
create nested folders, move items, change folder artwork and undo collection edits.
Desktop tidying is explicit and reversible: only shortcuts are moved to the
profile's archive; documents and application folders stay in place.

The client receives desktop file names and paths. AppShelf itself does not call an
AI service or require an API key. See [MCP setup and privacy](MCP.md).

## Build from source

Use Windows and a stable **.NET 10 SDK**:

```powershell
./build.ps1 -Check
./build.ps1 -Check -Package
```

The second command publishes into a fresh staging directory and produces
`artifacts/AppShelf-0.6.0-win-x64.zip` and its `.sha256` file. It excludes profiles
and debug symbols. To update a local portable folder instead, close its processes
and use `./build.ps1 -Publish`; preserve its `data` directory.

## Data and upgrades

With `portable.flag`, all app data lives in `data` next to the executables.
Otherwise it uses `%LOCALAPPDATA%/AppShelf`. `--data-dir` selects a separate profile.
Back up **the entire data directory**, including copied launchers and artwork,
before replacing binaries. Saves are atomic and the previous state is kept in
`shelf.json.bak`. Removing an item from AppShelf does not uninstall its application.

## On the way to 1.0

The remaining release work focuses on real Explorer clicks and dragging,
sleep/resume, mixed-DPI monitors, accessibility and packaging polish.
See [verification and manual QA](docs/VERIFICATION.md). The preview has no installer,
code-signing certificate or automatic updater.

The repository contains [five reconstructed development checkpoints](docs/HISTORY.md).
Code and assets were written independently; AppGroup source code was not copied.

## License

AppShelf is [MIT licensed](LICENSE), copyright 2026 bertelfeed. Bundled Microsoft
runtime components retain their separate notices in `licenses/` and
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
