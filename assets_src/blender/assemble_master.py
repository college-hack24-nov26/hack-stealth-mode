"""Collects every generated asset into one browsable, textured file: assets_src/StealthMode_Assets.blend

Run: Blender -b --factory-startup -P assets_src/blender/assemble_master.py
Collections: Compound (terrain/warehouse/towers/containers), Props, Soldiers, Weapons.
"""
import bpy, os, glob, json

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
A = os.path.join(ROOT, "Stealth Mode", "Assets", "StealthMode")
TEX = os.path.join(A, "Textures")
MAN = json.load(open(os.path.join(ROOT, "assets_src", "build", "materials.json")))


def coll(name):
    c = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(c)
    return c


def import_fbx(path, c, loc):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        for uc in list(o.users_collection):
            uc.objects.unlink(o)
        c.objects.link(o)
        if o.parent is None:
            o.location = loc
    return new


def append_blend(path, c, loc):
    with bpy.data.libraries.load(path) as (src, dst):
        dst.objects = [n for n in src.objects]
    for o in dst.objects:
        if o is None:
            continue
        c.objects.link(o)
        if o.parent is None:
            o.location = loc
        if "_LOD1" in o.name or "_LOD2" in o.name:
            o.hide_set(True); o.hide_render = True


def texture(m):
    """Wire the packed Unity textures into a Principled BSDF so the .blend looks like the game."""
    name = m.name.split(".")[0]
    d = MAN.get(name, {})
    folder = os.path.join(TEX, name)
    alb = os.path.join(folder, "albedo.png")
    nrm = os.path.join(folder, "normal.png")
    if not os.path.exists(alb):
        alb = os.path.join(TEX, "Characters", name + ".png")
        nrm = os.path.join(TEX, "Characters", ("uniform" if name.startswith("uniform") else name) + "_normal.png")
    m.use_nodes = True
    nt = m.node_tree
    b = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if b is None:
        return
    if os.path.exists(alb):
        tx = nt.nodes.new("ShaderNodeTexImage"); tx.image = bpy.data.images.load(alb, check_existing=True)
        nt.links.new(tx.outputs[0], b.inputs["Base Color"])
        if name in ("brows", "lashes", "hair") or "wire" in name or name.startswith("shrub"):
            nt.links.new(tx.outputs[1], b.inputs["Alpha"])
        if "tint" in d:
            b.inputs["Base Color"].default_value = (*d["tint"], 1)
        if os.path.exists(nrm):
            nm = nt.nodes.new("ShaderNodeNormalMap"); nx = nt.nodes.new("ShaderNodeTexImage")
            nx.image = bpy.data.images.load(nrm, check_existing=True); nx.image.colorspace_settings.name = "Non-Color"
            nt.links.new(nx.outputs[0], nm.inputs[1]); nt.links.new(nm.outputs[0], b.inputs["Normal"])
    elif "color" in d:
        b.inputs["Base Color"].default_value = (*d["color"], 1)
        b.inputs["Metallic"].default_value = d.get("metal", 0)
        b.inputs["Roughness"].default_value = 1 - d.get("smooth", 0.5)


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    comp, props, soldiers, weapons = coll("Compound"), coll("Props"), coll("Soldiers"), coll("Weapons")
    import_fbx(os.path.join(A, "Models", "Env", "Terrain.fbx"), comp, (0, 0, 0))
    import_fbx(os.path.join(A, "Models", "Env", "Warehouse.fbx"), comp, (0, 0, 0))
    import_fbx(os.path.join(A, "Models", "Env", "Watchtower.fbx"), comp, (21, 4, 0))
    import_fbx(os.path.join(A, "Models", "Env", "Watchtower.fbx"), comp, (-21, 9, 0))
    for i, (x, y) in enumerate(((-28, 21.3), (23, 18), (31, -10.5), (-26, -14))):
        import_fbx(os.path.join(A, "Models", "Env", "Container40.fbx"), comp, (x, y, 0))
    for i, f in enumerate(sorted(glob.glob(os.path.join(A, "Models", "Props", "*.fbx")))):
        import_fbx(f, props, (120 + (i % 8) * 4, (i // 8) * 4, 0))
    for i, n in enumerate(("Soldier_IN", "Enemy_A", "Enemy_B")):
        append_blend(os.path.join(ROOT, "assets_src", "build", n + ".blend"), soldiers, (100 + i * 1.5, -20, 0))
    for i, n in enumerate(("AK203", "AK47")):
        append_blend(os.path.join(ROOT, "assets_src", "build", n + ".blend"), weapons, (100 + i * 0.5, -24, 1.2))
    for m in bpy.data.materials:
        texture(m)
    out = os.path.join(ROOT, "assets_src", "StealthMode_Assets.blend")
    bpy.ops.wm.save_as_mainfile(filepath=out)
    print("MASTER_SAVED", out, len(bpy.data.objects), "objects")


main()
