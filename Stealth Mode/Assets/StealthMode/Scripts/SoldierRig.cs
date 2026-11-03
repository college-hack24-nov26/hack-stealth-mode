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
        public enum Stance { Stand, Crouch, Kneel, Prone, Run, Patrol }

        public Stance stance = Stance.Stand;
        public Transform rifle;            // rifle root = pistol grip; children "SupportHand", "Stock", "Muzzle"
        public Vector3 aimPoint;
        public bool aiming = true;
        [Range(0, 1)] public float breathing = 1f;
        public float runSpeed = 5.5f;
        public float walkSpeed = 1.5f;     // Patrol: ground speed the gait cadence is matched to

        public bool Dead { get; private set; }
        public Transform Muzzle { get; private set; }
        public Transform Head => head;
        public Transform Chest => chest;

        Animator anim;
        readonly List<(Transform t, Quaternion q)> rest = new List<(Transform, Quaternion)>();
        readonly List<(Transform t, Quaternion q)> grip = new List<(Transform, Quaternion)>();
        Vector3 restHipsLocalPos, restHip, restFootL, restFootR;
        Transform lThigh, lShin, lFoot, lToe, rThigh, rShin, rFoot, rToe;
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
            Transform B(HumanBodyBones b) => anim.GetBoneTransform(b);
            hips = B(HumanBodyBones.Hips); spine = B(HumanBodyBones.Spine); chest = B(HumanBodyBones.Chest);
            upperChest = B(HumanBodyBones.UpperChest); neck = B(HumanBodyBones.Neck); head = B(HumanBodyBones.Head);
            rUpper = B(HumanBodyBones.RightUpperArm); rLower = B(HumanBodyBones.RightLowerArm); rHand = B(HumanBodyBones.RightHand);
            lUpper = B(HumanBodyBones.LeftUpperArm); lLower = B(HumanBodyBones.LeftLowerArm); lHand = B(HumanBodyBones.LeftHand);
            rIndex = B(HumanBodyBones.RightIndexProximal) ?? B(HumanBodyBones.RightMiddleProximal);
            lIndex = B(HumanBodyBones.LeftIndexProximal) ?? B(HumanBodyBones.LeftMiddleProximal);
            rThumb = B(HumanBodyBones.RightThumbProximal); lThumb = B(HumanBodyBones.LeftThumbProximal);
            lThigh = B(HumanBodyBones.LeftUpperLeg); lShin = B(HumanBodyBones.LeftLowerLeg); lFoot = B(HumanBodyBones.LeftFoot); lToe = B(HumanBodyBones.LeftToes);
            rThigh = B(HumanBodyBones.RightUpperLeg); rShin = B(HumanBodyBones.RightLowerLeg); rFoot = B(HumanBodyBones.RightFoot); rToe = B(HumanBodyBones.RightToes);

            // the bind pose (standing) is the reference; every frame starts from it and is posed with IK in the soldier's own frame
            var rifleRoot = rifle;
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t != transform && (!rifleRoot || !t.IsChildOf(rifleRoot))) rest.Add((t, t.localRotation));
            restHipsLocalPos = hips.localPosition;
            restHip = transform.InverseTransformPoint(hips.position);
            restFootL = transform.InverseTransformPoint(lFoot.position); restFootR = transform.InverseTransformPoint(rFoot.position);
            BakeGrip();
            seed = Random.value * 10f;
            BuildHitboxes();
            if (rifle) AttachRifle(rifle);
        }

        // finger curl for holding a rifle: taken once from the humanoid muscle space, relative to the bind pose
        void BakeGrip()
        {
            if (!anim.avatar || !anim.avatar.isHuman) return;
            var handler = new HumanPoseHandler(anim.avatar, transform);
            var p0 = transform.position; var r0 = transform.rotation; var par = transform.parent;
            transform.SetParent(null, false); transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                var n = HumanTrait.MuscleName[i];
                if (!n.Contains(" Stretched")) continue;
                bool thumb = n.Contains("Thumb"), index1 = n.Contains("Index 1");
                pose.muscles[i] = Mathf.Clamp(pose.muscles[i] - (thumb ? 0.35f : index1 ? 0.7f : 1.15f), -1f, 1f);
            }
            handler.SetHumanPose(ref pose);
            for (var b = HumanBodyBones.LeftThumbProximal; b <= HumanBodyBones.RightLittleDistal; b++)
            {
                var t = anim.GetBoneTransform(b);
                if (t) grip.Add((t, t.localRotation));
            }
            foreach (var (t, q) in rest) t.localRotation = q;
            hips.localPosition = restHipsLocalPos;
            transform.SetParent(par, true); transform.SetPositionAndRotation(p0, r0);
            handler.Dispose();
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

        // Stance = hip placement + pelvis/spine pitch + two-bone leg IK to planted feet, all in the soldier's frame
        // (x right, y up, z forward, metres). Starts from the bind pose every frame so nothing accumulates.
        void ApplyStance()
        {
            foreach (var (t, q) in rest) t.localRotation = q;
            hips.localPosition = restHipsLocalPos;
            foreach (var (t, q) in grip) t.localRotation = q;

            float breath = Mathf.Sin((Time.time + seed) * 1.6f) * 1.2f * breathing;
            float ankle = restFootL.y, hipH = restHip.y;
            Vector3 lf = restFootL, rf = restFootR, lKnee = Vector3.forward, rKnee = Vector3.forward;
            Vector3 lToeDir = Vector3.zero, rToeDir = Vector3.zero;   // zero = keep the foot as posed
            float hipY = hipH - 0.02f, hipZ = 0, pelvis = 4, bend = 6, nod = 0;
            switch (stance)
            {
                case Stance.Stand:
                    rf += new Vector3(0.03f, 0, -0.14f); lf += new Vector3(-0.03f, 0, 0.06f);
                    break;
                case Stance.Crouch:   // low squat behind cover, rear heel raised
                    hipY = 0.58f; hipZ = -0.08f; pelvis = 28; bend = 14;
                    lf = new Vector3(-0.17f, ankle, 0.22f); rf = new Vector3(0.17f, ankle + 0.07f, -0.24f);
                    lKnee = new Vector3(-0.3f, 0, 1); rKnee = new Vector3(0.3f, 0, 1);
                    lToeDir = new Vector3(0, -0.15f, 1); rToeDir = new Vector3(0, -0.6f, 0.8f);
                    break;
                case Stance.Kneel:    // right knee on the ground, left foot planted forward
                    hipY = 0.6f; pelvis = 10; bend = 10;
                    lf = new Vector3(-0.15f, ankle, 0.42f); rf = new Vector3(0.14f, 0.14f, -0.4f);
                    lKnee = new Vector3(-0.15f, 0.2f, 1); rKnee = new Vector3(0, -1, 0.25f);
                    lToeDir = new Vector3(0, -0.15f, 1); rToeDir = new Vector3(0, -1f, -0.15f);
                    break;
                case Stance.Prone:    // flat on the ground, chest and head raised to the rifle
                    hipY = 0.17f; hipZ = -0.15f; pelvis = 84; bend = -24; nod = -32;
                    lf = new Vector3(-0.24f, 0.11f, -1.0f); rf = new Vector3(0.24f, 0.11f, -1.0f);
                    lKnee = rKnee = Vector3.down;
                    lToeDir = rToeDir = new Vector3(0, -1, -0.35f);
                    break;
                case Stance.Patrol:   // tactical walk: knees soft, hips low, short heel-to-toe steps, torso over the rifle
                {
                    runPhase += Time.deltaTime * walkSpeed * 4.4f;
                    float ws = Mathf.Sin(runPhase), wc = Mathf.Cos(runPhase);
                    hipY = hipH - 0.11f + 0.012f * Mathf.Abs(wc); pelvis = 12; bend = 11;
                    lf = new Vector3(-0.11f, ankle + 0.07f * Mathf.Max(0, wc), 0.27f * ws);
                    rf = new Vector3(0.11f, ankle + 0.07f * Mathf.Max(0, -wc), -0.27f * ws);
                    lKnee = new Vector3(-0.1f, 0, 1); rKnee = new Vector3(0.1f, 0, 1);
                    break;
                }
                case Stance.Run:
                {
                    // cadence rises a little with speed (~2.8 steps/s jogging, ~3 sprinting) and the foot travel is matched
                    // to the ground speed, so the planted foot stays put instead of skating; body pitched into the run,
                    // knees bent, hips dipping at each footfall, head held up
                    float w = 2f * Mathf.PI * (1.25f + 0.045f * runSpeed);
                    runPhase += Time.deltaTime * w;
                    float stride = Mathf.Clamp(runSpeed / w, 0.22f, 0.6f), lift = 0.1f + stride * 0.35f;
                    float s = Mathf.Sin(runPhase), c = Mathf.Cos(runPhase);
                    hipY = hipH - 0.1f + 0.045f * Mathf.Abs(s); pelvis = 18; bend = 12; nod = -16;
                    lf = new Vector3(-0.1f, ankle + lift * Mathf.Max(0, c), stride * s); rf = new Vector3(0.1f, ankle + lift * Mathf.Max(0, -c), -stride * s);
                    break;
                }
            }

            var T = transform;
            hips.position = T.TransformPoint(new Vector3(restHip.x, hipY, restHip.z + hipZ));
            hips.rotation = Quaternion.AngleAxis(pelvis, T.right) * hips.rotation;
            foreach (var b in new[] { spine, chest, upperChest })
                if (b) b.rotation = Quaternion.AngleAxis(bend / 3f + (b == chest ? breath : 0), T.right) * b.rotation;
            if (neck && nod != 0) neck.rotation = Quaternion.AngleAxis(nod, T.right) * neck.rotation;
            Leg(lThigh, lShin, lFoot, lToe, T.TransformPoint(lf), T.TransformDirection(lKnee), lToeDir);
            Leg(rThigh, rShin, rFoot, rToe, T.TransformPoint(rf), T.TransformDirection(rKnee), rToeDir);
        }

        void Leg(Transform thigh, Transform shin, Transform foot, Transform toe, Vector3 ankleTarget, Vector3 kneeDir, Vector3 toeDir)
        {
            if (!thigh || !shin || !foot) return;
            var hint = (thigh.position + ankleTarget) * 0.5f + kneeDir.normalized * 0.5f;
            var footRot = foot.rotation;
            SolveArm(thigh, shin, foot, ankleTarget, hint);
            foot.rotation = footRot;   // the IK swings the foot with the shin; keep it as it was, then aim the toes
            if (toe && toeDir != Vector3.zero)
                foot.rotation = Quaternion.FromToRotation(toe.position - foot.position, transform.TransformDirection(toeDir)) * foot.rotation;
        }

        void LateUpdate()
        {
            if (Dead) return;
            ApplyStance();
            if (!rifle) return;

            Vector3 shoulder = rUpper.position + transform.right * -0.06f + transform.up * -0.02f;
            Vector3 aimDir;
            if (stance == Stance.Patrol)
            {
                // low ready: stock stays in the shoulder pocket, muzzle ~30 degrees down along the walking line, eyes up
                aiming = true;
                aimPoint = shoulder + transform.forward * 4f - Vector3.up * 2.3f;
            }
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
                if (head && stance != Stance.Patrol)
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
                // the rifle rides the stride: a small bounce each footfall and a sway side to side
                if (stance == Stance.Run) rifle.position += transform.up * 0.018f * Mathf.Sin(runPhase * 2f) + transform.right * 0.012f * Mathf.Sin(runPhase);
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
