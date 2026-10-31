"""Cuts the AppShelf promo for one language: fast motion graphics, a calm look at the real app, fast again, logo.

The pace follows a beat (112.5 BPM: an eighth note is exactly 8 frames), and the music is written from the same
timeline, so every cut lands on a hit. App footage is shown 1:1, never enlarged: the menus from capture.ps1 (this
folder) and the taskbar recording and stills from the Store trailer's capture (../capture.ps1).
Usage: python tools/store-media/promo/edit.py en-US path\\to\\ffmpeg.exe
"""
import json
import math
import os
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import compose  # noqa: E402
import music  # noqa: E402
import trailer  # noqa: E402
from compose import H, MEDIA, MUTED, TEXT, W, font, read_json  # noqa: E402

FPS = 30
E = 8  # frames per eighth note
BG = (11, 10, 20)
# The app icon's colours.
ORANGE, TEAL, YELLOW, PINK = (255, 138, 61), (46, 196, 182), (255, 210, 63), (242, 95, 138)
PURPLE, PURPLE_DARK = (115, 118, 214), (91, 95, 199)
BRAND = [ORANGE, TEAL, YELLOW, PINK, PURPLE]
# Folder plate colours offered by the app.
PALETTE = [(0, 103, 192), (0, 153, 188), (0, 178, 148), (16, 137, 62), (255, 185, 0), (247, 99, 12),
           (232, 17, 35), (227, 0, 140), (135, 100, 184)]
MONTAGE = [E] * 8 + [2 * E] * 3 + [3 * E]  # frames per menu: fast, then slow enough to read


def clamp(v, lo=0.0, hi=1.0):
    return max(lo, min(hi, v))


def ease_out(t):
    return 1 - (1 - clamp(t)) ** 3


def ease_back(t, s=1.9):
    t = clamp(t) - 1
    return 1 + (s + 1) * t ** 3 + s * t ** 2


def tiles_layer(tiles, size=(W, H), ss=2):
    """Rounded squares (cx, cy, side, colour, alpha), drawn at twice the size and reduced for smooth edges."""
    layer = Image.new("RGBA", (size[0] * ss, size[1] * ss), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    for cx, cy, side, color, alpha in tiles:
        if side <= 1 or alpha <= 0:
            continue
        h = side * ss / 2
        draw.rounded_rectangle([cx * ss - h, cy * ss - h, cx * ss + h, cy * ss + h], radius=side * ss * 0.26,
                               fill=tuple(color) + (round(255 * alpha),))
    return layer.resize(size, Image.LANCZOS)


def grid(n, cols, side, gap, center):
    rows = math.ceil(n / cols)
    w, h = cols * side + (cols - 1) * gap, rows * side + (rows - 1) * gap
    x0, y0 = center[0] - w / 2 + side / 2, center[1] - h / 2 + side / 2
    return [(x0 + (i % cols) * (side + gap), y0 + (i // cols) * (side + gap)) for i in range(n)]


class Promo:
    def __init__(self, language, ffmpeg):
        self.ffmpeg = ffmpeg
        self.text = read_json(os.path.join(compose.HERE, "text", language + ".json"))
        self.promo = read_json(os.path.join(HERE, "text", language + ".json"))
        self.capture = compose.Capture(language)
        self.raw = os.path.join(MEDIA, language, "promo", "raw")
        self.variants = read_json(os.path.join(self.raw, "variants.json"))["variants"]
        self.out = os.path.join(MEDIA, language, "promo", "promo-1920x1080.mp4")
        self.silent = os.path.join(self.raw, "promo-video.mp4")
        self.background = compose.background(W, H, seed=5)
        self.frames, self.events, self.sections, self.last = 0, [], {}, None
        self.encoder = subprocess.Popen(
            [ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
             "-r", str(FPS), "-i", "-", *trailer.codec_options(), "-pix_fmt", "yuv420p", self.silent],
            stdin=subprocess.PIPE)

    def emit(self, image):
        image = image.convert("RGB")
        self.encoder.stdin.write(image.tobytes())
        self.last = image
        self.frames += 1

    def hit(self, kind, **params):
        self.events.append((self.frames / FPS, kind, params))

    def section(self, name):
        self.sections[name] = self.frames / FPS

    def dark(self):
        return Image.new("RGB", (W, H), BG)

    # ----- A: the icon's tiles pop in and rearrange, then become the logo. -------------------------------------
    def intro(self):
        self.section("intro")
        center, side, gap = (W / 2, H / 2), 128, 26
        layouts = [grid(1, 1, side, gap, center), grid(2, 2, side, gap, center), grid(3, 3, side, gap, center),
                   grid(4, 4, side, gap, center), grid(4, 1, side, gap, center), grid(4, 2, side, gap, center),
                   grid(4, 2, 112, 18, center)]
        colors = [ORANGE, TEAL, YELLOW, PINK]
        previous = [layouts[0][0]] * 4
        logo_size = 470
        mark = compose.logo(logo_size)
        for k in range(8):
            if k < 4:
                self.hit("pop", step=k)
            elif k < 6:
                self.hit("move", step=k)
            elif k == 6:
                self.hit("gather")
            else:
                self.hit("logo")
            target = layouts[min(k, 6)]
            for f in range(E):
                p = ease_out(f / 5)
                canvas = self.dark()
                if k < 7:
                    tiles = []
                    if k == 6:  # the purple plate of the icon grows behind the tiles
                        plate = 112 * 2 + 18 + 56
                        tiles.append((center[0], center[1], plate * ease_back(f / 6), PURPLE, 1))
                    for i in range(4):
                        if i > k:
                            continue
                        start = previous[i] if i < k or k >= 4 else target[i]
                        x = start[0] + (target[i][0] - start[0]) * p
                        y = start[1] + (target[i][1] - start[1]) * p
                        s = side if k < 6 else side + (112 - side) * p
                        if i == k and k < 4:
                            s *= ease_back(f / 6)
                        tiles.append((x, y, s, colors[i], 1))
                    layer = tiles_layer(tiles)
                    canvas.paste(layer, (0, 0), layer)
                else:  # the real icon, with a small punch
                    size = round(logo_size * (1.1 - 0.1 * ease_out(f / 6)))
                    m = mark.resize((size, size), Image.LANCZOS)
                    canvas.paste(m, ((W - size) // 2, (H - size) // 2), m)
                self.emit(canvas)
            if k < 7:
                previous = [(target[i][0], target[i][1]) if i <= k else previous[i] for i in range(4)]

    # ----- B: words, each with tiles that do what it says. --------------------------------------------------
    def words(self):
        self.section("words")
        word_font = font(150)
        for w, word in enumerate(self.promo["flash"]):
            self.hit("word", step=w)
            for f in range(2 * E):
                canvas = self.dark()
                tiles = []
                center, gap = (W / 2, 700), 12
                if w == 0:  # rows are added
                    rows = 1 + min(2, f // 5)
                    if f in (5, 10):
                        self.hit("tick", step=rows)
                    for i, (x, y) in enumerate(grid(4 * rows, 4, 52, gap, center)):
                        pop = ease_back((f - 5 * (i // 4)) / 4) if i // 4 else 1
                        tiles.append((x, y, 52 * pop, BRAND[i % 5], 1))
                elif w == 1:  # columns are added
                    cols = 2 + 2 * min(2, f // 5)
                    if f in (5, 10):
                        self.hit("tick", step=cols)
                    for i, (x, y) in enumerate(grid(2 * cols, cols, 52, gap, center)):
                        tiles.append((x, y, 52, BRAND[(i // 2) % 5], 1))
                elif w == 2:  # colours change
                    shift = f // 3
                    if f % 3 == 0 and f:
                        self.hit("tick", step=shift)
                    for i, (x, y) in enumerate(grid(6, 3, 64, 14, center)):
                        tiles.append((x, y, 64, PALETTE[(i + shift * 2) % len(PALETTE)], 1))
                else:  # sizes change
                    keys = [(0, 40), (5, 86), (10, 56), (15, 100)]
                    k = min(f // 5, 2)
                    t = ease_out((f - keys[k][0]) / 4)
                    side = keys[k][1] + (keys[k + 1][1] - keys[k][1]) * t
                    if f in (5, 10):
                        self.hit("tick", step=k)
                    for i, (x, y) in enumerate(grid(6, 3, side, side * 0.22, center)):
                        tiles.append((x, y, side, BRAND[i % 5], 1))
                layer = tiles_layer(tiles)
                canvas.paste(layer, (0, 0), layer)
                draw = ImageDraw.Draw(canvas)
                t = ease_out(f / 4)
                draw.text((W // 2, 430 + round(24 * (1 - t))), word, font=word_font,
                          fill=tuple(round(BG[c] + (TEXT[c] - BG[c]) * t) for c in range(3)), anchor="mm")
                self.emit(canvas)

    def stripes(self, before, after, frames=12):
        """Bars in the icon's colours rise over the old frame and uncover the new one."""
        self.hit("wipe", length=frames / FPS)
        old, new = np.asarray(before.convert("RGB")), np.asarray(after.convert("RGB"))
        bw = W // 5
        for f in range(frames):
            frame = np.empty_like(old)
            for i in range(5):
                q = clamp((f - i) / (frames - 5))
                y0, y1 = round(H * (1 - 2 * q)), round(H * (2 - 2 * q))
                xs = slice(i * bw, W if i == 4 else (i + 1) * bw)
                top, bottom = clamp(y0, 0, H), clamp(y1, 0, H)
                frame[:int(top), xs] = old[:int(top), xs]
                frame[int(top):int(bottom), xs] = BRAND[[4, 1, 0, 2, 3][i]]
                frame[int(bottom):, xs] = new[int(bottom):, xs]
            self.emit(Image.fromarray(frame))

    # ----- C: real menus, cut on the beat, slowing down until they can be read. ------------------------------
    def menu_frame(self, variant, offset=0):
        image = Image.open(os.path.join(self.raw, variant["name"] + ".png")).convert("RGB")
        card = image.crop(tuple(variant["rect"]))
        canvas = self.background.copy()
        cx, cy = 1210, H // 2
        compose.paste_card(canvas, card, (cx - card.width // 2 + offset, cy - card.height // 2), radius=8)
        settings, tags = variant["settings"], self.promo["tags"]
        tag = tags[variant["tag"]].format(columns=settings["Columns"], rows=settings["Rows"], scale=settings["DesktopIconScale"])
        draw = ImageDraw.Draw(canvas)
        y = H // 2 - 60
        for line in compose.wrap(draw, tag, font(104), 560):
            draw.text((150, y), line, font=font(104), fill=TEXT, anchor="lm")
            y += 120
        return canvas

    def montage(self):
        first = self.menu_frame(self.variants[0])
        self.stripes(self.last, first)
        self.section("montage")
        for i, (variant, frames) in enumerate(zip(self.variants, MONTAGE)):
            self.hit("cut", step=i, frames=frames)
            still = self.menu_frame(variant)
            for f in range(frames):
                self.emit(self.menu_frame(variant, [40, 14][f]) if f < 2 and i else still)

    # ----- D: the real app at its own pace. -----------------------------------------------------------------
    # Moments of the Store capture, as (still that gives the crop, caption, event, seconds from it to start and end).
    # The waits between them are left out: open a folder, open the nested one, open a long one, turn its page.
    CALM = [("taskbar", "taskbar", "taskbar", 1.0, 3.7),
            ("nested", "nested", "nested", 0.45, 2.95),
            ("pages", "pages", "pages", 1.65, 2.85),
            ("pages", "pages", "still:pages-1", 0.7, 1.6)]

    def calm(self):
        video = os.path.join(self.capture.raw, "screen.mp4")
        offset = trailer.video_offset(self.ffmpeg, video, self.capture)
        events = {e["name"]: e["t"] for e in self.capture.meta["events"]}
        top = self.capture.taskbar_top()
        self.section("calm")
        self.hit("calm")
        shown = None
        for still, caption_key, event, begin, finish in self.CALM:
            _, crop = compose.scene_image(self.capture, self.text, still)
            size = (crop[2] - crop[0], crop[3] - crop[1])
            xy = ((W - size[0]) // 2, H - size[1] - 50)
            base = self.background.copy()
            compose.paste_card(base, Image.new("RGB", size), xy)
            mask = compose.rounded_mask(size, 16)
            caption = self.text["trailer"]["scenes"][caption_key]
            fresh = caption != shown  # a caption fades in once, even over two pieces
            shown = caption
            before = self.last
            if before is not None and fresh:
                self.hit("tick", step=len(self.events) % 4)
            start = events[event] + begin
            for i, frame in enumerate(trailer.read_frames(self.ffmpeg, video, start + offset, finish - begin)):
                pixels = frame.copy()
                pixels[top:] = np.median(pixels[top:], axis=1, keepdims=True).astype(np.uint8)
                for r in self.capture.meta["taskbar"].values():
                    pixels[r[1]:r[3], r[0]:r[2]] = frame[r[1]:r[3], r[0]:r[2]]
                canvas = base.copy()
                canvas.paste(Image.fromarray(pixels).crop(tuple(crop)), xy, mask)
                t = ease_out((i - 6) / 14) if fresh else 1
                if t > 0:
                    draw = ImageDraw.Draw(canvas, "RGBA")
                    draw.text((W // 2, 140 - round(16 * (1 - t))), caption, font=font(60), fill=TEXT + (round(255 * t),), anchor="mm")
                if i < 6:
                    canvas = Image.blend(before, canvas, ease_out(i / 6))
                self.emit(canvas)

    # ----- E: fast again, every feature for a moment. -------------------------------------------------------
    def still_frame(self, name, word):
        image, crop = compose.scene_image(self.capture, self.text, name)
        card = image.crop(tuple(crop))
        canvas = self.background.copy()
        compose.paste_card(canvas, card, (W - card.width - 90, (H - card.height) // 2))
        draw = ImageDraw.Draw(canvas)
        lines = compose.wrap(draw, word, font(100), W - card.width - 300)
        y = H // 2 - (len(lines) - 1) * 58
        for line in lines:
            draw.text((140, y), line, font=font(100), fill=TEXT, anchor="lm")
            y += 116
        return canvas

    def rush(self):
        self.hit("riser_end")
        self.section("rush")
        before = self.last
        for f in range(E):  # a purple disc grows over the last frame
            canvas = before.copy()
            r = 1150 * ease_out(f / (E - 1)) ** 1.4
            draw = ImageDraw.Draw(canvas)
            draw.ellipse([W / 2 - r, H / 2 - r, W / 2 + r, H / 2 + r], fill=PURPLE_DARK)
            self.emit(canvas)
        for i, (name, word) in enumerate(zip(["taskbar", "desktop", "nested", "style"], self.promo["fast"])):
            self.hit("cut", step=8 + i, frames=2 * E)
            still = self.still_frame(name, word)
            for _ in range(2 * E):
                self.emit(still)

    # ----- F: logo and tagline. -----------------------------------------------------------------------------
    def outro(self, frames=4 * 2 * E):
        self.section("outro")
        self.hit("final")
        mark = compose.logo(240)
        for f in range(frames):
            canvas = self.dark()
            s = round(240 * (0.82 + 0.18 * ease_back(f / 10, 1.4)))
            m = mark.resize((s, s), Image.LANCZOS)
            canvas.paste(m, ((W - s) // 2, 330 - s // 2), m)
            draw = ImageDraw.Draw(canvas, "RGBA")
            for delay, y, text, fnt, color in ((4, 560, self.promo["name"], font(120), TEXT),
                                               (10, 670, self.promo["tagline"], font(52, "Regular Display"), TEXT),
                                               (16, 740, self.promo["subtitle"], font(36, "Regular Display"), MUTED)):
                t = ease_out((f - delay) / 8)
                if t > 0:
                    draw.text((W // 2, y + round(20 * (1 - t))), text, font=fnt, fill=color + (round(255 * t),), anchor="mm")
            fade = clamp((frames - f) / 12)
            self.emit(Image.blend(self.dark(), canvas, fade) if fade < 1 else canvas)

    def build(self):
        self.intro()
        self.words()
        self.montage()
        self.calm()
        self.rush()
        self.outro()
        self.encoder.stdin.close()
        self.encoder.wait()
        duration = self.frames / FPS
        wav = os.path.join(self.raw, "promo-music.wav")
        music.write_wav(wav, soundtrack(duration, self.events, self.sections))
        subprocess.run([self.ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", self.silent, "-i", wav,
                        "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-t", f"{duration:.3f}",
                        "-movflags", "+faststart", self.out], check=True)
        print(f"{duration:.1f} s, {self.out}")


# ----- Music, written from the same timeline. -------------------------------------------------------------------

BEAT = 2 * E / FPS
KEY = [62, 66, 69, 73, 74, 78, 81, 85]  # D major 7 upwards
PROGRESSION = [(38, [57, 61, 64, 66, 69]), (35, [54, 57, 61, 62, 66]), (31, [54, 57, 59, 62, 66]), (33, [57, 59, 61, 64, 69])]


def noise_band(seconds, rng, low=600, high=6000):
    n = int(seconds * music.RATE)
    spectrum = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / music.RATE)
    spectrum[(f < low) | (f > high)] = 0
    return np.fft.irfft(spectrum, n) / 3


def whoosh(seconds, rng, rise=True):
    n = int(seconds * music.RATE)
    shape = np.linspace(0, 1, n) ** 2 if rise else np.exp(-np.linspace(0, 6, n))
    return noise_band(seconds, rng) * shape


def impact(rng):
    t = np.arange(int(1.4 * music.RATE)) / music.RATE
    sub = np.sin(2 * np.pi * np.cumsum(40 + 60 * np.exp(-t * 12)) / music.RATE) * np.exp(-t * 2.6)
    burst = noise_band(1.4, rng, 200, 9000) * np.exp(-t * 9) * 1.5
    return sub * 0.9 + burst


def soundtrack(duration, events, sections):
    rng = np.random.default_rng(11)
    n = int(duration * music.RATE)
    tracks = {name: np.zeros((n, 2)) for name in ("pads", "plucks", "drums", "bass", "fx")}
    add = music.add
    calm, rush, outro = sections["calm"], sections["rush"], sections["outro"]

    # Beat-locked drums wherever the picture is fast; the calm part breathes without them.
    for i in range(int(duration / (BEAT / 2))):
        at = i * BEAT / 2
        busy = sections["words"] <= at < calm or rush <= at < outro
        if busy and i % 2 == 0:
            add(tracks["drums"], music.kick(), at, 0.36)
        if busy and i % 2 == 1:
            add(tracks["drums"], music.hat(rng), at, 0.2, pan=0.25)

    # Bass under everything but the intro; pads under the calm part and the ending.
    bar = 4 * BEAT
    for j, at in enumerate(np.arange(sections["words"], outro, bar)):
        root, voicing = PROGRESSION[j % len(PROGRESSION)]
        tt = np.arange(int(bar * music.RATE)) / music.RATE
        add(tracks["bass"], (np.sin(2 * np.pi * music.hz(root) * tt) + 0.35 * np.sin(4 * np.pi * music.hz(root) * tt)) *
            music.envelope(len(tt), 0.02, 0.3), at, 0.12)
    for j, at in enumerate(np.arange(calm, rush, 2 * bar)):
        for note in PROGRESSION[j % len(PROGRESSION)][1]:
            add(tracks["pads"], music.pad_note(note, min(2 * bar, rush - at) + 1.2, rng), at, 0.035)
    for j, at in enumerate(np.arange(calm, rush - BEAT, BEAT)):  # a slow arpeggio while the app is shown
        voicing = PROGRESSION[int((at - calm) // (2 * bar)) % len(PROGRESSION)][1]
        add(tracks["plucks"], music.pluck(voicing[[0, 2, 4, 3][j % 4]] + 12), at, 0.09, pan=0.3 if j % 2 else -0.3)
    for note in PROGRESSION[0][1]:
        add(tracks["pads"], music.pad_note(note, duration - outro + 1.2, rng), outro, 0.07)

    # One sound for every event the picture makes.
    for at, kind, p in events:
        if kind == "pop":
            add(tracks["plucks"], music.pluck(KEY[p["step"] * 2] + 12, 0.9), at, 0.3, pan=[-0.4, -0.1, 0.1, 0.4][p["step"]])
        elif kind == "move":
            add(tracks["plucks"], music.pluck(KEY[p["step"]], 0.6), at, 0.22)
            add(tracks["drums"], music.hat(rng), at, 0.3)
        elif kind == "gather":
            add(tracks["fx"], whoosh(BEAT / 2, rng), at, 0.6)
        elif kind == "logo":
            add(tracks["fx"], impact(rng), at, 0.55)
            for note in PROGRESSION[0][1]:
                add(tracks["plucks"], music.pluck(note + 12, 1.2), at, 0.12)
        elif kind == "word":
            add(tracks["plucks"], music.pluck(KEY[p["step"] * 2 % len(KEY)] + 12, 0.7), at, 0.22)
        elif kind == "tick":
            add(tracks["plucks"], music.pluck(KEY[(p["step"] + 3) % len(KEY)] + 24, 0.3), at, 0.1, pan=0.3)
        elif kind == "wipe":
            add(tracks["fx"], whoosh(p["length"], rng), at, 0.7)
            add(tracks["fx"], impact(rng), at + p["length"], 0.35)
        elif kind == "cut":
            add(tracks["plucks"], music.pluck(KEY[p["step"] % len(KEY)] + 12, 0.5), at, 0.17, pan=0.35 if p["step"] % 2 else -0.35)
        elif kind == "calm":
            add(tracks["fx"], whoosh(1.2, rng, rise=False), at, 0.5)
        elif kind == "riser_end":
            length = min(2.0, at - calm)
            add(tracks["fx"], whoosh(length, rng), at - length, 0.8)
            add(tracks["fx"], impact(rng), at, 0.6)
        elif kind == "final":
            add(tracks["fx"], impact(rng), at, 0.7)
            for note in PROGRESSION[0][1]:
                add(tracks["plucks"], music.pluck(note + 12, 2.0), at, 0.14)

    plucks = tracks["plucks"] + music.delay(tracks["plucks"], 3 * BEAT / 4, 0.28, 3)
    dry = tracks["pads"] + plucks + tracks["bass"] + tracks["drums"] + tracks["fx"]
    mix = dry + 0.28 * music.reverb(tracks["pads"] + plucks + 0.4 * tracks["fx"], 2.2, rng)
    tail = int(1.2 * music.RATE)
    mix[-tail:] *= np.linspace(1, 0, tail)[:, None] ** 1.5
    mix /= np.sqrt(np.mean(mix ** 2)) / 10 ** (-17 / 20)
    return np.tanh(mix * 1.1) / np.tanh(1.1)


if __name__ == "__main__":
    Promo(sys.argv[1] if len(sys.argv) > 1 else "en-US", sys.argv[2] if len(sys.argv) > 2 else "ffmpeg").build()
