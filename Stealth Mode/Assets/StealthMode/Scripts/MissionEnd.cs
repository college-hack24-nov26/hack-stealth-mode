using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// The end of the run: the view dips to black, the compound goes (the camera draws only layer 5, as in the briefing), all
    /// sound stops and an after-action panel builds itself in front of the Engineer, one row at a time. Success: it floats in
    /// the real room (passthrough). Failure: it floats on black. The world is frozen behind it: nothing else can happen.
    /// </summary>
    public class MissionEnd : MonoBehaviour
    {
        const int Layer = 5;
        static readonly Color Paper = new Color(0.94f, 0.95f, 0.92f), Grey = new Color(0.60f, 0.64f, 0.66f),
            Slate = new Color(0.055f, 0.07f, 0.085f, 0.95f), Tile = new Color(0.115f, 0.14f, 0.165f, 1f),
            Good = new Color(0.27f, 0.85f, 0.50f), Warn = new Color(0.96f, 0.76f, 0.30f), Bad = new Color(1f, 0.33f, 0.31f);

        readonly List<GameObject> rows = new List<GameObject>();
        Font font;
        Material flat;

        /// <summary>veil: a black veil that is already fully closed (the failure), or null to dip to black here.</summary>
        public static IEnumerator Show(Camera cam, bool success, string reason, MeshRenderer veil = null)
        {
            if (veil == null)
            {
                veil = Briefing.Veil(cam);
                yield return Briefing.Fade(veil, 0f, 1f, 1.2f);
            }
            yield return new WaitForSecondsRealtime(0.3f);

            // the mission is over: nothing out there moves or makes a sound any more
            Time.timeScale = 0f;
            cam.cullingMask = 1 << Layer;
            foreach (var s in FindObjectsByType<AudioSource>(FindObjectsSortMode.None)) s.Stop();
            var lpf = cam.GetComponent<AudioLowPassFilter>();
            if (lpf) lpf.enabled = false;

            var end = new GameObject("Mission end").AddComponent<MissionEnd>();
            end.Build(cam.transform, success, reason);
            var sting = end.gameObject.AddComponent<AudioSource>();
            sting.clip = CombatFx.I.Clip(success ? "ui_mission_success" : "ui_mission_failed");
            sting.spatialBlend = 0; sting.bypassListenerEffects = true; sting.volume = 0.8f;
            if (sting.clip) sting.Play();
            Pulse(success ? 0.5f : 1f, success ? 0.15f : 0.4f);

            if (success) yield return Briefing.Fade(veil, 1f, 0f, 0.9f);
            foreach (var row in end.rows)
            {
                row.SetActive(true);
                Pulse(0.15f, 0.02f);
                yield return new WaitForSecondsRealtime(0.14f);
            }
        }

        static void Pulse(float amp, float dur)
        {
            foreach (var node in new[] { UnityEngine.XR.XRNode.LeftHand, UnityEngine.XR.XRNode.RightHand })
            {
                var d = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
                if (d.isValid) d.SendHapticImpulse(0, amp, dur);
            }
        }

        void Build(Transform head, bool success, string reason)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            flat = Resources.Load<Material>("UI/UiUnlit");

            // 1.2 m ahead at eye height, upright, wherever he is looking
            var f = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (f.sqrMagnitude < 0.01f) f = Vector3.forward;
            transform.position = head.position + f * 1.2f;
            transform.rotation = Quaternion.LookRotation(f, Vector3.up);
            gameObject.layer = Layer;

            var accent = success ? Good : Bad;
            Round(transform, 0, 0, 1.34f, 0.96f, 0.035f, Slate, 4100);
            Round(transform, 0, 0.462f, 1.27f, 0.009f, 0.004f, accent, 4120);
            Text(transform, -0.63f, 0.42f, "OPERATION ALPHA  -  AFTER-ACTION REPORT", 0.026f, Grey, TextAnchor.MiddleLeft);
            Text(transform, -0.63f, 0.345f, success ? "MISSION SUCCESSFUL" : "MISSION FAILED", 0.078f, accent, TextAnchor.MiddleLeft, true);
            Text(transform, -0.63f, 0.275f, reason, 0.033f, Paper, TextAnchor.MiddleLeft);

            float Secs(float v) => Mathf.Round(v * 10f) / 10f;
            var m = new List<(string label, string value, Color c)>();
            float report = OpeningMetrics.ReportLatency, cover = OpeningMetrics.SafeTime < 0 ? -1 : OpeningMetrics.SafeTime - OpeningMetrics.Beat6Time;
            m.Add(("RADIO REPORT", report < 0 ? "none" : $"{Secs(report):0.0} s", report < 0 ? Bad : report <= 10f ? Good : Warn));
            m.Add(("COMMS IN COVER", cover < 0 ? "not reached" : $"{Secs(cover):0.0} s", cover < 0 ? Bad : cover <= 30f ? Good : Warn));
            int hits = 0; foreach (var s in OpeningMetrics.Shots) if (s.target != null) hits++;
            int shots = OpeningMetrics.Shots.Count;
            m.Add(("ACCURACY", shots == 0 ? "no shots" : $"{Mathf.RoundToInt(100f * hits / shots)} %  ({hits}/{shots})", Paper));
            int kills = 0; foreach (var k in OpeningMetrics.KilledBy) if (k.Value == "Engineer") kills++;
            m.Add(("ENEMY DOWN BY YOU", kills.ToString(), Paper));
            int peak = 0; float sum = 0; foreach (var b in OpeningMetrics.Heart) { peak = Mathf.Max(peak, (int)b.hr); sum += b.hr; }
            m.Add(("HEART RATE  AVG / PEAK", peak == 0 ? "no data" : $"{Mathf.RoundToInt(sum / OpeningMetrics.Heart.Count)} / {peak} bpm", Paper));
            float exposed = OpeningMetrics.Beat6ExposedSeconds;
            m.Add(("EXPOSED AFTER THE BLAST", $"{Secs(exposed):0.0} s", exposed <= 12f ? Good : Warn));
            for (int i = 0; i < m.Count; i++)
                StatTile(-0.42f + (i % 3) * 0.42f, 0.155f - (i / 3) * 0.15f, m[i].label, m[i].value, m[i].c);

            bool sitrepHeard = OpeningMetrics.SitrepLatency >= 0, all = OpeningMetrics.SitrepCover && OpeningMetrics.SitrepComms && OpeningMetrics.SitrepBackup;
            Check(-0.135f, "Casualty reported on the radio",
                report < 0 ? "NOT REPORTED" : OpeningMetrics.ReportPrompted ? "AFTER PROMPT" : "UNPROMPTED",
                report < 0 ? Bad : OpeningMetrics.ReportPrompted ? Warn : Good);
            Check(-0.19f, "Comms dragged to cover",
                cover < 0 ? "NO" : OpeningMetrics.Grabs > 1 ? $"YES, {OpeningMetrics.Grabs} GRABS" : "YES", cover < 0 ? Bad : Good);
            if (OpeningMetrics.BombTime >= 0)
            {
                Check(-0.245f, "Sitrep to the Captain",
                    !sitrepHeard ? "NO SITREP" : all ? (OpeningMetrics.SitrepPrompted ? "COMPLETE, PROMPTED" : "COMPLETE") : "INCOMPLETE",
                    !sitrepHeard ? Bad : all && !OpeningMetrics.SitrepPrompted ? Good : Warn);
                Check(-0.30f, "Held fire on friendlies", OpeningMetrics.FriendlyHits == 0 ? "YES" : $"{OpeningMetrics.FriendlyHits} ROUNDS ON TEAM", OpeningMetrics.FriendlyHits == 0 ? Good : Bad);
            }
            else if (OpeningMetrics.OpenShots > 0)
                Check(-0.245f, "Shots fired from the open", OpeningMetrics.OpenShots.ToString(), Bad);

            var foot = Group(); rows.Add(foot.gameObject);
            Text(foot, 0, -0.415f, "Your full debrief is in the Stealth Mode app. You can take the headset off.", 0.028f, Grey, TextAnchor.MiddleCenter);
            foreach (var r in rows) r.SetActive(false);
        }

        void StatTile(float x, float y, string label, string value, Color c)
        {
            var t = Group(); rows.Add(t.gameObject);
            Round(t, x, y, 0.40f, 0.135f, 0.016f, Tile, 4110);
            Text(t, x - 0.18f, y + 0.038f, label, 0.025f, Grey, TextAnchor.MiddleLeft);
            Text(t, x - 0.18f, y - 0.018f, value, 0.046f, c, TextAnchor.MiddleLeft, true);
        }

        void Check(float y, string label, string state, Color c)
        {
            var t = Group(); rows.Add(t.gameObject);
            Round(t, -0.615f, y, 0.02f, 0.02f, 0.0099f, c, 4120);
            Text(t, -0.585f, y, label, 0.033f, Paper, TextAnchor.MiddleLeft);
            Text(t, 0.63f, y, state, 0.029f, c, TextAnchor.MiddleRight, true);
        }

        Transform Group()
        {
            var g = new GameObject("row") { layer = Layer };
            g.transform.SetParent(transform, false);
            return g.transform;
        }

        void Text(Transform parent, float x, float y, string s, float height, Color c, TextAnchor anchor, bool bold = false)
        {
            var go = new GameObject("text") { layer = Layer };
            go.transform.SetParent(parent, false); go.transform.localPosition = new Vector3(x, y, -0.004f);
            var tm = go.AddComponent<TextMesh>();
            int size = height < 0.04f ? 48 : 96;      // near the size it lands on the display: small text stays clean
            tm.font = font; tm.fontSize = size; tm.characterSize = height / (size * 0.1f);
            tm.text = s; tm.color = c; tm.anchor = anchor; tm.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            go.GetComponent<MeshRenderer>().sharedMaterial = new Material(font.material) { renderQueue = 4500 };   // ignores depth: always on top
        }

        void Round(Transform parent, float x, float y, float w, float h, float r, Color c, int queue)
        {
            var go = new GameObject("panel") { layer = Layer };
            go.transform.SetParent(parent, false); go.transform.localPosition = new Vector3(x, y, 0);
            go.AddComponent<MeshFilter>().sharedMesh = RoundedRect(w, h, r);
            var m = new Material(flat) { renderQueue = queue };
            m.SetTexture("_BaseMap", Texture2D.whiteTexture); m.SetColor("_BaseColor", c);
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = m; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // a fan from the centre round four quarter circles, facing -Z (toward the viewer)
        static Mesh RoundedRect(float w, float h, float r, int seg = 6)
        {
            var v = new List<Vector3> { Vector3.zero };
            var corners = new[] { new Vector2(w / 2 - r, h / 2 - r), new Vector2(-w / 2 + r, h / 2 - r), new Vector2(-w / 2 + r, -h / 2 + r), new Vector2(w / 2 - r, -h / 2 + r) };
            for (int c = 0; c < 4; c++)
                for (int i = 0; i <= seg; i++)
                {
                    float a = (c + i / (float)seg) * Mathf.PI / 2;
                    v.Add(new Vector3(corners[c].x + Mathf.Cos(a) * r, corners[c].y + Mathf.Sin(a) * r, 0));
                }
            var tri = new List<int>();
            for (int i = 1; i < v.Count; i++) { tri.Add(0); tri.Add(i == v.Count - 1 ? 1 : i + 1); tri.Add(i); }
            var mesh = new Mesh { name = "Rounded rect", vertices = v.ToArray(), uv = new Vector2[v.Count], triangles = tri.ToArray() };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
