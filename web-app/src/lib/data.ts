"use client";

import { createClient, type SupabaseClient } from "@supabase/supabase-js";
import type { HrSample, Session, SessionEvent, Trainee } from "./scenario.ts";
import { simulate } from "./simulate.ts";

const url = process.env.NEXT_PUBLIC_SUPABASE_URL;
const anon = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY;

export const supabase: SupabaseClient | null = url && anon ? createClient(url, anon) : null;
/** No Supabase env at build time -> the app runs on generated demo sessions. */
export const isDemo = !supabase;

export interface Bundle { session: Session; events: SessionEvent[]; hr: HrSample[] }

// PostgREST caps a response at 1000 rows; page through anything that can grow.
async function fetchAll<T>(table: string, sessionId: string, select: string): Promise<T[]> {
  const out: T[] = [];
  for (let from = 0; ; from += 1000) {
    const { data, error } = await supabase!
      .from(table).select(select).eq("session_id", sessionId)
      .order("t_ms").range(from, from + 999);
    if (error) throw error;
    out.push(...(data as T[]));
    if (!data || data.length < 1000) return out;
  }
}

export async function listBundles(limit = 12): Promise<Bundle[]> {
  if (!supabase) return demoBundles();
  const { data, error } = await supabase
    .from("sessions").select("*, trainee:trainees(*)")
    .order("started_at", { ascending: false }).limit(limit);
  if (error) throw error;
  return Promise.all((data as Session[]).map(loadParts));
}

export async function getBundle(id: string): Promise<Bundle | null> {
  if (!supabase) return demoBundles().find((b) => b.session.id === id) ?? null;
  const { data, error } = await supabase.from("sessions").select("*, trainee:trainees(*)").eq("id", id).maybeSingle();
  if (error) throw error;
  return data ? loadParts(data as Session) : null;
}

async function loadParts(session: Session): Promise<Bundle> {
  const [events, hr] = await Promise.all([
    fetchAll<SessionEvent>("session_events", session.id, "t_ms,type,actor,data"),
    fetchAll<HrSample>("hr_samples", session.id, "t_ms,bpm,rr_ms"),
  ]);
  return { session, events, hr };
}

/** Streams new rows for a live session. Returns an unsubscribe. */
export function subscribe(
  id: string,
  on: { event?: (e: SessionEvent) => void; hr?: (h: HrSample) => void; session?: (s: Partial<Session>) => void },
) {
  if (!supabase) return () => {};
  const ch = supabase
    .channel(`session:${id}`)
    .on("postgres_changes", { event: "INSERT", schema: "public", table: "session_events", filter: `session_id=eq.${id}` },
      (p) => on.event?.(p.new as SessionEvent))
    .on("postgres_changes", { event: "INSERT", schema: "public", table: "hr_samples", filter: `session_id=eq.${id}` },
      (p) => on.hr?.(p.new as HrSample))
    .on("postgres_changes", { event: "UPDATE", schema: "public", table: "sessions", filter: `id=eq.${id}` },
      (p) => on.session?.(p.new as Session))
    .subscribe();
  return () => { supabase.removeChannel(ch); };
}

// ---------------------------------------------------------------- demo data

const DEMO_TRAINEE: Trainee = { id: "demo-trainee", name: "Arjun Mehta", callsign: "BADGER", resting_hr: 66 };
const DEMO_RUNS = [
  { skill: 0.18, engagesInstead: true, daysAgo: 6 },
  { skill: 0.22, daysAgo: 5 },
  { skill: 0.4, daysAgo: 4 },
  { skill: 0.35, engagesInstead: true, daysAgo: 3 },
  { skill: 0.62, daysAgo: 1 },
  { skill: 0.84, daysAgo: 0 },
];
let demoCache: Bundle[] | null = null;

function demoBundles(): Bundle[] {
  if (demoCache) return demoCache;
  const now = new Date("2026-11-03T15:40:00+05:30").getTime();
  demoCache = DEMO_RUNS.map((r, i) => {
    const sim = simulate({ seed: 101 + i * 7, skill: r.skill, engagesInstead: r.engagesInstead, restingHr: 66 });
    const started = new Date(now - r.daysAgo * 86400000 - (DEMO_RUNS.length - i) * 2400000);
    return {
      session: {
        id: `demo-${i + 1}`, trainee_id: DEMO_TRAINEE.id, scenario: "scenario_one", status: "complete",
        outcome: sim.outcome, end_reason: String(sim.events.at(-1)?.data.reason ?? ""),
        started_at: started.toISOString(), ended_at: new Date(started.getTime() + sim.duration_ms).toISOString(),
        duration_ms: sim.duration_ms, trainee: DEMO_TRAINEE,
      },
      events: sim.events,
      hr: sim.hr,
    } satisfies Bundle;
  }).reverse();
  return demoCache;
}
