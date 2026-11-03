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
    /// Beat 2: Comms (returning fire beside the Engineer) tells Lead the back is clear; Captain and Doc walk the west flank in
    /// low ready, through the cut in the west fence, stack on the back door and go in for the device.
    /// The run starts only when the Engineer presses the right trigger, so the rifle is already on his controller.
    /// </summary>
    public class OpeningDirector : MonoBehaviour
    {
        public EnemyAI enemy1, enemy2, enemy3, enemy4, enemy5;
        public EnemyAI[] insiders;          // Enemy-6..10, guarding the bomb
        public SoldierRig captain, paramedic;
        public SoldierRig comms;
        public Transform sniper1Muzzle, sniper2Muzzle;
        public PlayerRifle player;
        public AudioSource radio;
        public AudioSource alarm;
        public float stretchWindow = 6f, engineerDeadline = 40f;
        public bool autoPlay;               // editor play-test: the rig aims and fires by itself

        string phase = "gate";
        public string Phase
        {
            get => phase;
            private set
            {
                phase = value; OpeningMetrics.Phase = value;
                // the debrief's phase ids (web-app/src/lib/scenario.ts)
                var id = value == "briefing" ? "briefing" : value == "engagement" ? "contact" : value.StartsWith("beat1") ? "escalation" : null;
                if (id != null && id != sentPhase) { sentPhase = id; HeartLink.Event("phase", "system", $"{{\"id\":\"{id}\"}}"); }
            }
        }
        string sentPhase;

        // what each radio clip says (same text as assets_src/tools/gen_audio.py), for the debrief transcript
        static readonly System.Collections.Generic.Dictionary<string, string> RadioText = new System.Collections.Generic.Dictionary<string, string>
        {
            ["r01_captain"] = "Alpha team, this is Alpha Lead. Doc and I are in position at the rear. All call signs, hold fire.",
            ["r02_captain"] = "Engineer, Comms. Your targets: watchtower two, and the door guard on the right. Wait for Sniper One.",
            ["r03_sniper1"] = "Sniper One. Tango, rear left, near the back door. I have the shot.",
            ["r04_captain_mark"] = "Sniper One, on my mark. Three. Two. One. Mark.",
            ["r05_sniper1_down"] = "Sniper One. Tango down.",
            ["r06_captain_engage"] = "Engineer, engage! Engage!",
            ["r07_sniper2_runner"] = "Sniper Two! Runner, front door, left side. He's inside! He's inside!",
            ["r08_comms_tower"] = "Comms. Left tower is down.",
            ["r09_sniper2_down"] = "Sniper Two. Tango down.",
            ["r10_captain_hold"] = "They know we're here. Engineer, Comms, hold that cover. Doc, with me. Moving to the back door.",
            ["r11_comms_backdoor"] = "Alpha Lead, Comms. All six tangos are out front, on us. Back door is clear. Go now. Take the device.",
            ["r12_captain_copy"] = "Copy, Comms. Doc, on me. Moving.",
            ["r13_captain_in"] = "Alpha Lead. We're in. Moving to the device.",
        };

        static string ActorOf(string clip) =>
            clip.Contains("captain") ? "captain" : clip.Contains("sniper1") ? "sniper_1" : clip.Contains("sniper2") ? "sniper_2" :
            clip.Contains("comms") ? "communicator" : clip.Contains("doc") ? "paramedic" : "system";
        bool narrationDone;

        IEnumerator Start()
        {
            OpeningMetrics.Reset();
            foreach (var e in new[] { enemy1, enemy2, enemy3, enemy4, enemy5 }) e.player = player.cam.transform;
            if (comms) { comms.stance = SoldierRig.Stance.Kneel; comms.aiming = true; comms.aimPoint = enemy4.transform.position + Vector3.up * 1.2f; }
            CombatFx.I.Play("wind_loop", player.cam.transform.position, 0.35f, 50f, spatial: false).loop = true;
            if (!autoPlay) yield return StartGate();
            HeartLink.StartMission();
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
            StartCoroutine(Exposure(squad.ToArray(), "beat"));
            HeartLink.Event("enemy_wave", "enemy", $"{{\"count\":{squad.Count}}}");
            Debug.Log("[StealthMode] BEAT 1: " + squad.Count + " enemies out");
            yield return new WaitForSeconds(9f);   // they reach cover and settle into firing
            yield return BeatTwo(squad);
        }

        // ---------------------------------------------------------- beat 2: Captain and Doc go for the back door
        // West flank: out of the cover west of the compound, through the panel cut in the west fence, between Shipment-8
        // and Shipment-3, along the north side past Enemy-5's body, stack on the back door, in.
        static readonly Vector3[] FlankRoute =
        {
            new Vector3(-47.6f, 0, 11.6f), new Vector3(-46f, 0, 12f), new Vector3(-42f, 0, 11f), new Vector3(-34.5f, 0, 11f),
            new Vector3(-34.5f, 0, 18.5f), new Vector3(-28f, 0, 19.5f), new Vector3(-18f, 0, 19.5f), new Vector3(-8f, 0, 13.5f),
            new Vector3(-3.4f, 0, 11.6f), new Vector3(-2f, 0, 11.0f),
        };
        static readonly Vector3[] IntoWarehouse = { new Vector3(-2f, 0.35f, 8.6f), new Vector3(-2.2f, 0.35f, 5.5f), new Vector3(-1.2f, 0.35f, 2.2f) };
        bool captainInside;

        IEnumerator BeatTwo(System.Collections.Generic.List<EnemyAI> squad)
        {
            Phase = "beat2_flank";
            StartCoroutine(CommsSuppress(squad));
            yield return Say("r11_comms_backdoor", comms ? comms.Head : null);
            yield return new WaitForSeconds(0.5f);
            yield return Say("r12_captain_copy");
            if (captain && paramedic)
            {
                // single file, ~3 m apart; Doc moves off as the Captain clears his first stride
                StartCoroutine(Flank(captain, 0f, true));
                StartCoroutine(Flank(paramedic, 2.0f, false));
                while (!captainInside) yield return null;
            }
            yield return Say("r13_captain_in");
            Debug.Log("[StealthMode] BEAT 2: Captain + Doc inside");
            OpeningMetrics.Save();      // snapshot
            // the script ends here for now (beats 3-8 are not built): close the debrief session and let the relay stand down
            yield return new WaitForSeconds(20f);
            HeartLink.EndMission("failed", "script ends after beat 2 (build in progress)");
        }

        IEnumerator Flank(SoldierRig rig, float delay, bool lead)
        {
            yield return new WaitForSeconds(delay);
            rig.aiming = true;
            yield return Walk(rig, FlankRoute, 1.5f);
            // stack on the door: crouch, a breath, then in
            rig.stance = SoldierRig.Stance.Crouch;
            rig.aimPoint = rig.transform.position + new Vector3(0, 1.1f, -4f);
            yield return new WaitForSeconds(lead ? 2.2f : 1.0f);
            yield return Walk(rig, IntoWarehouse, 1.3f, lead ? 0f : 0.9f);
            if (lead) captainInside = true;
            rig.stance = SoldierRig.Stance.Kneel;   // at the device
            rig.aimPoint = rig.transform.position + rig.transform.forward * 0.8f;
        }

        /// <summary>Tactical walk along a path at ground speed, turning like a person (not on the spot), feet on the ground.</summary>
        IEnumerator Walk(SoldierRig rig, Vector3[] path, float speed, float stopShort = 0f)
        {
            rig.walkSpeed = speed;
            rig.stance = SoldierRig.Stance.Patrol;
            var t = rig.transform;
            for (int i = 0; i < path.Length; i++)
            {
                bool last = i == path.Length - 1;
                while (true)
                {
                    var to = path[i] - t.position; to.y = 0;
                    if (to.magnitude < (last ? 0.15f + stopShort : 0.5f)) break;
                    var want = Quaternion.LookRotation(to);
                    t.rotation = Quaternion.RotateTowards(t.rotation, want, 160f * Time.deltaTime);
                    // slow into tight turns instead of moonwalking round them
                    float turn = Quaternion.Angle(t.rotation, want);
                    t.position += t.forward * speed * Mathf.Lerp(1f, 0.35f, Mathf.InverseLerp(20f, 90f, turn)) * Time.deltaTime;
                    Ground(t, path[i].y);
                    yield return null;
                }
            }
        }

        static void Ground(Transform t, float hint)
        {
            var hits = Physics.RaycastAll(t.position + Vector3.up * 1.2f, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider.GetComponent<Hitbox>() || h.collider.transform.IsChildOf(t)) continue;
                if (h.point.y > t.position.y + 0.45f) continue;   // not onto a crate or barrel
                t.position = new Vector3(t.position.x, h.point.y, t.position.z);
                return;
            }
            t.position = new Vector3(t.position.x, hint, t.position.z);
        }

        // Comms doesn't sit still under fire: he picks a muzzle flash, waits for a gap, and puts 2-4 rounds toward it.
        // Suppression, not kills (his kill is beat 4): rounds land around the target, impacts where they really land.
        IEnumerator CommsSuppress(System.Collections.Generic.List<EnemyAI> squad)
        {
            if (!comms) yield break;
            while (Phase.StartsWith("beat"))
            {
                yield return new WaitForSeconds(Random.Range(2.5f, 4.5f));
                if (commsTalking || comms.Dead || !comms.Muzzle) continue;
                EnemyAI target = null;
                foreach (var e in squad) if (!e.Dead && !e.Running && e.rig.Chest && (target == null || Random.value < 0.4f)) target = e;
                if (target == null) continue;
                comms.aimPoint = target.rig.Chest.position;
                yield return new WaitForSeconds(0.55f);
                int n = Random.Range(2, 5);
                for (int i = 0; i < n && !commsTalking; i++)
                {
                    var m = comms.Muzzle;
                    var aim = target.rig.Chest.position + Random.insideUnitSphere * 0.9f + Vector3.up * 0.25f;
                    var dir = (aim - m.position).normalized;
                    var end = m.position + dir * 300f;
                    foreach (var h in Physics.RaycastAll(m.position + dir * 0.4f, dir, 300f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider.transform.IsChildOf(comms.transform) || h.collider.GetComponent<Hitbox>()) continue;
                        if (Vector3.Distance(h.point, m.position) > Vector3.Distance(end, m.position)) continue;
                        end = h.point;
                    }
                    CombatFx.I.MuzzleFlash(m);
                    CombatFx.I.Play("ak_close", m.position, 0.95f, 8f);
                    if (Random.value < 0.4f) CombatFx.I.Tracer(m.position, end);
                    if (end != m.position + dir * 300f) CombatFx.I.Impact(end, -dir, false);
                    yield return new WaitForSeconds(Random.Range(0.11f, 0.16f));
                }
            }
        }
        bool commsTalking;

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
            while (Phase.StartsWith(during))
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

        IEnumerator Say(string clip, Transform speakerNearby = null)
        {
            var c = CombatFx.I.Clip("Radio/" + clip);
            if (c == null) { Debug.LogWarning("[StealthMode] missing radio clip " + clip); yield break; }
            while (radio.isPlaying) yield return null;
            if (speakerNearby)
            {
                // a man two metres away: his own voice in the air a beat before the same words come through the earpiece
                commsTalking = true;
                CombatFx.I.Play("Voice/" + clip, speakerNearby.position, 1f, 1.2f);
                yield return new WaitForSeconds(0.12f);
            }
            radio.clip = c; radio.Play();
            Debug.Log("[StealthMode] radio: " + clip);
            int endMs = Mathf.RoundToInt((Time.time + c.length - HeartLink.MissionStart) * 1000f);
            HeartLink.Event("radio", ActorOf(clip), $"{{\"transcript\":{HeartLink.Str(RadioText.TryGetValue(clip, out var tx) ? tx : clip)},\"end_ms\":{endMs}}}");
            yield return new WaitForSeconds(c.length + 0.35f);
            commsTalking = false;
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
