"""Packs Poly Haven texture sets from assets_src/build/materials.json into Unity-ready maps.

Per material key, writes Stealth Mode/Assets/StealthMode/Textures/<key>/:
  albedo.png   RGB (+A from the alpha map for cutout materials)
  normal.png   OpenGL normal (Unity convention)
  mask.png     R = metallic, G = occlusion (1), B = 0, A = smoothness (1 - roughness)
Also tone-maps the sky HDR into Textures/Sky/sky.png.

Run:  python3 assets_src/tools/pack_textures.py
"""
import json, os, subprocess, tempfile
import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Textures")
MANIFEST = os.path.join(ROOT, "assets_src", "build", "materials.json")
SIZE = 1024


EXR_CACHE = os.path.join(ROOT, "assets_src", "build", "exr_cache")
BLENDER = "/Applications/Blender.app/Contents/MacOS/Blender"


def exr_png(path):
    return os.path.join(EXR_CACHE, os.path.basename(path)[:-4] + ".png")


def convert_exrs(paths):
    """ffmpeg can't decode several Poly Haven EXR compressions; Blender reads them all. Linear data, saved 8-bit."""
    todo = [p for p in sorted(set(paths)) if not os.path.exists(exr_png(p))]
    if not todo:
        return
    os.makedirs(EXR_CACHE, exist_ok=True)
    script = os.path.join(EXR_CACHE, "_conv.py")
    with open(script, "w") as f:
        f.write("import bpy, json\n")
        f.write("jobs = %r\n" % [(p, exr_png(p)) for p in todo])
        f.write("for src, dst in jobs:\n"
                "    im = bpy.data.images.load(src)\n"
                "    im.colorspace_settings.name = 'Non-Color'\n"
                "    sc = bpy.context.scene\n"
                "    sc.render.image_settings.file_format = 'PNG'\n"
                "    sc.render.image_settings.color_depth = '8'\n"
                "    sc.view_settings.view_transform = 'Standard'\n"
                "    sc.display_settings.display_device = 'sRGB'\n"
                "    im.save_render(dst, scene=sc)\n")
    subprocess.run([BLENDER, "-b", "--factory-startup", "-P", script], check=True, capture_output=True)


def load(path, mode):
    """Load any texture (jpg/png/exr) as float array in 0..1."""
    if path.lower().endswith(".exr"):
        im = Image.open(exr_png(path))
    else:
        im = Image.open(path)
    im = im.convert(mode)
    if im.size != (SIZE, SIZE):
        im = im.resize((SIZE, SIZE), Image.LANCZOS)
    return np.asarray(im, dtype=np.float32) / 255.0


def save(arr, path):
    arr = np.clip(arr * 255.0 + 0.5, 0, 255).astype(np.uint8)
    Image.fromarray(arr).save(path, optimize=True)


def pack(key, files, gray=False, metal_value=0.0):
    d = os.path.join(OUT, key)
    os.makedirs(d, exist_ok=True)
    if "diff" in files:
        rgb = load(files["diff"], "RGB")
        if gray:
            l = rgb @ np.array([0.299, 0.587, 0.114], np.float32)
            rgb = np.dstack([l / max(float(l.mean()), 1e-3) * 0.5] * 3)
        if "alpha" in files:
            a = load(files["alpha"], "L")
            save(np.dstack([rgb, a]), os.path.join(d, "albedo.png"))
        else:
            save(rgb, os.path.join(d, "albedo.png"))
        if key == "container_side":
            # neutral version for tinted colour variants: keep luminance detail around mid grey
            lum = rgb @ np.array([0.299, 0.587, 0.114], dtype=np.float32)
            lum = lum / max(lum.mean(), 1e-3) * 0.55
            save(np.dstack([lum] * 3), os.path.join(d, "albedo_gray.png"))
    if "nor_gl" in files:
        save(load(files["nor_gl"], "RGB"), os.path.join(d, "normal.png"))
    rough = load(files["rough"], "L") if "rough" in files else None
    if rough is None and "arm" in files:
        rough = load(files["arm"], "RGB")[..., 1]
    metal = load(files["metal"], "L") if "metal" in files else np.full((SIZE, SIZE), metal_value, np.float32)
    if rough is not None or "metal" in files:
        smooth = 1.0 - (rough if rough is not None else np.full((SIZE, SIZE), 0.6, np.float32))
        mask = np.dstack([metal, np.ones_like(metal), np.zeros_like(metal), smooth])
        save(mask, os.path.join(d, "mask.png"))


def sky():
    src = os.path.join(ROOT, "assets_src", "downloads", "belfast_sunset_puresky_4k.hdr")
    d = os.path.join(OUT, "Sky")
    os.makedirs(d, exist_ok=True)
    with tempfile.TemporaryDirectory() as td:
        tmp = os.path.join(td, "s.pfm")
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", src, "-f", "image2", "-pix_fmt", "gbrpf32le", tmp], check=True)
        hdr = read_pfm(tmp)
    exposure = 0.26  # sky median ~0.93 -> ~0.24 before the curve
    x = hdr * exposure
    # ACES-like filmic curve, then sRGB
    a, b, c, d2, e = 2.51, 0.03, 2.43, 0.59, 0.14
    y = np.clip((x * (a * x + b)) / (x * (c * x + d2) + e), 0, 1)
    y = np.where(y <= 0.0031308, 12.92 * y, 1.055 * np.power(y, 1 / 2.4) - 0.055)
    save(y, os.path.join(d, "sky.png"))


def read_pfm(path):
    with open(path, "rb") as f:
        hdr = f.readline().strip()
        w, h = map(int, f.readline().split())
        scale = float(f.readline())
        data = np.fromfile(f, "<f4" if scale < 0 else ">f4")
    ch = 3 if hdr == b"PF" else 1
    return np.flipud(data.reshape(h, w, ch))


def main(only=None):
    m = json.load(open(MANIFEST))
    convert_exrs([f for v in m.values() for f in v.get("files", {}).values() if f.lower().endswith(".exr")])
    for key, v in m.items():
        if "files" in v and v["files"]:
            if only and key not in only:
                continue
            pack(key, v["files"], gray=v.get("gray", False), metal_value=float(v.get("metal", 0.0)))
            print("packed", key)
    if not only:
        sky()
    print("PACK_DONE")


if __name__ == "__main__":
    import sys
    main(sys.argv[1:] or None)
