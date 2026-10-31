# Builds the promo video for one language: films the folder menus, then cuts the edit with its music.
#   .\tools\store-media\promo\make.ps1 -Language en-US -Ffmpeg 'C:\ffmpeg\bin\ffmpeg.exe'
# It reuses the Store trailer's capture of the same language (..\make.ps1 must have run first).
# -SkipCapture reuses the last menus (to change the edit without filming again).
param(
    [string]$Language = 'en-US',
    [string]$Ffmpeg = '',
    [string]$Codec = 'h264_nvenc',
    [switch]$SkipCapture
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not (Test-Path (Join-Path $PSScriptRoot "text\$Language.json"))) { throw "No promo texts for $Language. Copy text\en-US.json to text\$Language.json and translate it." }
if (-not (Test-Path (Join-Path $root "artifacts\store-media\$Language\raw\screen.mp4"))) { throw "Run ..\make.ps1 -Language $Language first: the promo reuses its recording." }
if (-not $Ffmpeg) { $found = Get-Command ffmpeg -ErrorAction SilentlyContinue; if ($found) { $Ffmpeg = $found.Source } }
if (-not $Ffmpeg) { throw 'ffmpeg was not found. Pass -Ffmpeg or put ffmpeg on PATH.' }
$env:APPSHELF_VIDEO_CODEC = $Codec
if (-not $SkipCapture) { & (Join-Path $PSScriptRoot 'capture.ps1') -Language $Language }
& python (Join-Path $PSScriptRoot 'edit.py') $Language $Ffmpeg
if ($LASTEXITCODE -ne 0) { throw 'edit.py failed.' }
