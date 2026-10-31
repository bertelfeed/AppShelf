# Films AppShelf for Store media: stills of every scene and, with ffmpeg, one screen recording for the trailer.
# It takes over the screen for about a minute: minimizes windows, sets the generated wallpaper, hides desktop icons
# and stops other AppShelf instances. Everything is restored at the end, even after an error; if the process is
# killed, run it again with -RestoreOnly.
#   .\tools\store-media\capture.ps1 -Language en-US [-Ffmpeg C:\path\ffmpeg.exe]
param(
    [string]$Language = 'en-US',
    [string]$App = '',
    [string]$Ffmpeg = '',
    # h264_nvenc needs an NVIDIA GPU; libx264 works with any full ffmpeg build.
    [string]$Codec = 'h264_nvenc',
    [switch]$RestoreOnly
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$media = Join-Path $root 'artifacts\store-media'
$statePath = Join-Path $media 'restore.json'
Add-Type -Path (Join-Path $PSScriptRoot 'Native.cs') -ReferencedAssemblies System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
[StoreMedia.Native]::DpiAware()
$native = [StoreMedia.Native]

# Started through WMI, the apps outlive this script even when its host closes every process it started.
# Wallpaper Engine also needs its own folder as the working directory, or it may exit at once.
function Start-Detached([string]$path, [string]$arguments = '') {
    [void](Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$path`" $arguments".Trim(); CurrentDirectory = (Split-Path $path) })
}
function Restore-Desktop {
    if (-not (Test-Path -LiteralPath $statePath)) { return }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.demoApp) { try { & $state.demoApp --quit --data-dir $state.demoProfile; Start-Sleep -Milliseconds 800 } catch { } }
    $native::SetDesktopIcons([bool]$state.icons)
    if ($state.wallpaper) { $native::SetWallpaper($state.wallpaper, [int]$state.position) }
    (New-Object -ComObject Shell.Application).UndoMinimizeALL()
    foreach ($path in @($state.apps)) { if ($path -and (Test-Path -LiteralPath $path)) { Start-Detached $path '--background' } }
    # Wallpaper Engine sometimes exits at once when started right after the desktop changes, so it gets a few tries.
    foreach ($path in @($state.animated)) {
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        for ($try = 0; $try -lt 3; $try++) {
            Start-Detached $path; Start-Sleep -Seconds 4
            if (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($path)) -ErrorAction SilentlyContinue) { break }
        }
    }
    Remove-Item -LiteralPath $statePath -Force
    Write-Host 'Desktop restored.'
}
if ($RestoreOnly) { Restore-Desktop; return }
if (Test-Path -LiteralPath $statePath) { throw "A previous capture did not finish. Run with -RestoreOnly first: $statePath" }

if (-not $App) { $App = Join-Path $root 'src\AppShelf.App\bin\Release\net10.0-windows\AppShelf.exe' }
$App = (Resolve-Path $App).Path
$text = Get-Content (Join-Path $PSScriptRoot "text\$Language.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$demo = Get-Content (Join-Path $PSScriptRoot 'demo.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$out = Join-Path $media $Language
$raw = Join-Path $out 'raw'
# A short profile path keeps the taskbar relaunch command under MAX_PATH, so the buttons show folder names.
$profileDir = Join-Path ([IO.Path]::GetTempPath()) "AppShelfMedia\$Language"
foreach ($dir in @($out, $profileDir)) { if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force } }
New-Item -ItemType Directory -Path $raw, $profileDir -Force | Out-Null
$wallpaper = Join-Path $media 'wallpaper.png'
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$width = $screen.Bounds.Width; $height = $screen.Bounds.Height
& python (Join-Path $PSScriptRoot 'make_wallpaper.py') $wallpaper $width $height | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Wallpaper generation failed.' }

# ----- Demonstration profile in the requested language, from apps installed on this PC.
$installed = @{}
foreach ($entry in Get-StartApps) { if ($entry.Name -and -not $installed.ContainsKey($entry.Name)) { $installed[$entry.Name] = $entry.AppID } }
$ids = @{}
$keys = @{}; foreach ($p in $text.folders.PSObject.Properties) { $keys[[string]$p.Value] = $p.Name }
function New-DemoFolder($spec) {
    $id = [guid]::NewGuid().ToString(); $ids[$spec.key] = $id
    $children = New-Object System.Collections.ArrayList
    foreach ($names in $spec.apps) {
        $name = @($names | Where-Object { $installed.ContainsKey($_) })[0]
        if (-not $name) { Write-Host "skip: $($names -join ' / ')"; continue }
        # Short labels fit under the icons: "Microsoft Teams" shows as "Teams", "Outlook (classic)" as "Outlook".
        $label = $name -replace '^Microsoft (?=\S)', '' -replace ' \((classic|64bit|Preview)\)$', ''
        [void]$children.Add([ordered]@{ Id = [guid]::NewGuid().ToString(); Kind = 1; Name = $label; Target = 'shell:AppsFolder\' + $installed[$name] })
    }
    foreach ($nested in @($spec.folders)) { if ($nested) { [void]$children.Insert([Math]::Min(4, $children.Count), (New-DemoFolder $nested)) } }
    $settings = [ordered]@{ Appearance = $demo.appearance; CloseOnFocusLost = $false; CloseAfterLaunch = $false; ShowOnTaskbar = [bool]$spec.taskbar }
    foreach ($p in $demo.defaults.PSObject.Properties) { $settings[$p.Name] = $p.Value }
    foreach ($p in $spec.settings.PSObject.Properties) { $settings[$p.Name] = $p.Value }
    [ordered]@{ Id = $id; Kind = 0; Name = [string]$text.folders.($spec.key); Children = $children; Settings = $settings }
}
$items = @(foreach ($folder in $demo.folders) { New-DemoFolder $folder })
$config = [ordered]@{ SchemaVersion = 2; Appearance = $demo.appearance; Language = $text.language; Items = $items }
[IO.File]::WriteAllText((Join-Path $profileDir 'shelf.json'), ($config | ConvertTo-Json -Depth 40), (New-Object Text.UTF8Encoding $false))

# ----- Scene helpers.
$events = New-Object System.Collections.ArrayList
$clock = [Diagnostics.Stopwatch]::new()
function Mark([string]$name) { [void]$events.Add([ordered]@{ name = $name; t = [Math]::Round($clock.Elapsed.TotalSeconds, 3) }) }
function Demo-Windows { @($native::Windows($script:demoProcess.Id)) }
function Still([string]$name) {
    Start-Sleep -Milliseconds 350
    $native::CaptureScreen((Join-Path $raw "$name.png"), $width, $height)
    $windows = @(Demo-Windows | ForEach-Object { [ordered]@{ title = $_.Title; cls = $_.Class; rect = @($_.Left, $_.Top, $_.Right, $_.Bottom) } })
    [ordered]@{ name = $name; t = [Math]::Round($clock.Elapsed.TotalSeconds, 3); cursor = @($native::Cursor()); windows = $windows } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $raw "$name.json") -Encoding UTF8
    Mark "still:$name"
}
function Rect-Of($element) { $r = $element.Current.BoundingRectangle; @([int]$r.Left, [int]$r.Top, [int]$r.Right, [int]$r.Bottom) }
function Center($rect) { @([int](($rect[0] + $rect[2]) / 2), [int](($rect[1] + $rect[3]) / 2)) }
function Find-Named($rootElement, [string]$name, [switch]$Prefix) {
    $all = $rootElement.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        $n = $e.Current.Name
        if (($Prefix -and $n.StartsWith($name)) -or $n -eq $name) { if (-not $e.Current.IsOffscreen) { return $e } }
    }
    $null
}
function Taskbar-Element {
    [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')))
}
# The taskbar names every folder button after the program, so they are told apart by opening each one.
function Shelf-Buttons {
    $all = (Taskbar-Element).FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    @($all | Where-Object { $_.Current.ClassName -like '*TaskListButton*' -and $_.Current.Name -like 'AppShelf*' } | Sort-Object { $_.Current.BoundingRectangle.Left })
}
function Popup-Window {
    Demo-Windows | Where-Object { $_.Title -ne 'AppShelf' -and $_.Class -like 'HwndWrapper*' -and $_.Left -ge 0 -and $_.Top -ge 0 -and
        $_.Right -le $width -and ($_.Right - $_.Left) -gt 120 } | Sort-Object { ($_.Right - $_.Left) * ($_.Bottom - $_.Top) } | Select-Object -Last 1
}
function Popup-Element {
    $popup = Popup-Window
    if (-not $popup) { throw 'The folder menu did not open.' }
    [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$popup.Handle)
}
function Taskbar-Button([string]$key) { $script:buttons[$key] }
function Wait-Closed { for ($i = 0; $i -lt 15; $i++) { if (-not (Popup-Window)) { return $true }; Start-Sleep -Milliseconds 100 }; $false }
# Esc reaches the menu only when it has the keyboard; otherwise the folder's own command closes a taskbar menu.
function Close-Popup {
    $w = Popup-Window
    if (-not $w) { return }
    $native::Front($w.Handle); Start-Sleep -Milliseconds 120; $native::Key(0x1B)
    if (Wait-Closed) { return }
    $key = $keys[$w.Title]
    if ($key) { & $App --folder $ids[$key] --data-dir $profileDir; [void](Wait-Closed) }
}
function Glide-To($point, [int]$ms = 650) { $native::Glide($point[0], $point[1], $ms) }
function Click-Element($element, [int]$ms = 650) { Glide-To (Center (Rect-Of $element)) $ms; Start-Sleep -Milliseconds 150; $native::Click() }
function Folder-Name([string]$key) { [string]$text.folders.$key }
# Over the menu's title strip: inside the menu (so the wheel turns its pages) but over no app, so no tooltip shows.
function Park-Pointer { $w = Popup-Window; if ($w) { Glide-To @([int](($w.Left + $w.Right) / 2), ($w.Top + 9)) 300 } }

# ----- Take over the screen; restore.json records how to give it back.
$others = @(Get-Process AppShelf -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ne $App } | ForEach-Object { $_.Path } | Sort-Object -Unique)
# Animated wallpaper apps draw over the desktop picture; they are closed for the capture and started again after it.
$animated = @(Get-Process wallpaper32, wallpaper64 -ErrorAction SilentlyContinue | Where-Object { $_.Path } | ForEach-Object { $_.Path } | Sort-Object -Unique)
[ordered]@{ wallpaper = $native::GetWallpaper(); position = $native::GetWallpaperPosition(); icons = $native::DesktopIconsVisible(); apps = $others; animated = $animated; demoApp = $App; demoProfile = $profileDir } |
    ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
$recorder = $null
try {
    foreach ($path in $others) { & $path --quit }
    Get-Process AppShelf -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $App } | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
    Get-Process wallpaper32, wallpaper64 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    (New-Object -ComObject Shell.Application).MinimizeAll()
    $native::SetWallpaper($wallpaper, 4)
    $native::SetDesktopIcons($false)
    $native::Glide([int]($width / 2), [int]($height / 2), 10)
    $script:demoProcess = Start-Process -FilePath $App -ArgumentList @('--background', '--data-dir', "`"$profileDir`"") -PassThru
    $expected = @($demo.folders | Where-Object { $_.taskbar }).Count
    $found = @()
    for ($i = 0; $i -lt 60 -and $found.Count -lt $expected; $i++) { Start-Sleep -Milliseconds 250; $found = Shelf-Buttons }
    if ($found.Count -lt $expected) { throw "Expected $expected folder buttons on the taskbar, found $($found.Count)." }
    Start-Sleep -Seconds 2 # Folder artwork is drawn after the buttons appear.
    $script:buttons = @{}; $taskbar = [ordered]@{}
    foreach ($button in $found) {
        Close-Popup
        Glide-To (Center (Rect-Of $button)) 10; Start-Sleep -Milliseconds 150; $native::Click()
        $title = $null
        for ($i = 0; $i -lt 20 -and -not $title; $i++) { Start-Sleep -Milliseconds 150; $title = (Popup-Window).Title }
        if ($title -and $keys.ContainsKey($title)) { $script:buttons[$keys[$title]] = $button; $taskbar[$keys[$title]] = Rect-Of $button }
        # A second click on the same button closes its menu.
        Start-Sleep -Milliseconds 300; $native::Click(); [void](Wait-Closed)
    }
    Close-Popup
    if (-not $script:buttons['work'] -or -not $script:buttons['games']) { throw "Could not identify the folder buttons: $($taskbar.Keys -join ', ')" }
    $work = $script:buttons['work']
    if ($Ffmpeg) {
        $info = New-Object Diagnostics.ProcessStartInfo $Ffmpeg
        $info.Arguments = "-hide_banner -loglevel error -y -f gdigrab -framerate 30 -draw_mouse 1 -i desktop $(if ($Codec -eq 'h264_nvenc') { '-c:v h264_nvenc -preset p5 -rc vbr -cq 16' } else { "-c:v $Codec -preset veryfast -crf 14" }) -pix_fmt yuv420p `"$(Join-Path $raw 'screen.mp4')`""
        $info.UseShellExecute = $false; $info.RedirectStandardInput = $true; $info.CreateNoWindow = $true
        $recorder = [Diagnostics.Process]::Start($info)
        Start-Sleep -Milliseconds 1200
    }
    $clock.Start(); Mark 'start'
    Glide-To @([int]($width * 0.55), [int]($height * 0.55)) 400
    Start-Sleep -Milliseconds 600

    # Taskbar: a click on the Work button opens its menu.
    Mark 'taskbar'
    Click-Element $work 900
    Start-Sleep -Milliseconds 700
    $popup = Popup-Element
    Glide-To (Center (Rect-Of (Find-Named $popup 'Excel'))) 700
    Start-Sleep -Milliseconds 500
    Park-Pointer
    Still 'taskbar'
    Start-Sleep -Milliseconds 600

    # Nested folder and back.
    Mark 'nested'
    Click-Element (Find-Named $popup (Folder-Name 'dev')) 700
    Start-Sleep -Milliseconds 800
    $popup = Popup-Element
    Glide-To (Center (Rect-Of (Find-Named $popup 'Docker Desktop'))) 600
    Start-Sleep -Milliseconds 400
    Park-Pointer
    Still 'nested'
    Start-Sleep -Milliseconds 700
    $native::Key(0x08) # Backspace returns to the parent folder.
    Start-Sleep -Milliseconds 700

    # Pages: the Games folder has more apps than fit.
    Mark 'pages'
    Click-Element (Taskbar-Button 'games') 900
    Start-Sleep -Milliseconds 800
    $popup = Popup-Element
    Park-Pointer
    Still 'pages-1'
    Start-Sleep -Milliseconds 500
    $native::Wheel(1)
    Start-Sleep -Milliseconds 700
    Still 'pages'
    Start-Sleep -Milliseconds 700
    $native::Wheel(-1)
    Start-Sleep -Milliseconds 800
    # The menu stays open: once it closes, Windows may bring an elevated window to the front, and then it ignores
    # all pointer movement. The desktop menu below replaces it.

    # Desktop: compose.py draws the folder icon under the pointer (desktop icons are hidden for privacy).
    Mark 'desktop'
    $anchor = @(64, 300)
    Glide-To $anchor 900
    Start-Sleep -Milliseconds 250
    Mark 'desktop-open'
    & $App --folder $ids['work'] --surface desktop --data-dir $profileDir
    Start-Sleep -Milliseconds 1100
    Still 'desktop'
    Start-Sleep -Milliseconds 900
    Close-Popup
    Start-Sleep -Milliseconds 500

    # Editor and folder settings.
    Mark 'editor'
    & $App --data-dir $profileDir
    $editor = $null
    for ($i = 0; $i -lt 40 -and -not $editor; $i++) { Start-Sleep -Milliseconds 250; $editor = Demo-Windows | Where-Object { $_.Title -eq 'AppShelf' } | Select-Object -First 1 }
    $native::Place($editor.Handle, [int](($width - 900) / 2), 110, 900, 780)
    $native::Front($editor.Handle)
    Start-Sleep -Milliseconds 900
    $editorElement = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$editor.Handle)
    # A tree item's bounds include its nested folders, so the click goes to its own row.
    $workItem = Rect-Of (Find-Named $editorElement (Folder-Name 'work'))
    Glide-To @(($workItem[0] + 60), ($workItem[1] + 12)) 700; Start-Sleep -Milliseconds 150; $native::Click()
    Start-Sleep -Milliseconds 900
    Glide-To @([int]($width / 2), 40) 400
    Still 'editor'
    Mark 'style'
    Click-Element (Find-Named $editorElement (Folder-Name 'create')) 600
    Start-Sleep -Milliseconds 700
    # The folder's gear exists only while a folder is open.
    $gear = $editorElement.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'FolderSettingsButton')))
    Click-Element $gear 600
    # The settings dialog is as tall as its content and opens centred on the editor, over the taskbar.
    # Moved up beside the editor, both fit the 1:1 frame above the taskbar.
    $dialog = $null
    for ($i = 0; $i -lt 150 -and -not $dialog; $i++) { Start-Sleep -Milliseconds 20; $dialog = Popup-Window }
    if ($dialog) { $native::Place($dialog.Handle, [int](($width - 900) / 2) + 490, 60, $dialog.Right - $dialog.Left, $dialog.Bottom - $dialog.Top) }
    Start-Sleep -Milliseconds 1200
    Still 'style'
    Start-Sleep -Milliseconds 800
    $native::Key(0x1B)
    Start-Sleep -Milliseconds 500
    Mark 'end'

    $meta = [ordered]@{ language = $Language; screen = @($width, $height); taskbar = $taskbar;
        desktopAnchor = $anchor; ids = $ids; profile = $profileDir; events = $events; recording = [bool]$recorder }
    $meta | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $raw 'capture.json') -Encoding UTF8
}
finally {
    if ($recorder) { try { $recorder.StandardInput.Write('q'); $recorder.StandardInput.Close(); [void]$recorder.WaitForExit(15000) } catch { } }
    Restore-Desktop
}
Write-Host "Captured into $raw"
