using System.Collections;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// Enemy behaviour for the opening: idle scan, alert, suppressive bursts at the Engineer's cover
    /// (deliberate near misses: nobody can kill the player in this phase), the runner's sprint, death.
    /// </summary>
    public class EnemyAI : MonoBehaviour
    {
        public enum Role { Tower, DoorGuard, Rear, Runner }

        public string id = "Enemy-1";
        public Role role;
        public SoldierRig rig;
        public Transform player;           // player head / camera
        public Vector3[] runPath;          // runner only: last point is inside the warehouse
        public bool canFire = true;
        public float scanArc = 70f;

        public bool Alerted { get; private set; }
        public bool Running { get; private set; }
        public bool Inside { get; private set; }
        public bool Dead => rig && rig.Dead;
        public int Hits { get; private set; }
        public float DeathTime { get; private set; } = -1f;
        public string Killer { get; private set; }

        float baseYaw, nextBurst;

        void Start()
        {
            baseYaw = transform.eulerAngles.y;
            rig.aiming = false;
            nextBurst = float.MaxValue;
        }

        void Update()
        {
            if (Dead || Inside) return;
            if (Running) { RunStep(); return; }
            if (!Alerted)
            {
                // slow scan of the area they are guarding
                float yaw = baseYaw + Mathf.Sin(Time.time * 0.18f + id.GetHashCode() * 0.01f) * scanArc * 0.5f;
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0, yaw, 0), Time.deltaTime * 0.8f);
                return;
            }
            if (!player) return;
            var to = Vector3.ProjectOnPlane(player.position - transform.position, Vector3.up);
            if (to.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), Time.deltaTime * 4f);
            rig.aimPoint = player.position + Vector3.down * 0.3f;
            if (canFire && Time.time >= nextBurst) StartCoroutine(Burst());
        }

        /// <summary>Heard the shot: turn, shoulder the rifle, start firing after a human reaction time.</summary>
        public void Alert(float reaction)
        {
            if (Alerted || Dead) return;
            Alerted = true;
            StartCoroutine(AlertRoutine(reaction));
        }

        IEnumerator AlertRoutine(float reaction)
        {
            yield return new WaitForSeconds(reaction);
            if (Dead) yield break;
            if (role == Role.Runner) { StartRun(); yield break; }
            rig.aiming = true;
            if (role == Role.DoorGuard) rig.stance = SoldierRig.Stance.Crouch;
            nextBurst = Time.time + Random.Range(0.6f, 1.4f);
        }

        IEnumerator Burst()
        {
            nextBurst = Time.time + Random.Range(1.6f, 3.2f);
            int n = Random.Range(2, 5);
            for (int i = 0; i < n && !Dead; i++)
            {
                FireAtCover();
                yield return new WaitForSeconds(Random.Range(0.09f, 0.14f));
            }
        }

        void FireAtCover()
        {
            var muzzle = rig.Muzzle ? rig.Muzzle : rig.Head;
            if (!muzzle) return;
            // aim at a point around the Engineer's cover, never at the head itself
            var right = Vector3.Cross(Vector3.up, (player.position - muzzle.position).normalized);
            float side = Random.Range(0.7f, 2.2f) * (Random.value < 0.5f ? -1 : 1);
            var target = player.position + right * side + Vector3.up * Random.Range(-1.4f, 0.6f);
            var dir = (target - muzzle.position).normalized;
            Vector3 end = muzzle.position + dir * 250f;
            if (Physics.Raycast(muzzle.position + dir * 0.3f, dir, out var hit, 250f, ~0, QueryTriggerInteraction.Ignore) && !hit.collider.GetComponent<Hitbox>())
            {
                end = hit.point;
                bool metal = hit.collider.name.StartsWith("Shipment") || hit.collider.name.Contains("Container") || hit.collider.name.Contains("Watchtower");
                CombatFx.I.Impact(hit.point, hit.normal, metal);
            }
            CombatFx.I.MuzzleFlash(muzzle, 0.9f);
            CombatFx.I.Tracer(muzzle.position, end);
            CombatFx.I.Play("ak_distant", muzzle.position, 1f, 25f);
            // supersonic crack when the round passes near the Engineer
            var closest = muzzle.position + dir * Vector3.Dot(player.position - muzzle.position, dir);
            if (Vector3.Distance(closest, player.position) < 4f && Vector3.Dot(player.position - muzzle.position, dir) < Vector3.Distance(muzzle.position, end) + 2f)
                CombatFx.I.Play("bullet_crack", closest, 0.8f, 2f);
        }

        void StartRun()
        {
            Running = true;
            rig.aiming = false;
            rig.stance = SoldierRig.Stance.Run;
            pathIndex = 0;
        }

        int pathIndex;

        void RunStep()
        {
            if (runPath == null || pathIndex >= runPath.Length) { Inside = true; gameObject.SetActive(false); return; }
            var target = runPath[pathIndex];
            var to = target - transform.position; to.y = 0;
            if (to.magnitude < 0.4f) { pathIndex++; return; }
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), Time.deltaTime * 10f);
            transform.position += to.normalized * rig.runSpeed * Time.deltaTime;
            if (Physics.Raycast(transform.position + Vector3.up * 2f, Vector3.down, out var g, 4f, ~0, QueryTriggerInteraction.Ignore) && !g.collider.GetComponent<Hitbox>())
                transform.position = new Vector3(transform.position.x, g.point.y, transform.position.z);
        }

        /// <summary>Returns true when the hit kills. Head or torso kills; two limb hits kill; the runner can't be stopped.</summary>
        public bool TakeHit(Hitbox.Part part, Vector3 point, Vector3 dir, string shooter)
        {
            if (Dead) return false;
            Hits++;
            if (role == Role.Runner) return false;
            bool kill = part != Hitbox.Part.Limb || Hits >= 2;
            if (!kill) { Alert(0.2f); return false; }
            Kill(point, dir, shooter, part == Hitbox.Part.Head ? 140f : 220f);
            return true;
        }

        public void Kill(Vector3 point, Vector3 dir, string shooter, float force = 200f)
        {
            if (Dead) return;
            StopAllCoroutines();
            Killer = shooter;
            DeathTime = Time.time;
            rig.Die(point, dir, force);
        }
    }
}
