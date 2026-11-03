"""Builds the Scenario One compound assets and exports them as FBX for Unity.

Run:  Blender -b --factory-startup -P assets_src/blender/build_env.py

Outputs
  Stealth Mode/Assets/StealthMode/Models/Env/*.fbx    generated structures + terrain
  Stealth Mode/Assets/StealthMode/Models/Props/*.fbx  Poly Haven props
  assets_src/build/materials.json                     material name -> texture files

Axis note: Blender (x, y, z) arrives in Unity as (-x, z, -y) with the exporter settings used
here, so terrain vertices are written at (-ux, -uz) to land at Unity (ux, uz).
"""
import bpy, bmesh, math, os, json, random
from mathutils import Vector, noise

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PH = os.path.expanduser("~/Documents/Poly Haven Assets")
UNITY_MODELS = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Models")
BUILD = os.path.join(ROOT, "assets_src", "build")
for d in (UNITY_MODELS + "/Env", UNITY_MODELS + "/Props", BUILD):
    os.makedirs(d, exist_ok=True)

MANIFEST = {}  # material key -> {"ph": asset, "files": {...}} or {"color": [...], ...}


# ---------------------------------------------------------------- helpers
def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def ph_textures(asset):
    d = os.path.join(PH, asset, "textures")
    files = {}
    for f in sorted(os.listdir(d)):
        base = f.rsplit("_", 1)[0]  # strip _1k
        for kind in ("diff", "nor_gl", "rough", "metal", "alpha", "arm", "ao", "mask", "translucent"):
            if base.endswith("_" + kind):
                files.setdefault(kind, os.path.join(d, f))
    return files


def mat_ph(key, asset=None, tile=None):
    """Material whose textures come from a Poly Haven texture set."""
    asset = asset or key
    if key not in MANIFEST:
        MANIFEST[key] = {"ph": asset, "files": ph_textures(asset)}
    return get_mat(key)


def mat_color(key, rgb, metal=0.0, smooth=0.3):
    MANIFEST.setdefault(key, {"color": list(rgb), "metal": metal, "smooth": smooth})
    return get_mat(key)


def get_mat(key):
    m = bpy.data.materials.get(key) or bpy.data.materials.new(key)
    return m


class Builder:
    """Accumulates quads/boxes into one bmesh with per-face material and planar UVs."""

    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")
        self.mats = []

    def mi(self, mat):
        if mat not in self.mats:
            self.mats.append(mat)
        return self.mats.index(mat)

    def face(self, pts, mat, tile=2.0, uvs=None):
        vs = [self.bm.verts.new(p) for p in pts]
        f = self.bm.faces.new(vs)
        f.material_index = self.mi(mat)
        f.normal_update()
        if uvs is None:
            n = f.normal
            ax = max(range(3), key=lambda i: abs(n[i]))
            for l in f.loops:
                c = l.vert.co
                if ax == 2:
                    u, v = c.x, c.y
                elif ax == 1:
                    u, v = c.x, c.z
                else:
                    u, v = c.y, c.z
                l[self.uv].uv = (u / tile, v / tile)
        else:
            for l, uv in zip(f.loops, uvs):
                l[self.uv].uv = uv
        return f

    def box(self, x0, x1, y0, y1, z0, z1, mat, tile=2.0, skip=()):
        p = [Vector((x, y, z)) for z in (z0, z1) for y in (y0, y1) for x in (x0, x1)]
        quads = {
            "-z": (0, 2, 3, 1), "+z": (4, 5, 7, 6),
            "-y": (0, 1, 5, 4), "+y": (2, 6, 7, 3),
            "-x": (0, 4, 6, 2), "+x": (1, 3, 7, 5),
        }
        for k, q in quads.items():
            if k not in skip:
                self.face([p[i] for i in q], mat, tile)

    def cyl(self, a, b, r, mat, seg=8, tile=1.0):
        a, b = Vector(a), Vector(b)
        ax = (b - a).normalized()
        t = ax.orthogonal().normalized()
        s = ax.cross(t)
        ring = lambda c: [c + (t * math.cos(2 * math.pi * i / seg) + s * math.sin(2 * math.pi * i / seg)) * r for i in range(seg)]
        ra, rb = ring(a), ring(b)
        L = (b - a).length
        for i in range(seg):
            j = (i + 1) % seg
            self.face([ra[i], ra[j], rb[j], rb[i]], mat, uvs=[(i / seg, 0), ((i + 1) / seg, 0), ((i + 1) / seg, L / tile), (i / seg, L / tile)])

    def finish(self, smooth_angle=None):
        me = bpy.data.meshes.new(self.name)
        bmesh.ops.remove_doubles(self.bm, verts=self.bm.verts, dist=1e-5)
        self.bm.normal_update()
        self.bm.to_mesh(me)
        self.bm.free()
        for m in self.mats:
            me.materials.append(m)
        ob = bpy.data.objects.new(self.name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob


def cap_tris(ob, cap):
    """Collapse-decimate a mesh object down to about `cap` triangles (Quest budget)."""
    tris = sum(len(p.vertices) - 2 for p in ob.data.polygons)
    if tris <= cap:
        return
    mod = ob.modifiers.new("cap", "DECIMATE")
    mod.ratio = cap / tris
    mod.use_collapse_triangulate = True


def budget(objs):
    for o in objs:
        for c in [o] + list(o.children_recursive):
            if c.type != "MESH":
                continue
            n = c.name
            cap = 1500 if n.endswith("_LOD2") else 3500 if n.endswith("_LOD1") else 7000
            if n.startswith("shrub"):
                cap //= 2
            cap_tris(c, cap)


def export(objs, path):
    budget(objs) if "/Props/" in path else None
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
        for c in o.children_recursive:
            c.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z", axis_up="Y", bake_space_transform=True, object_types={"MESH", "EMPTY"},
        mesh_smooth_type="FACE", use_mesh_modifiers=True, add_leaf_bones=False, path_mode="STRIP",
    )


# ---------------------------------------------------------------- container (ISO 40 ft)
CS_TILE = 1.94          # container_side texture covers 1.94 m
PITCH = CS_TILE / 7     # 7 corrugations per tile
# fraction of a pitch where (crest start, crest end, trough start, trough end), from the disp map
PROFILE = (0.22, 0.44, 0.71, 0.96)
DEPTH = 0.036


def corrugated_wall(b, mat, x0, x1, z0, z1, y, outward):
    """Vertical corrugation along x at plane y; outward = +1/-1 direction of the outer face."""
    xs = []
    n0 = math.floor(x0 / PITCH) - 1
    n1 = math.ceil(x1 / PITCH) + 1
    for n in range(n0, n1):
        for k, t in enumerate(PROFILE):
            x = (n + t) * PITCH
            depth = DEPTH if k in (0, 1) else 0.0  # crest sticks out
            xs.append((x, depth))
    pts = [(x0, None)] + [(x, d) for x, d in xs if x0 < x < x1] + [(x1, None)]

    def depth_at(x):
        # linear interpolation in the profile for the clipped ends
        prev = None
        for xx, d in xs:
            if xx >= x:
                if prev is None:
                    return d
                px, pd = prev
                return pd + (d - pd) * (x - px) / (xx - px)
            prev = (xx, d)
        return 0.0

    pts = [(x, depth_at(x) if d is None else d) for x, d in pts]
    for (xa, da), (xb, db) in zip(pts, pts[1:]):
        ya, yb = y + outward * da, y + outward * db
        quad = [Vector((xa, ya, z0)), Vector((xb, yb, z0)), Vector((xb, yb, z1)), Vector((xa, ya, z1))]
        uvs = [(xa / CS_TILE, z0 / CS_TILE), (xb / CS_TILE, z0 / CS_TILE), (xb / CS_TILE, z1 / CS_TILE), (xa / CS_TILE, z1 / CS_TILE)]
        if outward < 0:
            quad.reverse(); uvs.reverse()
        b.face(quad, mat, uvs=uvs)


def build_container():
    L, W, H = 12.192, 2.438, 2.591
    b = Builder("Container40")
    paint = mat_ph("container_side")
    steel = mat_color("steel_dark", (0.09, 0.09, 0.085), metal=0.8, smooth=0.35)
    seal = mat_color("rubber_black", (0.03, 0.03, 0.03), smooth=0.2)
    hw = W / 2
    post = 0.15
    # side walls (y = +-hw), corrugation outward
    corrugated_wall(b, paint, post, L - post, 0.16, H - 0.11, hw - DEPTH, +1)
    corrugated_wall(b, paint, post, L - post, 0.16, H - 0.11, -hw + DEPTH, -1)
    # front end wall (x = 0) corrugated across y: build in a rotated temp builder
    tmp = Builder("tmp")
    corrugated_wall(tmp, paint, -hw + post, hw - post, 0.16, H - 0.11, 0.0, +1)
    for f in tmp.bm.faces:
        pts = [Vector((-v.co.y + 0.02, v.co.x, v.co.z)) for v in f.verts]
        uvs = [tuple(l[tmp.uv].uv) for l in f.loops]
        b.face(pts, paint, uvs=uvs)
    tmp.bm.free()
    # bottom + top side rails, corner posts, castings
    for y0, y1 in ((hw - 0.12, hw), (-hw, -hw + 0.12)):
        b.box(0, L, y0, y1, 0, 0.16, paint, CS_TILE)
        b.box(0, L, y0, y1, H - 0.11, H, paint, CS_TILE)
    for x0, x1 in ((0, 0.16), (L - 0.16, L)):
        b.box(x0, x1, -hw, hw, 0, 0.16, paint, CS_TILE)
        b.box(x0, x1, -hw, hw, H - 0.11, H, paint, CS_TILE)
        for y0, y1 in ((hw - post, hw), (-hw, -hw + post)):
            b.box(x0, x1, y0, y1, 0, H, paint, CS_TILE)
    for x in (0, L - 0.178):
        for y in (hw - 0.162, -hw):
            for z in (0, H - 0.118):
                b.box(x, x + 0.178, y, y + 0.162, z, z + 0.118, steel, 0.5)
    # roof (slightly lower than rails, shallow)
    b.box(0.16, L - 0.16, -hw + 0.12, hw - 0.12, H - 0.13, H - 0.04, paint, CS_TILE, skip=("-z",))
    # floor underside
    b.box(0.16, L - 0.16, -hw + 0.12, hw - 0.12, 0.10, 0.16, steel, 2.0, skip=("+z",))
    # door end (x = L): two leaves with horizontal ribs, locking bars, cam keepers, hinges
    xd = L - 0.05
    for y0, y1 in ((-hw + post, -0.005), (0.005, hw - post)):
        b.box(xd - 0.05, xd, y0, y1, 0.16, H - 0.11, paint, CS_TILE)
        for zr in (0.6, 1.2, 1.8):
            b.box(xd, xd + 0.025, y0 + 0.05, y1 - 0.05, zr, zr + 0.07, paint, CS_TILE)
        b.box(xd, xd + 0.01, (y0 + y1) / 2 - 0.004, (y0 + y1) / 2 + 0.004, 0.16, H - 0.11, seal)
    for y in (-0.92, -0.42, 0.42, 0.92):
        b.cyl((xd + 0.05, y, 0.12), (xd + 0.05, y, H - 0.08), 0.0135, steel, seg=6)
        b.box(xd + 0.02, xd + 0.06, y - 0.03, y + 0.03, 0.10, 0.20, steel, 0.5)
        b.box(xd + 0.02, xd + 0.06, y - 0.03, y + 0.03, H - 0.16, H - 0.06, steel, 0.5)
        b.box(xd + 0.035, xd + 0.075, y - 0.02, y + 0.25, 1.05, 1.11, steel, 0.5)  # handle
    for y in (-hw + post + 0.02, hw - post - 0.06):
        for z in (0.35, 1.0, 1.65, 2.25):
            b.box(xd - 0.02, xd + 0.04, y, y + 0.04, z, z + 0.12, steel, 0.5)
    ob = b.finish()
    ob.location.x = -L / 2  # origin bottom-centre
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.transform_apply(location=True)
    # the corrugated side walls and the front end wall come out wound inward, so from outside they were back-face
    # culled and the box looked open; turn every wall face that points into the box back outward
    bm = bmesh.new(); bm.from_mesh(ob.data)
    flip = []
    for f in bm.faces:
        c, n = f.calc_center_median(), f.normal
        if abs(n.y) > 0.05 and abs(c.y) > hw - 0.3 and n.y * c.y < 0:     # side walls incl. the slanted ribs
            flip.append(f)
        elif abs(n.x) > 0.05 and abs(c.x) > L / 2 - 0.35 and n.x * c.x < 0:
            flip.append(f)
    bmesh.ops.reverse_faces(bm, faces=flip)
    bm.to_mesh(ob.data); bm.free()
    print("CONTAINER flipped", len(flip), "faces")
    return ob


# ---------------------------------------------------------------- warehouse 30 x 20, eaves 8, ridge 10.5
def build_warehouse():
    b = Builder("Warehouse")
    wall = mat_ph("rusty_corrugated_iron")
    roof = mat_ph("corrugated_iron_02")
    conc = mat_ph("concrete_floor_worn_001")
    shutter = mat_ph("rusty_metal_shutter")
    steel = mat_color("steel_dark", (0.09, 0.09, 0.085), metal=0.8, smooth=0.35)
    dark = mat_color("interior_dark", (0.02, 0.02, 0.02), smooth=0.1)
    glass = mat_color("window_dirty", (0.05, 0.055, 0.06), metal=0.0, smooth=0.75)
    door = mat_ph("green_metal_rust")
    X, Y, E, R, T, P = 15.0, 10.0, 8.0, 10.5, 0.12, 0.35  # half sizes, eave, ridge, wall thickness, plinth
    # plinth + floor
    b.box(-X - 0.3, X + 0.3, -Y - 0.3, Y + 0.3, -0.5, P, conc, 3.0)
    # ramp at the front (Blender +Y = Unity south) under the big door, centred x = +2 (Unity x = -2)
    dx = 2.0
    b.face([Vector((dx - 4, Y + 0.3, P)), Vector((dx + 4, Y + 0.3, P)), Vector((dx + 4, Y + 2.3, 0.0)), Vector((dx - 4, Y + 2.3, 0.0))][::-1], conc, 3.0)
    for s in (-1, 1):
        x = dx + 4 * s
        tri = [Vector((x, Y + 0.3, -0.05)), Vector((x, Y + 0.3, P)), Vector((x, Y + 2.3, -0.05))]
        b.face(tri if s > 0 else tri[::-1], conc, 3.0)
    z0 = P
    # front wall (+Y) with 6 x 5 m opening at x = dx
    ow, oh = 3.0, 5.0
    for (xa, xb) in ((-X, dx - ow), (dx + ow, X)):
        b.box(xa, xb, Y - T, Y, z0, E, wall, 2.0)
    b.box(dx - ow, dx + ow, Y - T, Y, z0 + oh, E, wall, 2.0)
    # opening reveals (inner sides)
    b.box(dx - ow - 0.15, dx - ow, Y - 0.3, Y + 0.05, z0, z0 + oh + 0.2, steel, 1.0)
    b.box(dx + ow, dx + ow + 0.15, Y - 0.3, Y + 0.05, z0, z0 + oh + 0.2, steel, 1.0)
    # rolled shutter housing + bottom 0.6 m of shutter visible
    b.box(dx - ow - 0.1, dx + ow + 0.1, Y, Y + 0.55, z0 + oh, z0 + oh + 0.6, shutter, 1.9)
    b.box(dx - ow, dx + ow, Y - 0.02, Y + 0.02, z0 + oh - 0.6, z0 + oh, shutter, 1.9)
    # back wall (-Y) with personnel door 1.2 x 2.2 at x = dx
    b.box(-X, dx - 0.6, -Y, -Y + T, z0, E, wall, 2.0)
    b.box(dx + 0.6, X, -Y, -Y + T, z0, E, wall, 2.0)
    b.box(dx - 0.6, dx + 0.6, -Y, -Y + T, z0 + 2.2, E, wall, 2.0)
    b.box(dx - 0.6, dx + 0.6, -Y + 0.03, -Y + 0.08, z0, z0 + 2.2, door, 1.2)
    b.box(dx - 0.7, dx + 0.7, -Y - 0.06, -Y, z0 + 2.2, z0 + 2.3, steel, 1.0)
    b.cyl((dx + 0.45, -Y - 0.01, z0 + 1.0), (dx + 0.45, -Y - 0.09, z0 + 1.0), 0.02, steel, seg=6)
    # side walls with gables
    for s in (-1, 1):
        x0, x1 = (X - T, X) if s > 0 else (-X, -X + T)
        b.box(x0, x1, -Y, Y, z0, E, wall, 2.0)
        # gable triangle (both faces)
        xo = x1 if s > 0 else x0
        xi = x0 if s > 0 else x1
        tri_o = [Vector((xo, -Y, E)), Vector((xo, Y, E)), Vector((xo, 0, R))]
        tri_i = [Vector((xi, -Y, E)), Vector((xi, Y, E)), Vector((xi, 0, R))]
        b.face(tri_o if s > 0 else tri_o[::-1], wall, 2.0)
        b.face(tri_i[::-1] if s > 0 else tri_i, wall, 2.0)
    # high windows along the long walls
    for y, s in ((Y + 0.01, 1), (-Y - 0.01, -1)):
        for x in range(-12, 13, 4):
            pts = [Vector((x - 0.9, y, 6.2)), Vector((x + 0.9, y, 6.2)), Vector((x + 0.9, y, 7.2)), Vector((x - 0.9, y, 7.2))]
            b.face(pts if s > 0 else pts[::-1], glass, uvs=[(0, 0), (1, 0), (1, 1), (0, 1)] if s > 0 else [(0, 1), (1, 1), (1, 0), (0, 0)])
    # roof: two slopes with 0.6 m overhang and thickness
    ov = 0.6
    slope = (R - E) / Y
    for s in (-1, 1):
        ye = s * (Y + ov)
        ze = E - slope * ov
        top = [Vector((-X - ov, 0, R + 0.08)), Vector((X + ov, 0, R + 0.08)), Vector((X + ov, ye, ze + 0.08)), Vector((-X - ov, ye, ze + 0.08))]
        bot = [Vector((p.x, p.y, p.z - 0.08)) for p in top]
        L = math.hypot(Y + ov, R - ze)
        uv = [(-X / 2.7, 0), (X / 2.7, 0), (X / 2.7, L / 2.7), (-X / 2.7, L / 2.7)]
        b.face(top if s < 0 else top[::-1], roof, uvs=uv if s < 0 else uv[::-1])
        b.face(bot[::-1] if s < 0 else bot, dark, 4.0)
        edge = [top[2], top[3], bot[3], bot[2]]
        b.face(edge if s < 0 else edge[::-1], steel, 1.0)
    # gutters
    for s in (-1, 1):
        y = s * (Y + 0.65)
        b.box(-X - 0.6, X + 0.6, y - 0.08, y + 0.08, E - 0.75, E - 0.6, steel, 1.0)
    # interior: dark floor patch and steel columns
    b.box(-X + T, X - T, -Y + T, Y - T, P - 0.01, P, conc, 3.0, skip=("-z", "-x", "+x", "-y", "+y"))
    for x in (-10, -5, 0, 5, 10):
        for y in (-Y + 0.4, Y - 0.4):
            b.box(x - 0.12, x + 0.12, y - 0.12, y + 0.12, P, E, steel, 1.0)
        b.box(x - 0.08, x + 0.08, -Y, Y, E - 0.3, E, steel, 1.0)
    return b.finish()


# ---------------------------------------------------------------- watchtower (platform at 6 m)
def build_watchtower():
    b = Builder("Watchtower")
    wood = mat_ph("weathered_planks")
    roof = mat_ph("corrugated_iron_02")
    steel = mat_color("steel_dark", (0.09, 0.09, 0.085), metal=0.8, smooth=0.35)
    H, base, top = 6.0, 1.9, 1.55
    legs = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            a = Vector((sx * base, sy * base, -0.3))
            c = Vector((sx * top, sy * top, H))
            legs.append((a, c))
            d = (c - a).normalized()
            # square timber leg made from 4 faces
            t = 0.11
            pts = lambda p: [p + Vector((-t, -t, 0)), p + Vector((t, -t, 0)), p + Vector((t, t, 0)), p + Vector((-t, t, 0))]
            pa, pc = pts(a), pts(c)
            for i in range(4):
                j = (i + 1) % 4
                b.face([pa[i], pa[j], pc[j], pc[i]], wood, uvs=[(0, 0), (0.12, 0), (0.12, (c - a).length / 2), (0, (c - a).length / 2)])
    # bracing boards between adjacent legs in two tiers
    def lerp(p, q, t):
        return p + (q - p) * t
    order = [0, 1, 3, 2]
    for k in range(4):
        (a1, c1), (a2, c2) = legs[order[k]], legs[order[(k + 1) % 4]]
        for t0, t1 in ((0.05, 0.5), (0.5, 0.95)):
            for p, q in ((lerp(a1, c1, t0), lerp(a2, c2, t1)), (lerp(a2, c2, t0), lerp(a1, c1, t1))):
                b.cyl(p, q, 0.05, wood, seg=4, tile=2.0)
            hb = lerp(a1, c1, t1), lerp(a2, c2, t1)
            b.cyl(hb[0], hb[1], 0.06, wood, seg=4, tile=2.0)
    # deck + joists
    S = 1.75
    b.box(-S, S, -S, S, H, H + 0.12, wood, 2.0)
    # parapet (planks, 1.1 m) on 3 sides + half wall at the ladder side (Blender -Y = Unity north)
    pt = 0.06
    PH_ = 0.95  # waist-high parapet: a standing guard shows head and upper chest
    b.box(-S, S, S - pt, S, H + 0.12, H + PH_, wood, 2.0)
    b.box(-S, -S + pt, -S, S, H + 0.12, H + PH_, wood, 2.0)
    b.box(S - pt, S, -S, S, H + 0.12, H + PH_, wood, 2.0)
    b.box(-S, -0.45, -S, -S + pt, H + 0.12, H + PH_, wood, 2.0)
    b.box(0.45, S, -S, -S + pt, H + 0.12, H + PH_, wood, 2.0)
    # top rail
    for x0, x1, y0, y1 in ((-S - 0.04, S + 0.04, S - 0.1, S + 0.04), (-S - 0.04, S + 0.04, -S - 0.04, -S + 0.1),
                           (-S - 0.04, -S + 0.1, -S, S), (S - 0.1, S + 0.04, -S, S)):
        b.box(x0, x1, y0, y1, H + PH_, H + PH_ + 0.07, wood, 2.0)
    # roof posts + sloped corrugated roof
    for sx in (-1, 1):
        for sy in (-1, 1):
            b.box(sx * S - 0.05 - (0.0 if sx < 0 else 0.0), sx * S + 0.05, sy * S - 0.05, sy * S + 0.05, H + 0.12, H + 2.55 + 0.25 * sy, wood, 2.0)
    r = [Vector((-S - 0.35, -S - 0.35, H + 2.3)), Vector((S + 0.35, -S - 0.35, H + 2.3)),
         Vector((S + 0.35, S + 0.35, H + 2.85)), Vector((-S - 0.35, S + 0.35, H + 2.85))]
    b.face(r, roof, 2.7)
    b.face([p - Vector((0, 0, 0.03)) for p in r][::-1], roof, 2.7)
    # ladder on Blender -Y side from ground to deck opening
    for x in (-0.3, 0.3):
        b.box(x - 0.04, x + 0.04, -base - 0.55, -base - 0.47, -0.3, 0.05, wood, 2.0)
        b.cyl((x, -base - 0.5, -0.2), (x, -S - 0.08, H + 1.2), 0.04, wood, seg=4, tile=2.0)
    for i in range(1, 22):
        t = i / 22
        p = lerp(Vector((0, -base - 0.5, -0.2)), Vector((0, -S - 0.08, H + 1.2)), t)
        b.cyl((p.x - 0.3, p.y, p.z), (p.x + 0.3, p.y, p.z), 0.022, steel, seg=4)
    return b.finish()


# ---------------------------------------------------------------- terrain (Unity coordinates)
FENCE = (-46.0, 46.0, -41.0, 29.0)  # x0, x1, z0, z1 in Unity metres
ROAD = [(-10.0, -41.0), (-14.0, -60.0), (-30.0, -95.0), (-60.0, -150.0), (-95.0, -230.0), (-130.0, -400.0)]
SNIPERS = [(-56.0, 34.0), (60.0, -46.0)]


def road_dist(x, z):
    best = 1e9
    for (ax, az), (bx, bz) in zip(ROAD, ROAD[1:]):
        vx, vz = bx - ax, bz - az
        t = max(0, min(1, ((x - ax) * vx + (z - az) * vz) / (vx * vx + vz * vz)))
        best = min(best, math.hypot(x - ax - vx * t, z - az - vz * t))
    return best


def outside_dist(x, z):
    x0, x1, z0, z1 = FENCE
    dx = max(x0 - x, 0, x - x1)
    dz = max(z0 - z, 0, z - z1)
    return math.hypot(dx, dz)


def height(x, z):
    n = lambda s, f: noise.noise(Vector((x * f + s, z * f - s, s * 0.37)))
    od = outside_dist(x, z)
    h = 0.03 * n(1.0, 0.7)  # micro bumps on the pad
    if od > 0:
        k = min(1.0, od / 25.0)
        h += k * (0.6 * n(3.1, 0.03) + 0.25 * n(7.7, 0.11))
        # perimeter berm 14-30 m outside the fence
        h += 1.4 * math.exp(-((od - 22.0) / 7.0) ** 2) * (0.7 + 0.3 * n(2.2, 0.05))
        # distant hills, kept low toward the setting sun in the south-west
        r = math.hypot(x, z)
        if r > 110:
            ang = math.atan2(z, x)
            sw = 0.5 + 0.5 * math.cos(ang - math.radians(225))
            amp = (1.0 - 0.75 * sw) * 34.0 * min(1.0, (r - 110) / 220.0)
            h += amp * (0.55 + 0.45 * n(5.5, 0.006)) + 4.0 * n(9.3, 0.02) * min(1, (r - 110) / 60)
    for sx, sz in SNIPERS:
        d = math.hypot(x - sx, z - sz)
        h += 2.6 * math.exp(-(d / 9.0) ** 2)
    rd = road_dist(x, z)
    if rd < 9 and od > 0:
        w = max(0.0, 1 - rd / 9.0) ** 2
        h = h * (1 - w) + (0.6 * n(3.1, 0.03) * min(1, od / 25)) * w * 0.3
    return h


def build_terrain():
    dense = [i * 2.0 for i in range(-50, 51)]               # -100..100 every 2 m
    sparse = [-400, -340, -290, -250, -215, -185, -160, -140, -125, -112]
    xs = sparse + dense + [-v for v in reversed(sparse)]
    zs = xs
    pad = mat_ph("gravel_ground_01")
    dirt = mat_ph("dry_ground_rocks")
    road = mat_ph("dry_mud_field_001")
    grass = mat_ph("sparse_grass")
    b = Builder("Terrain")
    V = {}
    for ix, x in enumerate(xs):
        for iz, z in enumerate(zs):
            V[ix, iz] = b.bm.verts.new((-x, -z, height(x, z)))
    for ix in range(len(xs) - 1):
        for iz in range(len(zs) - 1):
            x = (xs[ix] + xs[ix + 1]) / 2
            z = (zs[iz] + zs[iz + 1]) / 2
            od = outside_dist(x, z)
            if od <= 0.0:
                m = pad
            elif road_dist(x, z) < 4.5:
                m = road
            elif math.hypot(x, z) > 125:
                m = grass
            else:
                m = dirt
            vs = [V[ix, iz], V[ix + 1, iz], V[ix + 1, iz + 1], V[ix, iz + 1]]
            f = b.bm.faces.new(vs)
            f.material_index = b.mi(m)
            tile = 4.0 if m in (pad, road, dirt) else 9.0
            for l in f.loops:
                c = l.vert.co
                l[b.uv].uv = (-c.x / tile, -c.y / tile)
    for f in b.bm.faces:
        f.normal_update()
        if f.normal.z < 0:
            f.normal_flip()
    ob = b.finish()
    me = ob.data
    me.shade_smooth() if hasattr(me, "shade_smooth") else None
    for p in me.polygons:
        p.use_smooth = True
    return ob


# ---------------------------------------------------------------- Poly Haven props
PROPS = {
    # asset: object name filter (None = all non-LOD parts joined; "LOD" = keep LOD0-2)
    "wooden_military_crate": None, "old_military_crate": None, "ammo_box": None,
    "Barrel_01": None, "Barrel_02": None, "barrel_03": None, "metal_jerrycan_green": None,
    "old_tyre": None, "portable_generator": None, "portable_searchlight": None,
    "concrete_road_barrier": "LOD", "security_light": None, "propane_tank": None,
    "plastic_crate_01": None, "industrial_pastic_container": None, "cement_bag": None,
    "wooden_crate_02": None, "steel_frame_shelves_01": None, "hanging_industrial_lamp": None,
    "old_military_compressor": None, "metal_trash_can": "metal_trash_can_rust",
    "dead_tree_trunk": None, "dead_tree_trunk_02": "LOD", "boulder_01": "LOD",
    "namaqualand_boulder_02": "LOD", "namaqualand_boulder_03": "LOD", "namaqualand_boulder_05": "LOD",
    "shrub_01": "LODVAR", "shrub_02": "LODVAR", "shrub_03": "LODVAR", "shrub_04": "LODVAR",
    "bolt_action_rifle_7_62": None, "modular_chainlink_fence": "FENCE",
}
FENCE_PARTS = ["modular_chainlink_fence", "modular_chainlink_fence_post", "modular_chainlink_fence_corner_outer",
               "modular_chainlink_fence_door_gate", "modular_chainlink_fence_door_frame", "modular_chainlink_fence_end_01"]


def classify_tex(path):
    base = os.path.basename(path).rsplit(".", 1)[0]
    base = base.rsplit("_", 1)[0] if base.split("_")[-1] in ("1k", "2k", "4k") else base
    for kind in ("nor_gl", "diff", "rough", "metal", "alpha", "arm", "ao", "mask", "translucent", "disp", "nor_dx", "spec"):
        if base.endswith("_" + kind):
            return kind
    return None


def register_ph_materials(objs, asset):
    tex_dir = os.path.join(PH, asset, "textures")
    for o in objs:
        for slot in o.material_slots:
            m = slot.material
            if not m or m.name in MANIFEST:
                continue
            files = {}
            if m.use_nodes:
                for n in m.node_tree.nodes:
                    if n.type == "TEX_IMAGE" and n.image:
                        f = os.path.basename(n.image.filepath.replace("\\", "/"))
                        k = classify_tex(f)
                        p = os.path.join(tex_dir, f)
                        if k and os.path.exists(p):
                            files.setdefault(k, p)
            MANIFEST[m.name] = {"ph": asset, "files": files}


def import_asset(asset):
    path = os.path.join(PH, asset, asset + ".blend")
    with bpy.data.libraries.load(path) as (src, dst):
        dst.objects = list(src.objects)
    objs = [o for o in dst.objects if o is not None]
    for o in objs:
        bpy.context.scene.collection.objects.link(o)
    return objs


def join(objs, name):
    meshes = [o for o in objs if o.type == "MESH"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.parent = None
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    ob.name = name
    ob.data.name = name
    return ob


def export_prop(asset, mode):
    reset()
    objs = import_asset(asset)
    register_ph_materials(objs, asset)
    out = os.path.join(UNITY_MODELS, "Props")
    for o in objs:
        o.parent = None
    if mode == "LOD":
        root = bpy.data.objects.new(asset, None)
        bpy.context.scene.collection.objects.link(root)
        keep = []
        for lod in range(3):
            c = [o for o in objs if o.name.endswith("_LOD%d" % lod)]
            if c:
                c[0].parent = root
                c[0].location = (0, 0, 0)
                keep.append(c[0])
        export([root], os.path.join(out, asset + ".fbx"))
    elif mode == "LODVAR":
        variants = sorted({o.name.rsplit("_LOD", 1)[0] for o in objs if "_LOD" in o.name})
        for v in variants:
            root = bpy.data.objects.new(v, None)
            bpy.context.scene.collection.objects.link(root)
            for lod in range(3):
                c = [o for o in objs if o.name == "%s_LOD%d" % (v, lod)]
                if c:
                    c[0].parent = root
                    c[0].location = (0, 0, 0)
            export([root], os.path.join(out, v + ".fbx"))
    elif mode == "FENCE":
        for name in FENCE_PARTS:
            c = [o for o in objs if o.name == name]
            if c:
                c[0].location = (0, 0, 0)
                export([c[0]], os.path.join(out, name + ".fbx"))
    else:
        if mode:
            objs = [o for o in objs if o.name.startswith(mode)]
        ob = join(objs, asset)
        ob.location = (0, 0, 0)
        export([ob], os.path.join(out, asset + ".fbx"))


def main():
    import sys
    if "--tower" in sys.argv:
        reset(); export([build_watchtower()], os.path.join(UNITY_MODELS, "Env", "Watchtower.fbx")); print("BUILD_ENV_DONE tower"); return
    if "--container" in sys.argv:
        reset(); export([build_container()], os.path.join(UNITY_MODELS, "Env", "Container40.fbx")); print("BUILD_ENV_DONE container"); return
    if "--terrain" in sys.argv:
        reset(); export([build_terrain()], os.path.join(UNITY_MODELS, "Env", "Terrain.fbx")); print("BUILD_ENV_DONE terrain"); return
    props_only = "--props" in sys.argv
    if props_only:
        old = json.load(open(os.path.join(BUILD, "materials.json")))
        MANIFEST.update(old)
        for asset, mode in PROPS.items():
            export_prop(asset, mode)
        json.dump(MANIFEST, open(os.path.join(BUILD, "materials.json"), "w"), indent=1)
        print("BUILD_ENV_DONE props")
        return
    reset()
    env = os.path.join(UNITY_MODELS, "Env")
    export([build_container()], os.path.join(env, "Container40.fbx"))
    reset()
    export([build_warehouse()], os.path.join(env, "Warehouse.fbx"))
    reset()
    export([build_watchtower()], os.path.join(env, "Watchtower.fbx"))
    reset()
    export([build_terrain()], os.path.join(env, "Terrain.fbx"))
    for asset, mode in PROPS.items():
        try:
            export_prop(asset, mode)
        except Exception as e:  # keep going; report at the end
            print("PROP FAILED", asset, e)
    with open(os.path.join(BUILD, "materials.json"), "w") as f:
        json.dump(MANIFEST, f, indent=1)
    print("BUILD_ENV_DONE", len(MANIFEST), "materials")


main()
