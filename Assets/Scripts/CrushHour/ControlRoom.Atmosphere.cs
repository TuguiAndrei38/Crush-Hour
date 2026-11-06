using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CrushHour
{
    /// <summary>
    /// Step 8: atmosphere and performance. A CCTV look on every screen (scanlines + vignette overlay, flickering
    /// noise), a red alarm beacon and light that pulse during an incident, sounds made in code (monitor hum, alarm),
    /// and an automatic quality step-down when frames get slow.
    /// </summary>
    public partial class ControlRoom
    {
        [Header("Atmosphere")]
        [Range(0f, 1f)] public float scanlineStrength = 0.35f;
        [Range(0f, 1f)] public float noiseStrength = 0.12f;
        [Range(0f, 1f)] public float humVolume = 0.08f, alarmVolume = 0.18f;

        [Header("Performance")]
        [Tooltip("Lower the CCTV refresh rates when the frame time stays above this (s; 1/40 = 40 fps).")]
        public float slowFrameTime = 1f / 40f;
        public bool adaptiveQuality = true;

        /// <summary>Set by the game every frame: an incident is on (the alarm beacon and siren run).</summary>
        [System.NonSerialized] public bool alarm;

        Transform overlayRoot;
        Material scanlineMaterial, noiseMaterial;
        Texture2D scanlineTexture, noiseTexture;
        Light alarmLight;
        Material alarmMaterial;
        AudioSource humSource, alarmSource;
        float smoothedFrame = 1f / 60f, slowFor, fastFor;
        int qualityLevel;   // 0 full, 1 reduced, 2 low
        static readonly float[] FeedFpsLevels = { 12f, 8f, 5f }, WallFpsLevels = { 30f, 24f, 15f }, MinimapFpsLevels = { 10f, 6f, 4f };

        /// <summary>Overlay quads in front of every screen (after BuildScreenSurfaces).</summary>
        void BuildScreenOverlays()
        {
            if (overlayRoot != null) Destroy(overlayRoot.gameObject);
            overlayRoot = new GameObject("Screen Overlays").transform;
            overlayRoot.SetParent(Root, true);
            EnsureOverlayMaterials();
            foreach (var s in screens)
            {
                var rot = Quaternion.LookRotation(-s.normal, s.up);
                OverlayQuad(s, scanlineMaterial, 0.0012f, rot);
                OverlayQuad(s, noiseMaterial, 0.0018f, rot);
            }
        }

        void OverlayQuad(ScreenSurface s, Material material, float lift, Quaternion rot)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(go.GetComponent<Collider>());
            go.name = $"{s.kind} overlay";
            go.transform.SetParent(overlayRoot, true);
            go.transform.SetPositionAndRotation(s.center + s.normal * lift, rot);
            go.transform.localScale = new Vector3(s.width, s.height, 1f);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.layer = RoomLayer;
        }

        void EnsureOverlayMaterials()
        {
            if (scanlineMaterial != null) return;
            // Scanlines (every other row darker) and a soft vignette, as alpha over black.
            const int w = 64, h = 256;
            scanlineTexture = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "CCTV scanlines" };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = x / (w - 1f) * 2f - 1f, v = y / (h - 1f) * 2f - 1f;
                    float vignette = Mathf.Clamp01((u * u * 0.6f + v * v * 0.8f) - 0.15f) * 0.9f;
                    float line = (y % 2 == 0) ? scanlineStrength : 0f;
                    px[y * w + x] = new Color(0f, 0f, 0f, Mathf.Clamp01(line + vignette));
                }
            scanlineTexture.SetPixels(px);
            scanlineTexture.Apply();
            scanlineMaterial = OverlayMaterial("CCTV scanlines", scanlineTexture, Color.black, false);

            // Noise: white speckles, the texture offset jumps every frame.
            const int n = 128;
            noiseTexture = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point, name = "CCTV noise" };
            var noise = new Color[n * n];
            var rng = new System.Random(11);
            for (int i = 0; i < noise.Length; i++)
            {
                float a = (float)rng.NextDouble();
                noise[i] = new Color(1f, 1f, 1f, a * a * noiseStrength);
            }
            noiseTexture.SetPixels(noise);
            noiseTexture.Apply();
            noiseMaterial = OverlayMaterial("CCTV noise", noiseTexture, Color.white, true);
            noiseMaterial.SetTextureScale("_BaseMap", new Vector2(4f, 3f));
        }

        Material OverlayMaterial(string name, Texture texture, Color color, bool glow)
        {
            var m = new Material(litMaterial != null ? litMaterial : new Material(Shader.Find("Universal Render Pipeline/Lit"))) { name = name };
            m.SetTexture("_BaseMap", texture);
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", 0f);
            if (glow)
            {
                m.EnableKeyword("_EMISSION");
                m.SetTexture("_EmissionMap", texture);
                m.SetColor("_EmissionColor", new Color(0.6f, 0.6f, 0.6f));
            }
            MakeTransparent(m);
            ownedBoardMaterials.Add(m);
            return m;
        }

        /// <summary>The red beacon on the front wall, a red light that sweeps the room, and the looping sounds. Once, after Build.</summary>
        void SetupAtmosphere()
        {
            if (roomShell != null)
                foreach (var r in roomShell.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null && mats[i].name.StartsWith("M_Room_Alarm"))
                        {
                            alarmMaterial ??= new Material(mats[i]) { name = "Alarm beacon" };
                            alarmMaterial.EnableKeyword("_EMISSION");
                            mats[i] = alarmMaterial;
                            r.sharedMaterials = mats;
                            if (alarmLight == null)
                            {
                                alarmLight = new GameObject("Alarm Light").AddComponent<Light>();
                                alarmLight.transform.SetParent(Root, false);
                                alarmLight.transform.position = r.bounds.center + Root.forward * 0.3f;
                                alarmLight.type = LightType.Spot;
                                alarmLight.spotAngle = 70f;
                                alarmLight.range = 16f;
                                alarmLight.color = new Color(1f, 0.08f, 0.04f);
                                alarmLight.intensity = 0f;
                                alarmLight.shadows = LightShadows.None;
                                alarmLight.cullingMask = 1 << RoomLayer;
                                alarmLight.gameObject.layer = RoomLayer;
                            }
                        }
                }
            if (alarmMaterial != null) ownedBoardMaterials.Add(alarmMaterial);

            humSource = gameObject.AddComponent<AudioSource>();
            humSource.clip = MakeHum();
            humSource.loop = true;
            humSource.spatialBlend = 0f;
            humSource.volume = humVolume;
            humSource.Play();
            alarmSource = gameObject.AddComponent<AudioSource>();
            alarmSource.clip = MakeSiren();
            alarmSource.loop = true;
            alarmSource.spatialBlend = 0f;
            alarmSource.volume = 0f;
        }

        void AnimateAtmosphere()
        {
            float now = Time.unscaledTime;
            if (noiseMaterial != null)
                noiseMaterial.SetTextureOffset("_BaseMap", new Vector2(Random.value, Random.value));

            // Beacon: a rotating red sweep and a pulsing glow while the incident is on.
            float pulse = alarm ? 0.5f + 0.5f * Mathf.Sin(now * Mathf.PI * 2f * 1.2f) : 0f;
            if (alarmLight != null)
            {
                alarmLight.intensity = alarm ? 6f + 10f * pulse : 0f;
                alarmLight.transform.rotation = Root.rotation * Quaternion.Euler(25f, now * 220f, 0f);
            }
            if (alarmMaterial != null) alarmMaterial.SetColor("_EmissionColor", new Color(1f, 0.08f, 0.04f) * (alarm ? 1f + 5f * pulse : 0f));

            if (alarmSource != null)
            {
                float target = alarm ? alarmVolume : 0f;
                alarmSource.volume = Mathf.MoveTowards(alarmSource.volume, target, Time.unscaledDeltaTime * 0.5f);
                if (alarmSource.volume > 0f && !alarmSource.isPlaying) alarmSource.Play();
                if (alarmSource.volume <= 0f && alarmSource.isPlaying) alarmSource.Stop();
            }
            if (humSource != null) humSource.volume = humVolume;

            AdaptQuality();
        }

        /// <summary>When frames stay slow for 2 s, step the CCTV/wall/minimap refresh down; back up after 8 s of fast frames.</summary>
        void AdaptQuality()
        {
            if (!adaptiveQuality) return;
            smoothedFrame = Mathf.Lerp(smoothedFrame, Time.unscaledDeltaTime, 0.05f);
            if (smoothedFrame > slowFrameTime) { slowFor += Time.unscaledDeltaTime; fastFor = 0f; }
            else if (smoothedFrame < slowFrameTime * 0.7f) { fastFor += Time.unscaledDeltaTime; slowFor = 0f; }
            int level = qualityLevel;
            if (slowFor > 2f && level < 2) { level++; slowFor = 0f; }
            if (fastFor > 8f && level > 0) { level--; fastFor = 0f; }
            if (level == qualityLevel) return;
            qualityLevel = level;
            feedFps = FeedFpsLevels[level];
            wallFps = WallFpsLevels[level];
            minimapFps = MinimapFpsLevels[level];
            maxFeedsPerFrame = level == 0 ? 3 : level == 1 ? 2 : 1;
            Debug.Log($"Crush Hour: control room quality {(level == 0 ? "full" : level == 1 ? "reduced" : "low")} " +
                      $"(desk feeds {feedFps} fps, wall {wallFps} fps, minimap {minimapFps} fps).");
        }

        /// <summary>CCTV cameras skip shadows (cheaper); the wall keeps them.</summary>
        static void CheapCamera(Camera cam)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null) return;
            data.renderShadows = false;
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
        }

        // ---------------------------------------------------------------- Sounds (made in code)

        static AudioClip MakeHum()
        {
            const int rate = 22050;
            int length = rate * 2;   // loops seamlessly: whole cycles of 50/100/150 Hz in 2 s
            var data = new float[length];
            var r = new System.Random(3);
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)rate;
                data[i] = 0.5f * Mathf.Sin(2f * Mathf.PI * 50f * t) + 0.3f * Mathf.Sin(2f * Mathf.PI * 100f * t)
                        + 0.12f * Mathf.Sin(2f * Mathf.PI * 150f * t) + 0.08f * ((float)r.NextDouble() * 2f - 1f);
            }
            var clip = AudioClip.Create("Monitor hum", length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static AudioClip MakeSiren()
        {
            const int rate = 22050;
            int length = rate * 2;   // two-tone: 0.5 s high, 0.5 s low, twice
            var data = new float[length];
            float phase = 0f;
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)rate;
                float f = Mathf.Repeat(t, 1f) < 0.5f ? 880f : 660f;
                phase += 2f * Mathf.PI * f / rate;
                float square = Mathf.Sin(phase) > 0f ? 1f : -1f;
                data[i] = (0.6f * Mathf.Sin(phase) + 0.15f * square) * 0.7f;
            }
            var clip = AudioClip.Create("Alarm", length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
