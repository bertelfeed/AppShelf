# Verification

On 29 October 2026, the **0.6.0 build passed all 65 checks** in an interactive
Windows 11 session, with no compiler warnings. The CI configuration was also
exercised locally: **63 passed, 0 failed, 2 explicitly skipped**. Isolated portable
publishing, MSIX creation and package-content checks passed.

Coverage includes tree operations, IDs, nesting limits, migration, atomic saves,
backup recovery, inter-process commands, Shell shortcut identity, icon extraction,
ICO frames, folder artwork, local scales, real minimized taskbar windows, minimal
menus, paging, drop geometry, organization/restore/undo, MCP stdio and repair of
moved application launchers. Localization checks cover all 274 message pairs,
format placeholders, plural forms, persisted preferences, live switching,
English dialogs and artwork tool names with legacy Russian compatibility.
New regression checks cover profile import overlap, occupied destinations,
rollback of an invalid import, restoring from an imported desktop archive,
persisted shortcut ownership and leaving foreign desktop files untouched.
Settings checks also verify the GitHub and Microsoft Store buttons in both
languages, their position above support, and Store availability with and without
a configured product ID. The shipped portable build has no test product ID.
The links use embedded official brand artwork, with English/Russian Store
badges; checks verify the selected image loads and both badges have equal height.

Offline Store integration checks cover public identity, release tags, MSIX
contents and checksums, disabled publishing, dry runs, missing credentials,
mocked submission order and stopping after authentication failure. All three
GitHub workflows pass actionlint. The built MSIX passed submission-script dry
run validation without contacting Microsoft. Live Store submission remains
unverified until Partner Center setup; see [Store publishing](STORE-PUBLISHING.md).

An earlier validation encountered a foreground-switch timeout and passed on
repeat. A transient OneDrive directory-rename failure during profile import was
handled with bounded retries; the full suite then passed. Foreground routing
still needs manual release QA.

Hosted Windows CI has no user's interactive foreground desktop. Two checks that
change foreground focus are explicitly marked SKIP there. The remaining checks
run normally; the complete suite must also pass on an unlocked Windows desktop.

Passing a synthetic drop or sending a restore message to an owned window does
not prove Explorer mouse routing. Before stable 1.0, manually verify:

- Single/double/repeated clicks on real desktop and taskbar folder icons.
- Dragging shortcuts from Explorer into the menu, reordering and page-edge drops.
- Dragging a desktop folder icon without opening it.
- Sleep/resume, lock/unlock and hook reinstallation.
- Mixed-DPI monitors, screen edges and taskbar auto-hide.
- Windows sign-in with autostart and restored folder buttons.
- High contrast, keyboard navigation and a screen reader.
- UAC launches and recovery while the profile is briefly locked by OneDrive.

Screenshots use a separate demonstration profile. The public ZIP is created from
a fresh publish directory, checked for required licenses and executables, and
rejected if it contains a data directory, debug symbols or environment files.

The README images are real Windows 11 captures from the Store media pipeline
(`tools/store-media`), using its demonstration profile at 200% icon scale. The
taskbar keeps only AppShelf's buttons, and the desktop folder icons are drawn from
the app's own icon files because desktop icons are hidden during capture.

The MSIX uses a development identity and has not been installed in this review.
Packaged startup, execution aliases, per-folder taskbar identities and upgrades
remain unverified.
