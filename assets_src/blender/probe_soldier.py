"""Probe: build one MPFB soldier and report objects, triangles and textures."""
import bpy, os, json
from bl_ext.blender_org.mpfb.services.humanservice import HumanService

D = os.path.expanduser("~/Library/Application Support/Blender/5.2/extensions/.user/blender_org/mpfb/data")
bpy.ops.wm.read_factory_settings(use_empty=True)
macro = {"gender": 1.0, "age": 0.45, "muscle": 0.72, "weight": 0.55, "proportions": 0.6, "height": 0.55,
         "cupsize": 0.5, "firmness": 0.5, "race": {"asian": 0.2, "caucasian": 0.6, "african": 0.2}}
bm = HumanService.create_human(macro_detail_dict=macro)
HumanService.add_mhclo_asset(os.path.join(D, "proxymeshes/male_generic/male_generic.proxy"), bm, asset_type="Proxymeshes", subdiv_levels=0)
HumanService.set_character_skin(os.path.join(D, "skins/toigo_light_skin_male_bronze/toigo_light_skin_male_bronze.mhmat"), bm, skin_type="GAMEENGINE")
for kind, name in (("eyes", "low-poly"), ("eyebrows", "eyebrow001"), ("hair", "short02")):
    d = os.path.join(D, kind, name)
    f = [x for x in os.listdir(d) if x.endswith(".mhclo")]
    print(kind, os.listdir(d))
    if f:
        HumanService.add_mhclo_asset(os.path.join(d, f[0]), bm, asset_type=kind.capitalize(), subdiv_levels=0)
for c in ("male_worksuit01", "shoes01"):
    HumanService.add_mhclo_asset(os.path.join(D, "clothes", c, c + ".mhclo"), bm, asset_type="Clothes", subdiv_levels=0)
rig = HumanService.add_builtin_rig(bm, "mixamo_unity")
out = []
for o in bpy.data.objects:
    tris = sum(len(p.vertices) - 2 for p in o.data.polygons) if o.type == "MESH" else 0
    imgs = []
    for s in getattr(o, "material_slots", []):
        if s.material and s.material.use_nodes:
            imgs += [os.path.basename(n.image.filepath) for n in s.material.node_tree.nodes if n.type == "TEX_IMAGE" and n.image]
    out.append((o.name, o.type, tris, [m.type for m in o.modifiers], imgs))
print("PROBE" + json.dumps(out))
bpy.ops.wm.save_as_mainfile(filepath="/private/tmp/claude-501/-Users-pulin-pc-Desktop-Hackathon-2K26---Progress/d18b374a-b480-4ba8-90c5-93fd3f732303/scratchpad/soldier_probe.blend")
