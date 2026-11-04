# Stealth Mode — Debrief

Mobile-first after-action review for Scenario One (warehouse raid, trainee = Engineer).
Next.js static export on Cloudflare Pages; Supabase holds the timeline, heart rate and the ingest RPCs.

```
Quest ──Wi-Fi──▶ Mac relay ──RPC (device key)──▶ Supabase ──Realtime──▶ this app
WHOOP ──BLE────▶ Mac relay
```

The Quest only logs **what happened** (events with `t_ms` since mission start). Every score is computed
in `src/lib/metrics.ts`, so tuning a threshold never needs a new Quest build.

## Run

```bash
npm install
cp .env.example .env.local   # fill in the Supabase URL + anon key + device key
npm run dev                  # http://localhost:3000 — with no env it shows demo runs
```

Push a fake run through the real ingest path (also the reference for the Mac relay):

```bash
node --env-file=.env.local scripts/simulate-session.ts --skill 0.7 --live
node --env-file=.env.local scripts/simulate-session.ts --fail
```

## Deploy (Cloudflare)

Wrangler 4.15x routes Pages to Workers static assets; `wrangler.toml` serves `./out` with no Worker code.

```bash
npm run build && npx wrangler deploy
```

Live: https://stealth-mode-debrief.pulin-chaudhari.workers.dev

## Database

Applied to the **Stealth Mode** Supabase project `hykkqewhjapeebmytnov` (ap-south-1). The relay's device key is in `.env.local` only; the database stores its sha256.

`supabase/migrations/20261103000000_debrief.sql` — tables `trainees`, `sessions`, `session_events`,
`hr_samples`, `devices`. Clients can read; all writes go through `security definer` RPCs that check a
device key (`devices.key_hash` = sha256 of the key), so the relay never holds the service-role key.

| RPC | Args (all take `p_key`) |
|---|---|
| `upsert_trainee` | `p_name, p_callsign, p_resting_hr` → trainee id |
| `start_session`  | `p_trainee` → session id |
| `push_events`    | `p_session, p_events: [{t_ms, type, actor, data}]` |
| `push_hr`        | `p_session, p_samples: [{t_ms, bpm, rr_ms?}]` |
| `end_session`    | `p_session, p_outcome ('success'/'failed'), p_reason, p_duration_ms` |

## Events the Quest must send

Full contract with `data` fields: `src/lib/scenario.ts`. Minimum for the five script checks:

| Check | Events |
|---|---|
| (a) radio report speed | `trip_mine`, Engineer `radio` with `transcript` + `end_ms` (Whisper on the Mac) |
| (b) drag to cover | `drag_start`, `drag_end {reached_cover}` |
| (c) engaged instead → KIA | `shot`, `player_killed`, `mission_end {outcome:'failed'}` |
| (d) firing from cover | `shot {from_cover}` after `drag_end`, `bomb_defused` |
| (e) cover + backup call | Engineer `radio` after `bomb_defused` |

Extra metrics tracked beyond the script: freeze time after the blast, time to first look at the casualty,
report completeness (who / status / cause / own action), cover-me response, enemy-surge reaction, aim sway,
friendly-fire risk and no-target rounds, kill reports within 10 s, radio pace (wpm), HR spike, half-recovery,
time in stress zones, and a 0–100 readiness score split into Decision, Comms, Composure and
Marksmanship.
