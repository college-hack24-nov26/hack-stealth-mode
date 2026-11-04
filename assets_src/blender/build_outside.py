"""The land outside the fence: photoscanned Poly Haven (CC0) sources -> Quest-cheap game assets.

Run: blender -b --factory-startup -P assets_src/blender/build_outside.py -- <poly haven source dir>

Writes into Stealth Mode/Assets/StealthMode:
  Textures/outside_cards/albedo.png   one atlas of trees, bushes and grass rendered in Cycles (cards cost 2 tris each)
  Models/Outside/*.fbx                big rocks, decimated to a few thousand tris
  Textures/<rock>/albedo.png
  Models/Outside/modular_chainlink_fence_lite.fbx   fence panel with 8-sided pipes instead of 3000 tris of tube
The sources are not needed again after this; the atlas cell layout is mirrored in Editor/OutsideWorldBuilder.cs.
"""
import bpy, math, os, sys, random
import numpy as np

SRC = sys.argv[sys.argv.index("--") + 1]
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
A = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode")
MODELS = os.path.join(A, "Models", "Outside")
TEX = os.path.join(A, "Textures")
os.makedirs(MODELS, exist_ok=True)

ATLAS = 2048
# name: (x, y, size) in atlas pixels, y from the top. Mirrored in OutsideWorldBuilder.Cells
CELLS = {
    "tree_a": (0, 0, 1024), "tree_b": (1024, 0, 1024),
    "bush_a": (0, 1024, 512), "bush_b": (512, 1024, 512), "bush_c": (1024, 1024, 512), "bush_d": (1536, 1024, 512),
    "bush_e": (0, 1536, 512), "bush_f": (512, 1536, 512), "grass_a": (1024, 1536, 512), "grass_b": (1536, 1536, 512),
}


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def fbx(asset):
    bpy.ops.import_scene.fbx(filepath=f"{SRC}/{asset}/{asset}.fbx")
    return [o for o in bpy.data.objects if o.type == "MESH"]


def image(asset, name):
    p = f"{SRC}/{asset}/textures/{name}"
    return bpy.data.images.load(p) if os.path.exists(p) else None


GREEN, BARK = (0.78, 1.12, 0.62), (0.62, 0.42, 0.27)


def leaf_material(m, diff, alpha, tint=(1, 1, 1), sat=1.0):
    m.use_nodes = True
    t = m.node_tree
    t.nodes.clear()
    out = t.nodes.new("ShaderNodeOutputMaterial")
    b = t.nodes.new("ShaderNodeBsdfPrincipled")
    b.inputs["Roughness"].default_value = 0.6
    t.links.new(b.outputs[0], out.inputs[0])
    d = t.nodes.new("ShaderNodeTexImage")
    d.image = diff
    hs = t.nodes.new("ShaderNodeHueSaturation")
    hs.inputs["Saturation"].default_value = sat
    t.links.new(d.outputs["Color"], hs.inputs["Color"])
    mx = t.nodes.new("ShaderNodeMix")
    mx.data_type = "RGBA"
    mx.blend_type = "MULTIPLY"
    mx.inputs[0].default_value = 1
    t.links.new(hs.outputs[0], mx.inputs[6])
    mx.inputs[7].default_value = (*tint, 1)
    t.links.new(mx.outputs[2], b.inputs["Base Color"])
    if alpha is not None:
        a = t.nodes.new("ShaderNodeTexImage")
        a.image = alpha
        alpha.colorspace_settings.name = "Non-Color"
        t.links.new(a.outputs["Color"], b.inputs["Alpha"])
        b.inputs["Transmission Weight"].default_value = 0.0


def setup_render(res):
    s = bpy.context.scene
    s.render.engine = "CYCLES"
    pref = bpy.context.preferences.addons["cycles"].preferences
    pref.compute_device_type = "METAL"
    pref.get_devices()
    for d in pref.devices:
        d.use = True
    s.cycles.device = "GPU"
    s.cycles.samples = 48
    s.cycles.use_denoising = False
    s.cycles.transparent_max_bounces = 24
    s.cycles.max_bounces = 4
    s.render.film_transparent = True
    s.render.resolution_x = s.render.resolution_y = res
    s.render.image_settings.file_format = "PNG"
    s.render.image_settings.color_mode = "RGBA"
    s.view_settings.view_transform = "Standard"
    s.view_settings.look = "None"
    w = bpy.data.worlds.new("soft")
    w.use_nodes = True
    bg = w.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.95, 0.97, 1.0, 1)
    bg.inputs[1].default_value = 0.85
    s.world = w
    sun = bpy.data.objects.new("key", bpy.data.lights.new("key", "SUN"))
    sun.data.energy = 1.6
    sun.data.angle = math.radians(40)
    sun.rotation_euler = (math.radians(25), 0, 0)     # high and soft: shape without a direction the cards would betray
    s.collection.objects.link(sun)


def render_cell(objs, yaw, res, out):
    """Orthographic side view of objs, ground at the bottom edge of the cell, framed to the widest extent."""
    s = bpy.context.scene
    bpy.context.view_layer.update()
    pts = np.array([o.matrix_world @ o.data.vertices[i].co for o in objs
                    for i in range(0, len(o.data.vertices), max(1, len(o.data.vertices) // 4000))])
    lo, hi = pts.min(0), pts.max(0)
    c = (lo + hi) / 2
    size = max(hi[2] - lo[2], float(np.hypot(*(hi[:2] - lo[:2])))) * 1.02
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = size
    cam.data.clip_end = 1000
    a = math.radians(yaw)
    cam.location = (c[0] + math.sin(a) * 60, c[1] - math.cos(a) * 60, lo[2] + size / 2)
    cam.rotation_euler = (math.radians(90), 0, a)
    s.collection.objects.link(cam)
    s.camera = cam
    s.render.filepath = out
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam)
    return size


def tree_cells(tmp):
    reset()
    objs = fbx("island_tree_02")
    leaves, base, br = (image("island_tree_02", f"island_tree_02{n}_2k.png") for n in ("_leaves_diff", "", "_branches_diff"))
    base = image("island_tree_02", "island_tree_02_diff_2k.jpg")
    la = image("island_tree_02", "island_tree_02_leaves_alpha_2k.png")
    for m in bpy.data.materials:
        if m.name.endswith("_leaves"):
            leaf_material(m, leaves, la, GREEN, 1.3)
        elif m.name.endswith("_branches"):
            leaf_material(m, br, None, BARK)
        else:
            leaf_material(m, base, None, BARK)
    setup_render(1024)
    render_cell(objs, 0, 1024, f"{tmp}/tree_a.png")
    render_cell(objs, 90, 1024, f"{tmp}/tree_b.png")


def bush_cells(tmp):
    reset()
    objs = fbx("searsia_lucida")
    d = image("searsia_lucida", "searsia_lucida_diff_2k.jpg")
    al = image("searsia_lucida", "searsia_lucida_alpha_2k.png")
    for m in bpy.data.materials:
        if m.name.endswith("_leaves"):
            leaf_material(m, d, al, GREEN, 1.3)
        else:
            leaf_material(m, d, None if m.name == "searsia_lucida" else al, BARK)
    setup_render(512)
    by = {o.name.split("_LOD")[0][-1]: o for o in objs}
    for i, k in enumerate("abcdef"):
        o = by.get(k)
        if o is None:
            continue
        for other in objs:
            other.hide_render = other is not o
        render_cell([o], 30 * i, 512, f"{tmp}/bush_{k}.png")


def grass_cells(tmp):
    reset()
    src = fbx("grass_medium_02")
    d = image("grass_medium_02", "grass_medium_02_diff_2k.jpg")
    al = image("grass_medium_02", "grass_medium_02_alpha_2k.png")
    for m in bpy.data.materials:
        leaf_material(m, d, al, (0.85, 1.1, 0.7), 1.25)
    setup_render(512)
    rnd = random.Random(7)
    for name, count, half in (("grass_a", 70, 0.55), ("grass_b", 110, 0.8)):
        made = []
        for i in range(count):
            o = rnd.choice(src).copy()
            o.hide_render = False
            bpy.context.scene.collection.objects.link(o)
            o.location = (rnd.uniform(-half, half), rnd.uniform(-half * 0.5, half * 0.5), 0)
            o.rotation_euler = (0, 0, rnd.uniform(0, 6.28))
            s = rnd.uniform(1.0, 1.7)
            o.scale = (s, s, s * rnd.uniform(1.1, 1.8))
            made.append(o)
        for o in src:
            o.hide_render = True
        render_cell(made, 0, 512, f"{tmp}/{name}.png")
        for o in made:
            bpy.data.objects.remove(o)


def bleed(rgba):
    """Grow colour out past the alpha edge so mip maps and MSAA never pick up the black background."""
    rgb, a = rgba[..., :3].copy(), rgba[..., 3]
    filled = a > 0.02
    for _ in range(24):
        grow = np.zeros_like(rgb)
        n = np.zeros(a.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            f = np.roll(filled, (dy, dx), (0, 1))
            grow += np.roll(rgb, (dy, dx), (0, 1)) * f[..., None]
            n += f
        new = (~filled) & (n > 0)
        rgb[new] = grow[new] / n[new, None]
        filled = filled | new
    rgb[~filled] = rgb[filled].mean(0)
    return np.dstack([rgb, a])


def atlas(tmp):
    reset()
    out = np.zeros((ATLAS, ATLAS, 4), np.float32)
    for name, (x, y, size) in CELLS.items():
        p = f"{tmp}/{name}.png"
        img = bpy.data.images.load(p)
        px = np.array(img.pixels[:], np.float32).reshape(img.size[1], img.size[0], 4)[::-1]
        # straight alpha: Blender gives premultiplied-free PNG data, keep it as saved
        out[y:y + size, x:x + size] = bleed(px)
    d = os.path.join(TEX, "outside_cards")
    os.makedirs(d, exist_ok=True)
    im = bpy.data.images.new("outside_cards", ATLAS, ATLAS, alpha=True)
    im.colorspace_settings.name = "Non-Color"     # the values are already display-referred sRGB from the PNGs
    im.pixels = out[::-1].ravel()
    im.filepath_raw = os.path.join(d, "albedo.png")
    im.file_format = "PNG"
    im.save()


def save_scaled(path_in, path_out, size, non_color):
    img = bpy.data.images.load(path_in)
    if non_color:
        img.colorspace_settings.name = "Non-Color"
    img.scale(size, size)
    img.file_format = "PNG"
    img.filepath_raw = path_out
    s = bpy.context.scene
    s.render.image_settings.file_format = "PNG"
    s.render.image_settings.color_mode = "RGB"
    img.save_render(path_out, scene=s) if path_in.endswith(".exr") else img.save()


def export_selected(objs, path):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z", axis_up="Y", bake_space_transform=True, object_types={"MESH"},
        mesh_smooth_type="FACE", use_mesh_modifiers=True, add_leaf_bones=False, path_mode="STRIP",
    )


def rocks():
    for asset, pick, cap in (("rock_face_01", None, 2500), ("rock_face_02", None, 3000),
                             ("namaqualand_cliff_01", "_LOD0", 4000)):
        reset()
        objs = fbx(asset)
        o = next((x for x in objs if pick and x.name.endswith(pick)), objs[0])
        for x in objs:
            if x is not o:
                bpy.data.objects.remove(x)
        tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
        mod = o.modifiers.new("cap", "DECIMATE")
        mod.ratio = min(1.0, cap / tris)
        mod.use_collapse_triangulate = True
        o.name = asset
        for m in o.data.materials:
            m.name = asset
        # rest the rock on its base at the origin so scattering only has to set a ground height
        bpy.context.view_layer.update()
        zs = [(o.matrix_world @ v.co).z for v in o.data.vertices]
        o.location.z -= min(zs)
        export_selected([o], os.path.join(MODELS, asset + ".fbx"))
        d = os.path.join(TEX, asset)
        os.makedirs(d, exist_ok=True)
        save_scaled(f"{SRC}/{asset}/textures/{asset}_diff_2k.jpg", os.path.join(d, "albedo.png"), 1024, False)


def ground():
    for asset in ("aerial_grass_rock", "rocky_terrain_02"):
        reset()
        d = os.path.join(TEX, asset)
        os.makedirs(d, exist_ok=True)
        save_scaled(f"{SRC}/{asset}/Diffuse.jpg", os.path.join(d, "albedo.png"), 1024, False)


def fence():
    reset()
    bpy.ops.import_scene.fbx(filepath=os.path.join(A, "Models", "Props", "modular_chainlink_fence.fbx"))
    o = next(x for x in bpy.data.objects if x.type == "MESH")
    bpy.context.view_layer.objects.active = o
    o.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="DESELECT")
    bpy.ops.object.mode_set(mode="OBJECT")
    posts = next(i for i, m in enumerate(o.data.materials) if "posts" in m.name)
    for p in o.data.polygons:
        p.select = p.material_index == posts
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.decimate(ratio=0.13)
    bpy.ops.object.mode_set(mode="OBJECT")
    print("FENCE tris", sum(len(p.vertices) - 2 for p in o.data.polygons), [m.name for m in o.data.materials])
    o.name = "modular_chainlink_fence_lite"
    export_selected([o], os.path.join(MODELS, "modular_chainlink_fence_lite.fbx"))


tmp = os.path.join(os.path.dirname(SRC.rstrip("/")), "cards_tmp")
os.makedirs(tmp, exist_ok=True)
step = sys.argv[sys.argv.index("--") + 2] if len(sys.argv) > sys.argv.index("--") + 2 else "all"
if step in ("all", "cards"):
    tree_cells(tmp)
    bush_cells(tmp)
    grass_cells(tmp)
    atlas(tmp)
if step == "grass":
    grass_cells(tmp)
    atlas(tmp)
if step in ("all", "rocks"):
    rocks()
    ground()
if step in ("all", "fence"):
    fence()
print("OUTSIDE DONE", step)
