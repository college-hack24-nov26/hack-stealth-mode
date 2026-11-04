using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace StealthMode.EditorTools
{
    /// <summary>
    /// The land beyond the fence, built into the open Opening scene (safe to run again; it replaces its own output):
    /// near hills, cards for trees/bushes/grass, big boulders, and a dome carrying the sky and the far land rendered in Blender
    /// (assets_src/blender/build_backdrop.py), so everything past the near hills costs one texture on the Quest.
    /// Nothing here has a collider and the compound itself is not touched, so no gameplay changes. Every material ends in _mr,
    /// which is what puts it under the opacity slider.
    /// </summary>
    public static class OutsideWorldBuilder
    {
        const string Root = "Assets/StealthMode";
        const string GenDir = Root + "/Models/Outside/Generated";
        const string MatDir = Root + "/Materials/MR";
        const string TexDir = Root + "/Textures";
        const float EnvAlpha = 0.7f;      // same baked alpha as the rest of the _mr materials

        // the keep-out box: fence, sniper knolls and a margin stay exactly as they are
        const float BoxX0 = -70, BoxX1 = 72, BoxZ0 = -60, BoxZ1 = 48;
        const float FenceX0 = -46, FenceX1 = 46, FenceZ0 = -41, FenceZ1 = 29;
        const float LandDepth = 200, DomeRadius = 1250;     // real land this far past the fence, then the painted backdrop
        static readonly Vector3 BackdropEye = new Vector3(24.2f, 1.7f, -23.35f);      // where the backdrop was rendered from: the Engineer
        static readonly Vector3[] Knolls = { new Vector3(-55, 0, 33), new Vector3(59, 0, -45) };

        // sun: same as the Sun light. Sits low in the south-west
        static readonly Vector3 ToSun = -(Quaternion.Euler(OpeningSceneBuilder.SunPitch, OpeningSceneBuilder.SunYaw, 0) * Vector3.forward);

        // atlas cells from assets_src/blender/build_outside.py: x, y from the top, size, in 2048 px
        static readonly Dictionary<string, Vector3Int> Cells = new Dictionary<string, Vector3Int>
        {
            { "tree_a", new Vector3Int(0, 0, 1024) }, { "tree_b", new Vector3Int(1024, 0, 1024) },
            { "bush_a", new Vector3Int(0, 1024, 512) }, { "bush_b", new Vector3Int(512, 1024, 512) },
            { "bush_c", new Vector3Int(1024, 1024, 512) }, { "bush_d", new Vector3Int(1536, 1024, 512) },
            { "bush_e", new Vector3Int(0, 1536, 512) }, { "bush_f", new Vector3Int(512, 1536, 512) },
            { "grass_a", new Vector3Int(1024, 1536, 512) }, { "grass_b", new Vector3Int(1536, 1536, 512) },
        };

        const int GridN = 640; const float GridHalf = 1280f;
        static float[,] orig, grid;

        [MenuItem("Stealth Mode/3 Build Outside World")]
        public static void Build()
        {
            var terrain = GameObject.Find("Environment/Terrain");
            if (!terrain) { Debug.LogError("Open the Opening scene first."); return; }
            Directory.CreateDirectory(GenDir);
            ImportSettings();

            var old = GameObject.Find("Outside");
            if (old) Object.DestroyImmediate(old);
            var root = new GameObject("Outside").transform;

            var collider = terrain.GetComponentInChildren<MeshCollider>();
            BuildGrids(collider);
            CutTerrain(terrain);
            var hills = BuildHills(root);
            var probe = hills.AddComponent<MeshCollider>();      // only while scattering: gives exact ground heights
            Physics.SyncTransforms();
            Reground(probe);
            BuildCards(root, probe);
            BuildRocks(root, probe);
            Object.DestroyImmediate(probe);
            BuildDome(root);
            LightenFence();

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(terrain.scene);
            Debug.Log("Outside world built");
        }

        // ---------------------------------------------------------------- imports
        static void ImportSettings()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Root + "/Models/Outside" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
                mi.importAnimation = false; mi.animationType = ModelImporterAnimationType.None;
                mi.importCameras = false; mi.importLights = false; mi.addCollider = false; mi.generateSecondaryUV = false;
                mi.meshCompression = ModelImporterMeshCompression.Low;
                mi.SaveAndReimport();
            }
            Texture($"{TexDir}/outside_cards/albedo.png", false, 2048, t =>
            {
                t.alphaIsTransparency = true; t.mipMapsPreserveCoverage = true; t.alphaTestReferenceValue = 0.4f;
                t.wrapMode = TextureWrapMode.Clamp;
            });
            foreach (var n in new[] { "aerial_grass_rock", "rocky_terrain_02" })
                Texture($"{TexDir}/{n}/albedo.png", false, 1024, t => { t.wrapMode = TextureWrapMode.Repeat; t.anisoLevel = 4; });
            Texture($"{TexDir}/Sky/backdrop.png", false, 8192, t => { t.wrapMode = TextureWrapMode.Clamp; t.npotScale = TextureImporterNPOTScale.None; });
        }

        static void Texture(string path, bool normal, int size, System.Action<TextureImporter> extra)
        {
            var t = (TextureImporter)AssetImporter.GetAtPath(path);
            t.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            t.sRGBTexture = !normal; t.mipmapEnabled = true; t.maxTextureSize = size;
            extra?.Invoke(t);
            var a = t.GetPlatformTextureSettings("Android");
            a.overridden = true; a.maxTextureSize = size; a.format = TextureImporterFormat.ASTC_6x6;
            t.SetPlatformTextureSettings(a);
            t.SaveAndReimport();
        }

        // ---------------------------------------------------------------- the shape of the land
        static float S(float a, float b, float x) { x = Mathf.Clamp01((x - a) / (b - a)); return x * x * (3 - 2 * x); }

        static float Fbm(float x, float z, int octaves)
        {
            float v = 0, amp = 0.5f, f = 1, sum = 0;
            for (int i = 0; i < octaves; i++)
            {
                v += amp * Mathf.PerlinNoise(x * f + 311.7f + i * 17.3f, z * f + 127.1f - i * 9.1f); sum += amp;
                amp *= 0.5f; f *= 2.07f;
            }
            return v / sum;
        }

        // how far outside the keep-out box a point is; 0 inside it
        static float Outside(float x, float z)
        {
            float dx = Mathf.Max(BoxX0 - x, 0, x - BoxX1), dz = Mathf.Max(BoxZ0 - z, 0, z - BoxZ1);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float FenceDistance(float x, float z)
        {
            float dx = Mathf.Max(FenceX0 - x, 0, x - FenceX1), dz = Mathf.Max(FenceZ0 - z, 0, z - FenceZ1);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // trees thicken toward the far edge of the real land, where they stand in front of the painted forest
        static float TreeCover(float x, float z) => Mathf.Max(S(0.46f, 0.62f, Fbm(x / 120f, z / 120f, 3)) * 0.55f, S(85, 150, Outside(x, z)));

        static float Bilinear(float[,] g, float half, float x, float z)
        {
            int n = g.GetLength(0) - 1;
            float u = Mathf.Clamp((x + half) / (2 * half) * n, 0, n - 0.001f), v = Mathf.Clamp((z + half) / (2 * half) * n, 0, n - 0.001f);
            int i = (int)u, j = (int)v; u -= i; v -= j;
            return Mathf.Lerp(Mathf.Lerp(g[i, j], g[i + 1, j], u), Mathf.Lerp(g[i, j + 1], g[i + 1, j + 1], u), v);
        }

        static float HeightExact(float x, float z)
        {
            float d = Outside(x, z);
            float b = Bilinear(orig, 400, x, z) * (1 - S(20, 90, d));
            float roll = Fbm(x / 95f, z / 95f, 5);
            float h = S(0, 150, d) * (1.5f + 16 * roll) + S(90, 175, d) * 5;
            // lower toward the sun, so the sunset stays in view
            float toSun = Vector2.Dot(new Vector2(x, z).normalized, new Vector2(ToSun.x, ToSun.z).normalized);
            h *= Mathf.Lerp(1f, 0.4f, S(0.72f, 0.95f, toSun) * S(20, 90, d));
            return b + h - 0.06f;
        }

        static float Height(float x, float z) => Bilinear(grid, GridHalf, x, z);

        static void BuildGrids(MeshCollider c)
        {
            const int n = 200; orig = new float[n + 1, n + 1];
            for (int i = 0; i <= n; i++) for (int j = 0; j <= n; j++)
            {
                float x = -400 + 800f * i / n, z = -400 + 800f * j / n;
                orig[i, j] = c.Raycast(new Ray(new Vector3(x, 300, z), Vector3.down), out var hit, 600) ? hit.point.y : 0;
            }
            grid = new float[GridN + 1, GridN + 1];
            for (int i = 0; i <= GridN; i++) for (int j = 0; j <= GridN; j++)
                grid[i, j] = HeightExact(-GridHalf + 2 * GridHalf * i / GridN, -GridHalf + 2 * GridHalf * j / GridN);
        }

        // how much evening sun reaches a point: walks toward the sun and looks for ridges in the way
        static float SunReach(Vector3 p)
        {
            float vis = 1, s = 8;
            for (int i = 0; i < 70 && s < 1100; i++, s *= 1.07f)
            {
                var q = p + ToSun * s;
                float gap = q.y - Height(q.x, q.z);
                vis = Mathf.Min(vis, Mathf.Clamp01(gap / (1.5f + s * 0.02f) + 0.5f));
                if (vis <= 0) break;
            }
            return vis;
        }

        // ---------------------------------------------------------------- terrain: keep only the compound's part on screen
        static void CutTerrain(GameObject terrain)
        {
            var mf = terrain.GetComponentInChildren<MeshFilter>();
            var src = terrain.GetComponentInChildren<MeshCollider>().sharedMesh;     // collider keeps the full original
            var m = Object.Instantiate(src); m.name = "TerrainCompound";
            var v = src.vertices;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var idx = src.GetTriangles(s); var keep = new List<int>(idx.Length);
                for (int i = 0; i < idx.Length; i += 3)
                    if (FenceDistance(v[idx[i]].x, v[idx[i]].z) < 3 || FenceDistance(v[idx[i + 1]].x, v[idx[i + 1]].z) < 3 || FenceDistance(v[idx[i + 2]].x, v[idx[i + 2]].z) < 3)
                    { keep.Add(idx[i]); keep.Add(idx[i + 1]); keep.Add(idx[i + 2]); }
                m.SetTriangles(keep, s);
            }
            mf.sharedMesh = Save(m, "TerrainCompound");
        }

        static Mesh Save(Mesh m, string name)
        {
            var path = $"{GenDir}/{name}.asset";
            AssetDatabase.DeleteAsset(path);     // a fresh asset every build: copying over a mesh leaves the old one on the GPU
            m.name = name; AssetDatabase.CreateAsset(m, path); return m;
        }

        // ray from the origin to just outside the fence at this angle
        static float RimAt(float a)
        {
            float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
            float tx = Mathf.Abs(cx) < 1e-5f ? 1e9f : (cx > 0 ? FenceX1 : FenceX0) / cx;
            float tz = Mathf.Abs(cz) < 1e-5f ? 1e9f : (cz > 0 ? FenceZ1 : FenceZ0) / cz;
            return Mathf.Min(tx, tz);
        }

        static GameObject BuildHills(Transform root)
        {
            const int A = 440, R = 90;
            var pos = new Vector3[(A + 1) * (R + 1)]; var col = new Color[pos.Length]; var tris = new int[A * R * 6];
            for (int a = 0; a <= A; a++)
            {
                float ang = a % A * Mathf.PI * 2 / A, rim = RimAt(ang) + 1f;     // the terrain's cut edge lies on top of this inner rim
                for (int r = 0; r <= R; r++)
                {
                    float dist = rim + LandDepth * Mathf.Pow(r / (float)R, 1.35f);
                    float x = Mathf.Cos(ang) * dist, z = Mathf.Sin(ang) * dist;
                    pos[a * (R + 1) + r] = new Vector3(x, HeightExact(x, z), z);
                }
            }
            int t = 0;
            for (int a = 0; a < A; a++) for (int r = 0; r < R; r++)
            {
                int i = a * (R + 1) + r, j = i + R + 1;
                tris[t++] = i; tris[t++] = j; tris[t++] = i + 1; tris[t++] = j; tris[t++] = j + 1; tris[t++] = i + 1;
            }
            var m = new Mesh { name = "Hills", indexFormat = IndexFormat.UInt32 };
            m.vertices = pos; m.triangles = tris; m.RecalculateNormals(); m.RecalculateBounds();
            var nrm = m.normals;
            // the seam of the polar grid: same point at both ends, same normal
            for (int r = 0; r <= R; r++) { int i = r, j = A * (R + 1) + r; var n = (nrm[i] + nrm[j]).normalized; nrm[i] = nrm[j] = n; }
            for (int i = 0; i < pos.Length; i++)
            {
                var p = pos[i]; float d = Outside(p.x, p.z);
                float f = FenceDistance(p.x, p.z);
                float dry = Mathf.Clamp01(1 - S(1, 9, f) + S(0.60f, 0.72f, Fbm(p.x / 34f, p.z / 34f, 4)) * 0.4f * (1 - S(10, 50, f)));
                col[i] = new Color(0, dry, TreeCover(p.x, p.z), SunReach(p + Vector3.up * 0.5f));
            }
            m.normals = nrm; m.colors = col;
            m = Save(m, "Hills");

            var mat = Mat("Hills_mr", "StealthMode/Hills", 2000);
            mat.SetTexture("_GrassMap", Load<Texture2D>($"{TexDir}/rocky_terrain_02/albedo.png"));
            mat.SetTexture("_PatchMap", Load<Texture2D>($"{TexDir}/aerial_grass_rock/albedo.png"));
            mat.SetTexture("_DryMap", Load<Texture2D>($"{TexDir}/dry_ground_rocks/albedo.png"));
            mat.SetTexture("_DetailNormal", Load<Texture2D>($"{TexDir}/sparse_grass/normal.png"));
            return Renderer("Hills", root, m, mat);
        }

        static T Load<T>(string p) where T : Object => AssetDatabase.LoadAssetAtPath<T>(p);

        static Material Mat(string name, string shader, int queue)
        {
            var path = $"{MatDir}/{name}.mat";
            var m = Load<Material>(path);
            if (!m) { m = new Material(Shader.Find(shader)); AssetDatabase.CreateAsset(m, path); }
            m.shader = Shader.Find(shader);
            m.SetColor("_BaseColor", new Color(1, 1, 1, EnvAlpha));
            m.renderQueue = queue;
            EditorUtility.SetDirty(m);
            return m;
        }

        static GameObject Renderer(string name, Transform root, Mesh mesh, Material mat)
        {
            var go = new GameObject(name); go.transform.SetParent(root, false); go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            Cheap(r);
            return go;
        }

        static void Cheap(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off; r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        // ---------------------------------------------------------------- things already out there follow the new ground
        static void Reground(MeshCollider hills)
        {
            var nature = GameObject.Find("Nature");
            if (!nature) return;
            foreach (Transform c in nature.transform)
            {
                var p = c.position;
                if (Outside(p.x, p.z) <= 0) continue;
                float bedded = c.name.Contains("boulder") ? 0.2f : 0.02f;    // set from the height alone, so a rebuild lands in the same place
                if (hills.Raycast(new Ray(new Vector3(p.x, 400, p.z), Vector3.down), out var b, 800)) c.position = new Vector3(p.x, b.point.y - bedded, p.z);
            }
        }

        // ---------------------------------------------------------------- trees, bushes, grass
        class Card { public string cell; public Vector3 at; public float size, yaw; public int planes; public Color tint; public float sway; }

        static bool Ground(MeshCollider hills, float x, float z, out Vector3 p, out float slope)
        {
            p = default; slope = 0;
            if (!hills.Raycast(new Ray(new Vector3(x, 500, z), Vector3.down), out var hit, 1000)) return false;
            p = hit.point; slope = 1 - hit.normal.y; return true;
        }

        static bool NearKnoll(float x, float z, float radius)
        {
            foreach (var k in Knolls) if ((new Vector3(x, 0, z) - k).sqrMagnitude < radius * radius) return true;
            return false;
        }

        static void BuildCards(Transform root, MeshCollider hills)
        {
            var rnd = new System.Random(2611);
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
            var cards = new List<Card>();
            // colour is in the rendered plants (green leaves, brown bark); each plant only varies in brightness
            Color Shade() { float v = R(0.84f, 1.08f); return new Color(v, v, v * R(0.92f, 1.0f)); }
            bool TowardSun(float x, float z) => Vector2.Dot(new Vector2(x, z).normalized, new Vector2(ToSun.x, ToSun.z).normalized) > 0.965f;

            for (int i = 0, n = 0; i < 40000 && n < 620; i++)
            {
                float a = R(0, Mathf.PI * 2), rim = RimAt(a), dist = rim + 6 + (LandDepth - 8) * Mathf.Sqrt(R(0, 1));
                float x = Mathf.Cos(a) * dist, z = Mathf.Sin(a) * dist, d = Outside(x, z);
                if (d < 4 || TreeCover(x, z) < R(0.03f, 1f) || NearKnoll(x, z, 18) || TowardSun(x, z)) continue;
                if (!Ground(hills, x, z, out var p, out _)) continue;
                bool big = R(0, 1) < 0.72f;
                cards.Add(new Card
                {
                    cell = big ? (R(0, 1) < 0.5f ? "tree_a" : "tree_b") : (R(0, 1) < 0.5f ? "bush_a" : "bush_b"),
                    at = p, size = big ? R(16, 25) : R(8, 12), yaw = R(0, 360), planes = 3, tint = Shade(), sway = big ? 0.5f : 0.35f,
                });
                n++;
            }
            for (int i = 0, n = 0; i < 60000 && n < 800; i++)
            {
                float x = R(-240, 240), z = R(-230, 230), f = FenceDistance(x, z);
                if (f < 2.5f || f > 160 || NearKnoll(x, z, 8) || Mathf.Pow(R(0, 1), 0.6f) * 160 < f) continue;
                if (!Ground(hills, x, z, out var p, out _)) continue;
                cards.Add(new Card { cell = "bush_" + "abcdef"[rnd.Next(6)], at = p, size = Outside(x, z) <= 0 ? R(1.4f, 2.4f) : R(1.8f, 4.2f), yaw = R(0, 360), planes = 3, tint = Shade(), sway = 0.25f });
                n++;
            }
            for (int i = 0, n = 0; i < 400000 && n < 6000; i++)
            {
                float x = R(-170, 170), z = R(-160, 150), f = FenceDistance(x, z);
                if (f < 1.5f || f > 110 || Mathf.Pow(R(0, 1), 0.7f) * 110 < f) continue;
                if (!Ground(hills, x, z, out var p, out _)) continue;
                float g = R(0.5f, 0.72f);    // the rendered grass is bright straw; in this light it sits darker and greener
                cards.Add(new Card { cell = R(0, 1) < 0.5f ? "grass_a" : "grass_b", at = p, size = R(1.2f, 2.2f), yaw = R(0, 360), planes = 2, tint = new Color(g * 0.72f, g * 0.95f, g * 0.55f), sway = 0.12f });
                n++;
            }

            var mat = Mat("Foliage_mr", "StealthMode/Foliage Card", 2450);
            mat.SetTexture("_BaseMap", Load<Texture2D>($"{TexDir}/outside_cards/albedo.png"));

            // sectors keep culling useful; inside each one, nearest plants first so depth rejects what they hide
            const int Sectors = 12;
            var player = new Vector3(24, 0, -23);
            var parent = new GameObject("Plants").transform; parent.SetParent(root, false);
            foreach (var g in cards.GroupBy(c => (int)((Mathf.Atan2(c.at.z, c.at.x) + Mathf.PI) / (2 * Mathf.PI) * Sectors) % Sectors * 2 + (c.at.magnitude > 170 ? 1 : 0)))
            {
                var mesh = CardMesh(g.OrderBy(c => (c.at - player).sqrMagnitude).ToList());
                mesh = Save(mesh, $"Plants_{g.Key}");
                Renderer($"Plants {g.Key}", parent, mesh, mat);
            }
            Debug.Log($"Outside: {cards.Count} plant cards");
        }

        static Mesh CardMesh(List<Card> cards)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var c = new List<Color>(); var uv = new List<Vector2>(); var sw = new List<Vector2>(); var t = new List<int>();
            foreach (var k in cards)
            {
                var cell = Cells[k.cell];
                const float inset = 3f / 2048f;
                float u0 = cell.x / 2048f + inset, u1 = (cell.x + cell.z) / 2048f - inset;
                float v0 = 1 - (cell.y + cell.z) / 2048f + inset, v1 = 1 - cell.y / 2048f - inset;
                float sun = SunReach(k.at + Vector3.up * k.size * 0.4f);
                var c0 = new Color(k.tint.r, k.tint.g, k.tint.b, sun);
                float phase = k.at.x * 0.13f + k.at.z * 0.17f;
                for (int p = 0; p < k.planes; p++)
                {
                    float yaw = (k.yaw + p * 180f / k.planes) * Mathf.Deg2Rad;
                    var right = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw)) * k.size * 0.5f;
                    var up = Vector3.up * k.size;
                    var foot = k.at + Vector3.down * (k.size * 0.015f);
                    int b = v.Count;
                    v.Add(foot - right); v.Add(foot + right); v.Add(foot + right + up); v.Add(foot - right + up);
                    uv.Add(new Vector2(u0, v0)); uv.Add(new Vector2(u1, v0)); uv.Add(new Vector2(u1, v1)); uv.Add(new Vector2(u0, v1));
                    var side = right.normalized;
                    n.Add((-side + Vector3.up * 0.4f).normalized); n.Add((side + Vector3.up * 0.4f).normalized);
                    n.Add((side + Vector3.up).normalized); n.Add((-side + Vector3.up).normalized);
                    for (int i = 0; i < 4; i++) c.Add(c0);
                    float top = k.size * k.sway;
                    sw.Add(new Vector2(0, phase)); sw.Add(new Vector2(0, phase)); sw.Add(new Vector2(top, phase)); sw.Add(new Vector2(top, phase));
                    t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b); t.Add(b + 2); t.Add(b + 3);
                }
            }
            var m = new Mesh();
            m.SetVertices(v); m.SetNormals(n); m.SetColors(c); m.SetUVs(0, uv); m.SetUVs(1, sw); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        // ---------------------------------------------------------------- big rocks
        // the project's closed, fully scanned boulders, scaled up. Nothing here is a one-sided shell.
        static void BuildRocks(Transform root, MeshCollider hills)
        {
            var sources = new Dictionary<string, GameObject>();
            foreach (Transform c in GameObject.Find("Nature").transform)
                if (c.name.StartsWith("boulder_01")) sources[c.name.Split(' ')[0]] = c.gameObject;
            var kinds = sources.Values.ToArray();
            var parent = new GameObject("Big rocks").transform; parent.SetParent(root, false);
            var rnd = new System.Random(77);
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
            var placed = new List<Vector3>();
            void Scatter(int count, float near, float far, float minGap, float s0, float s1)
            {
                for (int i = 0, n = 0; i < 6000 && n < count; i++)
                {
                    float a = R(0, Mathf.PI * 2), dist = RimAt(a) + Mathf.Lerp(near, far, R(0, 1));
                    float x = Mathf.Cos(a) * dist, z = Mathf.Sin(a) * dist;
                    if (Outside(x, z) < 6 || NearKnoll(x, z, 22) || !Ground(hills, x, z, out var p, out _)) continue;
                    if (placed.Any(q => (q - p).sqrMagnitude < minGap * minGap)) continue;
                    var go = Object.Instantiate(kinds[rnd.Next(kinds.Length)], parent);
                    go.name = "Rock";
                    foreach (var col in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(col);
                    go.transform.SetPositionAndRotation(p, Quaternion.Euler(R(-4, 4), R(0, 360), R(-4, 4)));
                    go.transform.localScale = Vector3.one * R(s0, s1);
                    var rs = go.GetComponentsInChildren<MeshRenderer>();
                    var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                    // bedded into the lowest ground under it, so no slope ever shows the scan's open base
                    float low = p.y;
                    foreach (var o in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1), new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
                        if (Ground(hills, p.x + o.x * b.extents.x, p.z + o.y * b.extents.z, out var q, out _)) low = Mathf.Min(low, q.y);
                    go.transform.position += new Vector3(p.x - b.center.x, low - b.size.y * 0.14f - b.min.y, p.z - b.center.z);
                    foreach (var r in rs) { r.shadowCastingMode = ShadowCastingMode.Off; r.lightProbeUsage = LightProbeUsage.Off; r.reflectionProbeUsage = ReflectionProbeUsage.Off; }
                    foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.isStatic = true;
                    placed.Add(p); n++;
                }
            }
            Scatter(14, 28, 90, 14, 1.6f, 3f);
            Scatter(22, 70, 190, 24, 3f, 6.5f);
        }

        // ---------------------------------------------------------------- sky
        static void BuildDome(Transform root)
        {
            const int Lon = 128, Lat = 64; const float MaxLat = 100f;
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            float rot = OpeningSceneBuilder.SkyRotation * Mathf.Deg2Rad, ca = Mathf.Cos(rot), sa = Mathf.Sin(rot);
            for (int i = 0; i <= Lat; i++)
            {
                float lat = i / (float)Lat * MaxLat * Mathf.Deg2Rad;
                for (int j = 0; j <= Lon; j++)
                {
                    float lon = (j / (float)Lon * 2 - 1) * Mathf.PI;
                    var s = new Vector3(Mathf.Sin(lat) * Mathf.Cos(lon), Mathf.Cos(lat), Mathf.Sin(lat) * Mathf.Sin(lon));
                    // exactly what Skybox/Panoramic does: geometry turned by its _Rotation, texture read from the unturned direction
                    v.Add(new Vector3(ca * s.x - sa * s.z, s.y, sa * s.x + ca * s.z) * DomeRadius);
                    uv.Add(new Vector2(0.5f - lon / (2 * Mathf.PI), 1 - lat / Mathf.PI));
                }
            }
            for (int i = 0; i < Lat; i++) for (int j = 0; j < Lon; j++)
            {
                int a = i * (Lon + 1) + j, b = a + Lon + 1;
                t.Add(a); t.Add(a + 1); t.Add(b); t.Add(b); t.Add(a + 1); t.Add(b + 1);
            }
            var m = new Mesh(); m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0); m.RecalculateBounds();
            m = Save(m, "SkyDome");
            var mat = Mat("Sky_mr", "StealthMode/Sky Dome", 2499);
            mat.SetTexture("_BaseMap", Load<Texture2D>($"{TexDir}/Sky/backdrop.png"));
            Renderer("Sky dome", root, m, mat).transform.position = BackdropEye;
        }

        // ---------------------------------------------------------------- the same fence for an eighth of the triangles
        static void LightenFence()
        {
            var fbx = AssetDatabase.LoadAllAssetsAtPath($"{Root}/Models/Outside/modular_chainlink_fence_lite.fbx").OfType<Mesh>().First();
            // the panels' material slots are wire, then posts: put the small wire submesh first to match
            var lite = Object.Instantiate(fbx);
            if (fbx.GetIndexCount(0) > fbx.GetIndexCount(1)) { lite.SetTriangles(fbx.GetTriangles(1), 0); lite.SetTriangles(fbx.GetTriangles(0), 1); }
            // Blender hands the mesh back Z-up: stand it the way the original panel stands
            var orig = AssetDatabase.LoadAllAssetsAtPath($"{Root}/Models/Props/modular_chainlink_fence.fbx").OfType<Mesh>().First();
            var best = (err: float.MaxValue, v: (Vector3[])null, n: (Vector3[])null);
            foreach (var turn in new[] { Quaternion.identity, Quaternion.Euler(90, 0, 0), Quaternion.Euler(-90, 0, 0), Quaternion.Euler(0, 0, 90), Quaternion.Euler(0, 0, -90) })
            {
                var v = fbx.vertices.Select(p => turn * p).ToArray();
                var b = new Bounds(v[0], Vector3.zero); foreach (var p in v) b.Encapsulate(p);
                float err = (b.size - orig.bounds.size).magnitude;
                var shift = orig.bounds.center - b.center;
                if (err < best.err) best = (err, v.Select(p => p + shift).ToArray(), fbx.normals.Select(n => turn * n).ToArray());
            }
            lite.vertices = best.v; lite.normals = best.n; lite.RecalculateBounds(); lite.RecalculateTangents();
            lite = Save(lite, "FenceLite");
            var fence = GameObject.Find("Fence");
            int n = 0;
            foreach (var mf in fence.GetComponentsInChildren<MeshFilter>())
                if (!mf.sharedMesh || mf.sharedMesh.name == "modular_chainlink_fence" || mf.sharedMesh.name == fbx.name) { mf.sharedMesh = lite; n++; }    // empty = a previous build's mesh, replaced just above
            var was = Load<Material>($"{MatDir}/modular_chainlink_fence_wire_mr.mat");
            var wire = Mat("FenceWire_mr", "StealthMode/Fence Wire", 2450);
            wire.SetTexture("_BaseMap", was.GetTexture("_BaseMap"));
            wire.SetTextureScale("_BaseMap", was.GetTextureScale("_BaseMap")); wire.SetTextureOffset("_BaseMap", was.GetTextureOffset("_BaseMap"));
            foreach (var r in fence.GetComponentsInChildren<MeshRenderer>())
                r.sharedMaterials = r.sharedMaterials.Select(m => m == was ? wire : m).ToArray();
            Debug.Log($"Outside: {n} fence panels on the light mesh, wire {lite.GetIndexCount(0) / 3} + posts {lite.GetIndexCount(1) / 3} tris");
        }
    }
}
