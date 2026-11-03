// Pushes a simulated Scenario One run into Supabase through the same RPCs the Mac relay uses.
//   node --env-file=.env.local scripts/simulate-session.ts [--skill 0.7] [--fail] [--live] [--name "Arjun Mehta"]
// --live replays in real time (watch the debrief update); otherwise the run is written at once.

import { createClient } from "@supabase/supabase-js";
import { simulate } from "../src/lib/simulate.ts";

const arg = (k: string) => { const i = process.argv.indexOf(`--${k}`); return i > -1 ? process.argv[i + 1] : undefined; };
const flag = (k: string) => process.argv.includes(`--${k}`);

const url = process.env.NEXT_PUBLIC_SUPABASE_URL, anon = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY, key = process.env.STEALTH_DEVICE_KEY;
if (!url || !anon || !key) throw new Error("Set NEXT_PUBLIC_SUPABASE_URL, NEXT_PUBLIC_SUPABASE_ANON_KEY and STEALTH_DEVICE_KEY (see .env.example)");
const db = createClient(url, anon);

async function rpc<T>(fn: string, args: Record<string, unknown>): Promise<T> {
  const { data, error } = await db.rpc(fn, { p_key: key, ...args });
  if (error) throw new Error(`${fn}: ${error.message}`);
  return data as T;
}

const run = simulate({ seed: Date.now() % 100000, skill: Number(arg("skill") ?? 0.6), engagesInstead: flag("fail"), restingHr: 66 });
const trainee = await rpc<string>("upsert_trainee", { p_name: arg("name") ?? "Arjun Mehta", p_callsign: arg("callsign") ?? "BADGER", p_resting_hr: 66 });
const session = await rpc<string>("start_session", { p_trainee: trainee });
console.log(`session ${session}`);

if (flag("live")) {
  // Interleave events and HR in time order, flushing once a second like the relay does.
  const start = Date.now();
  let ei = 0, hi = 0;
  while (ei < run.events.length || hi < run.hr.length) {
    const now = Date.now() - start;
    const ev = []; const hr = [];
    while (ei < run.events.length && run.events[ei].t_ms <= now) ev.push(run.events[ei++]);
    while (hi < run.hr.length && run.hr[hi].t_ms <= now) hr.push(run.hr[hi++]);
    if (ev.length) await rpc("push_events", { p_session: session, p_events: ev });
    if (hr.length) await rpc("push_hr", { p_session: session, p_samples: hr });
    process.stdout.write(`\r${(now / 1000).toFixed(0)} s  events ${ei}/${run.events.length}`);
    await new Promise((r) => setTimeout(r, 1000));
  }
  process.stdout.write("\n");
} else {
  await rpc("push_events", { p_session: session, p_events: [...run.events].sort((a, b) => a.t_ms - b.t_ms) });
  await rpc("push_hr", { p_session: session, p_samples: run.hr });
}
await rpc("end_session", { p_session: session, p_outcome: run.outcome, p_reason: String(run.events.at(-1)?.data.reason ?? ""), p_duration_ms: run.duration_ms });
console.log(`done · ${run.outcome} · ${(run.duration_ms / 1000).toFixed(0)} s`);
