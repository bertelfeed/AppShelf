# Renders the application icon ("fan": a folder of four app tiles in front of two fanned plates)
# into a multi-size ICO and a preview sheet. Run from the repository root in Windows PowerShell:
#   .\tools\make-app-icon.ps1
# -StoreAssets also writes the logos of the MSIX package (tiles, Store logo, taskbar targetsize images).
# The vector source of the same design is src/AppShelf.App/Assets/AppShelf.svg.
param(
    [string]$Ico = "src\AppShelf.App\Assets\AppShelf.ico",
    [string]$Preview = "",
    [string]$StoreAssets = "",
    # A single large PNG of the icon, e.g. for Store artwork: -LogoPng logo.png -LogoSize 1024
    [string]$LogoPng = "",
    [int]$LogoSize = 1024
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

# Largest first: Windows picks the best frame itself, but WPF (the editor header) shows the first one,
# and a 16 px first frame would be blown up and blurry.
$sizes = 256, 128, 96, 64, 48, 40, 32, 24, 20, 16
function Brush([string]$hex) { $b = [Windows.Media.BrushConverter]::new().ConvertFromString($hex); $b.Freeze(); $b }
$teal = Brush '#2EC4B6'; $orange = Brush '#FF8A3D'; $plate = Brush '#5B5FC7'; $rim = Brush '#7376D6'
$tiles = @((Brush '#FF8A3D'), (Brush '#2EC4B6'), (Brush '#FFD23F'), (Brush '#F25F8A'))

function Render([int]$pixels, [int]$width = 0, [int]$height = 0) {
    # Design space is 128 x 128; small sizes get thicker tiles and no inner rim so they stay readable.
    # A larger canvas (tiles) keeps the icon centred with transparent space around it.
    if ($width -eq 0) { $width = $pixels }; if ($height -eq 0) { $height = $pixels }
    $small = $pixels -le 24
    $visual = New-Object Windows.Media.DrawingVisual
    [Windows.Media.RenderOptions]::SetEdgeMode($visual, [Windows.Media.EdgeMode]::Unspecified)
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.TranslateTransform ([math]::Round(($width - $pixels) / 2)), ([math]::Round(($height - $pixels) / 2))))
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($pixels / 128.0), ($pixels / 128.0)))
    $dc.PushTransform((New-Object Windows.Media.TranslateTransform 5, 1))
    foreach ($back in @(@($teal, 10, 22, -16, 52, 64), @($orange, 20, 20, -7, 62, 62))) {
        $dc.PushTransform((New-Object Windows.Media.RotateTransform $back[3], $back[4], $back[5]))
        $dc.DrawRoundedRectangle($back[0], $null, (New-Object Windows.Rect $back[1], $back[2], 84, 84), 20, 20)
        $dc.Pop()
    }
    $dc.PushTransform((New-Object Windows.Media.TranslateTransform 30, 18))
    $dc.PushTransform((New-Object Windows.Media.RotateTransform 4, 42, 42))
    $dc.DrawRoundedRectangle($plate, $null, (New-Object Windows.Rect 0, 0, 84, 84), 20, 20)
    if ($small) {
        $cells = @(@(10, 10), @(46, 10), @(10, 46), @(46, 46)); $tile = 28; $radius = 6
    } else {
        $dc.DrawRoundedRectangle($rim, $null, (New-Object Windows.Rect 6, 6, 72, 72), 15, 15)
        $cells = @(@(15, 15), @(45, 15), @(15, 45), @(45, 45)); $tile = 24; $radius = 7
    }
    for ($i = 0; $i -lt 4; $i++) {
        $dc.DrawRoundedRectangle($tiles[$i], $null, (New-Object Windows.Rect $cells[$i][0], $cells[$i][1], $tile, $tile), $radius, $radius)
    }
    $dc.Pop(); $dc.Pop(); $dc.Pop(); $dc.Pop(); $dc.Pop(); $dc.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $width, $height, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual); $bitmap.Freeze(); $bitmap
}

function Png($bitmap) {
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream; $encoder.Save($stream); $stream.ToArray()
}

$frames = foreach ($size in $sizes) { , (Png (Render $size)) }
$path = [IO.Path]::GetFullPath($Ico)
$file = [IO.File]::Create($path)
$writer = New-Object IO.BinaryWriter $file
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }
"wrote $path ($((Get-Item $path).Length) bytes, $($sizes.Count) sizes)"
# WPF's Image takes the smallest ICO frame and blows it up, so the editor header uses this 256 px PNG instead.
$png = [IO.Path]::ChangeExtension($path, ".png")
[IO.File]::WriteAllBytes($png, $frames[[array]::IndexOf($sizes, 256)])
"wrote $png"

if ($Preview) {
    # Every size at 1:1 on a light and a dark strip, as the icon appears in Explorer and on the taskbar.
    $visual = New-Object Windows.Media.DrawingVisual; $dc = $visual.RenderOpen()
    $dc.DrawRectangle((Brush '#F3F3F3'), $null, (New-Object Windows.Rect 0, 0, 720, 300))
    $dc.DrawRectangle((Brush '#202020'), $null, (New-Object Windows.Rect 0, 300, 720, 300))
    foreach ($row in 0, 300) {
        $x = 12
        foreach ($size in ($sizes | Sort-Object)) {
            $dc.DrawImage((Render $size), (New-Object Windows.Rect $x, ($row + 150 - $size / 2), $size, $size)); $x += $size + 12
        }
    }
    $dc.Close()
    $sheet = New-Object Windows.Media.Imaging.RenderTargetBitmap 720, 600, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($visual)
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Preview), (Png $sheet))
    "preview $Preview"
}

if ($LogoPng) {
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($LogoPng), (Png (Render $LogoSize)))
    "logo $LogoPng"
}

if ($StoreAssets) {
    # Names follow the MRT qualifiers the package's resources.pri is built from (see build.ps1 -Msix).
    $folder = [IO.Path]::GetFullPath($StoreAssets)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    function Asset([string]$name, [int]$pixels, [int]$width = 0, [int]$height = 0) {
        [IO.File]::WriteAllBytes((Join-Path $folder $name), (Png (Render $pixels $width $height)))
    }
    foreach ($scale in 100, 125, 150, 200, 400) {
        $f = $scale / 100.0
        Asset "Square44x44Logo.scale-$scale.png" ([math]::Round(44 * $f))
        Asset "StoreLogo.scale-$scale.png" ([math]::Round(50 * $f))
        Asset "Square71x71Logo.scale-$scale.png" ([math]::Round(48 * $f)) ([math]::Round(71 * $f)) ([math]::Round(71 * $f))
        Asset "Square150x150Logo.scale-$scale.png" ([math]::Round(96 * $f)) ([math]::Round(150 * $f)) ([math]::Round(150 * $f))
        Asset "Wide310x150Logo.scale-$scale.png" ([math]::Round(96 * $f)) ([math]::Round(310 * $f)) ([math]::Round(150 * $f))
        Asset "Square310x310Logo.scale-$scale.png" ([math]::Round(160 * $f)) ([math]::Round(310 * $f)) ([math]::Round(310 * $f))
    }
    # The taskbar, Start and Alt+Tab take these exact sizes; the unplated ones are drawn without a tile background.
    foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        foreach ($form in '', '_altform-unplated', '_altform-lightunplated') { Asset "Square44x44Logo.targetsize-$size$form.png" $size }
    }
    "store assets $folder"
}
