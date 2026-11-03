using UnityEngine;
using UnityEngine.InputSystem;

namespace StealthMode
{
    /// <summary>
    /// The Engineer's AK-203.
    /// Quest: welded to the right Touch controller in a real pistol-grip hold (see UpdateXr); the right trigger fires
    /// (semi-auto).
    /// Editor/desktop fallback: mouse look, left click fires, Q/E lean out of cover.
    /// </summary>
    public class PlayerRifle : MonoBehaviour
    {
        public Transform rifle;
        public Camera cam;
        public Transform trackingSpace;     // XR camera offset (hand joints are reported in this space)
        public float roundsPerSecondCap = 9f;
        public bool armed = true;           // false until the start gate: the start press must not fire a round

        public bool XrActive { get; private set; }
        /// <summary>Right trigger (headset) or left click / Space (desktop) went down this frame.</summary>
        public bool TriggerDown => XrActive ? rTrigger != null && rTrigger.WasPressedThisFrame()
            : (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) || (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame);
        public System.Func<Vector3, Vector3, bool> fireFilter;  // optional: return false to block a shot

        Transform muzzle;

        float lastShot, recoil, lean, forcedLean = float.NaN;
        Vector2 look;

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

        // ---------------------------------------------------------------- Quest right controller
        // The rifle is welded to the right Touch controller: the controller's handle IS the pistol grip. OpenXR's grip
        // pose sits at the centre of the closed fist, +Z along the tube of the fingers (little finger -> thumb), +X out
        // of the back of the right hand. The AK's grip is raked 18 deg (bottom behind the top), so that tube runs up the
        // grip and leans 18 deg toward the muzzle; the palm wraps it a third of the way down, index finger on the
        // trigger. Holding the controller the way you would hold the real grip puts the barrel exactly where a real
        // AK-203 would point. Right trigger fires (semi-auto: one press = one round).
        const float GripRake = 18f;
        static readonly Vector3 GripCenterLocal = new Vector3(0, -0.027f, -0.056f);   // centre of the fist on the grip
        static readonly Quaternion GripInRifle = Quaternion.LookRotation(
            new Vector3(0, Mathf.Cos(GripRake * Mathf.Deg2Rad), Mathf.Sin(GripRake * Mathf.Deg2Rad)),
            new Vector3(0, Mathf.Sin(GripRake * Mathf.Deg2Rad), -Mathf.Cos(GripRake * Mathf.Deg2Rad)));

        InputAction rPos, rRot, rTracked, rTrigger;

        void OnEnable()
        {
            rPos = new InputAction("Right Grip Position", binding: "<XRController>{RightHand}/devicePosition", expectedControlType: "Vector3");
            rRot = new InputAction("Right Grip Rotation", binding: "<XRController>{RightHand}/deviceRotation", expectedControlType: "Quaternion");
            rTracked = new InputAction("Right Tracked", InputActionType.Button, "<XRController>{RightHand}/isTracked");
            rTrigger = new InputAction("Right Trigger", InputActionType.Button, "<XRController>{RightHand}/triggerPressed");
            foreach (var a in new[] { rPos, rRot, rTracked, rTrigger }) a.Enable();
        }

        void OnDisable()
        {
            foreach (var a in new[] { rPos, rRot, rTracked, rTrigger }) a?.Dispose();
        }

        void UpdateXr()
        {
            if (rTracked.ReadValue<float>() < 0.5f) return;   // controller lost or put down: rifle stays where it was
            var space = trackingSpace ? trackingSpace : cam.transform.parent;
            var gripRot = space.rotation * rRot.ReadValue<Quaternion>();
            var gripPos = space.TransformPoint(rPos.ReadValue<Vector3>());
            // recoil kicks the muzzle up about the fist
            var rot = gripRot * Quaternion.Euler(-recoil * 5f, 0, 0) * Quaternion.Inverse(GripInRifle);
            rifle.SetPositionAndRotation(gripPos - rot * GripCenterLocal, rot);
            if (armed && rTrigger.WasPressedThisFrame()) Fire(muzzle.position, muzzle.forward);
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
            if (armed && mouse != null && mouse.leftButton.wasPressedThisFrame) Fire(cam.transform.position, cam.transform.forward);
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
            if (XrActive && UnityEngine.InputSystem.XR.XRController.rightHand is UnityEngine.InputSystem.XR.XRControllerWithRumble pad)
                pad.SendImpulse(0.9f, 0.06f);   // recoil felt in the hand
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
