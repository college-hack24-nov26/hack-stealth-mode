using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace StealthMode
{
    /// <summary>
    /// Beat 3 weather: a storm rolls in over ~8 s and stays. Rain streaks fall around the Engineer's head (world-space, so
    /// moving your head moves through them), heavy rain on the ground fills the air, rain drums on the steel of Shipment-1
    /// right beside him, the light goes grey and flat, fog closes the far side of the compound, and lightning flashes with
    /// the thunder arriving 1.5-3 s later (a storm a few km off). Everything is built at runtime; nothing is shown as UI.
    /// </summary>
    public class Weather : MonoBehaviour
    {
        public Transform head;
        public Transform coverSteel;            // Shipment-1: the roof the rain drums on
        public float ramp = 8f;

        ParticleSystem rain;
        ParticleSystem.EmissionModule emission;
        AudioSource ground, steel, thunder;
        Light sun;
        float sunI0, fog0, intensity;
        Color sunC0, fogC0;
        SphericalHarmonicsL2 ambient0;
        Material sky;
        float sky0;

        const float RainRate = 2600f, FogStorm = 0.024f, SunStorm = 0.32f, AmbientStorm = 0.42f;
        static readonly Color SunStormColor = new Color(0.62f, 0.68f, 0.78f), FogStormColor = new Color(0.30f, 0.32f, 0.36f);

        public static Weather Begin(Transform head, Transform coverSteel)
        {
            var w = new GameObject("Weather").AddComponent<Weather>();
            w.head = head; w.coverSteel = coverSteel;
            return w;
        }

        IEnumerator Start()
        {
            sun = RenderSettings.sun;
            if (sun) { sunI0 = sun.intensity; sunC0 = sun.color; }
            fog0 = RenderSettings.fogDensity; fogC0 = RenderSettings.fogColor;
            ambient0 = RenderSettings.ambientProbe;
            if (RenderSettings.skybox)
            {
                sky = new Material(RenderSettings.skybox);          // a copy: the sky asset itself is never edited
                RenderSettings.skybox = sky;
                sky0 = sky.HasProperty("_Exposure") ? sky.GetFloat("_Exposure") : 1f;
            }
            BuildRain();
            ground = Loop("rain_loop", null, 0f);
            steel = Loop("rain_metal_loop", coverSteel, 1.6f);
            thunder = gameObject.AddComponent<AudioSource>(); thunder.spatialBlend = 0; thunder.playOnAwake = false;

            StartCoroutine(Thunder(0.5f, 0.45f, false));            // the first roll, before a drop has fallen
            yield return new WaitForSeconds(1.5f);
            for (float t = 0; t < ramp; t += Time.deltaTime)
            {
                intensity = Mathf.SmoothStep(0, 1, t / ramp);
                yield return null;
            }
            intensity = 1;
            yield return Thunder(0f, 1f, true);
            while (true)
            {
                yield return new WaitForSeconds(Random.Range(11f, 24f));
                yield return Thunder(0f, Random.Range(0.6f, 1f), Random.value < 0.75f);
            }
        }

        void LateUpdate()
        {
            if (head && rain) rain.transform.position = head.position + Vector3.up * 7f + head.forward * 1.5f;
            if (rain) emission.rateOverTime = RainRate * intensity;
            if (ground) ground.volume = 0.75f * intensity;
            if (steel) steel.volume = intensity;
            float k = intensity;
            if (sun) { sun.intensity = Mathf.Lerp(sunI0, sunI0 * SunStorm, k) + flash * 3f; sun.color = Color.Lerp(Color.Lerp(sunC0, SunStormColor, k), Color.white, flash); }
            RenderSettings.fogDensity = Mathf.Lerp(fog0, FogStorm, k);
            RenderSettings.fogColor = Color.Lerp(Color.Lerp(fogC0, FogStormColor, k), new Color(0.75f, 0.78f, 0.85f), flash);
            RenderSettings.ambientProbe = ambient0 * (Mathf.Lerp(1f, AmbientStorm, k) + flash * 1.6f);
            if (sky && sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", Mathf.Lerp(sky0, sky0 * 0.3f, k) + flash * 1.2f);
        }

        float flash;

        // lightning: two or three ragged flickers, then the thunder after the light has crossed the distance
        IEnumerator Thunder(float delay, float volume, bool lightning)
        {
            yield return new WaitForSeconds(delay);
            if (lightning)
            {
                int n = Random.Range(2, 4);
                for (int i = 0; i < n; i++)
                {
                    flash = Random.Range(0.6f, 1f); yield return new WaitForSeconds(Random.Range(0.04f, 0.09f));
                    flash = 0; yield return new WaitForSeconds(Random.Range(0.05f, 0.16f));
                }
                yield return new WaitForSeconds(Random.Range(1.5f, 3f));
            }
            var c = CombatFx.I.Clip("thunder");
            if (c) thunder.PlayOneShot(c, volume);
        }

        AudioSource Loop(string clip, Transform at, float minDist)
        {
            var go = new GameObject(clip); go.transform.SetParent(at ? at : transform, false);
            if (at) go.transform.localPosition = Vector3.up * 2.6f;     // on the roof of the container
            var a = go.AddComponent<AudioSource>();
            a.clip = CombatFx.I.Clip(clip); a.loop = true; a.volume = 0; a.playOnAwake = false;
            a.spatialBlend = at ? 1f : 0f; a.minDistance = Mathf.Max(minDist, 1f); a.maxDistance = 60f; a.rolloffMode = AudioRolloffMode.Logarithmic;
            a.dopplerLevel = 0;
            if (a.clip) { a.time = Random.Range(0f, a.clip.length * 0.9f); a.Play(); }
            return a;
        }

        void BuildRain()
        {
            var go = new GameObject("Rain");
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(90f, 0, 0);       // emit straight down
            rain = go.AddComponent<ParticleSystem>();
            rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = rain.main;
            main.loop = true; main.playOnAwake = false; main.maxParticles = 4000;
            main.startLifetime = 1.0f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(9f, 11f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.010f, 0.018f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            emission = rain.emission; emission.rateOverTime = 0;
            var sh = rain.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(18f, 18f, 0.2f);
            var vel = rain.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(1.0f, 1.8f); vel.y = new ParticleSystem.MinMaxCurve(0f, 0f); vel.z = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);   // wind from the south-west

            var tex = new Texture2D(4, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 64; y++) for (int x = 0; x < 4; x++)
            {
                float v = y / 63f, a = Mathf.Sin(v * Mathf.PI) * (x == 0 || x == 3 ? 0.35f : 1f);
                tex.SetPixel(x, y, new Color(1, 1, 1, a));
            }
            tex.Apply();
            var baseMat = Resources.Load<Material>("Fx/FxAlpha");
            var m = new Material(baseMat);
            m.SetTexture("_BaseMap", tex); m.SetColor("_BaseColor", new Color(0.78f, 0.82f, 0.88f, 0.38f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = m; r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.05f; r.lengthScale = 1f;
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            rain.Play();
        }

        void OnDestroy()
        {
            if (sun) { sun.intensity = sunI0; sun.color = sunC0; }
            RenderSettings.fogDensity = fog0; RenderSettings.fogColor = fogC0; RenderSettings.ambientProbe = ambient0;
        }
    }
}
