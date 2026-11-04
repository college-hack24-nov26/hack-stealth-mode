using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// The Captain's own words on the radio: speech made on the Mac (mac/captain_agent.py), arriving over the link as 8 kHz
    /// mu-law frames while it is being sent. It fills a clip and starts playing as soon as the rest will arrive in time, so a
    /// slow link costs a little delay, not gaps. Frames: [0xB0, utterance id, flags (bit 0 last), frame number, audio...].
    /// </summary>
    public class RadioStream : MonoBehaviour
    {
        const int Rate = 8000, MaxSamples = Rate * 16, FrameSamples = 240;
        const float StartBuffer = 0.4f;

        static RadioStream I;
        static readonly float[] Mu = BuildTable();

        AudioSource src;
        AudioClip clip;
        int utterance = -1, expected = -1, filled, total;
        bool complete, started;
        float firstFrame;

        /// <summary>The stream is audible right now (it counts as the radio being busy).</summary>
        public static bool Playing => I && I.src.isPlaying;
        /// <summary>The expected speech has begun playing.</summary>
        public static bool Started => I && I.started && I.utterance == I.expected;

        static float[] BuildTable()
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++)
            {
                int u = ~i & 0xFF;
                int mag = (((u & 0x0F) << 3) + 0x84) << ((u >> 4) & 7);
                float x = (mag - 0x84) / 32768f;
                t[i] = (u & 0x80) != 0 ? -x : x;
            }
            return t;
        }

        static RadioStream Get()
        {
            if (I) return I;
            I = new GameObject("Radio stream").AddComponent<RadioStream>();
            I.src = I.gameObject.AddComponent<AudioSource>();
            I.src.spatialBlend = 0; I.src.playOnAwake = false; I.src.dopplerLevel = 0;
            I.clip = AudioClip.Create("Captain", MaxSamples, 1, Rate, false);
            I.src.clip = I.clip;
            return I;
        }

        /// <summary>This reply's speech is frames long: play it when enough of it is here.</summary>
        public static void Expect(int utterance, int frames)
        {
            var s = Get();
            s.expected = utterance; s.total = frames * FrameSamples;
        }

        public static void Feed(byte[] frame)
        {
            var s = Get();
            int u = frame[1];
            if (u != s.utterance) s.Begin(u);
            int n = Mathf.Min(frame.Length - 4, MaxSamples - s.filled);
            if (n > 0)
            {
                var pcm = new float[n];
                for (int i = 0; i < n; i++) pcm[i] = Mu[frame[i + 4]];
                s.clip.SetData(pcm, s.filled);
                s.filled += n;
            }
            if ((frame[2] & 1) != 0) s.complete = true;
        }

        void Begin(int u)
        {
            src.Stop();
            clip.SetData(new float[MaxSamples], 0);
            utterance = u; filled = 0; complete = started = false; firstFrame = Time.unscaledTime;
        }

        void Update()
        {
            if (utterance != expected || filled == 0) return;
            if (!started)
            {
                // how fast it is really arriving decides when the rest will make it in time
                float rate = filled / Mathf.Max(Time.unscaledTime - firstFrame, 0.02f);
                float needed = Mathf.Max(total - filled, 0) / Mathf.Max(rate, 1f), lasts = total / (float)Rate;
                if (complete || (filled >= Rate * StartBuffer && needed < lasts - 0.2f))
                {
                    started = true; src.timeSamples = 0; src.Play();
                    Debug.Log($"[StealthMode] radio stream: {total / (float)Rate:0.0} s of speech, link at {rate / Rate:0.0}x real time");
                }
            }
            else if (src.isPlaying && complete && src.timeSamples >= filled) src.Stop();
        }
    }
}
