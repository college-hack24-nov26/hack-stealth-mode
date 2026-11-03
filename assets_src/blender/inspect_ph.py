import bpy, os, sys, json
PH = os.path.expanduser("~/Documents/Poly Haven Assets")
names = sys.argv[sys.argv.index("--")+1:]
out = {}
for n in names:
    p = os.path.join(PH, n, n + ".blend")
    if not os.path.exists(p):
        out[n] = "MISSING"; continue
    with bpy.data.libraries.load(p) as (src, dst):
        objs = list(src.objects)
    out[n] = objs
print("JSON" + json.dumps(out))
