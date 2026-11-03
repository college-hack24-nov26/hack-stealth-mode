import type { Check, Metrics, RadioTx } from "@/lib/metrics";
import { TEAM } from "@/lib/scenario";
import { clock, pct, secs, toneLow, type Tone } from "@/lib/format";

export function Stat({ label, value, unit, sub, tone }: { label: string; value: React.ReactNode; unit?: string; sub?: React.ReactNode; tone?: Tone }) {
  return (
    <div className="stat">
      <div className="stat-label">{label}</div>
      <div className={`stat-value ${tone ? `t-${tone}` : ""}`}>{value}{unit && <span className="stat-unit">{unit}</span>}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  );
}

export function OutcomePill({ outcome, live }: { outcome: string | null; live?: boolean }) {
  if (live) return <span className="pill pill-live"><span className="dot dot-pulse" />Live</span>;
  if (outcome === "success") return <span className="pill pill-good"><span className="dot" />Success</span>;
  if (outcome === "failed") return <span className="pill pill-bad"><span className="dot" />Failed</span>;
  return <span className="pill">Aborted</span>;
}

/** Plain-language read of the critical window. Deterministic, no generated prose. */
export function verdict(m: Metrics, live = false): string[] {
  const i = m.incident;
  if (live) return [i.t0_ms == null ? "Mission in progress. The debrief fills in as events arrive." : "Trip mine triggered. Scoring starts when the run ends."];
  if (i.t0_ms == null) return ["Run ended before the trip mine."];
  const out: string[] = [];
  if (m.kia) {
    out.push(`Engaged the last three instead of moving to cover — shot ${secs(i.kia_s)} s after the blast.`);
  } else if (i.cover_s != null) {
    out.push(`Communicator behind the rock in ${secs(i.cover_s)} s${i.cover_s <= 15 ? ", inside the 15 s target" : `, ${secs(i.cover_s - 15)} s over target`}.`);
  }
  if (i.report_s == null) out.push("Never called the casualty in on radio.");
  else {
    const missed = i.report_checks.filter((c) => !c.ok).map((c) => c.label.toLowerCase());
    out.push(`Casualty called in at +${secs(i.report_s)} s${missed.length ? ` — left out ${missed.join(", ")}` : ", complete report"}.`);
  }
  if (!m.kia) out.push(i.backup ? `Asked for backup ${secs(i.backup.latency_s)} s after the bomb call.` : "No backup request after the bomb went down.");
  return out;
}

const SCORE_ROWS = [
  ["decision", "Decision", "Moved the casualty instead of fighting"],
  ["comms", "Comms", "Report speed, content, backup call"],
  ["composure", "Composure", "Heart-rate spike, recovery, freeze"],
  ["marksmanship", "Marksmanship", "Accuracy, cover fire, steady aim"],
] as const;

export function ScoreRows({ m }: { m: Metrics }) {
  return (
    <div style={{ display: "grid", gap: 14 }}>
      {SCORE_ROWS.map(([k, label, note]) => {
        const v = m.scores[k];
        return (
          <div key={k}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <span style={{ fontWeight: 600 }}>{label}</span>
              <span className="mono" style={{ fontSize: 18 }}>{v ?? "—"}</span>
            </div>
            <div style={{ position: "relative", height: 6, background: "var(--line)", borderRadius: 4, margin: "6px 0 5px" }}>
              <div style={{ width: `${v ?? 0}%`, height: "100%", background: "var(--signal)", borderRadius: 4 }} />
              <div title="Ready: 70" style={{ position: "absolute", left: "70%", top: -3, bottom: -3, width: 1.5, background: "var(--ink-3)" }} />
            </div>
            <div className="faint" style={{ fontSize: 12 }}>{note}</div>
          </div>
        );
      })}
    </div>
  );
}

function Chips({ checks }: { checks: Check[] }) {
  return (
    <div style={{ display: "flex", flexWrap: "wrap", gap: 6, marginTop: 8 }}>
      {checks.map((c) => (
        <span key={c.label} className={`pill ${c.ok ? "pill-good" : "pill-bad"}`} style={{ textTransform: "none", letterSpacing: 0, fontSize: 12 }}>
          {c.ok ? "✓" : "✕"} {c.label}
        </span>
      ))}
    </div>
  );
}

const ICON: Record<Tone, string> = { good: "✓", warn: "!", bad: "✕" };

function CheckRow({ tag, title, value, target, tone, children }: { tag: string; title: string; value: string; target?: string; tone: Tone; children?: React.ReactNode }) {
  return (
    <div style={{ display: "grid", gridTemplateColumns: "28px 1fr", gap: 12, padding: "14px 0", borderTop: "1px solid var(--line)" }}>
      <span className={`mono t-${tone}`} aria-label={tone} style={{
        width: 28, height: 28, borderRadius: 8, display: "grid", placeItems: "center", fontSize: 13,
        background: `var(--${tone}-soft)`,
      }}>{ICON[tone]}</span>
      <div style={{ minWidth: 0 }}>
        <div style={{ display: "flex", justifyContent: "space-between", gap: 12, alignItems: "baseline" }}>
          <span><span className="mono faint" style={{ marginRight: 6 }}>{tag}</span>{title}</span>
          <span className={`mono t-${tone}`} style={{ whiteSpace: "nowrap", fontSize: 16 }}>{value}</span>
        </div>
        {target && <div className="faint" style={{ fontSize: 12, marginTop: 2 }}>{target}</div>}
        {children}
      </div>
    </div>
  );
}

/** The five things the script says to measure, (a)–(e). */
export function ScriptChecks({ m }: { m: Metrics }) {
  const i = m.incident;
  const sup = i.suppress;
  const supTone: Tone = !sup ? "bad" : sup.coverage >= 0.7 ? "good" : sup.coverage >= 0.4 ? "warn" : "bad";
  const bk = i.backup;
  const bkTone: Tone = !bk ? "bad" : bk.checks.every((c) => c.ok) && bk.latency_s <= 10 ? "good" : "warn";
  const engaged: Tone = m.kia ? "bad" : i.shots_before_drag > 3 ? "warn" : "good";
  return (
    <div>
      <CheckRow tag="a" title="Casualty reported on radio" value={i.report_s == null ? "none" : `${secs(i.report_s)} s`}
        target="Target ≤ 5 s from the blast, spoken aloud" tone={toneLow(i.report_s, 5, 10)}>
        {i.report_checks.length > 0 && <Chips checks={i.report_checks} />}
      </CheckRow>
      <CheckRow tag="b" title="Communicator dragged to cover" value={i.cover_s == null ? "never" : `${secs(i.cover_s)} s`}
        target={i.drag_start_s != null ? `Target ≤ 15 s · started pulling at +${secs(i.drag_start_s)} s` : "Target ≤ 15 s"} tone={toneLow(i.cover_s, 15, 25)} />
      <CheckRow tag="c" title="Took cover instead of engaging" value={m.kia ? "KIA" : i.shots_before_drag > 3 ? `${i.shots_before_drag} rds first` : "Yes"}
        target={m.kia ? `Engaged the three, shot at +${secs(i.kia_s)} s — mission failed` : i.shots_before_drag ? `${i.shots_before_drag} rounds fired before moving him` : "Moved before firing"} tone={engaged} />
      <CheckRow tag="d" title="Kept firing from cover" value={sup ? `${pct(sup.coverage)}%` : "—"}
        target={sup ? `${sup.shots} shots over ${secs(sup.window_s, 0)} s · longest silence ${secs(sup.longest_gap_s)} s` : "Never reached cover"} tone={supTone} />
      <CheckRow tag="e" title="Radioed cover + asked for backup" value={bk ? `${secs(bk.latency_s)} s` : "none"}
        target="After the bomb call · target ≤ 10 s" tone={bkTone}>
        {bk && <Chips checks={bk.checks} />}
      </CheckRow>
    </div>
  );
}

export function RadioLog({ tx }: { tx: RadioTx[] }) {
  return (
    <ol style={{ listStyle: "none", margin: 0, padding: 0 }}>
      {tx.map((r, k) => {
        const me = r.actor === "engineer";
        return (
          <li key={k} style={{
            display: "grid", gridTemplateColumns: "44px 1fr", gap: 10, padding: "10px 0 10px 10px",
            borderLeft: `2px solid ${me ? "var(--signal)" : "transparent"}`, borderTop: k ? "1px solid var(--line)" : undefined,
          }}>
            <span className="mono faint" style={{ fontSize: 12, paddingTop: 2 }}>{clock(r.t_ms)}</span>
            <div style={{ minWidth: 0 }}>
              <div style={{ display: "flex", justifyContent: "space-between", gap: 8 }}>
                <span style={{ fontWeight: 600, fontSize: 13, color: me ? "var(--signal)" : "var(--ink-2)" }}>
                  {TEAM[r.actor as keyof typeof TEAM] ?? r.actor}{me ? " · you" : ""}
                </span>
                <span className="mono faint" style={{ fontSize: 11 }}>{secs((r.end_ms - r.t_ms) / 1000)} s{me && r.wpm ? ` · ${r.wpm} wpm` : ""}</span>
              </div>
              <p style={{ margin: "2px 0 0", fontSize: 14, color: me ? "var(--ink)" : "var(--ink-2)" }}>“{r.transcript}”</p>
              {r.audio_url && <audio controls preload="none" src={r.audio_url} style={{ width: "100%", height: 32, marginTop: 6 }} />}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

export function ZoneBar({ zones }: { zones: { calm: number; elevated: number; high: number } }) {
  const parts = [
    ["calm", "Calm", "var(--good)"],
    ["elevated", "Elevated", "var(--warn)"],
    ["high", "High stress", "var(--bad)"],
  ] as const;
  return (
    <div>
      <div style={{ display: "flex", gap: 2, height: 10 }}>
        {parts.map(([k, , c]) => zones[k] > 0 && <div key={k} style={{ flex: zones[k], background: c, borderRadius: 3 }} />)}
      </div>
      <div style={{ display: "flex", gap: 14, marginTop: 8, fontSize: 12, flexWrap: "wrap" }}>
        {parts.map(([k, label, c]) => (
          <span key={k} className="muted" style={{ display: "inline-flex", alignItems: "center", gap: 6 }}>
            <span style={{ width: 8, height: 8, borderRadius: 2, background: c }} />{label} <span className="mono">{pct(zones[k])}%</span>
          </span>
        ))}
      </div>
    </div>
  );
}

export function ScoreBadge({ v, size = 64 }: { v: number | null; size?: number }) {
  return (
    <span className="mono" style={{ fontSize: size, lineHeight: 0.9, letterSpacing: "-0.04em", fontWeight: 500 }}>
      {v ?? "—"}
    </span>
  );
}
