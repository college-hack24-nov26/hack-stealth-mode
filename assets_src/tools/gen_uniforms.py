"""Generates uniform albedo textures by re-printing the MPFB male_casualsuit01 scan with camouflage.

The original photo-scan keeps its folds, seams and belt (high-pass luminance); colour comes from a
procedural pattern. Shoe islands (top-left of the atlas) become dark boot leather.
Outputs Stealth Mode/Assets/StealthMode/Textures/Characters/uniform_<name>.png

Run: python3 assets_src/tools/gen_uniforms.py
"""
import os
import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
MPFB = os.path.expanduser("~/Library/Application Support/Blender/5.2/extensions/.user/blender_org/mpfb/data")
SRC = os.path.join(MPFB, "clothes", "male_casualsuit01", "male_casualsuit01_diffuse.png")
OUT = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Textures", "Characters")
N = 2048
rng = np.random.default_rng(7)


def srgb(c):
    return np.array(c, np.float32) / 255.0


def fbm(scale, octaves=4, seed=0):
    """Smooth value noise in 0..1, tileable enough for clothing atlases."""
    r = np.random.default_rng(seed)
    acc = np.zeros((N, N), np.float32)
    amp, tot = 1.0, 0.0
    for o in range(octaves):
        g = max(2, int(scale * 2 ** o))
        small = r.random((g, g)).astype(np.float32)
        im = Image.fromarray((small * 255).astype(np.uint8)).resize((N, N), Image.BICUBIC)
        acc += amp * np.asarray(im, np.float32) / 255.0
        tot += amp
        amp *= 0.5
    return acc / tot


def digital(field, block):
    """Pixelate a noise field into square blocks (digital camo look)."""
    small = Image.fromarray((field * 255).astype(np.uint8)).resize((N // block, N // block), Image.BILINEAR)
    return np.asarray(small.resize((N, N), Image.NEAREST), np.float32) / 255.0


def india_pattern():
    # Indian Army style digital disruptive: khaki base, olive, earth brown, dark green-grey
    cols = [srgb((96, 100, 66)), srgb((146, 132, 98)), srgb((96, 74, 52)), srgb((52, 58, 44))]
    a = digital(fbm(16, 3, 1), 5)
    b = digital(fbm(22, 3, 2), 5)
    c = digital(fbm(34, 2, 3), 5)
    img = np.empty((N, N, 3), np.float32)
    img[:] = cols[0]
    img[a > 0.56] = cols[1]
    img[(b > 0.56) & (a > 0.42)] = cols[2]
    img[c > 0.62] = cols[3]
    return img


def enemy_pattern():
    # generic arid woodland: sand base with organic olive/brown blobs and black brush strokes
    cols = [srgb((140, 122, 90)), srgb((98, 92, 64)), srgb((110, 82, 58)), srgb((40, 36, 32))]
    a = fbm(11, 3, 11)
    b = fbm(13, 3, 12)
    c = fbm(36, 2, 13)
    img = np.empty((N, N, 3), np.float32)
    img[:] = cols[0]
    img[a > 0.53] = cols[1]
    img[b > 0.58] = cols[2]
    img[(c > 0.66) & (a < 0.6)] = cols[3]
    return img


def make(name, pattern, wear=0.06):
    src = Image.open(SRC).convert("RGBA")
    rgb = np.asarray(src.convert("RGB"), np.float32) / 255.0
    alpha = np.asarray(src, np.float32)[..., 3:] / 255.0
    lum = rgb @ np.array([0.299, 0.587, 0.114], np.float32)
    blur = np.asarray(Image.fromarray((lum * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(5)), np.float32) / 255.0
    detail = np.clip(lum / np.maximum(blur, 0.05), 0.6, 1.35)          # folds, seams, stitching only (no denim fades)
    big = np.asarray(Image.fromarray((lum * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(40)), np.float32) / 255.0
    shade = np.clip(big / max(float(np.median(big[lum > 0.05])), 1e-3), 0.8, 1.15)  # broad AO only
    out = pattern * (detail ** 0.9)[..., None] * (shade ** 0.5)[..., None]
    # faded edges / dust
    dust = fbm(40, 2, 21)[..., None]
    out = out * (1 - wear) + srgb((150, 138, 112)) * wear * dust * 2
    # shoe islands (top-left region with brown leather) -> dark boot leather keeping the scan detail
    yy, xx = np.mgrid[0:N, 0:N] / N
    shoes = (xx < 0.31) & (yy < 0.31) & (rgb[..., 0] > rgb[..., 2] * 0.9)
    out[shoes] = (srgb((34, 30, 27)) * np.clip(lum / 0.35, 0.4, 1.6)[..., None])[shoes]
    # keep the belt dark: it is very dark in the scan
    belt = lum < 0.12
    out[belt] = (rgb * 0.8)[belt]
    os.makedirs(OUT, exist_ok=True)
    res = np.clip(np.concatenate([out, alpha], -1) * 255 + 0.5, 0, 255).astype(np.uint8)
    Image.fromarray(res, "RGBA").save(os.path.join(OUT, f"uniform_{name}.png"), optimize=True)


if __name__ == "__main__":
    make("india", india_pattern())
    make("enemy", enemy_pattern(), wear=0.1)
    print("UNIFORMS_DONE")
