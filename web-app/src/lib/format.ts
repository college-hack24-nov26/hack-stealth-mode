export const secs = (v: number | null | undefined, digits = 1) => (v == null ? "—" : v.toFixed(digits));

export function clock(ms: number) {
  const t = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, "0")}`;
}

export function when(iso: string) {
  const d = new Date(iso);
  return {
    day: d.toLocaleDateString("en-IN", { day: "numeric", month: "short" }),
    time: d.toLocaleTimeString("en-IN", { hour: "2-digit", minute: "2-digit", hour12: false }),
    weekday: d.toLocaleDateString("en-IN", { weekday: "short" }),
  };
}

export type Tone = "good" | "warn" | "bad";

/** Lower is better. `good` and `ok` are the cut-offs. */
export function toneLow(v: number | null | undefined, good: number, ok: number): Tone {
  if (v == null) return "bad";
  return v <= good ? "good" : v <= ok ? "warn" : "bad";
}
export function toneScore(v: number | null | undefined): Tone {
  if (v == null) return "bad";
  return v >= 70 ? "good" : v >= 45 ? "warn" : "bad";
}
export const pct = (v: number | null | undefined) => (v == null ? "—" : `${Math.round(v * 100)}`);
