"""Original desktop wallpaper for Store captures: soft light in the app icon's colors over a dark base.

The bottom edge stays calm and even, so the translucent taskbar over it has one uniform tone.
Usage: python make_wallpaper.py OUT.png [WIDTH HEIGHT]
"""
import sys

import numpy as np
from PIL import Image

# (x, y, radius, color, strength) in fractions of the picture; colors are those of the "fan" icon.
LIGHTS = [
    (0.18, 0.22, 0.42, (91, 95, 199), 0.95),   # indigo plate
    (0.78, 0.18, 0.36, (46, 196, 182), 0.55),  # teal plate
    (0.88, 0.62, 0.34, (255, 138, 61), 0.42),  # orange plate
    (0.42, 0.62, 0.30, (242, 95, 138), 0.30),  # pink tile
    (0.58, 0.30, 0.26, (115, 118, 214), 0.50),  # rim
]


def render(width, height, seed=7):
    y, x = np.mgrid[0:height, 0:width].astype(np.float32)
    x /= width
    y /= height
    aspect = width / height
    base_top = np.array([22, 20, 52], np.float32)
    base_bottom = np.array([12, 11, 28], np.float32)
    image = base_top[None, None] * (1 - y[..., None]) + base_bottom[None, None] * y[..., None]
    for cx, cy, radius, color, strength in LIGHTS:
        d2 = ((x - cx) * aspect) ** 2 + (y - cy) ** 2
        glow = np.exp(-d2 / (2 * (radius * 0.55) ** 2)) * strength
        image += (np.array(color, np.float32)[None, None] - image) * glow[..., None] * 0.85
    # Keep the taskbar strip even: fade everything towards the dark base near the bottom.
    calm = np.clip((y - 0.80) / 0.15, 0, 1)[..., None]
    image = image * (1 - calm) + base_bottom[None, None] * calm
    # A faint grain prevents banding in the smooth gradients after JPEG/H.264 compression.
    rng = np.random.default_rng(seed)
    image += rng.normal(0, 1.2, image.shape).astype(np.float32)
    return Image.fromarray(np.clip(image, 0, 255).astype(np.uint8), "RGB")


if __name__ == "__main__":
    out = sys.argv[1]
    width, height = (int(sys.argv[2]), int(sys.argv[3])) if len(sys.argv) > 3 else (1920, 1080)
    render(width, height).save(out)
    print(out)
