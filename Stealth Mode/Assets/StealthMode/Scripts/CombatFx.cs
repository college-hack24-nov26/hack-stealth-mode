using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// Shared combat effects: muzzle flash, tracer, impact dust/sparks and positional sounds.
    /// Everything is pooled and built from code (no extra assets), cheap enough for Quest.
    /// Sounds load from Resources/Audio.
    /// </summary>
    public class CombatFx : MonoBehaviour
    {
        static CombatFx inst;
        public static CombatFx I
        {
            get
            {
                if (inst == null) inst = new GameObject("CombatFx").AddComponent<CombatFx>();
                return inst;
            }
        }

        Material flashMat, tracerMat, dustMat, sparkMat;
        readonly Queue<AudioSource> voices = new Queue<AudioSource>();
        readonly Dictionary<string, AudioClip[]> clips = new Dictionary<string, AudioClip[]>();
        ParticleSystem dust, sparks;

        void Awake()
        {
            inst = this;
            // base materials ship in Resources/Fx so the particle shader is in the Quest build (Shader.Find alone gets stripped)
            var add = Resources.Load<Material>("Fx/FxAdditive") ?? MakeAdditive(Shader.Find(ParticleShader));
            var alpha = Resources.Load<Material>("Fx/FxAlpha") ?? MakeAlpha(Shader.Find(ParticleShader));
            flashMat = Tinted(add, FlashTexture(), new Color(1f, 0.78f, 0.45f));
            tracerMat = Tinted(add, Soft(), new Color(1f, 0.62f, 0.25f));
            sparkMat = Tinted(add, Soft(), new Color(1f, 0.7f, 0.35f));
            dustMat = Tinted(alpha, Soft(), new Color(0.55f, 0.48f, 0.38f, 0.55f));
            dust = MakeBurst("Dust", dustMat, 0.5f, 1.2f, 0.15f, 0.55f, 1.8f, false);
            sparks = MakeBurst("Sparks", sparkMat, 0.12f, 0.3f, 0.02f, 0.04f, 7f, true);
            for (int i = 0; i < 24; i++)
            {
                var a = new GameObject("Voice").AddComponent<AudioSource>();
                a.transform.SetParent(transform); a.playOnAwake = false; a.spatialBlend = 1; a.rolloffMode = AudioRolloffMode.Logarithmic;
                a.minDistance = 4; a.maxDistance = 600; a.dopplerLevel = 0;
                voices.Enqueue(a);
            }
        }

        public const string ParticleShader = "Universal Render Pipeline/Particles/Unlit";

        public static Material MakeAdditive(Shader s)
        {
            var m = new Material(s);
            m.SetFloat("_Surface", 1); m.SetFloat("_Blend", 2);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_ZWrite", 0); m.renderQueue = 3100;
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); m.EnableKeyword("_BLENDMODE_ADD");
            return m;
        }

        public static Material MakeAlpha(Shader s)
        {
            var m = new Material(s);
            m.SetFloat("_Surface", 1); m.SetFloat("_Blend", 0);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0); m.renderQueue = 3000; m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return m;
        }

        static Material Tinted(Material baseMat, Texture tex, Color c)
        {
            var m = new Material(baseMat);
            m.SetTexture("_BaseMap", tex); m.SetColor("_BaseColor", c);
            return m;
        }

        static Texture2D soft;
        public static Texture2D Soft()
        {
            if (soft) return soft;
            const int n = 64; soft = new Texture2D(n, n, TextureFormat.RGBA32, true);
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                float a = Mathf.Clamp01(1 - d); a *= a;
                soft.SetPixel(x, y, new Color(1, 1, 1, a));
            }
            soft.Apply(); return soft;
        }

        static Texture2D FlashTexture()
        {
            const int n = 128; var t = new Texture2D(n, n, TextureFormat.RGBA32, true);
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                var p = new Vector2(x - n / 2f, y - n / 2f) / (n / 2f);
                float r = p.magnitude, ang = Mathf.Atan2(p.y, p.x);
                float star = Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * 2.5f)), 6) * 0.8f + 0.2f;
                float a = Mathf.Clamp01(1 - r / star); a = a * a;
                float core = Mathf.Clamp01(1 - r * 3f);
                t.SetPixel(x, y, new Color(1, 0.9f + core * 0.1f, 0.7f + core * 0.3f, Mathf.Clamp01(a + core)));
            }
            t.Apply(); return t;
        }

        ParticleSystem MakeBurst(string name, Material m, float life0, float life1, float size0, float size1, float speed, bool gravity)
        {
            var go = new GameObject(name); go.transform.SetParent(transform);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main; main.playOnAwake = false; main.loop = false; main.maxParticles = 400;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life0, life1);
            main.startSize = new ParticleSystem.MinMaxCurve(size0, size1);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.3f, speed);
            main.gravityModifier = gravity ? 1.5f : -0.05f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission; em.enabled = false;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 35; sh.radius = 0.02f;
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
            col.color = g;
            var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, gravity ? 1 : 0.5f, 1, gravity ? 0.2f : 2.2f));
            var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = m;
            r.renderMode = gravity ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            r.lengthScale = 3; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        // ---------------------------------------------------------------- public API
        public void MuzzleFlash(Transform muzzle, float scale = 1f)
        {
            if (!muzzle) return;
            StartCoroutine(Flash(muzzle, scale));
        }

        IEnumerator Flash(Transform muzzle, float scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>(); r.sharedMaterial = flashMat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var side = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(side.GetComponent<Collider>());
            side.GetComponent<MeshRenderer>().sharedMaterial = flashMat;
            go.transform.SetParent(muzzle, false);
            side.transform.SetParent(muzzle, false);
            float s = Random.Range(0.14f, 0.2f) * scale;
            go.transform.localScale = Vector3.one * s;
            go.transform.localRotation = Quaternion.Euler(0, 0, Random.Range(0, 360f));
            side.transform.localPosition = Vector3.forward * s * 0.6f;
            side.transform.localRotation = Quaternion.Euler(0, 90, 0);
            side.transform.localScale = new Vector3(s * 2.4f, s * 0.7f, 1);
            yield return null; yield return null;
            Destroy(go); Destroy(side);
        }

        public void Tracer(Vector3 from, Vector3 to)
        {
            StartCoroutine(TracerRun(from, to));
        }

        IEnumerator TracerRun(Vector3 from, Vector3 to)
        {
            var go = new GameObject("Tracer");
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = tracerMat; lr.widthMultiplier = 0.025f; lr.positionCount = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.numCapVertices = 2;
            float dist = Vector3.Distance(from, to), speed = 650f, len = 6f, t = 0;
            var dir = (to - from).normalized;
            while (t * speed < dist + len)
            {
                float head = Mathf.Min(t * speed, dist), tail = Mathf.Max(0, t * speed - len);
                lr.SetPosition(0, from + dir * tail); lr.SetPosition(1, from + dir * head);
                t += Time.deltaTime;
                yield return null;
            }
            Destroy(go);
        }

        public void Impact(Vector3 point, Vector3 normal, bool metal)
        {
            var ep = new ParticleSystem.EmitParams { position = point + normal * 0.02f, applyShapeToPosition = false };
            dust.transform.rotation = Quaternion.LookRotation(normal);
            for (int i = 0; i < (metal ? 4 : 9); i++)
            {
                ep.velocity = (normal + Random.insideUnitSphere * 0.6f).normalized * Random.Range(0.4f, 1.6f);
                dust.Emit(ep, 1);
            }
            if (metal)
                for (int i = 0; i < 10; i++)
                {
                    ep.velocity = (normal + Random.insideUnitSphere * 0.9f).normalized * Random.Range(3f, 8f);
                    sparks.Emit(ep, 1);
                }
            Play(metal ? "impact_metal" : "impact_dirt", point, metal ? 0.9f : 0.7f, 3f);
        }

        public AudioClip Clip(string name)
        {
            if (!clips.TryGetValue(name, out var arr))
            {
                var list = new List<AudioClip>();
                var one = Resources.Load<AudioClip>("Audio/" + name);
                if (one) list.Add(one);
                for (int i = 1; i < 6; i++) { var c = Resources.Load<AudioClip>($"Audio/{name}_{i}"); if (c) list.Add(c); }
                clips[name] = arr = list.ToArray();
            }
            return arr.Length == 0 ? null : arr[Random.Range(0, arr.Length)];
        }

        /// <summary>Until then a loud sound is going off right at the Engineer (his own shot, Comms' beside him): the mic hears it too.</summary>
        public static float LoudUntil { get; private set; }

        public AudioSource Play(string name, Vector3 pos, float volume = 1f, float minDist = 4f, bool spatial = true)
        {
            var c = Clip(name);
            if (!c) return null;
            var ears = Camera.main ? Camera.main.transform.position : pos;
            if (volume > 0.5f && (!spatial || Vector3.Distance(pos, ears) < 6f) && !name.StartsWith("wind"))
                LoudUntil = Mathf.Max(LoudUntil, Time.time + Mathf.Min(c.length, 0.7f));
            var a = voices.Dequeue(); voices.Enqueue(a);
            a.transform.position = pos; a.clip = c; a.volume = volume; a.minDistance = minDist;
            a.spatialBlend = spatial ? 1 : 0; a.pitch = Random.Range(0.96f, 1.04f);
            a.Play();
            return a;
        }
    }
}
