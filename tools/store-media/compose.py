"""Builds the Store screenshots and artwork for one language from capture.ps1's raw stills.

Real captures are shown at 1:1 (never enlarged), next to the localized headline from text/<language>.json.
Usage: python tools/store-media/compose.py en-US
"""
import json
import os
import sys
import textwrap

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, os.path.dirname(__file__))
import make_wallpaper  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
MEDIA = os.path.join(ROOT, "artifacts", "store-media")
FONT = os.environ.get("APPSHELF_FONT", r"C:\Windows\Fonts\SegUIVar.ttf")
W, H = 1920, 1080
TEXT = (255, 255, 255)
MUTED = (200, 198, 232)


def font(size, weight="Semibold Display"):
    f = ImageFont.truetype(FONT, size)
    f.set_variation_by_name(weight)
    return f


def read_json(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


# ----- Captures -------------------------------------------------------------------------------------------------

class Capture:
    def __init__(self, language):
        self.raw = os.path.join(MEDIA, language, "raw")
        self.meta = read_json(os.path.join(self.raw, "capture.json"))
        self.screen = tuple(self.meta["screen"])

    def still(self, name):
        return Image.open(os.path.join(self.raw, name + ".png")).convert("RGB"), read_json(os.path.join(self.raw, name + ".json"))

    def window(self, info, exclude=("AppShelf",)):
        """The largest on-screen window of the demo app other than the editor: the open menu or dialog."""
        sw, sh = self.screen
        found = [w for w in info["windows"] if w["title"] not in exclude and w["cls"].startswith("HwndWrapper")
                 and w["rect"][0] >= 0 and w["rect"][1] >= 0 and w["rect"][2] <= sw and w["rect"][2] - w["rect"][0] > 120]
        return max(found, key=lambda w: (w["rect"][2] - w["rect"][0]) * (w["rect"][3] - w["rect"][1]))["rect"] if found else None

    def editor(self, info):
        found = [w["rect"] for w in info["windows"] if w["title"] == "AppShelf" and w["rect"][0] >= 0]
        return found[0] if found else None

    def taskbar_top(self):
        return min(r[1] for r in self.meta["taskbar"].values())

    def clean_taskbar(self, image):
        """Keeps only AppShelf's folder buttons on the taskbar; other pinned and running apps are the capturing PC's."""
        top = self.taskbar_top()
        pixels = np.asarray(image).copy()
        strip = pixels[top:]
        # The taskbar over the calm bottom of the wallpaper is one tone per row; the median ignores the icons.
        pixels[top:] = np.median(strip, axis=1, keepdims=True).astype(np.uint8)
        cleaned = Image.fromarray(pixels)
        for rect in self.meta["taskbar"].values():
            cleaned.paste(image.crop(tuple(rect)), tuple(rect[:2]))
        return cleaned


def union(*rects):
    rects = [r for r in rects if r]
    return [min(r[0] for r in rects), min(r[1] for r in rects), max(r[2] for r in rects), max(r[3] for r in rects)]


def frame(bbox, size, screen, bottom=False):
    """A crop of `size` around bbox, inside the screen; bottom=True keeps the taskbar edge in view."""
    sw, sh = screen
    w, h = max(size[0], bbox[2] - bbox[0]), max(size[1], bbox[3] - bbox[1])
    if w / h > size[0] / size[1]:
        h = round(w * size[1] / size[0])
    else:
        w = round(h * size[0] / size[1])
    w, h = min(w, sw), min(h, sh)
    x = min(max(0, (bbox[0] + bbox[2]) // 2 - w // 2), sw - w)
    y = sh - h if bottom else min(max(0, (bbox[1] + bbox[3]) // 2 - h // 2), sh - h)
    return [x, y, x + w, y + h]


# ----- Drawing --------------------------------------------------------------------------------------------------

def background(w, h, seed=11):
    """The wallpaper's colors, darker, so the framed capture stands out from it."""
    image = np.asarray(make_wallpaper.render(w, h, seed)).astype(np.float32) * 0.62
    yy, xx = np.mgrid[0:h, 0:w]
    vignette = 1 - 0.35 * (((xx / w - 0.5) ** 2 + (yy / h - 0.5) ** 2) / 0.5)
    return Image.fromarray(np.clip(image * vignette[..., None], 0, 255).astype(np.uint8))


def rounded_mask(size, radius):
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius, fill=255)
    return mask


def paste_card(canvas, card, xy, radius=16):
    """A capture as a floating panel: soft shadow, rounded corners and a hairline edge."""
    x, y = xy
    shadow = Image.new("L", (card.width + 160, card.height + 160), 0)
    shadow.paste(rounded_mask(card.size, radius).point(lambda v: v * 0.55), (80, 100))
    shadow = shadow.filter(ImageFilter.GaussianBlur(34))
    canvas.paste((4, 3, 14), (x - 80, y - 80), shadow)
    canvas.paste(card, (x, y), rounded_mask(card.size, radius))
    edge = Image.new("RGBA", card.size, (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle([0, 0, card.width - 1, card.height - 1], radius, outline=(255, 255, 255, 46), width=1)
    canvas.paste(edge, (x, y), edge)


def wrap(draw, text, fnt, width):
    """Lines that fit `width`: one sentence per line when that works, so no word is left alone on a line."""
    if draw.textlength(text, font=fnt) > width:
        sentences = [s.strip() + "." for s in text.rstrip(".").split(". ")]
        if len(sentences) > 1 and all(draw.textlength(s, font=fnt) <= width for s in sentences):
            return sentences
    words, lines, line = text.split(), [], ""
    for word in words:
        trial = (line + " " + word).strip()
        if draw.textlength(trial, font=fnt) <= width or not line:
            line = trial
        else:
            lines.append(line)
            line = word
    lines.append(line)
    return lines


def text_block(canvas, x, y, width, title, subtitle, title_size=62, subtitle_size=30):
    draw = ImageDraw.Draw(canvas)
    tf, sf = font(title_size), font(subtitle_size, "Regular Display")
    for line in wrap(draw, title, tf, width):
        draw.text((x, y), line, font=tf, fill=TEXT)
        y += round(title_size * 1.18)
    y += 18
    for line in wrap(draw, subtitle, sf, width):
        draw.text((x, y), line, font=sf, fill=MUTED)
        y += round(subtitle_size * 1.45)
    return y


def block_height(title, subtitle, width, title_size=62, subtitle_size=30):
    scratch = ImageDraw.Draw(Image.new("RGB", (8, 8)))
    return (len(wrap(scratch, title, font(title_size), width)) * round(title_size * 1.18) + 18 +
            len(wrap(scratch, subtitle, font(subtitle_size, "Regular Display"), width)) * round(subtitle_size * 1.45))


def brand(canvas, x, y, size=46):
    mark = logo(size)
    canvas.paste(mark, (x, y), mark)
    ImageDraw.Draw(canvas).text((x + size + 14, y + size // 2), "AppShelf", font=font(round(size * 0.6)), fill=TEXT, anchor="lm")


def desktop_icon(image, folder_id, profile, center_x, top, label, selected=False):
    """Draws a folder's desktop icon the way Explorer does: its real artwork (48 px), the name under it."""
    icons = [f for f in os.listdir(os.path.join(profile, "folder-icons")) if f.startswith(folder_id.replace("-", ""))]
    if not icons:  # AppShelf writes artwork only for folders shown on the taskbar or desktop.
        return
    ico = Image.open(os.path.join(profile, "folder-icons", sorted(icons)[-1]))
    sizes = sorted(ico.info.get("sizes", {(ico.width, ico.height)}))
    ico.size = (48, 48) if (48, 48) in sizes else sizes[-1]
    art = ico.convert("RGBA")
    if art.width != 48:
        art = art.resize((48, 48), Image.LANCZOS)
    draw = ImageDraw.Draw(image, "RGBA")
    label_font = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 12)
    lines = textwrap.wrap(label, 12)[:2]
    if selected:
        draw.rounded_rectangle([center_x - 38, top - 4, center_x + 38, top + 52 + 16 * len(lines) + 4], 4,
                               fill=(255, 255, 255, 38), outline=(255, 255, 255, 64))
    image.paste(art, (center_x - 24, top), art)
    shadow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    for i, line in enumerate(lines):
        sd.text((center_x + 1, top + 54 + 16 * i + 1), line, font=label_font, fill=(0, 0, 0, 230), anchor="ma")
    image.paste(shadow.filter(ImageFilter.GaussianBlur(1.2)), (0, 0), shadow.filter(ImageFilter.GaussianBlur(1.2)))
    for i, line in enumerate(lines):
        draw.text((center_x, top + 54 + 16 * i), line, font=label_font, fill=(255, 255, 255), anchor="ma")


# ----- Scenes ---------------------------------------------------------------------------------------------------

CARD = (900, 780)
CARD_AREA = (770, 40, 1840, 1040)


def scene_image(capture, text, shot):
    """The 1:1 crop of the capture that illustrates one screenshot."""
    meta, folders = capture.meta, text["folders"]
    image, info = capture.still("pages" if shot == "pages" else shot)
    if shot in ("taskbar", "nested", "pages"):
        image = capture.clean_taskbar(image)
        return image, frame(union(capture.window(info), *meta["taskbar"].values()), CARD, capture.screen, bottom=True)
    if shot == "desktop":
        image = capture.clean_taskbar(image)
        menu = capture.window(info)
        ax, ay = meta["desktopAnchor"]
        original = image.copy()
        icons = [("create", ay - 250), ("games", ay - 150), ("work", ay - 50), ("chat", menu[3] + 40), ("utilities", menu[3] + 140)]
        for key, top in icons:
            desktop_icon(image, meta["ids"][key], meta["profile"], ax, top, folders[key], selected=key == "work")
        # The open menu stays above the drawn icons.
        size = (menu[2] - menu[0], menu[3] - menu[1])
        image.paste(original.crop(tuple(menu)), tuple(menu[:2]), rounded_mask(size, 8))
        return image, frame(union(menu, [0, ay - 270, 140, menu[3] + 230]), CARD, capture.screen)
    if shot == "editor":
        return image, frame(capture.editor(info), CARD, capture.screen)
    if shot == "style":
        # The editor with the folder's settings dialog over its right side, with a margin of desktop around both.
        box = union(capture.editor(info), capture.window(info))
        return image, [max(0, box[0] - 30), max(0, box[1] - 16), min(capture.screen[0], box[2] + 30), min(capture.taskbar_top(), box[3] + 16)]
    raise ValueError(shot)


def screenshot(capture, text, shot):
    image, crop = scene_image(capture, text, shot["id"])
    card = image.crop(tuple(crop))
    ax0, ay0, ax1, ay1 = CARD_AREA
    scale = min(1, (ax1 - ax0) / card.width, (ay1 - ay0) / card.height)
    if scale < 1:  # Only ever reduced, never enlarged.
        card = card.resize((round(card.width * scale), round(card.height * scale)), Image.LANCZOS)
    canvas = background(W, H)
    paste_card(canvas, card, (ax0 + (ax1 - ax0 - card.width) // 2, ay0 + (ay1 - ay0 - card.height) // 2))
    brand(canvas, 110, 76)
    width = 560
    height = block_height(shot["title"], shot["subtitle"], width)
    text_block(canvas, 110, (H - height) // 2 + 10, width, shot["title"], shot["subtitle"])
    return canvas


# ----- Artwork --------------------------------------------------------------------------------------------------

def logo(size):
    """The app icon centred by its visible shape (the icon itself sits slightly off-centre in its square)."""
    art = Image.open(os.path.join(MEDIA, "logo-1024.png")).convert("RGBA")
    art = art.crop(art.getbbox())
    side = max(art.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(art, ((side - art.width) // 2, (side - art.height) // 2))
    return square.resize((size, size), Image.LANCZOS)


def art_box(size=2160):
    canvas = background(size, size, seed=5)
    mark = logo(round(size * 0.56))
    canvas.paste(mark, ((size - mark.width) // 2, (size - mark.height) // 2), mark)
    return canvas


def art_poster(text, w=1440, h=2160):
    canvas = background(w, h, seed=5)
    mark = logo(round(w * 0.56))
    canvas.paste(mark, ((w - mark.width) // 2, round(h * 0.22)), mark)
    draw = ImageDraw.Draw(canvas)
    y = round(h * 0.22) + mark.height + 90
    draw.text((w // 2, y), "AppShelf", font=font(150), fill=TEXT, anchor="ma")
    y += 210
    for line in wrap(draw, text["tagline"], font(58, "Regular Display"), w - 260):
        draw.text((w // 2, y), line, font=font(58, "Regular Display"), fill=MUTED, anchor="ma")
        y += 80
    return canvas


def art_hero(capture, text):
    """16:9 hero image, also the trailer thumbnail: name and tagline beside the taskbar menu."""
    image, crop = scene_image(capture, text, "taskbar")
    card = image.crop(tuple(frame([crop[0], crop[1], crop[2], crop[3]], (900, 760), capture.screen, bottom=True)))
    canvas = background(W, H, seed=5)
    paste_card(canvas, card, (W - card.width - 110, (H - card.height) // 2))
    mark = logo(150)
    canvas.paste(mark, (110, 300), mark)
    draw = ImageDraw.Draw(canvas)
    draw.text((110, 480), "AppShelf", font=font(110), fill=TEXT)
    y = 630
    for line in wrap(draw, text["tagline"], font(40, "Regular Display"), 700):
        draw.text((112, y), line, font=font(40, "Regular Display"), fill=MUTED)
        y += 56
    return canvas


def main(language):
    text = read_json(os.path.join(HERE, "text", language + ".json"))
    capture = Capture(language)
    out = os.path.join(MEDIA, language)
    shots_dir, art_dir = os.path.join(out, "screenshots"), os.path.join(out, "art")
    os.makedirs(shots_dir, exist_ok=True)
    os.makedirs(art_dir, exist_ok=True)
    for i, shot in enumerate(text["screenshots"], 1):
        path = os.path.join(shots_dir, f"{i:02d}-{shot['id']}.png")
        screenshot(capture, text, shot).save(path, optimize=True)
        print(path)
    art_box().save(os.path.join(art_dir, "box-art-1x1-2160.png"), optimize=True)
    art_poster(text).save(os.path.join(art_dir, "poster-art-2x3-1440x2160.png"), optimize=True)
    art_hero(capture, text).save(os.path.join(art_dir, "hero-16x9-1920x1080.png"), optimize=True)
    print(art_dir)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "en-US")
