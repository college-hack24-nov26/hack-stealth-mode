"""Character textures for Unity (Textures/Characters/<material key>.png), from MPFB scans + procedural gear cloth.

Run: python3 assets_src/tools/gen_character_textures.py
"""
import os, shutil
import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
M = os.path.expanduser("~/Library/Application Support/Blender/5.2/extensions/.user/blender_org/mpfb/data")
OUT = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Textures", "Characters")
os.makedirs(OUT, exist_ok=True)


def arr(p, mode="RGBA"):
    return np.asarray(Image.open(p).convert(mode), np.float32) / 255.0


def save(a, name):
    Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8)).save(os.path.join(OUT, name), optimize=True)


def skin_maps(folder, name):
    """Normal + mask (A = smoothness from the scan's specular) for skins that ship them."""
    d = os.path.join(M, "skins", folder)
    nrm = [f for f in os.listdir(d) if f.endswith("_NRM.png")]
    spec = [f for f in os.listdir(d) if f.endswith("_SPEC.png")]
    if nrm:
        Image.open(os.path.join(d, nrm[0])).convert("RGB").resize((1024, 1024), Image.LANCZOS).save(os.path.join(OUT, name + "_normal.png"))
    if spec:
        sp = np.asarray(Image.open(os.path.join(d, spec[0])).convert("L").resize((1024, 1024), Image.LANCZOS), np.float32) / 255.0
        sm = np.clip(0.28 + sp * 0.55, 0, 1)
        z = np.zeros_like(sm)
        save(np.dstack([z, np.ones_like(sm), z, sm]), name + "_mask.png")


def skin(src, name, tone, size=1024):
    a = arr(os.path.join(M, "skins", src))
    rgb = a[..., :3]
    lum = rgb @ np.array([0.299, 0.587, 0.114], np.float32)
    m = (a[..., 3] > 0.5) & (lum > 0.15)
    mean = rgb[m].mean(0)
    # per-channel gain keeps the scan's pores, redness and shading; only the overall complexion moves
    out = rgb * (np.array(tone, np.float32) / 255.0) / mean
    im = Image.fromarray(np.clip(out * 255 + 0.5, 0, 255).astype(np.uint8)).resize((size, size), Image.LANCZOS)
    im.save(os.path.join(OUT, name + ".png"), optimize=True)


def weave(n, seed, scale=1.0):
    r = np.random.default_rng(seed)
    y, x = np.mgrid[0:n, 0:n]
    w = 0.5 + 0.25 * np.sin(x * 2 * np.pi / (4 * scale)) * np.sin(y * 2 * np.pi / (4 * scale))
    grain = r.normal(0, 0.06, (n, n))
    return (w + grain).astype(np.float32)


def blobs(n, cells, seed):
    r = np.random.default_rng(seed)
    g = r.random((cells, cells)).astype(np.float32)
    g = np.tile(g, (2, 2))  # tileable
    im = Image.fromarray((g * 255).astype(np.uint8)).resize((n * 2, n * 2), Image.BICUBIC).crop((n // 2, n // 2, n // 2 + n, n // 2 + n))
    return np.asarray(im, np.float32) / 255.0


def gear(name, base, accents, digital, seed, n=512):
    out = np.empty((n, n, 3), np.float32); out[:] = np.array(base) / 255.0
    for i, (col, thr, cells) in enumerate(accents):
        f = blobs(n, cells, seed + i)
        if digital:
            f = np.asarray(Image.fromarray((f * 255).astype(np.uint8)).resize((n // 6, n // 6), Image.BILINEAR).resize((n, n), Image.NEAREST), np.float32) / 255.0
        out[f > thr] = np.array(col) / 255.0
    out *= (0.85 + 0.3 * weave(n, seed))[..., None]
    save(out, name + ".png")
    # matching tileable cloth normal from the weave height
    h = weave(n, seed) * 2.0
    gx = np.roll(h, -1, 1) - np.roll(h, 1, 1); gy = np.roll(h, -1, 0) - np.roll(h, 1, 0)
    nrm = np.dstack([-gx, -gy, np.full_like(h, 2.0)]); nrm /= np.linalg.norm(nrm, axis=2, keepdims=True)
    save(nrm * 0.5 + 0.5, name + "_normal.png")


def main():
    mf = "mindfront_skin_male_african_middleage"
    skin(mf + "/skin_male_african_middleage.png", "skin_in", (128, 88, 64))
    skin(mf + "/skin_male_african_middleage.png", "skin_en", (118, 82, 60))
    skin_maps(mf, "skin_in"); skin_maps(mf, "skin_en")
    beard = [f for f in os.listdir(os.path.join(M, "skins", "jartur69_middleage_slavic_male_with_genitals_and_beard")) if f.endswith(".png")]
    skin("jartur69_middleage_slavic_male_with_genitals_and_beard/" + sorted(beard)[0], "skin_enb", (116, 82, 60))
    e = arr(os.path.join(M, "eyes", "materials", "brown_eye.png"), "RGB")
    g = e.mean(2, keepdims=True)
    e = g + (e - g) * 0.35                      # drop the red cast
    dark = (g < 0.6)                            # iris: deep brown; sclera stays near white
    e = np.where(dark, e * np.array([0.62, 0.48, 0.36]) * 1.2, np.minimum(e * 1.05, 0.92))
    save(e, "eyes.png")
    shutil.copy(os.path.join(M, "eyebrows", "eyebrow001", "eyebrow001.png"), os.path.join(OUT, "brows.png"))
    shutil.copy(os.path.join(M, "hair", "short02", "short02_diffuse.png"), os.path.join(OUT, "hair.png"))
    lash = [f for f in os.listdir(os.path.join(M, "eyelashes", "eyelashes01")) if f.endswith(".png") and "thumb" not in f]
    shutil.copy(os.path.join(M, "eyelashes", "eyelashes01", sorted(lash)[0]), os.path.join(OUT, "lashes.png"))
    boots = arr(os.path.join(M, "clothes", "shoes03", "shoes03_diffuse.png"), "RGB")
    save(boots * np.array([0.55, 0.52, 0.48]), "boots.png")
    shutil.copy(os.path.join(M, "clothes", "male_casualsuit01", "male_casualsuit01_normal.png"), os.path.join(OUT, "uniform_normal.png"))
    gear("gear_india", (74, 78, 54), [((68, 72, 50), 0.6, 25)], False, 3)
    gear("helmet_india", (92, 96, 66), [((138, 126, 94), 0.6, 7), ((84, 66, 48), 0.64, 9), ((52, 56, 42), 0.68, 11)], True, 5)
    gear("gear_enemy", (120, 104, 78), [((96, 90, 62), 0.6, 7)], False, 7)
    gear("helmet_enemy", (82, 86, 62), [((70, 72, 52), 0.6, 15)], False, 9)
    gear("knit_cap", (38, 36, 34), [((48, 44, 40), 0.55, 40)], False, 11)
    print("CHAR_TEX_DONE")


main()
