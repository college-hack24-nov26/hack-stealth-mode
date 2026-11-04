"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useMemo, useState } from "react";
import { getBundle, subscribe, type Bundle } from "@/lib/data";
import { computeMetrics } from "@/lib/metrics";
import { PHASES, TEAM, type SessionEvent } from "@/lib/scenario";
import { clock, pct, secs, toneLow, toneScore, when } from "@/lib/format";
import { HrChart, IncidentTimeline, ShotRead, ShotStrip } from "@/components/charts";
import { OutcomePill, RadioLog, ScoreBadge, ScoreRows, ScriptChecks, Stat, verdict, ZoneBar } from "@/components/debrief";
import { Empty, Loading } from "@/components/states";

export default function SessionPage() {
  return <Suspense fallback={<Loading />}><Debrief /></Suspense>;
}

const READINESS = { good: "Ready", warn: "Needs work", bad: "Not ready" } as const;

function Debrief() {
  const id = useSearchParams().get("id");
  const [b, setB] = useState<Bundle | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!id) return;
    getBundle(id).then(setB).catch((e) => setError(e.message ?? String(e)));
  }, [id]);

  const live = b?.session.status === "live";
  useEffect(() => {
    if (!id || !live) return;
    return subscribe(id, {
      event: (e) => setB((cur) => cur && { ...cur, events: [...cur.events, e] }),
      hr: (h) => setB((cur) => cur && { ...cur, hr: [...cur.hr, h] }),
      session: (s) => setB((cur) => cur && { ...cur, session: { ...cur.session, ...s } }),
    });
  }, [id, live]);

  const m = useMemo(() => b && computeMetrics(b.events, b.hr, live ? null : b.session.duration_ms), [b, live]);

  if (!id) return <Empty title="No run selected" body="Open a run from the list." />;
  if (error) return <Empty title="Couldn’t load this run" body={error} />;
  if (b === undefined || !m) return <Loading />;
  if (b === null) return <Empty title="Run not found" body="It may have been deleted." />;

  const w = when(b.session.started_at);
  const t = b.session.trainee;
  const tone = toneScore(m.scores.overall);
  const engineerKills = b.events.filter((e) => e.type === "enemy_down" && e.actor === "engineer").length;

  return (
    <>
      <nav style={{ paddingTop: 18 }}>
        <Link href="/" className="faint" style={{ fontSize: 13, display: "inline-flex", alignItems: "center", gap: 6 }}>
          <svg width="8" height="12" viewBox="0 0 8 12" aria-hidden="true"><path d="M6.5 1l-5 5 5 5" stroke="currentColor" strokeWidth="1.5" fill="none" /></svg>
          All runs
        </Link>
      </nav>

      {/* ------------------------------------------------ header */}
      <header style={{ paddingTop: 14 }}>
        <div style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
          <OutcomePill outcome={b.session.outcome} live={live} />
          <span className="eyebrow">{w.weekday} {w.day} · {w.time} · {clock(m.duration_ms)}</span>
        </div>
        <h1 className="display" style={{ fontSize: "clamp(38px, 10vw, 68px)", margin: "12px 0 0" }}>
          {live ? "Run in progress" : m.kia ? <>Mission failed<span className="t-bad">.</span></> : b.session.outcome === "success" ? <>Mission successful<span className="t-good">.</span></> : "Run aborted"}
        </h1>
        <p className="muted" style={{ margin: "6px 0 0" }}>
          {t ? `${t.name}${t.callsign ? ` (${t.callsign})` : ""}` : "Trainee"} as Engineer · Scenario One, warehouse raid
        </p>
      </header>

      {/* ------------------------------------------------ score + verdict */}
      <div className="grid-2" style={{ marginTop: 22 }}>
        <div className="card">
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start" }}>
            <div>
              <div className="eyebrow">Readiness</div>
              <div style={{ display: "flex", alignItems: "flex-end", gap: 10, marginTop: 10 }}>
                <ScoreBadge v={m.scores.overall} size={84} />
                {m.scores.overall != null && !live && <span className={`pill pill-${tone === "good" ? "good" : tone === "bad" ? "bad" : "live"}`} style={{ marginBottom: 8 }}>{READINESS[tone]}</span>}
              </div>
            </div>
            {live && m.hr && (
              <div style={{ textAlign: "right" }}>
                <div className="eyebrow">Heart</div>
                <div className="mono t-signal" style={{ fontSize: 34, marginTop: 8 }}>{b.hr.at(-1)?.bpm}<span className="stat-unit">bpm</span></div>
              </div>
            )}
          </div>
          <ul style={{ margin: "18px 0 0", padding: 0, listStyle: "none", display: "grid", gap: 8 }}>
            {verdict(m, live).map((v) => (
              <li key={v} style={{ display: "grid", gridTemplateColumns: "14px 1fr", gap: 8, fontSize: 14 }}>
                <span style={{ width: 6, height: 6, background: "var(--signal)", borderRadius: 1, marginTop: 7 }} />{v}
              </li>
            ))}
          </ul>
        </div>
        <div className="card"><ScoreRows m={m} /></div>
      </div>

      {/* ------------------------------------------------ critical window */}
      <section className="section">
        <div className="section-head">
          <h2 className="display section-title" style={{ margin: 0 }}>The trip mine</h2>
          <span className="section-note mono">{m.incident.t0_ms != null ? `at ${clock(m.incident.t0_ms)}` : live ? "not yet" : "not reached"}</span>
        </div>
        <div className="grid-2">
          <div className="card">
            <p className="muted" style={{ margin: "0 0 14px", fontSize: 13 }}>
              Seconds from the blast. Green ticks are the targets.
            </p>
            {m.incident.t0_ms != null ? <IncidentTimeline m={m} /> : <p className="faint">{live ? "Waiting for the Communicator to step on the mine." : "The run ended before the Communicator stepped on the mine."}</p>}
          </div>
          <div className="card" style={{ paddingTop: 4, paddingBottom: 4 }}>
            <ScriptChecks m={m} />
          </div>
        </div>
      </section>

      {/* ------------------------------------------------ heart */}
      <section className="section">
        <div className="section-head">
          <h2 className="display section-title" style={{ margin: 0 }}>Heart</h2>
          <span className="section-note">WHOOP · 1 Hz</span>
        </div>
        <div className="card" style={{ padding: "16px 12px 10px" }}>
          <HrChart hr={b.hr} m={m} events={b.events} />
        </div>
        {m.hr && (
          <>
            <div className="stats stats-4" style={{ marginTop: 12 }}>
              <Stat label="Baseline (briefing)" value={m.hr.baseline} unit="bpm" />
              <Stat label="Peak" value={m.hr.peak} unit="bpm" sub={`at ${clock(m.hr.peak_t_ms)}`} />
              <Stat label="Spike after blast" value={m.hr.rise != null ? `+${m.hr.rise}` : "—"} unit="bpm" sub={m.hr.at_blast != null ? `from ${m.hr.at_blast}` : undefined} tone={m.hr.rise == null ? undefined : toneLow(m.hr.rise, 18, 30)} />
              <Stat label="Half recovery" value={secs(m.hr.half_recovery_s, 0)} unit="s" sub="peak back halfway" tone={m.hr.half_recovery_s == null ? "bad" : toneLow(m.hr.half_recovery_s, 15, 30)} />
            </div>
            <div className="card" style={{ marginTop: 12 }}>
              <div className="faint" style={{ fontSize: 12, marginBottom: 10 }}>Time in zone, relative to briefing baseline (+15 / +35 bpm)</div>
              <ZoneBar zones={m.hr.zones} />
            </div>
          </>
        )}
      </section>

      {/* ------------------------------------------------ fire + comms */}
      <div className="grid-2">
        <section className="section">
          <div className="section-head">
            <h2 className="display section-title" style={{ margin: 0 }}>Weapon</h2>
            <span className="section-note">hand-tracked</span>
          </div>
          <div className="card" style={{ padding: "14px 12px 8px" }}>
            <div style={{ display: "flex", gap: 14, fontSize: 12, marginBottom: 6 }} className="muted">
              <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}><svg width="10" height="10"><circle cx="5" cy="5" r="4" fill="var(--signal)" /></svg>Hit</span>
              <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}><svg width="10" height="10"><circle cx="5" cy="5" r="3.5" fill="none" stroke="var(--ink-2)" strokeWidth="1.5" /></svg>Miss</span>
              <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}><svg width="10" height="10"><circle cx="5" cy="5" r="3.5" fill="none" stroke="var(--bad)" strokeWidth="1.5" /></svg>Teammate in line</span>
            </div>
            <ShotStrip events={b.events} m={m} />
          </div>
          <div className="stats" style={{ marginTop: 12 }}>
            <Stat label="Accuracy" value={pct(m.fire.accuracy)} unit="%" sub={`${m.fire.hits} of ${m.fire.shots} rounds`} />
            <Stat label="Kills" value={engineerKills} sub="script expects 2" />
            <Stat label="Aim sway" value={secs(m.fire.sway_deg, 2)} unit="°" sub="500 ms before trigger" tone={m.fire.sway_deg == null ? undefined : toneLow(m.fire.sway_deg, 1.5, 2.8)} />
            <Stat label="Surge reaction" value={secs(m.fire.surge_reaction_s)} unit="s" sub="guards rush out → first shot" />
            <Stat label="Cover-me response" value={secs(m.incident.cover_fire_response_s)} unit="s" sub="“cover me” → first shot" tone={m.incident.cover_fire_response_s == null ? "bad" : toneLow(m.incident.cover_fire_response_s, 1.5, 3)} />
            <Stat label="Trigger discipline" value={m.fire.friendly_risk + m.fire.wasted} sub={`${m.fire.friendly_risk} teammate in line · ${m.fire.wasted} no target`} tone={m.fire.friendly_risk ? "bad" : m.fire.wasted > 3 ? "warn" : "good"} />
          </div>
        </section>

        <section className="section">
          <div className="section-head">
            <h2 className="display section-title" style={{ margin: 0 }}>Radio</h2>
            <span className="section-note">spoken, transcribed</span>
          </div>
          <div className="stats stats-3">
            <Stat label="Transmissions" value={m.comms.tx} />
            <Stat label="Talk time" value={secs(m.comms.talk_s)} unit="s" />
            <Stat label="Pace" value={m.comms.wpm ?? "—"} unit="wpm" sub="140–170 is clear" tone={m.comms.wpm == null ? undefined : m.comms.wpm > 190 ? "warn" : "good"} />
            <Stat label="Kills reported" value={`${m.comms.kills_reported}/${m.comms.kills}`} sub="within 10 s" tone={m.comms.kills_reported === m.comms.kills ? "good" : "warn"} />
            <Stat label="First action" value={secs(m.incident.first_action_s)} unit="s" sub="freeze after blast" tone={toneLow(m.incident.first_action_s, 3, 6)} />
            <Stat label="Eyes on casualty" value={secs(m.incident.assess_s)} unit="s" sub="head turned to him" />
          </div>
          <div className="card" style={{ marginTop: 12, padding: "6px 14px" }}>
            <RadioLog tx={m.comms.all} />
          </div>
        </section>
      </div>

      {/* ------------------------------------------------ why the shots missed */}
      <section className="section">
        <div className="section-head">
          <h2 className="display section-title" style={{ margin: 0 }}>Shot accuracy</h2>
          <span className="section-note">a prediction, not a diagnosis</span>
        </div>
        <div className="card"><ShotRead m={m} /></div>
      </section>

      {/* ------------------------------------------------ raw log */}
      <section className="section">
        <details className="card">
          <summary style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
            <span className="display" style={{ fontSize: 20 }}>Full event log</span>
            <span className="faint mono" style={{ fontSize: 12 }}>{b.events.length} events</span>
          </summary>
          <ol style={{ listStyle: "none", padding: 0, margin: "14px 0 0", fontSize: 12 }} className="mono">
            {b.events.map((e, k) => (
              <li key={k} style={{ display: "grid", gridTemplateColumns: "48px 1fr", gap: 10, padding: "5px 0", borderTop: "1px solid var(--line)" }}>
                <span className="faint">{clock(e.t_ms)}</span>
                <span className="muted" style={{ overflowWrap: "anywhere" }}>{describe(e)}</span>
              </li>
            ))}
          </ol>
        </details>
      </section>
    </>
  );
}

function describe(e: SessionEvent) {
  const who = e.actor ? TEAM[e.actor as keyof typeof TEAM] ?? e.actor : "";
  switch (e.type) {
    case "phase": return `— ${PHASES[e.data.id as keyof typeof PHASES] ?? String(e.data.id)} —`;
    case "shot": return `Engineer fires · ${e.data.hit ? "hit" : e.data.target ? "miss" : "no target"}${e.data.from_cover ? " · from cover" : ""}`;
    case "enemy_down": return `${who} · ${String(e.data.enemy)} down`;
    case "radio": return `${who} on radio: “${String(e.data.transcript)}”`;
    case "trip_mine": return "Communicator hits a trip mine";
    case "comrade_shout": return `Communicator shouts: “${String(e.data.line ?? "help")}”`;
    case "enemy_wave": return `${String(e.data.count)} guards rush out`;
    case "enemy_alert": return "Escaped guard screams for help";
    case "look_at": return `Engineer looks at ${String(e.data.target)}`;
    case "drag_start": return "Engineer starts dragging the Communicator";
    case "drag_end": return e.data.reached_cover === false ? "Drag stopped short of cover" : "Communicator behind the rock";
    case "mission_end": return `Mission ${String(e.data.outcome)} · ${String(e.data.reason ?? "")}`;
    default: return `${who ? `${who} · ` : ""}${e.type.replace(/_/g, " ")}`;
  }
}
