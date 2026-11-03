using System.Collections;
using UnityEngine;
using UnityEngine.Video;

namespace StealthMode
{
    /// <summary>
    /// The mission briefing (Aditi's video, played exactly as delivered) on a big curved screen floating in the room, the way a
    /// streaming app's theatre screen sits in the headset: ~90 degrees wide around the head at eye height, nothing of the
    /// compound drawn while it plays (passthrough around it on the Quest), sound straight in the ears.
    /// </summary>
    public static class Briefing
    {
        const string ClipPath = "Video/final-briefing";
        const int ScreenLayer = 5;                       // the only thing the camera draws while the briefing runs
        const float Radius = 3f, Arc = 90f;              // 4.7 m of screen wrapped around the viewer, 16:9

        public static IEnumerator Play(Camera cam, System.Action worldBack = null)
        {
            var clip = Resources.Load<VideoClip>(ClipPath);
            if (!clip) { Debug.LogWarning("[StealthMode] briefing video missing: Resources/" + ClipPath); worldBack?.Invoke(); yield break; }

            var head = cam.transform;
            var f = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (f.sqrMagnitude < 0.01f) f = Vector3.forward;
            var root = new GameObject("Briefing screen") { layer = ScreenLayer };
            root.transform.SetPositionAndRotation(head.position, Quaternion.LookRotation(f));

            var rt = new RenderTexture((int)clip.width, (int)clip.height, 0) { name = "Briefing video" };
            var mat = new Material(Resources.Load<Material>("UI/UiUnlit"));
            mat.SetTexture("_BaseMap", rt); mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Cull", 0f);
            float aspect = clip.width / (float)clip.height;
            root.AddComponent<MeshFilter>().sharedMesh = Curved(Radius, Arc, Radius * Arc * Mathf.Deg2Rad / aspect);
            var mr = root.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var sound = root.AddComponent<AudioSource>();
            sound.spatialBlend = 0; sound.playOnAwake = false; sound.bypassListenerEffects = true;
            var vp = root.AddComponent<VideoPlayer>();
            vp.playOnAwake = false; vp.isLooping = false; vp.clip = clip;
            vp.renderMode = VideoRenderMode.RenderTexture; vp.targetTexture = rt;
            vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
            vp.controlledAudioTrackCount = 1; vp.EnableAudioTrack(0, true); vp.SetTargetAudioSource(0, sound);
            bool ended = false;
            vp.loopPointReached += _ => ended = true;

            int mask = cam.cullingMask;
            cam.cullingMask = 1 << ScreenLayer;
            vp.Prepare();
            while (!vp.isPrepared) yield return null;
            vp.Play();
            Debug.Log("[StealthMode] briefing video playing (" + clip.length.ToString("0.0") + " s)");
            float t0 = Time.time;
            while (!ended && Time.time - t0 < (float)clip.length + 5f) yield return null;

            // out of the 3-2-1: the lights go down on the screen, the compound comes up out of the dark (the start prompt is
            // already waiting in it)
            var veil = Veil(cam);
            yield return Fade(veil, 0f, 1f, 0.7f);
            mr.enabled = false;
            cam.cullingMask = mask;
            worldBack?.Invoke();
            yield return new WaitForSecondsRealtime(0.25f);
            yield return Fade(veil, 1f, 0f, 1.6f);
            Object.Destroy(veil.gameObject); Object.Destroy(veil.sharedMaterial);
            Object.Destroy(root);
            rt.Release(); Object.Destroy(rt); Object.Destroy(mat);
            Debug.Log("[StealthMode] briefing video done");
        }

        // black over the whole view, a hand's width in front of the eyes, drawn after everything else
        internal static MeshRenderer Veil(Camera cam)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); Object.Destroy(q.GetComponent<Collider>());
            q.name = "Briefing fade"; q.layer = ScreenLayer;
            q.transform.SetParent(cam.transform, false); q.transform.localPosition = new Vector3(0, 0, 0.1f); q.transform.localScale = Vector3.one * 0.6f;
            var m = new Material(Resources.Load<Material>("UI/UiUnlit")) { renderQueue = 4000 };
            m.SetColor("_BaseColor", new Color(0, 0, 0, 0));
            var r = q.GetComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        internal static IEnumerator Fade(MeshRenderer veil, float from, float to, float seconds)
        {
            for (float t = 0; t < seconds; t += Time.unscaledDeltaTime)
            {
                veil.sharedMaterial.SetColor("_BaseColor", new Color(0, 0, 0, Mathf.Lerp(from, to, Mathf.SmoothStep(0, 1, t / seconds))));
                yield return null;
            }
            veil.sharedMaterial.SetColor("_BaseColor", new Color(0, 0, 0, to));
        }

        // a slice of a cylinder around the viewer, inside facing in, left edge u = 0
        static Mesh Curved(float r, float arcDeg, float h, int segs = 48)
        {
            var v = new Vector3[(segs + 1) * 2]; var uv = new Vector2[v.Length]; var tri = new int[segs * 6];
            for (int i = 0; i <= segs; i++)
            {
                float u = i / (float)segs, a = (u - 0.5f) * arcDeg * Mathf.Deg2Rad;
                var p = new Vector3(Mathf.Sin(a) * r, 0, Mathf.Cos(a) * r);
                v[i * 2] = p + Vector3.down * h * 0.5f; v[i * 2 + 1] = p + Vector3.up * h * 0.5f;
                uv[i * 2] = new Vector2(u, 0); uv[i * 2 + 1] = new Vector2(u, 1);
                if (i == segs) continue;
                int k = i * 6, b = i * 2;
                tri[k] = b; tri[k + 1] = b + 1; tri[k + 2] = b + 2; tri[k + 3] = b + 2; tri[k + 4] = b + 1; tri[k + 5] = b + 3;
            }
            var m = new Mesh { name = "Curved screen", vertices = v, uv = uv, triangles = tri };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }
    }
}
