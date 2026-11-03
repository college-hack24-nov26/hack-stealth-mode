"""Models the AK-203 (Indian Army) and a wood-furniture AK-47 (enemy) to real dimensions.

Run: Blender -b --factory-startup -P assets_src/blender/build_weapons.py
Rifle space: origin at the centre of the pistol grip, forward = Blender -Y (Unity +Z), up = +Z.
Empties exported with each rifle: Muzzle, SupportHand, Stock (used by the Unity gun/IK code).
"""
import bpy, bmesh, math, os, json
from mathutils import Vector, Matrix

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Models", "Weapons")
MANIFEST = os.path.join(ROOT, "assets_src", "build", "materials.json")
os.makedirs(OUT, exist_ok=True)

GEN = os.path.join(ROOT, "assets_src", "build", "gen_tex")
MATS = {
    # blued steel: Poly Haven scan, de-blued and darkened in the packer; edges are rubbed bright
    "rifle_metal": {"ph": "blue_metal_plate", "gray": True, "tint": [0.10, 0.10, 0.105], "metal": 0.55, "smooth_scale": 0.72},
    "rifle_metal_edge": {"ph": "blue_metal_plate", "gray": True, "tint": [0.32, 0.32, 0.33], "metal": 0.9, "smooth_scale": 0.95},
    "rifle_polymer": {"gen": "polymer", "tint": [1, 1, 1]},
    "rifle_polymer_edge": {"gen": "polymer", "tint": [1.7, 1.7, 1.7]},
    "rifle_rubber": {"color": [0.02, 0.02, 0.02], "metal": 0.0, "smooth": 0.15},
    "rifle_wood": {"ph": "fine_grained_wood"},
    "rifle_bore": {"color": [0.0, 0.0, 0.0], "metal": 0.0, "smooth": 0.0},
}


def mat(name):
    return bpy.data.materials.get(name) or bpy.data.materials.new(name)


def P(f, u, s=0.0):
    """rifle space (forward, up, side) -> Blender"""
    return Vector((s, -f, u))


class Part:
    def __init__(self, name):
        self.objs = []
        self.name = name

    def box(self, f0, f1, u0, u1, w, m, bevel=0.002, s=0.0, rot=None, pivot=None):
        bpy.ops.mesh.primitive_cube_add(size=1)
        o = bpy.context.active_object
        o.scale = (w, abs(f1 - f0), abs(u1 - u0))
        o.location = P((f0 + f1) / 2, (u0 + u1) / 2, s)
        if rot is not None:
            bpy.context.view_layer.update()
            pv = P(*pivot) if pivot else o.location.copy()
            R = Matrix.Rotation(math.radians(rot), 4, "X")
            o.matrix_world = Matrix.Translation(pv) @ R @ Matrix.Translation(-pv) @ o.matrix_world
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
        o.data.materials.append(mat(m))
        if bevel:
            # bevel faces get the worn-edge variant of the material (polished steel / rubbed polymer)
            o.data.materials.append(mat(m + "_edge" if m in ("rifle_metal", "rifle_polymer") else m))
            b = o.modifiers.new("b", "BEVEL"); b.width = bevel; b.segments = 2; b.limit_method = "ANGLE"; b.material = 1
        self.objs.append(o)
        return o

    def cyl(self, f0, f1, u, r, m, seg=16, s=0.0, bevel=0.0):
        bpy.ops.mesh.primitive_cylinder_add(vertices=seg, radius=r, depth=abs(f1 - f0))
        o = bpy.context.active_object
        o.rotation_euler = (math.radians(90), 0, 0)
        o.location = P((f0 + f1) / 2, u, s)
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
        o.data.materials.append(mat(m))
        if bevel:
            o.data.materials.append(mat(m + "_edge" if m in ("rifle_metal", "rifle_polymer") else m))
            b = o.modifiers.new("b", "BEVEL"); b.width = bevel; b.segments = 1; b.limit_method = "ANGLE"; b.material = 1
        self.objs.append(o)
        return o

    def empty(self, name, f, u, s=0.0):
        e = bpy.data.objects.new(name, None)
        e.location = P(f, u, s)
        bpy.context.scene.collection.objects.link(e)
        self.objs.append(e)
        return e

    def finish(self):
        meshes = [o for o in self.objs if o.type == "MESH"]
        empties = [o for o in self.objs if o.type == "EMPTY"]
        bpy.ops.object.select_all(action="DESELECT")
        for o in meshes:
            o.select_set(True)
            bpy.context.view_layer.objects.active = o
            for m in list(o.modifiers):
                bpy.ops.object.modifier_apply(modifier=m.name)
        bpy.context.view_layer.objects.active = meshes[0]
        bpy.ops.object.join()
        body = bpy.context.active_object
        body.name = self.name
        for p in body.data.polygons:
            p.use_smooth = True
        # weighted normals keep bevelled hard-surface shading crisp
        wn = body.modifiers.new("wn", "WEIGHTED_NORMAL"); wn.keep_sharp = True
        bpy.ops.object.modifier_apply(modifier="wn")
        uv_box(body, 0.6)
        for e in empties:
            e.parent = body
        return body


def uv_box(o, tile):
    bm = bmesh.new(); bm.from_mesh(o.data)
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for l in f.loops:
            c = l.vert.co
            u, v = ((c.x, c.y) if ax == 2 else (c.x, c.z) if ax == 1 else (c.y, c.z))
            l[uv].uv = (u / tile, v / tile)
    bm.to_mesh(o.data); bm.free()


def magazine(p, m, ribs=True):
    # curved 30-round AK magazine: 9 segments sweeping forward as it drops
    f, u, ang = 0.075, 0.035, 0.0
    for i in range(9):
        seg_len = 0.027
        o = p.box(f - 0.03, f + 0.03, u - seg_len, u, 0.027, m, bevel=0.0025, rot=-ang, pivot=(f, u, 0))
        if ribs and i % 2 == 0:
            p.box(f - 0.033, f + 0.033, u - seg_len * 0.6, u - seg_len * 0.4, 0.029, m, bevel=0.001, rot=-ang, pivot=(f, u, 0))
        ang += 3.2
        f += math.sin(math.radians(ang)) * seg_len
        u -= math.cos(math.radians(ang)) * seg_len
    p.box(f - 0.034, f + 0.034, u - 0.012, u, 0.031, m, bevel=0.003, rot=-ang, pivot=(f, u, 0))  # floor plate


def ak(name, wood):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    p = Part(name)
    furn = "rifle_wood" if wood else "rifle_polymer"
    # receiver + trunnion
    p.box(-0.105, 0.205, 0.025, 0.10, 0.046, "rifle_metal", bevel=0.003)
    p.box(0.12, 0.205, 0.0, 0.03, 0.040, "rifle_metal", bevel=0.002)          # magwell
    # dust cover (rounded) + picatinny rail on the AK-203
    p.box(-0.095, 0.185, 0.10, 0.122, 0.042, "rifle_metal", bevel=0.008)
    if not wood:
        p.box(-0.09, 0.18, 0.122, 0.13, 0.022, "rifle_metal", bevel=0.001)
        for i in range(27):
            f = -0.085 + i * 0.01
            p.box(f, f + 0.006, 0.13, 0.137, 0.022, "rifle_metal", bevel=0.0007)
    # rear sight block + leaf
    p.box(0.185, 0.235, 0.088, 0.128, 0.034, "rifle_metal", bevel=0.003)
    p.box(0.19, 0.24, 0.128, 0.134, 0.02, "rifle_metal", bevel=0.001)
    # barrel, gas tube, gas block, front sight, muzzle
    p.cyl(0.20, 0.62, 0.072, 0.0095, "rifle_metal", seg=16)
    p.cyl(0.235, 0.46, 0.108, 0.0125, "rifle_metal", seg=14)
    p.box(0.455, 0.49, 0.06, 0.12, 0.03, "rifle_metal", bevel=0.003)
    p.box(0.585, 0.605, 0.062, 0.095, 0.022, "rifle_metal", bevel=0.002)
    p.box(0.59, 0.60, 0.095, 0.145, 0.006, "rifle_metal", bevel=0.0)            # front post
    for s in (-1, 1):
        p.box(0.585, 0.605, 0.10, 0.15, 0.004, "rifle_metal", bevel=0.0, s=s * 0.011)  # post ears
    p.cyl(0.62, 0.70, 0.072, 0.0135, "rifle_metal", seg=16, bevel=0.001)
    p.cyl(0.695, 0.701, 0.072, 0.0075, "rifle_bore", seg=12)
    # handguard (M-LOK polymer on the 203, wood on the 47)
    hg = p.box(0.235, 0.44, 0.042, 0.102, 0.05, furn, bevel=0.012)
    if not wood:
        for i in range(4):
            f = 0.26 + i * 0.045
            for s in (-1, 1):
                p.box(f, f + 0.03, 0.06, 0.07, 0.003, "rifle_bore", bevel=0.0, s=s * 0.0245)
    # trigger guard, trigger, pistol grip, selector, charging handle
    p.box(-0.005, 0.105, -0.025, -0.019, 0.012, "rifle_metal", bevel=0.001)
    p.box(-0.01, -0.003, -0.025, 0.025, 0.012, "rifle_metal", bevel=0.001)
    p.box(0.035, 0.042, -0.012, 0.026, 0.006, "rifle_metal", bevel=0.0, rot=-10)
    p.box(-0.055, -0.02, -0.10, 0.03, 0.03, furn, bevel=0.009, rot=18, pivot=(-0.035, 0.03, 0))  # raked 18 deg: bottom of the grip sits behind the top, as on a real AK
    p.box(-0.06, 0.10, 0.06, 0.068, 0.004, "rifle_metal", bevel=0.0, s=0.025)
    p.cyl(0.12, 0.13, 0.095, 0.0055, "rifle_metal", seg=8, s=0.034)
    p.box(0.122, 0.128, 0.09, 0.10, 0.02, "rifle_metal", bevel=0.001, s=0.032)
    magazine(p, "rifle_polymer" if not wood else "rifle_metal")
    # stock
    if wood:
        p.box(-0.40, -0.105, 0.035, 0.105, 0.04, furn, bevel=0.01, rot=6, pivot=(-0.105, 0.09, 0))
        p.box(-0.42, -0.395, 0.0, 0.11, 0.044, "rifle_metal", bevel=0.004, rot=6, pivot=(-0.105, 0.09, 0))
    else:
        p.cyl(-0.30, -0.105, 0.085, 0.014, "rifle_metal", seg=12)
        p.box(-0.40, -0.29, 0.03, 0.125, 0.042, furn, bevel=0.012)
        p.box(-0.37, -0.30, 0.125, 0.14, 0.036, furn, bevel=0.006)                # cheek riser
        p.box(-0.415, -0.40, 0.02, 0.13, 0.046, "rifle_rubber", bevel=0.005)     # butt pad
        p.box(-0.13, -0.105, 0.04, 0.11, 0.05, furn, bevel=0.004)                 # hinge
    # receiver rivets and trunnion pins (both sides), dust-cover reinforcing rib, sling loop, barrel crown edge
    for side in (-1, 1):
        sx = side * 0.0235
        for f, u in ((0.16, 0.035), (0.185, 0.035), (0.16, 0.088), (0.185, 0.088), (-0.08, 0.04), (-0.06, 0.04), (0.02, 0.04), (0.04, 0.04)):
            bpy.ops.mesh.primitive_cylinder_add(vertices=8, radius=0.0028, depth=0.003, location=P(f, u, sx), rotation=(0, math.radians(90), 0))
            r = bpy.context.active_object
            bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
            r.data.materials.append(mat("rifle_metal_edge"))
            p.objs.append(r)
        p.box(0.11, 0.115, 0.028, 0.098, 0.002, "rifle_metal", bevel=0.0, s=sx * 1.04)
    p.box(-0.06, 0.15, 0.121, 0.124, 0.012, "rifle_metal", bevel=0.001)
    p.box(0.44, 0.452, 0.05, 0.062, 0.03, "rifle_metal", bevel=0.001)
    p.cyl(0.70, 0.703, 0.072, 0.0128, "rifle_metal_edge", seg=16)
    p.empty("Muzzle", 0.705, 0.072)
    p.empty("SupportHand", 0.33, 0.045)
    p.empty("Stock", -0.41, 0.08)
    body = p.finish()
    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    for c in body.children:
        c.select_set(True)
    bpy.context.view_layer.objects.active = body
    print("WEAPON", name, "tris", sum(len(f.vertices) - 2 for f in body.data.polygons), "size", tuple(round(x, 3) for x in body.dimensions))
    bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, name + ".fbx"), use_selection=True, object_types={"MESH", "EMPTY"},
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                             bake_space_transform=True, mesh_smooth_type="FACE")
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT, "assets_src", "build", name + ".blend"))


def make_polymer(n=1024):
    """Stippled black gun polymer: albedo, OpenGL normal and roughness from fine noise (numpy in Blender's python)."""
    import numpy as np
    os.makedirs(GEN, exist_ok=True)
    rng = np.random.default_rng(5)
    h = rng.random((n, n)).astype(np.float32)
    k = np.ones(3, np.float32) / 3
    h = (h + np.roll(h, 1, 0) + np.roll(h, 1, 1)) / 3  # ~1 mm stipple at the 0.6 m UV tile
    mott = rng.random((n // 32, n // 32)).astype(np.float32).repeat(32, 0).repeat(32, 1)
    for _ in range(6):
        mott = (np.roll(mott, 3, 0) + mott + np.roll(mott, -3, 0) + np.roll(mott, 3, 1) + np.roll(mott, -3, 1)) / 5
    alb = 0.055 + 0.02 * (h - 0.5) + 0.012 * (mott - 0.5)
    gx = np.roll(h, -1, 1) - np.roll(h, 1, 1); gy = np.roll(h, -1, 0) - np.roll(h, 1, 0)
    nr = np.dstack([-gx * 1.2, -gy * 1.2, np.ones_like(h)]); nr /= np.linalg.norm(nr, axis=2, keepdims=True)
    rough = 0.62 + 0.15 * (h - 0.5) - 0.1 * (mott - 0.5)

    def save(a, name):
        a = np.clip(a, 0, 1)
        img = bpy.data.images.new(name, n, n, alpha=True)
        if a.ndim == 2:
            a = np.dstack([a, a, a])
        img.pixels.foreach_set(np.dstack([a, np.ones((n, n, 1), np.float32)]).ravel())
        img.filepath_raw = os.path.join(GEN, name + ".png"); img.file_format = "PNG"; img.save()
    img_alb = alb  # stored as sRGB-ish values; dark enough that the transfer curve barely matters
    save(img_alb, "polymer_diff"); save(nr * 0.5 + 0.5, "polymer_nor"); save(rough, "polymer_rough")


def main():
    ak("AK203", wood=False)
    ak("AK47", wood=True)
    m = json.load(open(MANIFEST))
    PH = os.path.expanduser("~/Documents/Poly Haven Assets")
    make_polymer()
    for k, v in MATS.items():
        if "gen" in v:
            m[k] = dict(v, files={"diff": os.path.join(GEN, "polymer_diff.png"), "nor_gl": os.path.join(GEN, "polymer_nor.png"),
                                  "rough": os.path.join(GEN, "polymer_rough.png")})
            continue
        if "ph" in v:
            d = os.path.join(PH, v["ph"], "textures")
            files = {}
            for f in sorted(os.listdir(d)):
                b = f.rsplit("_", 1)[0]
                for kind, key in (("diff", "diff"), ("col", "diff"), ("nor_gl", "nor_gl"), ("rough", "rough")):
                    if b.endswith("_" + kind):
                        files[key] = os.path.join(d, f)
            m[k] = dict(v, files=files)
        else:
            m[k] = v
    json.dump(m, open(MANIFEST, "w"), indent=1)
    print("WEAPONS_DONE")


main()
