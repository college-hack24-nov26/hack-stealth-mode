using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

namespace StealthMode
{
    /// <summary>
    /// The Engineer's AK-203.
    /// Quest: the rifle follows the right hand (pistol grip); when the left hand is forward it becomes a
    /// two-handed hold aimed from right to left palm. Curling the right index finger fires (semi-auto).
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
        bool triggerHeld;
        float lastShot, recoil, lean, forcedLean = float.NaN;
        Vector2 look;
        Vector3 rifleVel;
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
        void UpdateXr()
        {
            if (hands == null || !hands.running)
            {
                SubsystemManager.GetSubsystems(found);
                hands = found.Count > 0 ? found[0] : null;
                if (hands == null) return;
            }
            var right = hands.rightHand;
            var left = hands.leftHand;
            if (!right.isTracked) return;
            if (!Joint(right, XRHandJointID.Palm, out var palm) || !Joint(right, XRHandJointID.Wrist, out var wrist) ||
                !Joint(right, XRHandJointID.IndexProximal, out var idxProx) || !Joint(right, XRHandJointID.IndexTip, out var idxTip) ||
                !Joint(right, XRHandJointID.MiddleProximal, out var midProx)) return;

            // one-handed: barrel follows the knuckle line; two-handed: right grip -> left palm
            Vector3 fwd = (midProx.position - wrist.position).normalized;
            Vector3 up = Vector3.up;
            Vector3 grip = palm.position + (palm.rotation * Vector3.down) * 0.02f;
            if (left.isTracked && Joint(left, XRHandJointID.Palm, out var lp))
            {
                var two = lp.position - grip;
                if (two.magnitude > 0.18f && two.magnitude < 0.75f && Vector3.Dot(two.normalized, fwd) > 0.2f)
                    fwd = two.normalized;
            }
            var targetRot = Quaternion.LookRotation(fwd, up) * Quaternion.Euler(-recoil * 6f, 0, 0);
            rifle.rotation = Quaternion.Slerp(rifle.rotation, targetRot, 1f - Mathf.Exp(-Time.deltaTime * 30f));
            rifle.position = Vector3.SmoothDamp(rifle.position, grip - rifle.forward * recoil * 0.03f, ref rifleVel, 0.012f);

            // trigger: index fingertip close to the palm (with hysteresis so one squeeze = one round)
            float curl = Vector3.Distance(idxTip.position, palm.position);
            if (!triggerHeld && curl < 0.055f) { triggerHeld = true; Fire(muzzle.position, muzzle.forward); }
            else if (triggerHeld && curl > 0.072f) triggerHeld = false;
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
