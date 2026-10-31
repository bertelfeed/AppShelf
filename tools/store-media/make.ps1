# Builds every Store image and the trailer for one language: build, capture, compose, cut.
#   .\tools\store-media\make.ps1 -Language en-US -Ffmpeg 'C:\ffmpeg\bin\ffmpeg.exe'
# -SkipCapture reuses the last capture (to change texts or layout without filming again).
# -Music uses your own track instead of the synthesized one (music.py); it is faded to the trailer's length.
param(
    [string]$Language = 'en-US',
    [string]$Ffmpeg = '',
    [string]$Codec = 'h264_nvenc',
    [string]$Music = '',
    [switch]$SkipCapture
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Test-Path (Join-Path $PSScriptRoot "text\$Language.json"))) { throw "No texts for $Language. Copy text\en-US.json to text\$Language.json and translate it." }
if (-not $Ffmpeg) { $found = Get-Command ffmpeg -ErrorAction SilentlyContinue; if ($found) { $Ffmpeg = $found.Source } }
if (-not $Ffmpeg) { Write-Warning 'ffmpeg was not found: images only, no trailer. Pass -Ffmpeg or put ffmpeg on PATH.' }
$env:APPSHELF_VIDEO_CODEC = $Codec
if ($Music) { $env:APPSHELF_MUSIC = (Resolve-Path $Music).Path } else { Remove-Item Env:APPSHELF_MUSIC -ErrorAction SilentlyContinue }

$media = Join-Path $root 'artifacts\store-media'
New-Item -ItemType Directory -Path $media -Force | Out-Null
& (Join-Path $root 'tools\make-app-icon.ps1') -Ico (Join-Path $media 'icon-check.ico') -LogoPng (Join-Path $media 'logo-1024.png') -LogoSize 1024 | Out-Null
if (-not $SkipCapture) {
    & (Join-Path $root 'build.ps1')
    & (Join-Path $PSScriptRoot 'capture.ps1') -Language $Language -Ffmpeg $Ffmpeg -Codec $Codec
}
& python (Join-Path $PSScriptRoot 'compose.py') $Language
if ($LASTEXITCODE -ne 0) { throw 'compose.py failed.' }
if ($Ffmpeg -and (Test-Path (Join-Path $media "$Language\raw\screen.mp4"))) {
    & python (Join-Path $PSScriptRoot 'trailer.py') $Language $Ffmpeg
    if ($LASTEXITCODE -ne 0) { throw 'trailer.py failed.' }
    # The promo is the trailer uploaded to the Store; the video above is kept but not uploaded.
    & (Join-Path $PSScriptRoot 'promo\make.ps1') -Language $Language -Ffmpeg $Ffmpeg -Codec $Codec -SkipCapture:$SkipCapture
}
Write-Host "Store media: $(Join-Path $media $Language)"
