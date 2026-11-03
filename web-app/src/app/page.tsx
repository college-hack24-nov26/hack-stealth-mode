"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { isDemo, listBundles, type Bundle } from "@/lib/data";
import { computeMetrics } from "@/lib/metrics";
import { clock, secs, toneLow, when } from "@/lib/format";
import { OutcomePill, ScoreBadge } from "@/components/debrief";
import { Trend } from "@/components/charts";
import { Empty, Loading } from "@/components/states";

export default function Home() {
  const [bundles, setBundles] = useState<Bundle[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { listBundles().then(setBundles).catch((e) => setError(e.message ?? String(e))); }, []);

  const runs = useMemo(
    () => bundles?.map((b) => ({ b, m: computeMetrics(b.events, b.hr, b.session.status === "live" ? null : b.session.duration_ms) })),
    [bundles],
  );

  if (error) return <Empty title="Couldn’t reach the debrief server" body={error} />;
  if (!runs) return <Loading />;
  if (!runs.length) return <Empty title="No runs yet" body="Start Scenario One on the Quest. The debrief appears here the moment the session begins." />;

  const live = runs.find((r) => r.b.session.status === "live");
  const done = runs.filter((r) => r.b.session.status !== "live");
  const latest = done[0];
  const trainee = runs[0].b.session.trainee;

  return (
    <>
      <section style={{ paddingTop: 26 }}>
        <div className="eyebrow">{trainee ? `${trainee.name}${trainee.callsign ? ` · ${trainee.callsign}` : ""} · Engineer` : "Engineer"}</div>
        <h1 className="display" style={{ fontSize: "clamp(40px, 11vw, 72px)", margin: "8px 0 0" }}>After-action<br />review</h1>
        {isDemo && <p className="faint" style={{ fontSize: 13, margin: "10px 0 0" }}>Showing generated demo runs. Connect Supabase to see real sessions.</p>}
      </section>

      {live && (
        <Link href={`/session/?id=${live.b.session.id}`} className="card" style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginTop: 20, borderColor: "var(--signal)" }}>
          <div>
            <OutcomePill outcome={null} live />
            <div style={{ marginTop: 8, fontWeight: 600 }}>Run in progress</div>
            <div className="faint mono" style={{ fontSize: 12 }}>{clock(live.m.duration_ms)} elapsed</div>
          </div>
          <span className="mono t-signal" style={{ fontSize: 34 }}>{live.b.hr.at(-1)?.bpm ?? "—"}<span className="stat-unit">bpm</span></span>
        </Link>
      )}

      {latest && (
        <div className="grid-2" style={{ marginTop: 20 }}>
          <Link href={`/session/?id=${latest.b.session.id}`} className="card" style={{ display: "block" }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
              <span className="eyebrow">Latest run</span>
              <OutcomePill outcome={latest.b.session.outcome} />
            </div>
            <div style={{ display: "flex", alignItems: "flex-end", gap: 10, margin: "18px 0 6px" }}>
              <ScoreBadge v={latest.m.scores.overall} size={76} />
              <span className="faint" style={{ paddingBottom: 6, fontSize: 13 }}>/ 100<br />readiness</span>
            </div>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: "10px 16px", marginTop: 16 }}>
              <KeyFact label="Casualty to cover" v={latest.m.incident.cover_s} target={15} />
              <KeyFact label="Radio report" v={latest.m.incident.report_s} target={5} />
            </div>
            <div className="faint" style={{ fontSize: 12, marginTop: 14 }}>
              {when(latest.b.session.started_at).weekday} {when(latest.b.session.started_at).day} · {when(latest.b.session.started_at).time} · {clock(latest.m.duration_ms)}
            </div>
          </Link>

          <div className="card">
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <span className="eyebrow">Readiness trend</span>
              <span className="faint" style={{ fontSize: 12 }}>last {done.length} runs</span>
            </div>
            <div style={{ marginTop: 14 }}>
              <Trend points={[...done].reverse().map((r) => ({
                score: r.m.scores.overall, failed: r.m.kia,
                label: `${when(r.b.session.started_at).day} ${when(r.b.session.started_at).time}`,
              }))} />
            </div>
            <p className="faint" style={{ fontSize: 12, margin: "10px 0 0" }}>Red points are runs that ended KIA.</p>
          </div>
        </div>
      )}

      <section className="section">
        <div className="section-head"><h2 className="display section-title" style={{ margin: 0 }}>All runs</h2></div>
        <div>
          {runs.map(({ b, m }) => {
            const w = when(b.session.started_at);
            return (
              <Link key={b.session.id} href={`/session/?id=${b.session.id}`} className="row-link">
                <div style={{ display: "flex", gap: 14, alignItems: "center", minWidth: 0 }}>
                  <div className="mono" style={{ width: 46, textAlign: "center", lineHeight: 1.1 }}>
                    <div style={{ fontSize: 17 }}>{w.day.split(" ")[0]}</div>
                    <div className="faint" style={{ fontSize: 10, textTransform: "uppercase", letterSpacing: "0.1em" }}>{w.day.split(" ")[1]}</div>
                  </div>
                  <div style={{ minWidth: 0 }}>
                    <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                      <OutcomePill outcome={b.session.outcome} live={b.session.status === "live"} />
                      <span className="faint mono" style={{ fontSize: 12 }}>{w.time} · {clock(m.duration_ms)}</span>
                    </div>
                    <div className="muted" style={{ fontSize: 13, marginTop: 4, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
                      {m.kia ? `KIA ${secs(m.incident.kia_s)} s after the blast` : m.incident.cover_s != null ? `Cover ${secs(m.incident.cover_s)} s · report ${secs(m.incident.report_s)} s` : "In progress"}
                    </div>
                  </div>
                </div>
                <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                  <span className="mono" style={{ fontSize: 22 }}>{m.scores.overall ?? "—"}</span>
                  <svg width="8" height="12" viewBox="0 0 8 12" aria-hidden="true"><path d="M1.5 1l5 5-5 5" stroke="var(--ink-3)" strokeWidth="1.5" fill="none" /></svg>
                </div>
              </Link>
            );
          })}
        </div>
      </section>
    </>
  );
}

function KeyFact({ label, v, target }: { label: string; v: number | null; target: number }) {
  const tone = toneLow(v, target, target * 1.8);
  return (
    <div>
      <div className="faint" style={{ fontSize: 12 }}>{label}</div>
      <div className={`mono t-${tone}`} style={{ fontSize: 20 }}>{v == null ? "never" : `${secs(v)} s`}</div>
      <div className="faint" style={{ fontSize: 11 }}>target ≤ {target} s</div>
    </div>
  );
}
