"""Stores the Microsoft Store badges and draws matching GitHub badges for the website.

The Store badge (get.microsoft.com, dark) is 44 px high with a #202020 plate, a 22 px icon and two
lines of Segoe UI outlined to paths. The GitHub badges repeat that layout with the GitHub mark, so both
buttons look like one set. The site serves its own copy of the Store badges, so a change on Microsoft's
side cannot break the pair. Run on Windows: python tools/site/badge.py
"""
import urllib.request
from pathlib import Path

from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "site" / "assets"
FONTS = Path(r"C:\Windows\Fonts")
HEIGHT, TEXT_X, RIGHT_PAD = 44, 38, 10.4
# The Store badge's outlines fitted against Segoe UI: caption 10 px on baseline 17, title Semibold 16 px on 34.
CAPTION = ("segoeui.ttf", 10, 17)
TITLE = ("seguisb.ttf", 16, 34)
# GitHub's mark (Octicons mark-github, 16 x 16).
MARK = ("M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 "
        "0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 "
        "0 0-.67-.22-2.2.82-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 "
        "1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55"
        "-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53.34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 "
        "1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z")
CAPTIONS = {"en": "Download from", "ru": "Скачать с"}
STORE = {"en": "en-us", "ru": "ru"}


def number(value):
    return f"{value:.2f}".rstrip("0").rstrip(".")


def outline(text, font_file, size, baseline):
    """Returns the text as one SVG path and the x where it ends."""
    font = TTFont(FONTS / font_file)
    glyphs, cmap = font.getGlyphSet(), font.getBestCmap()
    scale = size / font["head"].unitsPerEm
    pen, x = SVGPathPen(glyphs, ntos=number), TEXT_X
    for char in text:
        name = cmap[ord(char)]
        glyphs[name].draw(TransformPen(pen, (scale, 0, 0, -scale, x, baseline)))
        x += glyphs[name].width * scale
    return pen.getCommands(), x


def badge(language):
    caption, caption_end = outline(CAPTIONS[language], *CAPTION)
    title, title_end = outline("GitHub", *TITLE)
    width = round(max(caption_end, title_end) + RIGHT_PAD)
    return f"""<svg width="{width}" height="{HEIGHT}" viewBox="0 0 {width} {HEIGHT}" fill="none" xmlns="http://www.w3.org/2000/svg">
<rect x="1" y="1" width="{width - 2}" height="{HEIGHT - 2}" rx="4" fill="#202020"/>
<rect x="0.5" y="0.5" width="{width - 1}" height="{HEIGHT - 1}" rx="4.5" stroke="black" stroke-opacity="0.1"/>
<path transform="translate(10 11) scale(1.375)" fill="white" d="{MARK}"/>
<path fill="white" d="{caption}"/>
<path fill="white" d="{title}"/>
</svg>
"""


for language in CAPTIONS:
    (ASSETS / f"github-{language}.svg").write_text(badge(language), encoding="utf-8", newline="\n")
    with urllib.request.urlopen(f"https://get.microsoft.com/images/{STORE[language]}%20dark.svg") as response:
        (ASSETS / f"store-{language}.svg").write_bytes(response.read())
    print(language, "badges written")
