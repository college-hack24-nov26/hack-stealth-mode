import type { Actor, EventType, HrSample, PhaseId, SessionEvent } from "./scenario.ts";

// Plays Scenario One end to end for a synthetic Engineer. Used for demo data when Supabase
// isn't configured, by scripts/simulate-session.ts, and as a reference for the Mac relay.

export interface Persona {
  seed: number;
  skill: number;        // 0 (green) .. 1 (veteran): drives reaction times, accuracy, HR control
  engagesInstead?: boolean; // fights the last 3 instead of taking cover -> KIA
  restingHr?: number;
}

function rng(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function simulate(p: Persona): { events: SessionEvent[]; hr: HrSample[]; duration_ms: number; outcome: "success" | "failed" } {
  const r = rng(p.seed);
  const jitter = (base: number, spread: number) => base + (r() - 0.5) * 2 * spread;
  const lerp = (green: number, vet: number) => green + (vet - green) * p.skill;
  const ev: SessionEvent[] = [];
  const push = (t: number, type: EventType, actor: Actor | null = null, data: Record<string, unknown> = {}) =>
    ev.push({ t_ms: Math.round(t), type, actor, data });
  const phase = (t: number, id: PhaseId) => push(t, "phase", "system", { id });
  const radio = (t: number, actor: Actor, transcript: string) => {
    const dur = 600 + transcript.split(" ").length * jitter(330, 40);
    push(t, "radio", actor, { transcript, end_ms: Math.round(t + dur) });
    return t + dur;
  };
  let enemy = 0;
  const shoot = (t: number, opts: { hitP: number; target?: boolean; fromCover?: boolean; friendlyP?: number }) => {
    const hit = r() < opts.hitP;
    const target = opts.target === false ? null : `E${Math.min(10, enemy + 1)}`;
    push(t, "shot", "engineer", {
      hit: target ? hit : false,
      target,
      aim_sway_deg: +Math.max(0.3, jitter(lerp(3.6, 1.0), 0.6)).toFixed(2),
      from_cover: !!opts.fromCover,
      friendly_risk: r() < (opts.friendlyP ?? 0),
    });
    return hit && !!target;
  };
  const burst = (t0: number, n: number, gap: number, opts: Parameters<typeof shoot>[1], until = Infinity) => {
    let t = t0;
    for (let i = 0; i < n && t < until; i++) { shoot(t, opts); t += jitter(gap, gap * 0.35); }
    return t;
  };
  const down = (t: number, by: Actor) => push(t, "enemy_down", by, { enemy: `E${++enemy}` });
  const acc = lerp(0.32, 0.72);

  // 0–30 s briefing video
  phase(0, "briefing");
  phase(30000, "insertion");
  radio(36000, "captain", "Alpha, Captain. Two by two by two. Snipers set, Paramedic with me, we go on your shots.");

  phase(46000, "contact");
  down(48200, "sniper_1"); radio(49000, "sniper_1", "Sniper one. Tango down, north gate.");
  down(52100, "sniper_2"); radio(52900, "sniper_2", "Sniper two. Tango down, east corner.");
  let t = burst(55000, 4, 700, { hitP: acc });
  down(t, "engineer");
  radio(t + jitter(lerp(6500, 1800), 800), "engineer", "Engineer, one down at the front.");
  down(61200, "communicator"); radio(62000, "communicator", "Communicator, tango down.");
  push(63500, "enemy_alert", "enemy");

  phase(66000, "escalation");
  push(66000, "enemy_wave", "enemy", { count: 5 });
  burst(66000 + jitter(lerp(2600, 700), 300), 5, 650, { hitP: acc * 0.8, target: r() > 0.25 });
  radio(70000, "communicator", "Captain, they're all out front. Go in from behind and take out the bomb.");
  radio(75000, "captain", "Captain. We're inside. Moving to the device.");
  down(82000, "sniper_2"); radio(82800, "sniper_2", "Sniper two, tango down.");

  phase(88000, "rain");
  push(88000, "rain_start", "system");
  radio(91000, "sniper_1", "Sniper one. Rain's too heavy, no shot. You're on your own out front.");
  t = burst(94000, 5, 800, { hitP: acc, friendlyP: lerp(0.15, 0) });
  down(t, "engineer");
  radio(t + jitter(lerp(7000, 2000), 900), "engineer", "Engineer. Another one down.");
  down(104000, "communicator"); radio(104800, "communicator", "Communicator, got one. Three left.");

  // Communicator moves up, asks for cover
  radio(110000, "communicator", "Engineer, I'm moving up. Cover me.");
  push(111800, "cover_request", "communicator");
  const T0 = 116000;
  burst(111800 + jitter(lerp(3000, 800), 300), 4, 600, { hitP: acc * 0.6 }, T0 - 300);

  // ---- critical window
  phase(T0, "casualty");
  push(T0, "trip_mine", "communicator");
  push(T0 + 1800, "comrade_shout", "communicator", { line: "Engineer! I'm hit, I can't move! Get me out of here!" });
  push(T0 + jitter(lerp(2600, 700), 300), "look_at", "engineer", { target: "communicator" });

  if (p.engagesInstead) {
    let tt = T0 + jitter(lerp(3500, 2000), 400);
    for (let i = 0; i < 7; i++) { shoot(tt, { hitP: acc * 0.5 }); tt += jitter(650, 200); }
    push(tt + 400, "player_hit", "engineer", { by: "E8" });
    push(tt + 1800, "player_killed", "engineer", { by: "E9" });
    push(tt + 2400, "mission_end", "system", { outcome: "failed", reason: "KIA — engaged without cover" });
    return { events: ev, hr: heart(p, r, T0, tt + 2400, true), duration_ms: Math.round(tt + 2400), outcome: "failed" };
  }

  const reportAt = T0 + jitter(lerp(13000, 3200), 1200);
  const reportLine = p.skill > 0.6
    ? "Alpha, Engineer. Communicator is down, trip mine, he's hit bad. I'm pulling him behind the rock."
    : p.skill > 0.3
      ? "Communicator is hit. He's down."
      : "Uh, Communicator's down.";
  radio(reportAt, "engineer", reportLine);
  const dragAt = T0 + jitter(lerp(9000, 3800), 900);
  push(dragAt, "drag_start", "engineer");
  const coverAt = dragAt + jitter(lerp(16000, 7000), 1200);
  push(coverAt, "drag_end", "engineer", { reached_cover: true });
  push(coverAt, "cover_enter", "engineer");

  phase(coverAt, "holdout");
  const defuseAt = 158000;
  let tt = coverAt + jitter(lerp(6000, 1500), 500);
  while (tt < defuseAt - 1000) {
    shoot(tt, { hitP: acc * 0.55, fromCover: true });
    tt += jitter(lerp(9000, 2600), 1200);
  }
  push(defuseAt, "bomb_defused", "captain");
  phase(defuseAt, "extraction");
  radio(defuseAt + 300, "captain", "Captain. Bomb is neutralised. Coming out.");
  const callAt = defuseAt + 3500 + jitter(lerp(22000, 3000), 1500);
  radio(callAt, "engineer", p.skill > 0.45
    ? "Engineer. In cover behind the rock with Communicator, he's injured. Need backup and the medic."
    : "Engineer, we're behind the rock. Need help.");
  for (const [i, who] of (["captain", "paramedic", "captain"] as Actor[]).entries()) down(callAt + 5000 + i * 2600, who);
  radio(callAt + 13500, "captain", "Captain. Area clear. Paramedic on the Communicator.");
  const endAt = callAt + 17000;
  push(endAt, "mission_end", "system", { outcome: "success", reason: "Bomb neutralised, team extracted" });
  return { events: ev, hr: heart(p, r, T0, endAt, false), duration_ms: Math.round(endAt), outcome: "success" };
}

function heart(p: Persona, r: () => number, T0: number, end: number, died: boolean): HrSample[] {
  const rest = p.restingHr ?? 68;
  const control = 0.4 + 0.6 * p.skill; // higher = smaller spikes, faster recovery
  const out: HrSample[] = [];
  let bpm = rest + 4;
  for (let t = 0; t <= end; t += 1000) {
    let target = rest + 6;
    if (t > 30000) target = rest + 18;
    if (t > 46000) target = rest + 32 - 8 * control;
    if (t > 66000) target = rest + 44 - 14 * control;
    if (t > 88000) target = rest + 40 - 12 * control;
    if (t >= T0) {
      const since = (t - T0) / 1000;
      const spike = (70 - 34 * control) * Math.exp(-since / (15 + 45 * (1 - control)));
      target = rest + 40 - 12 * control + spike;
    }
    if (died && t > end - 6000) target += 12;
    bpm += (target - bpm) * 0.35 + (r() - 0.5) * 2.4;
    const ibi = 60000 / bpm;
    const hrv = (14 + 30 * control) * (t < 30000 ? 1.4 : t > T0 && t < T0 + 40000 ? 0.45 : 0.8);
    const beats = Math.max(1, Math.round(1000 / ibi));
    const rr = Array.from({ length: beats }, () => Math.round(ibi + (r() - 0.5) * hrv));
    out.push({ t_ms: t, bpm: Math.round(bpm), rr_ms: rr });
  }
  return out;
}
