using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>
    /// Day or night for every level: <see cref="ApplyTimeOfDay"/> (called by NewGame, so every new round, and by the
    /// Options "Time of day" button) picks one (random, or forced) and sets the skybox, the sun (moonlight at night),
    /// the ambient light, distance fog in the horizon colour, the unlit ground's tint, the crowd shader's flat ambient,
    /// the landscape's night lights, and at night a floodlight on every light tower (with a warm light pool) and coloured
    /// washes over every stage; a tower's floodlight goes down with the tower.
    /// Both skies are panoramas made in code once per session (2048×1024, on the built-in Skybox/Panoramic shader, kept
    /// in builds by Editor/SkyboxShaderInclude.cs): day = blue gradient, soft cumulus clouds, the sun and its glow where
    /// the scene's sun is; night = stars (fewer in the city), a Milky Way with dust lanes, thin moonlit clouds that hide
    /// the stars behind them, the moon where the moonlight comes from, and a warm light-pollution glow (strong over the
    /// city). The lower half of each sky is the fog colour, so the landscape fades into it without a seam. Without the
    /// panoramic shader the day falls back to the procedural sky and the night to a dark procedural one.
    /// </summary>
    public partial class CrushHourGame
    {
        public enum TimeOfDay { Day, Night }

        const string TimeOfDayPref = "CrushHour.Options.TimeOfDay";
        /// <summary>Options: 0 = random every level, 1 = always day, 2 = always night.</summary>
        int timeOfDayChoice;
        TimeOfDay timeOfDay;
        bool IsNight => timeOfDay == TimeOfDay.Night;

        // The scene's own sun and sky, captured once and used for the day.
        Light sun;
        bool sunCaptured;
        Quaternion daySunRotation;
        Color daySunColor;
        float daySunIntensity, daySunShadowStrength;
        Material sceneSkybox, proceduralDaySky, panoramaSky, proceduralNightSky;
        static Texture2D dayPanorama, lightPoolTexture;
        static readonly Dictionary<bool, Texture2D> nightPanoramas = new();
        Material lightPoolMaterial;

        readonly List<Floodlight> floodlights = new();

        sealed class Floodlight
        {
            public Light light;
            public GameObject pool;
            public int tower = -1; // index into venue.lightTowers, -1 for stage lights
            public float baseIntensity;
        }

        static readonly Quaternion MoonRotation = Quaternion.Euler(38f, 150f, 0f);
        static readonly Color MoonColor = new(0.62f, 0.72f, 1f);
        const float MoonIntensity = 0.35f;
        static readonly Color NightGroundTint = new(0.36f, 0.4f, 0.54f);
        static readonly Color DayCrowdAmbient = new(0.45f, 0.47f, 0.52f), NightCrowdAmbient = new(0.26f, 0.3f, 0.42f);
        static readonly Color FloodColor = new(1f, 0.86f, 0.62f);

        // Horizon colours (also the fog colours and the lower half of the skies).
        static Color DayHorizon => SkyPanorama.DayHorizon;
        static Color NightHorizonRural => SkyPanorama.NightHorizonRural;
        static Color NightHorizonUrban => SkyPanorama.NightHorizonUrban;
        bool UrbanSky => CurrentScenery != Scenery.Countryside;

        static string TimeOfDayLabel(int choice) => choice switch { 1 => "DAY", 2 => "NIGHT", _ => "RANDOM" };

        /// <summary>Picks day or night for this level and lights the scene for it. Call after the venue views and the landscape exist.</summary>
        void ApplyTimeOfDay()
        {
            timeOfDay = timeOfDayChoice switch
            {
                1 => TimeOfDay.Day,
                2 => TimeOfDay.Night,
                _ => Random.value < 0.5f ? TimeOfDay.Day : TimeOfDay.Night,
            };
            CaptureSceneLighting();
            bool night = IsNight;

            // Sky.
            var sky = night ? NightSky() : DaySky();
            if (sky != null) RenderSettings.skybox = sky;

            // Sun or moon.
            if (sun != null)
            {
                sun.transform.rotation = night ? MoonRotation : daySunRotation;
                sun.color = night ? MoonColor : daySunColor;
                sun.intensity = night ? MoonIntensity : daySunIntensity;
                sun.shadowStrength = night ? 0.55f : daySunShadowStrength;
                RenderSettings.sun = sun;
            }

            // Ambient: fixed colours, so the look doesn't depend on baking the skybox's ambient probe.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            if (night)
            {
                RenderSettings.ambientSkyColor = UrbanSky ? new Color(0.16f, 0.14f, 0.2f) : new Color(0.11f, 0.13f, 0.22f);
                RenderSettings.ambientEquatorColor = new Color(0.08f, 0.08f, 0.12f);
                RenderSettings.ambientGroundColor = new Color(0.03f, 0.03f, 0.04f);
            }
            else
            {
                RenderSettings.ambientSkyColor = new Color(0.56f, 0.62f, 0.72f);
                RenderSettings.ambientEquatorColor = new Color(0.44f, 0.45f, 0.44f);
                RenderSettings.ambientGroundColor = new Color(0.22f, 0.2f, 0.18f);
            }

            // Distance fog in the horizon colour: the landscape fades into the sky. Starts beyond the venue at full zoom-out.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = night ? (UrbanSky ? NightHorizonUrban : NightHorizonRural) : DayHorizon;
            RenderSettings.fogStartDistance = night ? 130f : 170f;
            RenderSettings.fogEndDistance = night ? 520f : 580f;

            // The ground is unlit (the heatmap must stay readable), so it is tinted instead; the crowd shader has a flat ambient.
            if (groundMaterial != null) groundMaterial.color = night ? NightGroundTint : Color.white;
            foreach (var model in personModels)
            {
                if (model.animatedMaterials == null) continue;
                foreach (var material in model.animatedMaterials)
                    if (material != null && material.HasProperty("_Ambient")) material.SetColor("_Ambient", night ? NightCrowdAmbient : DayCrowdAmbient);
            }
            SetLandscapeNight(night);

            // Old lights: already gone with the previous world after NewGame, still there when Options switches it in the menu.
            foreach (var flood in floodlights)
            {
                if (flood.light != null) Destroy(flood.light.gameObject);
                if (flood.pool != null) Destroy(flood.pool);
            }
            floodlights.Clear();
            if (night) BuildFloodlights();
        }

        void CaptureSceneLighting()
        {
            if (sunCaptured) return;
            sunCaptured = true;
            sceneSkybox = RenderSettings.skybox;
            sun = RenderSettings.sun;
            if (sun == null)
                foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (light.type == LightType.Directional) { sun = light; break; }
            if (sun == null) return;
            daySunRotation = sun.transform.rotation;
            daySunColor = sun.color;
            daySunIntensity = sun.intensity;
            daySunShadowStrength = sun.shadowStrength;
        }

        // ---------------------------------------------------------------- Skies

        /// <summary>The panoramic skybox material (one, its texture swapped for day / night), or null without the shader.</summary>
        Material PanoramaSky(Texture2D texture)
        {
            if (panoramaSky == null)
            {
                var shader = Shader.Find("Skybox/Panoramic");
                if (shader == null) return null;
                panoramaSky = new Material(shader) { name = "Crush Hour Sky" };
                panoramaSky.DisableKeyword("_MAPPING_6_FRAMES_LAYOUT"); // latitude-longitude layout
                SetIfPresent(panoramaSky, "_Mapping", 1f);
                SetIfPresent(panoramaSky, "_ImageType", 0f);
                SetIfPresent(panoramaSky, "_Rotation", 0f);
                SetIfPresent(panoramaSky, "_Exposure", 1f);
            }
            panoramaSky.SetTexture("_MainTex", texture);
            return panoramaSky;
        }

        Material DaySky()
        {
            var sunDirection = sun != null ? -(daySunRotation * Vector3.forward) : new Vector3(0.3f, 0.6f, 0.4f).normalized;
            if (Shader.Find("Skybox/Panoramic") != null) return PanoramaSky(DayPanorama(sunDirection));
            return ProceduralDaySky();
        }

        Material NightSky()
        {
            if (Shader.Find("Skybox/Panoramic") != null) return PanoramaSky(NightPanorama(-(MoonRotation * Vector3.forward), UrbanSky));
            // No panoramic shader (stripped from a build): a dark procedural sky with the moon as its "sun".
            if (proceduralNightSky == null)
            {
                var day = ProceduralDaySky();
                if (day == null) return null;
                proceduralNightSky = new Material(day) { name = "Night Sky (procedural)" };
                SetIfPresent(proceduralNightSky, "_Exposure", 0.18f);
                SetIfPresent(proceduralNightSky, "_SunSize", 0.02f);
                if (proceduralNightSky.HasProperty("_SkyTint")) proceduralNightSky.SetColor("_SkyTint", new Color(0.18f, 0.22f, 0.4f));
            }
            return proceduralNightSky;
        }

        Material ProceduralDaySky()
        {
            if (proceduralDaySky != null) return proceduralDaySky;
            if (sceneSkybox != null && sceneSkybox.shader != null && sceneSkybox.shader.name == "Skybox/Procedural")
                proceduralDaySky = new Material(sceneSkybox) { name = "Day Sky (procedural)" };
            else
            {
                var shader = Shader.Find("Skybox/Procedural");
                if (shader == null) return sceneSkybox;
                proceduralDaySky = new Material(shader) { name = "Day Sky (procedural)" };
                proceduralDaySky.EnableKeyword("_SUNDISK_HIGH_QUALITY");
            }
            SetIfPresent(proceduralDaySky, "_SunSize", 0.035f);
            SetIfPresent(proceduralDaySky, "_AtmosphereThickness", 0.9f);
            SetIfPresent(proceduralDaySky, "_Exposure", 1.25f);
            if (proceduralDaySky.HasProperty("_SkyTint")) proceduralDaySky.SetColor("_SkyTint", new Color(0.45f, 0.55f, 0.7f));
            if (proceduralDaySky.HasProperty("_GroundColor")) proceduralDaySky.SetColor("_GroundColor", DayHorizon);
            return proceduralDaySky;
        }

        static void SetIfPresent(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        static Texture2D DayPanorama(Vector3 sunDirection)
        {
            if (dayPanorama == null) dayPanorama = MakeSky("Day Sky Panorama", SkyPanorama.Day(sunDirection));
            return dayPanorama;
        }

        static Texture2D NightPanorama(Vector3 moonDirection, bool urban)
        {
            if (nightPanoramas.TryGetValue(urban, out var cached) && cached != null) return cached;
            var texture = MakeSky(urban ? "Night Sky Panorama (city)" : "Night Sky Panorama", SkyPanorama.Night(moonDirection, urban));
            nightPanoramas[urban] = texture;
            return texture;
        }

        static Texture2D MakeSky(string name, Color[] pixels)
        {
            var texture = new Texture2D(SkyPanorama.Width, SkyPanorama.Height, TextureFormat.RGB24, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // ---------------------------------------------------------------- Night lights

        /// <summary>A floodlight on every light tower (aimed into the venue) and coloured lights over every stage.</summary>
        void BuildFloodlights()
        {
            var centre = new Vector2(W * 0.5f, H * 0.5f);
            for (int t = 0; t < venue.lightTowers.Count; t++)
            {
                var tower = venue.lightTowers[t];
                Vector2 foot = tower.rect.center;
                Vector2 toCentre = (centre - foot).sqrMagnitude > 1f ? (centre - foot).normalized : Vector2.up;
                Vector2 target = foot + toCentre * 14f;
                float top = Mathf.Max(6f, tower.height) + 1f;
                var position = new Vector3(foot.x, top, foot.y);
                var light = MakeSpotLight($"Floodlight {t + 1}", position, new Vector3(target.x, 0f, target.y), FloodColor, 320f, 75f, 105f);
                if (towerViews.TryGetValue(t, out var view) && view != null) light.transform.SetParent(view, true); // falls with the tower
                var pool = MakeLightPool($"Floodlight {t + 1} pool", target, 17f, FloodColor * 0.42f);
                floodlights.Add(new Floodlight { light = light, pool = pool, tower = t, baseIntensity = light.intensity });
            }

            // Stage lights: a magenta and a cyan wash at the front of every stage.
            var stageColors = new[] { new Color(1f, 0.25f, 0.75f), new Color(0.2f, 0.8f, 1f) };
            foreach (var landmark in venue.landmarks)
            {
                if (!landmark.name.Contains("Stage")) continue;
                var rect = landmark.rect;
                Vector2 front = new(rect.center.x, rect.center.y < centre.y ? rect.yMax + 4f : rect.yMin - 4f);
                for (int k = 0; k < stageColors.Length; k++)
                {
                    float offset = (k == 0 ? -0.25f : 0.25f) * rect.width;
                    var target = new Vector2(front.x + offset, front.y);
                    var source = new Vector3(rect.center.x + offset, landmark.height + 5f, rect.center.y);
                    var light = MakeSpotLight($"{landmark.name} light {k + 1}", source, new Vector3(target.x, 0f, target.y), stageColors[k], 160f, 40f, 70f);
                    var pool = MakeLightPool($"{landmark.name} pool {k + 1}", target, 9f, stageColors[k] * 0.3f);
                    floodlights.Add(new Floodlight { light = light, pool = pool, baseIntensity = light.intensity });
                }
            }
        }

        Light MakeSpotLight(string name, Vector3 position, Vector3 target, Color color, float intensity, float range, float angle)
        {
            var go = new GameObject(name);
            go.transform.SetParent(world, false);
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(target - position);
            var light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.spotAngle = angle;
            light.innerSpotAngle = angle * 0.55f;
            light.shadows = LightShadows.None;
            light.cullingMask &= ~(1 << ControlRoom.RoomLayer); // never through the control room's walls
            return light;
        }

        /// <summary>A warm glow on the (unlit) ground: an additive quad with a radial falloff, under people and props.</summary>
        GameObject MakeLightPool(string name, Vector2 centre, float radius, Color color)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(world, false);
            quad.transform.SetPositionAndRotation(new Vector3(centre.x, 0.03f, centre.y), Quaternion.Euler(90f, 0f, 0f));
            quad.transform.localScale = new Vector3(radius * 2f, radius * 2f, 1f);
            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var material = new Material(LightPoolMaterial()) { name = name };
            material.color = color;
            renderer.sharedMaterial = material;
            return quad;
        }

        Material LightPoolMaterial()
        {
            if (lightPoolMaterial != null) return lightPoolMaterial;
            if (lightPoolTexture == null)
            {
                const int size = 128;
                lightPoolTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    name = "Light Pool",
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave,
                };
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float r = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f).magnitude / (size * 0.5f);
                        float a = Mathf.Clamp01(1f - r);
                        a = a * a * (3f - 2f * a); // smooth falloff
                        pixels[y * size + x] = new Color(a, a, a, a);
                    }
                lightPoolTexture.SetPixels(pixels);
                lightPoolTexture.Apply(false, true);
            }
            lightPoolMaterial = unlitMaterial != null ? new Material(unlitMaterial) : new Material(FindShader("Universal Render Pipeline/Unlit"));
            lightPoolMaterial.name = "Light Pool (additive)";
            lightPoolMaterial.mainTexture = lightPoolTexture;
            // Additive transparent: colour × falloff added on top of the ground.
            if (lightPoolMaterial.HasProperty("_Surface")) lightPoolMaterial.SetFloat("_Surface", 1f);
            if (lightPoolMaterial.HasProperty("_Blend")) lightPoolMaterial.SetFloat("_Blend", 2f);
            lightPoolMaterial.SetFloat("_SrcBlend", (float)BlendMode.One);
            lightPoolMaterial.SetFloat("_DstBlend", (float)BlendMode.One);
            if (lightPoolMaterial.HasProperty("_SrcBlendAlpha")) lightPoolMaterial.SetFloat("_SrcBlendAlpha", (float)BlendMode.Zero);
            if (lightPoolMaterial.HasProperty("_DstBlendAlpha")) lightPoolMaterial.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
            lightPoolMaterial.SetFloat("_ZWrite", 0f);
            lightPoolMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            lightPoolMaterial.renderQueue = (int)RenderQueue.Transparent;
            return lightPoolMaterial;
        }

        /// <summary>Every frame at night: a swaying tower's floodlight flickers, a fallen one goes out.</summary>
        void UpdateFloodlights()
        {
            if (floodlights.Count == 0 || schedule == null) return;
            foreach (var flood in floodlights)
            {
                if (flood.tower < 0 || flood.light == null) continue;
                TowerCollapseIncident tower = null;
                foreach (var incident in schedule.incidents)
                    if (incident is TowerCollapseIncident t && t.towerIndex == flood.tower && t.Started) { tower = t; break; }
                if (tower == null) continue;
                if (tower.Collapsed)
                {
                    flood.light.enabled = false;
                    if (flood.pool != null) flood.pool.SetActive(false);
                    continue;
                }
                // Swaying: the lamp flickers harder as it leans.
                float flicker = Mathf.PerlinNoise(Time.time * 14f, flood.tower * 3.7f);
                bool on = flicker > tower.SwayProgress * 0.6f;
                flood.light.intensity = on ? flood.baseIntensity * (0.7f + 0.3f * flicker) : 0f;
                if (flood.pool != null) flood.pool.SetActive(on);
            }
        }
    }
}
