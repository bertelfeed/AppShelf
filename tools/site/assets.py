"""Copies the Store media into site/assets for the website.

The pictures come from the same demo captures as the Store screenshots, without their background and captions
(the page has its own text), saved as WebP. They are cropped closer than the Store's 900 x 780 cards, so the menus
stay readable on a phone. The promo is re-encoded at 1280 px without sound, and the link previews get a 1200 x 630
JPEG. Run after tools/store-media/make.ps1 for both languages, with ffmpeg on PATH or in FFMPEG:

    python tools/site/assets.py
"""
import os
import subprocess
import sys

from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tools", "store-media"))
import compose  # noqa: E402

MEDIA = os.path.join(ROOT, "artifacts", "store-media")
SITE = os.path.join(ROOT, "site", "assets")
SHOTS = ["desktop", "taskbar", "nested", "pages", "style", "editor"]
LANGUAGES = {"en-US": "en", "ru-RU": "ru"}
WIDTH = 1200
MARGIN = 30  # Desktop around a window, so the page's rounded corners never cut into it.


def site_crop(capture, text, name):
    """The capture and a crop around just the part the feature text talks about."""
    image, crop = compose.scene_image(capture, text, name)
    _, info = capture.still("pages" if name == "pages" else name)
    sw, sh = capture.screen
    if name in ("taskbar", "nested", "pages"):
        # The menu and the folder buttons under it, down to the bottom of the screen.
        box = compose.union(capture.window(info), *capture.meta["taskbar"].values())
        return image, compose.frame(box, (640, 560), capture.screen, bottom=True)
    if name == "desktop":
        # The open menu and the desktop folder icons above it.
        menu = capture.window(info)
        _, ay = capture.meta["desktopAnchor"]
        return image, compose.frame(compose.union(menu, [0, ay - 270, 140, menu[3] + MARGIN]), (600, 600), capture.screen)
    if name == "editor":
        x0, y0, x1, y1 = capture.editor(info)
        return image, [max(0, x0 - MARGIN), max(0, y0 - MARGIN), min(sw, x1 + MARGIN), min(capture.taskbar_top(), y1 + MARGIN)]
    return image, crop


def ffmpeg():
    return os.environ.get("FFMPEG", "ffmpeg")


def main():
    for language, short in LANGUAGES.items():
        source = os.path.join(MEDIA, language)
        if not os.path.isdir(os.path.join(source, "raw")):
            sys.exit(f"{source} has no captures: run tools/store-media/make.ps1 -Language {language} first.")
        text = compose.read_json(os.path.join(compose.HERE, "text", language + ".json"))
        capture = compose.Capture(language)
        target = os.path.join(SITE, short)
        os.makedirs(target, exist_ok=True)
        for name in SHOTS:
            image, crop = site_crop(capture, text, name)
            card = image.crop(tuple(crop)).convert("RGB")
            if card.width > WIDTH:
                card = card.resize((WIDTH, round(card.height * WIDTH / card.width)), Image.LANCZOS)
            card.save(os.path.join(target, name + ".webp"), quality=88, method=6)
        hero = Image.open(os.path.join(source, "art", "hero-16x9-1920x1080.png")).convert("RGB")
        hero.resize((1280, 720), Image.LANCZOS).save(os.path.join(target, "promo.webp"), quality=84, method=6)
        share = hero.resize((1200, 675), Image.LANCZOS)
        share.crop((0, 22, 1200, 652)).save(os.path.join(target, "share.jpg"), quality=88, optimize=True)
        subprocess.run([ffmpeg(), "-v", "error", "-y", "-i", os.path.join(source, "promo", "promo-1920x1080.mp4"), "-an",
                        "-vf", "scale=1280:720:flags=lanczos", "-c:v", "libx264", "-preset", "veryslow", "-crf", "26",
                        "-profile:v", "high", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                        os.path.join(target, "promo.mp4")], check=True)
        print(target)
    with open(os.path.join(ROOT, "docs", "images", "appshelf.svg"), "rb") as icon, open(os.path.join(SITE, "appshelf.svg"), "wb") as copy:
        copy.write(icon.read())


if __name__ == "__main__":
    main()
