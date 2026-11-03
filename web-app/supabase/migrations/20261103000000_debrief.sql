-- Stealth Mode debrief: trainees, sessions, the event timeline and heart-rate stream.
-- Reads are open to the app (anon); every write goes through the device-keyed RPCs below,
-- so the Mac relay never needs the service-role key.

create table public.trainees (
  id          uuid primary key default gen_random_uuid(),
  name        text not null,
  callsign    text,
  role        text not null default 'engineer',
  resting_hr  smallint,
  created_at  timestamptz not null default now()
);

create table public.devices (
  id          uuid primary key default gen_random_uuid(),
  label       text not null,
  key_hash    text not null unique,
  created_at  timestamptz not null default now()
);

create table public.sessions (
  id          uuid primary key default gen_random_uuid(),
  trainee_id  uuid not null references public.trainees(id) on delete cascade,
  scenario    text not null default 'scenario_one',
  status      text not null default 'live' check (status in ('live', 'complete', 'aborted')),
  outcome     text check (outcome in ('success', 'failed')),
  end_reason  text,
  started_at  timestamptz not null default now(),
  ended_at    timestamptz,
  duration_ms integer,
  created_at  timestamptz not null default now()
);
create index sessions_trainee_started_idx on public.sessions (trainee_id, started_at desc);

-- t_ms is milliseconds since mission start (briefing begins at 0).
create table public.session_events (
  id          bigint generated always as identity primary key,
  session_id  uuid not null references public.sessions(id) on delete cascade,
  t_ms        integer not null,
  type        text not null,
  actor       text,
  data        jsonb not null default '{}'::jsonb,
  created_at  timestamptz not null default now()
);
create index session_events_session_t_idx on public.session_events (session_id, t_ms);

-- One row per WHOOP notification (~1 Hz). rr_ms holds the R-R intervals in that packet, if any.
create table public.hr_samples (
  session_id  uuid not null references public.sessions(id) on delete cascade,
  t_ms        integer not null,
  bpm         smallint not null,
  rr_ms       smallint[],
  primary key (session_id, t_ms)
);

alter table public.trainees       enable row level security;
alter table public.devices        enable row level security;
alter table public.sessions       enable row level security;
alter table public.session_events enable row level security;
alter table public.hr_samples     enable row level security;

create policy "read trainees" on public.trainees       for select to anon, authenticated using (true);
create policy "read sessions" on public.sessions       for select to anon, authenticated using (true);
create policy "read events"   on public.session_events for select to anon, authenticated using (true);
create policy "read hr"       on public.hr_samples     for select to anon, authenticated using (true);
-- devices: no policies, invisible to clients.

-- ---------------------------------------------------------------- ingest RPCs

create function public._device_ok(p_key text) returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (
    select 1 from public.devices
    where key_hash = encode(sha256(convert_to(p_key, 'UTF8')), 'hex')
  );
$$;
revoke all on function public._device_ok(text) from public, anon, authenticated;

create function public._require_device(p_key text) returns void
language plpgsql stable security definer set search_path = '' as $$
begin
  if not public._device_ok(p_key) then
    raise exception 'invalid device key' using errcode = '28000';
  end if;
end;
$$;
revoke all on function public._require_device(text) from public, anon, authenticated;

create function public.upsert_trainee(p_key text, p_name text, p_callsign text default null, p_resting_hr smallint default null)
returns uuid language plpgsql security definer set search_path = '' as $$
declare v_id uuid;
begin
  perform public._require_device(p_key);
  select id into v_id from public.trainees where lower(name) = lower(p_name) limit 1;
  if v_id is null then
    insert into public.trainees (name, callsign, resting_hr) values (p_name, p_callsign, p_resting_hr) returning id into v_id;
  else
    update public.trainees
       set callsign = coalesce(p_callsign, callsign), resting_hr = coalesce(p_resting_hr, resting_hr)
     where id = v_id;
  end if;
  return v_id;
end;
$$;

create function public.start_session(p_key text, p_trainee uuid, p_started_at timestamptz default now())
returns uuid language plpgsql security definer set search_path = '' as $$
declare v_id uuid;
begin
  perform public._require_device(p_key);
  insert into public.sessions (trainee_id, started_at) values (p_trainee, p_started_at) returning id into v_id;
  return v_id;
end;
$$;

-- p_events: [{ "t_ms": 116000, "type": "trip_mine", "actor": "communicator", "data": {...} }, ...]
create function public.push_events(p_key text, p_session uuid, p_events jsonb)
returns integer language plpgsql security definer set search_path = '' as $$
declare v_count integer;
begin
  perform public._require_device(p_key);
  insert into public.session_events (session_id, t_ms, type, actor, data)
  select p_session, (e->>'t_ms')::integer, e->>'type', e->>'actor', coalesce(e->'data', '{}'::jsonb)
  from jsonb_array_elements(p_events) e;
  get diagnostics v_count = row_count;
  return v_count;
end;
$$;

-- p_samples: [{ "t_ms": 1000, "bpm": 72, "rr_ms": [812, 830] }, ...]
create function public.push_hr(p_key text, p_session uuid, p_samples jsonb)
returns integer language plpgsql security definer set search_path = '' as $$
declare v_count integer;
begin
  perform public._require_device(p_key);
  insert into public.hr_samples (session_id, t_ms, bpm, rr_ms)
  select p_session, (s->>'t_ms')::integer, (s->>'bpm')::smallint,
         case when s ? 'rr_ms' and jsonb_typeof(s->'rr_ms') = 'array'
              then array(select jsonb_array_elements_text(s->'rr_ms')::smallint) end
  from jsonb_array_elements(p_samples) s
  on conflict (session_id, t_ms) do nothing;
  get diagnostics v_count = row_count;
  return v_count;
end;
$$;

create function public.end_session(p_key text, p_session uuid, p_outcome text, p_reason text default null, p_duration_ms integer default null)
returns void language plpgsql security definer set search_path = '' as $$
begin
  perform public._require_device(p_key);
  update public.sessions
     set status = 'complete', outcome = p_outcome, end_reason = p_reason, ended_at = now(),
         duration_ms = coalesce(p_duration_ms, (select max(t_ms) from public.session_events where session_id = p_session))
   where id = p_session;
end;
$$;

grant execute on function public.upsert_trainee(text, text, text, smallint)   to anon, authenticated;
grant execute on function public.start_session(text, uuid, timestamptz)       to anon, authenticated;
grant execute on function public.push_events(text, uuid, jsonb)                to anon, authenticated;
grant execute on function public.push_hr(text, uuid, jsonb)                    to anon, authenticated;
grant execute on function public.end_session(text, uuid, text, text, integer) to anon, authenticated;

-- Live view during a run.
alter publication supabase_realtime add table public.sessions, public.session_events, public.hr_samples;
