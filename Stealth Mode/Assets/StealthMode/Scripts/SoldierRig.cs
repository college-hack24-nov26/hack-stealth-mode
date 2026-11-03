using System.Collections.Generic;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// Drives a humanoid soldier without animation clips: stance from muscle values, rifle held with
    /// two-bone IK on both arms, spine/head aiming, procedural run cycle, and ragdoll death.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class SoldierRig : MonoBehaviour
    {
        public enum Stance { Stand, Crouch, Kneel, Prone, Run }

        public Stance stance = Stance.Stand;
        public Transform rifle;            // rifle root = pistol grip; children "SupportHand", "Stock", "Muzzle"
        public Vector3 aimPoint;
        public bool aiming = true;
        [Range(0, 1)] public float breathing = 1f;
        public float runSpeed = 5.5f;

        public bool Dead { get; private set; }
        public Transform Muzzle { get; private set; }
        public Transform Head => head;
        public Transform Chest => chest;

        Animator anim;
        HumanPoseHandler poseHandler;
        HumanPose pose;
        Vector3 restBodyPos;
        Quaternion restBodyRot;
        readonly Dictionary<string, int> muscle = new Dictionary<string, int>();
        Transform hips, spine, chest, upperChest, neck, head;
        Transform rUpper, rLower, rHand, lUpper, lLower, lHand;
        Transform rIndex, rThumb, lIndex, lThumb;
        Transform support, stock;
        float runPhase, seed;
        readonly List<Rigidbody> bodies = new List<Rigidbody>();

        void Awake()
        {
            anim = GetComponent<Animator>();
            anim.enabled = false; // poses are written directly
            poseHandler = new HumanPoseHandler(anim.avatar, transform);
            // read the rest pose with the root at the origin so body position/rotation are local to the soldier
            var p0 = transform.position; var r0 = transform.rotation; var par = transform.parent;
            transform.SetParent(null, false); transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            poseHandler.GetHumanPose(ref pose);
            transform.SetParent(par, true); transform.SetPositionAndRotation(p0, r0);
            restBodyPos = pose.bodyPosition;
            restBodyRot = pose.bodyRotation;
            for (int i = 0; i < HumanTrait.MuscleCount; i++) muscle[HumanTrait.MuscleName[i]] = i;
            Transform B(HumanBodyBones b) => anim.GetBoneTransform(b);
            hips = B(HumanBodyBones.Hips); spine = B(HumanBodyBones.Spine); chest = B(HumanBodyBones.Chest);
            upperChest = B(HumanBodyBones.UpperChest); neck = B(HumanBodyBones.Neck); head = B(HumanBodyBones.Head);
            rUpper = B(HumanBodyBones.RightUpperArm); rLower = B(HumanBodyBones.RightLowerArm); rHand = B(HumanBodyBones.RightHand);
            lUpper = B(HumanBodyBones.LeftUpperArm); lLower = B(HumanBodyBones.LeftLowerArm); lHand = B(HumanBodyBones.LeftHand);
            rIndex = B(HumanBodyBones.RightIndexProximal) ?? B(HumanBodyBones.RightMiddleProximal);
            lIndex = B(HumanBodyBones.LeftIndexProximal) ?? B(HumanBodyBones.LeftMiddleProximal);
            rThumb = B(HumanBodyBones.RightThumbProximal); lThumb = B(HumanBodyBones.LeftThumbProximal);
            seed = Random.value * 10f;
            BuildHitboxes();
            if (rifle) AttachRifle(rifle);
        }

        public void AttachRifle(Transform r)
        {
            rifle = r;
            support = r.Find("SupportHand") ?? FindDeep(r, "SupportHand");
            stock = r.Find("Stock") ?? FindDeep(r, "Stock");
            Muzzle = r.Find("Muzzle") ?? FindDeep(r, "Muzzle");
        }

        static Transform FindDeep(Transform t, string n)
        {
            foreach (var c in t.GetComponentsInChildren<Transform>(true)) if (c.name == n) return c;
            return null;
        }

        void Set(string name, float v) { if (muscle.TryGetValue(name, out var i)) pose.muscles[i] = v; }

        void SetBoth(string part, float v) { Set("Left " + part, v); Set("Right " + part, v); }

        void ApplyStance()
        {
            for (int i = 0; i < pose.muscles.Length; i++) pose.muscles[i] = 0f;
            float t = Time.time + seed;
            float breath = Mathf.Sin(t * 1.6f) * 0.02f * breathing;
            Vector3 body = restBodyPos;
            Quaternion bodyRot = restBodyRot;
            // relaxed arms (IK overrides them when a rifle is held), curled fingers for the grip
            SetBoth("Arm Down-Up", -0.4f); SetBoth("Forearm Stretch", 0.2f);
            foreach (var f in new[] { "Index", "Middle", "Ring", "Little" })
                for (int k = 1; k <= 3; k++) SetBoth(f + " " + k + " Stretched", f == "Index" && k == 1 ? -0.35f : -0.7f);
            SetBoth("Thumb 1 Stretched", -0.2f);
            Set("Chest Front-Back", breath); Set("Spine Front-Back", 0.05f);
            switch (stance)
            {
                case Stance.Stand:
                    SetBoth("Upper Leg In-Out", 0.15f); Set("Left Upper Leg Front-Back", 0.12f); Set("Right Upper Leg Front-Back", -0.05f);
                    SetBoth("Lower Leg Stretch", 0.92f); Set("Left Lower Leg Stretch", 0.85f); SetBoth("Foot Up-Down", 0.05f);
                    Set("Spine Front-Back", 0.12f);
                    body.y -= 0.03f;
                    break;
                case Stance.Crouch:
                    SetBoth("Upper Leg Front-Back", 0.75f); SetBoth("Lower Leg Stretch", -0.55f); SetBoth("Upper Leg In-Out", 0.25f);
                    SetBoth("Foot Up-Down", 0.4f); Set("Spine Front-Back", 0.35f);
                    body.y -= 0.42f;
                    break;
                case Stance.Kneel:
                    Set("Left Upper Leg Front-Back", 0.9f); Set("Left Lower Leg Stretch", -0.75f); Set("Left Foot Up-Down", 0.3f);
                    Set("Right Upper Leg Front-Back", -0.05f); Set("Right Lower Leg Stretch", -1f); Set("Right Foot Up-Down", -0.6f);
                    SetBoth("Upper Leg In-Out", 0.2f); Set("Spine Front-Back", 0.25f);
                    body.y -= 0.5f;
                    break;
                case Stance.Prone:
                    SetBoth("Upper Leg Front-Back", -0.05f); SetBoth("Lower Leg Stretch", 0.9f); SetBoth("Upper Leg In-Out", 0.4f);
                    Set("Spine Front-Back", -0.3f); Set("Chest Front-Back", -0.3f); Set("Neck Nod Down-Up", -0.6f); Set("Head Nod Down-Up", -0.5f);
                    body.y -= 0.8f;
                    bodyRot = bodyRot * Quaternion.Euler(80f, 0, 0);
                    break;
                case Stance.Run:
                    runPhase += Time.deltaTime * runSpeed * 1.35f;
                    float s = Mathf.Sin(runPhase), c = Mathf.Cos(runPhase);
                    Set("Left Upper Leg Front-Back", 0.25f + 0.75f * s); Set("Right Upper Leg Front-Back", 0.25f - 0.75f * s);
                    Set("Left Lower Leg Stretch", 0.2f - 0.9f * Mathf.Max(0, -c)); Set("Right Lower Leg Stretch", 0.2f - 0.9f * Mathf.Max(0, c));
                    SetBoth("Upper Leg In-Out", 0.1f); Set("Spine Front-Back", 0.45f);
                    Set("Spine Twist Left-Right", 0.1f * s);
                    body.y += -0.08f + 0.05f * Mathf.Abs(s);
                    break;
            }
            pose.bodyPosition = body;
            pose.bodyRotation = bodyRot;
            // write the pose in the soldier's local frame, then put the root back where it stands
            var p0 = transform.position; var r0 = transform.rotation;
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            poseHandler.SetHumanPose(ref pose);
            transform.SetPositionAndRotation(p0, r0);
        }

        void LateUpdate()
        {
            if (Dead) return;
            ApplyStance();
            if (!rifle) return;

            Vector3 shoulder = rUpper.position + transform.right * -0.06f + transform.up * -0.02f;
            Vector3 aimDir;
            if (aiming && stance != Stance.Run)
            {
                aimDir = (aimPoint - shoulder).normalized;
                // turn the torso toward the target (yaw on spine/chest, pitch on chest)
                var flat = Vector3.ProjectOnPlane(aimDir, Vector3.up);
                float yaw = Vector3.SignedAngle(Vector3.ProjectOnPlane(transform.forward, Vector3.up), flat, Vector3.up);
                float pitch = -Mathf.Asin(Mathf.Clamp(aimDir.y, -1, 1)) * Mathf.Rad2Deg;
                foreach (var b in new[] { spine, chest, upperChest })
                {
                    if (!b) continue;
                    b.rotation = Quaternion.AngleAxis(yaw / 3f, Vector3.up) * Quaternion.AngleAxis(pitch * 0.25f, transform.right) * b.rotation;
                }
                shoulder = rUpper.position + transform.right * -0.06f + transform.up * -0.02f;
                aimDir = (aimPoint - shoulder).normalized;
                var sway = new Vector3(Mathf.PerlinNoise(Time.time * 0.4f, seed) - 0.5f, Mathf.PerlinNoise(seed, Time.time * 0.4f) - 0.5f, 0) * 0.012f * breathing;
                var rot = Quaternion.LookRotation(aimDir, Vector3.up);
                rifle.rotation = rot;
                var stockOffset = stock ? rifle.InverseTransformPoint(stock.position) : new Vector3(0, 0.08f, -0.41f);
                rifle.position = shoulder + rot * (-stockOffset) + rot * sway;
                if (head)
                {
                    var look = Quaternion.FromToRotation(head.forward, Vector3.Lerp(head.forward, aimDir, 0.6f));
                    head.rotation = look * head.rotation;
                }
            }
            else
            {
                // low ready / port arms while running
                aimDir = (transform.forward * 0.6f - transform.up * 0.6f + transform.right * -0.35f).normalized;
                var rot = Quaternion.LookRotation(aimDir, transform.up);
                rifle.rotation = rot;
                rifle.position = (chest ? chest.position : transform.position + Vector3.up * 1.3f) + transform.forward * 0.28f + transform.right * 0.12f - transform.up * 0.12f;
            }

            // arms onto the rifle
            Vector3 gripPos = rifle.position;
            Vector3 supPos = support ? support.position : rifle.position + rifle.forward * 0.33f;
            SolveArm(rUpper, rLower, rHand, gripPos, rUpper.position - transform.up * 0.35f + transform.right * 0.25f - transform.forward * 0.1f);
            SolveArm(lUpper, lLower, lHand, supPos, supPos - transform.up * 0.35f - transform.right * 0.15f - transform.forward * 0.15f);
            OrientHand(rHand, rIndex, rThumb, (-rifle.right * 0.55f + rifle.forward * 0.25f - rifle.up * 0.6f).normalized, (rifle.up + rifle.forward).normalized);
            OrientHand(lHand, lIndex, lThumb, (rifle.right * 0.75f + rifle.up * 0.55f).normalized, rifle.forward);
        }

        static void SolveArm(Transform a, Transform b, Transform c, Vector3 target, Vector3 hint)
        {
            float la = Vector3.Distance(a.position, b.position), lb = Vector3.Distance(b.position, c.position);
            Vector3 toT = target - a.position;
            float d = Mathf.Clamp(toT.magnitude, 0.01f, la + lb - 0.001f);
            // place the elbow
            float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2 * la * d), -1, 1);
            Vector3 dir = toT.normalized;
            Vector3 bend = Vector3.ProjectOnPlane(hint - a.position, dir).normalized;
            if (bend.sqrMagnitude < 1e-4f) bend = Vector3.down;
            Vector3 elbow = a.position + (dir * cosA + bend * Mathf.Sqrt(1 - cosA * cosA)) * la;
            a.rotation = Quaternion.FromToRotation(b.position - a.position, elbow - a.position) * a.rotation;
            b.rotation = Quaternion.FromToRotation(c.position - b.position, a.position + dir * d - b.position) * b.rotation;
        }

        static void OrientHand(Transform hand, Transform finger, Transform thumb, Vector3 fingerDir, Vector3 thumbDir)
        {
            if (!finger || !thumb) return;
            var curF = (finger.position - hand.position).normalized;
            var curT = Vector3.ProjectOnPlane(thumb.position - hand.position, curF).normalized;
            var want = Quaternion.LookRotation(fingerDir, Vector3.ProjectOnPlane(thumbDir, fingerDir).normalized);
            var have = Quaternion.LookRotation(curF, curT);
            hand.rotation = want * Quaternion.Inverse(have) * hand.rotation;
        }

        // ---------------------------------------------------------------- hitboxes + ragdoll
        void BuildHitboxes()
        {
            void Capsule(HumanBodyBones a, HumanBodyBones b, float r, Hitbox.Part part)
            {
                var ta = anim.GetBoneTransform(a); var tb = anim.GetBoneTransform(b);
                if (!ta || !tb) return;
                var col = ta.gameObject.AddComponent<CapsuleCollider>();
                var local = ta.InverseTransformPoint(tb.position);
                col.center = local * 0.5f; col.radius = r; col.height = local.magnitude + r;
                var ax = Mathf.Abs(local.x) > Mathf.Abs(local.y) ? (Mathf.Abs(local.x) > Mathf.Abs(local.z) ? 0 : 2) : (Mathf.Abs(local.y) > Mathf.Abs(local.z) ? 1 : 2);
                col.direction = ax;
                Add(ta, part);
            }
            void Add(Transform t, Hitbox.Part part)
            {
                var hb = t.gameObject.AddComponent<Hitbox>(); hb.part = part; hb.owner = this;
                var rb = t.gameObject.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.mass = part == Hitbox.Part.Torso ? 12 : 4;
                rb.interpolation = RigidbodyInterpolation.None;
                bodies.Add(rb);
            }
            var hipsBox = hips.gameObject.AddComponent<BoxCollider>();
            hipsBox.size = new Vector3(0.32f, 0.22f, 0.22f) / Mathf.Max(hips.lossyScale.x, 1e-4f);
            Add(hips, Hitbox.Part.Torso);
            var ch = upperChest ? upperChest : chest;
            var chestBox = ch.gameObject.AddComponent<BoxCollider>();
            chestBox.size = new Vector3(0.38f, 0.32f, 0.26f) / Mathf.Max(ch.lossyScale.x, 1e-4f);
            chestBox.center = ch.InverseTransformVector(Vector3.up * -0.05f);
            Add(ch, Hitbox.Part.Torso);
            var hs = head.gameObject.AddComponent<SphereCollider>();
            hs.radius = 0.11f / Mathf.Max(head.lossyScale.x, 1e-4f); hs.center = head.InverseTransformVector(Vector3.up * 0.08f);
            Add(head, Hitbox.Part.Head);
            Capsule(HumanBodyBones.Neck, HumanBodyBones.Head, 0.065f, Hitbox.Part.Torso);
            Capsule(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, 0.08f, Hitbox.Part.Limb);
            Capsule(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, 0.06f, Hitbox.Part.Limb);
            Capsule(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, 0.08f, Hitbox.Part.Limb);
            Capsule(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, 0.06f, Hitbox.Part.Limb);
            Capsule(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, 0.055f, Hitbox.Part.Limb);
            Capsule(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, 0.045f, Hitbox.Part.Limb);
            Capsule(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 0.055f, Hitbox.Part.Limb);
            Capsule(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, 0.045f, Hitbox.Part.Limb);
        }

        public void Die(Vector3 point, Vector3 dir, float force = 180f)
        {
            if (Dead) return;
            Dead = true;
            foreach (var rb in bodies)
            {
                var parentRb = rb.transform.parent ? rb.transform.parent.GetComponentInParent<Rigidbody>() : null;
                if (parentRb && parentRb != rb && parentRb.transform.IsChildOf(transform))
                {
                    var j = rb.gameObject.AddComponent<CharacterJoint>();
                    j.connectedBody = parentRb;
                    j.enablePreprocessing = false;
                    j.lowTwistLimit = new SoftJointLimit { limit = -30 };
                    j.highTwistLimit = new SoftJointLimit { limit = 30 };
                    j.swing1Limit = new SoftJointLimit { limit = 40 };
                    j.swing2Limit = new SoftJointLimit { limit = 30 };
                }
            }
            foreach (var rb in bodies) { rb.isKinematic = false; rb.linearDamping = 0.2f; rb.angularDamping = 1.5f; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; }
            Rigidbody nearest = null; float best = 1e9f;
            foreach (var rb in bodies) { float dd = Vector3.Distance(rb.worldCenterOfMass, point); if (dd < best) { best = dd; nearest = rb; } }
            if (nearest) nearest.AddForceAtPosition(dir.normalized * force, point, ForceMode.Impulse);
            if (rifle)
            {
                rifle.SetParent(null, true);
                var rr = rifle.gameObject.AddComponent<Rigidbody>(); rr.mass = 3.6f;
                var bc = rifle.gameObject.AddComponent<BoxCollider>(); bc.center = new Vector3(0, 0.05f, 0.15f); bc.size = new Vector3(0.06f, 0.2f, 1.0f);
                rr.AddForce(dir.normalized * 2f + Vector3.up, ForceMode.Impulse);
                rifle = null;
            }
            foreach (var hb in GetComponentsInChildren<Hitbox>()) hb.enabled = false;
        }
    }
}
