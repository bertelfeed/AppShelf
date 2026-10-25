# Verification

The **0.6.0 build passed all 55 checks** in an interactive Windows 11 session
with English/Russian localization, with no compiler warnings.
The CI configuration was also exercised locally: **53 passed, 0 failed, 2 explicitly
skipped**. Isolated portable publishing and package-content checks passed.

Coverage includes tree operations, IDs, nesting limits, migration, atomic saves,
backup recovery, inter-process commands, Shell shortcut identity, icon extraction,
ICO frames, folder artwork, local scales, real minimized taskbar windows, minimal
menus, paging, drop geometry, organization/restore/undo, MCP stdio and repair of
moved application launchers. Localization checks cover all 259 message pairs,
format placeholders, plural forms, persisted preferences, live switching,
English dialogs and artwork tool names with legacy Russian compatibility.

The foreground-switch check timed out once during this validation and passed on
the complete repeat run. Foreground routing still needs manual release QA.

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

The README includes real Windows 11 desktop and taskbar captures. The desktop
Work folder was opened manually for the capture. Its demo menu uses 130% desktop
scale and 100% taskbar scale. Outside-click dismissal was disabled in this demo
profile while using Snipping Tool; the application default remains enabled.
The images were cropped in Snipping Tool to exclude personal windows and labels.
