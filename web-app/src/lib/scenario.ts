// Scenario One — warehouse bomb raid. The trainee is always the Engineer.
// This file is the event contract shared by the Quest -> Mac relay, the simulator and the app.

export type Actor =
  | "engineer" | "captain" | "paramedic" | "communicator" | "sniper_1" | "sniper_2"
  | "enemy" | "system";

export const TEAM: Record<Exclude<Actor, "enemy" | "system">, string> = {
  captain: "Captain",
  paramedic: "Paramedic",
  engineer: "Engineer",
  communicator: "Communicator",
  sniper_1: "Sniper 1",
  sniper_2: "Sniper 2",
};

export type PhaseId =
  | "briefing" | "insertion" | "contact" | "escalation" | "rain" | "casualty" | "holdout" | "extraction";

export const PHASES: Record<PhaseId, string> = {
  briefing: "Briefing",
  insertion: "Insertion",
  contact: "First contact",
  escalation: "Enemy surge",
  rain: "Rain — snipers blind",
  casualty: "Communicator down",
  holdout: "Hold & cover",
  extraction: "Bomb down, sweep",
};

/**
 * Event types. `actor` says who did it; `data` carries the extras listed here.
 *
 * phase          data.id: PhaseId
 * shot           actor engineer. data.hit, data.target (enemy id | null), data.aim_sway_deg,
 *                data.from_cover, data.friendly_risk (muzzle within 3 m / 10° of a teammate)
 * enemy_down     data.enemy, by = actor
 * enemy_alert    5th outside enemy escapes and screams
 * enemy_wave     data.count — inside guards rush out
 * radio          data.end_ms, data.transcript, data.audio_url? — every transmission, all speakers
 * cover_request  Communicator asks the Engineer for cover
 * trip_mine      Communicator hits the mine (t0 of the critical window)
 * comrade_shout  Communicator shouts for help, not over radio
 * look_at        data.target — first gaze on a teammate (head ray hit)
 * drag_start / drag_end   data.reached_cover on drag_end
 * cover_enter / cover_exit
 * player_hit / player_killed
 * bomb_defused
 * mission_end    data.outcome: "success" | "failed", data.reason
 */
export type EventType =
  | "phase" | "shot" | "enemy_down" | "enemy_alert" | "enemy_wave" | "radio" | "rain_start"
  | "cover_request" | "trip_mine" | "comrade_shout" | "look_at" | "drag_start" | "drag_end"
  | "cover_enter" | "cover_exit" | "player_hit" | "player_killed" | "bomb_defused" | "mission_end";

export interface SessionEvent {
  t_ms: number;
  type: EventType;
  actor: Actor | null;
  data: Record<string, unknown>;
}

export interface HrSample {
  t_ms: number;
  bpm: number;
  rr_ms: number[] | null;
}

export interface Trainee {
  id: string;
  name: string;
  callsign: string | null;
  resting_hr: number | null;
}

export interface Session {
  id: string;
  trainee_id: string;
  scenario: string;
  status: "live" | "complete" | "aborted";
  outcome: "success" | "failed" | null;
  end_reason: string | null;
  started_at: string;
  ended_at: string | null;
  duration_ms: number | null;
  trainee?: Trainee;
}

// What a good report must contain. Matched on the Whisper transcript, lower-cased.
export const KEYWORDS = {
  casualty: ["down", "hit", "injured", "wounded", "casualty", "hurt", "bleeding", "man down"],
  cause: ["mine", "trip", "ied", "blast", "explosion", "explosive"],
  who: ["communicator", "comms", "radio man", "signaller"],
  action: ["cover", "pulling", "dragging", "moving him", "extract", "getting him"],
  shelter: ["cover", "rock", "behind", "safe", "protected", "secure"],
  backup: ["backup", "back up", "support", "reinforce", "need help", "assist", "medic", "paramedic"],
} as const;

export function mentions(transcript: string, words: readonly string[]): boolean {
  const t = ` ${transcript.toLowerCase()} `;
  return words.some((w) => t.includes(w));
}
