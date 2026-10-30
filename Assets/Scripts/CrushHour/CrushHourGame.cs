using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace CrushHour
{
    /// <summary>
    /// Crush Hour prototype: top-down crowd safety at a summer music festival.
    /// Plan with barriers, zone patrols and police, start the show, then get everyone out when the fire breaks out,
    /// using emergency gates and your team. Scored on people saved, injuries, panic attacks and time.
    /// </summary>
    public class CrushHourGame : MonoBehaviour
    {
        enum Phase { Planning, Live, Results }
        enum Tool { Command, Barrier }
        enum Role { Patrolman, Megaphone }
        enum Severity { Info, Good, Warning, Danger }
        /// <summary>Escorting a drunk person: walking to them, walking them to a door, walking back to the post.</summary>
        enum EscortPhase { None, Fetching, Escorting, Returning }
        /// <summary>Zone order directions, relative to the way the patrolman is facing.</summary>
        enum OrderSide { Front, Back, Left, Right }

        /// <summary>A zone patrolman or police officer the player can send around.</summary>
        class Unit
        {
            public Role role;
            public int number;
            /// <summary>Patrolmen: the zone (index into venue.regions) they look after. -1 for police.</summary>
            public int homeRegion = -1;
            /// <summary>Patrolmen: the way they're looking, in grid coordinates (x, y), unit length. Drawn as an arrow on the ground.</summary>
            public Vector2 facing = Vector2.up;
            /// <summary>Display name when set (patrolmen: "Food Court – Patrol").</summary>
            public string label;
            public Vector2 position;
            public readonly List<Vector2> path = new();
            public Transform root;
            public LineRenderer ring, arrow;
            /// <summary>Patrolmen: the door their last zone order sent people to (-1 none), drawn as a big ground arrow.</summary>
            public int orderDoor = -1;
            public LineRenderer orderArrow;
            /// <summary>Where the unit started (its post); it walks back here after an escort.</summary>
            public Vector2 home;
            public EscortPhase escortPhase;
            /// <summary>Drunk person being fetched or escorted (-1 none), and the door they're being taken to.</summary>
            public int escortee = -1, escortDoor = -1;
            public float repathTimer;
            public Vector2 heading = Vector2.up;
            public Transform wave;          // megaphone announcement ripple
            public float announceTimer, waveTime = 1f;
            public bool hasAnnounced;
            public string Name => label ?? $"{RoleName(role)} {number}";
        }

        [Header("Materials (fallbacks are created when empty)")]
        public Material litMaterial;
        public Material unlitMaterial;

        [Header("Crowd look (optional)")]
        [Tooltip("Character prefabs to draw people with, e.g. from 'Low Poly People'. Empty = coloured cubes.\nTools > Crush Hour > Use Person Models fills this.")]
        public GameObject[] personPrefabs;
        [Tooltip("Idle / walk / run / wave clips baked into vertex animation textures. Matched to prefabs by name (\"normal man a\" uses \"normal walk\").")]
        public AnimationClip[] crowdClips;
        [Tooltip("CrushHour/CrowdVAT. Referenced here so it's included in builds.")]
        public Shader crowdAnimationShader;
        public float personHeight = 1.75f;
        [Tooltip("Turn the models if they don't face +Z (forward).")]
        public float modelYawOffset;
        [Tooltip("Coloured tile under each person showing their state (only with person models).")]
        public bool stateMarkers = true;

        [Header("Rules")]
        public int barrierBudget = 60;
        [Tooltip("Police officers with megaphones who announce the situation to everyone in earshot.")]
        public int megaphoneOfficers = 2;
        [Tooltip("People in this scene (0 = use crowd.agentCount). Settings upgrades never reset it (unlike crowd.agentCount), so a test scene can keep a small crowd.")]
        [Min(0)] public int crowdSizeOverride;
        public float evacuationTimeLimit = 240f;
        public int maxFireCells = 400;
        [FormerlySerializedAs("stewardSpeed")] public float patrolSpeed = 3.5f;
        public float policeSpeed = 4f;
        public float policeAnnounceInterval = 5f;

        public CrowdSettings crowd = new();

        /// <summary>
        /// Bump when the tuned defaults change: scenes saved with older values get the new rules, team size and
        /// crowd settings instead of silently keeping stale serialized ones.
        /// </summary>
        // Version 5: one patrolman per zone replaces the 4 stewards (crowd.patrolRadius is new).
        // Version 6: drunk people (crowd.drunkFraction 0.02 and friends).
        const int CurrentSettingsVersion = 6;
        [SerializeField, HideInInspector] int settingsVersion;

        void OnValidate() => UpgradeSettings();
        void Awake() => UpgradeSettings();

        void UpgradeSettings()
        {
            if (settingsVersion >= CurrentSettingsVersion) return;
            megaphoneOfficers = 2;
            maxFireCells = 400;
            crowd = new CrowdSettings();
            settingsVersion = CurrentSettingsVersion;
        }

        const int W = FestivalVenue.Width, H = FestivalVenue.Height;
        const float SimStep = 0.04f;
        const int MaxStepsPerFrame = 6;
        static readonly float[] Speeds = { 1f, 2f };
        static readonly Vector2[] MegaphonePosts = { new(40, 52), new(64, 52) };

        static readonly Color[] AgentColors =
        {
            new(0.3f, 0.55f, 0.95f),  // calm / unaware
            new(0.15f, 0.85f, 0.75f), // following directions
            new(1f, 0.6f, 0.15f),     // alarmed
            new(0.95f, 0.1f, 0.1f),   // panicking
            new(0.3f, 0.05f, 0.3f),   // injured
            new(1f, 0.45f, 0.85f),    // panic attack
            new(0.62f, 0.68f, 0.2f),  // intoxicated by smoke
            new(0.82f, 0.62f, 1f),    // drunk
        };
        static readonly Color PatrolColor = new(1f, 0.92f, 0.2f); // hi-vis vest; the cap and arrow take the zone colour
        static readonly Color PoliceColor = new(0.08f, 0.14f, 0.42f);
        static readonly Color MegaphoneColor = new(0.35f, 0.85f, 1f);
        static readonly Color FenceColor = new(0.25f, 0.25f, 0.27f);
        static readonly Color BarrierColor = new(0.9f, 0.9f, 0.85f);
        static readonly Color GateClosedColor = new(0.9f, 0.25f, 0.2f);
        static readonly Color GateOpeningColor = new(1f, 0.65f, 0.15f);
        static readonly Color GateOpenColor = new(0.25f, 0.85f, 0.4f);
        static readonly Color FireColor = new(1f, 0.35f, 0f);
        static readonly Color Grass = new(0.3f, 0.48f, 0.24f);
        static readonly Color Asphalt = new(0.28f, 0.28f, 0.3f);
        static readonly Color Scorched = new(0.12f, 0.08f, 0.05f);
        static readonly Color Blocked = new(0.18f, 0.18f, 0.18f);
        static readonly Color SmokeShade = new(0.14f, 0.14f, 0.16f);
        static readonly Color[] SmokeColors = { new(0.2f, 0.2f, 0.22f), new(0.3f, 0.3f, 0.32f), new(0.42f, 0.41f, 0.4f) };
        static readonly Vector3 Wind = new(0.7f, 0f, 0.35f);

        /// <summary>A rising voxel of smoke.</summary>
        class Puff
        {
            public Transform transform;
            public Vector3 velocity;
            public float age, life, size;
        }

        FestivalVenue venue;
        CrowdSimulation sim;
        CrowdRenderer crowdRenderer;
        readonly List<PersonModel> personModels = new();
        System.Random rng;
        int seed;

        Phase phase;
        Tool tool;
        bool paused, heatmap = true, incidentStarted, showRegions = true;
        int speedIndex, barriersUsed, lastInjured, lastSmokeInjured, lastAttacks, gatesOpened, selectedUnit = -1;
        float simAccumulator, groundTimer, warningTimer, attackWarningTime = -99f;
        float evacuationTime, lastProgressTime;
        /// <summary>This round's disasters. Empty until the show starts, then (by default) one fire at a random site.</summary>
        IncidentSchedule schedule;
        readonly List<Incident> startedIncidents = new();
        int lastSafe;
        string incidentSite;

        // Results
        int finalSafe, finalInjured, finalTrapped, finalAttacks, finalDrunkInside, finalScore, stars;
        string resultTitle, resultTip;

        Camera cam;
        TopDownCamera cameraRig;
        Transform world;
        Mesh cubeMesh;
        RenderParams[] agentRenderParams;
        Texture2D groundTexture;
        Material groundMaterial;
        Color32[] groundPixels;
        LineRenderer dragLine;
        bool dragging;
        int dragStartCell;

        readonly Dictionary<Color, Material> litCache = new();
        readonly Dictionary<Color, Material> unlitCache = new();
        readonly Dictionary<int, GameObject> barrierViews = new();
        readonly Dictionary<int, Transform> fireViews = new();
        readonly List<MeshRenderer> gateViews = new();
        readonly List<Unit> units = new();
        readonly List<Puff> smoke = new();
        float smokeBudget, lastSpotFireLog;
        int lastSpotFires;
        readonly List<Vector2> patrolPositions = new();
        const float RegionOrderCooldown = 5f;
        /// <summary>Seconds until each zone can be given another evacuation order (index = region).</summary>
        float[] regionOrderCooldown = Array.Empty<float>();
        /// <summary>Patrol whose order panel is drawn this OnGUI pass; fixed at the Layout event so the panel can't change mid-frame.</summary>
        int orderPanelUnit = -1;
        readonly List<Gate> changedGates = new();
        readonly List<(string text, Vector3 position)> labels = new();
        readonly List<(string text, Severity severity, string time)> log = new();
        readonly Dictionary<string, float> lastWarning = new();
        readonly List<Rect> guiRects = new();
        readonly List<Action> deferred = new();

        void Start()
        {
            cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
                cam = go.GetComponent<Camera>();
            }
            cam.farClipPlane = 600f;
            cameraRig = cam.GetComponent<TopDownCamera>();
            if (cameraRig == null) cameraRig = cam.gameObject.AddComponent<TopDownCamera>();

            var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            var bounds = new Bounds(new Vector3(W * 0.5f, 1f, H * 0.5f), new Vector3(W, 4f, H));
            agentRenderParams = new RenderParams[AgentColors.Length];
            for (int i = 0; i < AgentColors.Length; i++)
                agentRenderParams[i] = new RenderParams(Lit(AgentColors[i])) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };

            dragLine = new GameObject("Barrier Preview").AddComponent<LineRenderer>();
            dragLine.transform.SetParent(transform, false);
            dragLine.positionCount = 2;
            dragLine.widthMultiplier = 0.6f;
            dragLine.sharedMaterial = Unlit(BarrierColor);
            dragLine.shadowCastingMode = ShadowCastingMode.Off;
            dragLine.enabled = false;

            LoadPersonModels();
            NewGame();
        }

        void OnDestroy() => crowdRenderer?.Dispose();

        void LoadPersonModels()
        {
            if (personPrefabs == null || personPrefabs.Length == 0) return;
            var shader = crowdAnimationShader != null ? crowdAnimationShader : Shader.Find("CrushHour/CrowdVAT");
#if UNITY_EDITOR
            if (shader != null && UnityEditor.ShaderUtil.ShaderHasError(shader))
            {
                Debug.LogWarning("Crush Hour: CrowdVAT shader has errors, people won't be animated.");
                shader = null;
            }
#endif
            if (shader != null && !shader.isSupported) shader = null;
            foreach (var prefab in personPrefabs)
            {
                if (prefab == null) continue;
                var model = PersonModel.Build(prefab, Lit(Color.white), shader, crowdClips, personHeight, modelYawOffset);
                if (model != null) personModels.Add(model);
            }
            int animated = personModels.FindAll(m => m.Animated).Count;
            Debug.Log($"Crush Hour: {personModels.Count} person models loaded, {animated} animated.");
        }

        // ================================================================ Setup

        void NewGame()
        {
            if (world != null) Destroy(world.gameObject);
            if (groundTexture != null) Destroy(groundTexture);
            if (groundMaterial != null) Destroy(groundMaterial);
            crowdRenderer?.Dispose();
            world = new GameObject("Festival (generated)").transform;
            barrierViews.Clear();
            fireViews.Clear();
            gateViews.Clear();
            units.Clear();
            smoke.Clear();
            smokeBudget = 0f;
            lastSpotFires = 0;
            lastSpotFireLog = -99f;
            labels.Clear();
            log.Clear();
            lastWarning.Clear();

            seed = Environment.TickCount & int.MaxValue;
            rng = new System.Random(seed);
            venue = new FestivalVenue();
            if (crowdSizeOverride > 0) crowd.agentCount = Mathf.Max(100, crowdSizeOverride);
            sim = new CrowdSimulation(crowd, venue, rng);
            var bounds = new Bounds(new Vector3(W * 0.5f, 1f, H * 0.5f), new Vector3(W, 4f, H));
            crowdRenderer = new CrowdRenderer(sim, cubeMesh, agentRenderParams, personModels, bounds, rng) { ShowMarkers = stateMarkers };

            phase = Phase.Planning;
            tool = Tool.Command;
            paused = incidentStarted = dragging = false;
            selectedUnit = -1;
            barriersUsed = lastInjured = lastSmokeInjured = lastAttacks = gatesOpened = lastSafe = 0;
            simAccumulator = evacuationTime = warningTimer = lastProgressTime = 0f;
            attackWarningTime = -99f;
            bannerTime = 0f;
            incidentSite = null;
            schedule = new IncidentSchedule(maxFireCells);
            dragLine.enabled = false;

            BuildVenueViews();
            SpawnUnits();
            regionOrderCooldown = new float[venue.regions.Count];
            cameraRig.Focus(new Vector3(W * 0.5f, 0f, H * 0.45f), 85f);

            Log($"{sim.count} fans are enjoying the Summer Music Festival.");
            Log("Get ready: place your team and barriers, then start the show.");
        }

        void BuildVenueViews()
        {
            // Ground: one quad whose texture shows the cells and the crowd pressure map.
            groundTexture = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            groundPixels = new Color32[W * H];
            groundMaterial = unlitMaterial != null ? new Material(unlitMaterial) : new Material(FindShader("Universal Render Pipeline/Unlit"));
            groundMaterial.mainTexture = groundTexture;
            groundMaterial.color = Color.white;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ground.name = "Ground";
            Destroy(ground.GetComponent<Collider>());
            ground.transform.SetParent(world, false);
            ground.transform.SetPositionAndRotation(new Vector3(W * 0.5f, 0f, H * 0.5f), Quaternion.Euler(90f, 0f, 0f));
            ground.transform.localScale = new Vector3(W, H, 1f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundMaterial;
            UpdateGroundTexture();

            // Fence, merged into horizontal runs.
            for (int y = 0; y < H; y++)
            {
                int x = 0;
                while (x < W)
                {
                    if (venue.cells[FestivalVenue.Index(x, y)] != CellType.Wall) { x++; continue; }
                    int start = x;
                    while (x < W && venue.cells[FestivalVenue.Index(x, y)] == CellType.Wall) x++;
                    CreateBlock("Fence", new RectInt(start, y, x - start, 1), 1.2f, FenceColor);
                }
            }

            var labelPositions = new Dictionary<string, (Vector3 sum, int n)>();
            foreach (var l in venue.landmarks)
            {
                if (l.solid) CreateBlock(l.name, l.rect, l.height, l.color, 0.15f);
                var p = new Vector3(l.rect.center.x, l.height + 0.5f, l.rect.center.y);
                labelPositions[l.name] = labelPositions.TryGetValue(l.name, out var acc) ? (acc.sum + p, acc.n + 1) : (p, 1);
            }
            foreach (var kv in labelPositions) labels.Add((kv.Key.ToUpperInvariant(), kv.Value.sum / kv.Value.n));

            foreach (var g in venue.gates)
                gateViews.Add(CreateBlock(g.name, g.rect, 1.4f, GateClosedColor).GetComponent<MeshRenderer>());
        }

        GameObject CreateBlock(string name, RectInt rect, float height, Color color, float inset = 0f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(world, false);
            go.transform.localPosition = new Vector3(rect.center.x, height * 0.5f, rect.center.y);
            go.transform.localScale = new Vector3(rect.width - inset, height, rect.height - inset);
            go.GetComponent<MeshRenderer>().sharedMaterial = Lit(color);
            return go;
        }

        void SpawnUnits()
        {
            // One patrolman per zone, standing at the zone's post.
            for (int r = 0; r < venue.regions.Count; r++)
            {
                var region = venue.regions[r];
                var post = new Vector2(region.post.x + 0.5f, region.post.y + 0.5f);
                var unit = SpawnUnit(Role.Patrolman, r, post);
                unit.homeRegion = r;
                unit.label = $"{region.name} \u2013 Patrol";
                unit.root.name = unit.label;
                // Face the middle of the zone; if the post already is the middle, face the middle of the grounds.
                Vector2 look = region.Center - post;
                if (look.sqrMagnitude < 1f) look = new Vector2(W * 0.5f, H * 0.5f) - post;
                SetFacing(unit, look);
            }
            for (int i = 0; i < megaphoneOfficers; i++)
            {
                Vector2 post = i < MegaphonePosts.Length ? MegaphonePosts[i] : new Vector2(rng.Next(10, 94) + 0.5f, rng.Next(10, 60) + 0.5f);
                if (!venue.IsWalkable(FestivalVenue.CellAt(post))) post = MegaphonePosts[0];
                SpawnUnit(Role.Megaphone, i, post);
            }
        }

        Unit SpawnUnit(Role role, int index, Vector2 post)
        {
            var unit = new Unit { role = role, number = index + 1, position = post, home = post };
            var root = unit.root = new GameObject(unit.Name).transform;
            root.SetParent(world, false);
            root.position = new Vector3(post.x, 0f, post.y);
            if (role == Role.Patrolman)
            {
                var zoneColor = venue.regions[index].color;
                AddPart(root, new Vector3(0f, 0.9f, 0f), new Vector3(0.6f, 1.8f, 0.6f), PatrolColor);
                AddPart(root, new Vector3(0f, 1.9f, 0f), new Vector3(0.7f, 0.2f, 0.7f), zoneColor); // cap in the zone's colour
                var arrowObject = new GameObject("Facing");
                arrowObject.transform.SetParent(root, false);
                var arrow = unit.arrow = arrowObject.AddComponent<LineRenderer>();
                arrow.useWorldSpace = false;
                arrow.positionCount = 5;
                arrow.widthMultiplier = 0.18f;
                arrow.sharedMaterial = Unlit(zoneColor);
                arrow.shadowCastingMode = ShadowCastingMode.Off;
            }
            else
            {
                AddPart(root, new Vector3(0f, 0.9f, 0f), new Vector3(0.6f, 1.8f, 0.6f), PoliceColor);
                AddPart(root, new Vector3(0f, 1.9f, 0f), new Vector3(0.7f, 0.2f, 0.7f), MegaphoneColor); // cap
                var wave = new GameObject("Announcement").transform;
                wave.SetParent(root, false);
                var line = AddCircle(wave.gameObject, 1f, MegaphoneColor);
                line.widthMultiplier = 0.25f;
                line.enabled = false;
                unit.wave = wave;
            }
            unit.ring = AddCircle(root.gameObject, RoleRadius(role), RoleColor(role));
            units.Add(unit);
            return unit;
        }

        /// <summary>Turns a patrolman to look along <paramref name="direction"/> (grid x, y) and redraws their ground arrow.</summary>
        void SetFacing(Unit unit, Vector2 direction)
        {
            if (direction.sqrMagnitude < 1e-6f) return;
            unit.facing = direction.normalized;
            if (unit.arrow == null) return;
            var forward = new Vector3(unit.facing.x, 0f, unit.facing.y);
            var side = new Vector3(-unit.facing.y, 0f, unit.facing.x);
            var lift = new Vector3(0f, 0.1f, 0f);
            Vector3 tip = forward * 2.4f + lift;
            unit.arrow.SetPosition(0, forward * 0.7f + lift);
            unit.arrow.SetPosition(1, tip);
            unit.arrow.SetPosition(2, tip - forward * 0.6f + side * 0.45f);
            unit.arrow.SetPosition(3, tip);
            unit.arrow.SetPosition(4, tip - forward * 0.6f - side * 0.45f);
        }

        void AddPart(Transform parent, Vector3 position, Vector3 scale, Color color)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.GetComponent<MeshRenderer>().sharedMaterial = Lit(color);
        }

        LineRenderer AddCircle(GameObject owner, float radius, Color color)
        {
            var ring = owner.AddComponent<LineRenderer>();
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.positionCount = 48;
            ring.widthMultiplier = 0.12f;
            ring.sharedMaterial = Unlit(color);
            ring.shadowCastingMode = ShadowCastingMode.Off;
            for (int k = 0; k < ring.positionCount; k++)
            {
                float a = k * Mathf.PI * 2f / ring.positionCount;
                ring.SetPosition(k, new Vector3(Mathf.Cos(a) * radius, 0.08f, Mathf.Sin(a) * radius));
            }
            return ring;
        }

        float RoleRadius(Role role) => role == Role.Megaphone ? crowd.policeAnnounceRadius : crowd.patrolRadius;
        static Color RoleColor(Role role) => role == Role.Megaphone ? MegaphoneColor : PatrolColor;
        static string RoleName(Role role) => role == Role.Megaphone ? "Police" : "Patrol";
        Color UnitColor(Unit u) => u.homeRegion >= 0 ? venue.regions[u.homeRegion].color : RoleColor(u.role);

        static string RoleDescription(Role role) => role == Role.Megaphone
            ? "Megaphone police: once the fire starts, announces it every few seconds to everyone in the big ring and tells them which gates are open."
            : "Zone patrol: panicked people in the ring stop and get calmed down first, then are told which gates are open and leave in orderly, spaced-out queues. Also helps people through panic attacks. Once the fire starts, select them and press an arrow key to send their whole zone out that way.";

        Material Lit(Color color)
        {
            if (litCache.TryGetValue(color, out var m)) return m;
            m = litMaterial != null ? new Material(litMaterial) : new Material(FindShader("Universal Render Pipeline/Lit"));
            m.color = color;
            m.enableInstancing = true;
            return litCache[color] = m;
        }

        Material Unlit(Color color)
        {
            if (unlitCache.TryGetValue(color, out var m)) return m;
            m = unlitMaterial != null ? new Material(unlitMaterial) : new Material(FindShader("Universal Render Pipeline/Unlit"));
            m.color = color;
            return unlitCache[color] = m;
        }

        static Shader FindShader(string name)
        {
            var shader = Shader.Find(name);
            return shader != null ? shader : Shader.Find("Standard");
        }

        // ================================================================ Loop

        void Update()
        {
            if (deferred.Count > 0)
            {
                var actions = deferred.ToArray();
                deferred.Clear();
                foreach (var action in actions) action();
            }

            HandleKeyboard();
            HandleMouse();

            float simDt = 0f;
            if (phase != Phase.Results && !paused)
            {
                simAccumulator += Time.deltaTime * Speeds[speedIndex];
                int steps = 0;
                while (simAccumulator >= SimStep && steps < MaxStepsPerFrame && phase != Phase.Results)
                {
                    Simulate(SimStep);
                    simAccumulator -= SimStep;
                    simDt += SimStep;
                    steps++;
                }
                if (steps == MaxStepsPerFrame) simAccumulator = 0f; // can't keep up: run slower rather than spiral
            }

            UpdateViews();
            UpdateSmoke(paused ? 0f : phase == Phase.Results ? Time.deltaTime : Time.deltaTime * Speeds[speedIndex]);
            crowdRenderer.ShowMarkers = stateMarkers;
            crowdRenderer.Render(paused ? 0f : Mathf.Max(simDt, phase == Phase.Results ? Time.deltaTime : 0f));
        }

        void Simulate(float dt)
        {
            if (venue.UpdateGates(dt, changedGates))
            {
                foreach (var g in changedGates)
                {
                    if (!g.Passable)
                        foreach (var p in g.rect.allPositionsWithin) sim.Evict(FestivalVenue.Index(p.x, p.y), venue);
                    Log(g.Passable ? $"{g.name} is open." : $"{g.name} is closed.", g.Passable ? Severity.Good : Severity.Info);
                }
            }

            if (phase == Phase.Live)
            {
                bool fireChanged = schedule.Update(dt, venue, sim, rng, startedIncidents);
                foreach (var incident in startedIncidents) OnIncidentStarted(incident);
                if (fireChanged) OnFireChanged();
            }

            venue.UpdateDoorFields(3);
            MoveUnits(dt);
            UpdateEscorts(dt);
            sim.Step(dt, venue, incidentStarted);
            sim.ApplyStewards(patrolPositions, dt, incidentStarted, crowd.patrolRadius);
            if (!incidentStarted) return;

            UpdateMegaphones(dt);
            for (int r = 0; r < regionOrderCooldown.Length; r++) regionOrderCooldown[r] = Mathf.Max(0f, regionOrderCooldown[r] - dt);
            bool wasPlaying = !sim.ShowStopped;
            evacuationTime += dt;
            CheckWarnings(dt);
            if (wasPlaying && sim.ShowStopped)
                Log("The band has stopped playing. Everyone is starting to realise something is wrong.", Severity.Warning);
            if (sim.SafeCount != lastSafe)
            {
                lastSafe = sim.SafeCount;
                lastProgressTime = evacuationTime;
            }
            // Everyone left is trying to get out but nobody has made it for a while: they're trapped.
            // Drunk people never leave on their own, so the round ends on the sober ones.
            bool stalled = sim.EvacuatingCount == sim.SoberActiveCount && evacuationTime - lastProgressTime > 20f;
            if (sim.SoberActiveCount == 0 || stalled || evacuationTime >= evacuationTimeLimit) EndGame();
        }

        void StartShow()
        {
            if (phase != Phase.Planning) return;
            phase = Phase.Live;
            welcomeDismissed = true;
            // Default round: one fire at a random site, 4-12 s into the show (levels will fill the schedule themselves).
            if (schedule.incidents.Count == 0) schedule.Add(new FireIncident(rng) { startTime = 4f + (float)rng.NextDouble() * 8f });
            Log("The headliner is on stage. Stay sharp...");
        }

        /// <summary>An incident just began: the first one starts the evacuation clock; each gets a feed line, banner and camera jump.</summary>
        void OnIncidentStarted(Incident incident)
        {
            if (!incidentStarted)
            {
                incidentSite = incident.SiteName;
                incidentStarted = true;
                evacuationTime = 0f;
            }
            SyncFireViews();
            Log(incident.LogText, Severity.Danger);
            ShowBanner(incident.Headline, incident.Advice, Severity.Danger);
            var focus = incident.FocusCell;
            cameraRig.Focus(new Vector3(focus.x, 0f, focus.y));
        }

        /// <summary>The fire spread this step: new flame cubes, and a feed line for new ember spot fires (at most every 6 s).</summary>
        void OnFireChanged()
        {
            SyncFireViews();
            if (venue.SpotFires > lastSpotFires)
            {
                lastSpotFires = venue.SpotFires;
                if (evacuationTime - lastSpotFireLog > 6f)
                {
                    lastSpotFireLog = evacuationTime;
                    Log($"Embers started a new fire near {venue.DescribeLocation(venue.LastSpotFireCell)}!", Severity.Danger);
                }
            }
        }

        void SyncFireViews()
        {
            foreach (int c in venue.fireCells)
            {
                if (fireViews.ContainsKey(c)) continue;
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Fire";
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(world, false);
                go.GetComponent<MeshRenderer>().sharedMaterial = Unlit(FireColor);
                fireViews[c] = go.transform;
            }
            // Barriers that caught fire are gone.
            List<int> burnt = null;
            foreach (var kv in barrierViews)
                if (venue.cells[kv.Key] != CellType.Barrier) (burnt ??= new List<int>()).Add(kv.Key);
            if (burnt == null) return;
            foreach (int c in burnt)
            {
                Destroy(barrierViews[c]);
                barrierViews.Remove(c);
            }
        }

        /// <summary>Voxel smoke: puffs rise from random burning cells and drift with the wind; more fire, more smoke.</summary>
        void UpdateSmoke(float dt)
        {
            if (dt <= 0f) return;
            if (venue.fireCells.Count > 0)
            {
                smokeBudget += dt * Mathf.Min(45f, 3f + venue.fireCells.Count * 0.25f);
                while (smokeBudget >= 1f)
                {
                    smokeBudget -= 1f;
                    SpawnPuff();
                }
            }
            foreach (var puff in smoke)
            {
                if (!puff.transform.gameObject.activeSelf) continue;
                puff.age += dt;
                if (puff.age >= puff.life)
                {
                    puff.transform.gameObject.SetActive(false);
                    continue;
                }
                float t = puff.age / puff.life;
                puff.transform.localPosition += puff.velocity * dt;
                puff.transform.localScale = Vector3.one * (puff.size * Mathf.Sin(t * Mathf.PI) * (0.6f + t));
                puff.transform.Rotate(0f, 40f * dt, 0f);
            }
        }

        void SpawnPuff()
        {
            Puff puff = null;
            foreach (var p in smoke)
            {
                if (p.transform.gameObject.activeSelf) continue;
                puff = p;
                break;
            }
            if (puff == null)
            {
                if (smoke.Count >= 160) return;
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Smoke";
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(world, false);
                var renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = Unlit(SmokeColors[smoke.Count % SmokeColors.Length]);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                puff = new Puff { transform = go.transform };
                smoke.Add(puff);
            }
            int cell = venue.fireCells[UnityEngine.Random.Range(0, venue.fireCells.Count)];
            var c = FestivalVenue.CellCenter(cell);
            puff.transform.localPosition = new Vector3(c.x + UnityEngine.Random.Range(-0.4f, 0.4f), venue.blockHeight[cell] + 1.6f, c.y + UnityEngine.Random.Range(-0.4f, 0.4f));
            puff.velocity = Wind + new Vector3(UnityEngine.Random.Range(-0.3f, 0.3f), UnityEngine.Random.Range(1.2f, 2.2f), UnityEngine.Random.Range(-0.3f, 0.3f));
            puff.age = 0f;
            puff.life = UnityEngine.Random.Range(2.5f, 4.5f);
            puff.size = UnityEngine.Random.Range(0.7f, 1.5f);
            puff.transform.localScale = Vector3.zero;
            puff.transform.gameObject.SetActive(true);
        }

        void UpdateMegaphones(float dt)
        {
            foreach (var u in units)
            {
                if (u.role != Role.Megaphone || u.escortPhase == EscortPhase.Escorting) continue; // hands full
                u.announceTimer -= dt;
                if (u.announceTimer > 0f) continue;
                u.announceTimer = policeAnnounceInterval;
                u.waveTime = 0f;
                int informed = sim.PoliceAnnouncement(u.position);
                if (!u.hasAnnounced || informed >= 25)
                    Log($"{u.Name} near {venue.DescribeLocation(FestivalVenue.CellAt(u.position))}: \"Fire at {incidentSite}! Walk to an open gate.\" ({informed} now know the way)");
                u.hasAnnounced = true;
            }
        }

        void MoveUnits(float dt)
        {
            patrolPositions.Clear();
            foreach (var u in units)
            {
                float budget = UnitSpeed(u) * dt;
                if (u.path.Count > 0 && (u.path[0] - u.position).sqrMagnitude > 1e-4f) u.heading = (u.path[0] - u.position).normalized;
                while (budget > 0f && u.path.Count > 0)
                {
                    Vector2 delta = u.path[0] - u.position;
                    float length = delta.magnitude;
                    if (length <= budget)
                    {
                        u.position = u.path[0];
                        u.path.RemoveAt(0);
                        budget -= length;
                    }
                    else
                    {
                        u.position += delta / length * budget;
                        budget = 0f;
                    }
                }
                // While walking someone out, the unit isn't calming anyone.
                if (u.role == Role.Patrolman && u.escortPhase != EscortPhase.Escorting) patrolPositions.Add(u.position);
            }
        }

        float UnitSpeed(Unit u) =>
            (u.role == Role.Patrolman ? patrolSpeed : policeSpeed) * (u.escortPhase == EscortPhase.Escorting ? EscortSpeedFactor : 1f);

        const float EscortPickupRange = 3f, EscortSpeedFactor = 0.7f, EscortFollowGap = 1f;

        /// <summary>
        /// "Escort nearest drunk" (button / F): the nearest drunk person nobody is escorting, in the patrol's own zone
        /// first, otherwise anywhere.
        /// </summary>
        void EscortNearestDrunk(int unitIndex)
        {
            if (unitIndex < 0 || unitIndex >= units.Count) return;
            var u = units[unitIndex];
            if (u.escortPhase == EscortPhase.Escorting) return; // already walking someone out
            int person = u.homeRegion >= 0 ? sim.NearestUnescortedDrunk(u.position, u.homeRegion, venue) : -1;
            if (person < 0) person = sim.NearestUnescortedDrunk(u.position, -1, venue);
            if (person < 0)
            {
                Log("No drunk fans left to escort.", Severity.Good);
                return;
            }
            StartEscort(unitIndex, person);
        }

        /// <summary>Starts an escort: the unit takes the drunk person now if within 3 m, otherwise walks to them first.</summary>
        void StartEscort(int unitIndex, int person)
        {
            var u = units[unitIndex];
            CancelEscort(u);
            for (int k = 0; k < units.Count; k++)
                if (units[k].escortee == person) CancelEscort(units[k]); // someone else was on it
            u.escortee = person;
            if ((sim.position[person] - u.position).sqrMagnitude <= EscortPickupRange * EscortPickupRange) BeginWalkOut(unitIndex);
            else
            {
                u.escortPhase = EscortPhase.Fetching;
                u.repathTimer = 0f;
                Log($"{u.Name} is going to get a drunk fan out.");
            }
        }

        /// <summary>Takes the person by the arm and heads for the nearest open door (by walking distance).</summary>
        void BeginWalkOut(int unitIndex)
        {
            var u = units[unitIndex];
            int person = u.escortee;
            sim.SetEscort(person, unitIndex);
            u.escortPhase = EscortPhase.Escorting;
            if (!RouteToNearestDoor(u))
            {
                Log($"{u.Name} can't find an open exit to take them to. Open a gate.", Severity.Warning);
                CancelEscort(u);
                return;
            }
            Log($"{u.Name} is walking a drunk fan out via {venue.doors[u.escortDoor].name}.", Severity.Good);
        }

        /// <summary>Paths the unit to the open door it can reach by the shortest walk (to a cell just inside it).</summary>
        bool RouteToNearestDoor(Unit u)
        {
            int cell = FestivalVenue.CellAt(u.position), bestDoor = -1;
            float best = float.PositiveInfinity;
            for (int d = 0; d < venue.doors.Count; d++)
            {
                var door = venue.doors[d];
                if (!door.IsOpen || !door.field.Reachable(cell) || door.field.distance[cell] >= best) continue;
                best = door.field.distance[cell];
                bestDoor = d;
            }
            if (bestDoor < 0) return false;
            var target = venue.doors[bestDoor];
            int approach = -1;
            float nearest = float.MaxValue;
            foreach (int a in target.approach)
            {
                if (!venue.IsWalkable(a)) continue;
                float sq = (FestivalVenue.CellCenter(a) - target.Center).sqrMagnitude;
                if (sq < nearest) { nearest = sq; approach = a; }
            }
            if (approach < 0) return false;
            var path = venue.FindPath(u.position, FestivalVenue.CellCenter(approach));
            if (path == null) return false;
            u.path.Clear();
            u.path.AddRange(path);
            u.escortDoor = bestDoor;
            return true;
        }

        /// <summary>Drops an escort (the person goes back to wandering) without sending the unit anywhere.</summary>
        void CancelEscort(Unit u)
        {
            if (u.escortee >= 0 && sim.escortedBy[u.escortee] >= 0) sim.SetEscort(u.escortee, -1);
            u.escortee = -1;
            u.escortDoor = -1;
            if (u.escortPhase != EscortPhase.Returning) u.escortPhase = EscortPhase.None;
        }

        void ReturnToPost(Unit u)
        {
            u.escortee = -1;
            u.escortDoor = -1;
            u.escortPhase = EscortPhase.Returning;
            var path = venue.FindPath(u.position, u.home);
            u.path.Clear();
            if (path != null) u.path.AddRange(path);
        }

        /// <summary>Runs every simulation step after MoveUnits: fetching, walking people out (1 m behind), returning.</summary>
        void UpdateEscorts(float dt)
        {
            for (int k = 0; k < units.Count; k++)
            {
                var u = units[k];
                switch (u.escortPhase)
                {
                    case EscortPhase.Fetching:
                    {
                        int person = u.escortee;
                        if (person < 0 || sim.state[person] != AgentState.Active) { CancelEscort(u); break; }
                        if ((sim.position[person] - u.position).sqrMagnitude <= EscortPickupRange * EscortPickupRange)
                        {
                            BeginWalkOut(k);
                            break;
                        }
                        u.repathTimer -= dt;
                        if (u.repathTimer <= 0f || u.path.Count == 0)
                        {
                            u.repathTimer = 1f; // they stagger about, so keep re-aiming
                            var path = venue.FindPath(u.position, sim.position[person]);
                            u.path.Clear();
                            if (path != null) u.path.AddRange(path);
                        }
                        break;
                    }
                    case EscortPhase.Escorting:
                    {
                        int person = u.escortee;
                        if (person < 0 || sim.state[person] != AgentState.Active)
                        {
                            // Hurt on the way (or already out): go back.
                            CancelEscort(u);
                            ReturnToPost(u);
                            break;
                        }
                        if (u.escortDoor < 0 || !venue.doors[u.escortDoor].IsOpen)
                        {
                            if (!RouteToNearestDoor(u)) { CancelEscort(u); ReturnToPost(u); break; }
                        }
                        float speed = UnitSpeed(u);
                        sim.SetEscortTarget(person, u.position - u.heading * EscortFollowGap, speed);
                        bool arrived = u.path.Count == 0;
                        bool together = (sim.position[person] - u.position).sqrMagnitude <= EscortPickupRange * EscortPickupRange;
                        if (arrived && together)
                        {
                            string door = venue.doors[u.escortDoor].name;
                            sim.EscortOut(person, u.escortDoor);
                            Log($"{u.Name} got a drunk fan out via {door}. ({sim.DrunkInsideCount} drunk still inside)", Severity.Good);
                            ReturnToPost(u);
                        }
                        break;
                    }
                    case EscortPhase.Returning:
                        if (u.path.Count == 0) u.escortPhase = EscortPhase.None;
                        break;
                }
            }
        }

        void CheckWarnings(float dt)
        {
            warningTimer -= dt;
            if (warningTimer > 0f) return;
            warningTimer = 2f;

            if (sim.MaxPressure > crowd.crushThreshold * 0.8f)
            {
                string where = venue.DescribeLocation(sim.MaxPressureCell);
                if (!lastWarning.TryGetValue(where, out float last) || evacuationTime - last > 10f)
                {
                    lastWarning[where] = evacuationTime;
                    Log($"Dangerous crush building near {where}!", Severity.Danger);
                }
            }
            if (sim.InjuredBySmoke > lastSmokeInjured)
            {
                Log($"{sim.InjuredBySmoke - lastSmokeInjured} people collapsed in the smoke. Keep people out of the plume downwind of the fire.", Severity.Danger);
                lastSmokeInjured = sim.InjuredBySmoke;
            }
            if (sim.InjuredCount > lastInjured)
            {
                Log($"{sim.InjuredCount - lastInjured} more people injured (total {sim.InjuredCount}: {sim.InjuredByCrush} crushed, {sim.InjuredByFire} burnt, {sim.InjuredBySmoke} smoke).", Severity.Danger);
                lastInjured = sim.InjuredCount;
            }
            if (sim.PanicAttacksTotal > lastAttacks)
            {
                if (evacuationTime - attackWarningTime > 8f)
                {
                    string where = "the crowd";
                    for (int i = 0; i < sim.count; i++)
                    {
                        if (sim.attackTime[i] <= 0f) continue;
                        where = venue.DescribeLocation(FestivalVenue.CellAt(sim.position[i]));
                        break;
                    }
                    Log($"Panic attack near {where}. A patrol nearby helps them recover.", Severity.Warning);
                    attackWarningTime = evacuationTime;
                }
                lastAttacks = sim.PanicAttacksTotal;
            }
        }

        void EndGame()
        {
            phase = Phase.Results;
            CancelDrag();
            finalSafe = sim.SafeCount;
            finalInjured = sim.InjuredCount;
            finalTrapped = sim.SoberActiveCount;
            finalDrunkInside = sim.DrunkInsideCount;
            finalAttacks = sim.PanicAttacksTotal;
            finalScore = finalSafe * 10 - finalInjured * 100 - finalTrapped * 25 - finalDrunkInside * 50 - finalAttacks * 10
                         + Mathf.Max(0, Mathf.RoundToInt(evacuationTimeLimit - evacuationTime)) * 10;

            float safeRatio = finalSafe / (float)sim.count;
            stars = 0;
            if (safeRatio >= 0.9f) stars = 1;
            if (stars == 1 && finalInjured <= sim.count / 100) stars = 2;
            if (stars == 2 && finalInjured == 0 && finalTrapped == 0 && finalDrunkInside == 0 && evacuationTime <= evacuationTimeLimit * 0.5f) stars = 3;

            resultTitle = finalTrapped == 0 ? "Festival evacuated" : evacuationTime >= evacuationTimeLimit ? "Time's up" : "Evacuation stalled";
            if (gatesOpened == 0)
                resultTip = "No emergency gate was opened. People tried the gates, found them shut and all squeezed through the two narrow entrances.";
            else if (sim.InjuredBySmoke > sim.InjuredByCrush && sim.InjuredBySmoke > 0)
                resultTip = "The smoke got people. It drifts downwind of the fire: open gates away from the plume and get people moving early.";
            else if (finalInjured > 0)
                resultTip = "Crushes happen where frightened crowds hit bottlenecks. Open gates early and move patrols to where the queues build up.";
            else if (finalAttacks > 10)
                resultTip = "Lots of panic attacks: people stuck in long queues or near the flames lose it. Spread them over more gates and keep patrols near jams.";
            else if (finalDrunkInside > 0 && finalTrapped == 0)
                resultTip = "Everyone made it out except the drunk ones. They ignore orders: a patrol has to walk them out.";
            else if (finalTrapped > 0)
                resultTip = "Some people never heard about the fire. Move the megaphone police to where people are still standing around.";
            else
                resultTip = "Clean evacuation! The crowd and the fire change every run. Try again.";
            Log($"{resultTitle}: {finalSafe} safe, {finalInjured} injured, {finalTrapped} still inside, {finalDrunkInside} drunk left inside.", stars >= 2 ? Severity.Good : Severity.Warning);
        }

        // ================================================================ Player actions

        void SetTool(Tool newTool)
        {
            tool = newTool;
            CancelDrag();
            if (tool == Tool.Barrier) selectedUnit = -1;
        }

        void ToggleGate(Gate gate)
        {
            gate.open = !gate.open;
            if (gate.open) gatesOpened++;
            Log(gate.open ? $"Opening {gate.name}..." : $"Closing {gate.name}...");
        }

        void OpenAllGates()
        {
            foreach (var g in venue.gates)
                if (!g.open) ToggleGate(g);
        }

        Unit SelectedPatrol() =>
            selectedUnit >= 0 && selectedUnit < units.Count && units[selectedUnit].role == Role.Patrolman ? units[selectedUnit] : null;

        static Vector2 SideDirection(Unit u, OrderSide side) => side switch
        {
            OrderSide.Front => u.facing,
            OrderSide.Back => -u.facing,
            OrderSide.Left => new Vector2(-u.facing.y, u.facing.x), // grid is x right, y up: left = 90° anticlockwise
            _ => new Vector2(u.facing.y, -u.facing.x),
        };

        bool CanOrderZone(Unit u) =>
            phase == Phase.Live && incidentStarted && u.homeRegion >= 0 && regionOrderCooldown[u.homeRegion] <= 0f;

        /// <summary>The patrol orders everyone in its zone out through the open door that best matches <paramref name="side"/>.</summary>
        void OrderZone(Unit u, OrderSide side)
        {
            if (u.homeRegion < 0) return;
            if (!incidentStarted)
            {
                Log("Zone orders become available once the fire starts.");
                return;
            }
            if (!CanOrderZone(u)) return; // still on cooldown
            var region = venue.regions[u.homeRegion];
            int people = sim.CountInRegion(u.homeRegion, venue);
            int door = sim.OrderRegionEvacuation(u.homeRegion, SideDirection(u, side), venue);
            regionOrderCooldown[u.homeRegion] = RegionOrderCooldown;
            u.orderDoor = door;
            UpdateOrderArrow(u);
            if (door >= 0)
                Log($"{region.name} patrol: evacuating via {venue.doors[door].name} ({people} people, {side.ToString().ToLowerInvariant()})", Severity.Good);
            else
                Log($"{region.name} patrol: no open exit they can reach. {people} people told to get out anyway.", Severity.Warning);
        }

        /// <summary>Big ground arrow from the patrolman to the door of their last zone order; hidden while that door is shut.</summary>
        void UpdateOrderArrow(Unit u)
        {
            bool show = u.orderDoor >= 0 && venue.doors[u.orderDoor].IsOpen;
            if (!show)
            {
                if (u.orderArrow != null && u.orderArrow.enabled) u.orderArrow.enabled = false;
                return;
            }
            if (u.orderArrow == null)
            {
                var go = new GameObject($"{u.Name} order");
                go.transform.SetParent(world, false);
                var line = u.orderArrow = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 5;
                line.widthMultiplier = 0.55f;
                line.sharedMaterial = Unlit(venue.regions[u.homeRegion].color);
                line.shadowCastingMode = ShadowCastingMode.Off;
            }
            var c = venue.doors[u.orderDoor].Center;
            var from = new Vector3(u.position.x, 0.15f, u.position.y);
            var to = new Vector3(c.x, 0.15f, c.y);
            Vector3 along = to - from;
            float length = along.magnitude;
            if (length < 2f)
            {
                u.orderArrow.enabled = false;
                return;
            }
            Vector3 f = along / length, sideways = new Vector3(-f.z, 0f, f.x);
            float head = Mathf.Min(3f, length * 0.4f);
            u.orderArrow.SetPosition(0, from + f * 1f);
            u.orderArrow.SetPosition(1, to);
            u.orderArrow.SetPosition(2, to - f * head + sideways * head * 0.6f);
            u.orderArrow.SetPosition(3, to);
            u.orderArrow.SetPosition(4, to - f * head - sideways * head * 0.6f);
            if (!u.orderArrow.enabled) u.orderArrow.enabled = true;
        }

        void SelectUnit(int index, bool focus)
        {
            selectedUnit = index;
            SetTool(Tool.Command);
            if (focus && index >= 0) cameraRig.Focus(new Vector3(units[index].position.x, 0f, units[index].position.y));
        }

        void CommandClick(Vector2 ground, int cell)
        {
            int nearest = NearestUnit(ground, 1.5f);
            // With someone selected, a click closer to a drunk person than to any unit means "escort them out".
            int clickedDrunk = selectedUnit >= 0 ? sim.NearestDrunk(ground, 1.2f) : -1;
            if (clickedDrunk >= 0 && (nearest < 0 ||
                (sim.position[clickedDrunk] - ground).sqrMagnitude < (units[nearest].position - ground).sqrMagnitude))
            {
                StartEscort(selectedUnit, clickedDrunk);
                return;
            }
            if (nearest >= 0)
            {
                selectedUnit = nearest;
                return;
            }

            var gate = venue.GateNear(cell);
            if (gate != null)
            {
                ToggleGate(gate);
                return;
            }

            int index = selectedUnit >= 0 ? selectedUnit : NearestUnit(ground, float.MaxValue);
            if (index < 0) return;
            if (units[index].escortPhase != EscortPhase.None)
            {
                CancelEscort(units[index]); // a new move order replaces the escort
                units[index].escortPhase = EscortPhase.None;
            }
            var path = venue.FindPath(units[index].position, ground);
            if (path == null)
            {
                Log($"{units[index].Name} can't get there.", Severity.Warning);
                return;
            }
            units[index].path.Clear();
            units[index].path.AddRange(path);
        }

        int NearestUnit(Vector2 point, float maxDistance)
        {
            int best = -1;
            float bestSq = maxDistance * maxDistance;
            for (int i = 0; i < units.Count; i++)
            {
                float d = (units[i].position - point).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = i;
                }
            }
            return best;
        }

        void PlaceBarrierLine(int from, int to)
        {
            bool outOfBudget = false;
            foreach (int c in LineCells(from, to))
            {
                if (venue.cells[c] != CellType.Open) continue;
                if (barriersUsed >= barrierBudget)
                {
                    outOfBudget = true;
                    break;
                }
                venue.TryPlaceBarrier(c);
                sim.Evict(c, venue);
                barrierViews[c] = CreateBlock("Barrier", new RectInt(c % W, c / W, 1, 1), 1f, BarrierColor, 0.1f);
                barriersUsed++;
            }
            if (outOfBudget) Log($"Out of barriers ({barrierBudget} sections). Right-click a barrier to pick it up.", Severity.Warning);
        }

        void RemoveBarrier(int cell)
        {
            if (!venue.TryRemoveBarrier(cell)) return;
            if (barrierViews.Remove(cell, out var view)) Destroy(view);
            barriersUsed--;
        }

        /// <summary>4-connected line, so a diagonal barrier has no gaps to squeeze through.</summary>
        static IEnumerable<int> LineCells(int from, int to)
        {
            int x0 = from % W, y0 = from / W, x1 = to % W, y1 = to / W;
            int dx = Mathf.Abs(x1 - x0), dy = -Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            while (true)
            {
                yield return FestivalVenue.Index(x0, y0);
                if (x0 == x1 && y0 == y1) yield break;
                int e2 = 2 * err;
                bool stepX = e2 >= dy, stepY = e2 <= dx;
                if (stepX)
                {
                    err += dy;
                    x0 += sx;
                }
                if (stepX && stepY) yield return FestivalVenue.Index(x0, y0);
                if (stepY)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        // ================================================================ Input

        void HandleKeyboard()
        {
            var keyboard = Keyboard.current;
            // While a zone patrol is selected the arrow keys give orders, so they stop panning the camera.
            cameraRig.arrowKeysPan = SelectedPatrol() == null;
            if (keyboard == null) return;
            if (keyboard.rKey.wasPressedThisFrame)
            {
                NewGame();
                return;
            }
            if (keyboard.hKey.wasPressedThisFrame) heatmap = !heatmap;
            if (keyboard.mKey.wasPressedThisFrame) stateMarkers = !stateMarkers;
            if (keyboard.lKey.wasPressedThisFrame) showLegend = !showLegend;
            if (keyboard.gKey.wasPressedThisFrame) showRegions = !showRegions;
            if (phase == Phase.Results) return;
            if (keyboard.spaceKey.wasPressedThisFrame) paused = !paused;
            if (keyboard.tabKey.wasPressedThisFrame) speedIndex = (speedIndex + 1) % Speeds.Length;
            if (keyboard.digit1Key.wasPressedThisFrame) SetTool(Tool.Command);
            if (keyboard.digit2Key.wasPressedThisFrame) SetTool(Tool.Barrier);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) StartShow();
            if (keyboard.escapeKey.wasPressedThisFrame) selectedUnit = -1;
            var patrol = SelectedPatrol();
            if (patrol != null)
            {
                if (keyboard.upArrowKey.wasPressedThisFrame) OrderZone(patrol, OrderSide.Front);
                if (keyboard.downArrowKey.wasPressedThisFrame) OrderZone(patrol, OrderSide.Back);
                if (keyboard.leftArrowKey.wasPressedThisFrame) OrderZone(patrol, OrderSide.Left);
                if (keyboard.rightArrowKey.wasPressedThisFrame) OrderZone(patrol, OrderSide.Right);
                if (keyboard.fKey.wasPressedThisFrame) EscortNearestDrunk(selectedUnit);
            }
        }

        void HandleMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null || phase == Phase.Results)
            {
                CancelDrag();
                return;
            }
            Vector2 screen = mouse.position.ReadValue();
            bool overGui = IsOverGui(screen);
            bool onGround = TryGetCellUnderMouse(screen, out int cell, out Vector2 ground);

            if (tool == Tool.Barrier)
            {
                if (mouse.leftButton.wasPressedThisFrame && !overGui && onGround)
                {
                    dragging = true;
                    dragStartCell = cell;
                }
                if (dragging)
                {
                    if (onGround)
                    {
                        dragLine.enabled = true;
                        var a = FestivalVenue.CellCenter(dragStartCell);
                        var b = FestivalVenue.CellCenter(cell);
                        dragLine.SetPosition(0, new Vector3(a.x, 0.6f, a.y));
                        dragLine.SetPosition(1, new Vector3(b.x, 0.6f, b.y));
                    }
                    if (mouse.leftButton.wasReleasedThisFrame)
                    {
                        if (onGround) PlaceBarrierLine(dragStartCell, cell);
                        CancelDrag();
                    }
                }
                if (mouse.rightButton.wasPressedThisFrame && !overGui && onGround) RemoveBarrier(cell);
            }
            else
            {
                if (mouse.leftButton.wasPressedThisFrame && !overGui && onGround) CommandClick(ground, cell);
                if (mouse.rightButton.wasPressedThisFrame && !overGui)
                {
                    // Right-click turns a selected patrol to look at that spot (it sets Front/Back/Left/Right); otherwise it deselects.
                    var patrol = SelectedPatrol();
                    if (patrol != null && onGround) SetFacing(patrol, ground - patrol.position);
                    else selectedUnit = -1;
                }
            }
        }

        void CancelDrag()
        {
            dragging = false;
            if (dragLine != null) dragLine.enabled = false;
        }

        bool TryGetCellUnderMouse(Vector2 screen, out int cell, out Vector2 ground)
        {
            cell = -1;
            ground = default;
            var ray = cam.ScreenPointToRay(screen);
            if (ray.direction.y > -1e-4f) return false;
            Vector3 hit = ray.origin + ray.direction * (-ray.origin.y / ray.direction.y);
            ground = new Vector2(hit.x, hit.z);
            if (hit.x < 0f || hit.z < 0f || hit.x >= W || hit.z >= H) return false;
            cell = FestivalVenue.CellAt(ground);
            return true;
        }

        /// <summary>Is this screen point (pixels, bottom-left origin) over a UI panel?</summary>
        bool IsOverGui(Vector2 screen)
        {
            var guiPoint = new Vector2(screen.x, Screen.height - screen.y) / uiScale;
            foreach (var r in guiRects)
                if (r.Contains(guiPoint)) return true;
            return false;
        }

        // ================================================================ Views

        void UpdateViews()
        {
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                u.root.position = new Vector3(u.position.x, 0f, u.position.y);
                u.ring.widthMultiplier = i == selectedUnit ? 0.35f : 0.12f;
                bool ringOn = u.escortPhase != EscortPhase.Escorting; // no calming while walking someone out
                if (u.ring.enabled != ringOn) u.ring.enabled = ringOn;
                if (u.orderDoor >= 0) UpdateOrderArrow(u);
                if (u.wave == null) continue;
                // Announcement ripple: grows from the officer to the edge of earshot.
                u.waveTime += paused ? 0f : Time.deltaTime;
                bool visible = u.waveTime < 1f;
                var line = u.wave.GetComponent<LineRenderer>();
                if (line.enabled != visible) line.enabled = visible;
                if (visible) u.wave.localScale = Vector3.one * Mathf.Max(0.01f, u.waveTime * crowd.policeAnnounceRadius);
            }

            for (int i = 0; i < venue.gates.Count; i++)
            {
                var g = venue.gates[i];
                var t = gateViews[i].transform;
                float h = Mathf.Lerp(1.4f, 0.12f, g.progress);
                t.localPosition = new Vector3(t.localPosition.x, h * 0.5f, t.localPosition.z);
                t.localScale = new Vector3(t.localScale.x, h, t.localScale.z);
                var material = Lit(GateColor(g));
                if (gateViews[i].sharedMaterial != material) gateViews[i].sharedMaterial = material;
            }

            float time = Time.time;
            foreach (var kv in fireViews)
            {
                var p = FestivalVenue.CellCenter(kv.Key);
                float h = venue.blockHeight[kv.Key] + 0.8f + 1.2f * Mathf.PerlinNoise(time * 3f, kv.Key * 0.37f);
                kv.Value.localPosition = new Vector3(p.x, h * 0.5f, p.y);
                kv.Value.localScale = new Vector3(0.9f, h, 0.9f);
            }

            groundTimer -= Time.unscaledDeltaTime;
            if (groundTimer <= 0f)
            {
                groundTimer = 0.15f;
                UpdateGroundTexture();
            }
        }

        static Color GateColor(Gate g) => g.Passable ? GateOpenColor : g.open ? GateOpeningColor : GateClosedColor;
        static string GateStatus(Gate g) => g.Passable ? "OPEN" : g.open ? "OPENING" : "CLOSED";

        void UpdateGroundTexture()
        {
            for (int i = 0; i < groundPixels.Length; i++)
            {
                groundPixels[i] = venue.cells[i] switch
                {
                    CellType.Outside => Asphalt,
                    CellType.Open => Color.Lerp(heatmap ? PressureColor(sim.pressure[i]) : Grass, SmokeShade, venue.smoke[i] * 0.6f),
                    CellType.Fire => Scorched,
                    _ => Blocked,
                };
            }
            if (showRegions) DrawRegionOutlines();
            groundTexture.SetPixels32(groundPixels);
            groundTexture.Apply(false);
        }

        /// <summary>
        /// Thin (1 m) region borders on the ground texture: a cell is tinted with its region's colour when the cell to
        /// its right or above it is in another region. Only one side of each border is marked, so lines stay 1 cell wide.
        /// </summary>
        void DrawRegionOutlines()
        {
            var regionOf = venue.regionOf;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = FestivalVenue.Index(x, y), r = regionOf[i];
                    if (r < 0 || venue.cells[i] == CellType.Fire) continue;
                    bool border = (x + 1 < W && regionOf[i + 1] >= 0 && regionOf[i + 1] != r) ||
                                  (y + 1 < H && regionOf[i + W] >= 0 && regionOf[i + W] != r);
                    if (border) groundPixels[i] = Color.Lerp(groundPixels[i], venue.regions[r].color, 0.6f);
                }
            }
        }

        static Color PressureColor(float p)
        {
            var low = new Color(0.62f, 0.68f, 0.3f);
            var dense = new Color(0.95f, 0.65f, 0.15f);
            var danger = new Color(0.9f, 0.12f, 0.1f);
            var crush = new Color(0.75f, 0f, 0.65f);
            if (p < 2.5f) return Color.Lerp(Grass, low, p / 2.5f);
            if (p < 5f) return Color.Lerp(low, dense, (p - 2.5f) / 2.5f);
            if (p < 7.5f) return Color.Lerp(dense, danger, (p - 5f) / 2.5f);
            return Color.Lerp(danger, crush, Mathf.Clamp01((p - 7.5f) / 3f));
        }

        void Log(string message, Severity severity = Severity.Info)
        {
            log.Add((message, severity, incidentStarted ? FormatTime(evacuationTime) : ""));
            if (log.Count > 40) log.RemoveAt(0);
        }

        static string FormatTime(float seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

        // ================================================================ UI

        static bool welcomeDismissed;
        bool showLegend = true;
        float uiScale = 1f, vw, vh; // UI scale and the virtual screen size it works in
        float bannerTime;
        string bannerTitle, bannerText;
        Severity bannerSeverity;

        static readonly Color TextColor = new(0.95f, 0.96f, 0.98f);
        static readonly Color MutedText = new(0.68f, 0.72f, 0.8f);
        static readonly Color PanelColor = new(0.07f, 0.08f, 0.11f, 0.92f);
        static readonly Color CardColor = new(0.13f, 0.15f, 0.2f, 1f);
        static readonly Color ButtonColor = new(0.2f, 0.23f, 0.3f);
        static readonly Color SelectedColor = new(0.22f, 0.45f, 0.9f);
        static readonly Color GoodColor = new(0.2f, 0.66f, 0.38f);
        static readonly Color WarningColor = new(0.95f, 0.62f, 0.15f);
        static readonly Color DangerColor = new(0.9f, 0.27f, 0.2f);

        GUIStyle panelStyle, cardStyle, tooltipStyle, buttonStyle, selectedButtonStyle, goodButtonStyle, rowStyle, rowSelectedStyle;
        GUIStyle labelStyle, mutedStyle, smallStyle, headerStyle, titleStyle, bigTitleStyle, numberStyle, statCaptionStyle, worldLabelStyle, chipStyle;
        readonly Dictionary<string, Texture2D> roundedCache = new();

        void OnGUI()
        {
            if (sim == null) return;
            uiScale = Mathf.Clamp(Screen.height / 900f, 0.75f, 2f);
            vw = Screen.width / uiScale;
            vh = Screen.height / uiScale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));
            EnsureStyles();
            guiRects.Clear();
            if (Event.current.type == EventType.Layout) orderPanelUnit = SelectedPatrol() != null ? selectedUnit : -1;

            DrawWorldLabels();
            DrawTopBar();
            DrawTeamPanel();
            DrawControlPanel();
            DrawZoneOrderPanel();
            DrawEventFeed();
            DrawLegend();
            DrawBanner();
            DrawHoverInfo();
            if (phase == Phase.Planning && !welcomeDismissed) DrawWelcome();
            if (phase == Phase.Results) DrawResults();
        }

        // ---------------------------------------------------------------- Styles

        Texture2D Rounded(Color fill, int radius)
        {
            string key = $"{fill}{radius}";
            if (roundedCache.TryGetValue(key, out var cached) && cached != null) return cached;
            int size = radius * 2 + 2;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var inner = new Vector2(Mathf.Clamp(p.x, radius, size - radius), Mathf.Clamp(p.y, radius, size - radius));
                    float alpha = Mathf.Clamp01(radius + 0.5f - Vector2.Distance(p, inner));
                    pixels[y * size + x] = new Color(fill.r, fill.g, fill.b, fill.a * alpha);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return roundedCache[key] = texture;
        }

        GUIStyle BoxStyle(Color fill, int radius, int padding)
        {
            var style = new GUIStyle { border = new RectOffset(radius, radius, radius, radius), padding = new RectOffset(padding, padding, padding, padding) };
            style.normal.background = Rounded(fill, radius);
            style.normal.textColor = TextColor;
            return style;
        }

        GUIStyle ButtonStyle(Color fill, int fontSize = 13, TextAnchor alignment = TextAnchor.MiddleCenter)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontSize = fontSize,
                fontStyle = FontStyle.Bold,
                alignment = alignment,
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(10, 10, 6, 6),
                margin = new RectOffset(0, 0, 3, 3),
            };
            var normal = Rounded(fill, 6);
            var hover = Rounded(Color.Lerp(fill, Color.white, 0.15f), 6);
            var pressed = Rounded(Color.Lerp(fill, Color.black, 0.2f), 6);
            style.normal.background = style.onNormal.background = style.focused.background = style.onFocused.background = normal;
            style.hover.background = style.onHover.background = hover;
            style.active.background = style.onActive.background = pressed;
            foreach (var state in new[] { style.normal, style.onNormal, style.hover, style.onHover, style.active, style.onActive, style.focused, style.onFocused })
                state.textColor = TextColor;
            return style;
        }

        GUIStyle TextStyle(int size, Color color, FontStyle font = FontStyle.Normal, TextAnchor alignment = TextAnchor.UpperLeft, bool wrap = false)
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = font, alignment = alignment, wordWrap = wrap, richText = true };
            style.normal.textColor = color;
            return style;
        }

        void EnsureStyles()
        {
            if (panelStyle != null && panelStyle.normal.background != null) return;
            panelStyle = BoxStyle(PanelColor, 10, 12);
            cardStyle = BoxStyle(CardColor, 8, 10);
            tooltipStyle = BoxStyle(new Color(0.05f, 0.06f, 0.08f, 0.95f), 6, 8);
            tooltipStyle.fontSize = 13;
            tooltipStyle.wordWrap = true;
            chipStyle = BoxStyle(Color.white, 8, 4);
            chipStyle.alignment = TextAnchor.MiddleCenter;
            chipStyle.fontSize = 11;
            chipStyle.fontStyle = FontStyle.Bold;

            buttonStyle = ButtonStyle(ButtonColor);
            selectedButtonStyle = ButtonStyle(SelectedColor);
            goodButtonStyle = ButtonStyle(GoodColor, 15);
            rowStyle = ButtonStyle(CardColor, 13, TextAnchor.MiddleLeft);
            rowStyle.padding = new RectOffset(30, 10, 6, 6);
            rowStyle.fontStyle = FontStyle.Normal;
            rowSelectedStyle = ButtonStyle(SelectedColor, 13, TextAnchor.MiddleLeft);
            rowSelectedStyle.padding = rowStyle.padding;
            rowSelectedStyle.fontStyle = FontStyle.Normal;

            labelStyle = TextStyle(13, TextColor, wrap: true);
            mutedStyle = TextStyle(12, MutedText, wrap: true);
            smallStyle = TextStyle(11, MutedText);
            headerStyle = TextStyle(12, MutedText, FontStyle.Bold);
            titleStyle = TextStyle(20, TextColor, FontStyle.Bold);
            bigTitleStyle = TextStyle(26, TextColor, FontStyle.Bold, TextAnchor.UpperCenter);
            numberStyle = TextStyle(20, TextColor, FontStyle.Bold, TextAnchor.MiddleCenter);
            statCaptionStyle = TextStyle(10, MutedText, FontStyle.Bold, TextAnchor.MiddleCenter);
            worldLabelStyle = TextStyle(11, TextColor, FontStyle.Bold, TextAnchor.MiddleCenter);
        }

        // ---------------------------------------------------------------- Widgets

        Rect Panel(Rect rect, GUIStyle style = null)
        {
            GUI.Box(rect, GUIContent.none, style ?? panelStyle);
            guiRects.Add(rect);
            return rect;
        }

        void Chip(Rect rect, string text, Color color)
        {
            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(color.r, color.g, color.b, 0.3f);
            var oldText = chipStyle.normal.textColor;
            chipStyle.normal.textColor = Color.Lerp(color, Color.white, 0.35f);
            GUI.Box(rect, text, chipStyle);
            chipStyle.normal.textColor = oldText;
            GUI.backgroundColor = old;
        }

        void Swatch(Rect rect, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Rounded(Color.white, 3));
            GUI.color = old;
        }

        void ProgressBar(Rect rect, float value, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(rect, Rounded(Color.white, 4));
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, Mathf.Max(rect.height, rect.width * Mathf.Clamp01(value)), rect.height), Rounded(Color.white, 4));
            GUI.color = old;
        }

        void ShadowLabel(Rect rect, string text, GUIStyle style)
        {
            var color = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
            style.normal.textColor = color;
            GUI.Label(rect, text, style);
        }

        static Color SeverityColor(Severity severity) => severity switch
        {
            Severity.Good => GoodColor,
            Severity.Warning => WarningColor,
            Severity.Danger => DangerColor,
            _ => MutedText,
        };

        void ShowBanner(string title, string text, Severity severity)
        {
            bannerTitle = title;
            bannerText = text;
            bannerSeverity = severity;
            bannerTime = 5f;
        }

        // ---------------------------------------------------------------- Panels

        void DrawTopBar()
        {
            var bar = Panel(new Rect(0, 0, vw, 64), BoxStyle(PanelColor, 0, 0));
            GUI.Label(new Rect(16, 8, 220, 28), "CRUSH HOUR", titleStyle);
            GUI.Label(new Rect(17, 36, 220, 20), "Summer Music Festival", smallStyle);

            // What's happening now.
            float x = 230;
            switch (phase)
            {
                case Phase.Planning:
                    Chip(new Rect(x, 12, 120, 22), "PLANNING", SelectedColor);
                    GUI.Label(new Rect(x, 38, 380, 20), "Position your team and barriers, then start the show.", smallStyle);
                    break;
                case Phase.Live when !incidentStarted:
                    Chip(new Rect(x, 12, 120, 22), "SHOW ON", GoodColor);
                    GUI.Label(new Rect(x, 38, 380, 20), "Everything's fine... for now.", smallStyle);
                    break;
                case Phase.Live:
                    Chip(new Rect(x, 12, 120, 22), "EVACUATION", DangerColor);
                    GUI.Label(new Rect(x + 130, 12, 120, 22), $"{FormatTime(evacuationTime)} / {FormatTime(evacuationTimeLimit)}", labelStyle);
                    ProgressBar(new Rect(x + 230, 17, 140, 10), evacuationTime / evacuationTimeLimit,
                        Color.Lerp(GoodColor, DangerColor, evacuationTime / evacuationTimeLimit));
                    GUI.Label(new Rect(x, 38, 380, 20), $"Fire at {incidentSite}", smallStyle);
                    break;
                default:
                    Chip(new Rect(x, 12, 120, 22), "RESULTS", MutedText);
                    break;
            }

            // Simulation speed.
            float sx = x + 400;
            if (GUI.Button(new Rect(sx, 14, 70, 34), paused ? "Play" : "Pause", paused ? selectedButtonStyle : buttonStyle)) paused = !paused;
            for (int i = 0; i < Speeds.Length; i++)
                if (GUI.Button(new Rect(sx + 76 + i * 46, 14, 42, 34), $"{Speeds[i]:0}x", speedIndex == i && !paused ? selectedButtonStyle : buttonStyle))
                {
                    speedIndex = i;
                    paused = false;
                }

            // Headline numbers.
            float right = vw - 12;
            right = Stat(right, "PANIC ATTACKS", sim.PanicAttackCount.ToString(), AgentColors[5]);
            right = Stat(right, "INJURED", sim.InjuredCount.ToString(), DangerColor);
            right = Stat(right, "SAFE", sim.SafeCount.ToString(), GoodColor);
            Stat(right, "INSIDE", sim.ActiveCount.ToString(), TextColor);
            _ = bar;
        }

        float Stat(float right, string caption, string value, Color color)
        {
            const float width = 104;
            var rect = new Rect(right - width, 8, width - 6, 48);
            GUI.Box(rect, GUIContent.none, cardStyle);
            var old = numberStyle.normal.textColor;
            numberStyle.normal.textColor = color;
            GUI.Label(new Rect(rect.x, rect.y + 3, rect.width, 26), value, numberStyle);
            numberStyle.normal.textColor = old;
            GUI.Label(new Rect(rect.x, rect.y + 29, rect.width, 14), caption, statCaptionStyle);
            return right - width;
        }

        void DrawTeamPanel()
        {
            float height = (phase == Phase.Planning ? 64 : 0) + 60 + units.Count * 34 + 2 * 24 + 130;
            var rect = Panel(new Rect(12, 76, 280, height));
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 10, rect.width - 24, rect.height - 20));

            if (phase == Phase.Planning)
            {
                if (GUILayout.Button("START THE SHOW", goodButtonStyle, GUILayout.Height(44))) deferred.Add(StartShow);
                GUILayout.Label("Press Enter. A fire will break out somewhere soon after.", smallStyle);
                GUILayout.Space(4);
            }

            GUILayout.Label("YOUR TEAM", headerStyle);
            GUILayout.Label("Click someone, then click the map to send them.", mutedStyle);
            // Grouped: the zone patrols first (one per zone), then the police.
            foreach (var role in new[] { Role.Patrolman, Role.Megaphone })
            {
                GUILayout.Label(role == Role.Patrolman ? "ZONE PATROLS" : "MEGAPHONE POLICE", smallStyle, GUILayout.Height(20));
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u.role != role) continue;
                    string status = u.escortPhase switch
                    {
                        EscortPhase.Fetching => "fetching drunk",
                        EscortPhase.Escorting => "escorting",
                        EscortPhase.Returning => "returning",
                        _ => u.path.Count > 0 ? "on the move" : u.role == Role.Megaphone && incidentStarted ? "announcing" : "in position",
                    };
                    if (GUILayout.Button(u.Name, i == selectedUnit ? rowSelectedStyle : rowStyle, GUILayout.Height(30))) SelectUnit(i, true);
                    var row = GUILayoutUtility.GetLastRect();
                    Swatch(new Rect(row.x + 10, row.y + 9, 12, 12), UnitColor(u));
                    GUI.Label(new Rect(row.x, row.y + 8, row.width - 10, 16), status, TextStyle(11, MutedText, FontStyle.Normal, TextAnchor.UpperRight));
                }
            }

            GUILayout.Space(8);
            GUILayout.Label("TOOLS", headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Command  [1]", tool == Tool.Command ? selectedButtonStyle : buttonStyle, GUILayout.Height(32))) SetTool(Tool.Command);
            if (GUILayout.Button($"Barriers {barrierBudget - barriersUsed}  [2]", tool == Tool.Barrier ? selectedButtonStyle : buttonStyle, GUILayout.Height(32))) SetTool(Tool.Barrier);
            GUILayout.EndHorizontal();
            GUILayout.Label(tool == Tool.Command
                ? "Click a gate on the map to open or close it. Right-click turns a selected patrol (or deselects police). Esc deselects."
                : "Drag on the map to draw a barrier line. Right-click a barrier to pick it up.", mutedStyle);
            GUILayout.EndArea();
        }

        /// <summary>
        /// "EVACUATE REGION" panel for the selected patrol: Front/Back/Left/Right relative to where they face (arrow keys),
        /// each showing the door it would pick. Plain GUI (no GUILayout), and the unit is fixed at the Layout event.
        /// </summary>
        void DrawZoneOrderPanel()
        {
            if (phase == Phase.Results || orderPanelUnit < 0 || orderPanelUnit >= units.Count) return;
            var u = units[orderPanelUnit];
            if (u.role != Role.Patrolman || u.homeRegion < 0) return;
            var region = venue.regions[u.homeRegion];

            var rect = Panel(new Rect(vw * 0.5f - 170, vh - 304, 340, 292));
            GUI.Label(new Rect(rect.x + 14, rect.y + 10, rect.width - 28, 20), "EVACUATE REGION", headerStyle);
            Swatch(new Rect(rect.xMax - 26, rect.y + 14, 12, 12), region.color);
            GUI.Label(new Rect(rect.x + 14, rect.y + 32, rect.width - 28, 18),
                $"{region.name}: {sim.CountInRegion(u.homeRegion, venue)} people in the zone", labelStyle);
            float cooldown = regionOrderCooldown.Length > u.homeRegion ? regionOrderCooldown[u.homeRegion] : 0f;
            string status = !incidentStarted ? "Available once the fire starts. Right-click the map to turn the patrol."
                : cooldown > 0f ? $"Order given. Next one in {Mathf.CeilToInt(cooldown)}s."
                : "Arrow keys or buttons, relative to where the patrol faces. Right-click to turn.";
            GUI.Label(new Rect(rect.x + 14, rect.y + 50, rect.width - 28, 30), status, smallStyle);

            bool ready = CanOrderZone(u);
            float cx = rect.center.x, top = rect.y + 84;
            const float bw = 150, bh = 46;
            OrderButton(u, OrderSide.Front, new Rect(cx - bw * 0.5f, top, bw, bh), ready);
            OrderButton(u, OrderSide.Left, new Rect(cx - bw - 4, top + bh + 4, bw, bh), ready);
            OrderButton(u, OrderSide.Right, new Rect(cx + 4, top + bh + 4, bw, bh), ready);
            OrderButton(u, OrderSide.Back, new Rect(cx - bw * 0.5f, top + 2 * (bh + 4), bw, bh), ready);

            // Escorting drunk people out: they ignore orders, so someone has to walk them to a door.
            int inZone = sim.UnescortedDrunksInRegion(u.homeRegion, venue);
            bool busy = u.escortPhase == EscortPhase.Escorting;
            string label = busy ? "Escorting a drunk fan out..."
                : u.escortPhase == EscortPhase.Fetching ? "Going to get a drunk fan..."
                : sim.DrunkInsideCount == 0 ? "No drunk fans left inside"
                : $"Escort nearest drunk [F]  ({inZone} here, {sim.DrunkInsideCount} in all)";
            bool wasEnabled = GUI.enabled;
            GUI.enabled = !busy && sim.DrunkInsideCount > 0;
            int unitIndex = orderPanelUnit;
            if (GUI.Button(new Rect(rect.x + 14, rect.yMax - 54, rect.width - 28, 40), label, buttonStyle))
                deferred.Add(() => EscortNearestDrunk(unitIndex));
            GUI.enabled = wasEnabled;
        }

        void OrderButton(Unit u, OrderSide side, Rect rect, bool ready)
        {
            int door = sim.PickOrderDoor(u.homeRegion, SideDirection(u, side), venue);
            string key = side switch { OrderSide.Front => "Up", OrderSide.Back => "Down", OrderSide.Left => "Left", _ => "Right" };
            string target = door >= 0 ? venue.doors[door].name : "no open exit";
            bool wasEnabled = GUI.enabled;
            GUI.enabled = ready;
            if (GUI.Button(rect, $"{side} [{key}]\nto {target}", buttonStyle)) deferred.Add(() => OrderZone(u, side));
            GUI.enabled = wasEnabled;
        }

        void DrawControlPanel()
        {
            var rect = Panel(new Rect(vw - 312, 76, 300, 120 + venue.gates.Count * 34 + 114));
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 10, rect.width - 24, rect.height - 20));

            GUILayout.Label("EMERGENCY GATES", headerStyle);
            GUILayout.Label("Wide exits, closed until you open them. People who find a gate shut look for another.", mutedStyle);
            foreach (var g in venue.gates)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(30));
                if (GUILayout.Button(g.name, rowStyle, GUILayout.Height(30), GUILayout.Width(130)))
                    cameraRig.Focus(new Vector3(g.rect.center.x, 0f, g.rect.center.y));
                var chip = GUILayoutUtility.GetRect(70, 30, GUILayout.Width(70));
                Chip(new Rect(chip.x + 4, chip.y + 6, chip.width - 8, 20), GateStatus(g), GateColor(g));
                if (GUILayout.Button(g.open ? "Close" : "Open", g.open ? buttonStyle : goodButtonStyle, GUILayout.Height(30))) ToggleGate(g);
                GUILayout.EndHorizontal();
                Swatch(new Rect(GUILayoutUtility.GetLastRect().x + 10, GUILayoutUtility.GetLastRect().y + 10, 10, 10), GateColor(g));
            }
            if (GUILayout.Button("Open all gates", buttonStyle, GUILayout.Height(30))) OpenAllGates();

            GUILayout.Space(6);
            GUILayout.Label("VIEW", headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"Pressure map [H]", heatmap ? selectedButtonStyle : buttonStyle)) heatmap = !heatmap;
            if (crowdRenderer.UsesModels && GUILayout.Button($"State tiles [M]", stateMarkers ? selectedButtonStyle : buttonStyle)) stateMarkers = !stateMarkers;
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Zones [G]", showRegions ? selectedButtonStyle : buttonStyle)) showRegions = !showRegions;
            GUILayout.EndArea();
        }

        void DrawEventFeed()
        {
            int lines = Mathf.Min(6, log.Count);
            if (lines == 0) return;
            const float lineHeight = 22;
            var rect = Panel(new Rect(12, vh - 24 - lines * lineHeight, 560, lines * lineHeight + 12));
            for (int i = 0; i < lines; i++)
            {
                var (text, severity, time) = log[log.Count - lines + i];
                float y = rect.y + 6 + i * lineHeight;
                Swatch(new Rect(rect.x + 12, y + 6, 8, 8), SeverityColor(severity));
                GUI.Label(new Rect(rect.x + 26, y, 44, lineHeight), time, smallStyle);
                var style = i == lines - 1 ? labelStyle : mutedStyle;
                GUI.Label(new Rect(rect.x + (time.Length > 0 ? 66 : 28), y + 1, rect.width - 76, lineHeight), text, style);
            }
        }

        void DrawLegend()
        {
            if (!showLegend)
            {
                var button = new Rect(vw - 132, vh - 48, 120, 34);
                guiRects.Add(button);
                if (GUI.Button(button, "Legend  [L]", buttonStyle)) deferred.Add(() => showLegend = true);
                return;
            }
            var rect = Panel(new Rect(vw - 312, vh - 344, 300, 332));
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 10, rect.width - 24, rect.height - 20));
            GUILayout.BeginHorizontal();
            GUILayout.Label("LEGEND", headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Hide [L]", buttonStyle, GUILayout.Width(80))) deferred.Add(() => showLegend = false);
            GUILayout.EndHorizontal();
            LegendRow(AgentColors[0], "Calm: watching the show");
            LegendRow(AgentColors[1], "Knows which gates are open");
            LegendRow(AgentColors[2], "Alarmed");
            LegendRow(AgentColors[3], "Panicking: runs, pushes");
            LegendRow(AgentColors[5], "Panic attack: frozen for a while");
            LegendRow(AgentColors[6], "Choking on smoke: slowed, may collapse");
            LegendRow(AgentColors[7], "Drunk: ignores orders, needs an escort out");
            LegendRow(AgentColors[4], "Injured");
            LegendRow(PatrolColor, "Zone patrol: calms people, then sends them out");
            LegendRow(MegaphoneColor, "Police with megaphone");
            GUILayout.Space(4);
            GUILayout.Label("Ground colour: green = room to move, orange = packed, red/purple = crush danger, dark = thick smoke. Coloured lines = zones [G].", mutedStyle);
            GUILayout.EndArea();
        }

        void LegendRow(Color color, string text)
        {
            GUILayout.BeginHorizontal();
            var swatch = GUILayoutUtility.GetRect(14, 18, GUILayout.Width(14));
            Swatch(new Rect(swatch.x, swatch.y + 3, 12, 12), color);
            GUILayout.Label(text, labelStyle);
            GUILayout.EndHorizontal();
        }

        void DrawBanner()
        {
            if (bannerTime <= 0f) return;
            bannerTime -= Event.current.type == EventType.Repaint ? Time.unscaledDeltaTime : 0f;
            float alpha = Mathf.Clamp01(bannerTime);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            var rect = new Rect(vw * 0.5f - 260, 78, 520, 70);
            GUI.Box(rect, GUIContent.none, BoxStyle(SeverityColor(bannerSeverity), 10, 0));
            GUI.Label(new Rect(rect.x, rect.y + 8, rect.width, 32), bannerTitle, bigTitleStyle);
            GUI.Label(new Rect(rect.x, rect.y + 42, rect.width, 20), bannerText, TextStyle(13, TextColor, FontStyle.Normal, TextAnchor.UpperCenter));
            GUI.color = old;
        }

        void DrawWorldLabels()
        {
            if (showRegions)
            {
                foreach (var r in venue.regions)
                {
                    var style = TextStyle(10, Color.Lerp(r.color, Color.white, 0.25f), FontStyle.Bold, TextAnchor.MiddleCenter);
                    WorldLabel(new Vector3(r.post.x + 0.5f, 0.1f, r.post.y + 0.5f), $"{r.name.ToUpperInvariant()} ZONE", style);
                }
            }
            foreach (var (text, position) in labels) WorldLabel(position, text, worldLabelStyle);
            foreach (var g in venue.gates)
            {
                var style = TextStyle(11, Color.Lerp(GateColor(g), Color.white, 0.4f), FontStyle.Bold, TextAnchor.MiddleCenter);
                WorldLabel(new Vector3(g.rect.center.x, 2f, g.rect.center.y), $"{g.name.ToUpperInvariant()}\n{GateStatus(g)}", style);
            }
        }

        void WorldLabel(Vector3 position, string text, GUIStyle style)
        {
            Vector3 sp = cam.WorldToScreenPoint(position);
            if (sp.z < 0f) return;
            var p = new Vector2(sp.x, Screen.height - sp.y) / uiScale;
            ShadowLabel(new Rect(p.x - 80f, p.y - 16f, 160f, 32f), text, style);
        }

        void DrawHoverInfo()
        {
            var mouse = Mouse.current;
            if (mouse == null || phase == Phase.Results) return;
            Vector2 screen = mouse.position.ReadValue();
            if (IsOverGui(screen) || !TryGetCellUnderMouse(screen, out int cell, out Vector2 ground)) return;

            string text;
            int hovered = NearestUnit(ground, 1.5f);
            var gate = venue.GateNear(cell);
            if (dragging)
            {
                int free = 0;
                foreach (int c in LineCells(dragStartCell, cell))
                    if (venue.cells[c] == CellType.Open) free++;
                text = $"{free} barrier sections ({barrierBudget - barriersUsed} left)";
            }
            else if (hovered >= 0) text = $"<b>{units[hovered].Name}</b>\n{RoleDescription(units[hovered].role)}";
            else if (sim.NearestDrunk(ground, 1.2f) is int drunkPerson && drunkPerson >= 0)
            {
                int by = sim.escortedBy[drunkPerson];
                text = by >= 0 && by < units.Count ? $"<b>Drunk</b>: being walked out by {units[by].Name}."
                    : selectedUnit >= 0 ? $"<b>Drunk \u2013 needs escort</b>\nClick to have {units[selectedUnit].Name} walk them out."
                    : "<b>Drunk \u2013 needs escort</b>\nSelect a patrol or police officer, then click them.";
            }
            else if (gate != null) text = $"<b>{gate.name}</b>: {GateStatus(gate).ToLowerInvariant()}. Click to {(gate.open ? "close" : "open")} it.";
            else
            {
                text = venue.cells[cell] switch
                {
                    CellType.Open => $"{sim.density[cell]:0.0} people per m²   pressure {sim.pressure[cell]:0.0}" +
                                     (sim.pressure[cell] > crowd.crushThreshold * 0.8f ? "  <b>CRUSH RISK</b>" : "") +
                                     (venue.smoke[cell] > 0.1f ? $"\nSmoke {venue.smoke[cell] * 100f:0}%" + (venue.smoke[cell] > crowd.smokeHarmLevel ? " <b>TOXIC</b>" : "") : ""),
                    CellType.Fire => "Fire",
                    CellType.Barrier => "Barrier (right-click with the Barrier tool to remove)",
                    CellType.Outside => "Outside: safe",
                    _ => venue.DescribeLocation(cell),
                };
                var region = venue.RegionAt(cell);
                if (region != null) text += $"\n<color=#{ColorUtility.ToHtmlStringRGB(region.color)}>Zone: {region.name}</color>";
                if (selectedUnit >= 0 && tool == Tool.Command && venue.cells[cell] == CellType.Open)
                    text += $"\nClick to send {units[selectedUnit].Name} here.";
            }

            var content = new GUIContent(text);
            float width = Mathf.Min(320f, tooltipStyle.CalcSize(content).x + 4f);
            float height = tooltipStyle.CalcHeight(content, width);
            var p = new Vector2(screen.x, Screen.height - screen.y) / uiScale;
            var rect = new Rect(Mathf.Min(p.x + 18, vw - width - 8), Mathf.Min(p.y + 14, vh - height - 8), width, height);
            GUI.Box(rect, content, tooltipStyle);
        }

        void DrawWelcome()
        {
            var rect = Panel(new Rect(vw * 0.5f - 280, vh * 0.5f - 210, 560, 420), BoxStyle(new Color(0.09f, 0.1f, 0.14f, 0.97f), 12, 0));
            GUILayout.BeginArea(new Rect(rect.x + 28, rect.y + 22, rect.width - 56, rect.height - 44));
            GUILayout.Label("Welcome, safety commander", titleStyle);
            GUILayout.Space(4);
            GUILayout.Label($"{sim.count} fans are at the Summer Music Festival. Soon a fire will break out somewhere. " +
                            "People will run from it, then head for the nearest gate. If it's shut, they try another, and the queues grow.", labelStyle);
            GUILayout.Space(10);
            WelcomeStep("1", "Plan", $"Every zone has a patrol ({venue.regions.Count} in all), plus {megaphoneOfficers} megaphone police. Move them by clicking them, then the map. Draw barriers to steer the crowd.");
            WelcomeStep("2", "React", "When the fire starts, open the emergency gates and send your team to the crowds. Select a patrol and press an arrow key to send its whole zone out that way. The megaphone police tell everyone in earshot where to go.");
            WelcomeStep("3", "Keep them calm", "Panicked crowds push and crush, and too much panic causes panic attacks. Put patrols where queues form: they line people up with room to breathe.");
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Camera: WASD, scroll to zoom, Q/E to rotate.", smallStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Let's go", goodButtonStyle, GUILayout.Width(140), GUILayout.Height(40))) deferred.Add(() => welcomeDismissed = true);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void WelcomeStep(string number, string title, string text)
        {
            GUILayout.BeginHorizontal();
            var badge = GUILayoutUtility.GetRect(30, 30, GUILayout.Width(30), GUILayout.Height(30));
            GUI.Box(badge, number, BoxStyle(SelectedColor, 15, 0));
            GUI.Label(badge, number, numberStyle);
            GUILayout.Space(10);
            GUILayout.BeginVertical();
            GUILayout.Label($"<b>{title}</b>", labelStyle);
            GUILayout.Label(text, mutedStyle);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
        }

        void DrawResults()
        {
            var rect = Panel(new Rect(vw * 0.5f - 250, vh * 0.5f - 236, 500, 472), BoxStyle(new Color(0.09f, 0.1f, 0.14f, 0.97f), 12, 0));
            GUILayout.BeginArea(new Rect(rect.x + 28, rect.y + 22, rect.width - 56, rect.height - 44));
            GUILayout.Label(resultTitle, bigTitleStyle);
            GUILayout.Space(6);

            // Star rating as three badges.
            var starsRow = GUILayoutUtility.GetRect(10, 34, GUILayout.ExpandWidth(true));
            for (int i = 0; i < 3; i++)
            {
                var badge = new Rect(starsRow.center.x - 75 + i * 52, starsRow.y, 46, 30);
                GUI.Box(badge, GUIContent.none, BoxStyle(i < stars ? new Color(1f, 0.78f, 0.2f) : new Color(1f, 1f, 1f, 0.12f), 8, 0));
            }
            GUILayout.Label($"{stars} of 3 stars", TextStyle(12, MutedText, FontStyle.Normal, TextAnchor.UpperCenter));
            GUILayout.Space(8);

            ResultRow("Safe", $"{finalSafe} / {sim.count}  ({finalSafe * 100f / sim.count:0}%)", GoodColor);
            ResultRow("Injured", finalInjured == 0 ? "0" : $"{finalInjured}  ({sim.InjuredByCrush} crushed, {sim.InjuredByFire} burnt, {sim.InjuredBySmoke} smoke)", finalInjured > 0 ? DangerColor : TextColor);
            ResultRow("Panic attacks", finalAttacks.ToString(), finalAttacks > 0 ? AgentColors[5] : TextColor);
            ResultRow("Still inside", finalTrapped.ToString(), finalTrapped > 0 ? WarningColor : TextColor);
            ResultRow("Drunk left inside", finalDrunkInside == 0 ? "0" : $"{finalDrunkInside}  (-{finalDrunkInside * 50} points)", finalDrunkInside > 0 ? AgentColors[7] : TextColor);
            ResultRow("Evacuation time", FormatTime(evacuationTime), TextColor);
            ResultRow("Score", finalScore.ToString(), TextColor);
            GUILayout.Space(8);
            GUILayout.Label(resultTip, mutedStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Play again: new crowd, new fire  [R]", goodButtonStyle, GUILayout.Height(42))) deferred.Add(NewGame);
            GUILayout.EndArea();
        }

        void ResultRow(string caption, string value, Color color)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(caption, labelStyle, GUILayout.Width(180));
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<b>{value}</b>", TextStyle(14, color, FontStyle.Normal, TextAnchor.UpperRight));
            GUILayout.EndHorizontal();
        }
    }
}
