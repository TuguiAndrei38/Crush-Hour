using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>
    /// The world around the venue: one shared **event district**, laid out like the menu's venue plan (stadium in the
    /// middle, the festival stage west-north-west of it, the metro platforms south-west). The map being played sits at
    /// its spot in the district (its own grid is the venue); the other two are drawn at theirs: the stadium as a white
    /// bowl with stands, a roof ring, red banners and floodlight masts; the festival as a fenced field with the main
    /// stage, tents and light towers; the metro as a station hall with an "M" sign, platforms and trains on a railway.
    /// Around them: grass parkland with round low-poly tree clusters, a ring road around the stadium and roads between
    /// the venues and out of the district (with lamps), scattered low buildings (a bigger business block to the east),
    /// a car park, hills to the west and a city skyline to the east.
    /// Built once per map (fixed seed) under "Landscape (generated)", kept across rounds. Everything is merged into a few
    /// meshes per colour (<see cref="SceneryBatch"/>, no shadows) on <see cref="LandscapeLayer"/>; night-only parts (lit
    /// windows, lamp glows, floodlight heads, stage and train lights) are switched by <see cref="SetLandscapeNight"/>. The
    /// played venue plus <see cref="ClearMargin"/> stays flat (roads and paving only), so nothing stands in front of the
    /// VIP box.
    /// </summary>
    public partial class CrushHourGame
    {
        public const int LandscapeLayer = 24;

        /// <summary>Used by the sky: the festival's night is rural, the stadium's and the metro's urban.</summary>
        enum Scenery { Countryside, City, Downtown }

        Scenery CurrentScenery => selectedMap switch
        {
            VenueMap.CityStadium => Scenery.City,
            VenueMap.MetroConcourse => Scenery.Downtown,
            _ => Scenery.Countryside,
        };

        /// <summary>How the other venues look in the landscape: like their levels (built from the same layout), or the menu-plan style.</summary>
        public enum RemoteVenueLook { Level, Plan }

        [Header("Landscape")]
        [Tooltip("Level: the other venues look like their levels (ground, fence, landmarks, gates, a static crowd). Plan: the stylised look from the menu's venue plan (white stadium bowl, festival stage, metro station with the M sign).")]
        public RemoteVenueLook remoteVenueLook = RemoteVenueLook.Level;

        Transform landscapeRoot;
        VenueMap landscapeMap;
        RemoteVenueLook landscapeLook;
        readonly List<GameObject> landscapeNightOnly = new();
        static Mesh sphereMesh, crownMesh;

        // District plan (metres; +x east, +z north): where each venue's centre is (index = VenueMap).
        static readonly Vector2[] DistrictSites = { new(-210f, 50f), new(0f, 0f), new(-140f, -175f) };
        const float SiteHalfW = FestivalVenue.Width * 0.5f, SiteHalfH = FestivalVenue.Height * 0.5f;
        const float ClearMargin = 34f;
        static readonly Rect VenueRect = new(0f, 0f, FestivalVenue.Width, FestivalVenue.Height);

        // This build's layout, in world coordinates.
        Vector2 districtOffset;
        readonly List<(Vector2 a, Vector2 b, float halfWidth)> roadSegments = new();
        float trackZ, trackX0, trackX1;

        Vector2 ToWorld(Vector2 district) => district + districtOffset;
        Vector2 SiteCentre(VenueMap map) => ToWorld(DistrictSites[(int)map]);

        /// <summary>Builds the landscape for the selected map if it isn't there yet. Called by NewGame.</summary>
        void BuildLandscape()
        {
            if (landscapeRoot != null && landscapeMap == selectedMap && landscapeLook == remoteVenueLook) return;
            if (landscapeRoot != null) Destroy(landscapeRoot.gameObject);
            landscapeNightOnly.Clear();
            roadSegments.Clear();
            landscapeMap = selectedMap;
            landscapeLook = remoteVenueLook;
            landscapeRoot = new GameObject("Landscape (generated)").transform;
            landscapeRoot.SetParent(transform, false);
            if (sphereMesh == null) sphereMesh = PrimitiveMesh(PrimitiveType.Sphere);
            if (crownMesh == null) crownMesh = BuildCrownMesh();
            // The played venue's centre (52, 38) is its spot in the district.
            districtOffset = new Vector2(VenueRect.center.x, VenueRect.center.y) - DistrictSites[(int)selectedMap];

            var random = new System.Random(7700);   // same district for every map
            var day = new SceneryBatch();
            var glow = new SceneryBatch();          // unlit, night only
            BuildGround(new Color(0.3f, 0.47f, 0.22f), 0.28f, 14f);
            BuildRoadsAndPlazas(day, glow);
            BuildRailway(day, glow);
            foreach (VenueMap map in Enum.GetValues(typeof(VenueMap)))
            {
                if (map == selectedMap) continue;
                var centre = SiteCentre(map);
                if (remoteVenueLook == RemoteVenueLook.Level)
                {
                    BuildRemoteLevel(day, glow, map, centre, random);
                    continue;
                }
                // The menu-plan style (kept as the alternative look).
                switch (map)
                {
                    case VenueMap.CityStadium: BuildRemoteStadium(day, glow, centre); break;
                    case VenueMap.MetroConcourse: BuildRemoteMetro(day, glow, centre); break;
                    default: BuildRemoteFestival(day, glow, centre, random); break;
                }
            }
            BuildParking(day, random);
            BuildBuildings(day, glow, random);
            BuildTrees(day, random);
            BuildOutskirts(day, glow, random);
            day.Build(landscapeRoot, "Scenery", Lit);
            foreach (var go in glow.Build(landscapeRoot, "Night lights", Unlit)) landscapeNightOnly.Add(go);
            SetLandscapeNight(IsNight);
        }

        void SetLandscapeNight(bool night)
        {
            foreach (var go in landscapeNightOnly)
                if (go != null) go.SetActive(night);
        }

        static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var temp = GameObject.CreatePrimitive(type);
            var mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);
            return mesh;
        }

        /// <summary>A flat-shaded icosahedron (diameter 1): the round low-poly tree crown of the venue plan's photo.</summary>
        static Mesh BuildCrownMesh()
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var v = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            int[] f =
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int i = 0; i < f.Length; i += 3)
            {
                Vector3 a = v[f[i]].normalized * 0.5f, b = v[f[i + 1]].normalized * 0.5f, c = v[f[i + 2]].normalized * 0.5f;
                // Unity's front face: Cross(b - a, c - a) points outwards.
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) < 0f) (b, c) = (c, b);
                int n = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c);
                triangles.Add(n); triangles.Add(n + 1); triangles.Add(n + 2);
            }
            var mesh = new Mesh { name = "Tree crown" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static float Range(System.Random r, float min, float max) => min + (float)r.NextDouble() * (max - min);

        static T Pick<T>(System.Random r, T[] items) => items[r.Next(items.Length)];

        static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 1e-4f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>Is this world point (with a radius) on a venue (+ClearMargin), a road or the railway?</summary>
        bool Occupied(Vector2 p, float pad)
        {
            foreach (VenueMap map in Enum.GetValues(typeof(VenueMap)))
            {
                var c = SiteCentre(map);
                if (Mathf.Abs(p.x - c.x) < SiteHalfW + ClearMargin + pad && Mathf.Abs(p.y - c.y) < SiteHalfH + ClearMargin + pad) return true;
            }
            foreach (var (a, b, halfWidth) in roadSegments)
                if (SegmentDistance(p, a, b) < halfWidth + pad + 2f) return true;
            if (p.x > trackX0 - pad && p.x < trackX1 + pad && Mathf.Abs(p.y - trackZ) < 14f + pad) return true;
            return false;
        }

        // ---------------------------------------------------------------- Ground, roads, plazas

        /// <summary>The ground around the venue: four textured slabs that meet the venue's own ground quad (no overlap, no flicker).</summary>
        void BuildGround(Color baseColor, float speckle, float tile)
        {
            const float extent = 1100f;
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, true) { name = "Landscape Ground", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            var pixels = new Color[256 * 256];
            var noise = new System.Random(4242);
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    float n = CrowdSimulation.SmoothNoise(x * 0.06f, y * 0.06f) * 0.6f + CrowdSimulation.SmoothNoise(x * 0.25f, y * 0.25f) * 0.4f;
                    float k = 1f + (n - 0.5f) * speckle + ((float)noise.NextDouble() - 0.5f) * speckle * 0.35f;
                    pixels[y * 256 + x] = new Color(baseColor.r * k, baseColor.g * k, baseColor.b * k, 1f);
                }
            texture.SetPixels(pixels);
            texture.Apply(true, true);
            var material = litMaterial != null ? new Material(litMaterial) : new Material(FindShader("Universal Render Pipeline/Lit"));
            material.name = "Landscape Ground";
            material.mainTexture = texture;
            material.color = Color.white;
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.05f);

            float x0 = VenueRect.xMin, x1 = VenueRect.xMax, z0 = VenueRect.yMin, z1 = VenueRect.yMax;
            var slabs = new[]
            {
                new Rect(-extent, -extent, x0 + extent, extent * 2f + z1),          // west
                new Rect(x1, -extent, extent, extent * 2f + z1),                    // east
                new Rect(x0, -extent, x1 - x0, extent + z0),                        // south
                new Rect(x0, z1, x1 - x0, extent),                                  // north
            };
            foreach (var slab in slabs)
            {
                var mesh = new Mesh { name = "Landscape Ground" };
                mesh.vertices = new[]
                {
                    new Vector3(slab.xMin, 0f, slab.yMin), new Vector3(slab.xMin, 0f, slab.yMax),
                    new Vector3(slab.xMax, 0f, slab.yMax), new Vector3(slab.xMax, 0f, slab.yMin),
                };
                mesh.uv = new[]
                {
                    new Vector2(slab.xMin, slab.yMin) / tile, new Vector2(slab.xMin, slab.yMax) / tile,
                    new Vector2(slab.xMax, slab.yMax) / tile, new Vector2(slab.xMax, slab.yMin) / tile,
                };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var go = new GameObject("Ground", typeof(MeshFilter), typeof(MeshRenderer)) { layer = LandscapeLayer };
                go.transform.SetParent(landscapeRoot, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        /// <summary>A flat axis-aligned strip (pad, plaza) just above the ground.</summary>
        void Strip(SceneryBatch batch, float xMin, float zMin, float xMax, float zMax, Color color, float lift = 0.03f) =>
            batch.Box(new Vector3((xMin + xMax) * 0.5f, lift * 0.5f, (zMin + zMax) * 0.5f), new Vector3(xMax - xMin, lift, zMax - zMin), 0f, color);

        /// <summary>A flat strip from a to b (world), any direction.</summary>
        void Band(SceneryBatch batch, Vector2 a, Vector2 b, float width, Color color, float lift)
        {
            var d = b - a;
            float length = d.magnitude;
            if (length < 0.01f) return;
            float yaw = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
            var mid = (a + b) * 0.5f;
            batch.Box(new Vector3(mid.x, lift * 0.5f, mid.y), new Vector3(width, lift, length + width * 0.5f), yaw, color);
        }

        /// <summary>A road along a district polyline: asphalt, a dashed centre line near the venues, lamps on one side.</summary>
        void Road(SceneryBatch day, SceneryBatch glow, float width, params Vector2[] districtPoints)
        {
            var asphalt = new Color(0.25f, 0.25f, 0.27f);
            var marking = new Color(0.9f, 0.88f, 0.8f);
            for (int i = 0; i + 1 < districtPoints.Length; i++)
            {
                Vector2 a = ToWorld(districtPoints[i]), b = ToWorld(districtPoints[i + 1]);
                roadSegments.Add((a, b, width * 0.5f));
                Band(day, a, b, width, asphalt, 0.04f);
                var d = b - a;
                float length = d.magnitude;
                var dir = d / length;
                var side = new Vector2(dir.y, -dir.x);
                for (float s = 4f; s < length - 3f; s += 9f)
                {
                    var p = a + dir * s;
                    if ((p - ToWorld(Vector2.zero)).magnitude < 420f) Band(day, p, p + dir * 3.5f, 0.25f, marking, 0.06f);
                }
                for (float s = 10f; s < length; s += 32f)
                {
                    var p = a + dir * s + side * (width * 0.5f + 1.5f);
                    if (!InPlayedVenue(p, 2f)) Lamp(day, glow, p.x, p.y, 7f);
                }
            }
        }

        static bool InPlayedVenue(Vector2 p, float pad) =>
            p.x > VenueRect.xMin - pad && p.x < VenueRect.xMax + pad && p.y > VenueRect.yMin - pad && p.y < VenueRect.yMax + pad;

        void BuildRoadsAndPlazas(SceneryBatch day, SceneryBatch glow)
        {
            var paving = new Color(0.62f, 0.6f, 0.56f);
            // Paved plaza around the played venue (four strips, never over its own ground) with lamps.
            float cx0 = VenueRect.xMin - 18f, cx1 = VenueRect.xMax + 18f, cz0 = VenueRect.yMin - 18f, cz1 = VenueRect.yMax + 18f;
            Strip(day, cx0, cz0, cx1, VenueRect.yMin, paving, 0.02f);
            Strip(day, cx0, VenueRect.yMax, cx1, cz1, paving, 0.02f);
            Strip(day, cx0, VenueRect.yMin, VenueRect.xMin, VenueRect.yMax, paving, 0.02f);
            Strip(day, VenueRect.xMax, VenueRect.yMin, cx1, VenueRect.yMax, paving, 0.02f);
            for (float x = cx0 + 4f; x < cx1; x += 22f)
            {
                Lamp(day, glow, x, cz0 + 2f, 7f);
                Lamp(day, glow, x, cz1 - 2f, 7f);
            }

            // Ring road around the stadium (an octagon, like the dashed ellipse on the plan).
            var ring = new Vector2[9];
            for (int k = 0; k <= 8; k++)
            {
                float angle = k * Mathf.PI / 4f;
                ring[k] = new Vector2(Mathf.Cos(angle) * 98f, Mathf.Sin(angle) * 84f);
            }
            Road(day, glow, 10f, ring);
            // Between the venues.
            Road(day, glow, 9f, new Vector2(-98f, 0f), new Vector2(-116f, 28f), new Vector2(-124f, 48f));                 // stadium → festival
            Road(day, glow, 9f, new Vector2(-69f, -59f), new Vector2(-92f, -82f), new Vector2(-112f, -102f));             // stadium → metro
            Road(day, glow, 8f, new Vector2(-210f, 122f), new Vector2(-196f, 250f), new Vector2(-150f, 700f));            // festival north
            Road(day, glow, 8f, new Vector2(-296f, 52f), new Vector2(-700f, 70f));                                          // festival west
            // Out of the district.
            Road(day, glow, 10f, new Vector2(0f, 84f), new Vector2(18f, 170f), new Vector2(70f, 320f), new Vector2(140f, 700f));   // north
            Road(day, glow, 9f, new Vector2(69f, 59f), new Vector2(150f, 140f), new Vector2(460f, 460f));                        // north-east
            Road(day, glow, 10f, new Vector2(98f, 0f), new Vector2(250f, -12f), new Vector2(700f, -30f));                         // east
            Road(day, glow, 9f, new Vector2(0f, -84f), new Vector2(12f, -170f), new Vector2(36f, -700f));                         // south
            Road(day, glow, 8f, new Vector2(-226f, -160f), new Vector2(-700f, -150f));                                           // metro west
        }

        void Lamp(SceneryBatch day, SceneryBatch glow, float x, float z, float height)
        {
            day.Box(new Vector3(x, height * 0.5f, z), new Vector3(0.22f, height, 0.22f), 0f, new Color(0.3f, 0.31f, 0.33f));
            day.Box(new Vector3(x, height, z), new Vector3(0.8f, 0.22f, 0.5f), 0f, new Color(0.25f, 0.26f, 0.28f));
            glow.Box(new Vector3(x, height - 0.14f, z), new Vector3(0.7f, 0.08f, 0.42f), 0f, new Color(1f, 0.82f, 0.5f));
        }

        void Car(SceneryBatch day, System.Random r, float x, float z, float yaw)
        {
            var colors = new[]
            {
                new Color(0.75f, 0.12f, 0.12f), new Color(0.85f, 0.85f, 0.86f), new Color(0.12f, 0.13f, 0.15f),
                new Color(0.2f, 0.32f, 0.6f), new Color(0.5f, 0.52f, 0.55f), new Color(0.85f, 0.7f, 0.2f),
            };
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            day.Box(new Vector3(x, 0.55f, z), new Vector3(1.8f, 0.8f, 4.3f), yaw, Pick(r, colors));
            day.Box(new Vector3(x, 1.2f, z) + rotation * new Vector3(0f, 0f, -0.2f), new Vector3(1.6f, 0.6f, 2.2f), yaw, new Color(0.14f, 0.18f, 0.24f));
        }

        /// <summary>A round low-poly tree (icosahedron crown, like the plan's photo), sometimes a two-crown one.</summary>
        void Tree(SceneryBatch day, System.Random r, float x, float z)
        {
            float scale = Range(r, 0.8f, 1.5f);
            var greens = new[] { new Color(0.27f, 0.5f, 0.2f), new Color(0.34f, 0.56f, 0.22f), new Color(0.22f, 0.43f, 0.19f), new Color(0.4f, 0.6f, 0.25f) };
            var green = Pick(r, greens);
            day.Box(new Vector3(x, 1.2f * scale, z), new Vector3(0.4f, 2.4f, 0.4f) * scale, Range(r, 0f, 90f), new Color(0.35f, 0.25f, 0.16f));
            var spin = Quaternion.Euler(Range(r, 0f, 360f), Range(r, 0f, 360f), Range(r, 0f, 360f));
            day.Mesh(crownMesh, new Vector3(x, 3.6f * scale, z), new Vector3(3.6f, 3.4f, 3.6f) * scale, spin, green);
            if (r.NextDouble() < 0.3)
                day.Mesh(crownMesh, new Vector3(x + 0.8f * scale, 4.7f * scale, z - 0.5f * scale), Vector3.one * 2.2f * scale, spin, green);
        }

        // ---------------------------------------------------------------- Railway (south of the metro)

        void BuildRailway(SceneryBatch day, SceneryBatch glow)
        {
            var metro = SiteCentre(VenueMap.MetroConcourse);
            trackZ = metro.y - SiteHalfH - 16f;
            trackX0 = ToWorld(new Vector2(-720f, 0f)).x;
            trackX1 = ToWorld(new Vector2(60f, 0f)).x;
            var bed = new Color(0.42f, 0.39f, 0.35f);
            var rail = new Color(0.62f, 0.62f, 0.64f);
            var sleeper = new Color(0.33f, 0.28f, 0.22f);
            float length = trackX1 - trackX0, mid = (trackX0 + trackX1) * 0.5f;
            day.Box(new Vector3(mid, 0.15f, trackZ), new Vector3(length, 0.3f, 13f), 0f, bed);
            foreach (float offset in new[] { -3f, 3f })
            {
                day.Box(new Vector3(mid, 0.42f, trackZ + offset - 0.72f), new Vector3(length, 0.15f, 0.1f), 0f, rail);
                day.Box(new Vector3(mid, 0.42f, trackZ + offset + 0.72f), new Vector3(length, 0.15f, 0.1f), 0f, rail);
                for (float x = Mathf.Max(trackX0, metro.x - 260f); x < trackX1; x += 1.6f)
                    day.Box(new Vector3(x, 0.33f, trackZ + offset), new Vector3(0.25f, 0.1f, 2.4f), 0f, sleeper);
            }
            // Platforms at the station, a canopy over the north one, and two trains (as on the plan).
            var concrete = new Color(0.7f, 0.68f, 0.64f);
            day.Box(new Vector3(metro.x, 0.5f, trackZ + 7.6f), new Vector3(120f, 1f, 4f), 0f, concrete);
            day.Box(new Vector3(metro.x, 0.5f, trackZ - 7.6f), new Vector3(120f, 1f, 4f), 0f, concrete);
            for (float x = metro.x - 56f; x <= metro.x + 56f; x += 14f)
                day.Box(new Vector3(x, 2.6f, trackZ + 8.6f), new Vector3(0.3f, 4.2f, 0.3f), 0f, new Color(0.4f, 0.42f, 0.45f));
            day.Box(new Vector3(metro.x, 4.8f, trackZ + 7.6f), new Vector3(122f, 0.35f, 6f), 0f, new Color(0.78f, 0.79f, 0.8f));
            glow.Box(new Vector3(metro.x, 4.55f, trackZ + 7.6f), new Vector3(118f, 0.1f, 0.6f), 0f, new Color(0.9f, 0.95f, 1f));
            for (int train = 0; train < 2; train++)
            {
                float z = trackZ + (train == 0 ? 3f : -3f);
                float start = metro.x - 50f + train * 12f;
                for (int car = 0; car < 4; car++)
                {
                    float x = start + car * 19f;
                    day.Box(new Vector3(x, 2.2f, z), new Vector3(18f, 3.4f, 2.9f), 0f, new Color(0.84f, 0.85f, 0.87f));
                    day.Box(new Vector3(x, 1.6f, z), new Vector3(18.05f, 0.45f, 2.95f), 0f, new Color(0.15f, 0.25f, 0.55f));
                    day.Box(new Vector3(x, 2.8f, z), new Vector3(16f, 0.9f, 2.95f), 0f, new Color(0.16f, 0.2f, 0.26f));
                    glow.Box(new Vector3(x, 2.8f, z), new Vector3(15.5f, 0.7f, 3f), 0f, new Color(0.85f, 0.92f, 1f));
                }
            }
            for (float x = trackX0; x < trackX1; x += 50f)
            {
                day.Box(new Vector3(x, 3.8f, trackZ - 6.4f), new Vector3(0.3f, 7.6f, 0.3f), 0f, new Color(0.4f, 0.42f, 0.44f));
                day.Box(new Vector3(x, 7.4f, trackZ - 2.5f), new Vector3(0.2f, 0.2f, 8.5f), 0f, new Color(0.4f, 0.42f, 0.44f));
            }
        }

        // ---------------------------------------------------------------- The other venues, as their levels look

        /// <summary>
        /// Another venue as its level looks, built from the same layout (<see cref="FestivalVenue"/> for that map): its
        /// ground (cell colours as the level's ground, without the heatmap), the fence, every solid landmark (the
        /// festival's with Oli's art prefabs, like the level; the others as the level's blocks), the gates (closed), a
        /// paved plaza around it, a static crowd in its spawn zones, and at night a glow on its light towers.
        /// </summary>
        void BuildRemoteLevel(SceneryBatch day, SceneryBatch glow, VenueMap map, Vector2 centre, System.Random r)
        {
            var layout = new FestivalVenue(map);
            var origin = new Vector2(centre.x - SiteHalfW, centre.y - SiteHalfH); // the layout's cell (0, 0) corner
            var root = new GameObject($"{MapCatalog.Get(map).Title} (landscape)").transform;
            root.SetParent(landscapeRoot, false);
            root.localPosition = new Vector3(origin.x, 0f, origin.y);

            // Plaza around it, like the played venue's.
            var paving = new Color(0.62f, 0.6f, 0.56f);
            float x0 = origin.x, x1 = origin.x + FestivalVenue.Width, z0 = origin.y, z1 = origin.y + FestivalVenue.Height;
            Strip(day, x0 - 18f, z0 - 18f, x1 + 18f, z0, paving, 0.02f);
            Strip(day, x0 - 18f, z1, x1 + 18f, z1 + 18f, paving, 0.02f);
            Strip(day, x0 - 18f, z0, x0, z1, paving, 0.02f);
            Strip(day, x1, z0, x1 + 18f, z1, paving, 0.02f);

            // Ground: the level's cell colours on a lit quad (so it darkens at night like the rest of the landscape).
            var texture = new Texture2D(FestivalVenue.Width, FestivalVenue.Height, TextureFormat.RGBA32, false)
            {
                name = $"{map} ground (landscape)",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[FestivalVenue.CellCount];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = layout.cells[i] switch
                {
                    CellType.Outside => Asphalt,
                    CellType.Open => LevelFloorColor(map, i),
                    CellType.Fire => Scorched,
                    _ => Blocked,
                };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var groundMat = litMaterial != null ? new Material(litMaterial) : new Material(FindShader("Universal Render Pipeline/Lit"));
            groundMat.name = texture.name;
            groundMat.mainTexture = texture;
            groundMat.color = Color.white;
            if (groundMat.HasProperty("_Smoothness")) groundMat.SetFloat("_Smoothness", 0.05f);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ground.name = "Ground";
            Destroy(ground.GetComponent<Collider>());
            ground.transform.SetParent(root, false);
            ground.transform.SetLocalPositionAndRotation(new Vector3(FestivalVenue.Width * 0.5f, 0.05f, FestivalVenue.Height * 0.5f), Quaternion.Euler(90f, 0f, 0f)); // 5 cm over the landscape ground: no flicker at a distance
            ground.transform.localScale = new Vector3(FestivalVenue.Width, FestivalVenue.Height, 1f);
            var groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.sharedMaterial = groundMat;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;

            // Fence, merged into horizontal runs (as BuildVenueViews).
            for (int y = 0; y < FestivalVenue.Height; y++)
            {
                int x = 0;
                while (x < FestivalVenue.Width)
                {
                    if (layout.cells[FestivalVenue.Index(x, y)] != CellType.Wall) { x++; continue; }
                    int start = x;
                    while (x < FestivalVenue.Width && layout.cells[FestivalVenue.Index(x, y)] == CellType.Wall) x++;
                    day.Box(new Vector3(origin.x + (start + x) * 0.5f, 0.6f, origin.y + y + 0.5f), new Vector3(x - start, 1.2f, 1f), 0f, FenceColor);
                }
            }

            // Landmarks: the festival's art prefabs (as the level), otherwise the level's blocks.
            foreach (var l in layout.landmarks)
            {
                if (!l.solid) continue;
                if (TryBuildFestivalLandmark(l, out var view, root, map, false)) continue;
                float inset = 0.15f;
                day.Box(new Vector3(origin.x + l.rect.center.x, l.height * 0.5f, origin.y + l.rect.center.y),
                    new Vector3(l.rect.width - inset, l.height, l.rect.height - inset), 0f, l.color);
            }
            foreach (var tower in layout.lightTowers)
            {
                float top = Mathf.Max(6f, tower.height);
                glow.Box(new Vector3(origin.x + tower.rect.center.x, top + 0.4f, origin.y + tower.rect.center.y), new Vector3(1.6f, 0.6f, 1.6f), 0f, new Color(1f, 0.9f, 0.7f));
            }
            foreach (var landmark in layout.landmarks)
                if (landmark.name.Contains("Stage"))
                    glow.Box(new Vector3(origin.x + landmark.rect.center.x, landmark.height + 0.3f, origin.y + landmark.rect.center.y),
                        new Vector3(landmark.rect.width * 0.6f, 0.2f, 1f), 0f, new Color(0.75f, 0.35f, 1f));

            // Gates (closed, as at the start of a level).
            foreach (var g in layout.gates)
                day.Box(new Vector3(origin.x + g.rect.center.x, 0.7f, origin.y + g.rect.center.y), new Vector3(g.rect.width, 1.4f, g.rect.height), 0f, GateClosedColor);

            // A static crowd in the spawn zones (small figures: body + head), so the venue looks busy like the level.
            var clothes = new[]
            {
                new Color(0.85f, 0.25f, 0.2f), new Color(0.2f, 0.4f, 0.75f), new Color(0.95f, 0.8f, 0.25f), new Color(0.25f, 0.6f, 0.35f),
                new Color(0.9f, 0.9f, 0.88f), new Color(0.15f, 0.15f, 0.18f), new Color(0.6f, 0.35f, 0.7f), new Color(0.95f, 0.55f, 0.2f),
            };
            var skin = new[] { new Color(0.93f, 0.76f, 0.62f), new Color(0.72f, 0.52f, 0.38f), new Color(0.45f, 0.3f, 0.22f) };
            float totalWeight = 0f;
            foreach (var zone in layout.crowdZones) totalWeight += zone.weight;
            const int people = 420;
            foreach (var zone in layout.crowdZones)
            {
                int count = totalWeight > 0f ? Mathf.RoundToInt(people * zone.weight / totalWeight) : 0;
                for (int n = 0, tries = 0; n < count && tries < count * 4; tries++)
                {
                    float px = zone.rect.x + Range(r, 0f, zone.rect.width), pz = zone.rect.y + Range(r, 0f, zone.rect.height);
                    int cx = Mathf.Clamp(Mathf.FloorToInt(px), 0, FestivalVenue.Width - 1), cz = Mathf.Clamp(Mathf.FloorToInt(pz), 0, FestivalVenue.Height - 1);
                    if (layout.cells[FestivalVenue.Index(cx, cz)] != CellType.Open) continue;
                    var p = new Vector2(origin.x + px, origin.y + pz);
                    float height = Range(r, 1.55f, 1.85f);
                    day.Box(new Vector3(p.x, height * 0.4f, p.y), new Vector3(0.48f, height * 0.8f, 0.32f), Range(r, 0f, 180f), Pick(r, clothes));
                    day.Box(new Vector3(p.x, height * 0.9f, p.y), new Vector3(0.26f, height * 0.2f, 0.26f), 0f, Pick(r, skin));
                    n++;
                }
            }

            // Prefab views: landscape layer, no shadows (cheap at this distance).
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.gameObject.layer = LandscapeLayer;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = LandscapeLayer;
        }

        /// <summary>A level's floor colour for an open cell (as <see cref="MapFloorColor"/>, for any map).</summary>
        static Color LevelFloorColor(VenueMap map, int cell)
        {
            if (map == VenueMap.CityStadium) return new Color(0.43f, 0.46f, 0.49f);
            if (map == VenueMap.MetroConcourse)
                return ((cell % FestivalVenue.Width / 4 + cell / FestivalVenue.Width / 4) & 1) == 0 ? new Color(0.55f, 0.58f, 0.61f) : new Color(0.49f, 0.52f, 0.56f);
            return Grass;
        }

        // ---------------------------------------------------------------- The other venues, menu-plan style (alternative look)

        /// <summary>The stadium seen from outside: a white bowl of stands with a roof ring, red banners, a pitch and four floodlight masts.</summary>
        void BuildRemoteStadium(SceneryBatch day, SceneryBatch glow, Vector2 c)
        {
            Strip(day, c.x - SiteHalfW - 18f, c.y - SiteHalfH - 18f, c.x + SiteHalfW + 18f, c.y + SiteHalfH + 18f, new Color(0.64f, 0.62f, 0.58f), 0.025f);
            day.Box(new Vector3(c.x, 0.06f, c.y), new Vector3(66f, 0.12f, 42f), 0f, new Color(0.27f, 0.55f, 0.24f));
            day.Box(new Vector3(c.x, 0.1f, c.y), new Vector3(0.3f, 0.08f, 42f), 0f, new Color(0.92f, 0.94f, 0.9f));
            day.Box(new Vector3(c.x, 0.05f, c.y), new Vector3(78f, 0.1f, 54f), 0f, new Color(0.65f, 0.3f, 0.22f)); // running track
            const int segments = 56;
            const float a = 44f, b = 33f, depth = 17f;
            var seats = new[] { new Color(0.2f, 0.27f, 0.45f), new Color(0.65f, 0.16f, 0.14f), new Color(0.22f, 0.3f, 0.5f) };
            var white = new Color(0.9f, 0.9f, 0.88f);
            for (int k = 0; k < segments; k++)
            {
                float t0 = k * Mathf.PI * 2f / segments, t1 = (k + 1) * Mathf.PI * 2f / segments, tm = (t0 + t1) * 0.5f;
                var inner = new Vector2(Mathf.Cos(tm) * a, Mathf.Sin(tm) * b);
                var normal = new Vector2(Mathf.Cos(tm) / a, Mathf.Sin(tm) / b).normalized;
                var p0 = new Vector2(Mathf.Cos(t0) * (a + depth), Mathf.Sin(t0) * (b + depth));
                var p1 = new Vector2(Mathf.Cos(t1) * (a + depth), Mathf.Sin(t1) * (b + depth));
                float width = (p1 - p0).magnitude * 1.08f;
                var look = Quaternion.LookRotation(new Vector3(normal.x, 0f, normal.y));
                var stand = inner + normal * (depth * 0.5f);
                day.Box(new Vector3(c.x + stand.x, 6f, c.y + stand.y), new Vector3(width * 0.8f, 1.2f, depth * 1.12f), look * Quaternion.Euler(-30f, 0f, 0f), Pick(new System.Random(k), seats));
                var wall = inner + normal * depth;
                day.Box(new Vector3(c.x + wall.x, 8.5f, c.y + wall.y), new Vector3(width, 17f, 1.4f), look, white);
                var roof = inner + normal * (depth - 5f);
                day.Box(new Vector3(c.x + roof.x, 17.6f, c.y + roof.y), new Vector3(width, 0.5f, 12f), look * Quaternion.Euler(8f, 0f, 0f), white);
                if (k % 7 == 3) // red banners on the outside, as in the plan's photo
                {
                    var banner = inner + normal * (depth + 0.8f);
                    day.Box(new Vector3(c.x + banner.x, 9f, c.y + banner.y), new Vector3(1.6f, 9f, 0.2f), look, new Color(0.78f, 0.12f, 0.1f));
                }
            }
            foreach (var corner in new[] { new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f) })
            {
                var p = c + new Vector2(corner.x * (a + depth + 6f), corner.y * (b + depth + 6f));
                var face = Quaternion.LookRotation(new Vector3(c.x - p.x, 0f, c.y - p.y));
                day.Box(new Vector3(p.x, 20f, p.y), new Vector3(0.8f, 40f, 0.8f), 0f, new Color(0.55f, 0.56f, 0.58f));
                day.Box(new Vector3(p.x, 40f, p.y), new Vector3(7f, 3.5f, 1f), face, new Color(0.3f, 0.31f, 0.33f));
                glow.Box(new Vector3(p.x, 40f, p.y) + face * new Vector3(0f, 0f, 0.55f), new Vector3(6.4f, 3f, 0.1f), face, new Color(1f, 0.97f, 0.88f));
            }
        }

        /// <summary>The festival seen from outside: a fenced field, the main stage on the north side, tents, stalls and two light towers.</summary>
        void BuildRemoteFestival(SceneryBatch day, SceneryBatch glow, Vector2 c, System.Random r)
        {
            var fence = new Color(0.72f, 0.72f, 0.7f);
            float x0 = c.x - SiteHalfW, x1 = c.x + SiteHalfW, z0 = c.y - SiteHalfH, z1 = c.y + SiteHalfH;
            day.Box(new Vector3(c.x, 0.6f, z0), new Vector3(x1 - x0, 1.2f, 0.2f), 0f, fence);
            day.Box(new Vector3(c.x, 0.6f, z1), new Vector3(x1 - x0, 1.2f, 0.2f), 0f, fence);
            day.Box(new Vector3(x0, 0.6f, c.y), new Vector3(0.2f, 1.2f, z1 - z0), 0f, fence);
            day.Box(new Vector3(x1, 0.6f, c.y), new Vector3(0.2f, 1.2f, z1 - z0), 0f, fence);
            // Main stage (as on the plan): deck, back wall, truss towers, roof, speaker stacks, coloured lights at night.
            var stage = new Vector2(c.x, z1 - 9f);
            var dark = new Color(0.16f, 0.16f, 0.19f);
            day.Box(new Vector3(stage.x, 1.4f, stage.y), new Vector3(32f, 2.8f, 10f), 0f, dark);
            day.Box(new Vector3(stage.x, 7f, stage.y + 5f), new Vector3(32f, 11f, 0.6f), 0f, new Color(0.22f, 0.2f, 0.3f));
            foreach (float dx in new[] { -17f, 17f })
                foreach (float dz in new[] { -5.5f, 5f })
                    day.Box(new Vector3(stage.x + dx, 7f, stage.y + dz), new Vector3(0.8f, 14f, 0.8f), 0f, new Color(0.55f, 0.56f, 0.58f));
            day.Box(new Vector3(stage.x, 14.2f, stage.y), new Vector3(36f, 0.8f, 13f), 0f, new Color(0.3f, 0.3f, 0.33f));
            foreach (float dx in new[] { -19.5f, 19.5f }) day.Box(new Vector3(stage.x + dx, 3.5f, stage.y - 4f), new Vector3(3f, 7f, 2.6f), 0f, new Color(0.08f, 0.08f, 0.09f));
            glow.Box(new Vector3(stage.x - 8f, 13.6f, stage.y - 3f), new Vector3(10f, 0.2f, 1f), 0f, new Color(1f, 0.25f, 0.75f));
            glow.Box(new Vector3(stage.x + 8f, 13.6f, stage.y - 3f), new Vector3(10f, 0.2f, 1f), 0f, new Color(0.2f, 0.8f, 1f));
            glow.Box(new Vector3(stage.x, 7f, stage.y + 4.6f), new Vector3(24f, 6f, 0.1f), 0f, new Color(0.55f, 0.3f, 0.9f)); // screen
            // Tents (white pyramids as on the plan) and food stalls in the south half.
            for (int n = 0; n < 9; n++)
            {
                float x = x0 + 10f + n * 10.5f, z = z0 + 10f + (n % 2) * 9f;
                day.Box(new Vector3(x, 0f, z), new Vector3(4.2f, 4.2f, 6f), Quaternion.Euler(0f, 0f, 45f), new Color(0.93f, 0.93f, 0.9f));
            }
            var awnings = new[] { new Color(0.9f, 0.3f, 0.2f), new Color(0.2f, 0.5f, 0.85f), new Color(0.95f, 0.75f, 0.2f), new Color(0.3f, 0.65f, 0.35f) };
            for (int n = 0; n < 6; n++)
            {
                float x = x0 + 14f + n * 14f, z = c.y + 2f;
                day.Box(new Vector3(x, 1.3f, z), new Vector3(5f, 2.6f, 4f), 0f, new Color(0.8f, 0.76f, 0.68f));
                day.Box(new Vector3(x, 2.9f, z - 0.5f), new Vector3(5.6f, 0.3f, 5f), Quaternion.Euler(-10f, 0f, 0f), Pick(r, awnings));
                glow.Box(new Vector3(x, 2.4f, z - 2.1f), new Vector3(4f, 0.3f, 0.1f), 0f, new Color(1f, 0.85f, 0.55f));
            }
            foreach (float dx in new[] { -22f, 22f })
            {
                var p = new Vector2(c.x + dx, c.y + 8f);
                day.Box(new Vector3(p.x, 7f, p.y), new Vector3(1.2f, 14f, 1.2f), 0f, new Color(0.25f, 0.25f, 0.28f));
                day.Box(new Vector3(p.x, 14f, p.y), new Vector3(3.6f, 1.6f, 1f), 0f, new Color(0.3f, 0.31f, 0.33f));
                glow.Box(new Vector3(p.x, 14f, p.y - 0.55f), new Vector3(3.2f, 1.2f, 0.1f), 0f, new Color(1f, 0.92f, 0.75f));
            }
        }

        /// <summary>The metro seen from outside: a station hall with a glass band and an entrance canopy, and the blue "M" sign.</summary>
        void BuildRemoteMetro(SceneryBatch day, SceneryBatch glow, Vector2 c)
        {
            Strip(day, c.x - SiteHalfW - 12f, c.y - SiteHalfH - 12f, c.x + SiteHalfW + 12f, c.y + SiteHalfH + 12f, new Color(0.64f, 0.62f, 0.58f), 0.025f);
            var hall = new Vector2(c.x, c.y - 12f);
            day.Box(new Vector3(hall.x, 5f, hall.y), new Vector3(70f, 10f, 30f), 0f, new Color(0.76f, 0.75f, 0.72f));
            day.Box(new Vector3(hall.x, 5.5f, hall.y), new Vector3(70.2f, 3f, 30.2f), 0f, new Color(0.18f, 0.24f, 0.32f));
            glow.Box(new Vector3(hall.x, 5.5f, hall.y), new Vector3(70.4f, 2.6f, 30.4f), 0f, new Color(0.85f, 0.9f, 1f));
            day.Box(new Vector3(hall.x, 10.4f, hall.y), new Vector3(72f, 0.8f, 32f), 0f, new Color(0.55f, 0.56f, 0.58f));
            day.Box(new Vector3(hall.x, 4f, hall.y + 18f), new Vector3(24f, 0.4f, 6f), 0f, new Color(0.7f, 0.72f, 0.74f)); // entrance canopy
            // "M" sign on a pole (navy board, white letter).
            var sign = new Vector3(c.x + 30f, 0f, c.y + 18f);
            var navy = new Color(0.1f, 0.16f, 0.32f);
            var letter = new Color(0.97f, 0.97f, 0.97f);
            day.Box(sign + new Vector3(0f, 4.5f, 0f), new Vector3(0.4f, 9f, 0.4f), 0f, new Color(0.4f, 0.42f, 0.45f));
            day.Box(sign + new Vector3(0f, 10.5f, 0f), new Vector3(5f, 5f, 0.5f), 0f, navy);
            foreach (float side in new[] { 0.3f, -0.3f })
            {
                day.Box(sign + new Vector3(-1.4f, 10.5f, side), new Vector3(0.6f, 3.6f, 0.1f), 0f, letter);
                day.Box(sign + new Vector3(1.4f, 10.5f, side), new Vector3(0.6f, 3.6f, 0.1f), 0f, letter);
                day.Box(sign + new Vector3(-0.65f, 11.2f, side), new Vector3(0.55f, 2.4f, 0.1f), Quaternion.Euler(0f, 0f, 28f), letter);
                day.Box(sign + new Vector3(0.65f, 11.2f, side), new Vector3(0.55f, 2.4f, 0.1f), Quaternion.Euler(0f, 0f, -28f), letter);
            }
            glow.Box(sign + new Vector3(0f, 10.5f, 0f), new Vector3(5.1f, 5.1f, 0.3f), 0f, new Color(0.25f, 0.4f, 0.85f));
        }

        // ---------------------------------------------------------------- Buildings, parking, trees, outskirts

        void BuildParking(SceneryBatch day, System.Random r)
        {
            // North-west of the stadium, between it and the festival (the grey lot on the plan).
            var c = ToWorld(new Vector2(-70f, 175f));
            float x0 = c.x - 45f, x1 = c.x + 45f, z0 = c.y - 28f, z1 = c.y + 28f;
            Strip(day, x0, z0, x1, z1, new Color(0.3f, 0.3f, 0.32f), 0.03f);
            for (float x = x0 + 4f; x < x1 - 3f; x += 7f)
                for (float z = z0 + 3f; z < z1 - 3f; z += 2.7f)
                    if (r.NextDouble() < 0.7) Car(day, r, x, z, 90f + Range(r, -4f, 4f));
            roadSegments.Add((new Vector2(x0, c.y), new Vector2(x1, c.y), 30f)); // keep trees and buildings off it
        }

        void BuildBuildings(SceneryBatch day, SceneryBatch glow, System.Random r)
        {
            var facades = new[] { new Color(0.78f, 0.76f, 0.72f), new Color(0.7f, 0.7f, 0.7f), new Color(0.82f, 0.78f, 0.7f), new Color(0.62f, 0.64f, 0.66f), new Color(0.74f, 0.66f, 0.58f) };
            var glass = new Color(0.18f, 0.22f, 0.28f);
            var lit = new Color(1f, 0.84f, 0.55f);
            var stadium = SiteCentre(VenueMap.CityStadium);
            int placed = 0, windows = 0;
            // The business block east of the stadium (the big grey buildings on the plan), then scattered low buildings.
            var blocks = new List<(Vector2 centre, float w, float d, float h)>
            {
                (ToWorld(new Vector2(185f, -55f)), 46f, 30f, 22f),
                (ToWorld(new Vector2(200f, 55f)), 30f, 40f, 30f),
                (ToWorld(new Vector2(150f, 105f)), 24f, 24f, 16f),
            };
            for (int attempt = 0; attempt < 2000 && placed < 120; attempt++)
            {
                float angle = Range(r, 0f, Mathf.PI * 2f), distance = Range(r, 120f, 400f);
                var p = stadium + new Vector2(Mathf.Cos(angle) * distance, Mathf.Sin(angle) * distance);
                float w = Range(r, 12f, 30f), d = Range(r, 12f, 26f);
                bool east = p.x > stadium.x + 120f;
                float h = east ? Range(r, 10f, 30f) : Range(r, 5f, 13f);
                blocks.Add((p, w, d, h));
                placed++;
            }
            foreach (var (p, w, d, h) in blocks)
            {
                if (Occupied(p, Mathf.Max(w, d) * 0.6f + 3f)) continue;
                var colour = Pick(r, facades);
                var c = new Vector3(p.x, h * 0.5f, p.y);
                day.Box(c, new Vector3(w, h, d), 0f, colour);
                day.Box(new Vector3(p.x, h + 0.3f, p.y), new Vector3(w + 0.4f, 0.6f, d + 0.4f), 0f, Color.Lerp(colour, Color.black, 0.25f)); // roof edge
                if (r.NextDouble() < 0.6)
                    day.Box(new Vector3(p.x + Range(r, -w * 0.25f, w * 0.25f), h + 1.2f, p.y + Range(r, -d * 0.25f, d * 0.25f)), new Vector3(Range(r, 2f, 5f), 1.8f, Range(r, 2f, 4f)), 0f, new Color(0.5f, 0.51f, 0.53f));
                for (float y = 2.2f; y < h - 1f; y += 3.4f)
                {
                    day.Box(new Vector3(p.x, y, p.y), new Vector3(w + 0.1f, 1.2f, d + 0.1f), 0f, glass);
                    if (windows > 7000) continue;
                    for (int side = 0; side < 4; side++)
                    {
                        bool alongX = side < 2;
                        float length = alongX ? w : d;
                        for (float s = -length * 0.5f + 1.5f; s < length * 0.5f - 1.5f; s += 3f)
                        {
                            if (r.NextDouble() > 0.35) continue;
                            Vector3 q = alongX
                                ? new Vector3(p.x + s, y, side == 0 ? p.y - d * 0.5f - 0.08f : p.y + d * 0.5f + 0.08f)
                                : new Vector3(side == 2 ? p.x - w * 0.5f - 0.08f : p.x + w * 0.5f + 0.08f, y, p.y + s);
                            glow.Box(q, alongX ? new Vector3(2.2f, 1f, 0.05f) : new Vector3(0.05f, 1f, 2.2f), 0f, lit);
                            windows++;
                        }
                    }
                }
                roadSegments.Add((p, p, Mathf.Max(w, d) * 0.6f)); // later buildings and trees keep clear
            }
        }

        void BuildTrees(SceneryBatch day, System.Random r)
        {
            // Clusters where the plan has them (district coordinates), then scattered trees and a forest edge to the west.
            var clusters = new[]
            {
                new Vector2(-150f, 150f), new Vector2(-280f, 160f), new Vector2(-110f, 95f), new Vector2(-40f, 150f),
                new Vector2(90f, 160f), new Vector2(140f, 30f), new Vector2(150f, -110f), new Vector2(-40f, -120f),
                new Vector2(-240f, -60f), new Vector2(-300f, -10f), new Vector2(40f, -230f), new Vector2(-60f, -280f),
                new Vector2(220f, 200f), new Vector2(-150f, 320f), new Vector2(110f, -170f),
            };
            foreach (var cluster in clusters)
            {
                var centre = ToWorld(cluster);
                int count = r.Next(18, 40);
                for (int n = 0; n < count; n++)
                {
                    var p = centre + new Vector2(Range(r, -32f, 32f), Range(r, -32f, 32f));
                    if (!Occupied(p, 2f)) Tree(day, r, p.x, p.y);
                }
            }
            var stadium = SiteCentre(VenueMap.CityStadium);
            for (int n = 0; n < 900; n++)
            {
                float angle = Range(r, 0f, Mathf.PI * 2f), distance = Range(r, 90f, 440f);
                var p = stadium + new Vector2(Mathf.Cos(angle) * distance, Mathf.Sin(angle) * distance);
                bool west = Mathf.Cos(angle) < -0.2f;
                float forest = CrowdSimulation.SmoothNoise(p.x * 0.012f + 10f, p.y * 0.012f + 4f);
                if (forest < (west ? 0.5f : 0.62f) && r.NextDouble() > 0.05) continue;
                if (!Occupied(p, 2f)) Tree(day, r, p.x, p.y);
            }
        }

        void BuildOutskirts(SceneryBatch day, SceneryBatch glow, System.Random r)
        {
            var stadium = SiteCentre(VenueMap.CityStadium);
            var hills = new[] { new Color(0.2f, 0.33f, 0.2f), new Color(0.24f, 0.37f, 0.22f), new Color(0.18f, 0.3f, 0.21f) };
            var skyline = new[] { new Color(0.5f, 0.53f, 0.58f), new Color(0.44f, 0.47f, 0.53f), new Color(0.58f, 0.6f, 0.63f) };
            for (int n = 0; n < 30; n++)
            {
                float angle = n / 30f * Mathf.PI * 2f + Range(r, -0.05f, 0.05f);
                bool east = Mathf.Cos(angle) > 0.35f;
                if (east) continue;
                float distance = Range(r, 470f, 540f);
                var p = stadium + new Vector2(Mathf.Cos(angle) * distance, Mathf.Sin(angle) * distance);
                day.Mesh(sphereMesh, new Vector3(p.x, -8f, p.y), new Vector3(Range(r, 180f, 280f), Range(r, 50f, 110f), Range(r, 90f, 150f)),
                    Quaternion.Euler(0f, -angle * Mathf.Rad2Deg + 90f, 0f), Pick(r, hills));
            }
            // The city starts to the east: a skyline on the horizon with aircraft warning lights.
            for (int n = 0; n < 90; n++)
            {
                float angle = Range(r, -1.15f, 1.15f), distance = Range(r, 470f, 560f);
                var p = stadium + new Vector2(Mathf.Cos(angle) * distance, Mathf.Sin(angle) * distance);
                float height = Range(r, 25f, 120f), w = Range(r, 16f, 40f);
                day.Box(new Vector3(p.x, height * 0.5f, p.y), new Vector3(w, height, w), Range(r, 0f, 90f), Pick(r, skyline));
                if (r.NextDouble() < 0.6) glow.Box(new Vector3(p.x, height + 0.6f, p.y), Vector3.one, 0f, new Color(1f, 0.25f, 0.2f));
            }
        }

        // ---------------------------------------------------------------- Mesh batching

        /// <summary>Collects boxes (and other meshes) per colour and merges each colour into one mesh.</summary>
        sealed class SceneryBatch
        {
            readonly Dictionary<Color, List<CombineInstance>> parts = new();
            static Mesh cube;

            public void Box(Vector3 centre, Vector3 size, float yaw, Color color) => Box(centre, size, Quaternion.Euler(0f, yaw, 0f), color);

            public void Box(Vector3 centre, Vector3 size, Quaternion rotation, Color color)
            {
                if (cube == null) cube = PrimitiveMesh(PrimitiveType.Cube);
                Mesh(cube, centre, size, rotation, color);
            }

            public void Mesh(Mesh mesh, Vector3 centre, Vector3 size, Quaternion rotation, Color color)
            {
                if (!parts.TryGetValue(color, out var list)) parts[color] = list = new List<CombineInstance>();
                list.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.TRS(centre, rotation, size) });
            }

            public List<GameObject> Build(Transform parent, string name, Func<Color, Material> material)
            {
                var built = new List<GameObject>();
                foreach (var kv in parts)
                {
                    for (int start = 0; start < kv.Value.Count; start += 6000)
                    {
                        int count = Math.Min(6000, kv.Value.Count - start);
                        var mesh = new UnityEngine.Mesh { name = name, indexFormat = IndexFormat.UInt32 };
                        mesh.CombineMeshes(kv.Value.GetRange(start, count).ToArray(), true, true);
                        mesh.RecalculateBounds();
                        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)) { layer = LandscapeLayer };
                        go.transform.SetParent(parent, false);
                        go.GetComponent<MeshFilter>().sharedMesh = mesh;
                        var renderer = go.GetComponent<MeshRenderer>();
                        renderer.sharedMaterial = material(kv.Key);
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                        built.Add(go);
                    }
                }
                return built;
            }
        }
    }
}
