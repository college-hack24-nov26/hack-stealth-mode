using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace StealthMode
{
    /// <summary>
    /// On the headset: passthrough behind the translucent scene (camera clears to transparent; the environment's materials carry
    /// the 70% alpha, see the scene builder), and the scene stays locked to the real room.
    /// At start the scene is turned/shifted once so the Engineer's head lands on the cover spot; after that the only
    /// correction is undoing a tracking-origin jump (recenter / tracking loss), so walking around never moves the scene.
    /// Solid world: the head can't enter a container, the warehouse, a crate or a rock. When the real head walks into one,
    /// the scene is pushed back by exactly the overlap, so the steel stays in front of your face like a real wall.
    /// Desktop/editor: nothing changes.
    /// </summary>
    public class MixedReality : MonoBehaviour
    {
        public Camera cam;

        public bool Aligned { get; private set; }

        Vector3 spot; float spotYaw;
        Vector3 lastHead; float lastYaw; bool originJumped;
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();

        void Awake()
        {
            spot = transform.position; spotYaw = transform.eulerAngles.y;
            bool xr = XRSettings.isDeviceActive;
            if (xr)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
            }
            SubsystemManager.GetSubsystems(inputs);
            foreach (var s in inputs) s.trackingOriginUpdated += OnOriginJump;
        }

        void OnDestroy()
        {
            foreach (var s in inputs) s.trackingOriginUpdated -= OnOriginJump;
        }

        void OnOriginJump(XRInputSubsystem _) => originJumped = true;

        void LateUpdate()
        {
            if (!XRSettings.isDeviceActive || cam.transform.localPosition == Vector3.zero) return;   // no head pose yet
            var head = cam.transform;
            if (!Aligned)
            {
                Shift(spot, spotYaw);
                Aligned = true;
            }
            else if (originJumped)
            {
                Shift(lastHead, lastYaw);   // put the head back where it was in the scene, so the scene doesn't jump
                originJumped = false;
            }
            PushOutOfSolids(head);
            lastHead = head.position; lastYaw = head.eulerAngles.y;
        }

        const float HeadRadius = 0.16f;
        readonly Collider[] near = new Collider[16];
        SphereCollider headProbe;

        static bool IsGround(Transform t)
        {
            for (; t; t = t.parent) if (t.name == "Terrain") return true;
            return false;
        }

        void PushOutOfSolids(Transform head)
        {
            if (!headProbe)
            {
                var go = new GameObject("Head probe"); go.hideFlags = HideFlags.HideAndDontSave;
                headProbe = go.AddComponent<SphereCollider>(); headProbe.radius = HeadRadius; headProbe.isTrigger = true; headProbe.enabled = false;
            }
            int n = Physics.OverlapSphereNonAlloc(head.position, HeadRadius, near, ~0, QueryTriggerInteraction.Ignore);
            var push = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                var c = near[i];
                if (c.transform.IsChildOf(transform) || c.GetComponent<Hitbox>() || IsGround(c.transform) || c.attachedRigidbody && !c.attachedRigidbody.isKinematic) continue;
                if (Physics.ComputePenetration(headProbe, head.position + push, Quaternion.identity, c, c.transform.position, c.transform.rotation, out var dir, out var dist))
                    push += dir * dist;
            }
            push.y = 0;   // never lift or sink the floor
            if (push.sqrMagnitude > 1e-8f) transform.position += push;
        }

        // move the rig so the head sits at (x,z) of target facing yaw; height stays the real floor height
        void Shift(Vector3 target, float yaw)
        {
            var head = cam.transform;
            transform.RotateAround(head.position, Vector3.up, Mathf.DeltaAngle(head.eulerAngles.y, yaw));
            var d = target - head.position; d.y = 0;
            transform.position += d;
        }
    }
}
