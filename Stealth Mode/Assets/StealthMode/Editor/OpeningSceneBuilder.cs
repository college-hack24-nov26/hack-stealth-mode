// Builds the Scenario One opening scene (compound at sunset) from the generated assets.
// Menu: Stealth Mode > 1 Setup Assets, 2 Build Opening Scene. Both are rerunnable.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace StealthMode.EditorTools
{
    public static class OpeningSceneBuilder
    {
        const string Root = "Assets/StealthMode";
        const string TexDir = Root + "/Textures";
        const string MatDir = Root + "/Materials";
        const string EnvDir = Root + "/Models/Env";
        const string PropDir = Root + "/Models/Props";
        public const string ScenePath = Root + "/Scenes/Opening.unity";

        // ------------------------------------------------------------------ assets
        [MenuItem("Stealth Mode/1 Setup Assets")]
        public static void SetupAssets()
        {
            AssetDatabase.Refresh();
            ConfigureTextures();
            var manifest = LoadManifest();
            Directory.CreateDirectory(MatDir);
            foreach (var kv in manifest) BuildMaterial(kv.Key, kv.Value);
            BuildContainerVariants();
            BuildSkyMaterial();
            BuildCharacterMaterials();
            ConfigureModels();
            ConfigureCharacters();
            AssetDatabase.SaveAssets();
            Debug.Log("[StealthMode] Assets set up: " + manifest.Count + " materials");
        }

        static Dictionary<string, Dictionary<string, object>> LoadManifest()
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../assets_src/build/materials.json"));
            var json = File.ReadAllText(path);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(json);
        }

        static void ConfigureTextures()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TexDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var ti = (TextureImporter)AssetImporter.GetAtPath(p);
                var name = Path.GetFileNameWithoutExtension(p);
                bool changed = false;
                void Set<T>(ref T field, T v) { if (!EqualityComparer<T>.Default.Equals(field, v)) { field = v; changed = true; } }
                var type = ti.textureType; var srgb = ti.sRGBTexture; var max = ti.maxTextureSize; var mip = ti.mipmapEnabled;
                var alphaT = ti.alphaIsTransparency; var wrap = ti.wrapMode; var aniso = ti.anisoLevel;
                if (p.Contains("/Sky/"))
                {
                    Set(ref type, TextureImporterType.Default); Set(ref srgb, true); Set(ref max, 4096); Set(ref mip, false);
                    Set(ref wrap, TextureWrapMode.Clamp);
                }
                else
                {
                    Set(ref max, 1024); Set(ref mip, true); Set(ref wrap, TextureWrapMode.Repeat); Set(ref aniso, 4);
                    if (name == "normal") { Set(ref type, TextureImporterType.NormalMap); }
                    else if (name == "mask") { Set(ref type, TextureImporterType.Default); Set(ref srgb, false); }
                    else { Set(ref type, TextureImporterType.Default); Set(ref srgb, true); Set(ref alphaT, ti.DoesSourceTextureHaveAlpha()); }
                }
                ti.textureType = type; ti.sRGBTexture = srgb; ti.maxTextureSize = max; ti.mipmapEnabled = mip;
                ti.alphaIsTransparency = alphaT; ti.wrapMode = wrap; ti.anisoLevel = aniso;
                var android = ti.GetPlatformTextureSettings("Android");
                var fmt = p.Contains("/Sky/") ? TextureImporterFormat.ASTC_4x4 : name == "normal" ? TextureImporterFormat.ASTC_5x5 : TextureImporterFormat.ASTC_6x6;
                if (!android.overridden || android.format != fmt || android.maxTextureSize != max)
                {
                    android.overridden = true; android.format = fmt; android.maxTextureSize = max;
                    ti.SetPlatformTextureSettings(android); changed = true;
                }
                if (changed) ti.SaveAndReimport();
            }
        }

        static Shader Lit => Shader.Find("Universal Render Pipeline/Lit");

        static Material GetOrCreateMat(string key)
        {
            var path = $"{MatDir}/{key}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Lit); AssetDatabase.CreateAsset(m, path); }
            m.shader = Lit;
            m.enableInstancing = true;
            return m;
        }

        static void BuildMaterial(string key, Dictionary<string, object> def)
        {
            var m = GetOrCreateMat(key);
            if (def.ContainsKey("color"))
            {
                var c = ((Newtonsoft.Json.Linq.JArray)def["color"]).Select(x => (float)x).ToArray();
                m.SetColor("_BaseColor", new Color(c[0], c[1], c[2]).gamma);
                m.SetFloat("_Metallic", System.Convert.ToSingle(def["metal"]));
                m.SetFloat("_Smoothness", System.Convert.ToSingle(def["smooth"]));
                EditorUtility.SetDirty(m);
                return;
            }
            var dir = $"{TexDir}/{key}";
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(dir + "/albedo.png");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(dir + "/normal.png");
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(dir + "/mask.png");
            m.SetTexture("_BaseMap", albedo);
            m.SetColor("_BaseColor", Color.white);
            if (normal) { m.SetTexture("_BumpMap", normal); m.SetFloat("_BumpScale", 1f); m.EnableKeyword("_NORMALMAP"); }
            else m.DisableKeyword("_NORMALMAP");
            if (mask)
            {
                m.SetTexture("_MetallicGlossMap", mask); m.EnableKeyword("_METALLICSPECGLOSSMAP");
                m.SetFloat("_Smoothness", 1f); m.SetFloat("_Metallic", 1f);
            }
            else { m.DisableKeyword("_METALLICSPECGLOSSMAP"); m.SetFloat("_Smoothness", 0.25f); m.SetFloat("_Metallic", 0f); }
            if (def.ContainsKey("tint"))
            {
                var t = ((Newtonsoft.Json.Linq.JArray)def["tint"]).Select(x => (float)x).ToArray();
                m.SetColor("_BaseColor", new Color(t[0], t[1], t[2]));
            }
            if (def.ContainsKey("smooth_scale")) m.SetFloat("_Smoothness", System.Convert.ToSingle(def["smooth_scale"]));
            bool glass = key.EndsWith("_glass");
            bool cutout = !glass && albedo != null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(albedo)) is TextureImporter ti && ti.DoesSourceTextureHaveAlpha();
            SetSurface(m, glass ? Surface.Transparent : cutout ? Surface.Cutout : Surface.Opaque,
                twoSided: cutout || key.StartsWith("shrub") || key.Contains("wire"));
            if (glass) m.SetColor("_BaseColor", new Color(1, 1, 1, 0.35f));
            EditorUtility.SetDirty(m);
        }

        enum Surface { Opaque, Cutout, Transparent }

        static void SetSurface(Material m, Surface s, bool twoSided)
        {
            m.SetFloat("_Cull", twoSided ? 0 : 2);
            m.SetFloat("_AlphaClip", s == Surface.Cutout ? 1 : 0);
            m.SetFloat("_Cutoff", 0.45f);
            m.SetFloat("_Surface", s == Surface.Transparent ? 1 : 0);
            if (s == Surface.Cutout) m.EnableKeyword("_ALPHATEST_ON"); else m.DisableKeyword("_ALPHATEST_ON");
            if (s == Surface.Transparent)
            {
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0); m.renderQueue = (int)RenderQueue.Transparent;
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetShaderPassEnabled("DepthOnly", false); m.SetShaderPassEnabled("ShadowCaster", false);
            }
            else
            {
                m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetFloat("_SrcBlend", (float)BlendMode.One); m.SetFloat("_DstBlend", (float)BlendMode.Zero);
                m.SetFloat("_ZWrite", 1);
                m.renderQueue = s == Surface.Cutout ? (int)RenderQueue.AlphaTest : -1;
                m.SetOverrideTag("RenderType", s == Surface.Cutout ? "TransparentCutout" : "Opaque");
                m.SetShaderPassEnabled("DepthOnly", true); m.SetShaderPassEnabled("ShadowCaster", true);
            }
            if (twoSided) m.doubleSidedGI = true;
        }

        public static readonly (string name, Color tint)[] ContainerColors =
        {
            ("container_rust", new Color(0.55f, 0.22f, 0.13f)),
            ("container_blue", new Color(0.13f, 0.25f, 0.42f)),
            ("container_maroon", new Color(0.36f, 0.12f, 0.11f)),
            ("container_sand", new Color(0.66f, 0.56f, 0.40f)),
            ("container_grey", new Color(0.45f, 0.46f, 0.46f)),
            ("container_orange", new Color(0.72f, 0.36f, 0.12f)),
            ("container_white", new Color(0.78f, 0.78f, 0.74f)),
        };

        static void BuildContainerVariants()
        {
            var src = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/container_side.mat");
            var gray = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/container_side/albedo_gray.png");
            foreach (var (name, tint) in ContainerColors)
            {
                var m = GetOrCreateMat(name);
                m.CopyPropertiesFromMaterial(src);
                m.shaderKeywords = src.shaderKeywords;
                m.SetTexture("_BaseMap", gray);
                m.SetColor("_BaseColor", tint * 1.6f);
                EditorUtility.SetDirty(m);
            }
        }

        // key -> (smoothness when no mask, cutout)
        static readonly (string key, float smooth, bool cutout)[] CharacterMats =
        {
            ("skin_in", 0.45f, false), ("skin_en", 0.45f, false), ("skin_enb", 0.4f, false),
            ("uniform_india", 0.18f, false), ("uniform_enemy", 0.18f, false),
            ("gear_india", 0.22f, false), ("gear_enemy", 0.22f, false),
            ("helmet_india", 0.2f, false), ("helmet_enemy", 0.3f, false), ("knit_cap", 0.1f, false),
            ("boots", 0.45f, false), ("eyes", 0.85f, false), ("brows", 0.2f, true), ("lashes", 0.2f, true), ("hair", 0.3f, true),
        };

        static void BuildCharacterMaterials()
        {
            const string dir = TexDir + "/Characters";
            foreach (var (key, smooth, cutout) in CharacterMats)
            {
                var m = GetOrCreateMat(key);
                m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{key}.png"));
                m.SetColor("_BaseColor", Color.white);
                var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{(key.StartsWith("uniform") ? "uniform" : key)}_normal.png");
                if (nrm) { m.SetTexture("_BumpMap", nrm); m.EnableKeyword("_NORMALMAP"); m.SetFloat("_BumpScale", key.StartsWith("skin") ? 0.6f : 1f); }
                else { m.SetTexture("_BumpMap", null); m.DisableKeyword("_NORMALMAP"); }
                var mask = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{key}_mask.png");
                if (mask) { m.SetTexture("_MetallicGlossMap", mask); m.EnableKeyword("_METALLICSPECGLOSSMAP"); m.SetFloat("_Smoothness", 1f); }
                else { m.SetTexture("_MetallicGlossMap", null); m.DisableKeyword("_METALLICSPECGLOSSMAP"); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", 0); }
                SetSurface(m, cutout ? Surface.Cutout : Surface.Opaque, twoSided: cutout);
                if (cutout) m.SetFloat("_Cutoff", 0.35f);
                EditorUtility.SetDirty(m);
            }
        }

        static void ConfigureCharacters()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Root + "/Models/Characters" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                bool dirty = false;
                if (mi.animationType != ModelImporterAnimationType.Human) { mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; dirty = true; }
                if (mi.importAnimation) { mi.importAnimation = false; dirty = true; }
                if (mi.materialImportMode != ModelImporterMaterialImportMode.ImportViaMaterialDescription) { mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription; dirty = true; }
                if (mi.skinWeights != ModelImporterSkinWeights.Standard) { mi.skinWeights = ModelImporterSkinWeights.Standard; dirty = true; }
                if (dirty) mi.SaveAndReimport();
                MapHumanoid(mi, p);
                RemapMaterials(mi, p);
                var avatar = AssetDatabase.LoadAllAssetsAtPath(p).OfType<Avatar>().FirstOrDefault();
                Debug.Log($"[StealthMode] {Path.GetFileName(p)} humanoid valid={avatar != null && avatar.isValid && avatar.isHuman}");
            }
        }

        static readonly (string unity, string bone)[] HumanMap =
        {
            ("Hips", "Hips"), ("Spine", "Spine"), ("Chest", "Spine1"), ("UpperChest", "Spine2"), ("Neck", "Neck"), ("Head", "Head"),
            ("Jaw", "Jaw"), ("LeftEye", "LeftEye"), ("RightEye", "RightEye"),
            ("LeftShoulder", "LeftShoulder"), ("LeftUpperArm", "LeftArm"), ("LeftLowerArm", "LeftForeArm"), ("LeftHand", "LeftHand"),
            ("RightShoulder", "RightShoulder"), ("RightUpperArm", "RightArm"), ("RightLowerArm", "RightForeArm"), ("RightHand", "RightHand"),
            ("LeftUpperLeg", "LeftUpLeg"), ("LeftLowerLeg", "LeftLeg"), ("LeftFoot", "LeftFoot"), ("LeftToes", "LeftToeBase"),
            ("RightUpperLeg", "RightUpLeg"), ("RightLowerLeg", "RightLeg"), ("RightFoot", "RightFoot"), ("RightToes", "RightToeBase"),
        };

        static void MapHumanoid(ModelImporter mi, string p)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            var all = model.GetComponentsInChildren<Transform>(true);
            string Find(string bone) => all.Select(t => t.name).FirstOrDefault(n => n == "mixamorig:" + bone || n.EndsWith(":" + bone) || n == bone);
            var human = new List<HumanBone>();
            void Add(string unity, string bone)
            {
                var n = Find(bone);
                if (n != null) human.Add(new HumanBone { humanName = unity, boneName = n, limit = new HumanLimit { useDefaultValues = true } });
            }
            foreach (var (u, b) in HumanMap) Add(u, b);
            foreach (var side in new[] { "Left", "Right" })
                foreach (var (f, src) in new[] { ("Thumb", "Thumb"), ("Index", "Index"), ("Middle", "Middle"), ("Ring", "Ring"), ("Little", "Pinky") })
                {
                    Add($"{side} {f} Proximal", $"{side}Hand{src}1");
                    Add($"{side} {f} Intermediate", $"{side}Hand{src}2");
                    Add($"{side} {f} Distal", $"{side}Hand{src}3");
                }
            var skeleton = all.Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray();
            var desc = mi.humanDescription;
            if (desc.human != null && desc.human.Length == human.Count && desc.human.All(h => human.Any(x => x.humanName == h.humanName && x.boneName == h.boneName))) return;
            desc.human = human.ToArray();
            desc.skeleton = skeleton;
            desc.upperArmTwist = 0.5f; desc.lowerArmTwist = 0.5f; desc.upperLegTwist = 0.5f; desc.lowerLegTwist = 0.5f;
            desc.armStretch = 0.05f; desc.legStretch = 0.05f; desc.feetSpacing = 0; desc.hasTranslationDoF = false;
            mi.humanDescription = desc;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.SaveAndReimport();
        }

        static void RemapMaterials(ModelImporter mi, string p)
        {
            var names = AssetDatabase.LoadAllAssetsAtPath(p).OfType<Material>().Select(m => m.name).ToList();
            var remaps = mi.GetExternalObjectMap();
            foreach (var r in remaps.Keys.Where(k => k.type == typeof(Material))) names.Add(r.name);
            bool re = false;
            foreach (var n in names.Distinct())
            {
                var target = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{n}.mat");
                if (target == null) continue;
                var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), n);
                if (remaps.TryGetValue(id, out var cur) && cur == target) continue;
                mi.AddRemap(id, target); re = true;
            }
            if (re) mi.SaveAndReimport();
        }

        static void BuildSkyMaterial()
        {
            var path = $"{MatDir}/Sky_Sunset.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Skybox/Panoramic")); AssetDatabase.CreateAsset(m, path); }
            m.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Sky/sky.png"));
            m.SetFloat("_Mapping", 1); m.SetFloat("_ImageType", 0); m.SetFloat("_Exposure", 1f);
            m.SetFloat("_Rotation", SkyRotation);
            EditorUtility.SetDirty(m);
        }

        // Sky texture has the sun at u = 0.604; this rotation puts it in the south-west, matching SunYaw.
        public const float SkyRotation = 262f;
        public const float SunYaw = 45f;        // light travels toward the north-east (sun sits south-west)
        public const float SunPitch = 6f;

        static void ConfigureModels()
        {
            foreach (var dir in new[] { EnvDir, PropDir, Root + "/Models/Weapons" })
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { dir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                bool dirty = false;
                if (mi.importAnimation) { mi.importAnimation = false; dirty = true; }
                if (mi.animationType != ModelImporterAnimationType.None) { mi.animationType = ModelImporterAnimationType.None; dirty = true; }
                // only the big structures get lightmaps; props are lit by light probes (fast import, cheaper on Quest)
                bool lightmapped = p.Contains("/Env/");
                if (mi.generateSecondaryUV != lightmapped) { mi.generateSecondaryUV = lightmapped; dirty = true; }
                if (mi.importCameras || mi.importLights) { mi.importCameras = false; mi.importLights = false; dirty = true; }
                if (mi.meshCompression != ModelImporterMeshCompression.Low) { mi.meshCompression = ModelImporterMeshCompression.Low; dirty = true; }
                bool collide = p.Contains("/Env/") && !p.EndsWith("Terrain.fbx");
                if (p.Contains("/Weapons/")) { collide = false; lightmapped = false; if (mi.generateSecondaryUV) { mi.generateSecondaryUV = false; dirty = true; } }
                if (mi.addCollider != collide) { mi.addCollider = collide; dirty = true; }
                if (mi.materialImportMode != ModelImporterMaterialImportMode.ImportViaMaterialDescription)
                { mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription; dirty = true; }
                if (dirty) mi.SaveAndReimport();
                // remap every embedded material to our URP material of the same name
                RemapMaterials(mi, p);
            }
        }

        // ------------------------------------------------------------------ scene
        static Transform envRoot, propRoot, natureRoot;

        static GameObject Place(string modelPath, Vector3 pos, float yaw, Transform parent, string name = null, bool isStatic = true)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (asset == null) { Debug.LogWarning("[StealthMode] missing " + modelPath); return null; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            if (name != null) go.name = name;
            if (isStatic) foreach (var t in go.GetComponentsInChildren<Transform>()) GameObjectUtility.SetStaticEditorFlags(t.gameObject, (StaticEditorFlags)~0 & ~StaticEditorFlags.BatchingStatic);
            return go;
        }

        static GameObject Prop(string name, Vector3 pos, float yaw, Transform parent = null, float scale = 1f)
        {
            var go = Place($"{PropDir}/{name}.fbx", Ground(pos), yaw, parent ?? propRoot);
            if (go != null)
            {
                go.transform.localScale = Vector3.one * scale;
                if (go.GetComponentInChildren<Collider>() == null && !name.StartsWith("shrub"))
                {
                    var b = Bounds(go);
                    var bc = go.AddComponent<BoxCollider>();
                    bc.center = go.transform.InverseTransformPoint(b.center); bc.size = b.size / scale;
                }
            }
            return go;
        }

        static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); return b;
        }

        static Vector3 Ground(Vector3 p)
        {
            if (p.y != 0) return p;
            foreach (var hit in Physics.RaycastAll(new Vector3(p.x, 500, p.z), Vector3.down, 1000))
                if (hit.collider.gameObject.name == "Terrain") return hit.point;
            return p;
        }

        // Container positions: (x, z, yaw, colour index, stack height). Shipment numbers follow Pulin's map.
        public static readonly (string name, float x, float z, float yaw, int color, int levels)[] Containers =
        {
            ("Shipment-1 (cover)", 28.0f, -21.3f, 0f, 0, 1),
            ("Shipment-2", -23.0f, -18.0f, 25f, 1, 1),
            ("Shipment-3", -31.0f, 10.5f, 90f, 2, 2),
            ("Shipment-4", 26.0f, 14.0f, 0f, 3, 2),
            ("Shipment-5", -4.0f, -29.0f, 80f, 4, 1),
            ("Shipment-6 (breach cover)", 17.5f, -34.5f, 8f, 5, 1),
            ("Shipment-7", -40.5f, -12.0f, 90f, 6, 2),
            ("Shipment-8", -38.0f, 19.0f, 90f, 0, 1),
            ("Shipment-9", 39.5f, -4.0f, 90f, 1, 1),
            ("Shipment-10", 40.0f, 16.0f, 90f, 4, 2),
            ("Shipment-11", 9.0f, 22.0f, 0f, 2, 1),
            ("Shipment-12", -15.5f, -34.0f, 0f, 3, 1),
            ("Shipment-13", 36.0f, -30.0f, 30f, 6, 1),
        };

        public static readonly Vector3 PlayerSpot = new Vector3(24.2f, 0f, -23.35f);
        public static readonly Vector3 TowerLeft = new Vector3(-21f, 0f, -9f);   // Enemy-2 (clear of the warehouse corner from Shipment-1)
        public static readonly Vector3 TowerRight = new Vector3(21f, 0f, -4f);   // Enemy-1
        public const float TowerDeck = 6.12f;

        [MenuItem("Stealth Mode/2 Build Opening Scene")]
        public static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            envRoot = new GameObject("Environment").transform;
            propRoot = new GameObject("Props").transform;
            natureRoot = new GameObject("Nature").transform;
            Random.InitState(2611);
            FxMaterials();

            Lighting();

            var terrain = Place($"{EnvDir}/Terrain.fbx", Vector3.zero, 0, envRoot, "Terrain");
            foreach (var mf in terrain.GetComponentsInChildren<MeshFilter>()) mf.gameObject.AddComponent<MeshCollider>();
            foreach (var r in terrain.GetComponentsInChildren<MeshRenderer>())
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                var so = new SerializedObject(r); so.FindProperty("m_ScaleInLightmap").floatValue = 0.08f; so.ApplyModifiedPropertiesWithoutUndo();
            }
            Physics.SyncTransforms();

            Place($"{EnvDir}/Warehouse.fbx", Vector3.zero, 0, envRoot, "Warehouse (bomb)");
            Place($"{EnvDir}/Watchtower.fbx", TowerLeft, 0, envRoot, "Watchtower (Enemy-2)");
            Place($"{EnvDir}/Watchtower.fbx", TowerRight, 0, envRoot, "Watchtower-2 (Enemy-1)");

            var colors = ContainerColors.Select(c => AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{c.name}.mat")).ToArray();
            foreach (var c in Containers)
                for (int l = 0; l < c.levels; l++)
                {
                    var yaw = c.yaw + (l > 0 ? Random.Range(-4f, 4f) : 0f);
                    var go = Place($"{EnvDir}/Container40.fbx", new Vector3(c.x, 2.591f * l, c.z), yaw, envRoot, l == 0 ? c.name : c.name + " (stacked)");
                    foreach (var mc in go.GetComponentsInChildren<MeshCollider>())
                    {
                        // solid, closed steel box: nothing (head, rifle round, body) gets inside a container
                        var b = mc.GetComponent<MeshFilter>().sharedMesh.bounds;
                        var bc = mc.gameObject.AddComponent<BoxCollider>(); bc.center = b.center; bc.size = b.size;
                        Object.DestroyImmediate(mc);
                    }
                    var mat = colors[(c.color + l * 3) % colors.Length];
                    foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                        r.sharedMaterials = r.sharedMaterials.Select(m => m != null && m.name == "container_side" ? mat : m).ToArray();
                }

            BuildFence();
            DressCompound();
            DressNature();

            BuildActors();
            MakeTranslucent();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            var scenes = EditorBuildSettings.scenes.Where(x => x.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[StealthMode] Opening scene built");
        }

        // ------------------------------------------------------------------ actors
        const string CharDir = Root + "/Models/Characters";
        const string WeaponDir = Root + "/Models/Weapons";

        static GameObject Soldier(string model, string name, Vector3 pos, float yaw, string rifle, Transform parent)
        {
            var go = Place($"{CharDir}/{model}.fbx", Ground(pos), yaw, parent, name, isStatic: false);
            go.transform.position = pos.y != 0 ? pos : Ground(pos);
            var anim = go.GetComponent<Animator>() ?? go.AddComponent<Animator>();
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var lods = go.GetComponentsInChildren<SkinnedMeshRenderer>().OrderBy(r => r.name).ToArray();
            foreach (var r in lods) { r.sharedMesh = CleanWeights(r); r.updateWhenOffscreen = false; r.shadowCastingMode = ShadowCastingMode.On; r.lightProbeUsage = LightProbeUsage.BlendProbes; }
            if (lods.Length >= 2)
            {
                var lg = go.GetComponent<LODGroup>() ?? go.AddComponent<LODGroup>();
                var l = new List<LOD>();
                float[] cut = { 0.22f, 0.07f, 0.008f };
                for (int i = 0; i < lods.Length && i < 3; i++) l.Add(new LOD(cut[i], new Renderer[] { lods[i] }));
                lg.SetLODs(l.ToArray()); lg.RecalculateBounds();
            }
            var rig = go.AddComponent<StealthMode.SoldierRig>();
            if (rifle != null)
            {
                var r = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>($"{WeaponDir}/{rifle}.fbx"), go.transform);
                r.name = rifle;
                foreach (var mr in r.GetComponentsInChildren<MeshRenderer>()) mr.lightProbeUsage = LightProbeUsage.BlendProbes;
                rig.rifle = r.transform;
            }
            return go;
        }

        // The exported uniform/boots/vest carry index-shifted skin weights (boots bound to finger bones), which only shows
        // once a soldier leaves the bind pose. Re-skin those parts rigidly to the nearest torso/limb bone segment
        // (hands excluded; there is no body skin under the clothes to copy from).
        static readonly (string bone, string to)[] Segments =
        {
            ("Hips", "Spine"), ("Hips", "LeftUpLeg"), ("Hips", "RightUpLeg"), ("Spine", "Spine1"), ("Spine1", "Spine2"), ("Spine2", "Neck"), ("Neck", "Head"),
            ("LeftShoulder", "LeftArm"), ("LeftArm", "LeftForeArm"), ("LeftForeArm", "LeftHand"),
            ("RightShoulder", "RightArm"), ("RightArm", "RightForeArm"), ("RightForeArm", "RightHand"),
            ("LeftUpLeg", "LeftLeg"), ("LeftLeg", "LeftFoot"), ("LeftFoot", "LeftToeBase"),
            ("RightUpLeg", "RightLeg"), ("RightLeg", "RightFoot"), ("RightFoot", "RightToeBase"),
        };

        static Mesh CleanWeights(SkinnedMeshRenderer r)
        {
            var src = r.sharedMesh;
            var path = $"{CharDir}/Clean/{src.name}_clean.asset";
            var done = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (done) return done;
            var mats = r.sharedMaterials;
            var verts = src.vertices; var weights = src.boneWeights; var bind = src.bindposes;
            int Index(string b) => System.Array.FindIndex(r.bones, t => t.name.EndsWith(":" + b) || t.name == b);
            Vector3 Joint(int i) => bind[i].inverse.MultiplyPoint(Vector3.zero);
            var segs = Segments.Select(s => (i: Index(s.bone), j: Index(s.to))).Where(s => s.i >= 0 && s.j >= 0)
                               .Select(s => (bone: s.i, a: Joint(s.i), b: Joint(s.j))).ToArray();
            var fix = new HashSet<int>();
            for (int sm = 0; sm < src.subMeshCount; sm++)
            {
                var n = mats[sm] ? mats[sm].name : "";
                if (n.StartsWith("uniform") || n.StartsWith("boots") || n.StartsWith("gear")) fix.UnionWith(src.GetTriangles(sm));
            }
            foreach (var v in fix)
            {
                int best = segs[0].bone; float bd = float.MaxValue;
                foreach (var s in segs)
                {
                    var ab = s.b - s.a;
                    var p = s.a + ab * Mathf.Clamp01(Vector3.Dot(verts[v] - s.a, ab) / ab.sqrMagnitude);
                    float d = (verts[v] - p).sqrMagnitude;
                    if (d < bd) { bd = d; best = s.bone; }
                }
                weights[v] = new BoneWeight { boneIndex0 = best, weight0 = 1f };
            }
            var mesh = Object.Instantiate(src); mesh.name = src.name;
            mesh.boneWeights = weights;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // same as Shadow Batter: the URP renderer needs the AR Background feature for passthrough to composite
        static void ARBackgroundFeature()
        {
            const string path = "Assets/Settings/Mobile_Renderer.asset";
            var data = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRendererData>(path);
            foreach (var old in data.rendererFeatures.Where(f => f != null && f.name == "Passthrough Fade").ToList())   // earlier fade-pass experiment
            {
                data.rendererFeatures.Remove(old);
                Object.DestroyImmediate(old, true);
            }
            var type = System.Type.GetType("UnityEngine.XR.ARFoundation.ARBackgroundRendererFeature, Unity.XR.ARFoundation");
            if (!data.rendererFeatures.Any(f => f != null && f.GetType() == type))
            {
                var feature = (UnityEngine.Rendering.Universal.ScriptableRendererFeature)ScriptableObject.CreateInstance(type);
                feature.name = "AR Background Renderer Feature";
                AssetDatabase.AddObjectToAsset(feature, data);
                data.rendererFeatures.Add(feature);
            }
            data.SetDirty();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
        }

        // Shadow Batter's way: real transparent URP/Lit materials. Every environment asset (terrain, containers, props, soldiers)
        // gets a translucent twin (alpha EnvAlpha) as a saved asset, so the shader variant ships in the Quest build.
        const float EnvAlpha = 0.7f;

        static void MakeTranslucent()
        {
            const string dir = Root + "/Materials/MR";
            Directory.CreateDirectory(dir);
            var cache = new Dictionary<Material, Material>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if (r.GetComponentInParent<Unity.XR.CoreUtils.XROrigin>() != null) continue;   // hands + the Engineer's rifle stay solid
                if (Solid(r.transform)) continue;   // warehouse walls and shipping containers are never see-through
                var mats = r.sharedMaterials; bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (!m || !m.HasProperty("_Surface") || AssetDatabase.GetAssetPath(m).StartsWith(dir)) continue;
                    if (!cache.TryGetValue(m, out var t)) cache[m] = t = Translucent(m, dir);
                    mats[i] = t; changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
            AssetDatabase.SaveAssets();
        }

        static bool Solid(Transform t)
        {
            for (; t; t = t.parent) if (t.name.StartsWith("Warehouse") || t.name.StartsWith("Shipment")) return true;
            return false;
        }

        static Material Translucent(Material src, string dir)
        {
            var path = $"{dir}/{src.name}_mr.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m) { m = new Material(src); AssetDatabase.CreateAsset(m, path); }
            m.CopyPropertiesFromMaterial(src);
            bool cutout = src.HasProperty("_AlphaClip") && src.GetFloat("_AlphaClip") > 0.5f;
            SetSurface(m, Surface.Transparent, src.HasProperty("_Cull") && src.GetFloat("_Cull") == 0);
            if (cutout) { m.SetFloat("_AlphaClip", 1); m.EnableKeyword("_ALPHATEST_ON"); }
            m.SetFloat("_ZWrite", 1);   // depth writes keep overlapping surfaces ordered without sorting artefacts
            m.SetShaderPassEnabled("DepthOnly", true); m.SetShaderPassEnabled("ShadowCaster", true);
            var c = src.GetColor("_BaseColor"); c.a *= EnvAlpha; m.SetColor("_BaseColor", c);
            EditorUtility.SetDirty(m);
            return m;
        }

        // particle materials as assets in Resources so their shader ships in the build (CombatFx loads them)
        static void FxMaterials()
        {
            var dir = Root + "/Resources/Fx";
            Directory.CreateDirectory(dir);
            var shader = Shader.Find(StealthMode.CombatFx.ParticleShader);
            if (!AssetDatabase.LoadAssetAtPath<Material>(dir + "/FxAdditive.mat")) AssetDatabase.CreateAsset(StealthMode.CombatFx.MakeAdditive(shader), dir + "/FxAdditive.mat");
            if (!AssetDatabase.LoadAssetAtPath<Material>(dir + "/FxAlpha.mat")) AssetDatabase.CreateAsset(StealthMode.CombatFx.MakeAlpha(shader), dir + "/FxAlpha.mat");
        }

        static Transform MuzzleOf(GameObject soldier) =>
            soldier.GetComponentsInChildren<Transform>().First(t => t.name == "Muzzle");

        static void BuildActors()
        {
            var squad = new GameObject("Team Alpha").transform;
            var enemies = new GameObject("Enemies").transform;

            // enemies (numbers follow Pulin's map)
            var e1 = Soldier("Enemy_A", "Enemy-1 (Watchtower-2)", TowerRight + Vector3.up * TowerDeck + new Vector3(0.2f, 0, -0.6f), 185f, "AK47", enemies);
            var e2 = Soldier("Enemy_B", "Enemy-2 (Watchtower)", TowerLeft + Vector3.up * TowerDeck + new Vector3(0.3f, 0, -0.6f), 165f, "AK47", enemies);
            var e3 = Soldier("Enemy_A", "Enemy-3 (door left, runner)", new Vector3(-6.2f, 0, -12.6f), 160f, "AK47", enemies);
            var e4 = Soldier("Enemy_B", "Enemy-4 (door right)", new Vector3(3.8f, 0, -12.8f), 195f, "AK47", enemies);
            var e5 = Soldier("Enemy_A", "Enemy-5 (rear left)", new Vector3(-20f, 0, 16.5f), 20f, "AK47", enemies);
            StealthMode.EnemyAI AI(GameObject g, string id, StealthMode.EnemyAI.Role role)
            {
                var ai = g.AddComponent<StealthMode.EnemyAI>(); ai.id = id; ai.role = role; ai.rig = g.GetComponent<StealthMode.SoldierRig>();
                return ai;
            }
            var a1 = AI(e1, "Enemy-1", StealthMode.EnemyAI.Role.Tower);
            var a2 = AI(e2, "Enemy-2", StealthMode.EnemyAI.Role.Tower);
            var a3 = AI(e3, "Enemy-3", StealthMode.EnemyAI.Role.Runner);
            a3.runPath = new[] { new Vector3(-4.2f, 0, -11.6f), new Vector3(-2.2f, 0.35f, -9.4f), new Vector3(-2f, 0.35f, -5f) };
            a3.canFire = false;
            var a4 = AI(e4, "Enemy-4", StealthMode.EnemyAI.Role.DoorGuard);
            var a5 = AI(e5, "Enemy-5", StealthMode.EnemyAI.Role.Rear);
            // Enemy-6..10 guard the bomb inside (warehouse floor is 0.35 m up); they come out in beat 1
            var inside = new[] { new Vector3(-6f, 0.35f, 2f), new Vector3(2f, 0.35f, 3f), new Vector3(-2f, 0.35f, 6f), new Vector3(5f, 0.35f, -1f), new Vector3(-8f, 0.35f, -3f) };
            var insiders = new StealthMode.EnemyAI[inside.Length];
            for (int i = 0; i < inside.Length; i++)
            {
                var g = Soldier(i % 2 == 0 ? "Enemy_B" : "Enemy_A", $"Enemy-{6 + i} (inside)", inside[i], i * 72f, "AK47", enemies);
                insiders[i] = AI(g, $"Enemy-{6 + i}", StealthMode.EnemyAI.Role.Inside);
            }

            // Team Alpha
            var comms = Soldier("Soldier_IN", "Comms (beside Engineer)", new Vector3(26.1f, 0, -23.25f), -75f, "AK203", squad);
            var s1 = Soldier("Soldier_IN", "Sniper-1", new Vector3(-55f, 0, 33f), 120f, "AK203", squad);
            var s2 = Soldier("Soldier_IN", "Sniper-2", new Vector3(59f, 0, -45f), -52f, "AK203", squad);
            var cap = Soldier("Soldier_IN", "Captain (backstab route)", new Vector3(-50f, 0, 6f), 80f, "AK203", squad);
            var doc = Soldier("Soldier_IN", "Paramedic (backstab route)", new Vector3(-51.5f, 0, 4.4f), 75f, "AK203", squad);
            foreach (var g in new[] { s1, s2 }) g.GetComponent<StealthMode.SoldierRig>().stance = StealthMode.SoldierRig.Stance.Prone;
            foreach (var g in new[] { cap, doc }) g.GetComponent<StealthMode.SoldierRig>().stance = StealthMode.SoldierRig.Stance.Crouch;
            s1.GetComponent<StealthMode.SoldierRig>().aimPoint = new Vector3(-20, 1.4f, 16.5f);
            s2.GetComponent<StealthMode.SoldierRig>().aimPoint = new Vector3(0, 1.4f, -10f);
            cap.GetComponent<StealthMode.SoldierRig>().aimPoint = doc.GetComponent<StealthMode.SoldierRig>().aimPoint = new Vector3(-20, 1.2f, 18f);

            // the Engineer: XR origin (floor tracking) + camera + AK-203
            var origin = new GameObject("XR Origin (Engineer)");
            origin.transform.SetPositionAndRotation(Ground(PlayerSpot), Quaternion.Euler(0, -60f, 0));
            var offset = new GameObject("Camera Offset").transform; offset.SetParent(origin.transform, false);
            var camGo = new GameObject("Main Camera"); camGo.tag = "MainCamera"; camGo.transform.SetParent(offset, false);
            camGo.transform.localPosition = new Vector3(0, 1.62f, 0);
            var cam = camGo.AddComponent<Camera>(); cam.nearClipPlane = 0.03f; cam.farClipPlane = 1500f;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            var tpd = camGo.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            tpd.positionInput = new UnityEngine.InputSystem.InputActionProperty(new UnityEngine.InputSystem.InputAction("HMD Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
            tpd.rotationInput = new UnityEngine.InputSystem.InputActionProperty(new UnityEngine.InputSystem.InputAction("HMD Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));
            var xo = origin.AddComponent<Unity.XR.CoreUtils.XROrigin>();
            xo.Camera = cam; xo.CameraFloorOffsetObject = offset.gameObject; xo.Origin = origin;
            xo.RequestedTrackingOriginMode = Unity.XR.CoreUtils.XROrigin.TrackingOriginMode.Floor;
            var rifle = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>($"{WeaponDir}/AK203.fbx"), origin.transform);
            rifle.name = "AK-203 (Engineer)";
            foreach (var mr in rifle.GetComponentsInChildren<MeshRenderer>()) { mr.lightProbeUsage = LightProbeUsage.BlendProbes; mr.shadowCastingMode = ShadowCastingMode.Off; }
            // tracked hands (official XR Hands sample prefabs) so the Engineer sees his own hands on the rifle
            foreach (var side in new[] { "Left", "Right" })
            {
                var hp = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Samples/XR Hands/1.7.3/HandVisualizer/Prefabs/{side} Hand Tracking.prefab");
                if (hp) PrefabUtility.InstantiatePrefab(hp, offset);
            }
            var pr = origin.AddComponent<StealthMode.PlayerRifle>();
            pr.rifle = rifle.transform; pr.cam = cam; pr.trackingSpace = offset;
            var radio = camGo.AddComponent<AudioSource>(); radio.spatialBlend = 0; radio.volume = 1f; radio.playOnAwake = false;

            // mixed reality: passthrough (AR Foundation camera on Meta OpenXR) behind a 70%-opaque, room-locked scene
            xo.CameraYOffset = 1.6f;   // only used if the headset has no floor-level space
            camGo.AddComponent<UnityEngine.XR.ARFoundation.ARCameraManager>();
            new GameObject("AR Session").AddComponent<UnityEngine.XR.ARFoundation.ARSession>();
            var mixed = origin.AddComponent<StealthMode.MixedReality>();
            mixed.cam = cam;
            camGo.AddComponent<UnityEngine.XR.ARFoundation.ARCameraBackground>();
            ARBackgroundFeature();

            var alarmGo = new GameObject("Warehouse alarm"); alarmGo.transform.position = new Vector3(-2f, 7f, -10.3f);
            var alarm = alarmGo.AddComponent<AudioSource>(); alarm.clip = Resources.Load<AudioClip>("Audio/alarm_siren");
            alarm.loop = true; alarm.spatialBlend = 1; alarm.minDistance = 15; alarm.maxDistance = 400; alarm.playOnAwake = false; alarm.volume = 0.8f;

            var dir = new GameObject("Opening Director").AddComponent<StealthMode.OpeningDirector>();
            dir.enemy1 = a1; dir.enemy2 = a2; dir.enemy3 = a3; dir.enemy4 = a4; dir.enemy5 = a5;
            dir.comms = comms.GetComponent<StealthMode.SoldierRig>();
            dir.sniper1Muzzle = MuzzleOf(s1); dir.sniper2Muzzle = MuzzleOf(s2);
            dir.player = pr; dir.radio = radio; dir.alarm = alarm;
            dir.insiders = insiders;
            dir.gameObject.AddComponent<StealthMode.HeartLink>();

            // light probes over the compound for characters and props
            var probes = new GameObject("Light Probes").AddComponent<LightProbeGroup>();
            var pts = new List<Vector3>();
            for (float x = -44; x <= 44; x += 8) for (float z = -40; z <= 28; z += 8) foreach (var y in new[] { 0.6f, 2.5f, 7.5f }) pts.Add(Ground(new Vector3(x, 0, z)) + Vector3.up * y);
            probes.probePositions = pts.ToArray();
        }

        static void Lighting()
        {
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.66f, 0.42f);
            sun.intensity = 1.5f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            sun.lightmapBakeType = LightmapBakeType.Mixed;
            sunGo.transform.rotation = Quaternion.Euler(SunPitch, SunYaw, 0);

            RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/Sky_Sunset.mat");
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1.0f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0042f;
            RenderSettings.fogColor = new Color(0.56f, 0.53f, 0.60f);

            var ls = new LightingSettings
            {
                name = "OpeningLighting",
                bakedGI = true,
                realtimeGI = false,
                mixedBakeMode = MixedLightingMode.Subtractive,
                lightmapper = LightingSettings.Lightmapper.ProgressiveGPU,
                lightmapResolution = 6,
                lightmapMaxSize = 2048,
                directSampleCount = 16,
                indirectSampleCount = 128,
                environmentSampleCount = 128,
                maxBounces = 2,
                lightmapCompression = LightmapCompression.NormalQuality,
                ao = true,
                aoMaxDistance = 1.2f,
                directionalityMode = LightmapsMode.NonDirectional,
                filteringMode = LightingSettings.FilterMode.Auto,
            };
            var lsPath = Root + "/Scenes/OpeningLighting.lighting";
            Directory.CreateDirectory(Path.GetDirectoryName(lsPath));
            AssetDatabase.DeleteAsset(lsPath);
            AssetDatabase.CreateAsset(ls, lsPath);
            Lightmapping.lightingSettings = ls;
            RenderSettings.subtractiveShadowColor = new Color(0.32f, 0.30f, 0.38f);
        }

        static void BuildFence()
        {
            var fenceRoot = new GameObject("Fence").transform;
            var seg = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropDir}/modular_chainlink_fence.fbx");
            var tmp = (GameObject)Object.Instantiate(seg);
            var b = Bounds(tmp);
            float len = Mathf.Max(b.size.x, b.size.z);
            bool alongX = b.size.x >= b.size.z;
            Object.DestroyImmediate(tmp);
            float x0 = -46, x1 = 46, z0 = -41, z1 = 29;
            var corners = new[] { new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), new Vector3(x0, 0, z1) };
            for (int side = 0; side < 4; side++)
            {
                var a = corners[side]; var c = corners[(side + 1) % 4];
                var dir = (c - a).normalized; float total = Vector3.Distance(a, c);
                int n = Mathf.CeilToInt(total / len);
                float step = total / n;
                float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + (alongX ? -90f : 0f);
                for (int i = 0; i < n; i++)
                {
                    var p = a + dir * (step * (i + 0.5f));
                    bool gate = side == 0 && Mathf.Abs(p.x - -10f) < step * 0.6f;
                    bool breach = side == 0 && Mathf.Abs(p.x - 18f) < step * 0.5f;
                    if (gate)
                    {
                        Place($"{PropDir}/modular_chainlink_fence_door_frame.fbx", Ground(p), yaw, fenceRoot, "Gate frame");
                        Place($"{PropDir}/modular_chainlink_fence_door_gate.fbx", Ground(p), yaw + 70f, fenceRoot, "Gate (open)");
                        continue;
                    }
                    if (breach)
                    {
                        // the panel the squad cut is lying flat on the ground just outside
                        var cut = Place($"{PropDir}/modular_chainlink_fence.fbx", Ground(p + new Vector3(0.4f, 0.05f, -1.6f)), yaw + 8f, fenceRoot, "Breach (cut panel)");
                        cut.transform.rotation = Quaternion.Euler(0, yaw + 8f, 0) * Quaternion.Euler(alongX ? -84f : 0, 0, alongX ? 0 : 84f);
                        continue;
                    }
                    var go = Place($"{PropDir}/modular_chainlink_fence.fbx", Ground(p), yaw, fenceRoot);
                    go.transform.localScale = new Vector3(alongX ? step / len : 1, 1, alongX ? 1 : step / len);
                }
                Place($"{PropDir}/modular_chainlink_fence_post.fbx", Ground(a), 0, fenceRoot);
            }
        }

        static void DressCompound()
        {
            var p = propRoot;
            // warehouse front (keep the door lane and the Engineer's sight lines clear)
            Prop("Barrel_01", new Vector3(7.2f, 0, -11.2f), 10); Prop("Barrel_02", new Vector3(7.9f, 0, -11.6f), 70);
            Prop("barrel_03", new Vector3(7.5f, 0, -12.4f), 140); Prop("Barrel_01", new Vector3(8.6f, 0, -11.0f), 200);
            Prop("wooden_military_crate", new Vector3(-9.0f, 0, -11.3f), 5); Prop("wooden_military_crate", new Vector3(-10.4f, 0, -11.4f), -8);
            Prop("old_military_crate", new Vector3(-9.6f, 0.62f, -11.3f), 3);
            Prop("ammo_box", new Vector3(-8.2f, 0, -12.0f), 30);
            Prop("security_light", new Vector3(-2f, 6.7f, -10.15f), 180);
            Prop("security_light", new Vector3(-2f, 2.9f, 10.15f), 0);
            // generator + fuel by the east wall
            Prop("portable_generator", new Vector3(16.4f, 0, -5.0f), -90); Prop("metal_jerrycan_green", new Vector3(16.6f, 0, -3.6f), 20);
            Prop("metal_jerrycan_green", new Vector3(16.9f, 0, -3.2f), 75); Prop("propane_tank", new Vector3(16.2f, 0, 2.5f), 0);
            Prop("propane_tank", new Vector3(16.5f, 0, 3.2f), 40);
            // tower bases
            foreach (var t in new[] { TowerLeft, TowerRight })
            {
                Prop("ammo_box", t + new Vector3(2.3f, 0, 1.4f), 15); Prop("wooden_crate_02", t + new Vector3(-2.4f, 0, 1.0f), 30);
                Prop("portable_searchlight", t + new Vector3(1.2f, TowerDeck + 0.95f, -1.5f), 180);
                for (int i = 0; i < 5; i++) Prop("cement_bag", t + new Vector3(-1.4f + i * 0.62f, TowerDeck + 0.12f, -1.55f), 90 + Random.Range(-6, 6), null, 1f);
            }
            // gate checkpoint
            Prop("concrete_road_barrier", new Vector3(-13.5f, 0, -44.0f), 0); Prop("concrete_road_barrier", new Vector3(-6.5f, 0, -46.5f), 0);
            Prop("old_tyre", new Vector3(-17.0f, 0, -39.5f), 0); Prop("metal_trash_can", new Vector3(-17.8f, 0, -37.8f), 0);
            // around Shipment-1 and the Engineer (low items only, nothing that blocks the lean-out to the west)
            Prop("old_military_crate", new Vector3(31.2f, 0, -23.0f), 2); Prop("cement_bag", new Vector3(33.0f, 0, -22.95f), 0);
            Prop("cement_bag", new Vector3(33.05f, 0.22f, -23.0f), 6);
            Prop("plastic_crate_01", new Vector3(34.6f, 0, -23.2f), 20);
            // scattered yard clutter
            Prop("old_tyre", new Vector3(-27.0f, 0, -24.0f), 0); Prop("old_tyre", new Vector3(-26.4f, 0.22f, -24.2f), 30);
            Prop("industrial_pastic_container", new Vector3(-34.0f, 0, 2.0f), 15); Prop("industrial_pastic_container", new Vector3(-33.0f, 0, 3.5f), 70);
            Prop("old_military_compressor", new Vector3(12.0f, 0, 15.5f), 200);
            Prop("Barrel_02", new Vector3(30.5f, 0, 9.0f), 0); Prop("Barrel_01", new Vector3(31.2f, 0, 8.4f), 0); Prop("barrel_03", new Vector3(30.0f, 0, 8.0f), 0);
            Prop("wooden_military_crate", new Vector3(-25.0f, 0, 15.0f), 80); Prop("steel_frame_shelves_01", new Vector3(10f, 0.35f, 8.8f), 0);
            Prop("wooden_crate_02", new Vector3(-6.0f, 0.35f, 6.0f), 10); Prop("wooden_crate_02", new Vector3(-7.4f, 0.35f, 6.4f), 50);
            Prop("hanging_industrial_lamp", new Vector3(-2f, 7.3f, -4f), 0);
            Prop("old_tyre", new Vector3(36.5f, 0, -18.0f), 0);
            Prop("ammo_box", new Vector3(19.0f, 0, -33.0f), 40); // squad's drop at the breach
        }

        static void DressNature()
        {
            var shrubs = AssetDatabase.FindAssets("shrub_", new[] { PropDir }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            var boulders = new[] { "boulder_01", "namaqualand_boulder_02", "namaqualand_boulder_03", "namaqualand_boulder_05" };
            int placed = 0;
            for (int i = 0; i < 2000 && placed < 170; i++)
            {
                var r = Random.Range(52f, 170f); var a = Random.Range(0f, Mathf.PI * 2);
                var pos = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                if (Mathf.Abs(pos.x) < 55 && pos.z > -52 && pos.z < 40) continue;
                var go = Place(shrubs[Random.Range(0, shrubs.Length)], Ground(pos), Random.Range(0, 360f), natureRoot);
                go.transform.localScale = Vector3.one * Random.Range(0.8f, 1.5f);
                foreach (var rr in go.GetComponentsInChildren<Renderer>()) rr.shadowCastingMode = ShadowCastingMode.Off;
                placed++;
            }
            // sparse shrubs and rocks just outside the fence and on the sniper knolls
            foreach (var s in new[] { new Vector3(-56, 0, 34), new Vector3(60, 0, -46) })
            {
                for (int i = 0; i < 6; i++) Place(shrubs[Random.Range(0, shrubs.Length)], Ground(s + new Vector3(Random.Range(-6f, 6f), 0, Random.Range(-6f, 6f))), Random.Range(0, 360f), natureRoot);
                var bo = Place($"{PropDir}/{boulders[Random.Range(0, boulders.Length)]}.fbx", Ground(s + new Vector3(2.5f, 0, -1.5f)), Random.Range(0, 360f), natureRoot);
                bo.transform.localScale = Vector3.one * 1.6f;
            }
            for (int i = 0; i < 26; i++)
            {
                var r = Random.Range(60f, 160f); var a = Random.Range(0f, Mathf.PI * 2);
                var pos = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                var bo = Place($"{PropDir}/{boulders[Random.Range(0, boulders.Length)]}.fbx", Ground(pos) + Vector3.down * 0.2f, Random.Range(0, 360f), natureRoot);
                bo.transform.localScale = Vector3.one * Random.Range(1f, 3f);
            }
            for (int i = 0; i < 9; i++)
            {
                var r = Random.Range(58f, 120f); var a = Random.Range(0f, Mathf.PI * 2);
                var pos = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                Place($"{PropDir}/{(i % 2 == 0 ? "dead_tree_trunk" : "dead_tree_trunk_02")}.fbx", Ground(pos), Random.Range(0, 360f), natureRoot);
            }
            for (int i = 0; i < 40; i++)
            {
                var x = Random.Range(-60f, 60f); var z = Random.Range(-56f, 44f);
                if (Mathf.Abs(x) < 48 && z > -43 && z < 31) continue; // only between fence and berm
                var go = Place(shrubs[Random.Range(0, shrubs.Length)], Ground(new Vector3(x, 0, z)), Random.Range(0, 360f), natureRoot);
                go.transform.localScale = Vector3.one * Random.Range(0.6f, 1.1f);
            }
        }
    }
}
