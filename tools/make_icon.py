"""Draws the Junction mark (same shapes as src/Tfmm.App/Views/Logo.axaml) to PNG + ICO.

The mark: a J drawn as a transit line (top arm, rounded corner, stem, hook) with a terminus
station (a ring a bit wider than the line, like on a route map) at each end. Ink on an orange
tile, 64-unit grid. The icon has no train; that only runs in the app while it loads.

Usage: python3 tools/make_icon.py   (needs Pillow). Writes src/Tfmm.App/Assets/junction.ico and junction-256.png.
"""
import os
from PIL import Image, ImageDraw

ACCENT = (0xFF, 0x8A, 0x3D, 255)
INK = (0x0B, 0x0D, 0x10, 255)
SS = 8  # supersampling for smooth edges

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "Tfmm.App", "Assets")


def draw(size):
    big = size * SS
    k = big / 64
    im = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, big - 1, big - 1), radius=15 * k, fill=ACCENT)

    def P(x, y):
        return (x * k, y * k)

    def seg(x1, y1, x2, y2, w):
        # a stroke with round caps = rounded rectangle around the segment (axis-aligned only)
        h = w / 2
        d.rounded_rectangle((*P(min(x1, x2) - h, min(y1, y2) - h), *P(max(x1, x2) + h, max(y1, y2) + h)), radius=h * k, fill=INK)

    def circle(cx, cy, r, fill):
        d.ellipse((*P(cx - r, cy - r), *P(cx + r, cy + r)), fill=fill)

    def arc(cx, cy, r, start, end, w):
        # PIL angles: 0 = east, clockwise (y points down), stroke grows inward from the box
        o = r + w / 2
        d.arc((*P(cx - o, cy - o), *P(cx + o, cy + o)), start, end, fill=INK, width=round(w * k))

    seg(33, 15, 39, 15, 10)       # top arm
    arc(39, 21, 6, 270, 360, 10)  # corner
    seg(45, 21, 45, 37, 10)       # stem
    arc(32, 37, 13, 0, 180, 10)   # hook
    for cx, cy in ((33, 15), (19, 37)):  # terminus stations: ring a bit wider than the line
        circle(cx, cy, 6.5, INK)
        circle(cx, cy, 2.9, ACCENT)
    return im.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    big = draw(256)
    big.save(os.path.join(OUT, "junction-256.png"))
    big.save(os.path.join(OUT, "junction.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("wrote", OUT)
