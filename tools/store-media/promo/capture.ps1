# Films the folder menus for the promo's fast montage: one still per entry of variants.json.
# Like ../capture.ps1 it takes over the screen (for about 20 seconds) and gives it back at the end;
# after a crash, run it again with -RestoreOnly.
#   .\tools\store-media\promo\capture.ps1 -Language en-US
param(
    [string]$Language = 'en-US',
    [string]$App = '',
    [switch]$RestoreOnly
)
$ErrorActionPreference = 'Stop'
$media = Join-Path $PSScriptRoot '..'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$artifacts = Join-Path $root 'artifacts\store-media'
$statePath = Join-Path $artifacts 'restore-promo.json'
Add-Type -Path (Join-Path $media 'Native.cs') -ReferencedAssemblies System.Drawing
Add-Type -AssemblyName System.Windows.Forms
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
$text = Get-Content (Join-Path $media "text\$Language.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$demo = Get-Content (Join-Path $media 'demo.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$spec = Get-Content (Join-Path $PSScriptRoot 'variants.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$promoText = Get-Content (Join-Path $PSScriptRoot "text\$Language.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$raw = Join-Path $artifacts "$Language\promo\raw"
$profileDir = Join-Path ([IO.Path]::GetTempPath()) "AppShelfPromo\$Language"
foreach ($dir in @($raw, $profileDir)) { if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force } }
New-Item -ItemType Directory -Path $raw, $profileDir -Force | Out-Null
$wallpaper = Join-Path $artifacts 'wallpaper.png'
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$width = $screen.Bounds.Width; $height = $screen.Bounds.Height
& python (Join-Path $media 'make_wallpaper.py') $wallpaper $width $height | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Wallpaper generation failed.' }

# ----- One folder per variant, built from the demonstration folders and the apps installed on this PC.
$installed = @{}
foreach ($entry in Get-StartApps) { if ($entry.Name -and -not $installed.ContainsKey($entry.Name)) { $installed[$entry.Name] = $entry.AppID } }
function App-Items($folder) {
    foreach ($names in $folder.apps) {
        $name = @($names | Where-Object { $installed.ContainsKey($_) })[0]
        if (-not $name) { continue }
        $label = $name -replace '^Microsoft (?=\S)', '' -replace ' \((classic|64bit|Preview)\)$', ''
        [ordered]@{ Id = [guid]::NewGuid().ToString(); Kind = 1; Name = $label; Target = 'shell:AppsFolder\' + $installed[$name] }
    }
}
function Settings($overrides) {
    $settings = [ordered]@{ CloseOnFocusLost = $false; CloseAfterLaunch = $false; ShowOnTaskbar = $false }
    foreach ($p in $spec.defaults.PSObject.Properties) { $settings[$p.Name] = $p.Value }
    foreach ($p in $overrides.PSObject.Properties) { $settings[$p.Name] = $p.Value }
    $settings
}
$items = New-Object System.Collections.ArrayList
$variants = @()
$n = 0
foreach ($variant in $spec.variants) {
    $n++
    $children = New-Object System.Collections.ArrayList
    if ($variant.folders) {
        # The demonstration folders as tiles: each shows previews of its apps on a plate of its colour.
        $i = 0
        foreach ($folder in @($demo.folders)) {
            if ($i -ge $variant.colors.Count) { break }
            $look = [ordered]@{ Face = 0; FaceColor = $variant.colors[$i]; FaceOpacity = 70 }
            [void]$children.Add([ordered]@{ Id = [guid]::NewGuid().ToString(); Kind = 0; Name = [string]$text.folders.($folder.key)
                Children = @(App-Items $folder); Settings = (Settings ([pscustomobject]$look)) })
            $i++
        }
        $name = [string]$promoText.foldersMenu
    } else {
        $source = @($demo.folders | Where-Object { $_.key -eq $variant.from })[0]
        foreach ($item in @(App-Items $source)) { [void]$children.Add($item) }
        $name = [string]$text.folders.($variant.from)
    }
    $id = [guid]::NewGuid().ToString()
    [void]$items.Add([ordered]@{ Id = $id; Kind = 0; Name = $name; Children = $children; Settings = (Settings $variant.settings) })
    $variants += [ordered]@{ name = ('v{0:00}' -f $n); id = $id; tag = $variant.tag; settings = (Settings $variant.settings) }
}
$config = [ordered]@{ SchemaVersion = 2; Appearance = 2; Language = $text.language; Items = $items }
[IO.File]::WriteAllText((Join-Path $profileDir 'shelf.json'), ($config | ConvertTo-Json -Depth 40), (New-Object Text.UTF8Encoding $false))

function Menu-Window {
    @($native::Windows($script:demoProcess.Id)) | Where-Object { $_.Class -like 'HwndWrapper*' -and $_.Left -ge 0 -and $_.Top -ge 0 -and ($_.Right - $_.Left) -gt 120 } |
        Sort-Object { ($_.Right - $_.Left) * ($_.Bottom - $_.Top) } | Select-Object -Last 1
}

# ----- Take over the screen; restore-promo.json records how to give it back.
$animated = @(Get-Process wallpaper32, wallpaper64 -ErrorAction SilentlyContinue | Where-Object { $_.Path } | ForEach-Object { $_.Path } | Sort-Object -Unique)
[ordered]@{ wallpaper = $native::GetWallpaper(); position = $native::GetWallpaperPosition(); icons = $native::DesktopIconsVisible(); animated = $animated; demoApp = $App; demoProfile = $profileDir } |
    ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
try {
    Get-Process wallpaper32, wallpaper64 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    (New-Object -ComObject Shell.Application).MinimizeAll()
    $native::SetWallpaper($wallpaper, 4)
    $native::SetDesktopIcons($false)
    # The menus open under the pointer; above them it hovers no tile.
    $native::Glide([int]($width * 0.45), 120, 10)
    $script:demoProcess = Start-Process -FilePath $App -ArgumentList @('--background', '--data-dir', "`"$profileDir`"") -PassThru
    Start-Sleep -Seconds 3
    # The first menu of a session shows a flat grey backdrop; another folder opened beforehand settles it.
    & $App --folder $variants[-1].id --surface desktop --data-dir $profileDir; Start-Sleep -Milliseconds 1200
    & $App --folder $variants[-1].id --surface desktop --data-dir $profileDir; Start-Sleep -Milliseconds 600
    foreach ($variant in $variants) {
        & $App --folder $variant.id --surface desktop --data-dir $profileDir
        $menu = $null
        for ($i = 0; $i -lt 50 -and -not $menu; $i++) { Start-Sleep -Milliseconds 60; $menu = Menu-Window }
        if (-not $menu) { throw "The menu of $($variant.name) did not open." }
        # An inactive menu shows a flat grey instead of its translucent backdrop.
        $native::Front($menu.Handle)
        # The menu window is reused: its place and size are read once it has finished opening.
        Start-Sleep -Milliseconds 700
        $menu = Menu-Window
        $native::CaptureScreen((Join-Path $raw "$($variant.name).png"), $width, $height)
        $variant.rect = @($menu.Left, $menu.Top, $menu.Right, $menu.Bottom)
        & $App --folder $variant.id --surface desktop --data-dir $profileDir # the same command closes it
        for ($i = 0; $i -lt 30 -and (Menu-Window); $i++) { Start-Sleep -Milliseconds 60 }
        Start-Sleep -Milliseconds 200
    }
    [ordered]@{ language = $Language; screen = @($width, $height); variants = $variants } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $raw 'variants.json') -Encoding UTF8
}
finally {
    Restore-Desktop
}
Write-Host "Captured into $raw"
