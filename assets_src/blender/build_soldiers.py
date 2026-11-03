"""Builds the soldier characters (MPFB human + combat uniform + gear) and exports Unity-ready FBX.

Run:  Blender -b -P assets_src/blender/build_soldiers.py [-- Variant ...]

Each FBX holds one armature (mixamo_unity rig, Unity Humanoid friendly) and two skinned meshes:
<Variant>_LOD0 (full) and <Variant>_LOD1 (~35%). Materials (named, textures set up in Unity):
skin_<tone>, uniform_<side>, gear_<side>, boots, hair, eyes, brows, helmet_<side>.
"""
import bpy, bmesh, os, sys, math, shutil
from mathutils import Vector
from bl_ext.blender_org.mpfb.services.humanservice import HumanService

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
D = os.path.expanduser("~/Library/Application Support/Blender/5.2/extensions/.user/blender_org/mpfb/data")
OUT = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Models", "Characters")
TEX = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode", "Textures", "Characters")

VARIANTS = {
    # Indian squad (Comms, Captain, Paramedic, Snipers)
    "Soldier_IN": dict(side="india", skin="mindfront_skin_male_african_middleage", tone="in", hair="short02", head="helmet",
                       macro=dict(gender=1.0, age=0.42, muscle=0.72, weight=0.55, proportions=0.62, height=0.55,
                                  race=dict(asian=0.22, caucasian=0.58, african=0.20))),
    # enemy: helmeted guard
    "Enemy_A": dict(side="enemy", skin="mindfront_skin_male_african_middleage", tone="en", hair="short02", head="helmet",
                    macro=dict(gender=1.0, age=0.5, muscle=0.6, weight=0.6, proportions=0.5, height=0.5,
                               race=dict(asian=0.2, caucasian=0.55, african=0.25))),
    # enemy: bearded, knit cap
    "Enemy_B": dict(side="enemy", skin="jartur69_middleage_slavic_male_with_genitals_and_beard", tone="enb", hair="short02", head="cap",
                    macro=dict(gender=1.0, age=0.58, muscle=0.55, weight=0.62, proportions=0.5, height=0.52,
                               race=dict(asian=0.15, caucasian=0.6, african=0.25))),
}


def mhclo(kind, name):
    d = os.path.join(D, kind, name)
    return os.path.join(d, [f for f in os.listdir(d) if f.endswith(".mhclo") or f.endswith(".proxy")][0])


def tris(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


def set_active(o):
    bpy.ops.object.select_all(action="DESELECT")
    o.select_set(True)
    bpy.context.view_layer.objects.active = o


def apply_mods(o, kinds=("MASK",)):
    set_active(o)
    if o.data.shape_keys:  # bake the MPFB body-shape targets so modifiers can be applied
        bpy.ops.object.shape_key_remove(all=True, apply_mix=True)
    for m in list(o.modifiers):
        if m.type in kinds:
            bpy.ops.object.modifier_apply(modifier=m.name)


def uv_box(o, tile):
    """World-space box projection UVs (metres / tile) on a fresh UV map."""
    me = o.data
    bm = bmesh.new(); bm.from_mesh(me)
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for l in f.loops:
            c = o.matrix_world @ l.vert.co
            u, v = ((c.x, c.y) if ax == 2 else (c.x, c.z) if ax == 1 else (c.y, c.z))
            l[uv].uv = (u / tile, v / tile)
    bm.to_mesh(me); bm.free()


def material(name):
    return bpy.data.materials.get(name) or bpy.data.materials.new(name)


def bone_pos(rig, key):
    for b in rig.data.bones:
        if b.name.split(":")[-1] == key:
            return rig.matrix_world @ b.head_local, rig.matrix_world @ b.tail_local, b.name
    raise KeyError(key)


def rigid_bind(o, rig, bone_name):
    o.vertex_groups.clear()
    vg = o.vertex_groups.new(name=bone_name)
    vg.add(list(range(len(o.data.vertices))), 1.0, "REPLACE")
    o.parent = rig
    m = o.modifiers.new("Armature", "ARMATURE"); m.object = rig


def transfer_weights(target, source, rig):
    target.parent = rig
    set_active(target)
    dt = target.modifiers.new("dt", "DATA_TRANSFER")
    dt.object = source
    dt.use_vert_data = True
    dt.data_types_verts = {"VGROUP_WEIGHTS"}
    dt.vert_mapping = "POLYINTERP_NEAREST"
    dt.layers_vgroup_select_src = "ALL"
    dt.layers_vgroup_select_dst = "NAME"
    bpy.ops.object.datalayout_transfer(modifier="dt")
    bpy.ops.object.modifier_apply(modifier="dt")
    m = target.modifiers.new("Armature", "ARMATURE"); m.object = rig


def head_frame(eyes):
    """Eye-centre position (MPFB faces -Y); helmet/cap are placed relative to the eyes."""
    ws = [eyes.matrix_world @ v.co for v in eyes.data.vertices]
    return Vector((sum(v.x for v in ws) / len(ws), max(v.y for v in ws) - 0.012, sum(v.z for v in ws) / len(ws)))


def reweight(o, body, rig):
    """Clothes/eyes/brows inherit skin weights from the body surface (MPFB leaves them unweighted here)."""
    for m in list(o.modifiers):
        if m.type == "ARMATURE":
            o.modifiers.remove(m)
    o.vertex_groups.clear()
    transfer_weights(o, body, rig)


def make_helmet(rig, side, eyes):
    _, _, hname = bone_pos(rig, "Head")
    e = head_frame(eyes)
    c = e + Vector((0, 0.086, 0.033))
    R = Vector((0.104, 0.118, 0.106))
    bpy.ops.mesh.primitive_uv_sphere_add(segments=32, ring_count=18, radius=1, location=c)
    h = bpy.context.active_object
    h.scale = R
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bm = bmesh.new(); bm.from_mesh(h.data)
    kill = []
    for v in bm.verts:
        # azimuth from the face direction: 0 = front (MPFB faces -Y), pi = back of the head
        th = math.atan2(abs(v.co.x), -v.co.y) / math.pi
        t = min(1.0, max(0.0, (th - 0.30) / 0.45))
        t = t * t * (3 - 2 * t)
        # brim ~1.5 cm above the brows across the whole face arc, dropping only over the ears and nape
        limit = -0.004 - 0.056 * t
        if v.co.z < limit:
            kill.append(v)
    bmesh.ops.delete(bm, geom=kill, context="VERTS")
    bm.to_mesh(h.data); bm.free()
    sol = h.modifiers.new("s", "SOLIDIFY"); sol.thickness = 0.011; sol.offset = 1
    bev = h.modifiers.new("b", "BEVEL"); bev.width = 0.002; bev.segments = 1; bev.limit_method = "ANGLE"
    apply_mods(h, ("SOLIDIFY", "BEVEL"))
    h.data.materials.append(material("helmet_" + side))
    h.data.materials.append(material("steel_dark"))
    for uvl in h.data.uv_layers:
        for d in uvl.data:
            d.uv = (d.uv[0] * 5, d.uv[1] * 3)
    # NVG mount on the brow, accessory rails on both sides, strap buckle under the chin line
    top_front = c + Vector((0, -R.y * 0.95 - 0.012, R.z * 0.30))
    pieces = [(top_front, (0.034, 0.01, 0.024)), (c + Vector((-R.x - 0.004, 0, 0.0)), (0.008, 0.11, 0.022)),
              (c + Vector((R.x + 0.004, 0, 0.0)), (0.008, 0.11, 0.022))]
    for loc, size in pieces:
        bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
        cu = bpy.context.active_object
        cu.scale = size; bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        b = cu.modifiers.new("b", "BEVEL"); b.width = 0.002
        apply_mods(cu, ("BEVEL",))
        cu.data.materials.append(material("steel_dark"))
        set_active(h); cu.select_set(True); bpy.context.view_layer.objects.active = h
        bpy.ops.object.join()
    rigid_bind(h, rig, hname)
    h.name = "helmet"
    return h


def make_cap(rig, eyes):
    _, _, hname = bone_pos(rig, "Head")
    c = head_frame(eyes) + Vector((0, 0.07, 0.045))
    bpy.ops.mesh.primitive_uv_sphere_add(segments=24, ring_count=14, radius=1, location=c)
    h = bpy.context.active_object
    h.scale = (0.1, 0.112, 0.105); bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bm = bmesh.new(); bm.from_mesh(h.data)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.z < -0.035 + 0.035 * max(0, -v.co.y / 0.112) - 0.015 * max(0, v.co.y / 0.112)], context="VERTS")
    bm.to_mesh(h.data); bm.free()
    sol = h.modifiers.new("s", "SOLIDIFY"); sol.thickness = 0.008; sol.offset = 1
    apply_mods(h, ("SOLIDIFY",))
    h.data.materials.append(material("knit_cap"))
    uv_box(h, 0.12)
    rigid_bind(h, rig, hname)
    h.name = "cap"
    return h


def make_vest(suit, rig, side):
    """Plate carrier: torso region of the shirt pushed outward + pouches."""
    hips, _, _ = bone_pos(rig, "Hips")
    sh_l, _, _ = bone_pos(rig, "LeftArm")
    neck, _, _ = bone_pos(rig, "Neck")
    zlo, zhi = hips.z + 0.13, neck.z - 0.03
    half = abs(sh_l.x) - 0.045
    v = suit.copy(); v.data = suit.data.copy(); bpy.context.scene.collection.objects.link(v)
    for m in list(v.modifiers):
        v.modifiers.remove(m)
    bm = bmesh.new(); bm.from_mesh(v.data)
    mw = v.matrix_world
    keep = set()
    for f in bm.faces:
        cc = mw @ f.calc_center_median()
        if zlo < cc.z < zhi and abs(cc.x) < half:
            keep.add(f)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f not in keep], context="FACES")
    bm.to_mesh(v.data); bm.free()
    v.data.materials.clear()
    v.data.materials.append(material("gear_" + side))
    for p in v.data.polygons:
        p.material_index = 0
    sol = v.modifiers.new("s", "SOLIDIFY"); sol.thickness = 0.03; sol.offset = 1
    disp = v.modifiers.new("d", "DISPLACE"); disp.strength = 0.012
    set_active(v)
    bpy.ops.object.modifier_apply(modifier="d")
    bpy.ops.object.modifier_apply(modifier="s")
    uv_box(v, 0.35)
    # pouches: 3 rifle mags + admin on the front, radio on the left side
    chest_z = zlo + (zhi - zlo) * 0.32
    front_y = min((mw @ vv.co).y for vv in v.data.vertices if abs((mw @ vv.co).x) < 0.06 and abs((mw @ vv.co).z - chest_z) < 0.05)
    parts = []
    for i, x in enumerate((-0.085, 0.0, 0.085)):
        parts.append(((x, front_y - 0.028, chest_z), (0.075, 0.05, 0.13)))
        parts.append(((x, front_y - 0.03, chest_z + 0.075), (0.078, 0.054, 0.03)))  # flap
    parts.append(((0.0, front_y - 0.02, zlo + (zhi - zlo) * 0.7), (0.17, 0.035, 0.09)))  # admin pouch
    side_x = half - 0.01
    parts.append(((-side_x - 0.02, front_y + 0.12, chest_z + 0.02), (0.05, 0.07, 0.16)))  # radio
    for loc, size in parts:
        bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
        cu = bpy.context.active_object
        cu.scale = size; bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        b = cu.modifiers.new("b", "BEVEL"); b.width = 0.008; b.segments = 2
        apply_mods(cu, ("BEVEL",))
        cu.data.materials.append(material("gear_" + side))
        uv_box(cu, 0.35)
        set_active(v); cu.select_set(True); bpy.context.view_layer.objects.active = v
        bpy.ops.object.join()
    # radio antenna
    bpy.ops.mesh.primitive_cylinder_add(vertices=6, radius=0.004, depth=0.32, location=(-side_x - 0.02, front_y + 0.12, chest_z + 0.25))
    ant = bpy.context.active_object
    ant.data.materials.append(material("steel_dark"))
    set_active(v); ant.select_set(True); bpy.context.view_layer.objects.active = v
    bpy.ops.object.join()
    transfer_weights(v, suit, rig)
    v.name = "vest"
    return v


def build(name, spec):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    macro = dict(spec["macro"]); macro.update(cupsize=0.5, firmness=0.5)
    bm = HumanService.create_human(macro_detail_dict=macro)
    HumanService.set_character_skin(os.path.join(D, "skins", spec["skin"], spec["skin"] + ".mhmat"), bm, skin_type="GAMEENGINE")
    HumanService.add_mhclo_asset(mhclo("eyes", "low-poly"), bm, asset_type="Eyes", subdiv_levels=0)
    HumanService.add_mhclo_asset(mhclo("eyebrows", "eyebrow001"), bm, asset_type="Eyebrows", subdiv_levels=0)
    HumanService.add_mhclo_asset(mhclo("eyelashes", "eyelashes01"), bm, asset_type="Eyelashes", subdiv_levels=0)
    if spec["head"] != "helmet":
        HumanService.add_mhclo_asset(mhclo("hair", spec["hair"]), bm, asset_type="Hair", subdiv_levels=0)
    for c in ("male_casualsuit01", "shoes03"):
        HumanService.add_mhclo_asset(mhclo("clothes", c), bm, asset_type="Clothes", subdiv_levels=0)
    rig = HumanService.add_builtin_rig(bm, "mixamo_unity")
    rig.name = name
    objs = {o.name: o for o in bpy.data.objects}
    body = bm  # full base mesh: the skin scans are authored for its UVs
    suit = next(o for n, o in objs.items() if n.endswith("male_casualsuit01"))
    boots = next(o for n, o in objs.items() if n.endswith("shoes03"))
    eyes = next(o for n, o in objs.items() if n.endswith("low-poly"))
    lashes = next(o for n, o in objs.items() if n.endswith("eyelashes01"))
    brows = next(o for n, o in objs.items() if n.endswith("eyebrow001"))
    hair = next((o for n, o in objs.items() if n.endswith(spec["hair"])), None)
    # drop helper geometry and everything hidden under the clothes
    apply_mods(body)
    # rename materials to stable keys
    def setmat(o, key):
        o.data.materials.clear(); o.data.materials.append(material(key))
        for p in o.data.polygons: p.material_index = 0
    setmat(body, "skin_" + spec["tone"]); setmat(suit, "uniform_" + spec["side"]); setmat(boots, "boots")
    setmat(eyes, "eyes"); setmat(brows, "brows"); setmat(lashes, "lashes")
    if hair: setmat(hair, "hair")
    for o in [suit, boots, eyes, brows, lashes] + ([hair] if hair else []):
        reweight(o, body, rig)
    parts = [body, suit, boots, eyes, brows, lashes] + ([hair] if hair else [])
    parts.append(make_vest(suit, rig, spec["side"]))
    parts.append(make_helmet(rig, spec["side"], eyes) if spec["head"] == "helmet" else make_cap(rig, eyes))
    # Quest budget: reduce clothing and gear only; the head and hands keep full resolution
    for o, ratio in ((suit, 0.5), (boots, 0.45), (parts[-2], 0.6)):
        dm = o.modifiers.new("dec", "DECIMATE"); dm.ratio = ratio
        o.modifiers.move(len(o.modifiers) - 1, 0)
        set_active(o); bpy.ops.object.modifier_apply(modifier="dec")
    # join into one skinned mesh (fewer draw calls / skinning passes on Quest)
    set_active(body)
    for p in parts: p.select_set(True)
    bpy.ops.object.join()
    lod0 = bpy.context.active_object
    lod0.name = name + "_LOD0"
    for m in list(lod0.modifiers):
        if m.type == "ARMATURE" and m.object != rig: lod0.modifiers.remove(m)
    if not any(m.type == "ARMATURE" for m in lod0.modifiers):
        m = lod0.modifiers.new("Armature", "ARMATURE"); m.object = rig
    lod0.parent = rig
    for p in lod0.data.polygons: p.use_smooth = True
    lods = [lod0]
    for i, ratio in ((1, 0.22), (2, 0.08)):
        l = lod0.copy(); l.data = lod0.data.copy(); l.name = "%s_LOD%d" % (name, i)
        bpy.context.scene.collection.objects.link(l)
        dec = l.modifiers.new("dec", "DECIMATE"); dec.ratio = ratio
        l.modifiers.move(len(l.modifiers) - 1, 0)
        set_active(l); bpy.ops.object.modifier_apply(modifier="dec")
        lods.append(l)
    print("SOLDIER", name, "tris", [tris(l) for l in lods], "bones", len(rig.data.bones))
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for o in [rig] + lods: o.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, name + ".fbx"), use_selection=True, object_types={"ARMATURE", "MESH"},
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
                             add_leaf_bones=False, bake_anim=False, mesh_smooth_type="FACE", use_armature_deform_only=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT, "assets_src", "build", name + ".blend"))
    return spec


def copy_textures():
    os.makedirs(TEX, exist_ok=True)
    from PIL import Image  # noqa: only if available in Blender's python
    return


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else list(VARIANTS)
    for n in args:
        build(n, VARIANTS[n])
    print("SOLDIERS_DONE")


main()
