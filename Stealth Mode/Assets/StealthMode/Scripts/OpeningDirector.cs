using System.Collections;
using System.Collections.Generic;
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
    /// Beat 3: Sniper-2 takes one more, the storm rolls in, both snipers go blind.
    /// Beat 4: Comms takes the corner of Shipment-1 low, gives the Engineer his man, takes one himself -> three left.
    /// Beat 5: Comms moves up under the Engineer's cover, walks into a trip wire; the mine throws him into the open.
    /// Beat 6: the Engineer reports it out loud and drags Comms behind the container (or dies fighting from the open).
    /// Beat 7: device neutralised; the Captain asks for a sitrep and the Engineer gives it out loud (mac/captain_agent.py).
    /// Beat 8: Captain and Doc come out of the front door, drop the last men, reach Comms; MISSION SUCCESSFUL.
    /// First the briefing film (Briefing.cs) on its own trigger press, then the run starts only when the Engineer presses the
    /// right trigger again, so the rifle is already on his controller.
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
        public bool autoPlayNoRescue;       // editor play-test of the fail: in beat 6 he fights from the open instead
        public bool autoPlaySilent;         // editor play-test: he never says a word on the radio

        string phase = "gate";
        public string Phase
        {
            get => phase;
            private set
            {
                phase = value; OpeningMetrics.Phase = value;
                // the debrief's phase ids (web-app/src/lib/scenario.ts)
                var id = value == "briefing" ? "briefing" : value == "engagement" ? "contact" : value.StartsWith("beat1") ? "escalation"
                       : value.StartsWith("beat3") ? "rain" : value.StartsWith("beat5_casualty") ? "casualty"
                       : value.StartsWith("beat6_holdout") ? "holdout" : value.StartsWith("beat8") ? "extraction" : null;
                if (id != null && id != sentPhase) { sentPhase = id; HeartLink.Event("phase", "system", $"{{\"id\":\"{id}\"}}"); }
            }
        }
        string sentPhase;

        // what each radio clip says (same text as assets_src/tools/gen_audio.py), for the debrief transcript
        static readonly System.Collections.Generic.Dictionary<string, string> RadioText = new System.Collections.Generic.Dictionary<string, string>
        {
            ["r01_captain"] = "Alpha team, this is Alpha Lead. Doc and I are in position at the rear. All call signs, hold fire.",
            ["r02_captain"] = "Engineer, your targets. One: the guard up on the watchtower, straight ahead. Two: the guard at the warehouse door. Lean out past the left edge of your container to shoot. Wait for Sniper One.",
            ["r03_sniper1"] = "Sniper One. Tango, rear left, near the back door. I have the shot.",
            ["r04_captain_mark"] = "Sniper One, on my mark. Three. Two. One. Mark.",
            ["r05_sniper1_down"] = "Sniper One. Tango down.",
            ["r06_captain_engage"] = "Engineer, engage! Lean out left and fire!",
            ["r07_sniper2_runner"] = "Sniper Two! Runner, front door, left side. He's inside! He's inside!",
            ["r08_comms_tower"] = "Comms. Left tower is down.",
            ["r09_sniper2_down"] = "Sniper Two. Tango down.",
            ["r10_captain_hold"] = "They know we're here. Engineer, get back behind your container and hold. Doc, with me. Moving to the back door.",
            ["r10b_comms_stay"] = "Engineer, six coming out! Stay behind the steel. Lean out only to shoot, then get back in.",
            ["r11_comms_backdoor"] = "Alpha Lead, Comms. All six tangos are out front, on us. Back door is clear. Go now. Take the device.",
            ["r12_captain_copy"] = "Copy, Comms. Doc, on me. Moving.",
            ["r13_captain_in"] = "Alpha Lead. We're in. Moving to the device.",
            ["r14_sniper2_down2"] = "Sniper Two. Tango down. Five left out front.",
            ["r15_sniper1_rain"] = "Sniper One. Heavy rain, coming in fast. I've lost my sight picture. I can't see a thing.",
            ["r16_sniper2_blind"] = "Sniper Two. Same here. Can't see past the fence. We're blind. Engineer, Comms, you are on your own up front.",
            ["r17_comms_corner"] = "Engineer, the snipers are blind. It's just us now. Lean out left and fire over my head.",
            ["r18_comms_yours_generator"] = "Engineer, yours is behind the generator. I'll take the left.",
            ["r18_comms_yours_barrels"] = "Engineer, yours is behind the barrels. I'll take the left.",
            ["r18_comms_yours_door"] = "Engineer, yours is in the doorway. I'll take the left.",
            ["r18_comms_yours_crates"] = "Engineer, yours is behind the crates. I'll take the next one.",
            ["r18_comms_yours_tower"] = "Engineer, yours is by the tower. I'll take the left.",
            ["r19_comms_down"] = "Comms. Tango down.",
            ["r19_comms_down4"] = "Comms. Tango down. Four left.",
            ["r19_comms_down3"] = "Comms. Tango down. Three left.",
            ["r20_comms_second"] = "Comms. Got him too. Three left. Engineer, stay on your rifle!",
            ["r21_comms_moving"] = "Engineer, I'm moving up! Cover me! Shoot at them while I run!",
            ["r22_captain_report"] = "Engineer, Alpha Lead! We heard a blast. What happened? Report!",
            ["r23_captain_drag"] = "Copy, Engineer! Get Comms out of the open. Grab him and drag him back behind your container. Move!",
            ["r23b_captain_keepgoing"] = "Copy, Engineer. Keep him moving. Get him behind the steel.",
            ["r24_captain_open"] = "Engineer, you're in the open! Get him into cover, now!",
            ["r25_captain_down"] = "Engineer's down! Engineer's down!",
            ["r26_captain_neutralised"] = "Alpha team, Alpha Lead. Device neutralised. I say again, device neutralised. Engineer, sitrep!",
            ["r27_captain_sayagain"] = "Engineer, say again. You're breaking up.",
            ["r28_captain_status"] = "Engineer, status?",
            ["r30_captain_copy_all"] = "Good copy, Engineer. Hold there and keep Comms down. Doc and I are coming to you.",
            ["r31_captain_ask_cover"] = "Engineer, are you in cover? Confirm your position.",
            ["r32_captain_ask_comms"] = "Engineer, what's Comms' status? Is he with you?",
            ["r33_captain_ask_backup"] = "Copy. Engineer, do you need support?",
            ["r34_captain_coming"] = "Copy, Engineer. Hold where you are. We're coming to you.",
            ["r35_captain_no_contact"] = "Engineer, nothing heard. Stay down. We are coming to you.",
            ["r36_captain_coming_out"] = "Engineer, Alpha Lead. We are coming out of the front door. Watch your fire.",
            ["r37_captain_clear"] = "Alpha Lead. All tangos down. Area secure.",
            ["r38_doc_comms"] = "Doc. I've got Comms. He's alive. Bleeding is under control.",
            ["r39_captain_success"] = "All call signs, Alpha Lead. Device neutralised, area secure, all of Alpha accounted for. Mission successful. Good work, Engineer.",
            ["r40_captain_comms_lost"] = "Engineer, Alpha Lead. Comms is gone. We lost him. Mission failed.",
            ["r41_captain_copy"] = "Copy, Engineer.",
            ["r42_captain_goodcopy"] = "Good copy.",
            ["r43_captain_roger"] = "Roger.",
            ["r44_captain_rogerthat"] = "Roger that, Engineer.",
            ["r45_captain_staysharp"] = "Good. Stay sharp.",
            ["r46_captain_one_down"] = "Copy, one down. Good shot.",
            ["r47_captain_two_down"] = "Copy, two down. Keep it up.",
            ["r48_captain_three_down"] = "Copy, three down. Good work.",
            ["r49_captain_tango_down"] = "Copy, tango down. Stay on them.",
            ["r50_captain_good_kill"] = "Good kill, Engineer.",
        };

        // what the captions add for the person in the headset (the controls), after the words on the radio
        static readonly Dictionary<string, string> CaptionHint = new Dictionary<string, string>
        {
            ["r23_captain_drag"] = "(close your LEFT HAND on any part of him, walk backwards)",
        };
        // Comms' voice in the air (no radio)
        static readonly Dictionary<string, string> AirText = new Dictionary<string, string>
        {
            ["v_comms_goodhit"] = "Good hit.", ["v_comms_moving"] = "Moving!",
            ["v_comms_thanks"] = "You got me. Thanks, brother.",
        };

        static string SpeakerOf(string clip) =>
            clip.Contains("captain") ? "CAPTAIN" : clip.Contains("sniper1") ? "SNIPER 1" : clip.Contains("sniper2") ? "SNIPER 2" :
            clip.Contains("comms") ? "COMMS" : clip.Contains("doc") ? "DOC" : null;

        static string ActorOf(string clip) =>
            clip.Contains("captain") ? "captain" : clip.Contains("sniper1") ? "sniper_1" : clip.Contains("sniper2") ? "sniper_2" :
            clip.Contains("comms") ? "communicator" : clip.Contains("doc") ? "paramedic" : "system";
        bool narrationDone;

        IEnumerator Start()
        {
            OpeningMetrics.Reset();
            foreach (var e in new[] { enemy1, enemy2, enemy3, enemy4, enemy5 }) e.player = player.cam.transform;
            if (comms)
            {
                // he is at the west corner of Shipment-1 from the first frame, in front of the Engineer and in his view:
                // the low man on the corner, nobody appearing from behind his back
                comms.transform.SetPositionAndRotation(CornerSpot, Quaternion.LookRotation(AdvanceDir));
                Ground(comms.transform, 0f);
                comms.stance = SoldierRig.Stance.Kneel; comms.aiming = true; comms.aimPoint = enemy4.transform.position + Vector3.up * 1.2f;
                // his own voice, in the air where he is (one mouth: a new line cuts the last)
                commsVoice = comms.Head.gameObject.AddComponent<AudioSource>();
                commsVoice.spatialBlend = 1; commsVoice.minDistance = 1.2f; commsVoice.maxDistance = 80f; commsVoice.dopplerLevel = 0;
                commsVoice.rolloffMode = AudioRolloffMode.Logarithmic; commsVoice.playOnAwake = false;
                // he is the one man the Engineer works beside: drawn solid, not see-through like the rest of the scene
                foreach (var r in comms.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.materials)
                        if (m.HasProperty("_BaseColor")) { var c = m.GetColor("_BaseColor"); c.a = 1f; m.SetColor("_BaseColor", c); m.SetFloat("_ZWrite", 1f); }
            }
            // the trap has been there all along, on the line Comms will take when he moves up (beat 5)
            mine = TripMine.Place(CornerSpot + AdvanceDir * WireAt, AdvanceDir, player.cam.transform);
            VoiceActivity.Begin(() => radio.isPlaying || RadioStream.Playing || (commsVoice && commsVoice.isPlaying) || Time.time < CombatFx.LoudUntil);
            StartCoroutine(CommsFaces());
            if (player.XrActive || UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var names = new List<string>();
                foreach (var dev in UnityEngine.InputSystem.InputSystem.devices)
                    if (dev is UnityEngine.InputSystem.XR.XRController) names.Add($"{dev.name} [{string.Join(",", dev.usages)}]");
                Debug.Log("[StealthMode] XR controllers: " + string.Join("; ", names));
            }
            if (!autoPlay)
            {
                // script step 1: the briefing film first, on its own trigger press; then the simulation's own start
                player.armed = false;
                yield return Prompt("Press RIGHT TRIGGER\non your Quest controller\nto start the briefing", "Click or press Space\nto start the briefing");
                // the start prompt and the wind come up with the compound as the film fades out
                bool worldBack = false;
                StartCoroutine(Briefing.Play(player.cam, () => worldBack = true));
                while (!worldBack) yield return null;
            }
            CombatFx.I.Play("wind_loop", player.cam.transform.position, 0.35f, 50f, spatial: false).loop = true;
            if (!autoPlay) yield return StartGate();
            if (!autoPlay) HeartLink.StartMission();     // an editor play-test opens no debrief run
            VoiceActivity.Listen = "free";               // from here on every word he says goes on the record
            StartCoroutine(OnTheNet());
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
        // Used twice: before the briefing film and before the simulation.
        IEnumerator StartGate()
        {
            player.armed = false;
            yield return Prompt("Press RIGHT TRIGGER\non your Quest controller\nto start the simulation", "Click or press Space\nto start the simulation");
            OpeningMetrics.StartPressTime = Time.time;
            yield return null;          // the press that started the run is not a shot
            player.armed = true;
        }

        IEnumerator Prompt(string xrMsg, string desktopMsg)
        {
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
            var msg = player.XrActive ? xrMsg : desktopMsg;
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
            StartCoroutine(SayAfter(2.5f, "r10b_comms_stay", comms ? comms.Head : null));
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
            // Shipment-1 is between Comms and the yard where he kneels: he runs to its west corner and fights from there
            if (comms && !comms.Dead && Vector3.Distance(comms.transform.position, CornerSpot) > 0.6f) yield return CommsToCorner();
            StartSuppress(squad);
            yield return Say("r11_comms_backdoor", comms ? comms.Head : null);
            yield return new WaitForSeconds(0.5f);
            yield return Say("r12_captain_copy");
            StartCoroutine(CommsFirstKill(squad));
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
            yield return BeatThree(squad);
        }

        // ---------------------------------------------------------- beat 3: one more down, then the storm blinds the snipers
        // Sniper-2 (south-east corner) takes the eastern-most of the men out front. Then the storm rolls in: thunder, rain
        // building over ~8 s, light going flat, fog. Both snipers lose their sight picture and say so; from here on they are
        // out of the fight and the front belongs to the Engineer and Comms.
        public bool SnipersBlind { get; private set; }

        IEnumerator BeatThree(System.Collections.Generic.List<EnemyAI> squad)
        {
            yield return new WaitForSeconds(2f);
            Phase = "beat3_rain";
            EnemyAI target = null;
            foreach (var e in squad)
                if (!e.Dead && !e.Running && (target == null || e.transform.position.x > target.transform.position.x)) target = e;
            if (target)
            {
                CombatFx.I.Play("sniper_distant", sniper2Muzzle.position, 1f, 60f);
                yield return new WaitForSeconds(0.18f);   // the round arrives a beat after the sound leaves the rifle
                if (!target.Dead)
                {
                    target.Kill(target.rig.Chest.position, (target.rig.Chest.position - sniper2Muzzle.position).normalized, "Sniper-2", 260f);
                    OpeningMetrics.RecordKill(target.id, "Sniper-2");
                }
                yield return new WaitForSeconds(0.8f);
                int left = 0; foreach (var e in squad) if (!e.Dead) left++;
                yield return Say(left == 5 ? "r14_sniper2_down2" : "r09_sniper2_down");
            }

            OpeningMetrics.RainTime = Time.time;
            HeartLink.Event("rain_start", "system", "{}");
            Weather.Begin(player.cam.transform, GameObject.Find("Shipment-1 (cover)")?.transform);
            Debug.Log("[StealthMode] BEAT 3: storm rolling in");
            yield return new WaitForSeconds(6.5f);         // rain is heavy before they give up the glass
            yield return Say("r15_sniper1_rain");
            yield return Say("r16_sniper2_blind");
            SnipersBlind = true;
            OpeningMetrics.Save();
            yield return BeatFour(squad);
        }

        // ---------------------------------------------------------- beat 4: the Engineer and Comms each take one
        // Comms can't shoot from where he kneels (Shipment-1 is in the way), so with the snipers gone he moves to the west
        // corner and kneels there: the low man; the Engineer fires high, over him. He gives the Engineer the nearest man
        // that can be seen from that corner and takes the west-most himself. The Engineer has ~25 s from that call to
        // drop anyone; if he hasn't, Comms takes a second and tells him to get on his rifle. Whether the Engineer calls
        // his kill out loud is only logged (headset mic: any voice from him within 6 s of the kill).
        // clear of the end of Shipment-1 (its west face is x = 21.9): body and rifle in open air, every firing position in view
        static readonly Vector3 CornerSpot = new Vector3(21.1f, 0, -22.45f);
        static readonly Vector3[] CornerRoute = { new Vector3(25.3f, 0, -24.15f), new Vector3(23.3f, 0, -24.2f), new Vector3(21.3f, 0, -23.7f), CornerSpot };
        static readonly Vector3 CornerEye = new Vector3(21.6f, 1.35f, -23.1f);     // where the Engineer shoots from, round the corner
        static readonly string[] PositionLabel = { "door", "barrels", "door", "generator", "crates", "tower" };   // FiringPositions
        public float engineerWindow = 25f;
        EnemyAI designated, engineerKill;
        AudioSource commsVoice;
        bool commsBusy;

        IEnumerator BeatFour(List<EnemyAI> squad)
        {
            yield return new WaitForSeconds(2f);
            Phase = "beat4_corner";
            OpeningMetrics.Beat4Time = Time.time;
            OpeningMetrics.MicAvailable = VoiceActivity.Available;
            StopSuppress();
            if (comms && !comms.Dead)
            {
                if (Vector3.Distance(comms.transform.position, CornerSpot) > 0.6f) yield return CommsToCorner();
                yield return Say("r17_comms_corner", comms.Head);
            }
            Phase = "beat4_engage";
            Debug.Log("[StealthMode] BEAT 4: " + Alive(squad) + " left, Comms at the corner");
            if (Alive(squad) > 3 && comms && !comms.Dead)
            {
                designated = Designate(squad);
                var mine4 = CommsTarget(squad, designated);
                OpeningMetrics.Designated = designated ? designated.id : null;
                if (designated) yield return Say("r18_comms_yours_" + Label(designated), comms.Head);
                OpeningMetrics.DesignateTime = Time.time;
                float t0 = Time.time;
                StartSuppress(squad);                                   // from the corner his rounds land among them
                StartCoroutine(EngineerKillWatch(squad, OpeningMetrics.Beat4Time));
                yield return new WaitForSeconds(Random.Range(0.5f, 1.0f));
                if (Alive(squad) > 3 && mine4 && !mine4.Dead) yield return CommsTakes(mine4, squad);
                while (Time.time - t0 < engineerWindow && engineerKill == null && Alive(squad) > 3) yield return null;
                OpeningMetrics.CommsSecond = engineerKill == null && Alive(squad) > 3;
                bool first = true;
                while (Alive(squad) > 3 && !comms.Dead)
                {
                    var e = designated && !designated.Dead ? designated : CommsTarget(squad, null);
                    yield return CommsTakes(e, squad, first && engineerKill == null ? "r20_comms_second" : null);
                    first = false;
                }
            }
            else if (Alive(squad) >= 2 && comms && !comms.Dead)
            {
                // the Engineer has been quick: Comms still does his share and says so; someone is left for the blast
                StartSuppress(squad);
                yield return CommsTakes(CommsTarget(squad, null), squad);
            }
            // let the Engineer's call and Comms' answer to it play out before the next thing happens
            while (commsBusy || commsTalking || (engineerKill && Time.time < engineerKill.DeathTime + 4.5f) || (commsVoice && commsVoice.isPlaying)) yield return null;
            OpeningMetrics.Save();
            yield return BeatFive(squad);
        }

        // kneeling, he turns his whole body onto whoever he is aiming at: the arms never have to twist round to reach
        IEnumerator CommsFaces()
        {
            while (comms && !comms.Dead)
            {
                if (comms.stance == SoldierRig.Stance.Kneel)
                {
                    var to = comms.aimPoint - comms.transform.position; to.y = 0;
                    if (to.sqrMagnitude > 0.25f)
                        comms.transform.rotation = Quaternion.RotateTowards(comms.transform.rotation, Quaternion.LookRotation(to), 220f * Time.deltaTime);
                }
                yield return null;
            }
        }

        // while the Captain is moving, Comms puts one down himself and calls it
        IEnumerator CommsFirstKill(List<EnemyAI> squad)
        {
            yield return new WaitForSeconds(Random.Range(4f, 7f));
            if (comms && !comms.Dead && Alive(squad) >= 2) yield return CommsTakes(CommsTarget(squad, null), squad);
        }

        // where a round from this muzzle reaches him: chest, or the head over his cover; false if neither is in the open
        bool OpenShot(EnemyAI e, out Vector3 at)
        {
            var from = comms.Muzzle ? comms.Muzzle.position : comms.Head.position;
            at = e.rig.Chest.position;
            if (LineClear(from, at)) return true;
            at = e.rig.Head.position;
            return LineClear(from, at);
        }

        // the nearest man he can really hit from where he is
        EnemyAI CommsTarget(List<EnemyAI> squad, EnemyAI not)
        {
            EnemyAI best = null; float bd = float.MaxValue;
            foreach (var e in squad)
            {
                if (e.Dead || e.Running || e == not || !e.rig.Chest || !OpenShot(e, out _)) continue;
                float d = Vector3.Distance(comms.transform.position, e.transform.position);
                if (d < bd) { bd = d; best = e; }
            }
            return best ? best : WestMost(squad, not);
        }

        // "Moving!" and he goes: up off the knee, a hard run behind the Engineer to the west corner, down on the knee there,
        // rifle on the yard
        IEnumerator CommsToCorner()
        {
            StartCoroutine(Shout("v_comms_moving"));
            yield return new WaitForSeconds(0.3f);
            yield return RunTo(comms, CornerRoute, 2.6f, SoldierRig.Stance.Rush, () => comms.aimPoint);
            comms.stance = SoldierRig.Stance.Kneel;
            comms.aimPoint = CornerSpot + AdvanceDir * 20f + Vector3.up * 1.2f;
            OpeningMetrics.CommsCornerTime = Time.time;
            yield return new WaitForSeconds(0.3f);
        }

        static Vector3 NearestThreat(List<EnemyAI> squad, Vector3 from)
        {
            EnemyAI best = null; float bd = float.MaxValue;
            foreach (var e in squad)
                if (!e.Dead && e.rig.Chest && Vector3.Distance(from, e.rig.Chest.position) < bd) { bd = Vector3.Distance(from, e.rig.Chest.position); best = e; }
            return best ? best.rig.Chest.position : from + AdvanceDir * 20f + Vector3.up * 1.2f;
        }

        static int Alive(List<EnemyAI> squad) { int n = 0; foreach (var e in squad) if (!e.Dead) n++; return n; }

        static string Label(EnemyAI e)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < FiringPositions.Length; i++)
            {
                float d = Vector3.Distance(e.transform.position, FiringPositions[i].pos);
                if (d < bd) { bd = d; best = i; }
            }
            return PositionLabel[best];
        }

        /// <summary>Nothing but air between two points (bodies' hit boxes, the Engineer and Comms don't count).</summary>
        bool LineClear(Vector3 a, Vector3 b)
        {
            foreach (var h in Physics.RaycastAll(a, (b - a).normalized, Vector3.Distance(a, b), ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider.GetComponent<Hitbox>() || h.collider.transform.IsChildOf(player.transform)) continue;
                if (comms && h.collider.transform.IsChildOf(comms.transform)) continue;
                if (h.collider.name == "Blast debris") continue;
                return false;
            }
            return true;
        }

        // the man for the Engineer: the nearest one he can actually see from the corner
        EnemyAI Designate(List<EnemyAI> squad)
        {
            EnemyAI best = null, any = null; float bd = float.MaxValue, ad = float.MaxValue;
            foreach (var e in squad)
            {
                if (e.Dead || e.Running || !e.rig.Chest) continue;
                float d = Vector3.Distance(CornerEye, e.rig.Chest.position);
                if (d < ad) { ad = d; any = e; }
                if (d < bd && LineClear(CornerEye, e.rig.Chest.position)) { bd = d; best = e; }
            }
            return best ? best : any;
        }

        static EnemyAI WestMost(List<EnemyAI> squad, EnemyAI not)
        {
            EnemyAI best = null;
            foreach (var e in squad)
                if (!e.Dead && !e.Running && e != not && (best == null || e.transform.position.x < best.transform.position.x)) best = e;
            return best;
        }

        // one aimed round from a teammate's rifle; it lands wherever it really goes
        void Round(SoldierRig who, Vector3 aim)
        {
            var m = who.Muzzle; if (!m) return;
            var dir = (aim - m.position).normalized;
            var end = m.position + dir * 300f; float best = 300f;
            foreach (var h in Physics.RaycastAll(m.position + dir * 0.4f, dir, 300f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(who.transform)) continue;
                if (h.distance < best) { best = h.distance; end = h.point; }
            }
            CombatFx.I.MuzzleFlash(m);
            CombatFx.I.Play("ak_close", m.position, 0.95f, 8f);
            if (Random.value < 0.4f) CombatFx.I.Tracer(m.position, end);
            if (best < 300f) CombatFx.I.Impact(end, -dir, false);
        }

        // Comms kills a man: sights on, a controlled pair, done; then he says so on the radio (and in the air, a few
        // metres from the Engineer)
        IEnumerator CommsTakes(EnemyAI e, List<EnemyAI> squad, string line = null)
        {
            if (!comms || comms.Dead || e == null || e.Dead) yield break;
            commsBusy = true;
            OpenShot(e, out var at);
            comms.aimPoint = at;
            yield return new WaitForSeconds(Random.Range(0.5f, 0.75f));     // turning onto him, sights settling
            bool got = false;
            if (!e.Dead)
            {
                OpenShot(e, out at);
                comms.aimPoint = at;
                for (int i = 0; i < 2; i++) { Round(comms, at); yield return new WaitForSeconds(0.13f); }
                if (!e.Dead)
                {
                    e.Kill(e.rig.Chest.position, (e.rig.Chest.position - comms.Muzzle.position).normalized, "Comms");
                    OpeningMetrics.RecordKill(e.id, "Comms");
                    got = true;
                }
            }
            commsBusy = false;
            if (!got) yield break;
            yield return new WaitForSeconds(0.6f);
            int left = Alive(squad);
            yield return Say(line ?? (left == 4 ? "r19_comms_down4" : left == 3 ? "r19_comms_down3" : "r19_comms_down"), comms.Head);
        }

        // the Engineer's kill in this beat (any man), and whether he called it out
        IEnumerator EngineerKillWatch(List<EnemyAI> squad, float since)
        {
            while (engineerKill == null && Phase.StartsWith("beat4"))
            {
                foreach (var e in squad) if (e.Dead && e.Killer == "Engineer" && e.DeathTime >= since) { engineerKill = e; break; }
                yield return null;
            }
            if (engineerKill == null) yield break;
            float tk = engineerKill.DeathTime, called = -1;
            OpeningMetrics.EngineerKillTime = tk; OpeningMetrics.EngineerKillTarget = engineerKill.id;
            Debug.Log("[StealthMode] BEAT 4: Engineer dropped " + engineerKill.id);
            void Heard()
            {
                if (called >= 0 || VoiceActivity.LastOnset < tk) return;
                called = VoiceActivity.LastOnset;
                OpeningMetrics.KillCallLatency = called - tk;      // his words reach the debrief through the relay
            }
            // Comms answers once the Engineer has finished his call, or after a couple of seconds of silence
            while (Time.time - tk < 6f)
            {
                Heard();
                if (called >= 0 ? !VoiceActivity.Speaking : Time.time - tk > 2.5f) break;
                yield return null;
            }
            // if he called it himself the Captain answers him on the radio
            if (called < 0 && !commsTalking && !commsBusy && !radio.isPlaying && comms && !comms.Dead) yield return Shout("v_comms_goodhit");
            while (called < 0 && Time.time - tk < 6f) { Heard(); yield return null; }
        }

        IEnumerator Shout(string clip)
        {
            var c = CombatFx.I.Clip("Voice/" + clip);
            if (!c || !commsVoice) yield break;
            commsVoice.Stop(); commsVoice.clip = c; commsVoice.Play();
            if (AirText.TryGetValue(clip, out var said) && !radio.isPlaying) Caption.Show(player.cam.transform, "COMMS", said, c.length + 0.5f);
            yield return new WaitForSeconds(c.length);
        }

        // ---------------------------------------------------------- beat 5: Comms moves up and walks into the wire
        // "Cover me!" and he goes, out from the corner toward the yard, low ready, the enemy switching their fire onto him.
        // 2.8 m out his shin takes the wire: the mine fires and throws him back into the open, ~1.5 m from the Engineer
        // (a spot the three can see, so getting him means stepping out). Comms' radio dies with him on it; from now on he
        // is only a voice in the air: short broken calls for help.
        static readonly Vector3 AdvanceDir = new Vector3(-0.834f, 0, 0.551f);
        const float WireAt = 2.8f, AdvanceLength = 4.2f;
        TripMine mine;

        IEnumerator BeatFive(List<EnemyAI> squad)
        {
            yield return new WaitForSeconds(2.5f);
            Phase = "beat5_advance";
            OpeningMetrics.Beat5Time = Time.time;
            if (!comms || comms.Dead || !mine) { yield return Finish(false, "Comms was lost before he could move up."); yield break; }
            StartSuppress(squad);
            HeartLink.Event("cover_request", "communicator", "{}");
            int shots0 = OpeningMetrics.Shots.Count; float advance0 = Time.time;
            yield return Say("r21_comms_moving", comms.Head);
            StopSuppress();
            // "Cover me!" - he goes when the Engineer's covering fire starts (heads down out there), or after ~3 s anyway
            for (float w = Time.time; Time.time - w < 3f && OpeningMetrics.Shots.Count == shots0;) yield return null;
            StartCoroutine(Shout("v_comms_moving"));
            yield return new WaitForSeconds(0.25f);
            foreach (var e in squad) if (!e.Dead) e.player = comms.Head;   // he's the one in the open now: they shift onto him
            var from = comms.transform.position;
            // a low bound, sights on the nearest man the whole way, not an upright sprint
            var walking = StartCoroutine(RunTo(comms, new[] { from + AdvanceDir * AdvanceLength }, 2.7f, SoldierRig.Stance.Rush,
                () => NearestThreat(squad, comms.transform.position)));
            var prev = from;
            while (true)
            {
                yield return null;
                var now = comms.transform.position;
                if (mine.Crossed(prev, now) || Vector3.Distance(now, from) > AdvanceLength + 0.3f) break;
                prev = now;
            }
            StopCoroutine(walking);

            for (int i = shots0; i < OpeningMetrics.Shots.Count; i++) { OpeningMetrics.CoverShots++; if (OpeningMetrics.Shots[i].target != null) OpeningMetrics.CoverHits++; }
            OpeningMetrics.VoiceDuringAdvance = VoiceActivity.LastOnset >= advance0;
            Phase = "beat5_casualty";
            OpeningMetrics.TripTime = Time.time;
            var landing = Landing(squad);
            HeartLink.Event("trip_mine", "communicator", $"{{\"cover_shots\":{OpeningMetrics.CoverShots},\"cover_hits\":{OpeningMetrics.CoverHits}}}");
            foreach (var e in squad) if (!e.Dead) e.player = player.cam.transform;
            Debug.Log("[StealthMode] BEAT 5: wire tripped, landing " + landing);
            float blast = Time.time;
            yield return mine.Detonate(comms, landing, player.cam, radio);
            wounded = StartCoroutine(Wounded());
            yield return BeatSix(squad, blast);
        }

        // ---------------------------------------------------------- beat 6: man down in the open
        // Three things are measured from the blast: how soon the Engineer says it on the radio (out loud: the headset mic),
        // how soon he gets Comms out of the open (bare left hand closed on any part of him, walking backwards behind the
        // container), and whether he fights from the open instead. Comms is safe when his whole body is behind the steel.
        // Captain asks for a report if he hasn't spoken in ~9 s, and tells him plainly what to do once he has.
        // FAIL: 10 s in the open (cumulative) without holding Comms, 3 shots fired from the open without holding him,
        // or Comms bleeds out: never touched in 90 s, or not in cover 150 s after the blast.
        Rescue rescue;
        Coroutine wounded;
        bool beat6Over;
        public float openLimit = 10f, openWarn = 5f;
        public int openShotLimit = 3;
        public float bleedOut = 90f, bleedOutHeld = 150f;

        IEnumerator BeatSix(List<EnemyAI> squad, float blast)
        {
            Phase = "beat6_rescue";
            StartCoroutine(MeasureLanding());
            OpeningMetrics.Beat6Time = blast;
            rescue = Rescue.Begin(comms, player.cam);
            if (autoPlay) rescue.simulate = true;
            StartCoroutine(ReportWatch(blast));

            float open = 0, hiddenFor = 0; int shotsSeen = OpeningMetrics.Shots.Count; bool warned = false;
            while (true)
            {
                yield return null;
                bool holding = rescue.Holding, exposed = Exposed(squad);
                bool counting = Time.time - blast > 3f;                 // ears ringing, dust in the air: the clock starts after
                if (exposed && !holding && counting) open += Time.deltaTime;
                if (exposed) OpeningMetrics.Beat6ExposedSeconds += Time.deltaTime;
                for (; shotsSeen < OpeningMetrics.Shots.Count; shotsSeen++)
                    if (exposed && !holding) OpeningMetrics.OpenShots++;
                OpeningMetrics.OpenSeconds = open;
                if (!warned && open > openWarn) { warned = true; StartCoroutine(Say("r24_captain_open")); }
                if (open >= openLimit || OpeningMetrics.OpenShots >= openShotLimit)
                {
                    yield return Fail(OpeningMetrics.OpenShots >= openShotLimit ? "You fought from the open instead of getting Comms to cover." : "You stayed in the open too long without Comms.");
                    yield break;
                }
                // safe: he has been pulled there and every part of him is behind the steel
                hiddenFor = rescue.EverGrabbed && CommsBehindSteel() ? hiddenFor + Time.deltaTime : 0;
                if (hiddenFor > 1f) break;
                if ((!rescue.EverGrabbed && Time.time - blast > bleedOut) || Time.time - blast > bleedOutHeld) { yield return CommsLost(); yield break; }
            }

            // he made it: Comms is behind the steel
            beat6Over = true;
            OpeningMetrics.SafeTime = Time.time; OpeningMetrics.FirstGrabTime = rescue.FirstGrabTime;
            OpeningMetrics.Grabs = rescue.Grabs; OpeningMetrics.DragMetres = rescue.Moved;
            rescue.Drop(true);
            rescue.Hide();
            if (wounded != null) StopCoroutine(wounded);
            CombatFx.I.Play("ui_cover_ok", player.cam.transform.position, 0.8f, 5f, spatial: false);
            Caption.Big(player.cam.transform, "<color=#45D980>COVER SUCCESSFUL</color>", 3f);
            Phase = "beat6_holdout";
            int shotsAtCover = OpeningMetrics.Shots.Count;
            Debug.Log($"[StealthMode] BEAT 6: Comms in cover {Time.time - blast:0.0} s after the blast (first grab {rescue.FirstGrabTime - blast:0.0} s, {rescue.Grabs} grab(s), {rescue.Moved:0.00} m)");
            yield return new WaitForSeconds(1.2f);
            if (!radio.isPlaying) yield return Shout("v_comms_thanks");
            yield return BeatSeven(squad, shotsAtCover);
        }

        // Comms bled out where he lay: the Captain says it, the mission is over
        IEnumerator CommsLost()
        {
            beat6Over = true;
            rescue.Drop();
            if (wounded != null) StopCoroutine(wounded);
            commsVoice.Stop();
            Phase = "beat6_failed";
            Debug.Log("[StealthMode] BEAT 6: Comms bled out");
            yield return new WaitForSeconds(2f);
            yield return Say("r40_captain_comms_lost");
            yield return Finish(false, "Comms bled out. He never reached cover.");
        }

        // ---------------------------------------------------------- beat 7: device neutralised, sitrep
        // Both of them are behind the steel; the three are still shooting. A few seconds on, the Captain calls the device
        // neutralised and asks for a sitrep. The Engineer answers out loud: in cover, Comms with him, needs backup. The
        // Captain (mac/captain_agent.py) asks for what is missing, at most twice. Silent 10 s: "Engineer, status?" once,
        // logged as prompted. The mission never hangs on this: no answer, and they come anyway.
        IEnumerator BeatSeven(List<EnemyAI> squad, int shotsAtCover)
        {
            Phase = "beat7_sitrep";
            while (Time.time - OpeningMetrics.SafeTime < 7f || radio.isPlaying) yield return null;
            // (d) did he keep shooting once both of them were behind cover? logged only
            OpeningMetrics.ShotsAfterCover = OpeningMetrics.Shots.Count - shotsAtCover;
            OpeningMetrics.BombTime = Time.time;
            HeartLink.Event("bomb_defused", "captain", "{}");
            Debug.Log("[StealthMode] BEAT 7: device neutralised");
            yield return Say("r26_captain_neutralised");

            float asked = Time.time, called = -1; HeartLink.AgentReply said = null;
            System.Func<bool> never = () => false;
            yield return Hear("sitrep", asked, 10f, never, t => called = t, r => said = r);
            if (called < 0)
            {
                OpeningMetrics.SitrepPrompted = true;
                yield return Say("r28_captain_status");
                asked = Time.time;
                yield return Hear("sitrep", asked, 10f, never, t => called = t, r => said = r);
            }
            string closing = called >= 0 ? null : "r35_captain_no_contact";
            if (called >= 0)
            {
                OpeningMetrics.SitrepLatency = called - asked;
                for (int round = 0; ; round++)
                {
                    LogEngineer(said, "(gave a sitrep: voice heard, words not checked)", called, OpeningMetrics.SitrepPrompted);
                    if (said != null)
                    {
                        OpeningMetrics.SitrepCover |= said.cover == 1; OpeningMetrics.SitrepComms |= said.comms == 1; OpeningMetrics.SitrepBackup |= said.backup == 1;
                        OpeningMetrics.SitrepText = (OpeningMetrics.SitrepText == null ? "" : OpeningMetrics.SitrepText + " / ") + said.heard;
                    }
                    if (said == null || string.IsNullOrEmpty(said.clip)) { closing = "r34_captain_coming"; break; }   // the Mac didn't answer: he was still heard
                    yield return Speak(said);
                    if (said.done == 1) break;
                    if (round >= 2) { closing = "r34_captain_coming"; break; }
                    OpeningMetrics.SitrepFollowUps++;
                    float again = -1; HeartLink.AgentReply next = null;
                    yield return Hear("sitrep", Time.time, 8f, never, t => again = t, r => next = r);
                    if (again < 0) { closing = "r34_captain_coming"; break; }
                    called = again; said = next;
                }
            }
            Debug.Log($"[StealthMode] BEAT 7: sitrep cover={OpeningMetrics.SitrepCover} comms={OpeningMetrics.SitrepComms} backup={OpeningMetrics.SitrepBackup} prompted={OpeningMetrics.SitrepPrompted}: {OpeningMetrics.SitrepText}");
            if (closing != null) yield return Say(closing);
            OpeningMetrics.Save();
            yield return BeatEight(squad);
        }

        // ---------------------------------------------------------- beat 8: Captain and Doc come out
        // Out of the device room to the front door, shooting whoever is left from each stop on the way, then a run to the
        // Engineer: Doc goes down at Comms, the Captain closes it out.
        static readonly Vector3[] SweepStops = { new Vector3(-2.2f, 0.35f, -5f), new Vector3(-2f, 0.35f, -8.2f), new Vector3(-2f, 0f, -12.6f) };
        static readonly Vector3[] ToEngineer = { new Vector3(10f, 0, -17f), new Vector3(20.5f, 0, -25.5f) };

        IEnumerator BeatEight(List<EnemyAI> squad)
        {
            Phase = "beat8_sweep";
            OpeningMetrics.SweepTime = Time.time;
            yield return Say("r36_captain_coming_out");
            int turn = 0;
            foreach (var stop in SweepStops)
            {
                if (Alive(squad) == 0) break;
                StartCoroutine(Walk(paramedic, new[] { stop + Vector3.right * 1.3f }, 2.2f));
                yield return Walk(captain, new[] { stop }, 2.2f);
                captain.stance = paramedic.stance = SoldierRig.Stance.Crouch;
                captain.aiming = paramedic.aiming = true;
                foreach (var e in squad) if (!e.Dead) e.player = captain.Head;   // the fire swings onto the door
                for (var e = InSight(squad); e != null; e = InSight(squad))
                    yield return TeamTakes(turn++ % 2 == 0 ? captain : paramedic, e);
            }
            // anyone still out of sight behind cover: go and find him
            while (Alive(squad) > 0)
            {
                EnemyAI e = null;
                foreach (var c in squad)
                    if (!c.Dead && (e == null || Vector3.Distance(c.transform.position, captain.transform.position) < Vector3.Distance(e.transform.position, captain.transform.position))) e = c;
                captain.aiming = true;
                var go = StartCoroutine(RunTo(captain, new[] { e.transform.position }, 3f, SoldierRig.Stance.Rush, () => e.rig.Chest.position));
                for (float t = Time.time; Time.time - t < 8f && !e.Dead && !LineClear(captain.Head.position, e.rig.Chest.position);) yield return null;
                StopCoroutine(go);
                yield return TeamTakes(captain, e);
            }
            OpeningMetrics.ClearTime = Time.time;
            Debug.Log("[StealthMode] BEAT 8: all tangos down");
            yield return Say("r37_captain_clear");

            // Doc to Comms, the Captain to the Engineer
            var casualty = comms.Chest.position; casualty.y = 0;
            var side = (ToEngineer[1] - casualty); side.y = 0;
            var docSpot = casualty + side.normalized * 0.75f;
            var eye = player.cam.transform.position; eye.y = 0;
            var captainSpot = eye + (ToEngineer[1] - eye).normalized * 2f;
            bool docThere = false;
            IEnumerator DocGoes()
            {
                yield return RunTo(paramedic, new[] { ToEngineer[0], ToEngineer[1], docSpot }, 3.6f);
                paramedic.stance = SoldierRig.Stance.Kneel; paramedic.aiming = false;
                docThere = true;
            }
            StartCoroutine(DocGoes());
            yield return new WaitForSeconds(0.6f);
            yield return RunTo(captain, new[] { ToEngineer[0], ToEngineer[1], captainSpot }, 3.4f);
            captain.stance = SoldierRig.Stance.Stand; captain.aiming = false;
            var look = player.cam.transform.position - captain.transform.position; look.y = 0;
            captain.transform.rotation = Quaternion.LookRotation(look);
            while (!docThere) yield return null;
            yield return new WaitForSeconds(0.8f);
            yield return Say("r38_doc_comms", paramedic.Head);
            yield return Say("r39_captain_success", captain.Head);
            yield return new WaitForSeconds(1.2f);
            yield return Finish(true, "Device neutralised. Area secure. Comms is alive.");
        }

        EnemyAI InSight(List<EnemyAI> squad)
        {
            EnemyAI best = null; float bd = float.MaxValue;
            foreach (var e in squad)
            {
                if (e.Dead || !e.rig.Chest) continue;
                float d = Vector3.Distance(captain.Head.position, e.rig.Chest.position);
                if (d < bd && LineClear(captain.Head.position, e.rig.Chest.position)) { bd = d; best = e; }
            }
            return best;
        }

        // sights on, three rounds, and he is down
        IEnumerator TeamTakes(SoldierRig who, EnemyAI e)
        {
            who.aiming = true; who.aimPoint = e.rig.Chest.position;
            yield return new WaitForSeconds(Random.Range(0.5f, 0.8f));
            if (e.Dead) yield break;
            for (int i = 0; i < 3; i++) { Round(who, e.rig.Chest.position); yield return new WaitForSeconds(0.12f); }
            if (e.Dead) yield break;
            var by = who == captain ? "Captain" : "Paramedic";
            e.Kill(e.rig.Chest.position, (e.rig.Chest.position - who.Head.position).normalized, by);
            OpeningMetrics.RecordKill(e.id, by);
            yield return new WaitForSeconds(0.5f);
        }

        // ---------------------------------------------------------- the end
        // The one way out of the mission, success or failure: metrics saved, debrief closed, the end screen.
        bool finished;
        IEnumerator Finish(bool success, string reason, MeshRenderer veil = null)
        {
            if (finished) yield break;
            finished = true;
            player.armed = false;
            VoiceActivity.Listen = null;
            StopSuppress();
            foreach (var e in FindObjectsByType<EnemyAI>(FindObjectsSortMode.None)) e.canFire = false;
            OpeningMetrics.Success = success; OpeningMetrics.EndReason = reason; OpeningMetrics.FinishTime = Time.time;
            OpeningMetrics.FailReason = success ? null : reason;
            OpeningMetrics.Save();
            HeartLink.EndMission(success ? "success" : "failed", reason);
            Debug.Log($"[StealthMode] MISSION {(success ? "SUCCESSFUL" : "FAILED")}: {reason}");
            yield return MissionEnd.Show(player.cam, success, reason, veil);
        }

        // every part of Comms is on the far side of Shipment-1 from the enemy, along its length
        bool CommsBehindSteel()
        {
            var steel = SteelBounds();
            foreach (var rb in comms.GetComponentsInChildren<Rigidbody>())
            {
                var p = rb.worldCenterOfMass;
                if (p.z > steel.min.z || p.x < steel.min.x + 0.2f || p.x > steel.max.x) return false;
            }
            return true;
        }

        static Bounds SteelBounds()
        {
            var cols = GameObject.Find("Shipment-1 (cover)").GetComponentsInChildren<Collider>();
            var b = cols[0].bounds;
            foreach (var c in cols) b.Encapsulate(c.bounds);
            return b;
        }

        IEnumerator MeasureLanding()
        {
            yield return new WaitForSeconds(2.5f);          // once he has come down and stopped
            var c = comms.Chest.position; var h = player.cam.transform.position;
            OpeningMetrics.LandingDistance = Vector2.Distance(new Vector2(c.x, c.z), new Vector2(h.x, h.z));
            Debug.Log($"[StealthMode] BEAT 5: Comms down {OpeningMetrics.LandingDistance:0.00} m from the Engineer");
        }

        // The Engineer talking to the Captain: wait for his voice, stream it to the Mac while he speaks, wait for the
        // Captain's decision (mac/captain_agent.py). done(null) when the Mac doesn't answer: he was still heard, the words
        // just weren't checked. Nothing is called when he stays silent for `silence` seconds.
        IEnumerator Hear(string ctx, float since, float silence, System.Func<bool> stop, System.Action<float> voice, System.Action<HeartLink.AgentReply> done)
        {
            while (HeartLink.TakeReply(ctx) != null) { }    // nothing left over from an earlier exchange
            if (autoPlay && !autoPlaySilent)
            {
                yield return new WaitForSeconds(2f);
                if (stop()) yield break;
                voice(Time.time - 1.5f);
                done(new HeartLink.AgentReply
                {
                    ctx = ctx, done = 1, cover = 1, comms = 1, backup = 1, intent = "sim", ack = "r41_captain_copy",
                    heard = ctx == "report" ? "Alpha Lead, Comms is down, trip mine." : "Behind the container, Comms is with me, need backup.",
                    clip = ctx == "report" ? "r23_captain_drag" : "r30_captain_copy_all",
                });
                yield break;
            }
            VoiceActivity.Listen = ctx;
            float t0 = Time.time;
            while (VoiceActivity.LastOnset < since)
            {
                if (Time.time - t0 > silence || stop()) { VoiceActivity.Listen = "free"; yield break; }
                yield return null;
            }
            voice(VoiceActivity.LastOnset);
            for (float t = Time.time; VoiceActivity.Speaking && Time.time - t < 14f && !stop();) yield return null;
            HeartLink.AgentReply r = null;
            for (float t = Time.time; r == null && Time.time - t < 5f && !stop();) { r = HeartLink.TakeReply(ctx); yield return null; }
            VoiceActivity.Listen = "free";
            done(r);
        }

        // the relay puts his words on the debrief timeline; this is only for when it never heard them
        void LogEngineer(HeartLink.AgentReply said, string unchecked_, float onset, bool prompted)
        {
            if (said != null && !string.IsNullOrEmpty(said.heard)) return;
            HeartLink.Event("radio", "engineer", $"{{\"transcript\":{HeartLink.Str(unchecked_)},\"voice_only\":true,\"prompted\":{(prompted ? "true" : "false")},\"end_ms\":{Mathf.RoundToInt((Time.time - HeartLink.MissionStart) * 1000f)}}}", onset);
        }

        // the Engineer's report: his first words after the blast (headset mic); the Captain asks once if he says nothing,
        // and asks him to say again if the words were no use
        IEnumerator ReportWatch(float blast)
        {
            float called = -1; HeartLink.AgentReply said = null; bool toldToDrag = false;
            System.Func<bool> over = () => beat6Over;
            yield return Hear("report", blast + 0.8f, 9f, over, t => called = t, r => said = r);
            if (called < 0 && !beat6Over)
            {
                OpeningMetrics.ReportPrompted = true;
                yield return Say("r22_captain_report");
                yield return Hear("report", Time.time, 8f, over, t => called = t, r => said = r);
            }
            if (called >= 0)
            {
                OpeningMetrics.ReportLatency = called - blast;
                OpeningMetrics.ReportText = said?.heard; OpeningMetrics.ReportClear = said != null && said.intent == "report_ok";
                LogEngineer(said, "(reported the blast: voice heard, words not checked)", called, OpeningMetrics.ReportPrompted);
                Debug.Log($"[StealthMode] BEAT 6: Engineer reported {called - blast:0.0} s after the blast{(OpeningMetrics.ReportPrompted ? " (prompted)" : "")}: {said?.heard} -> {said?.intent}");
                if (said != null && !beat6Over)
                {
                    yield return Speak(said);
                    toldToDrag = said.clip == "r23_captain_drag";
                    if (!toldToDrag && !beat6Over)
                    {
                        float again = -1; HeartLink.AgentReply next = null;
                        yield return Hear("report", Time.time, 7f, over, t => again = t, r => next = r);
                        if (again >= 0)
                        {
                            OpeningMetrics.ReportText = next?.heard ?? OpeningMetrics.ReportText; OpeningMetrics.ReportClear = next != null && next.intent == "report_ok";
                            LogEngineer(next, "(reported the blast: voice heard, words not checked)", again, true);
                            if (next != null && !beat6Over) { yield return Speak(next); toldToDrag = true; }
                        }
                    }
                }
            }
            if (beat6Over || toldToDrag) yield break;
            // then the plain instruction, unless he is already pulling him
            if (!rescue.EverGrabbed) yield return Say("r23_captain_drag");
            else if (called >= 0) yield return Say("r23b_captain_keepgoing");
        }

        // The running talk on the net: whatever the Engineer says outside the set exchanges, the Captain answers in the first gap
        // in the traffic. If the net stays busy the moment has passed and he lets it go.
        IEnumerator OnTheNet()
        {
            while (!finished)
            {
                var r = HeartLink.TakeReply("free");
                if (r != null)
                {
                    for (float t = Time.time; (radio.isPlaying || RadioStream.Playing) && Time.time - t < 4f;) yield return null;
                    for (var newer = HeartLink.TakeReply("free"); newer != null; newer = HeartLink.TakeReply("free")) r = newer;   // only the latest still matters
                    if (!radio.isPlaying && !RadioStream.Playing && !finished) yield return Speak(r);
                }
                yield return null;
            }
        }

        // the Captain's answer in two steps, like a man on a radio: the instant "Copy" (a clip already on the headset), then his
        // real answer to what was said, coming over the link while the "Copy" plays. What the Engineer said shows meanwhile, so
        // he sees he was understood. If the real answer doesn't come in time: the pre-made line.
        IEnumerator Speak(HeartLink.AgentReply r)
        {
            var head = player.cam.transform;
            while (radio.isPlaying) yield return null;
            if (!string.IsNullOrEmpty(r.ack)) yield return Say(r.ack);
            if (!string.IsNullOrEmpty(r.heard)) Caption.Show(head, "YOU", r.heard, 5f);
            if (r.tts > 0)
            {
                string words = null; int frames = 0;
                for (float t = Time.time; !HeartLink.TakeSpeech(r.u, out words, out frames) && Time.time - t < 5f;) yield return null;
                for (float t = Time.time; radio.isPlaying && Time.time - t < 8f;) yield return null;   // the net is someone else's
                if (frames > 0) RadioStream.Expect(r.u, frames);
                for (float t = Time.time; frames > 0 && !RadioStream.Started && Time.time - t < 4f;) yield return null;
                if (frames > 0 && RadioStream.Started)
                {
                    float length = frames * 0.03f;
                    Caption.Show(head, "CAPTAIN (radio)", words + (CaptionHint.TryGetValue(r.clip, out var hint) ? "\n" + hint : ""), length + 0.6f);
                    int endMs = Mathf.RoundToInt((Time.time + length - HeartLink.MissionStart) * 1000f);
                    HeartLink.Event("radio", "captain", $"{{\"transcript\":{HeartLink.Str(words)},\"end_ms\":{endMs}}}");
                    for (float t = Time.time; RadioStream.Playing && Time.time - t < 16f;) yield return null;
                    yield return new WaitForSeconds(0.35f);
                    yield break;
                }
                Debug.LogWarning("[StealthMode] the Captain's voice didn't arrive: pre-made line instead");
            }
            if (!string.IsNullOrEmpty(r.clip)) yield return Say(r.clip);
        }

        // the Engineer's head can be seen by one of them
        bool Exposed(List<EnemyAI> squad) => SeenByEnemy(squad, player.cam.transform.position);

        bool SeenByEnemy(List<EnemyAI> squad, Vector3 p)
        {
            foreach (var e in squad)
                if (!e.Dead && e.rig.Head && LineClear(e.rig.Head.position, p)) return true;
            return false;
        }

        // shot in the open: a hard hit, the view closes to black, the Captain on the radio, MISSION FAILED
        IEnumerator Fail(string reason)
        {
            beat6Over = true;
            player.armed = false;
            rescue.Drop();
            if (wounded != null) StopCoroutine(wounded);
            commsVoice.Stop();
            var head = player.cam.transform;
            CombatFx.I.Play("bullet_crack", head.position + head.right * 0.3f, 1f, 3f);
            CombatFx.I.Play("impact_dirt_1", head.position - Vector3.up * 0.5f, 1f, 3f);
            foreach (var node in new[] { UnityEngine.XR.XRNode.LeftHand, UnityEngine.XR.XRNode.RightHand })
            {
                var d = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
                if (d.isValid) d.SendHapticImpulse(0, 1f, 0.5f);
            }
            HeartLink.Event("player_hit", "engineer", "{}");
            HeartLink.Event("player_killed", "engineer", $"{{\"reason\":{HeartLink.Str(reason)}}}");
            Phase = "beat6_failed";
            Debug.Log("[StealthMode] BEAT 6: MISSION FAILED - " + reason);
            var veil = Briefing.Veil(player.cam);
            StartCoroutine(Say("r25_captain_down"));
            yield return Briefing.Fade(veil, 0f, 1f, 1.8f);
            yield return new WaitForSeconds(0.9f);
            yield return Finish(false, reason, veil);
        }

        IEnumerator SayAfter(float delay, string clip, Transform speakerNearby)
        {
            yield return new WaitForSeconds(delay);
            yield return Say(clip, speakerNearby);
        }

        // where the blast puts him: ~1.5 m from where the Engineer is right now, past the end of the steel so that he really
        // has to be pulled in behind it, where at least one of them can see him, with a clear flight from the wire to there
        Vector3 Landing(List<EnemyAI> squad)
        {
            var steel = SteelBounds();
            var head = player.cam.transform.position;
            var g = new Vector3(head.x, 0, head.z);
            Vector3 best = Vector3.zero; float bestScore = -1;
            for (int a = -110; a <= 20; a += 10)
            {
                var d = Quaternion.Euler(0, player.transform.eulerAngles.y + a, 0) * Vector3.forward;
                var p = g + d * 1.5f;
                p.y = 0;
                foreach (var hit in Physics.RaycastAll(new Vector3(p.x, 5f, p.z), Vector3.down, 10f, ~0, QueryTriggerInteraction.Ignore))
                    if (hit.collider.name == "Terrain") p.y = hit.point.y;
                bool blocked = false;
                foreach (var c in Physics.OverlapSphere(p + Vector3.up * 0.45f, 0.35f, ~0, QueryTriggerInteraction.Ignore))
                    if (!c.GetComponent<Hitbox>() && c.name != "Terrain" && !c.transform.IsChildOf(player.transform)) { blocked = true; break; }
                if (blocked || p.x > steel.min.x + 0.1f || !LineClear(comms.Chest.position, p + Vector3.up * 0.5f)) continue;
                int seen = 0;
                foreach (var e in squad) if (!e.Dead && e.rig.Head && LineClear(e.rig.Head.position, p + Vector3.up * 0.35f)) seen++;
                float score = (seen > 0 ? 10f : 0f) + seen - Mathf.Abs(a + 45f) / 90f;   // seen, and front-left by preference
                if (score > bestScore) { bestScore = score; best = p; }
            }
            if (bestScore < 0) best = new Vector3(steel.min.x, 0, steel.min.z - 1.1f);      // just past the corner, in the open
            return best;
        }

        // his calls for help, broken by gasps (same words as assets_src/tools/gen_audio.py HELP)
        static readonly string[] HelpText =
        {
            "Engineer... help!", "Help... I can't... move.", "Engineer! Please... help me.", "My leg... help!", "I'm hit... Engineer...",
        };

        // Comms on the ground in the open, hurt, lying where the blast put him. He shouts for the Engineer, never on the radio.
        IEnumerator Wounded()
        {
            yield return new WaitForSeconds(1.4f);         // the first shout lands while the ears are still ringing
            int last = -1;
            while (true)
            {
                while (VoiceActivity.Speaking) yield return null;       // a breath while the Engineer is on the radio
                int i; do i = Random.Range(0, HelpText.Length); while (i == last && HelpText.Length > 1);
                last = i;
                var c = CombatFx.I.Clip("Voice/v_comms_help_" + (i + 1));
                if (c) { commsVoice.Stop(); commsVoice.clip = c; commsVoice.Play(); }
                if (!radio.isPlaying) Caption.Show(player.cam.transform, "COMMS (wounded)", HelpText[i], (c ? c.length : 1.5f) + 0.6f);
                HeartLink.Event("comrade_shout", "communicator", $"{{\"text\":{HeartLink.Str(HelpText[i])}}}");
                yield return new WaitForSeconds((c ? c.length : 1.5f) + Random.Range(2.0f, 3.5f));
            }
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

        /// <summary>
        /// Combat run between covers: a hard first couple of steps off the knee, full pace, braking into the last metre.
        /// The gait (cadence, stride) follows the speed he is actually moving at.
        /// </summary>
        IEnumerator RunTo(SoldierRig rig, Vector3[] path, float speed, SoldierRig.Stance gait = SoldierRig.Stance.Run, System.Func<Vector3> aim = null)
        {
            rig.stance = gait;
            if (aim != null) rig.aiming = true;
            var t = rig.transform; float t0 = Time.time;
            for (int i = 0; i < path.Length; i++)
            {
                bool last = i == path.Length - 1;
                while (true)
                {
                    var to = path[i] - t.position; to.y = 0;
                    if (to.magnitude < (last ? 0.15f : 0.45f)) break;
                    var want = Quaternion.LookRotation(to);
                    t.rotation = Quaternion.RotateTowards(t.rotation, want, 300f * Time.deltaTime);
                    float v = speed * Mathf.Clamp01(0.35f + (Time.time - t0) / 0.5f);
                    if (last) v *= Mathf.Clamp(to.magnitude, 0.45f, 1f);
                    v *= Mathf.Lerp(1f, 0.55f, Mathf.InverseLerp(30f, 100f, Quaternion.Angle(t.rotation, want)));
                    rig.runSpeed = Mathf.Max(v, 1.5f);
                    if (aim != null) rig.aimPoint = aim();
                    t.position += t.forward * v * Time.deltaTime;
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
        Coroutine suppress;
        void StartSuppress(List<EnemyAI> squad) { StopSuppress(); suppress = StartCoroutine(CommsSuppress(squad)); }
        void StopSuppress() { if (suppress != null) StopCoroutine(suppress); suppress = null; }

        IEnumerator CommsSuppress(System.Collections.Generic.List<EnemyAI> squad)
        {
            if (!comms) yield break;
            while (Phase.StartsWith("beat"))
            {
                yield return new WaitForSeconds(Random.Range(2.5f, 4.5f));
                if (commsTalking || commsBusy || comms.Dead || !comms.Muzzle) continue;
                // only at a man he can actually see from where he is: no rounds into his own cover
                EnemyAI target = null;
                foreach (var e in squad)
                    if (!e.Dead && !e.Running && e.rig.Chest && (target == null || Random.value < 0.4f) && LineClear(comms.Muzzle.position, e.rig.Chest.position)) target = e;
                if (target == null) continue;
                comms.aimPoint = target.rig.Chest.position;
                yield return new WaitForSeconds(0.55f);
                int n = Random.Range(2, 5);
                for (int i = 0; i < n && !commsTalking && !commsBusy; i++)
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
                        if (during == "engagement") OpeningMetrics.ExposedSeconds += step;
                        else
                        {
                            OpeningMetrics.Beat1ExposedSeconds += step;
                            var b = Phase.Length >= 5 ? Phase.Substring(0, 5) : Phase;
                            OpeningMetrics.ExposedByBeat[b] = (OpeningMetrics.ExposedByBeat.TryGetValue(b, out var v) ? v : 0f) + step;
                        }
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
            while (radio.isPlaying || RadioStream.Playing) yield return null;
            if (speakerNearby)
            {
                // a man two metres away: his own voice in the air a beat before the same words come through the earpiece
                commsTalking = true;
                var v = CombatFx.I.Clip("Voice/" + clip);
                if (v && commsVoice && comms && speakerNearby == comms.Head) { commsVoice.Stop(); commsVoice.clip = v; commsVoice.Play(); }
                else CombatFx.I.Play("Voice/" + clip, speakerNearby.position, 1f, 1.2f);
                yield return new WaitForSeconds(0.12f);
            }
            radio.clip = c; radio.Play();
            Debug.Log("[StealthMode] radio: " + clip);
            var said = RadioText.TryGetValue(clip, out var words) ? words : clip;
            Caption.Show(player.cam.transform, SpeakerOf(clip) + (speakerNearby ? "" : " (radio)"),
                         said + (CaptionHint.TryGetValue(clip, out var hint) ? "\n" + hint : ""), c.length + 0.6f);
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

            // beat 4: lean out and take the man Comms gives him
            while (OpeningMetrics.DesignateTime < 0 || designated == null) yield return null;
            yield return new WaitForSeconds(1.5f);
            player.SetLean(-1);
            for (int shots = 0; shots < 14 && !designated.Dead && engineerKill == null; shots++)
            {
                yield return new WaitForSeconds(0.45f);
                player.AimAt(designated.rig.Chest.position + Random.insideUnitSphere * 0.05f);
                yield return null;
                player.PullTrigger();
            }
            player.SetLean(0);

            // beat 6: the left hand onto his vest, grip, and back behind the container
            while (rescue == null) yield return null;
            yield return new WaitForSeconds(4f);
            if (autoPlayNoRescue)
            {
                player.SetLean(-1);
                while (!beat6Over) { player.PullTrigger(); yield return new WaitForSeconds(0.6f); }
                yield break;
            }
            // by the foot: any part of him will do
            rescue.simHand = comms.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftFoot).position; rescue.simGrip = true;
            yield return new WaitForSeconds(0.3f);
            var to = OpeningSceneBuilder_PlayerSpot + Vector3.right * 1.2f + Vector3.up * 0.5f;
            for (float t = 0; t < 6f && !beat6Over; t += Time.deltaTime)
            {
                rescue.simHand = Vector3.MoveTowards(rescue.simHand, to, 0.7f * Time.deltaTime);
                yield return null;
            }
            rescue.simGrip = false;
        }
        static readonly Vector3 OpeningSceneBuilder_PlayerSpot = new Vector3(24.2f, 0f, -23.35f);
    }
}
