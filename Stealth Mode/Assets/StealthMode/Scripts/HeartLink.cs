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
    /// Link to the Mac relay (mac/whoop_relay.py). On the headset it is Bluetooth LE (BleLink.java), so the venue Wi-Fi
    /// does not matter; in the editor, and as a fallback while Bluetooth is not connected, it is UDP over Wi-Fi:
    ///   Mac -> Quest  UDP 5005 / BLE notify  {"hr":..,"level":0-3} each second while a mission runs, and {"type":"ack","n":N}
    ///   Quest -> Mac  UDP 5006 / BLE write   hello (until the relay answers), beat every 2 s, and the mission events
    /// The relay is idle until the Engineer presses the right trigger: StartMission() sends mission_start, which wakes it
    /// (WHOOP connect + debrief session); EndMission() closes it. Every event carries a sequence number and is resent until
    /// the relay acks it, so a dropped UDP packet never loses a shot or a mission end. Nothing is shown to the Engineer.
    /// The same link carries his voice to the Captain: Audio() sends mic frames (VoiceActivity.cs), TakeReply() gets the
    /// Captain's decision back (mac/captain_agent.py).
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

        /// <summary>The Captain's answer to what the Engineer said: which radio clip to play, and what he understood.</summary>
        [System.Serializable]
        public class AgentReply { public int u, cover, comms, backup, done, tts; public string ctx, clip, intent, reply, heard; }
        [System.Serializable] class Message { public string type, t; public int u; }
        readonly ConcurrentQueue<string> replies = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<byte[]> speech = new ConcurrentQueue<byte[]>();
        readonly Dictionary<int, string> heard = new Dictionary<int, string>();
        static readonly List<Pending> pending = new List<Pending>();
        static int mid, seq, lastEventMs;
        static bool ended;

        volatile int hr, level;
        volatile bool rxFlag;
        float lastRx = -100f, nextHello, nextSample, nextBeat;
        volatile IPEndPoint mac;
        readonly ConcurrentQueue<int> acks = new ConcurrentQueue<int>();
        UdpClient rx, tx;
        Thread thread;
        volatile bool running;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject ble;
        bool bleWasUp;
        public bool BluetoothUp => ble != null && ble.Call<bool>("isConnected");
#else
        public bool BluetoothUp => false;
#endif

        void Awake()
        {
            I = this;
            MissionStart = -1f; ended = false; seq = 0; lastEventMs = 0; pending.Clear();
            try
            {
                rx = new UdpClient(new IPEndPoint(IPAddress.Any, HrPort));
                tx = new UdpClient { EnableBroadcast = true };
                running = true;
                thread = new Thread(Receive) { IsBackground = true };
                thread.Start();
            }
            catch (System.Exception e) { Debug.LogWarning("[StealthMode] heart link (Wi-Fi) off: " + e.Message); }
            StartBle();
        }

        void StartBle()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                foreach (var perm in new[] { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT" })
                    if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(perm)) UnityEngine.Android.Permission.RequestUserPermission(perm);
                using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = up.GetStatic<AndroidJavaObject>("currentActivity"))
                    ble = new AndroidJavaObject("com.stealthmode.BleLink", activity);
                ble.Call("start");
                Debug.Log("[StealthMode] Bluetooth link started");
            }
            catch (System.Exception e) { Debug.LogWarning("[StealthMode] Bluetooth link off: " + e.Message); }
#endif
        }

        void Receive()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    var data = rx.Receive(ref any);
                    mac = new IPEndPoint(any.Address, EventPort);   // whoever talks to us is the relay
                    if (data.Length > 4 && data[0] == 0xB0) speech.Enqueue(data);
                    else Handle(Encoding.UTF8.GetString(data));
                }
                catch { if (!running) return; }
            }
        }

        const char BleSpeech = '\u0001';    // BleLink.java: a frame of the Captain's voice, base64 after this mark

        // one message from the relay, from either transport (UDP thread or the Bluetooth poll): an ack or a heart-rate reading
        void Handle(string msg)
        {
            if (msg.Length > 0 && msg[0] == BleSpeech) { speech.Enqueue(System.Convert.FromBase64String(msg.Substring(1))); return; }
            if (msg.Contains("\"agent\"") || msg.Contains("\"said\"")) { replies.Enqueue(msg); return; }
            if (msg.Contains("\"ack\""))
            {
                int n = Field(msg, "\"n\":");
                if (n >= 0) acks.Enqueue(n);
                return;
            }
            int h = Field(msg, "\"hr\":"), l = Field(msg, "\"level\":");
            if (h > 0) { hr = h; level = Mathf.Max(0, l); rxFlag = true; }
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
#if UNITY_ANDROID && !UNITY_EDITOR
            if (ble != null)
            {
                string m;
                while ((m = ble.Call<string>("poll")) != null) Handle(m);
                bool up = ble.Call<bool>("isConnected");
                if (up != bleWasUp) { bleWasUp = up; Debug.Log("[StealthMode] Bluetooth " + (up ? "connected" : "lost: " + ble.Call<string>("getStatus"))); }
            }
#endif
            if (rxFlag) { lastRx = now; rxFlag = false; }
            while (speech.TryDequeue(out var frame)) RadioStream.Feed(frame);

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

        /// <summary>The next answer for this exchange, or null. TakeReply(null) just empties the queue.</summary>
        public static AgentReply TakeReply(string ctx)
        {
            if (I == null) return null;
            while (I.replies.TryDequeue(out var json))
            {
                var m = JsonUtility.FromJson<Message>(json);
                if (m.type == "said") { I.heard[m.u] = m.t; continue; }
                if (ctx == null) continue;
                var r = JsonUtility.FromJson<AgentReply>(json);
                I.heard.TryGetValue(r.u, out r.heard);
                if (r.ctx == ctx) return r;
            }
            return null;
        }

        /// <summary>One frame of the Engineer's voice for the Captain: binary, best effort, no ack (a gap is just a gap).</summary>
        public static void Audio(byte[] frame)
        {
            if (I == null || MissionStart < 0 || ended) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (I.ble != null && I.ble.Call<bool>("isConnected")) { I.ble.Call("sendAudio", System.Convert.ToBase64String(frame)); return; }
#endif
            if (I.tx == null) return;
            try { I.tx.Send(frame, frame.Length, I.mac ?? new IPEndPoint(IPAddress.Broadcast, EventPort)); } catch { }
        }

        /// <summary>Trigger pressed: t_ms = 0, and the relay wakes up (WHOOP connects, debrief session opens).</summary>
        public static void StartMission()
        {
            mid = Random.Range(1, int.MaxValue); seq = 0; lastEventMs = 0; ended = false; pending.Clear();
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
        public static void Event(string type, string actor = null, string dataJson = "{}", float at = -1f)
        {
            if (I == null || MissionStart < 0 || ended) return;
            int tMs = Mathf.Max(lastEventMs, Mathf.RoundToInt((Time.time - MissionStart) * 1000f));   // never earlier than the last event (the clock is gone at shutdown)
            lastEventMs = tMs;
            if (at >= 0) tMs = Mathf.RoundToInt((at - MissionStart) * 1000f);   // something that started earlier (a radio call), logged when it finished
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
#if UNITY_ANDROID && !UNITY_EDITOR
            if (ble != null && ble.Call<bool>("isConnected")) { ble.Call("send", json); return; }
#endif
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
#if UNITY_ANDROID && !UNITY_EDITOR
            try { ble?.Call("stop"); ble?.Dispose(); } catch { }
#endif
            rx?.Close(); tx?.Close();
            if (I == this) I = null;
            MissionStart = -1f;
        }
    }
}
