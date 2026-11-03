using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// The Engineer's own voice from the headset microphone. No button: he just speaks, like a VOX radio.
    /// Plain level detection: 20 ms frames, a noise floor that follows the room (rain, gunfire leaking out of the
    /// speakers), and an onset once the level stays well above that floor for 250 ms. While our own radio or Comms'
    /// voice is playing it is deaf (that is speech too, coming out of the speakers). A call already under way isn't cut
    /// off by that. Words are not checked here: while the Captain is listening (Listen set) each call is streamed to the
    /// Mac as it is spoken, 8 kHz mu-law in 30 ms frames, with the 400 ms before the onset so the first word isn't lost:
    ///   [0xA0, utterance id, flags (bit 0 last frame, bits 1-2 context: 0 report, 1 sitrep), frame number, audio...]
    /// </summary>
    public class VoiceActivity : MonoBehaviour
    {
        public static bool Available { get; private set; }
        public static bool Speaking { get; private set; }
        public static float LastOnset { get; private set; } = -1f;
        public static float PeakSpeechLevel { get; private set; }
        public static float Floor { get; private set; }

        public System.Func<bool> deaf;

        /// <summary>What the Captain is waiting to hear ("report", "sitrep"); null: the voice stays on the headset.</summary>
        public static string Listen;

        const int Rate = 16000, Frame = 320;          // 20 ms
        const float MinLevel = 0.03f, OverFloor = 4f, Hold = 0.25f, Release = 0.6f, CallRelease = 1f, MaxCall = 12f;
        const int Packet = 240, PreRoll = 20;   // 30 ms of 8 kHz mu-law; 20 x 20 ms
        AudioClip mic; string device; int readPos;
        float floor = 0.05f, above, below;     // starts high and drops to the room in a second: no false start
        readonly float[] buf = new float[Frame];
        readonly Queue<byte[]> preRoll = new Queue<byte[]>();
        readonly List<byte> packet = new List<byte>(Packet);
        bool streaming, capped; byte utterance, frameNo, context; float streamed;

        public static VoiceActivity Begin(System.Func<bool> deaf)
        {
            var v = new GameObject("Voice activity (mic)").AddComponent<VoiceActivity>();
            v.deaf = deaf;
            Speaking = false; LastOnset = -1f; PeakSpeechLevel = 0; Listen = null;
            return v;
        }

        IEnumerator Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                for (float t = 0; t < 60f && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone); t += 0.5f)
                    yield return new WaitForSecondsRealtime(0.5f);
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                { Debug.LogWarning("[StealthMode] microphone permission denied: Engineer's voice not logged"); yield break; }
            }
#endif
            if (Microphone.devices.Length == 0) { Debug.LogWarning("[StealthMode] no microphone"); yield break; }
            device = Microphone.devices[0];
            mic = Microphone.Start(device, true, 1, Rate);
            for (float t = 0; t < 3f && Microphone.GetPosition(device) <= 0; t += Time.unscaledDeltaTime) yield return null;
            Available = Microphone.GetPosition(device) > 0;
            readPos = Microphone.GetPosition(device);
            Debug.Log("[StealthMode] microphone " + (Available ? "on: " + device : "not delivering"));
        }

        void Update()
        {
            if (!Available) return;
            int pos = Microphone.GetPosition(device);
            int avail = (pos - readPos + mic.samples) % mic.samples;
            while (avail >= Frame)
            {
                mic.GetData(buf, readPos);                  // wraps round the looping clip by itself
                readPos = (readPos + Frame) % mic.samples; avail -= Frame;
                float s = 0; for (int i = 0; i < Frame; i++) s += buf[i] * buf[i];
                Capture();
                Step(Mathf.Sqrt(s / Frame), Frame / (float)Rate);
                Stream(Frame / (float)Rate);
            }
        }

        // the 20 ms just read, down to 8 kHz mu-law: into the call if there is one, else into the pre-roll
        void Capture()
        {
            var f = new byte[Frame / 2];
            for (int i = 0; i < f.Length; i++) f[i] = Ulaw((buf[i * 2] + buf[i * 2 + 1]) * 0.5f);
            if (streaming) { Push(f); return; }
            preRoll.Enqueue(f);
            if (preRoll.Count > PreRoll) preRoll.Dequeue();
        }

        void Stream(float dt)
        {
            if (!Speaking) capped = false;
            if (!streaming)
            {
                if (!Speaking || Listen == null || capped) return;
                streaming = true; streamed = 0; utterance++; frameNo = 0; context = (byte)(Listen == "sitrep" ? 1 : 0);
                packet.Clear();
                while (preRoll.Count > 0) Push(preRoll.Dequeue());
                return;
            }
            streamed += dt;
            capped = streamed > MaxCall;
            if (Speaking && Listen != null && !capped) return;
            Send(true);
            streaming = false;
        }

        void Push(byte[] f)
        {
            foreach (var b in f)
            {
                packet.Add(b);
                if (packet.Count == Packet) Send(false);
            }
        }

        void Send(bool last)
        {
            var p = new byte[packet.Count + 4];
            p[0] = 0xA0; p[1] = utterance; p[2] = (byte)((last ? 1 : 0) | context << 1); p[3] = frameNo++;
            packet.CopyTo(p, 4);
            packet.Clear();
            HeartLink.Audio(p);
        }

        static byte Ulaw(float x)
        {
            int s = Mathf.Clamp((int)(x * 32767f), -32635, 32635), sign = s < 0 ? 0x80 : 0;
            if (s < 0) s = -s;
            s += 0x84;
            int exp = 7;
            for (int m = 0x4000; (s & m) == 0 && exp > 0; m >>= 1) exp--;
            return (byte)~(sign | exp << 4 | (s >> (exp + 3)) & 0x0F);
        }

        void Step(float rms, float dt)
        {
            // the floor drops quickly to quiet and climbs slowly, so a few seconds of talking don't drag it up
            floor = Mathf.Max(0.002f, Mathf.Lerp(floor, rms, rms < floor ? 0.2f : 0.002f));
            Floor = floor;
            bool isDeaf = deaf != null && deaf();
            bool loud = !isDeaf && rms > Mathf.Max(floor * OverFloor, MinLevel);
            if (loud) { above += dt; below = 0; }
            else
            {
                if (!(Speaking && isDeaf)) below += dt;     // the radio or a shot talking over him doesn't end his sentence
                if (below > 0.15f) above = 0;
            }
            if (!Speaking && above >= Hold)
            {
                Speaking = true; LastOnset = Time.time - above;
                Debug.Log($"[StealthMode] Engineer speaking (level {rms:0.000}, floor {floor:0.000})");
            }
            if (Speaking)
            {
                if (loud) PeakSpeechLevel = Mathf.Max(PeakSpeechLevel, rms);
                if (below > (Listen != null ? CallRelease : Release)) Speaking = false;
            }
        }

        void OnDestroy() { if (device != null) Microphone.End(device); Available = false; }
    }
}
