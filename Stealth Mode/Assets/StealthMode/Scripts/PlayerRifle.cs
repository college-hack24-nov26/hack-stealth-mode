using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

namespace StealthMode
{
    /// <summary>
    /// The Engineer's AK-203.
    /// Quest: a free rigid body you pick up with either hand however you like (see UpdateXr); the index finger on the
    /// trigger fires (semi-auto).
    /// Editor/desktop fallback: mouse look, left click fires, Q/E lean out of cover.
    /// </summary>
    public class PlayerRifle : MonoBehaviour
    {
        public Transform rifle;
        public Camera cam;
        public Transform trackingSpace;     // XR camera offset (hand joints are reported in this space)
        public float roundsPerSecondCap = 9f;

        public bool XrActive { get; private set; }
        public System.Func<Vector3, Vector3, bool> fireFilter;  // optional: return false to block a shot

        Transform muzzle;
        XRHandSubsystem hands;

        float lastShot, recoil, lean, forcedLean = float.NaN;
        Vector2 look;

        static readonly List<XRHandSubsystem> found = new List<XRHandSubsystem>();

        void Start()
        {
            foreach (var t in rifle.GetComponentsInChildren<Transform>()) if (t.name == "Muzzle") muzzle = t;
            foreach (var c in rifle.GetComponentsInChildren<Collider>()) c.enabled = false;
            look = new Vector2(transform.eulerAngles.y, 0);
        }

        void Update()
        {
            XrActive = UnityEngine.XR.XRSettings.isDeviceActive;
            if (XrActive) UpdateXr(); else UpdateDesktop();
            recoil = Mathf.MoveTowards(recoil, 0, Time.deltaTime * 6f);
        }

        // ---------------------------------------------------------------- Quest hands
        // The rifle is a real rigid body. It starts floating in front of the Engineer; close a hand around it to pick it
        // up (held exactly the way you grabbed it), open the hand to drop it (gravity, collisions). A second hand on it
        // steers the barrel. Fires when an index finger resting on the trigger squeezes.
        class Grab { public bool holding; public Vector3 localPoint; public Quaternion localRot; public bool trigger; }
        readonly Grab[] grabs = { new Grab(), new Grab() };   // 0 = left, 1 = right
        Rigidbody body;
        Collider bodyCol;
        Vector3 lastPos;
        bool placed;
        static readonly Vector3 TriggerLocal = new Vector3(0, 0.035f, 0.055f);

        void SetupBody()
        {
            body = rifle.gameObject.AddComponent<Rigidbody>();
            body.mass = 3.6f; body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true; body.useGravity = false;
            var box = rifle.gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0, -0.036f, 0.144f); box.size = new Vector3(0.07f, 0.2f, 1.12f);
            bodyCol = box;
            rifle.SetParent(null, true);
        }

        void UpdateXr()
        {
            if (hands == null || !hands.running)
            {
                SubsystemManager.GetSubsystems(found);
                hands = found.Count > 0 ? found[0] : null;
                if (hands == null) return;
            }
            if (!body) SetupBody();
            var mr = GetComponent<MixedReality>();
            if (!placed && (mr == null || mr.Aligned))
            {
                // floating at chest height, an arm's length ahead, barrel pointing away
                var f = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                rifle.SetPositionAndRotation(cam.transform.position + f * 0.42f + Vector3.down * 0.35f, Quaternion.LookRotation(f));
                placed = true;
            }
            if (!placed) return;

            UpdateHand(hands.leftHand, grabs[0], grabs[1]);
            UpdateHand(hands.rightHand, grabs[1], grabs[0]);

            bool held = grabs[0].holding || grabs[1].holding;
            if (held)
            {
                body.isKinematic = true;
                // primary = the hand on the pistol grip (closest to the grip), the other one steers the barrel
                int p = grabs[0].holding && grabs[1].holding
                    ? (grabs[0].localPoint.sqrMagnitude < grabs[1].localPoint.sqrMagnitude ? 0 : 1)
                    : (grabs[0].holding ? 0 : 1);
                var hp = p == 0 ? hands.leftHand : hands.rightHand;
                if (Joint(hp, XRHandJointID.Palm, out var palm))
                {
                    var rot = palm.rotation * grabs[p].localRot;
                    var other = grabs[1 - p];
                    var ho = p == 0 ? hands.rightHand : hands.leftHand;
                    if (other.holding && Joint(ho, XRHandJointID.Palm, out var palm2))
                    {
                        var have = rot * (other.localPoint - grabs[p].localPoint);
                        var want = palm2.position - palm.position;
                        if (want.sqrMagnitude > 0.01f) rot = Quaternion.FromToRotation(have, want) * rot;
                    }
                    rot *= Quaternion.Euler(-recoil * 5f, 0, 0);
                    lastPos = rifle.position;
                    rifle.rotation = rot;
                    rifle.position = palm.position - rot * grabs[p].localPoint;
                }
            }
            else if (body.isKinematic && lastPos != Vector3.zero)
            {
                // just let go: hand the motion to physics so it drops (or is tossed) for real
                body.isKinematic = false; body.useGravity = true;
                body.linearVelocity = (rifle.position - lastPos) / Mathf.Max(Time.deltaTime, 1e-3f);
                lastPos = Vector3.zero;
            }
        }

        void UpdateHand(XRHand hand, Grab g, Grab other)
        {
            if (!hand.isTracked) { g.holding = false; return; }
            if (!Joint(hand, XRHandJointID.Palm, out var palm) || !Joint(hand, XRHandJointID.IndexTip, out var idxTip) ||
                !Joint(hand, XRHandJointID.IndexProximal, out var idxProx)) return;
            // fist = middle, ring and little fingertips curled in to the palm
            float curl = 0; int n = 0;
            foreach (var id in new[] { XRHandJointID.MiddleTip, XRHandJointID.RingTip, XRHandJointID.LittleTip })
                if (Joint(hand, id, out var tip)) { curl += Vector3.Distance(tip.position, palm.position); n++; }
            if (n == 0) return;
            curl /= n;

            if (!g.holding)
            {
                bool near = Vector3.Distance(bodyCol.ClosestPoint(palm.position), palm.position) < 0.09f;
                if (near && curl < 0.065f)
                {
                    g.holding = true;
                    g.localPoint = rifle.InverseTransformPoint(palm.position);
                    g.localRot = Quaternion.Inverse(palm.rotation) * rifle.rotation;
                }
            }
            else if (curl > 0.085f) { g.holding = false; g.trigger = false; return; }

            // trigger: only the index finger that is actually on the trigger, squeezed (hysteresis: one squeeze = one round)
            if (!g.holding) return;
            bool onTrigger = Vector3.Distance(idxTip.position, rifle.TransformPoint(TriggerLocal)) < 0.07f;
            float bend = Vector3.Distance(idxTip.position, idxProx.position);
            if (!g.trigger && onTrigger && bend < 0.05f) { g.trigger = true; Fire(muzzle.position, muzzle.forward); }
            else if (g.trigger && bend > 0.062f) g.trigger = false;
        }

        bool Joint(XRHand hand, XRHandJointID id, out Pose world)
        {
            world = default;
            if (!hand.GetJoint(id).TryGetPose(out var p)) return false;
            var space = trackingSpace ? trackingSpace : cam.transform.parent;
            world = new Pose(space.TransformPoint(p.position), space.rotation * p.rotation);
            return true;
        }

        // ---------------------------------------------------------------- desktop / editor
        void UpdateDesktop()
        {
            var tpd = cam.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            if (tpd && tpd.enabled) tpd.enabled = false;
            var mouse = Mouse.current; var kb = Keyboard.current;
            if (mouse != null && (mouse.rightButton.isPressed || Cursor.lockState == CursorLockMode.Locked))
            {
                var d = mouse.delta.ReadValue() * 0.12f;
                look.x += d.x; look.y = Mathf.Clamp(look.y - d.y, -70, 70);
            }
            float target = 0;
            if (kb != null) { if (kb.qKey.isPressed) target = -1; if (kb.eKey.isPressed) target = 1; }
            if (!float.IsNaN(forcedLean)) target = forcedLean;
            lean = Mathf.MoveTowards(lean, target, Time.deltaTime * 3f);
            cam.transform.localPosition = new Vector3(lean * 0.6f, 1.62f - Mathf.Abs(lean) * 0.06f, 0);
            cam.transform.localRotation = Quaternion.Euler(look.y - recoil * 3f, look.x - transform.eulerAngles.y, -lean * 8f);
            rifle.position = cam.transform.TransformPoint(new Vector3(0.13f, -0.17f, 0.32f - recoil * 0.04f));
            rifle.rotation = cam.transform.rotation * Quaternion.Euler(-recoil * 4f, 0, 0);
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) Fire(cam.transform.position, cam.transform.forward);
        }

        /// <summary>Aim the desktop camera straight at a world point (used by the automated play-test).</summary>
        public void AimAt(Vector3 p)
        {
            var d = (p - cam.transform.position).normalized;
            look.x = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            look.y = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
        }

        public void SetLean(float v) => forcedLean = Mathf.Clamp(v, -1, 1);

        public void PullTrigger()
        {
            if (XrActive) Fire(muzzle.position, muzzle.forward); else Fire(cam.transform.position, cam.transform.forward);
        }

        // ---------------------------------------------------------------- ballistics
        void Fire(Vector3 origin, Vector3 dir)
        {
            if (Time.time - lastShot < 1f / roundsPerSecondCap) return;
            if (fireFilter != null && !fireFilter(origin, dir)) return;
            lastShot = Time.time;
            recoil = 1f;
            CombatFx.I.MuzzleFlash(muzzle, 1.1f);
            CombatFx.I.Play("ak_close", muzzle.position, 1f, 6f, spatial: false);
            string target = null, part = null; float dist = -1;
            var hitsAll = Physics.RaycastAll(origin, dir, 400f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hitsAll, (a, b) => a.distance.CompareTo(b.distance));
            Vector3 end = origin + dir * 400f;
            foreach (var h in hitsAll)
            {
                if (h.collider.transform.IsChildOf(transform) || h.collider.transform.IsChildOf(rifle)) continue;
                end = h.point; dist = h.distance;
                var hb = h.collider.GetComponent<Hitbox>();
                if (hb && hb.owner)
                {
                    var ai = hb.owner.GetComponent<EnemyAI>();
                    if (ai == null) break; // friendly: the round stops, no score
                    target = ai.id; part = hb.part.ToString();
                    if (ai.TakeHit(hb.part, h.point, dir, "Engineer")) OpeningMetrics.RecordKill(ai.id, "Engineer");
                    CombatFx.I.Impact(h.point, -dir, false);
                }
                else
                {
                    bool metal = h.collider.name.StartsWith("Shipment") || h.collider.name.Contains("Container") || h.collider.name.Contains("Watchtower") || h.collider.name.Contains("Warehouse");
                    CombatFx.I.Impact(h.point, h.normal, metal);
                }
                break;
            }
            if (Random.value < 0.35f) CombatFx.I.Tracer(muzzle.position, end);
            OpeningMetrics.RecordShot(target, part, dist);
        }
    }
}
