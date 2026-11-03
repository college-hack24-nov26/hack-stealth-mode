using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

namespace StealthMode
{
    /// <summary>
    /// Beat 6: getting Comms out of the open with the left hand. The Engineer holds the left Touch controller too: a small
    /// glove marker shows where his left hand is; it brightens when it is on Comms' vest. Holding the GRIP there takes hold
    /// of the drag handle at the back of his vest; walking backwards pulls the whole body (heavy, the heels drag in the
    /// dirt). Letting go of the grip drops him where he is.
    /// Editor play-test: `simulate` + simHand / simGrip stand in for the controller.
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
        public float Moved { get; private set; }          // metres the body has come from where it landed
        public Vector3 HandPos { get; private set; }

        const float Reach = 0.45f;
        InputAction lPos, lTracked, lGrip;
        Rigidbody collar, handBody;
        SpringJoint joint;
        Material glove;
        Vector3 start;

        public static Rescue Begin(SoldierRig casualty, Camera cam)
        {
            var r = new GameObject("Rescue (left hand)").AddComponent<Rescue>();
            r.casualty = casualty; r.cam = cam;
            return r;
        }

        void Start()
        {
            lPos = new InputAction("Left Grip Position", binding: "<XRController>{LeftHand}/devicePosition", expectedControlType: "Vector3");
            lTracked = new InputAction("Left Tracked", InputActionType.Button, "<XRController>{LeftHand}/isTracked");
            lGrip = new InputAction("Left Grip", InputActionType.Button, "<XRController>{LeftHand}/gripPressed");
            foreach (var a in new[] { lPos, lTracked, lGrip }) a.Enable();

            // the drag handle sits on the back of the vest, between the shoulder blades: the upper-chest body
            foreach (var rb in casualty.GetComponentsInChildren<Rigidbody>())
                if (collar == null || Vector3.Distance(rb.position, casualty.Chest.position) < Vector3.Distance(collar.position, casualty.Chest.position)) collar = rb;
            start = collar.position;

            var g = GameObject.CreatePrimitive(PrimitiveType.Sphere); Destroy(g.GetComponent<Collider>());
            g.name = "Left hand"; g.transform.SetParent(transform, false); g.transform.localScale = Vector3.one * 0.07f;
            glove = new Material(Resources.Load<Material>("UI/UiUnlit")); glove.SetTexture("_BaseMap", Texture2D.whiteTexture);
            var mr = g.GetComponent<MeshRenderer>(); mr.sharedMaterial = glove; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            handBody = gameObject.AddComponent<Rigidbody>(); handBody.isKinematic = true; handBody.interpolation = RigidbodyInterpolation.Interpolate;
        }

        void OnDestroy()
        {
            foreach (var a in new[] { lPos, lTracked, lGrip }) a?.Dispose();
            if (joint) Destroy(joint);
            if (glove) Destroy(glove);
        }

        void Update()
        {
            bool tracked, grip; Vector3 p;
            if (simulate) { tracked = true; grip = simGrip; p = simHand; }
            else if (XRSettings.isDeviceActive)
            {
                tracked = lTracked.ReadValue<float>() > 0.5f;
                grip = lGrip.IsPressed();
                p = cam.transform.parent.TransformPoint(lPos.ReadValue<Vector3>());
            }
            else { tracked = false; grip = false; p = transform.position; }

            transform.GetChild(0).gameObject.SetActive(tracked);
            if (!tracked) { if (joint) Drop(); return; }
            HandPos = p;
            handBody.MovePosition(p);
            bool onVest = Vector3.Distance(p, collar.position) < Reach;
            glove.SetColor("_BaseColor", Holding ? new Color(0.45f, 1f, 0.55f, 0.95f) : onVest ? new Color(1f, 0.92f, 0.45f, 0.95f) : new Color(1f, 1f, 1f, 0.55f));

            if (grip && !joint && onVest) Grab();
            else if (!grip && joint) Drop();
            var d = collar.position - start; d.y = 0; Moved = d.magnitude;
        }

        void Grab()
        {
            foreach (var rb in casualty.GetComponentsInChildren<Rigidbody>()) { rb.interpolation = RigidbodyInterpolation.Interpolate; rb.WakeUp(); }
            joint = collar.gameObject.AddComponent<SpringJoint>();
            joint.connectedBody = handBody; joint.autoConfigureConnectedAnchor = false;
            joint.anchor = Vector3.zero; joint.connectedAnchor = Vector3.zero;
            joint.spring = 6000f; joint.damper = 400f; joint.minDistance = 0; joint.maxDistance = 0.03f;
            Grabs++;
            if (!EverGrabbed) { EverGrabbed = true; FirstGrabTime = Time.time; }
            HeartLink.Event("drag_start", "engineer", $"{{\"grab\":{Grabs}}}");
            Pulse(0.6f, 0.12f);
        }

        /// <summary>Let go of him (grip opened, or the beat is over).</summary>
        public void Drop(bool reachedCover = false)
        {
            if (!joint) return;
            Destroy(joint); joint = null;
            HeartLink.Event("drag_end", "engineer", $"{{\"reached_cover\":{(reachedCover ? "true" : "false")},\"moved_m\":{Moved.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}}}");
        }

        static void Pulse(float amp, float dur)
        {
            var d = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (d.isValid) d.SendHapticImpulse(0, amp, dur);
        }
    }
}
