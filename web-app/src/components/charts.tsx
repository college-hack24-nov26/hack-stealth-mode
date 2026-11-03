"use client";

import { useEffect, useRef, useState } from "react";
import type { Metrics } from "@/lib/metrics";
import { PHASES, type HrSample, type SessionEvent } from "@/lib/scenario";
import { clock, secs } from "@/lib/format";

function useWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [w, setW] = useState(0);
  useEffect(() => {
    if (!ref.current) return;
    const ro = new ResizeObserver(([e]) => setW(Math.round(e.contentRect.width)));
    ro.observe(ref.current);
    return () => ro.disconnect();
  }, []);
  return [ref, w] as const;
}

const niceTicks = (lo: number, hi: number, step: number) => {
  const out: number[] = [];
  for (let v = Math.ceil(lo / step) * step; v <= hi; v += step) out.push(v);
  return out;
};

// ---------------------------------------------------------------- heart rate over the mission

export function HrChart({ hr, m, events }: { hr: HrSample[]; m: Metrics; events: SessionEvent[] }) {
  const [ref, W] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);
  const H = 210, L = 34, R = 10, T = 14, B = 24;
  if (!hr.length) return <div className="card faint" style={{ fontSize: 13 }}>No heart-rate stream for this run.</div>;

  const dur = Math.max(m.duration_ms, hr.at(-1)!.t_ms);
  const lo = Math.floor((Math.min(...hr.map((h) => h.bpm)) - 6) / 10) * 10;
  const hi = Math.ceil((Math.max(...hr.map((h) => h.bpm)) + 6) / 10) * 10;
  const iw = Math.max(1, W - L - R), ih = H - T - B;
  const x = (t: number) => L + (t / dur) * iw;
  const y = (b: number) => T + ih - ((b - lo) / (hi - lo)) * ih;
  const path = hr.map((h, i) => `${i ? "L" : "M"}${x(h.t_ms).toFixed(1)},${y(h.bpm).toFixed(1)}`).join("");
  const area = `${path}L${x(hr.at(-1)!.t_ms)},${T + ih}L${x(hr[0].t_ms)},${T + ih}Z`;
  const blast = m.incident.t0_ms;
  const xTicks = niceTicks(0, dur, dur > 240000 ? 60000 : 30000);
  const h = hover != null ? hr[hover] : null;
  const phaseAt = (t: number) => m.phases.findLast((p) => p.start_ms <= t);
  const evNear = h ? events.find((e) => Math.abs(e.t_ms - h.t_ms) < 1500 && ["trip_mine", "enemy_wave", "bomb_defused", "rain_start", "player_killed"].includes(e.type)) : null;

  const onMove = (clientX: number) => {
    const r = ref.current!.getBoundingClientRect();
    const t = ((clientX - r.left - L) / iw) * dur;
    let best = 0;
    for (let i = 1; i < hr.length; i++) if (Math.abs(hr[i].t_ms - t) < Math.abs(hr[best].t_ms - t)) best = i;
    setHover(best);
  };

  return (
    <div ref={ref} style={{ position: "relative", touchAction: "pan-y" }}
      onPointerMove={(e) => onMove(e.clientX)} onPointerDown={(e) => onMove(e.clientX)} onPointerLeave={() => setHover(null)}>
      {W > 0 && (
        <svg width={W} height={H} role="img" aria-label={`Heart rate over the mission, baseline ${m.hr?.baseline} bpm, peak ${m.hr?.peak} bpm`}>
          {m.phases.map((p, i) => i % 2 === 1 && (
            <rect key={p.id} x={x(p.start_ms)} y={T} width={Math.max(0, x(p.end_ms) - x(p.start_ms))} height={ih} fill="var(--raised-2)" />
          ))}
          {niceTicks(lo, hi, hi - lo > 60 ? 20 : 10).map((v) => (
            <g key={v}>
              <line x1={L} x2={W - R} y1={y(v)} y2={y(v)} stroke="var(--line)" />
              <text x={L - 8} y={y(v) + 4} textAnchor="end" fontSize="10" fill="var(--ink-3)" className="mono">{v}</text>
            </g>
          ))}
          {xTicks.map((t) => (
            <text key={t} x={x(t)} y={H - 6} textAnchor="middle" fontSize="10" fill="var(--ink-3)" className="mono">{clock(t)}</text>
          ))}
          {m.hr && (
            <g>
              <line x1={L} x2={W - R} y1={y(m.hr.baseline)} y2={y(m.hr.baseline)} stroke="var(--ink-3)" strokeDasharray="3 4" />
              <text x={W - R} y={y(m.hr.baseline) - 5} textAnchor="end" fontSize="10" fill="var(--ink-2)" className="mono">baseline {m.hr.baseline}</text>
            </g>
          )}
          <path d={area} fill="var(--signal)" opacity="0.07" />
          <path d={path} fill="none" stroke="var(--signal)" strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
          {blast != null && (
            <g>
              <line x1={x(blast)} x2={x(blast)} y1={T} y2={T + ih} stroke="var(--bad)" strokeWidth="1.5" />
              <text x={x(blast) + 5} y={T + 10} fontSize="10" fill="var(--bad)" className="mono">MINE</text>
            </g>
          )}
          {h && (
            <g>
              <line x1={x(h.t_ms)} x2={x(h.t_ms)} y1={T} y2={T + ih} stroke="var(--ink-2)" strokeWidth="1" />
              <circle cx={x(h.t_ms)} cy={y(h.bpm)} r="4.5" fill="var(--signal)" stroke="var(--raised)" strokeWidth="2" />
            </g>
          )}
        </svg>
      )}
      {h && (
        <div className="tip" style={{ left: Math.min(Math.max(x(h.t_ms), 70), W - 70), top: y(h.bpm) }}>
          <span className="mono">{clock(h.t_ms)}</span> · <strong className="mono">{h.bpm}</strong> bpm
          <div className="faint">{evNear ? eventLabel(evNear) : PHASES[phaseAt(h.t_ms)?.id ?? "briefing"]}</div>
        </div>
      )}
    </div>
  );
}

function eventLabel(e: SessionEvent) {
  return ({ trip_mine: "Trip mine", enemy_wave: "Enemy surge", bomb_defused: "Bomb neutralised", rain_start: "Rain starts", player_killed: "Engineer KIA" } as Record<string, string>)[e.type] ?? e.type;
}

// ---------------------------------------------------------------- critical window: blast -> cover

export function IncidentTimeline({ m }: { m: Metrics }) {
  const [ref, W] = useWidth<HTMLDivElement>();
  const i = m.incident;
  const LBL = 104, R = 44, ROW = 34;
  const span = Math.max(20, Math.ceil(Math.max(i.cover_s ?? 0, i.report_s ?? 0, i.kia_s ?? 0, 15) / 5) * 5 + 5);
  const iw = Math.max(1, W - LBL - R);
  const x = (s: number) => LBL + (Math.min(s, span) / span) * iw;

  type Row = { label: string; kind: "bar" | "dot" | "x"; from?: number | null; at: number | null; target?: number; tone: string };
  const rows: Row[] = [
    { label: "Freeze", kind: "bar", from: 0, at: i.first_action_s, tone: "var(--ink-3)" },
    { label: "Eyes on him", kind: "dot", at: i.assess_s, tone: "var(--ink-2)" },
    { label: "Radio report", kind: "dot", at: i.report_s, target: 5, tone: "var(--signal)" },
    { label: "Drag to rock", kind: "bar", from: i.drag_start_s, at: i.cover_s, target: 15, tone: "var(--signal)" },
  ];
  if (i.kia_s != null) rows.push({ label: "Shot (KIA)", kind: "x", at: i.kia_s, tone: "var(--bad)" });
  const H = rows.length * ROW + 26;

  return (
    <div ref={ref}>
      {W > 0 && (
        <svg width={W} height={H} role="img" aria-label="Seconds from the trip mine to each action">
          {niceTicks(0, span, span > 30 ? 10 : 5).map((t) => (
            <g key={t}>
              <line x1={x(t)} x2={x(t)} y1={4} y2={H - 22} stroke="var(--line)" />
              <text x={x(t)} y={H - 6} textAnchor="middle" fontSize="10" fill="var(--ink-3)" className="mono">{t === 0 ? "blast" : `+${t}s`}</text>
            </g>
          ))}
          {rows.map((r, k) => {
            const cy = 4 + k * ROW + ROW / 2;
            const missing = r.at == null;
            return (
              <g key={r.label}>
                <text x={0} y={cy + 4} fontSize="12" fill="var(--ink-2)">{r.label}</text>
                {r.target != null && (
                  <g>
                    <line x1={x(r.target)} x2={x(r.target)} y1={cy - 11} y2={cy + 11} stroke="var(--good)" strokeWidth="1.5" strokeDasharray="2 2" />
                  </g>
                )}
                {missing ? (
                  <text x={LBL} y={cy + 4} fontSize="11" fill="var(--bad)" className="mono">never</text>
                ) : r.kind === "bar" ? (
                  <g>
                    {r.from != null && <rect x={x(r.from)} y={cy - 5} width={Math.max(4, x(r.at!) - x(r.from))} height={10} rx={4} fill={r.tone} />}
                    <text x={x(r.at!) + 6} y={cy + 4} fontSize="11" fill="var(--ink)" className="mono halo">{secs(r.at)}s</text>
                  </g>
                ) : r.kind === "x" ? (
                  <g stroke={r.tone} strokeWidth="2.2" strokeLinecap="round">
                    <path d={`M${x(r.at!) - 5},${cy - 5}L${x(r.at!) + 5},${cy + 5}M${x(r.at!) + 5},${cy - 5}L${x(r.at!) - 5},${cy + 5}`} />
                    <text x={x(r.at!) + 9} y={cy + 4} fontSize="11" fill="var(--bad)" className="mono halo">{secs(r.at)}s</text>
                  </g>
                ) : (
                  <g>
                    <circle cx={x(r.at!)} cy={cy} r="5" fill={r.tone} stroke="var(--raised)" strokeWidth="2" />
                    <text x={x(r.at!) + 9} y={cy + 4} fontSize="11" fill="var(--ink)" className="mono halo">{secs(r.at)}s</text>
                  </g>
                )}
              </g>
            );
          })}
        </svg>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- every trigger pull

export function ShotStrip({ events, m }: { events: SessionEvent[]; m: Metrics }) {
  const [ref, W] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<SessionEvent | null>(null);
  const shots = events.filter((e) => e.type === "shot" && e.actor === "engineer");
  const H = 56, L = 4, R = 4;
  const dur = Math.max(1, m.duration_ms);
  const x = (t: number) => L + (t / dur) * (W - L - R);
  return (
    <div ref={ref} style={{ position: "relative" }} onPointerLeave={() => setHover(null)}>
      {W > 0 && (
        <svg width={W} height={H} role="img" aria-label={`${shots.length} shots, ${m.fire.hits} hits`}>
          {m.phases.map((p) => (
            <line key={p.id} x1={x(p.start_ms)} x2={x(p.start_ms)} y1={6} y2={H - 16} stroke="var(--line-2)" />
          ))}
          {m.incident.t0_ms != null && <line x1={x(m.incident.t0_ms)} x2={x(m.incident.t0_ms)} y1={2} y2={H - 16} stroke="var(--bad)" strokeWidth="1.5" />}
          <line x1={L} x2={W - R} y1={(H - 16) / 2 + 3} y2={(H - 16) / 2 + 3} stroke="var(--line)" />
          {shots.map((s, k) => {
            const hit = !!s.data.hit;
            const cy = hit ? 14 : 30;
            return (
              <circle key={k} cx={x(s.t_ms)} cy={cy} r={4}
                fill={hit ? "var(--signal)" : "var(--raised)"} stroke={s.data.friendly_risk ? "var(--bad)" : hit ? "var(--raised)" : "var(--ink-2)"} strokeWidth={hit ? 1.5 : 1.5}
                onPointerEnter={() => setHover(s)} style={{ cursor: "default" }} />
            );
          })}
          <text x={L} y={H - 3} fontSize="10" fill="var(--ink-3)" className="mono">0:00</text>
          <text x={W - R} y={H - 3} textAnchor="end" fontSize="10" fill="var(--ink-3)" className="mono">{clock(dur)}</text>
        </svg>
      )}
      {hover && (
        <div className="tip" style={{ left: Math.min(Math.max(x(hover.t_ms), 80), W - 80), top: hover.data.hit ? 12 : 28 }}>
          <span className="mono">{clock(hover.t_ms)}</span> · {hover.data.hit ? "Hit" : hover.data.target ? "Miss" : "No target"}
          {hover.data.from_cover ? " · from cover" : ""}
          <div className="faint mono">sway {String(hover.data.aim_sway_deg ?? "—")}°{hover.data.friendly_risk ? " · friendly in line" : ""}</div>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- why the shots missed (a prediction, not a diagnosis)

const BAND = {
  low: ["Low", "var(--bad)"], below: ["Below expected", "var(--warn)"], average: ["Average", "var(--signal)"],
  good: ["Good", "var(--good)"], few: ["Too few shots", "var(--ink-3)"],
} as const;
const BAND_STEPS = [["low", "Low", 30], ["below", "Below", 20], ["average", "Average", 20], ["good", "Good", 30]] as const;

const READ = {
  none: ["No concern", "Shot accuracy is in the normal range for this drill.", "var(--good)"],
  few: ["Not enough shots yet", "Fire at least 20 rounds for a reading.", "var(--ink-3)"],
  stress: ["Stress likely affected focus", "Most misses came while the heart rate was high. Slow the breathing in cover, then run it again.", "var(--bad)"],
  distance: ["Shots at distance were less accurate", "Near shots landed and far shots didn’t, while calm. Consider an eye check.", "var(--warn)"],
  fit: ["Check headset fit and glasses", "Calm, and near and far shots missed alike. Adjust the headset and lens spacing, wear glasses if you use them, and practise the controls.", "var(--signal)"],
  unknown: ["Not enough data to tell why", "Accuracy was low, but heart rate or target distance wasn’t logged for these shots.", "var(--ink-3)"],
} as const;

/** Rings with hits near the centre and misses outside; positions are a fixed scatter, only the counts are real. */
function Target({ hits, misses, size, seed, label }: { hits: number; misses: number; size: number; seed: number; label: string }) {
  let a = seed;
  const next = () => (a = (a * 16807) % 2147483647) / 2147483647;
  const dot = (hit: boolean) => {
    const ang = next() * Math.PI * 2, rad = hit ? Math.sqrt(next()) * 20 : 30 + next() * 15;
    return [50 + Math.cos(ang) * rad, 50 + Math.sin(ang) * rad] as const;
  };
  return (
    <svg width={size} height={size} viewBox="0 0 100 100" role="img" aria-label={label}>
      {[46, 34, 22, 10].map((r, i) => <circle key={r} cx="50" cy="50" r={r} fill={i % 2 ? "var(--raised)" : "var(--raised-2)"} stroke="var(--line-2)" />)}
      {Array.from({ length: misses }, (_, i) => { const [x, y] = dot(false); return <circle key={`m${i}`} cx={x} cy={y} r="2.6" fill="none" stroke="var(--ink-2)" strokeWidth="1.2" />; })}
      {Array.from({ length: hits }, (_, i) => { const [x, y] = dot(true); return <circle key={`h${i}`} cx={x} cy={y} r="3" fill="var(--signal)" />; })}
    </svg>
  );
}

function Word({ text, color }: { text: string; color: string }) {
  return <div className="display" style={{ fontSize: 26, color, marginTop: 10 }}>{text}</div>;
}

export function ShotRead({ m }: { m: Metrics }) {
  const r = m.shotRead;
  if (!r) return <p className="faint" style={{ fontSize: 13, margin: 0 }}>No shots fired this run.</p>;
  const [bandWord, bandColor] = BAND[r.band];
  const misses = [...r.misses].sort((x, y) => Number(!!y.stressed) - Number(!!x.stressed));
  const share = r.stressed_share;
  const stress = share == null ? null : share >= 0.6 ? ["Mostly under stress", "var(--bad)"] : share >= 0.4 ? ["Mixed", "var(--warn)"] : ["Mostly calm", "var(--good)"];
  const drop = r.range_drop;
  const range = drop == null ? null : drop >= 0.4 ? ["Far much worse", "var(--bad)"] : drop >= 0.15 ? ["Far a bit worse", "var(--warn)"] : ["About equal", "var(--good)"];
  const [title, body, tone] = READ[r.reason === "none" && r.band === "few" ? "few" : r.reason];
  return (
    <div>
      <div className="grid-3">
        <div>
          <div className="eyebrow">On target</div>
          <div style={{ display: "flex", justifyContent: "center", marginTop: 10 }}>
            <Target hits={m.fire.hits} misses={m.fire.shots - m.fire.hits} size={132} seed={7} label={`${m.fire.hits} of ${m.fire.shots} shots on target`} />
          </div>
          <Word text={bandWord} color={bandColor} />
          <div style={{ display: "flex", gap: 3, marginTop: 10 }}>
            {BAND_STEPS.map(([k, , w]) => <div key={k} style={{ flex: w, height: 8, borderRadius: 2, background: BAND[k][1], opacity: r.band === k ? 1 : 0.22 }} />)}
          </div>
          <div style={{ display: "flex", gap: 3, marginTop: 5, fontSize: 11 }} className="mono">
            {BAND_STEPS.map(([k, label, w]) => <span key={k} style={{ flex: w, color: r.band === k ? "var(--ink)" : "var(--ink-3)" }}>{label}</span>)}
          </div>
        </div>
        <div>
          <div className="eyebrow">When the shots missed</div>
          {r.misses.length === 0 ? <p className="faint" style={{ fontSize: 13 }}>No misses.</p>
            : share == null ? <p className="faint" style={{ fontSize: 13 }}>No heart rate at the moments of the misses.</p>
            : (
              <>
                <div style={{ display: "flex", flexWrap: "wrap", gap: 6, marginTop: 14 }} role="img" aria-label="One dot per missed shot, filled red when the heart rate was high">
                  {misses.map((x, i) => (
                    <span key={i} style={{
                      width: 13, height: 13, borderRadius: "50%",
                      background: x.stressed ? "var(--bad)" : "transparent",
                      border: x.stressed ? "none" : `2px solid ${x.stressed === false ? "var(--good)" : "var(--ink-3)"}`,
                    }} />
                  ))}
                </div>
                {stress && <Word text={stress[0]} color={stress[1]} />}
                <div style={{ display: "flex", gap: 14, marginTop: 8, fontSize: 12 }} className="muted">
                  <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}><span style={{ width: 9, height: 9, borderRadius: "50%", background: "var(--bad)" }} />Heart rate high</span>
                  <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}><span style={{ width: 9, height: 9, borderRadius: "50%", border: "2px solid var(--good)" }} />Calm</span>
                </div>
              </>
            )}
        </div>
        <div>
          <div className="eyebrow">Near vs far</div>
          {r.near && r.far ? (
            <>
              <div style={{ display: "flex", justifyContent: "space-around", marginTop: 10 }}>
                {([["Near", r.near, 3], ["Far", r.far, 5]] as const).map(([label, t, seed]) => (
                  <div key={label} style={{ textAlign: "center" }}>
                    <Target hits={t.hits} misses={t.shots - t.hits} size={96} seed={seed} label={`${label}: ${t.hits} of ${t.shots} on target`} />
                    <div className="eyebrow" style={{ marginTop: 4 }}>{label}</div>
                  </div>
                ))}
              </div>
              {range && <Word text={range[0]} color={range[1]} />}
            </>
          ) : <p className="faint" style={{ fontSize: 13 }}>{r.ranged ? "Not enough shots at each distance yet." : "Target distance isn’t logged for these shots yet."}</p>}
        </div>
      </div>
      <div style={{ display: "grid", gridTemplateColumns: "6px 1fr", gap: 14, marginTop: 18, paddingTop: 16, borderTop: "1px solid var(--line)" }}>
        <span style={{ background: tone, borderRadius: 3 }} />
        <div>
          <div style={{ fontWeight: 600 }}>{title}</div>
          <p className="muted" style={{ margin: "2px 0 0", fontSize: 14 }}>{body}</p>
          <p className="faint" style={{ margin: "6px 0 0", fontSize: 12 }}>A prediction from this session, not a medical diagnosis.</p>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- overall score across runs

export function Trend({ points }: { points: { score: number | null; failed: boolean; label: string }[] }) {
  const [ref, W] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);
  const H = 92, P = 10;
  const pts = points.filter((p) => p.score != null);
  if (pts.length < 2) return null;
  const x = (i: number) => P + (i / (pts.length - 1)) * (W - 2 * P);
  const y = (v: number) => P + (1 - v / 100) * (H - 2 * P - 12);
  const d = pts.map((p, i) => `${i ? "L" : "M"}${x(i)},${y(p.score!)}`).join("");
  return (
    <div ref={ref} style={{ position: "relative" }} onPointerLeave={() => setHover(null)}>
      {W > 0 && (
        <svg width={W} height={H} role="img" aria-label="Readiness score across recent runs">
          <line x1={P} x2={W - P} y1={y(70)} y2={y(70)} stroke="var(--good)" strokeDasharray="2 4" opacity="0.6" />
          <text x={P} y={y(70) - 5} fontSize="10" fill="var(--ink-3)" className="mono">ready 70</text>
          <path d={d} fill="none" stroke="var(--signal)" strokeWidth="2" strokeLinejoin="round" />
          {pts.map((p, i) => (
            <g key={i} onPointerEnter={() => setHover(i)}>
              <circle cx={x(i)} cy={y(p.score!)} r={14} fill="transparent" />
              <circle cx={x(i)} cy={y(p.score!)} r={4.5} fill={p.failed ? "var(--bad)" : "var(--signal)"} stroke="var(--raised)" strokeWidth="2" />
            </g>
          ))}
        </svg>
      )}
      {hover != null && (
        <div className="tip" style={{ left: Math.min(Math.max(x(hover), 60), W - 60), top: y(pts[hover].score!) }}>
          <strong className="mono">{pts[hover].score}</strong> · {pts[hover].label}{pts[hover].failed ? " · KIA" : ""}
        </div>
      )}
    </div>
  );
}
