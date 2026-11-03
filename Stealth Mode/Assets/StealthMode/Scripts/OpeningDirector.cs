using System.Collections;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// Scenario One opening, per Pulin's map and the approved kill plan:
    /// Captain's mark -> Sniper-1 drops Enemy-5 -> Engineer engages Enemy-1 (Watchtower-2) and Enemy-4 (right door guard),
    /// Enemy-2 (left tower) is the stretch target (Comms takes him if the Engineer hasn't in time) ->
    /// Enemy-3 sprints into the warehouse to raise the alarm -> hold. Ends here; the trip-mine phase comes next.
    /// </summary>
    public class OpeningDirector : MonoBehaviour
    {
        public EnemyAI enemy1, enemy2, enemy3, enemy4, enemy5;
        public SoldierRig comms;
        public Transform sniper1Muzzle, sniper2Muzzle;
        public PlayerRifle player;
        public AudioSource radio;
        public AudioSource alarm;
        public float stretchWindow = 6f, engineerDeadline = 40f;
        public bool autoPlay;               // editor play-test: the rig aims and fires by itself

        public string Phase { get; private set; } = "briefing";
        bool narrationDone;

        IEnumerator Start()
        {
            OpeningMetrics.Reset();
            foreach (var e in new[] { enemy1, enemy2, enemy3, enemy4, enemy5 }) e.player = player.cam.transform;
            if (comms) { comms.stance = SoldierRig.Stance.Kneel; comms.aiming = true; comms.aimPoint = enemy4.transform.position + Vector3.up * 1.2f; }
            CombatFx.I.Play("wind_loop", player.cam.transform.position, 0.35f, 50f, spatial: false).loop = true;
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
            StartCoroutine(Exposure());
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

        IEnumerator Exposure()
        {
            // exposed = an armed, living enemy has a clear line of sight to the Engineer's head
            var head = player.cam.transform;
            var shooters = new[] { enemy1, enemy2, enemy4 };
            const float step = 0.1f;
            while (Phase == "engagement")
            {
                foreach (var e in shooters)
                {
                    if (e.Dead || !e.Alerted || !e.rig.Head) continue;
                    var from = e.rig.Head.position; var to = head.position;
                    bool blocked = false;
                    foreach (var h in Physics.RaycastAll(from, (to - from).normalized, Vector3.Distance(from, to) - 0.3f, ~0, QueryTriggerInteraction.Ignore))
                        if (!h.collider.GetComponent<Hitbox>() && !h.collider.transform.IsChildOf(player.transform) && !h.collider.transform.IsChildOf(player.rifle)) { blocked = true; break; }
                    if (!blocked) { OpeningMetrics.ExposedSeconds += step; break; }
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
