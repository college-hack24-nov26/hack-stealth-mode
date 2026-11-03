using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace StealthMode
{
    /// <summary>
    /// Beat 5's trap, in the open past Shipment-1's west corner on the line Comms takes when he moves up: a steel trip
    /// wire at shin height between two pegs across his path, running to a directional mine (MON-50 type) on short legs a
    /// pace and a half ahead-left of it, aimed back down that path. It is there from the start of the run: thin wire that
    /// only catches the light now and then (and in the lightning).
    /// When Comms walks through the wire it fires: flash, dirt and smoke, clods and stones flying, the shock through both
    /// controllers, Comms thrown back toward the Engineer, and the Engineer's ears shut: the world muffled under a
    /// ringing tone that takes several seconds to clear.
    /// </summary>
    public class TripMine : MonoBehaviour
    {
        public Vector3 pegA, pegB, mine, aim;
        Transform head, visuals, glint;
        LineRenderer wire;
        Material wireMat, glintMat;
        float nextGlint, glintUntil;

        const float WireH = 0.14f;

        /// <summary>Lay the wire across a path at `crossing` (walking direction `dir`), the mine ahead-left of it.</summary>
        public static TripMine Place(Vector3 crossing, Vector3 dir, Transform head)
        {
            var m = new GameObject("Trip mine (MON-50) + wire").AddComponent<TripMine>();
            dir.y = 0; dir.Normalize();
            var right = new Vector3(dir.z, 0, -dir.x);
            m.head = head;
            m.pegA = OnGround(crossing + right * 0.7f);
            m.pegB = OnGround(crossing - right * 0.7f);
            m.mine = OnGround(crossing + dir * 1.5f - right * 0.6f);
            m.aim = Vector3.ProjectOnPlane(crossing - m.mine, Vector3.up).normalized;
            m.Build();
            return m;
        }

        static Vector3 OnGround(Vector3 p)
        {
            foreach (var h in Physics.RaycastAll(new Vector3(p.x, 30f, p.z), Vector3.down, 60f, ~0, QueryTriggerInteraction.Ignore))
                if (h.collider.name == "Terrain" || (h.collider.transform.parent && h.collider.transform.parent.name == "Terrain")) return h.point;
            return new Vector3(p.x, 0, p.z);
        }

        static Material Borrow(string objectName)
        {
            var go = GameObject.Find(objectName);
            var r = go ? go.GetComponentInChildren<Renderer>() : null;
            return r ? r.sharedMaterial : null;
        }

        GameObject Part(PrimitiveType type, Vector3 pos, Quaternion rot, Vector3 scale, Material mat)
        {
            var g = GameObject.CreatePrimitive(type);
            Destroy(g.GetComponent<Collider>());                   // nothing here stops a round or a foot
            g.transform.SetParent(visuals, false);
            g.transform.SetPositionAndRotation(pos, rot); g.transform.localScale = scale;
            var r = g.GetComponent<MeshRenderer>(); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (mat) r.sharedMaterial = mat;
            return g;
        }

        void Build()
        {
            visuals = new GameObject("visuals").transform; visuals.SetParent(transform, false);
            var steel = Borrow("AK-203 (Engineer)");
            var up = Quaternion.identity;
            // pegs: thin steel pins driven into the dirt
            foreach (var p in new[] { pegA, pegB }) Part(PrimitiveType.Cylinder, p + Vector3.up * 0.09f, up, new Vector3(0.012f, 0.09f, 0.012f), steel);
            // the mine: a curved slab the size of a hardback book, standing on two folded wire legs, face toward the path
            var face = Quaternion.LookRotation(aim);
            var body = mine + Vector3.up * 0.2f;
            Part(PrimitiveType.Cube, body, face, new Vector3(0.22f, 0.155f, 0.066f), steel);
            foreach (var s in new[] { -0.07f, 0.07f })
                Part(PrimitiveType.Cylinder, mine + face * new Vector3(s, 0, 0) + Vector3.up * 0.06f, up, new Vector3(0.008f, 0.06f, 0.008f), steel);

            wireMat = new Material(Resources.Load<Material>("Fx/FxAdditive"));
            wire = new GameObject("wire").AddComponent<LineRenderer>(); wire.transform.SetParent(visuals, false);
            wire.useWorldSpace = true; wire.positionCount = 3; wire.widthMultiplier = 0.0025f; wire.sharedMaterial = wireMat;
            wire.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // peg to peg across the path, then up to the fuze on top of the mine
            wire.SetPositions(new[] { pegA + Vector3.up * WireH, pegB + Vector3.up * WireH, body + Vector3.up * 0.08f });

            glintMat = new Material(Resources.Load<Material>("Fx/FxAdditive"));
            glintMat.SetTexture("_BaseMap", CombatFx.Soft()); glintMat.SetColor("_BaseColor", new Color(1f, 1f, 0.95f, 0.9f));
            glint = Part(PrimitiveType.Quad, pegA, up, Vector3.one * 0.035f, glintMat).transform;
        }

        void Update()
        {
            if (!wire) return;
            // a steel thread you only see when it catches the light: a sparkle now and then, and all of it in the lightning
            if (Time.time > nextGlint)
            {
                glintUntil = Time.time + Random.Range(0.05f, 0.1f);
                nextGlint = Time.time + Random.Range(1.5f, 4.5f);
                glint.position = Vector3.Lerp(pegA, pegB, Random.value) + Vector3.up * WireH;
            }
            bool sparkle = Time.time < glintUntil;
            glint.gameObject.SetActive(sparkle);
            if (sparkle && head) glint.rotation = Quaternion.LookRotation(glint.position - head.position);
            float a = 0.16f + Weather.FlashLevel * 0.8f + (sparkle ? 0.3f : 0f);
            wireMat.SetColor("_BaseColor", new Color(0.8f, 0.82f, 0.85f, Mathf.Clamp01(a)));
        }

        /// <summary>True when a foot moving from `from` to `to` this frame went through the wire.</summary>
        public bool Crossed(Vector3 from, Vector3 to)
        {
            float Side(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            Vector2 A = new Vector2(pegA.x, pegA.z), B = new Vector2(pegB.x, pegB.z), P = new Vector2(from.x, from.z), Q = new Vector2(to.x, to.z);
            return Side(A, B, P) * Side(A, B, Q) <= 0 && Side(P, Q, A) * Side(P, Q, B) <= 0 && P != Q;
        }

        // ---------------------------------------------------------------- the blast
        public IEnumerator Detonate(SoldierRig victim, Vector3 landing, Camera cam, AudioSource radio)
        {
            var c = mine + Vector3.up * 0.2f;
            Destroy(visuals.gameObject); wire = null;

            var s = new GameObject("Blast sound").AddComponent<AudioSource>();
            s.transform.position = c; s.clip = CombatFx.I.Clip("blast_close"); s.spatialBlend = 1; s.minDistance = 10; s.maxDistance = 500;
            s.rolloffMode = AudioRolloffMode.Logarithmic; s.dopplerLevel = 0; s.Play();
            if (s.clip) Destroy(s.gameObject, s.clip.length + 0.5f);

            StartCoroutine(LightFlash(c));
            Smoke(c);
            var thrown = victim ? victim.GetComponentsInChildren<Collider>() : new Collider[0];
            Debris(c, thrown);
            Scorch(mine);
            if (victim) Throw(victim, c, landing);
            if (cam) { StartCoroutine(ViewFlash(cam)); StartCoroutine(Ears(cam)); }
            StartCoroutine(Rumble());

            // Comms' set was keyed when he went down: a crushed burst of static in the Engineer's ear, then nothing
            yield return new WaitForSeconds(0.45f);
            if (radio) { radio.Stop(); radio.clip = CombatFx.I.Clip("radio_dead"); if (radio.clip) radio.Play(); }
        }

        void Throw(SoldierRig rig, Vector3 c, Vector3 landing)
        {
            var chest = rig.Chest.position;
            rig.Die(c, aim, 0f);
            // a short arc back toward the Engineer; he hits the dirt and stops there (wet ground: barely a slide)
            const float T = 0.6f;
            var to = landing + Vector3.up * 0.3f;
            var v = new Vector3((to.x - chest.x) / T, (to.y - chest.y + 0.5f * 9.81f * T * T) / T, (to.z - chest.z) / T);
            var bodies = rig.GetComponentsInChildren<Rigidbody>();
            foreach (var rb in bodies)
            {
                if (rb.isKinematic) continue;
                rb.linearVelocity = v + Random.insideUnitSphere * 0.4f;
                rb.angularVelocity = Random.insideUnitSphere * 5f;
            }
            StartCoroutine(Settle(bodies, T));
        }

        static IEnumerator Settle(Rigidbody[] bodies, float flight)
        {
            yield return new WaitForSeconds(flight);
            foreach (var rb in bodies) if (rb) rb.linearDamping = 4f;
            yield return new WaitForSeconds(1.2f);
            foreach (var rb in bodies) if (rb) rb.linearDamping = 0.2f;
        }

        IEnumerator LightFlash(Vector3 c)
        {
            var l = new GameObject("Blast light").AddComponent<Light>();
            l.type = LightType.Point; l.range = 22f; l.color = new Color(1f, 0.78f, 0.5f); l.shadows = LightShadows.None;
            l.transform.position = c + Vector3.up * 0.5f;
            for (float t = 0; t < 0.22f; t += Time.deltaTime) { l.intensity = 45f * (1 - t / 0.22f); yield return null; }
            Destroy(l.gameObject);
        }

        static ParticleSystem Burst(string name, Vector3 at, Quaternion rot, Material m, Vector2 life, Vector2 size, Vector2 speed,
                                    float gravity, float angle, float grow, bool stretch, int count, Vector3 drift = default)
        {
            var go = new GameObject(name); go.transform.SetPositionAndRotation(at, rot);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main; main.playOnAwake = false; main.loop = false; main.maxParticles = count;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
            main.gravityModifier = gravity; main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission; em.enabled = false;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = angle; sh.radius = 0.15f;
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0, 1) });
            col.color = g;
            var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 1, 1, grow));
            if (drift != default)
            {
                var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
                vel.x = drift.x; vel.y = drift.y; vel.z = drift.z;
            }
            var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = m;
            r.renderMode = stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            r.lengthScale = 2.5f; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ps.Emit(count);
            Destroy(go, life.y + 1f);
            return ps;
        }

        static Material Tint(string baseMat, Color c)
        {
            var m = new Material(Resources.Load<Material>(baseMat));
            m.SetTexture("_BaseMap", CombatFx.Soft()); m.SetColor("_BaseColor", c);
            return m;
        }

        void Smoke(Vector3 c)
        {
            var upAim = Quaternion.LookRotation(Vector3.up + aim * 0.6f);
            // the fireball is over in a blink; dirt is thrown up in a fan; a brown-grey cloud rolls out low and drifts on the wind
            Burst("Blast fire", c, upAim, Tint("Fx/FxAdditive", new Color(1f, 0.62f, 0.3f)), new Vector2(0.08f, 0.2f), new Vector2(0.6f, 1.5f), new Vector2(2f, 7f), 0, 70, 1.8f, false, 30);
            Burst("Blast dirt", c, upAim, Tint("Fx/FxAlpha", new Color(0.17f, 0.13f, 0.09f, 0.95f)), new Vector2(0.6f, 1.4f), new Vector2(0.04f, 0.12f), new Vector2(5f, 13f), 1.6f, 40, 0.6f, true, 140);
            Burst("Blast smoke", c + Vector3.up * 0.3f, upAim, Tint("Fx/FxAlpha", new Color(0.24f, 0.21f, 0.18f, 0.8f)), new Vector2(3.5f, 7f), new Vector2(1.2f, 2.6f), new Vector2(0.6f, 3.5f), -0.02f, 55, 2.6f, false, 60, new Vector3(0.8f, 0.15f, 0.3f));
            Burst("Blast dust ring", c, Quaternion.LookRotation(Vector3.up), Tint("Fx/FxAlpha", new Color(0.45f, 0.4f, 0.33f, 0.55f)), new Vector2(1.5f, 3f), new Vector2(0.7f, 1.5f), new Vector2(4f, 8f), 0.05f, 88, 2.2f, false, 50);
        }

        static void Debris(Vector3 c, Collider[] ignore)
        {
            var dirt = Borrow("Terrain");
            for (int i = 0; i < 26; i++)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                g.name = "Blast debris";
                g.transform.position = c + Random.insideUnitSphere * 0.2f + Vector3.up * 0.15f;
                g.transform.rotation = Random.rotation;
                g.transform.localScale = new Vector3(Random.Range(0.03f, 0.1f), Random.Range(0.03f, 0.08f), Random.Range(0.03f, 0.1f));
                var r = g.GetComponent<MeshRenderer>(); if (dirt) r.sharedMaterial = dirt;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var col = g.GetComponent<Collider>();
                foreach (var o in ignore) if (o) Physics.IgnoreCollision(col, o);
                var rb = g.AddComponent<Rigidbody>(); rb.mass = 0.15f; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                var d = Random.onUnitSphere; d.y = Mathf.Abs(d.y);
                rb.linearVelocity = d * Random.Range(2f, 8f) + Vector3.up * Random.Range(3f, 8f);
                rb.angularVelocity = Random.insideUnitSphere * 20f;
                Destroy(g, Random.Range(9f, 14f));
            }
        }

        static void Scorch(Vector3 ground)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(g.GetComponent<Collider>());
            g.name = "Blast scorch";
            g.transform.SetPositionAndRotation(ground + Vector3.up * 0.025f, Quaternion.Euler(90, Random.Range(0, 360f), 0));
            g.transform.localScale = Vector3.one * 1.9f;
            g.GetComponent<MeshRenderer>().sharedMaterial = Tint("Fx/FxAlpha", new Color(0.04f, 0.035f, 0.03f, 0.85f));
        }

        // a hot white blink across the whole view: the light of it, even from behind the steel
        static IEnumerator ViewFlash(Camera cam)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(cam.transform, false); q.transform.localPosition = new Vector3(0, 0, 0.1f); q.transform.localScale = Vector3.one * 0.6f;
            var m = new Material(Resources.Load<Material>("UI/UiUnlit"));
            q.GetComponent<MeshRenderer>().sharedMaterial = m;
            for (float t = 0; t < 0.3f; t += Time.deltaTime)
            {
                float k = 1 - t / 0.3f;
                m.SetColor("_BaseColor", new Color(1f, 0.93f, 0.8f, 0.55f * k * k));
                yield return null;
            }
            Destroy(q); Destroy(m);
        }

        // the ears: the crack gets through, then everything goes dull under a high ring that fades over ~8 s
        static IEnumerator Ears(Camera cam)
        {
            yield return new WaitForSeconds(0.07f);
            var lpf = cam.GetComponent<AudioLowPassFilter>();
            if (!lpf) lpf = cam.gameObject.AddComponent<AudioLowPassFilter>();
            lpf.enabled = true; lpf.lowpassResonanceQ = 1f;
            var ring = new GameObject("Tinnitus").AddComponent<AudioSource>();
            ring.transform.SetParent(cam.transform, false);
            ring.clip = CombatFx.I.Clip("tinnitus"); ring.loop = true; ring.spatialBlend = 0; ring.playOnAwake = false;
            ring.bypassListenerEffects = true;     // the ringing is inside his head: not muffled
            if (ring.clip) ring.Play();
            for (float t = 0; t < 10f; t += Time.deltaTime)
            {
                float k = Mathf.Clamp01((t - 3.5f) / 5.5f);
                lpf.cutoffFrequency = Mathf.Lerp(420f, 22000f, k * k);
                ring.volume = 0.45f * (1f - Mathf.Clamp01((t - 3.8f) / 4.5f));
                yield return null;
            }
            lpf.enabled = false;
            Destroy(ring.gameObject);
        }

        static void Haptic(float amp, float dur)
        {
            foreach (var node in new[] { XRNode.LeftHand, XRNode.RightHand })
            {
                var d = InputDevices.GetDeviceAtXRNode(node);
                if (d.isValid) d.SendHapticImpulse(0, amp, dur);
            }
        }

        static IEnumerator Rumble()
        {
            Haptic(1f, 0.3f); yield return new WaitForSeconds(0.3f);
            Haptic(0.7f, 0.4f); yield return new WaitForSeconds(0.4f);
            Haptic(0.35f, 0.6f);
        }
    }
}
