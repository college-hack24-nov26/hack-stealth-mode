using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// Live WHOOP heart rate from the Mac relay (mac/whoop_relay.py) over Wi-Fi, per the architecture contract:
    /// Mac -> Quest UDP 5005 {"hr":..,"level":0-3}, Quest -> Mac UDP 5006 events. Until the relay is heard the headset
    /// broadcasts a hello on 5006 so the relay learns its address. Every second the current heart rate is logged
    /// against the mission phase. Nothing is shown to the Engineer.
    /// </summary>
    public class HeartLink : MonoBehaviour
    {
        public const int HrPort = 5005, EventPort = 5006;

        public static HeartLink I { get; private set; }
        public int Hr => hr;                       // 0 = no data yet
        public int Level => level;
        public bool Live => hr > 0 && Time.realtimeSinceStartup - lastRx < 3f;

        volatile int hr, level;
        volatile float rxStamp;
        float lastRx = -100f, nextHello, nextSample;
        IPEndPoint mac;
        UdpClient rx, tx;
        Thread thread;
        volatile bool running;

        void Awake()
        {
            I = this;
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
                    int h = Field(msg, "\"hr\":"), l = Field(msg, "\"level\":");
                    if (h > 0) { hr = h; level = Mathf.Max(0, l); rxStamp = 1; mac = new IPEndPoint(any.Address, EventPort); }
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
            if (rxStamp > 0) { lastRx = Time.realtimeSinceStartup; rxStamp = 0; }
            if (!Live && Time.realtimeSinceStartup >= nextHello)
            {
                nextHello = Time.realtimeSinceStartup + 2f;
                Send("{\"type\":\"hello\",\"device\":\"" + SystemInfo.deviceModel + "\"}", true);
            }
            if (Time.time >= nextSample)
            {
                nextSample = Time.time + 1f;
                if (Live) OpeningMetrics.RecordHeart(hr, level);
            }
        }

        /// <summary>Mission start (t_ms = 0): from here on every event is timed from this moment and the Mac opens a debrief session.</summary>
        public static float MissionStart { get; private set; } = -1f;

        public static void StartMission()
        {
            MissionStart = Time.time;
            Event("mission_start", "system", "{}");
        }

        /// <summary>
        /// One event of the debrief contract (web-app/src/lib/scenario.ts): type, actor, data as a JSON object.
        /// The Mac stamps it with the heart rate at that moment and uploads it.
        /// </summary>
        public static void Event(string type, string actor = null, string dataJson = "{}")
        {
            if (I == null || MissionStart < 0) return;
            int tMs = Mathf.RoundToInt((Time.time - MissionStart) * 1000f);
            var a = actor == null ? "null" : "\"" + actor + "\"";
            I.Send($"{{\"type\":\"event\",\"ev\":\"{type}\",\"actor\":{a},\"t_ms\":{tMs},\"hr\":{I.hr},\"data\":{dataJson}}}", I.mac == null);
        }

        public static string Str(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        void Send(string json, bool broadcast)
        {
            if (tx == null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                tx.Send(bytes, bytes.Length, broadcast || mac == null ? new IPEndPoint(IPAddress.Broadcast, EventPort) : mac);
            }
            catch { }
        }

        void OnDestroy()
        {
            running = false;
            rx?.Close(); tx?.Close();
            if (I == this) I = null;
        }
    }
}
