"""Render quick look-dev previews of whatever .blend is open: full body + face.
Usage: Blender -b file.blend -P preview.py -- out_prefix [object_filter]"""
import bpy, sys, os, math
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
out = args[0]
sc = bpy.context.scene
sc.render.engine = "BLENDER_EEVEE"
sc.render.resolution_x, sc.render.resolution_y = 900, 1200
sc.render.film_transparent = False
w = bpy.data.worlds.new("w") if not sc.world else sc.world
sc.world = w
w.use_nodes = True
nt = w.node_tree
env = nt.nodes.new("ShaderNodeTexEnvironment")
env.image = bpy.data.images.load(os.path.expanduser("~/Desktop/Hackathon 2K26 - Progress/assets_src/downloads/belfast_sunset_puresky_4k.hdr"))
bg = nt.nodes["Background"]
nt.links.new(env.outputs[0], bg.inputs[0])
bg.inputs[1].default_value = 0.6
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
sun.data.energy = 3.0
sun.data.color = (1.0, 0.7, 0.5)
sun.rotation_euler = (math.radians(80), 0, math.radians(-40))
sc.collection.objects.link(sun)
for o in sc.objects:
    if o.type == "MESH" and ("_LOD1" in o.name or "_LOD2" in o.name):
        o.hide_render = True; o.hide_set(True)
TEX = os.path.expanduser("~/Desktop/Hackathon 2K26 - Progress/Stealth Mode/Assets/StealthMode/Textures/Characters")
for m in bpy.data.materials:
    f = os.path.join(TEX, m.name + ".png")
    if not os.path.exists(f):
        continue
    m.use_nodes = True
    nt2 = m.node_tree
    bsdf = next(n for n in nt2.nodes if n.type == "BSDF_PRINCIPLED") if any(n.type == "BSDF_PRINCIPLED" for n in nt2.nodes) else nt2.nodes.new("ShaderNodeBsdfPrincipled")
    out_n = next((n for n in nt2.nodes if n.type == "OUTPUT_MATERIAL"), None) or nt2.nodes.new("ShaderNodeOutputMaterial")
    nt2.links.new(bsdf.outputs[0], out_n.inputs[0])
    tex = nt2.nodes.new("ShaderNodeTexImage"); tex.image = bpy.data.images.load(f)
    nt2.links.new(tex.outputs[0], bsdf.inputs["Base Color"])
    if tex.image.channels == 4 and m.name in ("brows", "hair", "lashes"):
        nt2.links.new(tex.outputs[1], bsdf.inputs["Alpha"])
    bsdf.inputs["Roughness"].default_value = 0.45 if m.name.startswith("skin") else 0.85
    if m.name in ("brows", "hair", "lashes"):
        m.blend_method = "HASHED" if hasattr(m, "blend_method") else None
    nf = os.path.join(TEX, ("uniform" if m.name.startswith("uniform") else m.name) + "_normal.png")
    if os.path.exists(nf):
        nm = nt2.nodes.new("ShaderNodeNormalMap"); nt_ = nt2.nodes.new("ShaderNodeTexImage")
        nt_.image = bpy.data.images.load(nf); nt_.image.colorspace_settings.name = "Non-Color"
        nt2.links.new(nt_.outputs[0], nm.inputs[1]); nt2.links.new(nm.outputs[0], bsdf.inputs["Normal"])
for m in bpy.data.materials:
    if m.name == "steel_dark":
        m.use_nodes = True
        b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
        b.inputs["Base Color"].default_value = (0.02, 0.02, 0.02, 1); b.inputs["Metallic"].default_value = 0.8
meshes = [o for o in sc.objects if o.type == "MESH" and o.visible_get()]
lo = Vector((min(v[0] for o in meshes for v in o.bound_box), 0, min((o.matrix_world @ Vector(v)).z for o in meshes for v in o.bound_box)))
top = max((o.matrix_world @ Vector(v)).z for o in meshes for v in o.bound_box)
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
sc.collection.objects.link(cam)
sc.camera = cam
view = Vector(tuple(float(x) for x in args[1].split(","))) if len(args) > 1 else Vector((0.6, -1, 0))

def shot(name, target, dist, lens):
    cam.data.lens = lens
    d = view.normalized()
    cam.location = target + d * dist
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
    sc.render.filepath = f"{out}_{name}.png"
    bpy.ops.render.render(write_still=True)

shot("body", Vector((0, 0, top * 0.52)), 4.2, 50)
shot("face", Vector((0, 0, top * 0.93)), 0.9, 85)
