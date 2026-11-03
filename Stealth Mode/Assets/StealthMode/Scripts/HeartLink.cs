using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// Link to the Mac relay (mac/whoop_relay.py) over Wi-Fi:
    ///   Mac -> Quest  UDP 5005  {"hr":..,"level":0-3} each second while a mission runs, and {"type":"ack","n":N}
    ///   Quest -> Mac  UDP 5006  hello (until the relay answers), beat every 2 s, and the mission events
    /// The relay is idle until the Engineer presses the right trigger: StartMission() sends mission_start, which wakes it
    /// (WHOOP connect + debrief session); EndMission() closes it. Every event carries a sequence number and is resent until
    /// the relay acks it, so a dropped UDP packet never loses a shot or a mission end. Nothing is shown to the Engineer.
    /// </summary>
    public class HeartLink : MonoBehaviour
    {
        public const int HrPort = 5005, EventPort = 5006;

        public static HeartLink I { get; private set; }
        public int Hr => hr;                       // 0 = no data yet
        public int Level => level;
        public bool Live => hr > 0 && Time.realtimeSinceStartup - lastRx < 3f;

        /// <summary>Game time of mission start (t_ms = 0); negative while no mission is running.</summary>
        public static float MissionStart { get; private set; } = -1f;

        class Pending { public int n; public string json; public float sent; public int tries; }
        static readonly List<Pending> pending = new List<Pending>();
        static int mid, seq;
        static bool ended;

        volatile int hr, level;
        volatile bool rxFlag;
        float lastRx = -100f, nextHello, nextSample, nextBeat;
        volatile IPEndPoint mac;
        readonly ConcurrentQueue<int> acks = new ConcurrentQueue<int>();
        UdpClient rx, tx;
        Thread thread;
        volatile bool running;

        void Awake()
        {
            I = this;
            MissionStart = -1f; ended = false; seq = 0; pending.Clear();
            try
            {
                rx = new UdpClient(new IPEndPoint(IPAddress.Any, HrPort));
                tx = new UdpClient { EnableBroadcast = true };
                running = true;
                thread = new Thread(Receive) { IsBackground = true };
                thread.Start();
            }
            catch (System.Exception e) { Debug.LogWarning("[StealthMode] heart link off: " + e.Message); }
        }

        void Receive()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    var data = rx.Receive(ref any);
                    var msg = Encoding.UTF8.GetString(data);
                    mac = new IPEndPoint(any.Address, EventPort);   // whoever talks to us is the relay
                    if (msg.Contains("\"ack\""))
                    {
                        int n = Field(msg, "\"n\":");
                        if (n >= 0) acks.Enqueue(n);
                        continue;
                    }
                    int h = Field(msg, "\"hr\":"), l = Field(msg, "\"level\":");
                    if (h > 0) { hr = h; level = Mathf.Max(0, l); rxFlag = true; }
                }
                catch { if (!running) return; }
            }
        }

        static int Field(string json, string key)
        {
            int i = json.IndexOf(key);
            if (i < 0) return -1;
            i += key.Length;
            while (i < json.Length && json[i] == ' ') i++;
            int v = 0; bool any = false;
            while (i < json.Length && char.IsDigit(json[i])) { v = v * 10 + (json[i] - '0'); i++; any = true; }
            return any ? v : -1;
        }

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (rxFlag) { lastRx = now; rxFlag = false; }

            while (acks.TryDequeue(out var n)) pending.RemoveAll(p => p.n == n);
            foreach (var p in pending)
                if (now - p.sent > 0.6f) { Send(p.json); p.sent = now; p.tries++; }
            pending.RemoveAll(p => p.tries > 25);

            if (MissionStart < 0 && !Live && now >= nextHello)
            {
                nextHello = now + 2f;
                Send("{\"type\":\"hello\",\"device\":\"" + SystemInfo.deviceModel + "\"}");
            }
            if (MissionStart >= 0 && !ended && now >= nextBeat)
            {
                nextBeat = now + 2f;
                Send($"{{\"type\":\"beat\",\"mid\":{mid}}}");   // keeps the relay's watchdog quiet
            }
            if (Time.time >= nextSample)
            {
                nextSample = Time.time + 1f;
                if (Live && MissionStart >= 0 && !ended) OpeningMetrics.RecordHeart(hr, level);
            }
        }

        /// <summary>Trigger pressed: t_ms = 0, and the relay wakes up (WHOOP connects, debrief session opens).</summary>
        public static void StartMission()
        {
            mid = Random.Range(1, int.MaxValue); seq = 0; ended = false; pending.Clear();
            MissionStart = Time.time;
            Event("mission_start", "system", "{}");
        }

        /// <summary>Script finished: closes the debrief session and lets the relay disconnect the WHOOP and go idle.</summary>
        public static void EndMission(string outcome, string reason)
        {
            if (I == null || MissionStart < 0 || ended) return;
            Event("mission_end", "system", $"{{\"outcome\":{Str(outcome)},\"reason\":{Str(reason)}}}");
            ended = true;
        }

        /// <summary>
        /// One event of the debrief contract (web-app/src/lib/scenario.ts): type, actor, data as a JSON object.
        /// Numbered, resent until the relay acks it; the relay stamps it with the heart rate at that moment.
        /// </summary>
        public static void Event(string type, string actor = null, string dataJson = "{}")
        {
            if (I == null || MissionStart < 0 || ended) return;
            int tMs = Mathf.RoundToInt((Time.time - MissionStart) * 1000f);
            var a = actor == null ? "null" : "\"" + actor + "\"";
            var p = new Pending
            {
                n = ++seq, sent = Time.realtimeSinceStartup,
                json = $"{{\"type\":\"event\",\"mid\":{mid},\"n\":{seq},\"ev\":\"{type}\",\"actor\":{a},\"t_ms\":{tMs},\"hr\":{I.hr},\"data\":{dataJson}}}"
            };
            pending.Add(p);
            I.Send(p.json);
        }

        public static string Str(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        void Send(string json)
        {
            if (tx == null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                var to = mac ?? new IPEndPoint(IPAddress.Broadcast, EventPort);   // until the relay answers, shout
                tx.Send(bytes, bytes.Length, to);
            }
            catch { }
        }

        void OnDestroy()
        {
            if (MissionStart >= 0 && !ended)
            {
                EndMission("failed", "aborted: app closed");
                for (int i = 0; i < 3; i++) foreach (var p in pending) Send(p.json);   // last words: no more frames to resend in
            }
            running = false;
            rx?.Close(); tx?.Close();
            if (I == this) I = null;
            MissionStart = -1f;
        }
    }
}
