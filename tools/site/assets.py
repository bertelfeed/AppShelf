"""Copies the Store media into site/assets for the website.

The pictures are the same crops of the demo captures that the Store screenshots frame, without the screenshots'
background and captions (the page has its own text), saved as WebP. Run after tools/store-media/make.ps1 for both
languages:

    python tools/site/assets.py
"""
import os
import shutil
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
            image, crop = compose.scene_image(capture, text, name)
            card = image.crop(tuple(crop)).convert("RGB")
            if card.width > WIDTH:
                card = card.resize((WIDTH, round(card.height * WIDTH / card.width)), Image.LANCZOS)
            card.save(os.path.join(target, name + ".webp"), quality=88, method=6)
        poster = Image.open(os.path.join(source, "art", "hero-16x9-1920x1080.png")).convert("RGB")
        poster.resize((1280, 720), Image.LANCZOS).save(os.path.join(target, "promo.webp"), quality=84, method=6)
        shutil.copyfile(os.path.join(source, "promo", "promo-1920x1080.mp4"), os.path.join(target, "promo.mp4"))
        print(target)
    shutil.copyfile(os.path.join(ROOT, "docs", "images", "appshelf.svg"), os.path.join(SITE, "appshelf.svg"))


if __name__ == "__main__":
    main()
