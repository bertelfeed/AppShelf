# Promo video

A 21-second promo with a fast, slow, fast pace. It is the trailer uploaded to the Store; the calmer video from `../trailer.py` is kept but not uploaded.

```powershell
.\tools\store-media\make.ps1 -Language en-US -Ffmpeg 'C:\path\to\ffmpeg.exe'        # everything, the promo included
.\tools\store-media\promo\make.ps1 -Language en-US -Ffmpeg 'C:\path\to\ffmpeg.exe'  # only the promo, from the last Store capture
```

Output: `artifacts/store-media/<language>/promo/promo-1920x1080.mp4`.

| Part | Pace | Picture |
| --- | --- | --- |
| Intro | an eighth note per step | the icon's tiles pop in, rearrange and become the logo |
| Words | a quarter note per word | "Rows. Columns. Colors. Sizes." with tiles doing just that |
| Montage | eighths, slowing to readable | real folder menus with other rows, columns, colours, sizes and themes |
| Calm | real time | moments of the Store recording: a taskbar folder opens, then its nested folder, then a long folder turns its page |
| Rush | quarter notes | taskbar, desktop, nested folder, folder settings |
| Ending | | logo, name, tagline |

- `variants.json` lists the menus of the montage. `capture.ps1` builds one folder per entry and films each menu once (about 30 s of screen takeover, restored at the end; after a crash use `capture.ps1 -RestoreOnly`).
- `edit.py` cuts on a 112.5 BPM grid, where an eighth note is exactly 8 frames. The music is written from the same timeline, so each cut, pop and wipe has its own sound. App footage stays 1:1.
- `-SkipCapture` reuses the last menus. For another language, copy `text/en-US.json` to `text/<language>.json` and translate it.
