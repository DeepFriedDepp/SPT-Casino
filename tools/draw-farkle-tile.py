"""Draws src/Casino.Client/assets/tile-farkle.png: the lobby tile for the fifth table.

Two dice, a 1 and a 5 -- the two faces that score on their own -- in the same
black-and-white, weathered style as the four tiles beside it. Those were hand-made;
this one is drawn, so it can be redrawn. Rendered at 4x and downsampled so the
outlines are smooth at 320x320, which is the size the other four are.

    python tools/draw-farkle-tile.py
"""
import math
import random
from PIL import Image, ImageDraw, ImageFilter

SIZE = 320
SCALE = 4
S = SIZE * SCALE

INK = (12, 12, 12, 255)
BONE = (222, 219, 211, 255)

random.seed(2026)


def pips(face, size):
    """Pip centres for a face, as fractions of the die's edge."""
    a, b, c = 0.26, 0.5, 0.74
    layout = {
        1: [(b, b)],
        2: [(a, a), (c, c)],
        3: [(a, a), (b, b), (c, c)],
        4: [(a, a), (c, a), (a, c), (c, c)],
        5: [(a, a), (c, a), (b, b), (a, c), (c, c)],
        6: [(a, a), (c, a), (a, b), (c, b), (a, c), (c, c)],
    }
    return [(x * size, y * size) for x, y in layout[face]]


def die(face, size, angle):
    """One die on its own transparent layer, rotated."""
    pad = size // 2
    layer = Image.new("RGBA", (size + pad * 2, size + pad * 2), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    radius = int(size * 0.20)
    outline = int(size * 0.075)
    box = [pad, pad, pad + size, pad + size]

    d.rounded_rectangle(box, radius=radius, fill=INK)
    inner = [v + outline * (1 if i < 2 else -1) for i, v in enumerate(box)]
    d.rounded_rectangle(inner, radius=radius - outline // 2, fill=BONE)

    # A thin dark rule inside the rim, the way the reference tiles double their edges.
    rule = int(size * 0.02)
    gap = int(size * 0.035)
    ring = [inner[0] + gap, inner[1] + gap, inner[2] - gap, inner[3] - gap]
    d.rounded_rectangle(ring, radius=max(2, radius - outline // 2 - gap), outline=INK, width=rule)

    r = size * (0.16 if face == 1 else 0.085)
    for x, y in pips(face, size):
        d.ellipse([pad + x - r, pad + y - r, pad + x + r, pad + y + r], fill=INK)

    return layer.rotate(angle, resample=Image.BICUBIC, expand=True)


def weather(img, specks, scratches):
    """Fine grain over everything, then faint specks and a few scratches."""
    w, h = img.size
    px = img.load()

    # Grain: Pillow's own noise, kept only where it darkens, only inside the tile.
    noise = Image.effect_noise((w, h), 26).point(lambda v: max(0, (v - 128) * 3))
    grain = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    grain.putalpha(Image.composite(noise, Image.new("L", (w, h), 0), img.getchannel("A")))
    img.alpha_composite(grain)

    d = ImageDraw.Draw(img)

    for _ in range(specks):
        x, y = random.randrange(w), random.randrange(h)
        r, g, b, a = px[x, y]
        if a < 200:
            continue
        rad = random.uniform(0.4, 2.2) * SCALE
        light = (r + g + b) / 3 > 128
        colour = (30, 30, 30, random.randint(35, 110)) if light else (200, 198, 190, random.randint(15, 45))
        d.ellipse([x - rad, y - rad, x + rad, y + rad], fill=colour)

    for _ in range(scratches):
        x, y = random.randrange(w), random.randrange(h)
        if px[x, y][3] < 200:
            continue
        length = random.uniform(20, 90) * SCALE
        theta = random.uniform(0, math.pi)
        x2, y2 = x + math.cos(theta) * length, y + math.sin(theta) * length
        light = sum(px[x, y][:3]) / 3 > 128
        colour = (30, 30, 30, random.randint(40, 100)) if light else (200, 198, 190, random.randint(25, 60))
        d.line([x, y, x2, y2], fill=colour, width=max(1, int(0.9 * SCALE)))

    return img


def main():
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    big = die(5, int(S * 0.46), -14)
    small = die(1, int(S * 0.40), 22)

    # The 5 sits back and left, the 1 forward and right, overlapping a little so they
    # read as a pair that has just landed rather than two icons side by side.
    def centred(layer, cx, cy):
        return (int(S * cx - layer.width / 2), int(S * cy - layer.height / 2))

    canvas.alpha_composite(big, centred(big, 0.38, 0.40))
    canvas.alpha_composite(small, centred(small, 0.66, 0.65))

    canvas = weather(canvas, specks=760, scratches=26)

    # Soften the speckle a touch so it reads as texture rather than dust.
    texture = canvas.filter(ImageFilter.GaussianBlur(0.8))
    out = texture.resize((SIZE, SIZE), Image.LANCZOS)

    # Keep the silhouette crisp: hard alpha, so the tile does not carry a grey halo.
    r, g, b, a = out.split()
    a = a.point(lambda v: 255 if v > 96 else (0 if v < 24 else v))
    out = Image.merge("RGBA", (r, g, b, a))

    out.save("src/Casino.Client/assets/tile-farkle.png")
    print("wrote src/Casino.Client/assets/tile-farkle.png", out.size)


if __name__ == "__main__":
    main()
