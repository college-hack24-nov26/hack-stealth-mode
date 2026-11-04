using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace StealthMode
{
    /// <summary>
    /// Scene opacity, poked with the LEFT index fingertip (Shadow Batter's panel, rebuilt): a small "OPACITY" pill floats
    /// low on the Engineer's left and lazily follows the head. Touch the pill to open it; drag the fingertip along the
    /// track to set 20-100 %; touch the red x to close. It scales the alpha of every translucent (_mr) material, i.e.
    /// terrain, hills, plants, rocks, sky, props and soldiers; the warehouse and containers stay solid. Desktop: [ and ] keys.
    /// Nothing in the scene blends with anything else: every surface is drawn as a plain solid with depth, and only the alpha left
    /// in the frame carries the slider, so the headset shows passthrough, never another object, behind the nearest surface.
    /// Materials baked fully opaque (Comms, the Engineer's rifle) stay at 100 %.
    /// </summary>
    public class OpacitySlider : MonoBehaviour
    {
        public Camera cam;
        public Transform trackingSpace;
        [Range(0.2f, 1f)] public float value = 0.7f;

        const float Min = 0.2f, Max = 1f;
        const float TrackHalf = 0.075f;
        const int QueueSolid = 2000, QueueCutout = 2450, QueueSky = 2499;   // the opaque pass: nearest first, sky last, only into what's left

        // Shadow Batter's stadium-neon palette
        static readonly Color Bg = new Color32(12, 15, 14, 200), Track = new Color32(23, 29, 24, 255), Lime = new Color32(168, 230, 66, 255),
            Text = new Color32(238, 245, 233, 255), Close = new Color32(214, 60, 52, 255);

        Transform root, pill, panel, fill, knob, closeBtn;
        TextMesh title;
        Material knobMat;
        bool expanded, armed = true;
        readonly List<(Material m, float a, float cutoff)> mats = new List<(Material, float, float)>();
        XRHandSubsystem hands;
        static readonly List<XRHandSubsystem> found = new List<XRHandSubsystem>();

        void Start()
        {
            CollectMaterials();
            Build();
            Apply();
        }

        // every translucent scene material gets its own runtime copy (never edits the asset), remembered with its baked alpha
        void CollectMaterials()
        {
            var copies = new Dictionary<Material, Material>();
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var shared = r.sharedMaterials; bool changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    var m = shared[i];
                    if (!m || !m.name.EndsWith("_mr") || !m.HasProperty("_BaseColor")) continue;
                    if (!copies.TryGetValue(m, out var c))
                    {
                        c = new Material(m) { name = m.name + " (opacity)" };
                        bool cutout = m.HasProperty("_Cutoff") && (!m.HasProperty("_AlphaClip") || m.GetFloat("_AlphaClip") > 0.5f);
                        float baked = m.GetColor("_BaseColor").a;
                        if (baked >= 0.5f) c.renderQueue = m.name == "Sky_mr" ? QueueSky : cutout ? QueueCutout : QueueSolid;   // real glass (0.25) stays glass
                        if (baked >= 0.5f && m.HasProperty("_DstBlend"))     // these ship premultiplied: overwrite instead of blend, with depth on
                        {
                            c.SetFloat("_SrcBlend", 1); c.SetFloat("_DstBlend", 0); c.SetFloat("_SrcBlendAlpha", 1); c.SetFloat("_DstBlendAlpha", 0);
                            c.SetFloat("_ZWrite", 1);
                        }
                        copies[m] = c; mats.Add((c, m.GetColor("_BaseColor").a, m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f ? 0.45f : 0));
                    }
                    shared[i] = c; changed = true;
                }
                if (changed) r.sharedMaterials = shared;
            }
        }

        void Apply()
        {
            foreach (var (m, a, cutoff) in mats)
            {
                var c = m.GetColor("_BaseColor"); c.a = a > 0.99f ? 1f : a < 0.5f ? a / 0.7f * value : value; m.SetColor("_BaseColor", c);
                if (cutoff > 0) m.SetFloat("_Cutoff", cutoff * c.a);     // fence wire and the like keep their shape at any opacity
            }
            if (title) title.text = $"OPACITY   {Mathf.RoundToInt(value * 100)}%";
            float x = Mathf.Lerp(-TrackHalf, TrackHalf, Mathf.InverseLerp(Min, Max, value));
            if (knob) knob.localPosition = new Vector3(x, -0.012f, -0.006f);
            if (fill) { fill.localScale = new Vector3(Mathf.Max(0.001f, x + TrackHalf), 0.008f, 1); fill.localPosition = new Vector3((x - TrackHalf) / 2f, -0.012f, -0.002f); }
        }

        // ---------------------------------------------------------------- geometry (unlit quads, no colliders)
        void Build()
        {
            var baseMat = Resources.Load<Material>("UI/UiUnlit");
            root = new GameObject("Opacity slider").transform;
            Transform Quad(string n, Transform parent, Vector2 size, Vector3 pos, Color c, PrimitiveType type = PrimitiveType.Quad)
            {
                var go = GameObject.CreatePrimitive(type); go.name = n;
                Destroy(go.GetComponent<Collider>());
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = new Material(baseMat) { color = c }; r.sharedMaterial.SetColor("_BaseColor", c);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                go.transform.SetParent(parent, false); go.transform.localPosition = pos; go.transform.localScale = new Vector3(size.x, size.y, type == PrimitiveType.Quad ? 1 : size.x);
                return go.transform;
            }
            TextMesh Label(string text, Transform parent, Vector3 pos, float size, TextAnchor anchor)
            {
                var go = new GameObject("label"); go.transform.SetParent(parent, false); go.transform.localPosition = pos;
                var tm = go.AddComponent<TextMesh>();
                tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
                tm.text = text; tm.color = Text; tm.fontSize = 64; tm.characterSize = size; tm.anchor = anchor; tm.fontStyle = FontStyle.Bold;
                return tm;
            }
            pill = new GameObject("pill").transform; pill.SetParent(root, false);
            Quad("bg", pill, new Vector2(0.085f, 0.026f), Vector3.zero, Bg);
            Quad("edge", pill, new Vector2(0.004f, 0.026f), new Vector3(-0.0405f, 0, -0.001f), Lime);
            Label("OPACITY", pill, new Vector3(0.003f, 0, -0.002f), 0.0019f, TextAnchor.MiddleCenter);

            panel = new GameObject("panel").transform; panel.SetParent(root, false);
            Quad("bg", panel, new Vector2(0.2f, 0.06f), Vector3.zero, Bg);
            title = Label("", panel, new Vector3(-0.09f, 0.014f, -0.002f), 0.0017f, TextAnchor.MiddleLeft);
            Quad("track", panel, new Vector2(TrackHalf * 2, 0.008f), new Vector3(0, -0.012f, -0.001f), Track);
            fill = Quad("fill", panel, new Vector2(0.01f, 0.008f), new Vector3(0, -0.012f, -0.002f), Lime);
            knob = Quad("knob", panel, new Vector2(0.016f, 0.016f), new Vector3(0, -0.012f, -0.006f), Lime, PrimitiveType.Sphere);
            knobMat = knob.GetComponent<Renderer>().sharedMaterial;
            closeBtn = Quad("close", panel, new Vector2(0.018f, 0.018f), new Vector3(0.085f, 0.016f, -0.001f), Close);
            Label("x", panel, new Vector3(0.085f, 0.017f, -0.003f), 0.0019f, TextAnchor.MiddleCenter);
            SetExpanded(false);
        }

        void SetExpanded(bool on) { expanded = on; pill.gameObject.SetActive(!on); panel.gameObject.SetActive(on); }

        // ---------------------------------------------------------------- per frame
        void LateUpdate()
        {
            Follow();
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                if (kb.leftBracketKey.isPressed) { value = Mathf.Max(Min, value - Time.deltaTime * 0.5f); Apply(); }
                if (kb.rightBracketKey.isPressed) { value = Mathf.Min(Max, value + Time.deltaTime * 0.5f); Apply(); }
            }
            if (!LeftIndexTip(out var tip)) return;
            var p = root.InverseTransformPoint(tip);
            bool touching = p.z > -0.012f && p.z < 0.04f;
            if (!touching && p.z < -0.03f) armed = true;   // finger pulled back: the next touch counts as a new press

            if (!expanded)
            {
                if (armed && touching && Mathf.Abs(p.x) < 0.045f && Mathf.Abs(p.y) < 0.016f) { SetExpanded(true); armed = false; }
                return;
            }
            var c = closeBtn.localPosition;
            if (armed && touching && Mathf.Abs(p.x - c.x) < 0.014f && Mathf.Abs(p.y - c.y) < 0.014f) { SetExpanded(false); armed = false; return; }
            bool onTrack = touching && Mathf.Abs(p.x) < TrackHalf + 0.012f && Mathf.Abs(p.y + 0.012f) < 0.018f;
            knobMat.SetColor("_BaseColor", onTrack ? Color.white : Lime);
            if (onTrack)
            {
                value = Mathf.Lerp(Min, Max, Mathf.InverseLerp(-TrackHalf, TrackHalf, p.x));
                Apply();
            }
        }

        // low on the left, a forearm's reach away; only drifts after the head has clearly moved on
        void Follow()
        {
            var head = cam.transform;
            var f = Vector3.ProjectOnPlane(head.forward, Vector3.up); if (f.sqrMagnitude < 1e-4f) f = Vector3.forward; f.Normalize();
            var right = Vector3.Cross(Vector3.up, f);
            var want = head.position + f * 0.40f - right * 0.24f + Vector3.down * 0.30f;
            if (root.position == Vector3.zero) root.position = want;
            if (Vector3.Distance(root.position, want) > 0.22f) root.position = Vector3.MoveTowards(root.position, want, Time.deltaTime * 1.5f);
            root.rotation = Quaternion.LookRotation(root.position - head.position, Vector3.up);
        }

        bool LeftIndexTip(out Vector3 world)
        {
            world = default;
            if (hands == null || !hands.running)
            {
                SubsystemManager.GetSubsystems(found);
                hands = found.Count > 0 ? found[0] : null;
                if (hands == null) return false;
            }
            var hand = hands.leftHand;
            if (!hand.isTracked || !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var pose)) return false;
            var space = trackingSpace ? trackingSpace : cam.transform.parent;
            world = space.TransformPoint(pose.position);
            return true;
        }
    }
}
