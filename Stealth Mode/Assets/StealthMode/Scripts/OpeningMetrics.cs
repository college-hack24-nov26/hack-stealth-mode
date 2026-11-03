using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace StealthMode
{
    /// <summary>Engineer-only metrics for the opening engagement; written as JSON at the end of the run.</summary>
    public static class OpeningMetrics
    {
        public struct Shot { public float t; public string target; public string part; public float distance; }
        public struct Beat { public float hr; public int level; public float t; public string phase; }

        public static float SessionStart, MarkTime = -1, FirstShotTime = -1, RunnerInsideTime = -1, EndTime = -1;
        public static readonly List<Shot> Shots = new List<Shot>();
        public static readonly Dictionary<string, float> KillTimes = new Dictionary<string, float>();
        public static readonly Dictionary<string, string> KilledBy = new Dictionary<string, string>();
        public static float ExposedSeconds;   // head outside cover while enemies were firing
        public static float StartPressTime = -1, Beat1Time = -1, Beat1ExposedSeconds, RainTime = -1;
        public static string Phase = "gate";
        public static readonly List<Beat> Heart = new List<Beat>();   // live WHOOP, one sample a second
        public static readonly Dictionary<string, float> ExposedByBeat = new Dictionary<string, float>();

        // beat 4: Engineer and Comms each take one
        public static float Beat4Time = -1, CommsCornerTime = -1, DesignateTime = -1, EngineerKillTime = -1, KillCallLatency = -1;
        public static string Designated, EngineerKillTarget;
        public static bool CommsSecond, MicAvailable;
        // beat 5: Comms moves up under the Engineer's cover and trips the mine
        public static float Beat5Time = -1, TripTime = -1, LandingDistance = -1;
        public static int CoverShots, CoverHits;
        public static bool VoiceDuringAdvance;

        public static void Reset()
        {
            SessionStart = Time.time; MarkTime = FirstShotTime = RunnerInsideTime = EndTime = -1;
            Shots.Clear(); KillTimes.Clear(); KilledBy.Clear(); ExposedSeconds = 0;
            StartPressTime = Beat1Time = RainTime = -1; Beat1ExposedSeconds = 0; Heart.Clear(); ExposedByBeat.Clear();
            Beat4Time = CommsCornerTime = DesignateTime = EngineerKillTime = KillCallLatency = -1;
            Designated = EngineerKillTarget = null; CommsSecond = MicAvailable = false;
            Beat5Time = TripTime = LandingDistance = -1; CoverShots = CoverHits = 0; VoiceDuringAdvance = false;
        }

        public static void RecordShot(string target, string part, float distance)
        {
            if (FirstShotTime < 0) FirstShotTime = Time.time;
            Shots.Add(new Shot { t = Time.time, target = target, part = part, distance = distance });
            HeartLink.Event("shot", "engineer", $"{{\"hit\":{(target != null ? "true" : "false")},\"target\":{(target == null ? "null" : HeartLink.Str(target))},\"part\":{(part == null ? "null" : HeartLink.Str(part))}}}");
        }

        public static void RecordHeart(int hr, int level) => Heart.Add(new Beat { t = Time.time, hr = hr, level = level, phase = Phase });

        public static void RecordKill(string enemy, string by)
        {
            KillTimes[enemy] = Time.time;
            KilledBy[enemy] = by;
            var actor = by == "Engineer" ? "engineer" : by == "Sniper-1" ? "sniper_1" : by == "Sniper-2" ? "sniper_2" : by == "Comms" ? "communicator" : by.ToLowerInvariant();
            HeartLink.Event("enemy_down", actor, $"{{\"enemy\":{HeartLink.Str(enemy)}}}");
        }

        static string F(float v) => v < 0 ? "null" : v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        static float Rel(float t) => t < 0 || MarkTime < 0 ? -1 : t - MarkTime;

        public static string Save()
        {
            int hits = 0, heads = 0;
            foreach (var s in Shots) { if (s.target != null) hits++; if (s.part == "Head") heads++; }
            var sb = new StringBuilder();
            sb.Append("{\n  \"scenario\": \"opening\",\n");
            sb.Append($"  \"reaction_time_s\": {F(Rel(FirstShotTime))},\n");
            sb.Append($"  \"shots_fired\": {Shots.Count},\n  \"hits\": {hits},\n  \"headshots\": {heads},\n");
            sb.Append($"  \"accuracy\": {F(Shots.Count == 0 ? -1 : hits / (float)Shots.Count)},\n");
            sb.Append($"  \"exposed_s\": {F(ExposedSeconds)},\n");
            sb.Append($"  \"runner_inside_s\": {F(Rel(RunnerInsideTime))},\n");
            sb.Append($"  \"beat1_s\": {F(Rel(Beat1Time))},\n  \"beat1_exposed_s\": {F(Beat1ExposedSeconds)},\n  \"rain_s\": {F(Rel(RainTime))},\n");
            sb.Append("  \"exposed_by_beat_s\": {");
            int j = 0;
            foreach (var kv in ExposedByBeat) sb.Append($"{(j++ > 0 ? ", " : "")}\"{kv.Key}\": {F(kv.Value)}");
            sb.Append("},\n");
            string S(string v) => v == null ? "null" : "\"" + v + "\"";
            string B(bool v) => v ? "true" : "false";
            sb.Append($"  \"beat4\": {{\"t\": {F(Rel(Beat4Time))}, \"comms_at_corner_s\": {F(Rel(CommsCornerTime))}, \"designated\": {S(Designated)}, \"designated_s\": {F(Rel(DesignateTime))}, " +
                      $"\"engineer_kill\": {S(EngineerKillTarget)}, \"engineer_kill_after_call_s\": {F(EngineerKillTime < 0 || DesignateTime < 0 ? -1 : EngineerKillTime - DesignateTime)}, " +
                      $"\"kill_called_after_s\": {F(KillCallLatency)}, \"mic\": {B(MicAvailable)}, \"comms_took_second\": {B(CommsSecond)}}},\n");
            sb.Append($"  \"beat5\": {{\"t\": {F(Rel(Beat5Time))}, \"cover_shots\": {CoverShots}, \"cover_hits\": {CoverHits}, \"spoke_during_advance\": {B(VoiceDuringAdvance)}, " +
                      $"\"trip_s\": {F(Rel(TripTime))}, \"comms_landed_m_from_engineer\": {F(LandingDistance)}}},\n  \"kills\": {{");
            int i = 0;
            foreach (var kv in KillTimes)
                sb.Append($"{(i++ > 0 ? ", " : "")}\"{kv.Key}\": {{\"t\": {F(Rel(kv.Value))}, \"by\": \"{KilledBy[kv.Key]}\"}}");
            sb.Append("},\n  \"shots\": [");
            for (int k = 0; k < Shots.Count; k++)
            {
                var s = Shots[k];
                sb.Append($"{(k > 0 ? ", " : "")}{{\"t\": {F(Rel(s.t))}, \"target\": {(s.target == null ? "null" : "\"" + s.target + "\"")}, \"part\": {(s.part == null ? "null" : "\"" + s.part + "\"")}, \"dist_m\": {F(s.distance)}}}");
            }
            sb.Append("],\n  \"heart\": [");
            for (int k = 0; k < Heart.Count; k++)
                sb.Append($"{(k > 0 ? ", " : "")}{{\"t\": {F(Rel(Heart[k].t))}, \"hr\": {Heart[k].hr}, \"level\": {Heart[k].level}, \"phase\": \"{Heart[k].phase}\"}}");
            sb.Append("]\n}\n");
            var json = sb.ToString();
            var path = Path.Combine(Application.persistentDataPath, $"opening_{System.DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, json);
            Debug.Log("[StealthMode] metrics saved " + path + "\n" + json);
            return path;
        }
    }
}
