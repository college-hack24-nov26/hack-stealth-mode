using System.Collections;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// Scenario One opening, per Pulin's map and the approved kill plan:
    /// Captain's mark -> Sniper-1 drops Enemy-5 -> Engineer engages Enemy-1 (Watchtower-2) and Enemy-4 (right door guard),
    /// Enemy-2 (left tower) is the stretch target (Comms takes him if the Engineer hasn't in time) ->
    /// Enemy-3 sprints into the warehouse to raise the alarm -> hold.
    /// Second half (built one beat at a time, see the approved beat plan):
    /// Beat 1: the runner and the five inside burst out of the front door to firing positions and pin down Shipment-1.
    /// The run starts only when the Engineer presses the right trigger, so the rifle is already on his controller.
    /// </summary>
    public class OpeningDirector : MonoBehaviour
    {
        public EnemyAI enemy1, enemy2, enemy3, enemy4, enemy5;
        public EnemyAI[] insiders;          // Enemy-6..10, guarding the bomb
        public SoldierRig comms;
        public Transform sniper1Muzzle, sniper2Muzzle;
        public PlayerRifle player;
        public AudioSource radio;
        public AudioSource alarm;
        public float stretchWindow = 6f, engineerDeadline = 40f;
        public bool autoPlay;               // editor play-test: the rig aims and fires by itself

        string phase = "gate";
        public string Phase { get => phase; private set { phase = value; OpeningMetrics.Phase = value; HeartLink.Event("phase:" + value); } }
        bool narrationDone;

        IEnumerator Start()
        {
            OpeningMetrics.Reset();
            foreach (var e in new[] { enemy1, enemy2, enemy3, enemy4, enemy5 }) e.player = player.cam.transform;
            if (comms) { comms.stance = SoldierRig.Stance.Kneel; comms.aiming = true; comms.aimPoint = enemy4.transform.position + Vector3.up * 1.2f; }
            CombatFx.I.Play("wind_loop", player.cam.transform.position, 0.35f, 50f, spatial: false).loop = true;
            if (!autoPlay) yield return StartGate();
            Phase = "briefing";
            yield return new WaitForSeconds(2f);

            yield return Say("r01_captain");
            yield return Say("r02_captain");
            yield return Say("r03_sniper1");
            yield return Say("r04_captain_mark");

            // ---------------------------------------------------------- the mark
            Phase = "engagement";
            OpeningMetrics.MarkTime = Time.time;
            CombatFx.I.Play("sniper_distant", sniper1Muzzle.position, 1f, 60f);
            yield return new WaitForSeconds(0.18f);
            enemy5.Kill(enemy5.rig.Head.position, (enemy5.rig.Head.position - sniper1Muzzle.position).normalized, "Sniper-1", 260f);
            OpeningMetrics.RecordKill(enemy5.id, "Sniper-1");
            enemy1.Alert(Random.Range(0.7f, 1.1f));
            enemy2.Alert(Random.Range(0.9f, 1.3f));
            enemy4.Alert(Random.Range(0.5f, 0.9f));
            enemy3.Alert(2.4f);                          // the runner hesitates, then sprints for the door
            StartCoroutine(Narration());
            StartCoroutine(Exposure(new[] { enemy1, enemy2, enemy4 }, "engagement"));
            if (autoPlay) StartCoroutine(AutoPlay());

            // ---------------------------------------------------------- Engineer's window
            float t0 = Time.time, bothDownAt = -1;
            while (true)
            {
                bool mainDown = enemy1.Dead && enemy4.Dead;
                if (mainDown && bothDownAt < 0) bothDownAt = Time.time;
                bool stretchOver = enemy2.Dead || (bothDownAt > 0 && Time.time - bothDownAt > stretchWindow) || Time.time - t0 > 30f;
                if (stretchOver && !enemy2.Dead) yield return CommsTakesTower();
                if (Time.time - t0 > engineerDeadline)
                {
                    // fail-safe so the scenario always completes: Sniper-2 finishes whoever is left
                    foreach (var e in new[] { enemy1, enemy4 })
                        if (!e.Dead) { yield return SniperTwoTakes(e); }
                }
                if (enemy3.Inside && OpeningMetrics.RunnerInsideTime < 0) OpeningMetrics.RunnerInsideTime = Time.time;
                if (enemy1.Dead && enemy2.Dead && enemy4.Dead && enemy3.Inside && narrationDone) break;
                yield return null;
            }

            // ---------------------------------------------------------- alarm + hold
            Phase = "alarm";
            yield return new WaitForSeconds(1.5f);
            if (alarm) alarm.Play();
            yield return Say("r10_captain_hold");
            Phase = "hold";
            OpeningMetrics.EndTime = Time.time;
            OpeningMetrics.Save();
            Debug.Log("[StealthMode] OPENING COMPLETE");

            yield return BeatOne();
        }

        // ---------------------------------------------------------- start gate
        // A plain floating line of text in front of the Engineer; nothing else happens until the right trigger goes down.
        IEnumerator StartGate()
        {
            player.armed = false;
            var panel = new GameObject("Start prompt").transform;
            TextMesh Line(string text, Color c, Vector3 offset)
            {
                var go = new GameObject("text"); go.transform.SetParent(panel, false); go.transform.localPosition = offset;
                var tm = go.AddComponent<TextMesh>();
                tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
                tm.text = text; tm.color = c; tm.fontSize = 96; tm.characterSize = 0.0042f;
                tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center; tm.fontStyle = FontStyle.Bold;
                return tm;
            }
            var msg = player.XrActive ? "Press RIGHT TRIGGER\non your Quest controller\nto start the simulation" : "Click or press Space\nto start the simulation";
            Line(msg, new Color(0, 0, 0, 0.85f), new Vector3(0.004f, -0.004f, 0.002f));   // drop shadow, readable over passthrough
            Line(msg, new Color(0.96f, 0.97f, 0.94f), Vector3.zero);
            yield return null;
            while (!player.TriggerDown)
            {
                // floats 1.1 m ahead at eye height and lazily follows the head
                var head = player.cam.transform;
                var f = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                if (f.sqrMagnitude < 0.01f) f = Vector3.forward;
                var want = head.position + f * 1.1f + Vector3.down * 0.05f;
                panel.position = Vector3.Lerp(panel.position == Vector3.zero ? want : panel.position, want, Time.deltaTime * 3f);
                panel.rotation = Quaternion.LookRotation(panel.position - head.position, Vector3.up);
                yield return null;
            }
            Destroy(panel.gameObject);
            OpeningMetrics.StartPressTime = Time.time;
            yield return null;          // the press that started the run is not a shot
            player.armed = true;
        }

        // ---------------------------------------------------------- beat 1: they all come out
        // The runner and the five guards from inside pour out of the front door in a stack and spread to firing positions
        // (barrels, door frame, crates, generator, tower-2 crate), then pin down Shipment-1. Near misses only.
        static readonly (Vector3 pos, SoldierRig.Stance stance)[] FiringPositions =
        {
            (new Vector3(0.2f, 0.35f, -8.8f), SoldierRig.Stance.Stand),     // just inside the door, east edge
            (new Vector3(6.5f, 0f, -10.9f), SoldierRig.Stance.Crouch),      // behind the barrels
            (new Vector3(-4.2f, 0.35f, -8.8f), SoldierRig.Stance.Stand),    // just inside the door, west edge
            (new Vector3(15.3f, 0f, -4.4f), SoldierRig.Stance.Crouch),      // behind the generator
            (new Vector3(-11.0f, 0f, -10.7f), SoldierRig.Stance.Crouch),    // behind the crates
            (new Vector3(19.0f, 0f, -2.0f), SoldierRig.Stance.Crouch),      // behind the crate at tower-2
        };

        IEnumerator BeatOne()
        {
            yield return new WaitForSeconds(3f);
            Phase = "beat1_breakout";
            OpeningMetrics.Beat1Time = Time.time;
            CombatFx.I.Play("bullet_crack", new Vector3(-2f, 1.5f, -10f), 0.6f, 20f);
            var squad = new System.Collections.Generic.List<EnemyAI>();
            if (!enemy3.Dead) squad.Add(enemy3);
            foreach (var e in insiders) if (e && !e.Dead) squad.Add(e);
            for (int i = 0; i < squad.Count; i++)
            {
                var (pos, stance) = FiringPositions[i % FiringPositions.Length];
                float lane = -2f + (i % 3 - 1) * 1.2f;   // three abreast through the 6 m door
                var start = new Vector3(lane, 0.35f, -3.5f - (i % 2) * 1.5f);
                var path = pos.z > -10f
                    ? new[] { start, new Vector3(pos.x, 0.35f, -6.5f), pos }      // holds the doorway from inside
                    : new[] { start, new Vector3(lane, 0.35f, -9.2f), new Vector3(lane, 0f, -12.6f),
                              Vector3.Lerp(new Vector3(lane, 0f, -12.6f), pos, 0.6f), pos };
                squad[i].player = player.cam.transform;
                squad[i].Assault(path, stance, 0.15f + i * 0.45f);
            }
            StartCoroutine(Exposure(squad.ToArray(), "beat1_breakout"));
            HeartLink.Event("beat1_breakout");
            Debug.Log("[StealthMode] BEAT 1: " + squad.Count + " enemies out");
            yield return new WaitForSeconds(30f);
            OpeningMetrics.Save();      // Beat 1 snapshot; the firefight carries on until beat 2 exists
        }

        IEnumerator Narration()
        {
            yield return new WaitForSeconds(1.1f);
            yield return Say("r05_sniper1_down");
            yield return Say("r06_captain_engage");
            while (!enemy3.Running) yield return null;
            yield return new WaitForSeconds(0.5f);
            yield return Say("r07_sniper2_runner");
            while (!enemy3.Inside) yield return null;
            narrationDone = true;
        }

        IEnumerator CommsTakesTower()
        {
            if (!comms) { enemy2.Kill(enemy2.rig.Chest.position, Vector3.forward, "Comms"); OpeningMetrics.RecordKill(enemy2.id, "Comms"); yield break; }
            comms.aimPoint = enemy2.rig.Chest.position;
            yield return new WaitForSeconds(0.6f);
            for (int i = 0; i < 3; i++)
            {
                CombatFx.I.MuzzleFlash(comms.Muzzle);
                CombatFx.I.Play("ak_close", comms.Muzzle.position, 0.9f, 8f);
                CombatFx.I.Tracer(comms.Muzzle.position, enemy2.rig.Chest.position);
                yield return new WaitForSeconds(0.12f);
            }
            enemy2.Kill(enemy2.rig.Chest.position, (enemy2.rig.Chest.position - comms.Muzzle.position).normalized, "Comms");
            OpeningMetrics.RecordKill(enemy2.id, "Comms");
            yield return Say("r08_comms_tower");
            comms.aimPoint = enemy4.Dead ? enemy3.transform.position + Vector3.up : enemy4.transform.position + Vector3.up * 1.2f;
        }

        IEnumerator SniperTwoTakes(EnemyAI e)
        {
            CombatFx.I.Play("sniper_distant", sniper2Muzzle.position, 1f, 60f);
            yield return new WaitForSeconds(0.15f);
            e.Kill(e.rig.Chest.position, (e.rig.Chest.position - sniper2Muzzle.position).normalized, "Sniper-2", 260f);
            OpeningMetrics.RecordKill(e.id, "Sniper-2");
            yield return Say("r09_sniper2_down");
        }

        IEnumerator Exposure(EnemyAI[] shooters, string during)
        {
            // exposed = an armed, living enemy has a clear line of sight to the Engineer's head
            var head = player.cam.transform;
            const float step = 0.1f;
            while (Phase == during)
            {
                foreach (var e in shooters)
                {
                    if (e.Dead || !e.Alerted || !e.rig.Head) continue;
                    var from = e.rig.Head.position; var to = head.position;
                    bool blocked = false;
                    foreach (var h in Physics.RaycastAll(from, (to - from).normalized, Vector3.Distance(from, to) - 0.3f, ~0, QueryTriggerInteraction.Ignore))
                        if (!h.collider.GetComponent<Hitbox>() && !h.collider.transform.IsChildOf(player.transform) && !h.collider.transform.IsChildOf(player.rifle)) { blocked = true; break; }
                    if (!blocked)
                    {
                        if (during == "engagement") OpeningMetrics.ExposedSeconds += step; else OpeningMetrics.Beat1ExposedSeconds += step;
                        break;
                    }
                }
                yield return new WaitForSeconds(step);
            }
        }

        IEnumerator Say(string clip)
        {
            var c = CombatFx.I.Clip("Radio/" + clip);
            if (c == null) { Debug.LogWarning("[StealthMode] missing radio clip " + clip); yield break; }
            while (radio.isPlaying) yield return null;
            radio.clip = c; radio.Play();
            Debug.Log("[StealthMode] radio: " + clip);
            yield return new WaitForSeconds(c.length + 0.35f);
        }

        // ---------------------------------------------------------- editor play-test
        IEnumerator AutoPlay()
        {
            yield return new WaitForSeconds(1.4f);
            player.SetLean(-1);
            foreach (var e in new[] { enemy1, enemy4, enemy2 })
            {
                yield return new WaitForSeconds(0.9f);
                int shots = 0;
                while (!e.Dead && shots < 6)
                {
                    var aim = e.role == EnemyAI.Role.Tower ? e.rig.Head.position + Vector3.up * 0.04f : e.rig.Chest.position;
                    player.AimAt(aim + Random.insideUnitSphere * 0.05f);
                    yield return null;
                    player.PullTrigger(); shots++;
                    yield return new WaitForSeconds(0.35f);
                }
            }
            player.SetLean(0);
        }
    }
}
