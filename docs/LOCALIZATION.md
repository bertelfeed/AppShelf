# Interface languages

AppShelf supports English (`en-US`) and Russian (`ru-RU`). Open **Settings →
Language**, choose a language and save. The editor, folder menus, tray menu and
app messages update without restarting. The selection is saved per profile.

New profiles follow Windows: Russian on a Russian Windows installation, English
otherwise. Existing profiles retain their saved preference. Folder names, app
names and file paths are user content and are not translated. Windows supplies
its own file pickers, title-bar system menu and installed app names in the
system's language.

The local MCP server follows the profile language for app messages. Its tool
definitions use English. Artwork parameters accept English names such as `Blue`,
`Games` and `Automatic`, as well as the original Russian aliases, regardless of
the chosen interface language.

## Editing translations

Shared text lives in `src/AppShelf.Core/Resources/Strings.resx` (English neutral
resources) and `Strings.ru.resx` (Russian). Keep the same keys and format
placeholders in both files. Use `Localization.Get` for messages and
`Localization.F` for formatted messages. XAML uses the `Text.*` dynamic resources
populated by `App.ApplyLanguage` so existing views change immediately.

Artwork catalog names remain stable for compatibility; `Localization.CatalogLabel`
provides their display names and `CatalogMatches` accepts both languages.

Run `./build.ps1 -Check` on Windows. Checks cover matching resource keys and
format placeholders, English and Russian plurals, persistent language choices,
live UI switching, English dialogs, and bilingual artwork tool inputs. Packaging
also verifies the Russian satellite assembly is included.
