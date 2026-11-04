"""The far land as one painting: Cycles renders mountains, forest and haze around the Engineer, under the project's sunset sky,
and the result is laid over that sky into a single panorama. On the Quest the whole world past the near hills costs one
textured dome, however many million triangles went into the render.

Run: blender -b --factory-startup -P assets_src/blender/build_backdrop.py -- <tree source dir> <width> <samples> <out png>
Everything is generated here except the tree models and two ground photos, read from what is already on disk. No downloads.
The output keeps sky.png's mapping exactly, so the dome's UVs and the sun direction stay as they are.
"""
import bpy, math, os, sys
import numpy as np
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
SRC, WIDTH, SAMPLES, OUT = argv[0], int(argv[1]), int(argv[2]), argv[3]
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TEX = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Textures")
SKY = os.path.join(TEX, "Sky", "sky.png")
EYE = 1.7
EL_LO, EL_HI = -7.0, 22.0          # the band of elevation angles that holds land; the rest of the panorama is sky.png as it is
SUN_U, SUN_EL = 0.651, 6.0
HAZE_M = 2600.0                    # distance over which the air swallows about two thirds of the light

SUN_AZ = (0.5 - SUN_U) * 2 * math.pi    # Cycles equirect: u = -atan2(y, x) / 2pi + 0.5


def S(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def lattice(ix, iy, seed):
    h = (ix.astype(np.uint32) * np.uint32(374761393) + iy.astype(np.uint32) * np.uint32(668265263) + np.uint32(seed * 2246822519 % 2**32))
    h = (h ^ (h >> np.uint32(13))) * np.uint32(1274126177)
    return ((h ^ (h >> np.uint32(16))) & np.uint32(0xFFFFFF)).astype(np.float32) / np.float32(0xFFFFFF)


def noise(x, y, seed):
    ix, iy = np.floor(x).astype(np.int64), np.floor(y).astype(np.int64)
    fx, fy = x - ix, y - iy
    u, v = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    a, b = lattice(ix, iy, seed), lattice(ix + 1, iy, seed)
    c, d = lattice(ix, iy + 1, seed), lattice(ix + 1, iy + 1, seed)
    return (a + (b - a) * u) * (1 - v) + (c + (d - c) * u) * v


def fbm(x, y, octaves, seed):
    v, amp, f, tot = 0.0, 0.5, 1.0, 0.0
    for i in range(octaves):
        v = v + amp * noise(x * f, y * f, seed + i)
        tot += amp
        amp *= 0.52
        f *= 2.03
    return v / tot


def ridged(x, y, octaves, seed):
    v, amp, f, w, tot = 0.0, 0.5, 1.0, 1.0, 0.0
    for i in range(octaves):
        n = 1 - np.abs(2 * noise(x * f, y * f, seed + i) - 1)
        n = n * n * w
        w = np.clip(n * 2, 0, 1)
        v = v + amp * n
        tot += amp
        amp *= 0.5
        f *= 2.1
    return v / tot


def height(x, y):
    r = np.hypot(x, y)
    roll = fbm(x / 420, y / 420, 7, 11)
    mountains = ridged(x / 2300, y / 2300, 8, 31)
    h = S(150, 1400, r) * (10 + 210 * roll * roll + 40 * roll) + S(800, 4200, r) * (mountains * 820 + roll * 110)
    toward = np.cos(np.arctan2(y, x) - SUN_AZ)
    return h * (1 - 0.72 * S(0.72, 0.96, toward) * S(200, 900, r) * (1 - S(3500, 7000, r)))


def forest(x, y):
    return S(0.42, 0.56, fbm(x / 520, y / 520, 5, 51))


def land():
    rings, spokes = 1500, 2600
    r = 150 * (9500 / 150) ** (np.arange(rings) / (rings - 1))
    a = np.arange(spokes) / spokes * 2 * math.pi
    R, A = np.meshgrid(r, a, indexing="ij")
    x, y = (R * np.cos(A)).ravel(), (R * np.sin(A)).ravel()
    z = height(x, y)
    n = len(x)
    i = np.arange(rings - 1)[:, None] * spokes
    j = np.arange(spokes)[None, :]
    j2 = (j + 1) % spokes
    q = np.stack([i + j, i + j2, i + spokes + j2, i + spokes + j], -1).reshape(-1, 4)
    me = bpy.data.meshes.new("land")
    me.vertices.add(n)
    me.vertices.foreach_set("co", np.stack([x, y, z], 1).astype(np.float32).ravel())
    me.loops.add(q.size)
    me.loops.foreach_set("vertex_index", q.ravel().astype(np.int32))
    me.polygons.add(len(q))
    me.polygons.foreach_set("loop_start", (np.arange(len(q)) * 4).astype(np.int32))
    me.update(calc_edges=True)
    me.shade_smooth()
    # slope from the height function: rock where the ground is steep, forest floor where trees stand
    e = 6.0
    gx, gy = (height(x + e, y) - z) / e, (height(x, y + e) - z) / e
    slope = np.hypot(gx, gy)
    rock = S(0.38, 0.72, slope + (fbm(x / 90, y / 90, 4, 71) - 0.5) * 0.3)
    wood = forest(x, y) * (1 - rock)
    col = me.color_attributes.new("mix", "FLOAT_COLOR", "POINT")
    col.data.foreach_set("color", np.stack([rock, wood, fbm(x / 170, y / 170, 5, 81), np.ones(n)], 1).astype(np.float32).ravel())
    ob = bpy.data.objects.new("land", me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def haze_sky():
    """The sky shrunk to a few blurred pixels: air has the sky's colour, never its clouds."""
    im = bpy.data.images.get("haze")
    if im is None:
        im = bpy.data.images.load(SKY).copy()
        im.name = "haze"
        im.scale(48, 24)
        im.pack()
    return im


def haze_wrap(m, bsdf_out, alpha_out=None):
    """Air between the eye and this surface, taken from the sky at the horizon in that same direction."""
    t = m.node_tree
    out = next(n for n in t.nodes if n.type == "OUTPUT_MATERIAL")
    geo = t.nodes.new("ShaderNodeNewGeometry")
    flat = t.nodes.new("ShaderNodeVectorMath")
    flat.operation = "MULTIPLY"
    flat.inputs[1].default_value = (-1, -1, 0)
    t.links.new(geo.outputs["Incoming"], flat.inputs[0])
    up = t.nodes.new("ShaderNodeVectorMath")
    up.operation = "ADD"
    up.inputs[1].default_value = (0, 0, 0.1)
    t.links.new(flat.outputs[0], up.inputs[0])
    norm = t.nodes.new("ShaderNodeVectorMath")
    norm.operation = "NORMALIZE"
    t.links.new(up.outputs[0], norm.inputs[0])
    env = t.nodes.new("ShaderNodeTexEnvironment")
    env.image = haze_sky()
    t.links.new(norm.outputs[0], env.inputs["Vector"])
    emit = t.nodes.new("ShaderNodeEmission")
    t.links.new(env.outputs[0], emit.inputs[0])
    cam = t.nodes.new("ShaderNodeCameraData")
    d = t.nodes.new("ShaderNodeMath")
    d.operation = "DIVIDE"
    d.inputs[1].default_value = -HAZE_M
    t.links.new(cam.outputs["View Distance"], d.inputs[0])
    ex = t.nodes.new("ShaderNodeMath")
    ex.operation = "EXPONENT"
    t.links.new(d.outputs[0], ex.inputs[0])
    mix = t.nodes.new("ShaderNodeMixShader")
    t.links.new(ex.outputs[0], mix.inputs[0])
    t.links.new(emit.outputs[0], mix.inputs[1])
    t.links.new(bsdf_out, mix.inputs[2])
    final = mix.outputs[0]
    if alpha_out is not None:
        cut = t.nodes.new("ShaderNodeMixShader")
        tr = t.nodes.new("ShaderNodeBsdfTransparent")
        t.links.new(alpha_out, cut.inputs[0])
        t.links.new(tr.outputs[0], cut.inputs[1])
        t.links.new(final, cut.inputs[2])
        final = cut.outputs[0]
    t.links.new(final, out.inputs["Surface"])


def tinted(t, colour_socket, tint, sat=1.0):
    hs = t.nodes.new("ShaderNodeHueSaturation")
    hs.inputs["Saturation"].default_value = sat
    t.links.new(colour_socket, hs.inputs["Color"])
    mx = t.nodes.new("ShaderNodeMix")
    mx.data_type = "RGBA"
    mx.blend_type = "MULTIPLY"
    mx.inputs[0].default_value = 1
    t.links.new(hs.outputs[0], mx.inputs[6])
    mx.inputs[7].default_value = (*tint, 1)
    return mx.outputs[2]


def land_material(ob):
    m = bpy.data.materials.new("land")
    m.use_nodes = True
    t = m.node_tree
    b = t.nodes["Principled BSDF"]
    b.inputs["Roughness"].default_value = 0.9
    co = t.nodes.new("ShaderNodeTexCoord")

    def photo(path, metres):
        mp = t.nodes.new("ShaderNodeMapping")
        mp.inputs["Scale"].default_value = (1 / metres, 1 / metres, 1)
        t.links.new(co.outputs["Object"], mp.inputs["Vector"])
        im = t.nodes.new("ShaderNodeTexImage")
        im.image = bpy.data.images.load(path)
        t.links.new(mp.outputs[0], im.inputs["Vector"])
        return im.outputs["Color"]

    grass = tinted(t, photo(os.path.join(TEX, "rocky_terrain_02", "albedo.png"), 130), (1.9, 2.2, 1.7), 1.15)
    rock = tinted(t, photo(os.path.join(TEX, "gravel_ground_01", "albedo.png"), 170), (0.75, 0.72, 0.68))
    at = t.nodes.new("ShaderNodeAttribute")
    at.attribute_name = "mix"
    sep = t.nodes.new("ShaderNodeSeparateColor")
    t.links.new(at.outputs["Color"], sep.inputs[0])
    dark = t.nodes.new("ShaderNodeMix")           # under the trees the ground is dark
    dark.data_type = "RGBA"
    dark.blend_type = "MULTIPLY"
    t.links.new(sep.outputs[1], dark.inputs[0])
    t.links.new(grass, dark.inputs[6])
    dark.inputs[7].default_value = (0.1, 0.17, 0.07, 1)
    mx = t.nodes.new("ShaderNodeMix")
    mx.data_type = "RGBA"
    t.links.new(sep.outputs[0], mx.inputs[0])
    t.links.new(dark.outputs[2], mx.inputs[6])
    t.links.new(rock, mx.inputs[7])
    t.links.new(mx.outputs[2], b.inputs["Base Color"])
    bump = t.nodes.new("ShaderNodeBump")
    bump.inputs["Distance"].default_value = 14
    tex = t.nodes.new("ShaderNodeTexNoise")
    tex.inputs["Scale"].default_value = 0.02
    tex.inputs["Detail"].default_value = 12
    t.links.new(co.outputs["Object"], tex.inputs["Vector"])
    t.links.new(tex.outputs[0], bump.inputs["Height"])
    t.links.new(bump.outputs[0], b.inputs["Normal"])
    haze_wrap(m, b.outputs[0])
    ob.data.materials.append(m)


def leaf(m, diff, alpha, tint, sat):
    m.use_nodes = True
    t = m.node_tree
    t.nodes.clear()
    t.nodes.new("ShaderNodeOutputMaterial")
    b = t.nodes.new("ShaderNodeBsdfPrincipled")
    b.inputs["Roughness"].default_value = 0.65
    d = t.nodes.new("ShaderNodeTexImage")
    d.image = diff
    t.links.new(tinted(t, d.outputs["Color"], tint, sat), b.inputs["Base Color"])
    a = None
    if alpha is not None:
        n = t.nodes.new("ShaderNodeTexImage")
        n.image = alpha
        alpha.colorspace_settings.name = "Non-Color"
        a = n.outputs["Color"]
    haze_wrap(m, b.outputs[0], a)


GREEN, BARK = (0.78, 1.1, 0.62), (0.62, 0.42, 0.27)


def load_tree(asset, pick):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=f"{SRC}/{asset}/{asset}.fbx")
    new = [o for o in bpy.data.objects if o not in before]
    keep = next(o for o in new if o.type == "MESH" and pick in o.name)
    for o in new:
        if o is not keep:
            bpy.data.objects.remove(o)
    keep.data.transform(keep.matrix_world)
    keep.matrix_world.identity()
    vs = np.empty(len(keep.data.vertices) * 3, np.float32)
    keep.data.vertices.foreach_get("co", vs)
    vs = vs.reshape(-1, 3)
    c = (vs.min(0) + vs.max(0)) / 2
    keep.data.transform(__import__("mathutils").Matrix.Translation((-c[0], -c[1], -vs[:, 2].min())))
    img = lambda n: bpy.data.images.load(f"{SRC}/{asset}/textures/{n}")
    for m in keep.data.materials:
        if asset == "island_tree_02":
            if m.name.endswith("_leaves"):
                leaf(m, img("island_tree_02_leaves_diff_2k.png"), img("island_tree_02_leaves_alpha_2k.png"), GREEN, 1.3)
            elif m.name.endswith("_branches"):
                leaf(m, img("island_tree_02_branches_diff_2k.png"), None, BARK, 1.0)
            else:
                leaf(m, img("island_tree_02_diff_2k.jpg"), None, BARK, 1.0)
        elif m.name == "searsia_lucida":
            leaf(m, img("searsia_lucida_diff_2k.jpg"), None, BARK, 1.0)
        else:
            leaf(m, img("searsia_lucida_diff_2k.jpg"), img("searsia_lucida_alpha_2k.png"), GREEN, 1.3)
    keep.hide_render = True
    return keep


def plant(trees, land_ob):
    rng = np.random.default_rng(2611)
    n = 900000
    r = 300 + 3300 * rng.random(n) ** 1.3
    a = rng.random(n) * 2 * math.pi
    x, y = r * np.cos(a), r * np.sin(a)
    keep = rng.random(n) < forest(x, y) * 0.9 + 0.006
    e = 6.0
    z = height(x, y)
    slope = np.hypot(height(x + e, y) - z, height(x, y + e) - z) / e
    keep &= slope < 0.62
    x, y, z = x[keep], y[keep], z[keep]
    x, y, z = x[:170000], y[:170000], z[:170000]
    print("TREES", len(x), flush=True)
    kinds = [(trees[0], (3.6, 4.5, 5.4)), (trees[1], (4.0, 5.0, 6.2))]
    bucket = rng.integers(0, 24, len(x))
    for b in range(24):
        src, scales = kinds[b % 2]
        s = scales[(b // 2) % 3]
        yaw = (b // 6) * math.pi / 2 + 0.4
        sel = bucket == b
        me = bpy.data.meshes.new(f"spots{b}")
        me.vertices.add(int(sel.sum()))
        me.vertices.foreach_set("co", np.stack([x[sel], y[sel], z[sel] - 0.25], 1).astype(np.float32).ravel())
        p = bpy.data.objects.new(f"spots{b}", me)
        bpy.context.scene.collection.objects.link(p)
        p.instance_type = "VERTS"
        p.show_instancer_for_render = False
        c = bpy.data.objects.new(f"tree{b}", src.data)
        bpy.context.scene.collection.objects.link(c)
        c.parent = p
        c.scale = (s, s, s * 1.05)
        c.rotation_euler = (0, 0, yaw)


def world():
    w = bpy.data.worlds.new("sky")
    w.use_nodes = True
    t = w.node_tree
    env = t.nodes.new("ShaderNodeTexEnvironment")
    env.image = bpy.data.images.load(SKY, check_existing=True)
    t.links.new(env.outputs[0], t.nodes["Background"].inputs[0])
    t.nodes["Background"].inputs[1].default_value = 1.0
    bpy.context.scene.world = w
    el = math.radians(SUN_EL)
    sun_dir = Vector((math.cos(el) * math.cos(SUN_AZ), math.cos(el) * math.sin(SUN_AZ), math.sin(el)))
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 5.0
    sun.data.color = (1.0, 0.62, 0.36)
    sun.data.angle = math.radians(2.5)
    sun.rotation_mode = "QUATERNION"
    sun.rotation_quaternion = sun_dir.to_track_quat("Z", "Y")
    bpy.context.scene.collection.objects.link(sun)


def camera_and_render():
    s = bpy.context.scene
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = "PANO"
    cam.data.panorama_type = "EQUIRECTANGULAR"
    cam.data.clip_end = 30000
    cam.location = (0, 0, EYE)
    cam.rotation_euler = (math.radians(90), 0, math.radians(-90))
    s.collection.objects.link(cam)
    s.camera = cam
    s.render.engine = "CYCLES"
    pref = bpy.context.preferences.addons["cycles"].preferences
    pref.compute_device_type = "METAL"
    pref.get_devices()
    for d in pref.devices:
        d.use = True
    s.cycles.device = "GPU"
    s.cycles.samples = SAMPLES
    s.cycles.use_denoising = True
    s.cycles.denoiser = "OPENIMAGEDENOISE"
    s.cycles.max_bounces = 3
    s.cycles.use_light_tree = False      # the haze is emissive: without these every tree in the forest becomes a light to sort
    for mat in bpy.data.materials:
        mat.cycles.emission_sampling = "NONE"
    s.cycles.transparent_max_bounces = 10
    s.render.film_transparent = True
    s.render.resolution_x = WIDTH
    s.render.resolution_y = WIDTH // 2
    s.render.use_border = True
    s.render.use_crop_to_border = True
    s.render.border_min_x, s.render.border_max_x = 0, 1
    s.render.border_min_y, s.render.border_max_y = 0.5 + EL_LO / 180, 0.5 + EL_HI / 180
    s.render.image_settings.file_format = "PNG"
    s.render.image_settings.color_mode = "RGBA"
    s.view_settings.view_transform = "Standard"
    s.view_settings.look = "None"
    s.render.filepath = OUT
    bpy.ops.render.render(write_still=True)


bpy.ops.wm.read_factory_settings(use_empty=True)
ob = land()
land_material(ob)
trees = [load_tree("island_tree_02", "island_tree_02"), load_tree("searsia_lucida", "searsia_lucida_a")]
plant(trees, ob)
world()
camera_and_render()
print("BACKDROP DONE", OUT)
