using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// The sky panoramas, as pixels (equirectangular, 2048×1024, row 0 = bottom; the layout of Skybox/Panoramic:
    /// u = 0.5 − atan2(z, x)/2π, v = 1 − acos(y)/π). Pure code (managed Mathf/Color/Vector3 and
    /// <see cref="CrowdSimulation.SmoothNoise"/> only), so it runs headless; CrushHourGame.Sky.cs turns the pixels into
    /// textures. The lower half of each sky is the horizon/fog colour.
    /// </summary>
    public static class SkyPanorama
    {
        public static readonly Color DayHorizon = new(0.74f, 0.82f, 0.9f), DayZenith = new(0.24f, 0.47f, 0.83f);
        public static readonly Color NightHorizonRural = new(0.13f, 0.12f, 0.19f), NightHorizonUrban = new(0.24f, 0.16f, 0.13f);

        public const int Width = 2048, Height = 1024;
        const int SkyWidth = Width, SkyHeight = Height;

        /// <summary>World direction of a panorama pixel (Skybox/Panoramic: u = 0.5 − atan2(z, x)/2π, v = 1 − acos(y)/π).</summary>
        static Vector3 SkyDirection(int x, int y)
        {
            float u = (x + 0.5f) / SkyWidth, v = (y + 0.5f) / SkyHeight;
            float latitude = (1f - v) * Mathf.PI, longitude = (0.5f - u) * 2f * Mathf.PI;
            float ring = Mathf.Sin(latitude);
            return new Vector3(ring * Mathf.Cos(longitude), Mathf.Cos(latitude), ring * Mathf.Sin(longitude));
        }

        static float Fbm(float x, float y, int octaves)
        {
            float sum = 0f, amplitude = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += CrowdSimulation.SmoothNoise(x, y) * amplitude;
                norm += amplitude;
                x = x * 2.03f + 17.1f;
                y = y * 2.03f + 9.7f;
                amplitude *= 0.5f;
            }
            return sum / norm;
        }

        static float SmoothStep01(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Clouds on a plane overhead: coordinates of a direction on it (spread out near the horizon, faded there).</summary>
        static Vector2 CloudPlane(Vector3 dir, float scale) => new Vector2(dir.x, dir.z) / Mathf.Max(dir.y + 0.12f, 0.12f) * scale;

        /// <summary>Day sky pixels (row 0 = bottom), sun at <paramref name="sunDirection"/> (world, towards the sun).</summary>
        public static Color[] Day(Vector3 sunDirection)
        {
            sunDirection.Normalize();
            var pixels = new Color[SkyWidth * SkyHeight];
            var sunColor = new Color(1f, 0.93f, 0.8f);
            for (int y = 0; y < SkyHeight; y++)
                for (int x = 0; x < SkyWidth; x++)
                {
                    var dir = SkyDirection(x, y);
                    if (dir.y <= 0f)
                    {
                        pixels[y * SkyWidth + x] = DayHorizon;
                        continue;
                    }
                    var c = Color.Lerp(DayHorizon, DayZenith, Mathf.Pow(dir.y, 0.62f));
                    float sunCos = Mathf.Max(0f, Vector3.Dot(dir, sunDirection));
                    c += sunColor * (Mathf.Pow(sunCos, 60f) * 0.28f + Mathf.Pow(sunCos, 6f) * 0.07f);

                    // Cumulus: fbm on a plane overhead, fading out towards the horizon.
                    var p = CloudPlane(dir, 2.2f);
                    float density = Fbm(p.x + 31f, p.y + 7f, 5);
                    float cover = SmoothStep01(0.5f, 0.7f, density) * SmoothStep01(0.05f, 0.3f, dir.y);
                    if (cover > 0f)
                    {
                        // Lighter tops towards the sun, grey-blue undersides elsewhere.
                        float shade = Fbm(p.x + 31f + sunDirection.x * 0.25f, p.y + 7f + sunDirection.z * 0.25f, 3);
                        float lit = Mathf.Clamp01(0.6f + (density - shade) * 3.5f + sunCos * 0.25f);
                        var cloud = Color.Lerp(new Color(0.66f, 0.71f, 0.8f), new Color(0.99f, 0.98f, 0.96f), lit);
                        cloud = Color.Lerp(cloud, DayHorizon, Mathf.Clamp01(1f - dir.y * 2.5f) * 0.6f); // haze near the horizon
                        c = Color.Lerp(c, cloud, cover * 0.95f);
                    }
                    // The sun disc on top.
                    if (sunCos > 0.99996f) c = Color.Lerp(c, new Color(1f, 0.99f, 0.95f), 0.95f);
                    else if (sunCos > 0.9995f) c += sunColor * 0.6f * SmoothStep01(0.9995f, 0.99996f, sunCos);
                    pixels[y * SkyWidth + x] = Clamp(c);
                }
            return pixels;
        }

        /// <summary>
        /// Night panorama (made once per variant): gradient with a warm light-pollution glow (strong over the city), stars
        /// (fewer and dimmer in the city, fewer near the horizon), a Milky Way with dark dust lanes, the moon at
        /// <paramref name="moonDirection"/> with a halo, and thin clouds (moonlit edges, orange undersides over the city)
        /// that hide the stars behind them.
        /// </summary>
        public static Color[] Night(Vector3 moonDirection, bool urban)
        {
            moonDirection.Normalize();
            var pixels = new Color[SkyWidth * SkyHeight];
            var clouds = new float[SkyWidth * SkyHeight];
            var cloudColors = new Color[SkyWidth * SkyHeight];
            var zenith = new Color(0.006f, 0.01f, 0.035f);
            var horizon = urban ? new Color(0.07f, 0.07f, 0.13f) : new Color(0.06f, 0.075f, 0.16f);
            var glow = urban ? new Color(0.17f, 0.09f, 0.0f) : new Color(0.07f, 0.045f, 0.035f);
            float glowHeight = urban ? 0.32f : 0.14f;
            var below = urban ? NightHorizonUrban : NightHorizonRural;
            var band = new Vector3(0.32f, 0.62f, 0.72f).normalized; // normal of the Milky Way's great circle

            for (int y = 0; y < SkyHeight; y++)
                for (int x = 0; x < SkyWidth; x++)
                {
                    int i = y * SkyWidth + x;
                    var dir = SkyDirection(x, y);
                    if (dir.y <= 0f)
                    {
                        pixels[i] = below;
                        continue;
                    }
                    var c = Color.Lerp(horizon, zenith, Mathf.Pow(dir.y, 0.45f));
                    if (dir.y < glowHeight) c += glow * Mathf.Pow(1f - dir.y / glowHeight, 2.2f);
                    float moonCos = Mathf.Max(0f, Vector3.Dot(dir, moonDirection));
                    c += new Color(0.05f, 0.06f, 0.09f) * Mathf.Pow(moonCos, 24f); // wide moon halo

                    // Milky Way with dust lanes (dimmer in the city).
                    float distance = Mathf.Abs(Vector3.Dot(dir, band));
                    if (distance < 0.4f)
                    {
                        float u = (x + 0.5f) / SkyWidth, v = (y + 0.5f) / SkyHeight;
                        float cloud = Fbm(u * 50f, v * 25f, 4);
                        float lanes = SmoothStep01(0.45f, 0.7f, Fbm(u * 90f + 5f, v * 45f + 3f, 3));
                        float strength = Mathf.Exp(-(distance / 0.15f) * (distance / 0.15f)) * cloud * (1f - 0.75f * lanes)
                                         * Mathf.Clamp01(dir.y * 4f) * (urban ? 0.45f : 1f);
                        var tint = Color.Lerp(new Color(0.06f, 0.055f, 0.1f), new Color(0.05f, 0.075f, 0.085f), cloud);
                        c += tint * strength * 2.3f;
                    }
                    pixels[i] = c;

                    // Thin clouds (applied after the stars, so they hide them).
                    var p = CloudPlane(dir, 1.6f);
                    float density = Fbm(p.x + 101f, p.y + 57f, 4);
                    float cover = SmoothStep01(urban ? 0.54f : 0.58f, 0.78f, density) * SmoothStep01(0.03f, 0.2f, dir.y) * 0.85f;
                    clouds[i] = cover;
                    if (cover > 0f)
                    {
                        var cloudColor = Color.Lerp(new Color(0.03f, 0.035f, 0.06f), new Color(0.2f, 0.22f, 0.28f), Mathf.Pow(moonCos, 6f));
                        if (urban) cloudColor += new Color(0.12f, 0.07f, 0.04f) * Mathf.Clamp01(1f - dir.y * 2f); // lit from below by the city
                        cloudColors[i] = cloudColor;
                    }
                }

            // Stars.
            var random = new System.Random(urban ? 77 : 20261126);
            int count = urban ? 2600 : 5200;
            for (int n = 0; n < count; n++)
            {
                float dirY = Mathf.Sqrt((float)random.NextDouble());
                float longitude = (float)random.NextDouble() * 2f * Mathf.PI;
                float ring = Mathf.Sqrt(Mathf.Max(0f, 1f - dirY * dirY));
                var dir = new Vector3(ring * Mathf.Cos(longitude), dirY, ring * Mathf.Sin(longitude));
                float magnitude = Mathf.Pow((float)random.NextDouble(), 4f);
                float brightness = (0.2f + 0.8f * magnitude) * Mathf.Clamp01(dirY * 5f) * (urban ? 0.7f : 1f);
                var color = Color.Lerp(new Color(0.7f, 0.8f, 1f), new Color(1f, 0.88f, 0.72f), (float)random.NextDouble()) * brightness;
                PanoramaPixel(dir, out int px, out int py);
                AddPixel(pixels, px, py, color);
                if (magnitude > 0.45f)
                {
                    var halo = color * 0.3f;
                    AddPixel(pixels, px + 1, py, halo);
                    AddPixel(pixels, px - 1, py, halo);
                    AddPixel(pixels, px, py + 1, halo);
                    AddPixel(pixels, px, py - 1, halo);
                }
                if (magnitude > 0.85f) // a few very bright ones with a little cross
                    for (int k = 2; k <= 3; k++)
                    {
                        var ray = color * (0.18f / k);
                        AddPixel(pixels, px + k, py, ray);
                        AddPixel(pixels, px - k, py, ray);
                        AddPixel(pixels, px, py + k, ray);
                        AddPixel(pixels, px, py - k, ray);
                    }
            }

            // Moon: a soft disc with a few darker patches and a glow.
            PanoramaPixel(moonDirection, out int mx, out int my);
            const int glowRadius = 80;
            const float discRadius = 10f;
            for (int dy = -glowRadius; dy <= glowRadius; dy++)
                for (int dx = -glowRadius; dx <= glowRadius; dx++)
                {
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (r > glowRadius) continue;
                    var add = new Color(0.1f, 0.11f, 0.15f) * Mathf.Pow(1f - r / glowRadius, 3f);
                    if (r < discRadius + 1f)
                    {
                        float disc = Mathf.Clamp01(discRadius + 0.5f - r);
                        float maria = Fbm(dx * 0.18f + 3f, dy * 0.18f + 7f, 3);
                        float shade = 0.78f + 0.22f * SmoothStep01(0.35f, 0.65f, maria);
                        add = Color.Lerp(add, new Color(0.96f, 0.95f, 0.88f) * shade, disc);
                        SetPixel(pixels, mx + dx, my + dy, Color.Lerp(Pixel(pixels, mx + dx, my + dy), add, disc));
                        continue;
                    }
                    AddPixel(pixels, mx + dx, my + dy, add);
                }

            // Clouds over everything.
            for (int i = 0; i < pixels.Length; i++)
                if (clouds[i] > 0f) pixels[i] = Clamp(Color.Lerp(pixels[i], cloudColors[i], clouds[i]));

            return pixels;
        }

        static void PanoramaPixel(Vector3 dir, out int x, out int y)
        {
            float u = 0.5f - Mathf.Atan2(dir.z, dir.x) / (2f * Mathf.PI);
            float v = 1f - Mathf.Acos(Mathf.Clamp(dir.y, -1f, 1f)) / Mathf.PI;
            x = Mathf.FloorToInt(Mathf.Repeat(u, 1f) * SkyWidth);
            y = Mathf.Clamp(Mathf.FloorToInt(v * SkyHeight), 0, SkyHeight - 1);
        }

        static int Wrap(int x) => ((x % SkyWidth) + SkyWidth) % SkyWidth;

        static Color Pixel(Color[] pixels, int x, int y) => y < 0 || y >= SkyHeight ? Color.black : pixels[y * SkyWidth + Wrap(x)];

        static void SetPixel(Color[] pixels, int x, int y, Color c)
        {
            if (y < 0 || y >= SkyHeight) return;
            pixels[y * SkyWidth + Wrap(x)] = Clamp(c);
        }

        static void AddPixel(Color[] pixels, int x, int y, Color add)
        {
            if (y < 0 || y >= SkyHeight) return;
            int i = y * SkyWidth + Wrap(x);
            pixels[i] = Clamp(pixels[i] + add);
        }

        static Color Clamp(Color c) => new(Mathf.Min(1f, c.r), Mathf.Min(1f, c.g), Mathf.Min(1f, c.b), 1f);
    }
}
