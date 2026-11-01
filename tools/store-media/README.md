# Store media pipeline

One command builds the Microsoft Store screenshots, artwork and videos for one language:

```powershell
.\tools\store-media\make.ps1 -Language en-US -Ffmpeg 'C:\path\to\ffmpeg.exe'
```

Output goes to `artifacts/store-media/<language>/`:

| File | Partner Center field |
| --- | --- |
| `screenshots/01-…06-*.png` (1920×1080) | Screenshots (desktop) |
| `art/box-art-1x1-2160.png` | 1:1 Box art |
| `art/poster-art-2x3-1440x2160.png` | 2:3 Poster art |
| `art/hero-16x9-1920x1080.png` | Trailer thumbnail, with the name and tagline |
| `art/super-hero-16x9-1920x1080.png` | 16:9 Super hero art, without the title as the Store requires |
| `promo/promo-1920x1080.mp4` | Trailer: the only video uploaded |
| `trailer-1920x1080.mp4` | Not uploaded: a calmer 43-second tour, kept for a website or GitHub |

## Steps

1. `capture.ps1` films the real app with a demonstration profile (`demo.json`, folder names from `text/<language>.json`).
   It takes over the screen for about a minute: minimizes windows, sets a generated wallpaper (`make_wallpaper.py`),
   hides desktop icons, pauses Wallpaper Engine and other AppShelf instances, then restores everything.
   Do not touch the mouse while it runs. After a crash: `capture.ps1 -RestoreOnly`.
2. `compose.py` lays out the screenshots and artwork. Captures are shown 1:1, never enlarged. The taskbar keeps
   only AppShelf's buttons, and desktop folder icons are drawn from the app's own icon files.
3. `trailer.py` cuts the screen recording into captioned scenes between a title and a closing card.
   The music is `music.py`'s own piece, synthesized from code, so it needs no licence. `-Music track.mp3` uses yours instead
   (it is faded in and out to fit; the ffmpeg build must be able to decode it).
4. `promo/make.ps1` films the folder menus once more and cuts the promo, the Store trailer (see `promo/README.md`).

`-SkipCapture` reuses the last capture to change texts or layout. `-Codec libx264` works without an NVIDIA GPU.

## Another language

Copy `text/en-US.json` to `text/<language>.json` (for example `ru-RU`), translate it, and run `make.ps1 -Language ru-RU`.
The demo profile then shows the app in that language. Requires Python with Pillow and NumPy, and ffmpeg with gdigrab.
