using System.Collections;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// The Engineer's own voice from the headset microphone. No button: he just speaks, like a VOX radio.
    /// Plain level detection: 20 ms frames, a noise floor that follows the room (rain, gunfire leaking out of the
    /// speakers), and an onset once the level stays well above that floor for 250 ms. While our own radio or Comms'
    /// voice is playing it is deaf (that is speech too, coming out of the speakers). Words are not checked here.
    /// </summary>
    public class VoiceActivity : MonoBehaviour
    {
        public static bool Available { get; private set; }
        public static bool Speaking { get; private set; }
        public static float LastOnset { get; private set; } = -1f;
        public static float PeakSpeechLevel { get; private set; }
        public static float Floor { get; private set; }

        public System.Func<bool> deaf;

        const int Rate = 16000, Frame = 320;          // 20 ms
        const float MinLevel = 0.03f, OverFloor = 4f, Hold = 0.25f, Release = 0.6f;
        AudioClip mic; string device; int readPos;
        float floor = 0.01f, above, below;
        readonly float[] buf = new float[Frame];

        public static VoiceActivity Begin(System.Func<bool> deaf)
        {
            var v = new GameObject("Voice activity (mic)").AddComponent<VoiceActivity>();
            v.deaf = deaf;
            Speaking = false; LastOnset = -1f; PeakSpeechLevel = 0;
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
                Step(Mathf.Sqrt(s / Frame), Frame / (float)Rate);
            }
        }

        void Step(float rms, float dt)
        {
            // the floor drops quickly to quiet and climbs slowly, so a few seconds of talking don't drag it up
            floor = Mathf.Max(0.002f, Mathf.Lerp(floor, rms, rms < floor ? 0.2f : 0.002f));
            Floor = floor;
            bool loud = !(deaf != null && deaf()) && rms > Mathf.Max(floor * OverFloor, MinLevel);
            if (loud) { above += dt; below = 0; } else { below += dt; if (below > 0.15f) above = 0; }
            if (!Speaking && above >= Hold)
            {
                Speaking = true; LastOnset = Time.time - above;
                Debug.Log($"[StealthMode] Engineer speaking (level {rms:0.000}, floor {floor:0.000})");
            }
            if (Speaking)
            {
                if (loud) PeakSpeechLevel = Mathf.Max(PeakSpeechLevel, rms);
                if (below > Release) Speaking = false;
            }
        }

        void OnDestroy() { if (device != null) Microphone.End(device); Available = false; }
    }
}
