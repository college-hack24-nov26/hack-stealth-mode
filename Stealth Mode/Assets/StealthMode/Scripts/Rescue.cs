using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace StealthMode
{
    /// <summary>
    /// Beat 6: getting Comms out of the open with the bare left hand (hand tracking; the left controller is not used). Close
    /// the hand or pinch with any part of it on any part of him, and that spot of his body is held by a spring to the hand:
    /// the ragdoll follows through its own joints, heavy, dragging on the ground. A toe works as well as the collar. Open the
    /// hand and he drops where he is. Small dots show the tracked hand: white, yellow in reach of him, green holding.
    /// Editor play-test: `simulate` + simHand / simGrip stand in for the hand.
    /// </summary>
    public class Rescue : MonoBehaviour
    {
        public SoldierRig casualty;
        public Camera cam;
        public bool simulate; public Vector3 simHand; public bool simGrip;

        public bool Holding => joint;
        public bool EverGrabbed { get; private set; }
        public float FirstGrabTime { get; private set; } = -1;
        public int Grabs { get; private set; }
        public float Moved { get; private set; }          // metres his chest has been pulled from where he was first grabbed

        const float Reach = 0.12f;                        // any joint of the hand this close to his body is touching him
        const float CurlIn = 0.085f, CurlOut = 0.105f, PinchIn = 0.03f, PinchOut = 0.05f;
        const float LostGrace = 0.4f;                     // tracking blinks while the hand is closed: don't drop him for that
        const int FirstJoint = (int)XRHandJointID.Wrist, LastJoint = (int)XRHandJointID.LittleTip;
        static readonly XRHandJointID[] Curling = { XRHandJointID.IndexTip, XRHandJointID.MiddleTip, XRHandJointID.RingTip, XRHandJointID.LittleTip };
        static readonly List<XRHandSubsystem> found = new List<XRHandSubsystem>();

        XRHandSubsystem hands;
        readonly List<Vector3> joints = new List<Vector3>();
        readonly List<Transform> dots = new List<Transform>();
        Collider[] body;
        Rigidbody handBody;
        SpringJoint joint;
        Material dotMat;
        Vector3 palm, start;
        bool closed;
        float lastSeen, nextLog;

        public static Rescue Begin(SoldierRig casualty, Camera cam)
        {
            var r = new GameObject("Rescue (left hand)").AddComponent<Rescue>();
            r.casualty = casualty; r.cam = cam;
            return r;
        }

        void Start()
        {
            var parts = new List<Collider>();
            foreach (var c in casualty.GetComponentsInChildren<Collider>())
                if (c.attachedRigidbody && !c.isTrigger) parts.Add(c);
            body = parts.ToArray();
            handBody = gameObject.AddComponent<Rigidbody>(); handBody.isKinematic = true; handBody.interpolation = RigidbodyInterpolation.Interpolate;
            dotMat = new Material(Resources.Load<Material>("UI/UiUnlit")); dotMat.SetTexture("_BaseMap", Texture2D.whiteTexture);
            for (int i = FirstJoint; i <= LastJoint; i++)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Sphere); Destroy(g.GetComponent<Collider>());
                g.name = "Hand joint"; g.transform.SetParent(transform, false); g.transform.localScale = Vector3.one * 0.016f;
                var mr = g.GetComponent<MeshRenderer>(); mr.sharedMaterial = dotMat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                dots.Add(g.transform);
            }
            // a limp body slides along the dirt instead of sticking to it
            var slide = new PhysicsMaterial("Dragged body") { dynamicFriction = 0.35f, staticFriction = 0.4f, frictionCombine = PhysicsMaterialCombine.Minimum };
            foreach (var c in body) c.sharedMaterial = slide;
            foreach (var rb in casualty.GetComponentsInChildren<Rigidbody>()) { rb.solverIterations = 16; rb.solverVelocityIterations = 4; }
        }

        void OnDestroy()
        {
            if (joint) Destroy(joint);
            if (dotMat) Destroy(dotMat);
        }

        void Update()
        {
            bool tracked = ReadHand();
            for (int i = 0; i < dots.Count; i++)
            {
                bool on = tracked && i < joints.Count;
                dots[i].gameObject.SetActive(on);
                if (on) dots[i].position = joints[i];
            }
            if (!tracked)
            {
                if (joint && Time.time - lastSeen > LostGrace) Drop();
                return;
            }
            lastSeen = Time.time;
            handBody.MovePosition(palm);

            Touching(out var part, out var contact, out var gap);
            bool inReach = part != null;
            dotMat.SetColor("_BaseColor", Holding ? new Color(0.45f, 1f, 0.55f, 0.95f) : inReach ? new Color(1f, 0.92f, 0.45f, 0.95f) : new Color(1f, 1f, 1f, 0.6f));
            if (Time.time > nextLog)
            {
                // what the left hand is doing, once a second, for checking the headset runs (adb logcat -s Unity)
                nextLog = Time.time + 1f;
                Debug.Log($"[StealthMode] RESCUE hand closed={closed} nearest {(part ? part.name : "-")} {gap:0.00} m, holding={Holding}, moved {Moved:0.00} m");
            }
            if (closed && !joint && inReach) Grab(part, contact);
            else if (!closed && joint) Drop();
            var d = casualty.Chest.position - start; d.y = 0;
            if (EverGrabbed) Moved = d.magnitude;
        }

        // the left hand this frame: every joint in world space, the palm, and whether it is closed or pinching
        bool ReadHand()
        {
            joints.Clear();
            if (simulate) { palm = simHand; closed = simGrip; joints.Add(simHand); return true; }
            if (hands == null || !hands.running)
            {
                SubsystemManager.GetSubsystems(found);
                hands = found.Count > 0 ? found[0] : null;
                if (hands == null) return false;
            }
            var hand = hands.leftHand;
            if (!hand.isTracked) return false;
            var space = cam.transform.parent;
            Vector3 World(XRHandJointID id) => hand.GetJoint(id).TryGetPose(out var p) ? space.TransformPoint(p.position) : Vector3.zero;
            for (int i = FirstJoint; i <= LastJoint; i++) { var w = World((XRHandJointID)i); if (w != Vector3.zero) joints.Add(w); }
            palm = World(XRHandJointID.Palm);
            if (palm == Vector3.zero || joints.Count == 0) return false;
            int curled = 0;
            foreach (var tip in Curling) if (Vector3.Distance(World(tip), palm) < (closed ? CurlOut : CurlIn)) curled++;
            float pinch = Vector3.Distance(World(XRHandJointID.ThumbTip), World(XRHandJointID.IndexTip));
            closed = curled >= 3 || pinch < (closed ? PinchOut : PinchIn);
            return true;
        }

        // the part of him nearest to any joint of the hand, if it is in reach
        void Touching(out Rigidbody part, out Vector3 contact, out float gap)
        {
            part = null; contact = default; gap = float.MaxValue;
            foreach (var c in body)
            {
                if (!c) continue;
                foreach (var j in joints)
                {
                    var p = c.ClosestPoint(j);
                    float d = Vector3.Distance(p, j);
                    if (d >= gap) continue;
                    gap = d; contact = p;
                    part = d < Reach ? c.attachedRigidbody : null;
                }
            }
        }

        void Grab(Rigidbody part, Vector3 contact)
        {
            foreach (var rb in casualty.GetComponentsInChildren<Rigidbody>()) { rb.interpolation = RigidbodyInterpolation.Interpolate; rb.WakeUp(); }
            joint = part.gameObject.AddComponent<SpringJoint>();
            joint.connectedBody = handBody; joint.autoConfigureConnectedAnchor = false;
            joint.anchor = part.transform.InverseTransformPoint(contact);
            joint.connectedAnchor = contact - palm;           // the hand body doesn't rotate: he stays held where he was touched
            joint.spring = 6000f; joint.damper = 400f; joint.minDistance = 0; joint.maxDistance = 0.03f;
            joint.massScale = 4f;                             // one light limb must be able to carry the pull into the rest of him
            Grabs++;
            if (!EverGrabbed) { EverGrabbed = true; FirstGrabTime = Time.time; start = casualty.Chest.position; }
            Debug.Log($"[StealthMode] RESCUE grabbed him by {part.name}");
            HeartLink.Event("drag_start", "engineer", $"{{\"grab\":{Grabs},\"part\":{HeartLink.Str(part.name)}}}");
        }

        /// <summary>Let go of him (hand opened, or the beat is over).</summary>
        public void Drop(bool reachedCover = false)
        {
            if (!joint) return;
            Destroy(joint); joint = null;
            HeartLink.Event("drag_end", "engineer", $"{{\"reached_cover\":{(reachedCover ? "true" : "false")},\"moved_m\":{Moved.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}}}");
        }

        /// <summary>The drag is over: no more hand dots.</summary>
        public void Hide()
        {
            enabled = false;
            foreach (var d in dots) d.gameObject.SetActive(false);
        }
    }
}
