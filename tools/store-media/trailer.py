"""Cuts the Store trailer for one language from capture.ps1's screen recording.

Each scene is a 1:1 crop of the real recording (never enlarged) under a localized caption from text/<language>.json,
between a short title and closing card. The taskbar is cleaned and the desktop icons are drawn as in compose.py.
The music is music.py's original piece, or the track in APPSHELF_MUSIC (any file ffmpeg reads), faded to fit.
Usage: python tools/store-media/trailer.py en-US [path\\to\\ffmpeg.exe]
"""
import os
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(__file__))
import compose  # noqa: E402
import music  # noqa: E402
from compose import H, MEDIA, MUTED, TEXT, W, font, read_json  # noqa: E402

FPS = 30
FADE = 0.4
AREA = (110, 175, 1810, 1040)
SCENES = [  # (caption key, still that gives the crop, start event, end event)
    ("taskbar", "taskbar", "taskbar", "nested"),
    ("nested", "nested", "nested", "pages"),
    ("pages", "pages", "pages", "desktop"),
    ("desktop", "desktop", "desktop", "editor"),
    ("editor", "style", "editor", "style"),
    ("style", "style", "style", "end"),
]


def ffmpeg_path():
    if len(sys.argv) > 2:
        return sys.argv[2]
    return os.environ.get("FFMPEG", "ffmpeg")


def read_frames(ffmpeg, video, start, duration, size=(1920, 1080), fps=FPS, gray=False):
    """Decodes part of the recording frame by frame as numpy arrays."""
    w, h = size
    fmt, channels = ("gray", 1) if gray else ("rgb24", 3)
    cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-ss", f"{max(0, start):.3f}", "-t", f"{duration:.3f}", "-i", video,
           "-vf", f"fps={fps},scale={w}:{h}", "-f", "rawvideo", "-pix_fmt", fmt, "-"]
    process = subprocess.Popen(cmd, stdout=subprocess.PIPE)
    frame = w * h * channels
    try:
        while True:
            data = process.stdout.read(frame)
            if len(data) < frame:
                break
            array = np.frombuffer(data, np.uint8)
            yield array.reshape(h, w) if gray else array.reshape(h, w, 3)
    finally:
        process.stdout.close()
        process.wait()


def video_offset(ffmpeg, video, capture, stills=("taskbar", "pages-1", "desktop")):
    """Seconds between the capture clock and the recording, found by matching stills to frames."""
    offsets = []
    for name in stills:
        info = read_json(os.path.join(capture.raw, name + ".json"))
        target = np.asarray(Image.open(os.path.join(capture.raw, name + ".png")).convert("L").resize((192, 108)), np.float32)
        start = info["t"] - 0.5
        frames = list(read_frames(ffmpeg, video, start, 4.0, (192, 108), 15, gray=True))
        errors = [np.mean((f.astype(np.float32) - target) ** 2) for f in frames]
        offsets.append(start + int(np.argmin(errors)) / 15 - info["t"])
    return float(np.median(offsets))


def codec_options():
    """h264_nvenc needs an NVIDIA GPU; APPSHELF_VIDEO_CODEC=libx264 works with any full ffmpeg build."""
    codec = os.environ.get("APPSHELF_VIDEO_CODEC", "h264_nvenc")
    if codec == "h264_nvenc":
        return ["-c:v", codec, "-preset", "p7", "-rc", "vbr", "-cq", "18", "-b:v", "0"]
    return ["-c:v", codec, "-preset", "slow", "-crf", "18"]


def ease(t):
    return t * t * (3 - 2 * t)


class Trailer:
    def __init__(self, language, ffmpeg):
        self.language, self.ffmpeg = language, ffmpeg
        self.text = read_json(os.path.join(compose.HERE, "text", language + ".json"))
        self.capture = compose.Capture(language)
        self.video = os.path.join(self.capture.raw, "screen.mp4")
        self.events = {e["name"]: e["t"] for e in self.capture.meta["events"]}
        self.background = compose.background(W, H, seed=5)
        self.out = os.path.join(MEDIA, language, "trailer-1920x1080.mp4")
        self.silent = os.path.join(self.capture.raw, "trailer-video.mp4")
        self.frames = 0
        cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
               "-r", str(FPS), "-i", "-",
               *codec_options(), "-pix_fmt", "yuv420p",
               self.silent]
        self.encoder = subprocess.Popen(cmd, stdin=subprocess.PIPE)

    def emit(self, image):
        self.encoder.stdin.write(image.convert("RGB").tobytes())
        self.frames += 1

    def caption_layer(self, caption):
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        ImageDraw.Draw(layer).text((W // 2, 92), caption, font=font(56), fill=TEXT + (255,), anchor="mm")
        return layer

    def title_card(self, title, subtitle, seconds, logo_size=220):
        mark = compose.logo(logo_size)
        frames = round(seconds * FPS)
        for i in range(frames):
            t = i / FPS
            canvas = self.background.copy()
            appear = ease(min(1, t / 0.6))
            fade = ease(min(1, (seconds - t) / FADE))
            layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            size = round(logo_size * (0.85 + 0.15 * appear))
            m = mark.resize((size, size))
            layer.paste(m, ((W - size) // 2, 300 + (logo_size - size) // 2), m)
            draw = ImageDraw.Draw(layer)
            draw.text((W // 2, 620), title, font=font(76), fill=TEXT + (255,), anchor="mm")
            words = ease(min(1, max(0, (t - 0.5) / 0.6)))
            draw.text((W // 2, 705), subtitle, font=font(40, "Regular Display"), fill=MUTED + (round(255 * words),), anchor="mm")
            alpha = layer.getchannel("A").point(lambda v: round(v * appear * fade))
            canvas.paste(layer, (0, 0), alpha)
            self.emit(canvas)

    def scene(self, caption_key, still, start_event, end_event, offset):
        image, crop = compose.scene_image(self.capture, self.text, still)
        # Under the caption there is less room than on a screenshot: a taller crop loses its bottom, not its sharpness.
        crop = [crop[0], crop[1], crop[2], min(crop[3], crop[1] + AREA[3] - AREA[1])]
        cw, ch = crop[2] - crop[0], crop[3] - crop[1]
        scale = min(1, (AREA[2] - AREA[0]) / cw, (AREA[3] - AREA[1]) / ch)
        size = (round(cw * scale), round(ch * scale))
        xy = (AREA[0] + (AREA[2] - AREA[0] - size[0]) // 2, AREA[1] + (AREA[3] - AREA[1] - size[1]) // 2)
        base = self.background.copy()
        compose.paste_card(base, Image.new("RGB", size), xy)  # shadow and edge; the frame replaces the inside
        caption = self.caption_layer(self.text["trailer"]["scenes"][caption_key])
        mask = compose.rounded_mask(size, 16)
        overlay = self.desktop_overlay() if still == "desktop" else None
        start = self.events[start_event] - (0.3 if start_event == "taskbar" else 0)
        duration = self.events[end_event] - start
        top = self.capture.taskbar_top()
        for i, frame in enumerate(read_frames(self.ffmpeg, self.video, start + offset, duration)):
            t = i / FPS
            pixels = frame.copy()
            if still in ("taskbar", "nested", "pages", "desktop"):
                pixels[top:] = np.median(pixels[top:], axis=1, keepdims=True).astype(np.uint8)
                for r in self.capture.meta["taskbar"].values():
                    pixels[r[1]:r[3], r[0]:r[2]] = frame[r[1]:r[3], r[0]:r[2]]
            if overlay is not None:
                selected = start + t >= self.events["desktop-open"]
                pixels = self.draw_desktop(pixels, overlay[1] if selected else overlay[0])
            card = Image.fromarray(pixels).crop(tuple(crop))
            if scale < 1:
                card = card.resize(size, Image.LANCZOS)
            canvas = base.copy()
            canvas.paste(card, xy, mask)
            canvas.paste(caption, (0, 0), caption)
            fade = ease(min(1, t / FADE, (duration - t) / FADE))
            if fade < 1:
                canvas = Image.blend(self.background, canvas, fade)
            self.emit(canvas)

    def desktop_overlay(self):
        """The folder icons compose.py draws on the desktop, as layers without and with the clicked selection."""
        meta, folders = self.capture.meta, self.text["folders"]
        _, info = self.capture.still("desktop")
        menu = self.capture.window(info)
        ax, ay = meta["desktopAnchor"]
        icons = [("create", ay - 250), ("games", ay - 150), ("work", ay - 50), ("chat", menu[3] + 40), ("utilities", menu[3] + 140)]
        layers = []
        for selected in (False, True):
            layer = Image.new("RGBA", self.capture.screen, (0, 0, 0, 0))
            for key, y in icons:
                compose.desktop_icon(layer, meta["ids"][key], meta["profile"], ax, y, folders[key], selected=selected and key == "work")
            layers.append(np.asarray(layer).astype(np.float32))
        self.wallpaper = np.asarray(Image.open(os.path.join(MEDIA, "wallpaper.png")).convert("RGB")).astype(np.float32)
        return layers

    def draw_desktop(self, pixels, layer):
        # Icons go only where the frame shows bare wallpaper, so the pointer and the menu stay in front of them.
        bare = np.abs(pixels.astype(np.float32) - self.wallpaper).max(axis=2) < 28
        alpha = layer[..., 3:4] / 255 * bare[..., None]
        return (pixels * (1 - alpha) + layer[..., :3] * alpha).astype(np.uint8)

    def build(self):
        offset = video_offset(self.ffmpeg, self.video, self.capture)
        print(f"recording offset {offset:+.2f} s")
        trailer = self.text["trailer"]
        self.title_card(trailer["intro"], trailer["introSub"], 3.2)
        intro_end = self.frames / FPS
        for scene in SCENES:
            self.scene(*scene, offset)
        outro_start = self.frames / FPS
        self.title_card(trailer["outro"], trailer["outroSub"], 3.6)
        self.encoder.stdin.close()
        self.encoder.wait()
        self.add_music(self.frames / FPS, intro_end, outro_start)
        print(self.out)

    def add_music(self, duration, intro_end, outro_start):
        track = os.environ.get("APPSHELF_MUSIC")
        if track:  # someone else's track: faded in and out to the trailer's length
            audio = ["-i", track, "-filter_complex",
                     f"[1:a]afade=t=in:d=0.5,afade=t=out:st={duration - 2.5:.2f}:d=2.5[a]", "-map", "0:v", "-map", "[a]"]
        else:
            wav = os.path.join(self.capture.raw, "music.wav")
            music.write_wav(wav, music.render(duration, intro_end, outro_start))
            audio = ["-i", wav, "-map", "0:v", "-map", "1:a"]
        subprocess.run([self.ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", self.silent, *audio,
                        "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-t", f"{duration:.3f}", "-movflags", "+faststart", self.out],
                       check=True)


if __name__ == "__main__":
    Trailer(sys.argv[1] if len(sys.argv) > 1 else "en-US", ffmpeg_path()).build()
