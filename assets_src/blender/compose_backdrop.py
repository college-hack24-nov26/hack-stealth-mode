"""Lays the rendered land band over sky.png at full width: Textures/Sky/backdrop.png, same mapping as the sky.
Run: python3 assets_src/blender/compose_backdrop.py <band png> <width>"""
import os, sys
import numpy as np
from PIL import Image, ImageFilter
Image.MAX_IMAGE_PIXELS = None
band, W = sys.argv[1], int(sys.argv[2])
sky_dir = os.path.join(os.path.dirname(__file__), "..", "..", "Stealth Mode", "Assets", "StealthMode", "Textures", "Sky")
sky = Image.open(os.path.join(sky_dir, "sky.png")).convert("RGB").resize((W, W // 2), Image.LANCZOS)
land = Image.open(band).convert("RGBA")
top = round(W / 2 * (0.5 - 22 / 180))
sky.paste(land, (0, top), land)
a = np.array(sky)
# below the land's near edge the real near hills cover everything; carry the ground colour down, never the sky's reflection
alpha = np.array(land)[..., 3] > 200
last = alpha.shape[0] - 1 - np.argmax(alpha[::-1], axis=0)          # lowest land pixel of each column
edge = a[top + last - 2, np.arange(W)]
edge = np.array(Image.fromarray(edge[None].repeat(16, 0)).filter(ImageFilter.GaussianBlur(6)))[8]
rows = np.arange(a.shape[0])[:, None]
below = rows >= (top + last)[None, :]
a[below] = np.broadcast_to(edge[None], a.shape)[below]
Image.fromarray(a).save(os.path.join(sky_dir, "backdrop.png"), compress_level=6)
print("backdrop", a.shape)
