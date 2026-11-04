import { KEYWORDS, mentions, type HrSample, type PhaseId, type SessionEvent } from "./scenario.ts";

// Every number the debrief shows is derived here from the raw event timeline + heart-rate stream,
// so the Quest only has to log what happened, never compute scores.

export interface RadioTx {
  t_ms: number;
  end_ms: number;
  actor: string;
  transcript: string;
  audio_url: string | null;
  wpm: number | null;
}

export interface Check { label: string; ok: boolean }

export interface Metrics {
  duration_ms: number;
  outcome: "success" | "failed" | null;
  kia: boolean;
  phases: { id: PhaseId; start_ms: number; end_ms: number }[];
  incident: {
    t0_ms: number | null;
    assess_s: number | null;        // first look at the Communicator
    first_action_s: number | null;  // freeze: blast -> first radio / drag / shot / cover
    report_s: number | null;        // (a) casualty reported on radio
    report_checks: Check[];
    drag_start_s: number | null;
    cover_s: number | null;         // (b) Communicator behind the rock
    shots_before_drag: number;      // (c) fighting instead of moving
    kia_s: number | null;
    suppress: { shots: number; hits: number; coverage: number; longest_gap_s: number; window_s: number } | null; // (d)
    backup: { latency_s: number; checks: Check[] } | null; // (e)
    cover_fire_response_s: number | null; // Communicator "cover me" -> first shot
  };
  fire: {
    shots: number; hits: number; accuracy: number | null; kills: number;
    sway_deg: number | null; friendly_risk: number; wasted: number; surge_reaction_s: number | null;
  };
  comms: { all: RadioTx[]; tx: number; talk_s: number; wpm: number | null; kills_reported: number; kills: number };
  hr: {
    baseline: number; peak: number; peak_t_ms: number; at_blast: number | null; rise: number | null;
    half_recovery_s: number | null; rmssd_base: number | null; rmssd_post: number | null; hrv_estimated: boolean;
    zones: { calm: number; elevated: number; high: number };
  } | null;
  shotRead: ShotRead | null;
  scores: { decision: number | null; comms: number | null; composure: number | null; marksmanship: number | null; overall: number | null };
}

/** Why the shots missed: a prediction for the debrief, never a diagnosis. */
export interface ShotRead {
  band: "few" | "low" | "below" | "average" | "good";
  misses: { stressed: boolean | null }[];   // one per missed shot, null = no heart rate at that moment
  stressed_share: number | null;            // misses taken in the elevated zone or above
  ranged: number;                           // shots that carry a target distance
  near: { shots: number; hits: number } | null;
  far: { shots: number; hits: number } | null;
  range_drop: number | null;                // (near accuracy - far accuracy) / near accuracy
  reason: "none" | "stress" | "distance" | "fit" | "unknown";
}

// Starting values, to tune after more runs. stressBpm matches the "elevated" zone below.
export const SHOT_READ = { minShots: 20, minPerRange: 10, stressBpm: 15, nearM: 20, stressShare: 0.6, rangeDrop: 0.4 } as const;

const s = (ms: number) => ms / 1000;
const first = <T,>(xs: T[]) => (xs.length ? xs[0] : undefined);

/** 1 at or better than `good`, 0 at or worse than `bad`, linear between. */
export function scale(v: number | null | undefined, good: number, bad: number): number | null {
  if (v == null || Number.isNaN(v)) return null;
  const x = (v - bad) / (good - bad);
  return Math.max(0, Math.min(1, x));
}

function mean(xs: number[]) { return xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null; }
function avgOf(parts: (number | null)[]) { return mean(parts.filter((p): p is number => p != null)); }

function rootMeanSquare(d: number[]) { return Math.sqrt(d.reduce((x, v) => x + v * v, 0) / d.length); }

// True RMSSD from the R-R intervals. The WHOOP broadcasts them only in bursts, so differences are taken inside an unbroken
// stretch of readings, and an interval far from what the heart rate implies (a missed beat) is left out.
function rmssd(samples: HrSample[]) {
  const d: number[] = [];
  let last: number | null = null, lastT = -Infinity;
  for (const x of samples) {
    if (x.t_ms - lastT > 2500) last = null;
    for (const v of x.rr_ms ?? []) {
      const expected = 60000 / x.bpm;
      if (v < expected * 0.75 || v > expected * 1.25) { last = null; continue; }
      if (last != null) d.push(v - last);
      last = v;
    }
    if (x.rr_ms?.length) lastT = x.t_ms;
  }
  return d.length >= 8 ? rootMeanSquare(d) : null;
}

// Without enough of them: the same sum on the interval each once-a-second reading implies (60000 / bpm). It follows the trend
// of real RMSSD but it is an estimate, and the debrief labels it so.
function rmssdEstimate(samples: HrSample[]) {
  const rr = samples.filter((x) => x.bpm >= 30 && x.bpm <= 220).map((x) => 60000 / x.bpm);
  return rr.length >= 8 ? rootMeanSquare(rr.slice(1).map((v, i) => v - rr[i])) : null;
}

function words(t: string) { return t.trim().split(/\s+/).filter(Boolean).length; }

export function computeMetrics(rawEvents: SessionEvent[], rawHr: HrSample[], durationHint?: number | null): Metrics {
  const ev = [...rawEvents].sort((a, b) => a.t_ms - b.t_ms);
  const hr = [...rawHr].sort((a, b) => a.t_ms - b.t_ms);
  const end = ev.find((e) => e.type === "mission_end");
  const duration_ms = durationHint ?? end?.t_ms ?? Math.max(ev.at(-1)?.t_ms ?? 0, hr.at(-1)?.t_ms ?? 0);
  const outcome = (end?.data.outcome as "success" | "failed" | undefined) ?? null;

  const of = (type: SessionEvent["type"], actor?: string) => ev.filter((e) => e.type === type && (!actor || e.actor === actor));
  const after = (xs: SessionEvent[], t: number) => xs.filter((e) => e.t_ms >= t);

  // phases
  const phaseEv = of("phase");
  const phases = phaseEv.map((p, i) => ({
    id: p.data.id as PhaseId,
    start_ms: p.t_ms,
    end_ms: phaseEv[i + 1]?.t_ms ?? duration_ms,
  }));

  // radio
  const all: RadioTx[] = of("radio").map((r) => {
    const end_ms = Number(r.data.end_ms ?? r.t_ms + 2000);
    const transcript = String(r.data.transcript ?? "");
    const mins = (end_ms - r.t_ms) / 60000;
    return {
      t_ms: r.t_ms, end_ms, actor: r.actor ?? "unknown", transcript,
      audio_url: (r.data.audio_url as string) ?? null,
      wpm: mins > 0 && transcript ? Math.round(words(transcript) / mins) : null,
    };
  });
  const eng = all.filter((r) => r.actor === "engineer");
  const shots = of("shot", "engineer");
  const killed = first(of("player_killed", "engineer"));

  // ---------------------------------------------------------------- critical window
  const blast = first(of("trip_mine"));
  const t0 = blast?.t_ms ?? null;
  const rel = (t: number | undefined) => (t0 != null && t != null ? s(t - t0) : null);

  let incident: Metrics["incident"] = {
    t0_ms: t0, assess_s: null, first_action_s: null, report_s: null, report_checks: [], drag_start_s: null,
    cover_s: null, shots_before_drag: 0, kia_s: null, suppress: null, backup: null, cover_fire_response_s: null,
  };

  const coverReq = first(of("cover_request"));
  if (coverReq) {
    const f = first(shots.filter((x) => x.t_ms >= coverReq.t_ms && (t0 == null || x.t_ms < t0)));
    incident.cover_fire_response_s = f ? s(f.t_ms - coverReq.t_ms) : null;
  }

  if (t0 != null) {
    const look = first(after(of("look_at", "engineer"), t0).filter((e) => e.data.target === "communicator"));
    const report = first(eng.filter((r) => r.t_ms >= t0 && (mentions(r.transcript, KEYWORDS.casualty) || mentions(r.transcript, KEYWORDS.who))));
    const drag = first(after(of("drag_start", "engineer"), t0));
    const dragEnd = first(after(of("drag_end", "engineer"), t0).filter((e) => e.data.reached_cover !== false));
    const shotAfter = first(after(shots, t0));
    const coverIn = first(after(of("cover_enter", "engineer"), t0));
    const firstRadio = first(eng.filter((r) => r.t_ms >= t0));
    const actions = [firstRadio?.t_ms, drag?.t_ms, shotAfter?.t_ms, coverIn?.t_ms].filter((x): x is number => x != null);

    incident = {
      ...incident,
      assess_s: rel(look?.t_ms),
      first_action_s: actions.length ? s(Math.min(...actions) - t0) : null,
      report_s: rel(report?.t_ms),
      report_checks: report
        ? [
            { label: "Who is hit", ok: mentions(report.transcript, KEYWORDS.who) },
            { label: "Casualty status", ok: mentions(report.transcript, KEYWORDS.casualty) },
            { label: "Cause (mine)", ok: mentions(report.transcript, KEYWORDS.cause) },
            { label: "Own action", ok: mentions(report.transcript, KEYWORDS.action) },
          ]
        : [],
      drag_start_s: rel(drag?.t_ms),
      cover_s: rel(dragEnd?.t_ms),
      shots_before_drag: shots.filter((x) => x.t_ms >= t0 && x.t_ms < (drag?.t_ms ?? killed?.t_ms ?? Infinity)).length,
      kia_s: rel(killed?.t_ms),
    };

    const defuse = first(of("bomb_defused"));
    if (dragEnd) {
      const winEnd = defuse?.t_ms ?? end?.t_ms ?? duration_ms;
      const inWin = shots.filter((x) => x.t_ms >= dragEnd.t_ms && x.t_ms < winEnd);
      const window_s = Math.max(0, s(winEnd - dragEnd.t_ms));
      const bins = Math.max(1, Math.ceil(window_s / 5));
      const hitBins = new Set(inWin.map((x) => Math.floor(s(x.t_ms - dragEnd.t_ms) / 5)));
      const stamps = [dragEnd.t_ms, ...inWin.map((x) => x.t_ms), winEnd];
      const longest = Math.max(...stamps.slice(1).map((t, i) => t - stamps[i]));
      incident.suppress = {
        shots: inWin.length,
        hits: inWin.filter((x) => x.data.hit).length,
        coverage: Math.min(1, hitBins.size / bins),
        longest_gap_s: s(longest),
        window_s,
      };
    }
    if (defuse) {
      const call = first(eng.filter((r) => r.t_ms >= defuse.t_ms && (mentions(r.transcript, KEYWORDS.shelter) || mentions(r.transcript, KEYWORDS.backup))));
      if (call) {
        incident.backup = {
          latency_s: s(call.t_ms - defuse.t_ms),
          checks: [
            { label: "In cover", ok: mentions(call.transcript, KEYWORDS.shelter) },
            { label: "Comrade protected", ok: mentions(call.transcript, [...KEYWORDS.who, "him", "comrade", "casualty"]) },
            { label: "Backup requested", ok: mentions(call.transcript, KEYWORDS.backup) },
          ],
        };
      }
    }
  }

  // ---------------------------------------------------------------- fire
  const hits = shots.filter((x) => x.data.hit).length;
  const sways = shots.map((x) => Number(x.data.aim_sway_deg)).filter((v) => Number.isFinite(v));
  const wave = first(of("enemy_wave"));
  const surgeShot = wave ? first(after(shots, wave.t_ms)) : undefined;
  const engKills = of("enemy_down", "engineer");
  const fire: Metrics["fire"] = {
    shots: shots.length,
    hits,
    accuracy: shots.length ? hits / shots.length : null,
    kills: engKills.length,
    sway_deg: mean(sways),
    friendly_risk: shots.filter((x) => x.data.friendly_risk).length,
    wasted: shots.filter((x) => x.data.target == null).length,
    surge_reaction_s: wave && surgeShot ? s(surgeShot.t_ms - wave.t_ms) : null,
  };

  // ---------------------------------------------------------------- comms
  const talk_ms = eng.reduce((a, r) => a + (r.end_ms - r.t_ms), 0);
  const engWords = eng.reduce((a, r) => a + words(r.transcript), 0);
  const comms: Metrics["comms"] = {
    all,
    tx: eng.length,
    talk_s: s(talk_ms),
    wpm: talk_ms > 0 ? Math.round(engWords / (talk_ms / 60000)) : null,
    kills: engKills.length,
    kills_reported: engKills.filter((k) => eng.some((r) => r.t_ms >= k.t_ms && r.t_ms - k.t_ms <= 10000)).length,
  };

  // ---------------------------------------------------------------- heart
  let hrOut: Metrics["hr"] = null;
  if (hr.length > 5) {
    const briefEnd = phases.find((p) => p.id === "briefing")?.end_ms ?? 30000;
    const base = mean(hr.filter((x) => x.t_ms < briefEnd).map((x) => x.bpm)) ?? hr[0].bpm;
    const peak = hr.reduce((a, b) => (b.bpm > a.bpm ? b : a));
    let at_blast: number | null = null, rise: number | null = null, half: number | null = null;
    const calm = hr.filter((x) => x.t_ms < briefEnd);
    let afterBlast: HrSample[] = [];
    if (t0 != null) {
      const near = hr.reduce((a, b) => (Math.abs(b.t_ms - t0) < Math.abs(a.t_ms - t0) ? b : a));
      at_blast = near.bpm;
      const win = hr.filter((x) => x.t_ms >= t0 && x.t_ms <= t0 + 45000);
      if (win.length) {
        const pk = win.reduce((a, b) => (b.bpm > a.bpm ? b : a));
        rise = pk.bpm - at_blast;
        const target = at_blast + rise / 2;
        const back = hr.find((x) => x.t_ms > pk.t_ms && x.bpm <= target);
        half = back ? s(back.t_ms - pk.t_ms) : null;
      }
      afterBlast = hr.filter((x) => x.t_ms >= t0 && x.t_ms <= t0 + 60000);
    }
    const z = { calm: 0, elevated: 0, high: 0 };
    hr.forEach((x) => { const d = x.bpm - base; if (d < 15) z.calm++; else if (d < 35) z.elevated++; else z.high++; });
    // both numbers from the same source, or comparing them means nothing
    const trueBase = rmssd(calm), truePost = rmssd(afterBlast);
    const hrv_estimated = trueBase == null || truePost == null;
    hrOut = {
      baseline: Math.round(base), peak: peak.bpm, peak_t_ms: peak.t_ms, at_blast, rise, half_recovery_s: half,
      rmssd_base: hrv_estimated ? rmssdEstimate(calm) : trueBase,
      rmssd_post: hrv_estimated ? rmssdEstimate(afterBlast) : truePost,
      hrv_estimated,
      zones: { calm: z.calm / hr.length, elevated: z.elevated / hr.length, high: z.high / hr.length },
    };
  }

  // ---------------------------------------------------------------- shot read
  let shotRead: ShotRead | null = null;
  if (shots.length) {
    const C = SHOT_READ;
    const a = hits / shots.length;
    const band: ShotRead["band"] = shots.length < C.minShots ? "few" : a >= 0.7 ? "good" : a >= 0.5 ? "average" : a >= 0.3 ? "below" : "low";
    const bpmAt = (t: number) => {
      let b: HrSample | undefined;
      for (const x of hr) { if (x.t_ms > t) break; b = x; }
      return b && t - b.t_ms <= 5000 ? b.bpm : null;
    };
    const misses = shots.filter((x) => !x.data.hit).map((x) => {
      const b = hrOut ? bpmAt(x.t_ms) : null;
      return { stressed: b == null || !hrOut ? null : b - hrOut.baseline >= C.stressBpm };
    });
    const known = misses.filter((x) => x.stressed != null);
    const stressed_share = known.length ? known.filter((x) => x.stressed).length / known.length : null;
    const ranged = shots.filter((x) => typeof x.data.target_dist_m === "number");
    const tally = (xs: SessionEvent[]) => (xs.length >= C.minPerRange ? { shots: xs.length, hits: xs.filter((x) => x.data.hit).length } : null);
    const near = tally(ranged.filter((x) => (x.data.target_dist_m as number) < C.nearM));
    const far = tally(ranged.filter((x) => (x.data.target_dist_m as number) >= C.nearM));
    const range_drop = near && far && near.hits ? (near.hits / near.shots - far.hits / far.shots) / (near.hits / near.shots) : null;
    let reason: ShotRead["reason"] = "none";
    if (band === "below" || band === "low") {
      reason = stressed_share != null && stressed_share >= C.stressShare ? "stress"
        : stressed_share != null && range_drop != null ? (range_drop >= C.rangeDrop ? "distance" : "fit")
        : "unknown";
    }
    shotRead = { band, misses, stressed_share, ranged: ranged.length, near, far, range_drop, reason };
  }

  // ---------------------------------------------------------------- scores (0–100)
  const kia = !!killed;
  const pct = (v: number | null) => (v == null ? null : Math.round(Math.max(0, Math.min(1, v)) * 100));
  const checkFrac = (c: Check[]) => (c.length ? c.filter((x) => x.ok).length / c.length : 0);

  const decision = t0 == null ? null
    : kia ? 0
    : incident.cover_s == null ? 0.1
    : 0.6 * (scale(incident.cover_s, 10, 40) ?? 0) + 0.4 * (scale(incident.first_action_s, 1.5, 8) ?? 0);

  const reportPart = incident.report_s == null ? 0 : (scale(incident.report_s, 3, 20) ?? 0) * (0.6 + 0.4 * checkFrac(incident.report_checks));
  const backupPart = incident.backup ? (scale(incident.backup.latency_s, 5, 40) ?? 0) * (0.5 + 0.5 * checkFrac(incident.backup.checks)) : 0;
  const killPart = comms.kills ? comms.kills_reported / comms.kills : 1;
  const commsScore = t0 == null ? null : 0.5 * reportPart + 0.3 * backupPart + 0.2 * killPart;

  const composure = avgOf([
    scale(hrOut?.rise, 10, 45),
    scale(hrOut?.half_recovery_s, 10, 45),
    scale(incident.first_action_s, 1.5, 8),
  ]);

  const marks = fire.shots === 0 ? null
    : 0.5 * (fire.accuracy ?? 0) + 0.3 * (incident.suppress?.coverage ?? 0) + 0.2 * (scale(fire.sway_deg, 0.8, 4) ?? 0.5)
      - 0.1 * fire.friendly_risk;

  const weighted = [[decision, 0.35], [commsScore, 0.25], [composure, 0.2], [marks, 0.2]] as const;
  const have = weighted.filter(([v]) => v != null) as [number, number][];
  let overall = have.length ? have.reduce((a, [v, w]) => a + v * w, 0) / have.reduce((a, [, w]) => a + w, 0) : null;
  if (overall != null && kia) overall = Math.min(overall, 0.4);

  return {
    duration_ms, outcome, kia, phases, incident, fire, comms, hr: hrOut, shotRead,
    scores: { decision: pct(decision), comms: pct(commsScore), composure: pct(composure), marksmanship: pct(marks), overall: pct(overall) },
  };
}
