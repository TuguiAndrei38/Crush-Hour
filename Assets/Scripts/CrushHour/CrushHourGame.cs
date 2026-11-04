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
    public partial class CrushHourGame : MonoBehaviour
    {
        /// <summary>Menu (start menu, CrushHourGame.Menu.cs) → Planning → Live → Results.</summary>
        enum Phase { Menu, Planning, Live, Results }
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
            /// <summary>Door (index into venue.doors) this unit is clearing right now, -1 none. Its ring is off meanwhile.</summary>
            public int clearingDoor = -1;
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
        [Tooltip("Dev override: people in this scene, whatever the difficulty (0 = the difficulty's player count). Settings upgrades never reset it.")]
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
        static readonly Color GasColor = new(0.78f, 0.9f, 0.2f);
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
        /// <summary>This round's disasters. Filled for the chosen difficulty when the round starts.</summary>
        IncidentSchedule schedule;
        /// <summary>Chosen in Planning (welcome card or F1/F2/F3), remembered in PlayerPrefs.</summary>
        Difficulty difficulty = Difficulty.Medium;
        DifficultySettings difficultySettings = DifficultySettings.For(Difficulty.Medium);
        const string DifficultyPref = "CrushHour.Difficulty";
        readonly List<Incident> startedIncidents = new();
        int lastSafe;
        /// <summary>What the first evacuation incident is, as the police shout it ("Fire at the Food Court!").</summary>
        string incidentAnnouncement;

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
        /// <summary>The truck or scaffolding in front of each blocked door.</summary>
        readonly Dictionary<Door, GameObject> blockerViews = new();
        /// <summary>Light tower blocks (index = venue.lightTowers), and the pivot each is hinged on once it starts swaying.</summary>
        readonly Dictionary<int, Transform> towerViews = new(), towerPivots = new();
        /// <summary>Seconds since each tower started falling (fall animation).</summary>
        readonly Dictionary<TowerCollapseIncident, float> towerFallTime = new();
        /// <summary>The gas layer is drawn on the ground once someone has noticed the gas.</summary>
        bool gasVisible;
        /// <summary>Units that have already reported smelling gas.</summary>
        readonly HashSet<Unit> smeltGas = new();
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
        readonly List<(string text, Severity severity, string time, float at)> log = new();
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

            difficulty = (Difficulty)Mathf.Clamp(PlayerPrefs.GetInt(DifficultyPref, (int)Difficulty.Medium), 0, 2);
            LoadMenuPreferences();
            LoadPersonModels();
            NewGame();
            OpenMenu();
        }

        void OnDestroy()
        {
            crowdRenderer?.Dispose();
            DisposeMenuAssets();
        }

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
            world = new GameObject($"{MapTitle} (generated)").transform;
            barrierViews.Clear();
            fireViews.Clear();
            gateViews.Clear();
            blockerViews.Clear();
            towerViews.Clear();
            towerPivots.Clear();
            towerFallTime.Clear();
            smeltGas.Clear();
            gasVisible = false;
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
            venue = new FestivalVenue(selectedMap);
            difficultySettings = DifficultySettings.For(difficulty);
            crowd.agentCount = !menuLaunchedGame && crowdSizeOverride > 0 ? Mathf.Max(100, crowdSizeOverride) : difficultySettings.playerCount;
            crowd.drunkFraction = difficultySettings.drunkFraction;
            sim = new CrowdSimulation(crowd, venue, rng);
            var bounds = new Bounds(new Vector3(W * 0.5f, 1f, H * 0.5f), new Vector3(W, 4f, H));
            crowdRenderer = new CrowdRenderer(sim, cubeMesh, agentRenderParams, personModels, bounds, rng) { ShowMarkers = stateMarkers };

            phase = Phase.Planning;
            cameraRig.controlsEnabled = true;
            tool = Tool.Command;
            paused = incidentStarted = dragging = false;
            selectedUnit = -1;
            barriersUsed = lastInjured = lastSmokeInjured = lastAttacks = gatesOpened = lastSafe = 0;
            simAccumulator = evacuationTime = warningTimer = lastProgressTime = 0f;
            attackWarningTime = -99f;
            bannerTime = 0f;
            incidentAnnouncement = null;
            schedule = new IncidentSchedule(maxFireCells);
            dragLine.enabled = false;

            BuildVenueViews();
            SpawnUnits();
            regionOrderCooldown = new float[venue.regions.Count];
            cameraRig.Focus(new Vector3(W * 0.5f, 0f, H * 0.45f), 85f);

            Log($"{sim.count} people are at {MapTitle}.");
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
                if (l.solid)
                {
                    var block = CreateBlock(l.name, l.rect, l.height, l.color, 0.15f);
                    int tower = venue.lightTowers.IndexOf(l);
                    if (tower >= 0) towerViews[tower] = block.transform;
                }
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
                if (!venue.IsWalkable(FestivalVenue.CellAt(post)))
                {
                    var region = venue.regions[i % venue.regions.Count];
                    post = new Vector2(region.post.x + 0.5f, region.post.y + 0.5f);
                }
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

        Transform AddPart(Transform parent, Vector3 position, Vector3 scale, Color color)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.GetComponent<MeshRenderer>().sharedMaterial = Lit(color);
            return part.transform;
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
                foreach (var incident in schedule.incidents)
                    if (incident is TowerCollapseIncident tower && tower.Collapsed && !tower.collapseShown) OnTowerCollapsed(tower);
                CheckGas();
                if (fireChanged) OnFireChanged();
            }

            venue.UpdateDoorFields(3);
            MoveUnits(dt);
            UpdateEscorts(dt);
            UpdateDoorClearing(dt);
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

        /// <summary>Planning only: switches difficulty (saved) and starts a new game with its crowd.</summary>
        void SetDifficulty(Difficulty d)
        {
            if (phase != Phase.Planning) return;
            difficulty = d;
            PlayerPrefs.SetInt(DifficultyPref, (int)d);
            PlayerPrefs.Save();
            deferred.Add(NewGame);
        }

        string DifficultyLabel => $"{difficultySettings.Name} (score \u00d7{difficultySettings.scoreMultiplier:0.#})";

        void StartShow()
        {
            if (phase != Phase.Planning) return;
            phase = Phase.Live;
            welcomeDismissed = true;
            // A random round for the chosen difficulty: disasters, blocked exits, chained tower collapses.
            if (schedule.incidents.Count == 0) RoundGenerator.Build(difficultySettings, venue, rng, schedule);
            Log($"{MapTitle} is open ({difficultySettings.Name}). Stay sharp...");
        }

        /// <summary>An incident just began: the first one starts the evacuation clock; each gets a feed line, banner and camera jump.</summary>
        void OnIncidentStarted(Incident incident)
        {
            // A blocked exit alone doesn't raise the alarm; the first fire (or other evacuation incident) does.
            if (incident.StartsEvacuation && !incidentStarted)
            {
                incidentAnnouncement = incident.Announcement;
                incidentStarted = true;
                evacuationTime = 0f;
            }
            SyncFireViews();
            if (incident.Silent) return; // nobody knows yet: announced when it's detected (see CheckGas)
            if (incident is BlockDoorIncident block) CreateBlockerView(block);
            if (incident is TowerCollapseIncident swaying) HingeTower(swaying);
            var severity = incident.StartsEvacuation ? Severity.Danger : Severity.Warning;
            Log(incident.LogText, severity);
            ShowBanner(incident.Headline, incident.Advice, severity);
            var focus = incident.FocusCell;
            cameraRig.Focus(new Vector3(focus.x, 0f, focus.y));
        }

        // ---------------------------------------------------------------- Gas leak

        /// <summary>
        /// Every step while Live: a gas leak nobody knows about is given away by the first cough or by a patrolman or
        /// police officer getting gas in their ring ("Food Court patrol smells gas!"; each unit reports once).
        /// </summary>
        void CheckGas()
        {
            if (!venue.HasGas) return;
            // The leak to report: the first one nobody has noticed yet, otherwise any that's still leaking gas.
            GasLeakIncident leak = null;
            foreach (var incident in schedule.incidents)
                if (incident is GasLeakIncident g && g.Started && !g.Ignited && (leak == null || (leak.Detected && !g.Detected))) leak = g;
            if (leak == null) return;

            foreach (var u in units)
            {
                if (smeltGas.Contains(u) || !GasLeakIncident.Smells(venue, u.position, RoleRadius(u.role))) continue;
                smeltGas.Add(u);
                string who = u.homeRegion >= 0 ? $"{venue.regions[u.homeRegion].name} patrol" : u.Name;
                Log($"{who} smells gas!", Severity.Danger);
                DetectGas(leak, $"the {who}");
            }
            if (!leak.Detected && sim.CoughingCount > 0)
            {
                Log($"People are coughing near {leak.SiteName}. There's gas in the air!", Severity.Danger);
                DetectGas(leak, "coughing people");
            }
        }

        /// <summary>The leak has been noticed: show the gas, raise the alarm (if nothing else has), banner and camera.</summary>
        void DetectGas(GasLeakIncident leak, string by)
        {
            gasVisible = true;
            if (leak.Detected) return;
            leak.Detect(by, evacuationTime);
            if (!incidentStarted)
            {
                incidentAnnouncement = leak.Announcement;
                incidentStarted = true;
                evacuationTime = 0f;
            }
            Log(leak.LogText, Severity.Danger);
            ShowBanner(leak.Headline, leak.Advice, Severity.Danger);
            cameraRig.Focus(new Vector3(leak.FocusCell.x, 0f, leak.FocusCell.y));
        }

        // ---------------------------------------------------------------- Light tower collapse

        /// <summary>Puts the tower block on a pivot at its foot on the side it will fall to, so it can sway and topple.</summary>
        void HingeTower(TowerCollapseIncident t)
        {
            if (towerPivots.ContainsKey(t.towerIndex) || !towerViews.TryGetValue(t.towerIndex, out var view)) return;
            var r = t.tower.rect;
            Vector2 foot = r.center + new Vector2(t.direction.x * r.width, t.direction.y * r.height) * 0.5f;
            var pivot = new GameObject($"{view.name} (hinge)").transform;
            pivot.SetParent(world, false);
            pivot.localPosition = new Vector3(foot.x, 0f, foot.y);
            view.SetParent(pivot, true);
            towerPivots[t.towerIndex] = pivot;
        }

        /// <summary>Axis the tower tips over around: turning "up" towards its fall direction.</summary>
        static Vector3 FallAxis(TowerCollapseIncident t) => Vector3.Cross(Vector3.up, new Vector3(t.direction.x, 0f, t.direction.y));

        /// <summary>Wobble while swaying (growing towards the fall), then a quick topple once it has collapsed.</summary>
        void UpdateTowerViews()
        {
            foreach (var incident in schedule.incidents)
            {
                if (incident is not TowerCollapseIncident t || !t.Started || !towerPivots.TryGetValue(t.towerIndex, out var pivot)) continue;
                float angle;
                if (!t.Collapsed)
                    angle = Mathf.Sin(t.Age * 7f) * (1.5f + 6f * t.SwayProgress) + 4f * t.SwayProgress; // leaning further each swing
                else
                {
                    float fall = towerFallTime.TryGetValue(t, out float f) ? f : 0f;
                    if (!paused) towerFallTime[t] = fall += Time.deltaTime;
                    angle = Mathf.Min(90f, 90f * Mathf.Pow(fall / 0.8f, 2f));
                }
                pivot.localRotation = Quaternion.AngleAxis(angle, FallAxis(t));
            }
        }

        /// <summary>The tower is down: rubble on the wreck cells, feed lines and banner.</summary>
        void OnTowerCollapsed(TowerCollapseIncident t)
        {
            t.collapseShown = true;
            towerFallTime[t] = 0f;
            HingeTower(t); // in case it started before the views existed
            SyncFireViews(); // barriers under the wreck are gone

            var rubble = new GameObject($"Wreck of {t.SiteName}").transform;
            rubble.SetParent(world, false);
            var steel = new Color(0.32f, 0.32f, 0.35f);
            var random = new System.Random(t.towerIndex * 7919 + t.direction.x * 31 + t.direction.y);
            foreach (int c in t.wreck)
            {
                if (venue.cells[c] != CellType.Obstacle) continue;
                var p = FestivalVenue.CellCenter(c);
                var piece = AddPart(rubble, new Vector3(p.x, 0.25f, p.y), new Vector3(0.9f, 0.5f, 0.9f), steel);
                piece.localRotation = Quaternion.Euler(0f, (float)random.NextDouble() * 50f - 25f, 0f);
            }
            // The lighting rig at the far end.
            var end = FestivalVenue.CellCenter(t.wreck[^1]);
            AddPart(rubble, new Vector3(end.x, 0.5f, end.y), new Vector3(2f, 0.9f, 1.4f), new Color(1f, 0.85f, 0.35f));

            string crushed = t.Crushed == 1 ? "1 person was" : $"{t.Crushed} people were";
            Log($"{t.SiteName.Substring(0, 1).ToUpperInvariant()}{t.SiteName.Substring(1)} has collapsed! {crushed} crushed under it. {t.Shaken} people nearby are fleeing in fright.", Severity.Danger);
            ShowBanner("LIGHT TOWER COLLAPSED!", t.Crushed > 0 ? $"{crushed} crushed. Keep people away from the wreckage." : "Nobody was under it. Keep people away from the wreckage.", Severity.Danger);
            foreach (var door in t.blockedDoors)
                Log($"The wreckage is blocking {door.name}. Send a patrol to clear it ({FestivalVenue.DoorClearTime:0} s).", Severity.Warning);
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
                if (u.role != Role.Megaphone || u.escortPhase == EscortPhase.Escorting || u.clearingDoor >= 0) continue; // hands full
                u.announceTimer -= dt;
                if (u.announceTimer > 0f) continue;
                u.announceTimer = policeAnnounceInterval;
                u.waveTime = 0f;
                int informed = sim.PoliceAnnouncement(u.position);
                if (!u.hasAnnounced || informed >= 25)
                    Log($"{u.Name} near {venue.DescribeLocation(FestivalVenue.CellAt(u.position))}: \"{incidentAnnouncement} Walk to an open gate.\" ({informed} now know the way)");
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
                u.clearingDoor = ClearingDoorFor(u);
                // While walking someone out or clearing a door, the unit isn't calming anyone.
                if (u.role == Role.Patrolman && u.escortPhase != EscortPhase.Escorting && u.clearingDoor < 0) patrolPositions.Add(u.position);
            }
        }

        float UnitSpeed(Unit u) =>
            (u.role == Role.Patrolman ? patrolSpeed : policeSpeed) * (u.escortPhase == EscortPhase.Escorting ? EscortSpeedFactor : 1f);

        // ---------------------------------------------------------------- Blocked doors

        /// <summary>The blocked door a standing, free unit is close enough to work on (-1 none).</summary>
        int ClearingDoorFor(Unit u)
        {
            if (u.path.Count > 0 || u.escortPhase != EscortPhase.None) return -1;
            for (int d = 0; d < venue.doors.Count; d++)
                if (venue.doors[d].blocked && FestivalVenue.CanReachToClear(venue.doors[d], u.position)) return d;
            return -1;
        }

        /// <summary>Every unit standing at a blocked door works on it; each one adds 1/8 of the job per second.</summary>
        void UpdateDoorClearing(float dt)
        {
            for (int d = 0; d < venue.doors.Count; d++)
            {
                var door = venue.doors[d];
                if (!door.blocked) continue;
                int workers = 0;
                foreach (var u in units)
                    if (u.clearingDoor == d) workers++;
                if (!venue.ClearDoor(door, workers, dt)) continue;

                foreach (var u in units)
                    if (u.clearingDoor == d) u.clearingDoor = -1;
                if (blockerViews.Remove(door, out var view)) Destroy(view);
                Log($"{door.name} is clear again: people can get out that way.", Severity.Good);
                ShowBanner($"{door.name.ToUpperInvariant()} CLEARED", "The exit can be used again.", Severity.Good);
            }
        }

        /// <summary>Walks a unit to the cell just inside a blocked door; once it stands there it starts clearing.</summary>
        void SendToClear(int index, Door door)
        {
            if (index < 0) return;
            var u = units[index];
            int front = venue.DoorFrontCell(door);
            var path = front >= 0 ? venue.FindPath(u.position, FestivalVenue.CellCenter(front)) : null;
            if (path == null)
            {
                Log($"{u.Name} can't get to {door.name}.", Severity.Warning);
                return;
            }
            if (u.escortPhase != EscortPhase.None)
            {
                CancelEscort(u); // clearing the door replaces the escort
                u.escortPhase = EscortPhase.None;
            }
            u.path.Clear();
            u.path.AddRange(path);
            Log($"{u.Name} is on the way to clear {door.name}.");
        }

        /// <summary>A delivery truck parked across the door outside, or a heap of fallen scaffolding poles over it. Plain cubes.</summary>
        void CreateBlockerView(BlockDoorIncident block)
        {
            var door = block.door;
            if (blockerViews.ContainsKey(door)) return;
            var root = new GameObject($"Blocked: {door.name}").transform;
            root.SetParent(world, false);
            // Local axes: +z points out of the venue (towards the safe zone behind the door), x runs along the door.
            Vector2 outward = Vector2.zero;
            foreach (int c in door.beyond) outward += FestivalVenue.CellCenter(c) - door.Center;
            outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : Vector2.down;
            root.localPosition = new Vector3(door.Center.x, 0f, door.Center.y);
            root.localRotation = Quaternion.LookRotation(new Vector3(outward.x, 0f, outward.y));
            float length = door.Width + 1.5f;

            if (block.reason == BlockDoorIncident.Reasons[0])
            {
                // Delivery truck, just outside, across the whole door.
                var body = new Color(0.92f, 0.92f, 0.88f);
                var cab = new Color(0.85f, 0.2f, 0.15f);
                var tyre = new Color(0.08f, 0.08f, 0.08f);
                AddPart(root, new Vector3(-0.6f, 1.55f, 1.6f), new Vector3(length, 2.3f, 2.3f), body);
                AddPart(root, new Vector3(length * 0.5f + 0.4f, 1.15f, 1.6f), new Vector3(1.8f, 1.7f, 2.3f), cab);
                AddPart(root, new Vector3(length * 0.5f + 1.0f, 1.45f, 1.6f), new Vector3(0.6f, 0.7f, 2.1f), new Color(0.3f, 0.45f, 0.6f)); // windscreen
                foreach (float wx in new[] { -length * 0.5f + 0.3f, length * 0.5f - 0.2f, length * 0.5f + 0.7f })
                    foreach (float wz in new[] { 0.5f, 2.7f })
                        AddPart(root, new Vector3(wx, 0.4f, wz), new Vector3(0.8f, 0.8f, 0.3f), tyre);
            }
            else
            {
                // Collapsed scaffolding: poles and planks lying across the door, a couple still leaning.
                var pole = new Color(0.6f, 0.62f, 0.66f);
                var plank = new Color(0.62f, 0.45f, 0.25f);
                var random = new System.Random(door.name.GetHashCode());
                for (int k = 0; k < 7; k++)
                {
                    float z = -0.4f + (float)random.NextDouble() * 1.8f;
                    var part = AddPart(root, new Vector3(((float)random.NextDouble() - 0.5f) * 1.5f, 0.15f + k * 0.1f, z),
                        new Vector3(length + 1f, 0.15f, 0.15f), k % 3 == 2 ? plank : pole);
                    part.localRotation = Quaternion.Euler(0f, ((float)random.NextDouble() - 0.5f) * 40f, 0f);
                    if (k % 3 == 2) part.localScale = new Vector3(length, 0.1f, 0.5f);
                }
                foreach (float side in new[] { -1f, 1f })
                {
                    var leaning = AddPart(root, new Vector3(side * length * 0.4f, 1.3f, 0.9f), new Vector3(0.15f, 3f, 0.15f), pole);
                    leaning.localRotation = Quaternion.Euler(-35f, 0f, side * 20f);
                }
            }
            blockerViews[door] = root.gameObject;
        }

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
                Log($"{sim.InjuredCount - lastInjured} more people injured (total {sim.InjuredCount}: {sim.InjuredByCrush} crushed, {sim.InjuredByFire} burnt, {sim.InjuredBySmoke} smoke, {sim.InjuredByGas} gas, {sim.InjuredByCollapse} crushed by collapse).", Severity.Danger);
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
            finalScore = difficultySettings.Score(finalSafe, finalInjured, finalTrapped, finalDrunkInside, finalAttacks, evacuationTime, evacuationTimeLimit);

            float safeRatio = finalSafe / (float)sim.count;
            stars = 0;
            if (safeRatio >= 0.9f) stars = 1;
            if (stars == 1 && finalInjured <= sim.count / 100) stars = 2;
            if (stars == 2 && finalInjured == 0 && finalTrapped == 0 && finalDrunkInside == 0 && evacuationTime <= evacuationTimeLimit * 0.5f) stars = 3;
            MapProgress.Record(selectedMap, difficultySettings.difficulty, stars, finalScore);

            resultTitle = finalTrapped == 0 ? "Venue evacuated" : evacuationTime >= evacuationTimeLimit ? "Time's up" : "Evacuation stalled";
            if (gatesOpened == 0)
                resultTip = "No emergency gate was opened. People tried the gates, found them shut and all squeezed through the two narrow entrances.";
            else if (sim.InjuredByCollapse > 0 && sim.InjuredByCollapse >= sim.InjuredByCrush && sim.InjuredByCollapse >= sim.InjuredBySmoke)
                resultTip = "The light tower came down on people. When a tower sways you have 6 seconds: select that zone's patrol and order the zone out.";
            else if (sim.InjuredByGas > 0 && sim.InjuredByGas >= sim.InjuredByCrush && sim.InjuredByGas >= sim.InjuredBySmoke)
                resultTip = "The gas got people. Nobody runs from it on their own: as soon as a patrol smells it, order the zones around the leak out.";
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
                line.sharedMaterial = Unlit(new Color(0.82f, 0.157f, 0.18f)); // red route arrow, like the menu art
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

            // A blocked door: send the selected unit (or, for an entrance, the nearest one) to clear it.
            // With nobody selected, clicking a blocked gate still opens/closes the gate behind the block.
            var door = venue.DoorNear(cell);
            if (door != null && door.blocked && (selectedUnit >= 0 || door.gate == null))
            {
                SendToClear(selectedUnit >= 0 ? selectedUnit : NearestUnit(ground, float.MaxValue), door);
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
            cameraRig.controlsEnabled = phase != Phase.Menu;
            if (keyboard == null) return;
            if (phase == Phase.Menu)
            {
                if (keyboard.escapeKey.wasPressedThisFrame) MenuBack();
                return;
            }
            if (keyboard.rKey.wasPressedThisFrame)
            {
                NewGame();
                return;
            }
            if (keyboard.hKey.wasPressedThisFrame) heatmap = !heatmap;
            if (keyboard.mKey.wasPressedThisFrame) stateMarkers = !stateMarkers;
            if (keyboard.lKey.wasPressedThisFrame) showLegend = !showLegend;
            if (keyboard.uKey.wasPressedThisFrame) hudHidden = !hudHidden;
            if (keyboard.gKey.wasPressedThisFrame) showRegions = !showRegions;
            if (phase == Phase.Results) return;
            if (keyboard.spaceKey.wasPressedThisFrame) paused = !paused;
            if (keyboard.tabKey.wasPressedThisFrame) speedIndex = (speedIndex + 1) % Speeds.Length;
            if (keyboard.digit1Key.wasPressedThisFrame) SetTool(Tool.Command);
            if (keyboard.digit2Key.wasPressedThisFrame) SetTool(Tool.Barrier);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) StartShow();
            if (keyboard.f1Key.wasPressedThisFrame) SetDifficulty(Difficulty.Easy);
            if (keyboard.f2Key.wasPressedThisFrame) SetDifficulty(Difficulty.Medium);
            if (keyboard.f3Key.wasPressedThisFrame) SetDifficulty(Difficulty.Hard);
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (selectedUnit < 0 && phase == Phase.Planning) OpenMenu(); // back to the start menu
                else selectedUnit = -1;
            }
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
            if (mouse == null || phase == Phase.Results || phase == Phase.Menu)
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
                bool ringOn = u.escortPhase != EscortPhase.Escorting && u.clearingDoor < 0; // no calming while walking someone out or clearing a door
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

            UpdateTowerViews();

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
                    CellType.Open => Color.Lerp(heatmap ? PressureColor(sim.pressure[i]) : MapFloorColor(i), SmokeShade, venue.smoke[i] * 0.6f),
                    CellType.Fire => Scorched,
                    _ => Blocked,
                };
            }
            if (gasVisible && venue.HasGas)
            {
                // Yellow-green haze where the gas is.
                for (int i = 0; i < groundPixels.Length; i++)
                    if (venue.gas[i] > GasLeakIncident.DetectLevel && venue.cells[i] != CellType.Outside)
                        groundPixels[i] = Color.Lerp(groundPixels[i], GasColor, Mathf.Min(0.75f, 0.2f + venue.gas[i] * 0.6f));
            }
            if (showRegions) DrawRegionOutlines();
            groundTexture.SetPixels32(groundPixels);
            groundTexture.Apply(false);
        }

        Color MapFloorColor(int cell)
        {
            if (venue.map == VenueMap.CityStadium) return new Color(0.43f, 0.46f, 0.49f);
            if (venue.map == VenueMap.MetroConcourse)
                return ((cell % W / 4 + cell / W / 4) & 1) == 0 ? new Color(0.55f, 0.58f, 0.61f) : new Color(0.49f, 0.52f, 0.56f);
            return Grass;
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
            log.Add((message, severity, incidentStarted ? FormatTime(evacuationTime) : "", feedClock));
            if (log.Count > 40) log.RemoveAt(0);
        }

        static string FormatTime(float seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

        // ================================================================ UI
        //
        // "Operations desk" look, matching the menu art: cream paper panels with soft shadows, navy ink, red accents,
        // yellow tabs ("01 — YOUR TEAM ///"), numbered rows with a red slanted slab for the active one, boxed
        // "01 | LABEL" tags, green EXIT signs. Fonts are Barlow Condensed / Barlow from Resources/Fonts (OFL); without
        // them it falls back to the default font. Textures are generated and cached (paper, frames, slabs, stripes,
        // the EXIT runner).

        static bool welcomeDismissed;
        bool showLegend;                    // colour key (L), closed by default
        bool showTeam = true, showGates = true; // fold the roster / exits panel to its title
        bool hudHidden;                     // U: hide every panel except the top strip, the toolbar and the toggles
        float feedClock;                    // game-time clock (stops while paused) that fades out old log lines
        float viewTogglesTop;               // top of the view toggle stack, set each frame; the legend sits above it
        float uiScale = 1f, vw, vh;         // UI scale and the virtual screen size it works in
        float bannerTime;
        string bannerTitle, bannerText;
        Severity bannerSeverity;

        // Palette (from the menu art).
        static readonly Color Ink = new(0.106f, 0.137f, 0.251f);        // #1b2340 navy
        static readonly Color InkSoft = new(0.294f, 0.329f, 0.439f);    // #4b5470
        static readonly Color InkFaint = new(0.42f, 0.443f, 0.502f);    // #6b7180
        static readonly Color Paper = new(0.937f, 0.906f, 0.839f);      // #efe7d6
        static readonly Color PaperLight = new(0.969f, 0.949f, 0.902f); // #f7f2e6
        static readonly Color Red = new(0.82f, 0.157f, 0.18f);          // #d1282e
        static readonly Color Yellow = new(0.949f, 0.761f, 0.188f);     // #f2c230
        static readonly Color ExitGreen = new(0.122f, 0.541f, 0.227f);  // #1f8a3a
        static readonly Color Amber = new(0.604f, 0.42f, 0f);           // #9a6b00, yellow that reads on paper
        static readonly Color PanicInk = new(0.69f, 0.157f, 0.478f);    // #b0287a

        // Older names, still used by the menu and the results.
        static readonly Color TextColor = Ink;
        static readonly Color MutedText = InkSoft;
        static readonly Color CardColor = PaperLight;
        static readonly Color SelectedColor = Red;
        static readonly Color GoodColor = ExitGreen;
        static readonly Color WarningColor = new(0.878f, 0.478f, 0.063f); // #e07a10
        static readonly Color DangerColor = Red;

        const float TopBarHeight = 60f, ToolbarHeight = 50f;

        Font fontHeavy, fontBoldItalic, fontBold, fontSemi, fontBody, fontBodyBold;
        GUIStyle buttonStyle, selectedButtonStyle, goodButtonStyle, smallButtonStyle, smallGoodButtonStyle, navyButtonStyle, hoverStyle, tooltipStyle;
        GUIStyle labelStyle, mutedStyle, smallStyle, headerStyle, titleStyle, bigTitleStyle, numberStyle;
        GUIStyle logoStyle, tabStyle, timerStyle, timerTotalStyle, statValueStyle, captionStyle, speedStyle;
        GUIStyle rowNumberStyle, rowNameStyle, rowStatusStyle, groupStyle, headerRightStyle, gateStatusStyle;
        GUIStyle feedTimeStyle, feedStyle, orderTitleStyle, orderSubStyle, arrowStyle, toolNumberStyle, toolLabelStyle;
        GUIStyle keyStyle, toggleLabelStyle, hintStyle, legendStyle, bannerTitleStyle, bannerTextStyle;
        GUIStyle worldTagStyle, worldKeyStyle, signStyle, signNameStyle, landmarkStyle, crossStyle, stepTitleStyle, resultValueStyle, starsCaptionStyle;
        readonly Dictionary<string, Texture2D> textureCache = new();
        readonly Dictionary<string, GUIStyle> drawStyleCache = new();

        void OnGUI()
        {
            if (sim == null) return;
            uiScale = Mathf.Clamp(Screen.height / 900f, 0.75f, 2f);
            vw = Screen.width / uiScale;
            vh = Screen.height / uiScale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));
            EnsureStyles();
            guiRects.Clear();
            if (phase == Phase.Menu)
            {
                DrawMenu();
                return;
            }
            if (Event.current.type == EventType.Layout) orderPanelUnit = SelectedPatrol() != null ? selectedUnit : -1;
            if (Event.current.type == EventType.Repaint && !paused) feedClock += Time.unscaledDeltaTime * (phase == Phase.Live ? Speeds[speedIndex] : 1f);

            DrawWorldLabels();
            DrawTopBar();
            if (!hudHidden)
            {
                DrawTeamPanel();
                DrawControlPanel();
                DrawEventFeed();
            }
            DrawToolbar();
            DrawZoneOrderPanel();
            DrawViewToggles();
            if (!hudHidden) DrawLegend();
            DrawBanner();
            DrawHoverInfo();
            if (phase == Phase.Planning && !welcomeDismissed) DrawWelcome();
            if (phase == Phase.Results) DrawResults();
        }

        // ---------------------------------------------------------------- Textures

        Texture2D MakeTexture(string key, int width, int height, System.Func<int, int, Color> pixel, bool repeat = false, bool point = false)
        {
            if (textureCache.TryGetValue(key, out var cached) && cached != null) return cached;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                filterMode = point ? FilterMode.Point : FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = pixel(x, y);
            texture.SetPixels(pixels);
            texture.Apply();
            return textureCache[key] = texture;
        }

        Texture2D Solid(Color c) => MakeTexture($"solid{c}", 2, 2, (x, y) => c);

        /// <summary>Box with a <paramref name="thickness"/>-pixel outline (9-sliced, so the line stays thin at any size).</summary>
        Texture2D Framed(Color fill, Color line, int thickness = 2) =>
            MakeTexture($"framed{fill}{line}{thickness}", 8, 8, (x, y) => x < thickness || y < thickness || x >= 8 - thickness || y >= 8 - thickness ? line : fill, point: true);

        /// <summary>Slab whose right end slants in towards the bottom, like the menu's "START GAME" bar.</summary>
        Texture2D Slab(Color fill, int slant)
        {
            int width = slant + 12, height = 32;
            return MakeTexture($"slab{fill}{slant}", width, height, (x, y) =>
            {
                float edge = width - slant + slant * (y + 0.5f) / height; // y = 0 is the bottom row
                float alpha = Mathf.Clamp01(edge - (x + 0.5f) + 0.5f);
                return new Color(fill.r, fill.g, fill.b, fill.a * alpha);
            });
        }

        Texture2D Stripes(Color c) => MakeTexture($"stripes{c}", 16, 16, (x, y) => ((x - y) % 8 + 8) % 8 < 4 ? c : Color.clear, repeat: true);

        Texture2D PaperGrid => MakeTexture("paperGrid", 22, 22, (x, y) => x == 0 || y == 0 ? new Color(Ink.r, Ink.g, Ink.b, 0.06f) : Color.clear, repeat: true, point: true);

        Texture2D ShadowTexture => MakeTexture("shadow", 48, 48, (x, y) =>
        {
            float dx = Mathf.Max(0f, Mathf.Max(16f - (x + 0.5f), (x + 0.5f) - 32f));
            float dy = Mathf.Max(0f, Mathf.Max(16f - (y + 0.5f), (y + 0.5f) - 32f));
            float a = 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / 16f);
            return new Color(0f, 0f, 0f, 0.4f * a * a);
        });

        static readonly string[] RunnerPixels =
        {
            "................",
            "..........###...",
            "..........###...",
            "..........###...",
            "......######....",
            ".....##.####....",
            "....##..####.#..",
            "........####..#.",
            "........###.....",
            ".......#####....",
            "......##...##...",
            ".....##.....##..",
            "....##.......#..",
            "...##.........#.",
            "..##............",
            "................",
        };

        /// <summary>The running man from an exit sign, white (tint it with GUI.color).</summary>
        Texture2D Runner => MakeTexture("runner", 16, 16, (x, y) => RunnerPixels[15 - y][x] == '#' ? Color.white : Color.clear, point: true);

        // Kept for the menu, which still asks for rounded boxes.
        Texture2D Rounded(Color fill, int radius)
        {
            radius = Mathf.Max(1, radius);
            int size = radius * 2 + 2;
            return MakeTexture($"rounded{fill}{radius}", size, size, (x, y) =>
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var inner = new Vector2(Mathf.Clamp(p.x, radius, size - radius), Mathf.Clamp(p.y, radius, size - radius));
                float alpha = Mathf.Clamp01(radius + 0.5f - Vector2.Distance(p, inner));
                return new Color(fill.r, fill.g, fill.b, fill.a * alpha);
            });
        }

        // ---------------------------------------------------------------- Styles

        GUIStyle DrawStyle(string key, Texture2D background, RectOffset border)
        {
            if (drawStyleCache.TryGetValue(key, out var s) && s.normal.background != null) return s;
            return drawStyleCache[key] = new GUIStyle { normal = { background = background }, border = border };
        }

        GUIStyle BoxStyle(Color fill, int radius, int padding)
        {
            var style = new GUIStyle { border = new RectOffset(radius, radius, radius, radius), padding = new RectOffset(padding, padding, padding, padding) };
            style.normal.background = Rounded(fill, radius);
            style.normal.textColor = Ink;
            return style;
        }

        /// <summary>Framed paper button (dark fills get light text). Kept for the menu's map rows.</summary>
        GUIStyle ButtonStyle(Color fill, int fontSize = 15, TextAnchor alignment = TextAnchor.MiddleCenter)
        {
            bool dark = fill.grayscale < 0.5f;
            var text = dark ? Color.white : Ink;
            var line = dark ? fill : Ink;
            var style = new GUIStyle(GUI.skin.button)
            {
                font = fontBold,
                fontSize = fontSize,
                fontStyle = FontStyle.Normal,
                alignment = alignment,
                border = new RectOffset(3, 3, 3, 3),
                padding = new RectOffset(10, 10, 4, 4),
                margin = new RectOffset(0, 0, 3, 3),
                richText = true,
            };
            style.normal.background = style.onNormal.background = style.focused.background = style.onFocused.background = Framed(fill, line);
            style.hover.background = style.onHover.background = Framed(Color.Lerp(fill, Color.white, dark ? 0.15f : 0.5f), line);
            style.active.background = style.onActive.background = Framed(Color.Lerp(fill, Ink, 0.15f), line);
            foreach (var state in new[] { style.normal, style.onNormal, style.hover, style.onHover, style.active, style.onActive, style.focused, style.onFocused })
                state.textColor = text;
            return style;
        }

        GUIStyle SlabButtonStyle(Color fill, Color text, Font font, int fontSize)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                font = font,
                fontSize = fontSize,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                border = new RectOffset(4, 22, 0, 0),
                padding = new RectOffset(14, 26, 2, 2),
                margin = new RectOffset(0, 0, 4, 4),
                richText = true,
            };
            style.normal.background = style.onNormal.background = style.focused.background = style.onFocused.background = Slab(fill, 18);
            style.hover.background = style.onHover.background = Slab(Color.Lerp(fill, Color.white, 0.15f), 18);
            style.active.background = style.onActive.background = Slab(Color.Lerp(fill, Color.black, 0.15f), 18);
            foreach (var state in new[] { style.normal, style.onNormal, style.hover, style.onHover, style.active, style.onActive, style.focused, style.onFocused })
                state.textColor = text;
            return style;
        }

        GUIStyle Txt(Font font, int size, Color color, TextAnchor anchor = TextAnchor.UpperLeft, bool wrap = false, bool clip = false)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                font = font,
                fontSize = size,
                fontStyle = FontStyle.Normal,
                alignment = anchor,
                wordWrap = wrap,
                richText = true,
                clipping = clip ? TextClipping.Clip : TextClipping.Overflow,
            };
            SetTextColor(style, color);
            return style;
        }

        /// <summary>
        /// Sets the text colour of every state. A style cloned from <c>GUI.skin.label</c> keeps the skin's own hover/active
        /// colours, so text drawn over a hoverable row would change colour under the mouse (the fix from the Oli branch).
        /// </summary>
        static void SetTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = color;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = color;
        }

        /// <summary>Old-style text helper (the menu uses it): Bold maps to Barlow Condensed Bold, the rest to Barlow.</summary>
        GUIStyle TextStyle(int size, Color color, FontStyle font = FontStyle.Normal, TextAnchor alignment = TextAnchor.UpperLeft, bool wrap = false) =>
            font == FontStyle.Normal ? Txt(fontBody, size, color, alignment, wrap) : Txt(fontBold, size + 2, color, alignment, wrap);

        void EnsureStyles()
        {
            if (buttonStyle != null && buttonStyle.normal.background != null) return;
            fontHeavy = Resources.Load<Font>("Fonts/BarlowCondensed-ExtraBoldItalic");
            fontBoldItalic = Resources.Load<Font>("Fonts/BarlowCondensed-BoldItalic");
            fontBold = Resources.Load<Font>("Fonts/BarlowCondensed-Bold");
            fontSemi = Resources.Load<Font>("Fonts/BarlowCondensed-SemiBold");
            fontBody = Resources.Load<Font>("Fonts/Barlow-Medium");
            fontBodyBold = Resources.Load<Font>("Fonts/Barlow-SemiBold");

            buttonStyle = ButtonStyle(PaperLight);
            selectedButtonStyle = ButtonStyle(Red);
            smallButtonStyle = ButtonStyle(PaperLight, 13);
            smallGoodButtonStyle = ButtonStyle(ExitGreen, 13);
            navyButtonStyle = ButtonStyle(Ink, 15);
            foreach (var s in new[] { smallButtonStyle, smallGoodButtonStyle }) s.padding = new RectOffset(4, 4, 1, 1);
            goodButtonStyle = SlabButtonStyle(Red, Color.white, fontHeavy, 24);
            hoverStyle = new GUIStyle();
            hoverStyle.hover.background = Solid(new Color(Ink.r, Ink.g, Ink.b, 0.08f));

            tooltipStyle = Txt(fontBody, 13, Ink, wrap: true);
            tooltipStyle.normal.background = Framed(PaperLight, Ink);
            tooltipStyle.border = new RectOffset(3, 3, 3, 3);
            tooltipStyle.padding = new RectOffset(10, 10, 7, 7);

            labelStyle = Txt(fontBody, 14, Ink, wrap: true);
            mutedStyle = Txt(fontBody, 13, InkSoft, wrap: true);
            smallStyle = Txt(fontBody, 12, InkFaint, clip: true);
            stepTitleStyle = Txt(fontHeavy, 18, Ink);
            resultValueStyle = Txt(fontBold, 17, Ink, TextAnchor.UpperRight);
            starsCaptionStyle = Txt(fontBold, 14, InkSoft, TextAnchor.UpperCenter);
            headerStyle = Txt(fontBold, 14, InkSoft);
            titleStyle = Txt(fontHeavy, 30, Ink);
            bigTitleStyle = Txt(fontHeavy, 36, Ink, TextAnchor.UpperCenter);
            numberStyle = Txt(fontHeavy, 20, Color.white, TextAnchor.MiddleCenter);

            logoStyle = Txt(fontHeavy, 32, Ink, TextAnchor.MiddleLeft);
            tabStyle = Txt(fontHeavy, 15, Ink, TextAnchor.MiddleLeft);
            timerStyle = Txt(fontHeavy, 30, Red, TextAnchor.MiddleLeft);
            timerTotalStyle = Txt(fontSemi, 15, InkFaint, TextAnchor.MiddleLeft);
            statValueStyle = Txt(fontHeavy, 24, Ink, TextAnchor.MiddleCenter);
            captionStyle = Txt(fontBold, 13, Ink, TextAnchor.MiddleLeft);
            speedStyle = Txt(fontHeavy, 16, Ink, TextAnchor.MiddleCenter);
            rowNumberStyle = Txt(fontBoldItalic, 14, Ink, TextAnchor.MiddleLeft);
            rowNameStyle = Txt(fontBold, 16, Ink, TextAnchor.MiddleLeft, clip: true);
            rowStatusStyle = Txt(fontBold, 11, InkSoft, TextAnchor.MiddleRight);
            groupStyle = Txt(fontBold, 12, InkSoft, TextAnchor.LowerLeft);
            headerRightStyle = Txt(fontBold, 14, InkFaint, TextAnchor.MiddleRight);
            gateStatusStyle = Txt(fontHeavy, 13, Ink, TextAnchor.MiddleRight);
            feedTimeStyle = Txt(fontBoldItalic, 14, Ink, TextAnchor.MiddleLeft);
            feedStyle = Txt(fontBodyBold, 13, Ink, TextAnchor.MiddleLeft, clip: true);
            orderTitleStyle = Txt(fontHeavy, 17, Red, TextAnchor.MiddleCenter);
            orderSubStyle = Txt(fontSemi, 13, Ink, TextAnchor.MiddleCenter);
            arrowStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            SetTextColor(arrowStyle, Red);
            toolNumberStyle = Txt(fontBoldItalic, 15, Ink, TextAnchor.MiddleLeft);
            toolLabelStyle = Txt(fontHeavy, 24, Ink, TextAnchor.MiddleLeft);
            keyStyle = Txt(fontBold, 14, Ink, TextAnchor.MiddleCenter);
            toggleLabelStyle = Txt(fontBold, 14, Ink, TextAnchor.MiddleLeft);
            hintStyle = Txt(fontBodyBold, 13, Ink, TextAnchor.MiddleCenter);
            legendStyle = Txt(fontBody, 13, Ink);
            bannerTitleStyle = Txt(fontHeavy, 30, Color.white, TextAnchor.MiddleLeft);
            bannerTextStyle = Txt(fontBodyBold, 14, Color.white, TextAnchor.MiddleLeft);
            worldTagStyle = Txt(fontBold, 14, Ink, TextAnchor.MiddleLeft);
            worldKeyStyle = Txt(fontBold, 14, Ink, TextAnchor.MiddleCenter);
            signStyle = Txt(fontHeavy, 17, ExitGreen, TextAnchor.MiddleLeft);
            signNameStyle = Txt(fontBold, 12, Ink, TextAnchor.MiddleCenter);
            landmarkStyle = Txt(fontBold, 14, Paper, TextAnchor.MiddleCenter);
            crossStyle = Txt(fontHeavy, 18, Color.white, TextAnchor.MiddleCenter);
        }

        // ---------------------------------------------------------------- Widgets

        static bool Repaint => Event.current.type == EventType.Repaint;

        void Fill(Rect r, Color c)
        {
            if (Repaint) GUI.DrawTexture(r, Solid(c));
        }

        void Frame(Rect r, Color fill, Color line)
        {
            if (Repaint) DrawStyle($"frame{fill}{line}", Framed(fill, line), new RectOffset(3, 3, 3, 3)).Draw(r, false, false, false, false);
        }

        void DrawSlab(Rect r, Color fill, int slant = 12)
        {
            if (Repaint) DrawStyle($"slab{fill}{slant}", Slab(fill, slant), new RectOffset(4, slant + 4, 0, 0)).Draw(r, false, false, false, false);
        }

        void DrawStripes(Rect r, Color c)
        {
            if (Repaint) GUI.DrawTextureWithTexCoords(r, Stripes(c), new Rect(0f, 0f, r.width / 16f, r.height / 16f));
        }

        void DropShadow(Rect r)
        {
            if (Repaint) DrawStyle("shadow", ShadowTexture, new RectOffset(16, 16, 16, 16)).Draw(new Rect(r.x - 12, r.y - 7, r.width + 24, r.height + 22), false, false, false, false);
        }

        /// <summary>A paper panel with a soft shadow; the mouse doesn't click through it.</summary>
        Rect Panel(Rect rect, GUIStyle style = null)
        {
            if (style != null)
            {
                GUI.Box(rect, GUIContent.none, style);
                guiRects.Add(rect);
                return rect;
            }
            DropShadow(rect);
            Fill(rect, Paper);
            guiRects.Add(rect);
            return rect;
        }

        void Label(Rect r, string text, GUIStyle style, Color color)
        {
            var old = style.normal.textColor;
            SetTextColor(style, color);
            GUI.Label(r, text, style);
            SetTextColor(style, old);
        }

        static bool Hovered(Rect rect) => GUI.enabled && rect.Contains(Event.current.mousePosition);

        /// <summary>Hover colour for a filled shape: dark fills get a little lighter, light ones a little darker (like the menu).</summary>
        static Color HoverShade(Color fill) => fill.grayscale < 0.5f ? Color.Lerp(fill, Color.white, 0.12f) : Color.Lerp(fill, Ink, 0.08f);

        float TextWidth(string text, GUIStyle style) => style.CalcSize(new GUIContent(text)).x;

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        /// <summary>Yellow (or other) tab with a slanted end: "TEXT  ///". Returns its rect.</summary>
        Rect Tab(float x, float y, string text, Color fill, Color ink, Color slashes, float height = 24f)
        {
            string content = $"{text}  <color=#{Hex(slashes)}>///</color>";
            var rect = new Rect(x, y, TextWidth(content, tabStyle) + 34f, height);
            DrawSlab(rect, fill, 10);
            Label(new Rect(rect.x + 10, rect.y, rect.width, height), content, tabStyle, ink);
            return rect;
        }

        /// <summary>Panel title row: the tab on the left, <paramref name="extra"/> and "–"/"+" on the right; the row folds the panel.</summary>
        bool SectionHeader(Rect panel, string title, string extra, bool open)
        {
            var row = new Rect(panel.x, panel.y, panel.width, 24);
            bool clicked = GUI.Button(row, GUIContent.none, GUIStyle.none);
            Tab(panel.x, panel.y, title, Yellow, Ink, Ink);
            Label(new Rect(row.x, row.y, row.width - 10, 24), $"{extra}   {(open ? "–" : "+")}", headerRightStyle, InkFaint);
            return clicked;
        }

        /// <summary>Boxed "key | label" tag like the map labels in the menu art.</summary>
        void BoxedTag(Rect rect, string key, string label, bool inverted, float keyWidth = 26f, GUIStyle labelStyleOverride = null)
        {
            var fill = inverted ? Ink : PaperLight;
            var ink = inverted ? Paper : Ink;
            Frame(rect, fill, Ink);
            Fill(new Rect(rect.x + keyWidth, rect.y + 2, 2, rect.height - 4), inverted ? Paper : Ink);
            Label(new Rect(rect.x, rect.y, keyWidth, rect.height), key, keyStyle, ink);
            Label(new Rect(rect.x + keyWidth + 9, rect.y, rect.width - keyWidth - 9, rect.height), label, labelStyleOverride ?? toggleLabelStyle, ink);
        }

        void Swatch(Rect rect, Color color)
        {
            Fill(rect, Ink);
            Fill(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width - 3f, rect.height - 3f), color);
        }

        void ProgressBar(Rect rect, float value, Color color)
        {
            Frame(rect, PaperLight, Ink);
            var inner = new Rect(rect.x + 2, rect.y + 2, (rect.width - 4) * Mathf.Clamp01(value), rect.height - 4);
            Fill(inner, color);
            DrawStripes(inner, new Color(0f, 0f, 0f, 0.15f));
        }

        void HaloLabel(Rect rect, string text, GUIStyle style, Color halo)
        {
            var color = style.normal.textColor;
            SetTextColor(style, halo);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) GUI.Label(new Rect(rect.x + dx, rect.y + dy, rect.width, rect.height), text, style);
            SetTextColor(style, color);
            GUI.Label(rect, text, style);
        }

        void ShadowLabel(Rect rect, string text, GUIStyle style) => HaloLabel(rect, text, style, new Color(0f, 0f, 0f, 0.75f));

        static Color SeverityColor(Severity severity) => severity switch
        {
            Severity.Good => ExitGreen,
            Severity.Warning => WarningColor,
            Severity.Danger => Red,
            _ => InkSoft,
        };

        void ShowBanner(string title, string text, Severity severity)
        {
            bannerTitle = title;
            bannerText = text;
            bannerSeverity = severity;
            bannerTime = 5f;
        }

        // ---------------------------------------------------------------- Top strip

        void DrawTopBar()
        {
            var bar = new Rect(0, 0, vw, TopBarHeight);
            DropShadow(bar);
            Fill(bar, Paper);
            if (Repaint) GUI.DrawTextureWithTexCoords(bar, PaperGrid, new Rect(0f, 0f, vw / 22f, TopBarHeight / 22f));
            Fill(new Rect(0, TopBarHeight - 2, vw, 2), Ink);
            guiRects.Add(bar);

            // Logo: CRUSH (navy) HOUR (red) and the red stripes.
            float x = 16;
            string logo = $"CRUSH <color=#{Hex(Red)}>HOUR</color>";
            float logoWidth = TextWidth(logo, logoStyle);
            GUI.Label(new Rect(x, 6, logoWidth + 4, 46), logo, logoStyle);
            x += logoWidth + 8;
            DrawStripes(new Rect(x, 19, 24, 22), Red);
            x += 38;
            Fill(new Rect(x, 13, 2, 34), Ink);
            x += 16;
            if (phase == Phase.Planning)
            {
                if (SpeedButton(new Rect(x, 14, 104, 32), "MAIN MENU", false)) deferred.Add(OpenMenu);
                x += 118;
            }

            // Headline numbers, from the right.
            float right = vw - 14;
            right = Stat(right, "PANIC\nATTACKS", sim.PanicAttackCount.ToString(), PanicInk);
            right = Stat(right, "INJURED", sim.InjuredCount.ToString(), Red);
            right = Stat(right, "OUT\nSAFELY", sim.SafeCount.ToString(), ExitGreen);
            right = Stat(right, "PEOPLE\nINSIDE", sim.ActiveCount.ToString(), Ink);

            // Speed, left of the numbers.
            right -= 18;
            float sx = right - (46 + 4 + Speeds.Length * 42);
            if (SpeedButton(new Rect(sx, 14, 46, 32), paused ? "PLAY" : "II", paused)) paused = !paused;
            for (int i = 0; i < Speeds.Length; i++)
                if (SpeedButton(new Rect(sx + 50 + i * 42, 14, 38, 32), $"{Speeds[i]:0}×", speedIndex == i && !paused))
                {
                    speedIndex = i;
                    paused = false;
                }
            right = sx - 18;

            // Evacuation clock, left of the speed buttons.
            if (phase == Phase.Live && incidentStarted)
            {
                float barWidth = Mathf.Clamp(right - x - 360, 60, 150);
                float tx = right - barWidth;
                float t = evacuationTime / evacuationTimeLimit;
                ProgressBar(new Rect(tx, 25, barWidth, 11), t, Red);
                string total = $"/ {ClockTime(evacuationTimeLimit)}";
                float totalWidth = TextWidth(total, timerTotalStyle);
                tx -= totalWidth + 10;
                GUI.Label(new Rect(tx, 14, totalWidth + 4, 32), total, timerTotalStyle);
                string now = ClockTime(evacuationTime);
                float nowWidth = TextWidth(now, timerStyle);
                tx -= nowWidth + 8;
                GUI.Label(new Rect(tx, 12, nowWidth + 4, 36), now, timerStyle);
                right = tx - 14;
            }

            // Phase tab and one line under it.
            string tab, line;
            switch (phase)
            {
                case Phase.Planning:
                    tab = "PLANNING — SET UP YOUR TEAM";
                    line = "Place patrols and barriers, then open the venue. F1/F2/F3: difficulty.";
                    break;
                case Phase.Live when !incidentStarted:
                    tab = "VENUE OPEN — ALL CALM";
                    line = "Everything's fine... for now.";
                    break;
                case Phase.Live:
                    tab = "LIVE — EVACUATION";
                    line = incidentAnnouncement;
                    break;
                default:
                    tab = "RESULTS";
                    line = "";
                    break;
            }
            Tab(x, 8, tab, Yellow, Ink, Ink, 22);
            string sub = $"{line}{(line.Length > 0 ? "  ·  " : "")}{MapTitle} · {difficultySettings.Name} ×{difficultySettings.scoreMultiplier:0.#}";
            Label(new Rect(x, 33, Mathf.Max(60, right - x), 18), sub, smallStyle, InkSoft);
        }

        static string ClockTime(float seconds) => $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";

        bool SpeedButton(Rect rect, string label, bool on)
        {
            Frame(rect, on ? Red : PaperLight, on ? Red : Ink);
            bool clicked = GUI.Button(rect, GUIContent.none, hoverStyle);
            Label(rect, label, speedStyle, on ? Color.white : Ink);
            return clicked;
        }

        float Stat(float right, string caption, string value, Color color)
        {
            float valueWidth = Mathf.Max(44, TextWidth(value, statValueStyle) + 18);
            float captionWidth = TextWidth(caption, captionStyle) + 18;
            var rect = new Rect(right - valueWidth - captionWidth, 10, valueWidth + captionWidth, 40);
            Frame(rect, PaperLight, Ink);
            Fill(new Rect(rect.x + valueWidth, rect.y + 2, 2, rect.height - 4), Ink);
            Label(new Rect(rect.x, rect.y, valueWidth, rect.height), value, statValueStyle, color);
            GUI.Label(new Rect(rect.x + valueWidth + 9, rect.y, captionWidth, rect.height), caption, captionStyle);
            return rect.x - 6;
        }

        // ---------------------------------------------------------------- 01 — Your team

        string UnitStatus(Unit u) => u.escortPhase switch
        {
            EscortPhase.Fetching => "FETCHING",
            EscortPhase.Escorting => "ESCORTING",
            EscortPhase.Returning => "RETURNING",
            _ => u.clearingDoor >= 0 ? "CLEARING EXIT"
                : u.path.Count > 0 ? "MOVING"
                : u.role == Role.Megaphone && incidentStarted ? "ANNOUNCING"
                : null, // in position: nothing to say
        };

        void DrawTeamPanel()
        {
            const float width = 252, rowHeight = 27, groupHeight = 28;
            float height = 24 + (showTeam ? 2 * groupHeight + units.Count * rowHeight + 8 : 6);
            var rect = Panel(new Rect(14, TopBarHeight + 16, width, height));
            if (SectionHeader(rect, "01 — YOUR TEAM", units.Count.ToString(), showTeam)) deferred.Add(() => showTeam = !showTeam);
            if (!showTeam) return;

            // Grouped: the zone patrols first (numbered by zone), then the police.
            float y = rect.y + 24;
            foreach (var role in new[] { Role.Patrolman, Role.Megaphone })
            {
                var caption = new Rect(rect.x + 10, y + 8, width - 20, 17);
                GUI.Label(caption, role == Role.Patrolman ? "ZONE PATROLS" : "MEGAPHONE POLICE", groupStyle);
                Fill(new Rect(caption.x, caption.yMax + 1, caption.width, 2), Ink);
                y += groupHeight;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u.role != role) continue;
                    var row = new Rect(rect.x + 4, y, width - 10, rowHeight);
                    bool selected = i == selectedUnit;
                    bool zone = u.homeRegion >= 0 && u.homeRegion < venue.regions.Count;
                    if (selected) DrawSlab(row, Red, 12);
                    if (GUI.Button(row, GUIContent.none, selected ? GUIStyle.none : hoverStyle)) SelectUnit(i, true);
                    var ink = selected ? Color.white : Ink;
                    string number = zone ? $"{u.homeRegion + 1:00}" : $"P{u.number}";
                    Label(new Rect(row.x + 6, row.y, 24, rowHeight), number, rowNumberStyle, ink);
                    Fill(new Rect(row.x + 30, row.y + 5, 2, rowHeight - 10), selected ? new Color(1f, 1f, 1f, 0.75f) : Ink);
                    Swatch(new Rect(row.x + 39, row.y + 9, 9, 9), UnitColor(u));
                    string status = UnitStatus(u);
                    float statusWidth = status != null ? TextWidth(status, rowStatusStyle) + 6 : 0;
                    Label(new Rect(row.x + 55, row.y, row.width - 55 - 22 - statusWidth, rowHeight), zone ? venue.regions[u.homeRegion].name : u.Name, rowNameStyle, ink);
                    if (status != null)
                        Label(new Rect(row.x, row.y, row.width - 22, rowHeight), status, rowStatusStyle,
                            selected ? Color.white : status == "CLEARING EXIT" ? Red : InkSoft);
                    y += rowHeight;
                }
            }
        }

        // ---------------------------------------------------------------- 02 — Exits

        void DrawControlPanel()
        {
            const float width = 252, rowHeight = 32;
            float height = 24 + (showGates ? 6 + venue.gates.Count * rowHeight + 52 : 6);
            var rect = Panel(new Rect(vw - 14 - width, TopBarHeight + 16, width, height));
            int open = 0;
            foreach (var g in venue.gates)
                if (g.open) open++;
            if (SectionHeader(rect, "02 — EXITS", $"{open}/{venue.gates.Count} OPEN", showGates)) deferred.Add(() => showGates = !showGates);
            if (!showGates) return;

            float y = rect.y + 30;
            foreach (var g in venue.gates)
            {
                var row = new Rect(rect.x + 10, y, width - 20, rowHeight);
                var nameRect = new Rect(row.x, row.y, row.width - 56, rowHeight);
                if (GUI.Button(nameRect, GUIContent.none, hoverStyle)) cameraRig.Focus(new Vector3(g.rect.center.x, 0f, g.rect.center.y));
                ExitSign(new Rect(row.x + 2, row.y + 5, 22, 22), g);
                GUI.Label(new Rect(row.x + 32, row.y, nameRect.width - 32, rowHeight), g.name, rowNameStyle);
                Label(new Rect(nameRect.x, nameRect.y, nameRect.width - 6, rowHeight), GateWord(g), gateStatusStyle, GateInk(g));
                if (GUI.Button(new Rect(row.xMax - 52, row.y + 5, 52, 22), g.open ? "CLOSE" : "OPEN", g.open ? smallButtonStyle : smallGoodButtonStyle)) ToggleGate(g);
                Fill(new Rect(row.x, row.yMax - 1, row.width, 1), new Color(Ink.r, Ink.g, Ink.b, 0.25f));
                y += rowHeight;
            }
            var all = new Rect(rect.x + 10, y + 10, width - 20, 32);
            DrawSlab(all, Hovered(all) ? HoverShade(Ink) : Ink, 12);
            if (GUI.Button(all, GUIContent.none, GUIStyle.none)) OpenAllGates();
            Label(new Rect(all.x + 12, all.y, all.width, all.height), "OPEN ALL EXITS", tabStyle, Paper);
        }

        static string GateWord(Gate g) => g.Passable ? "OPEN" : g.open ? "OPENING" : "SHUT";

        static Color GateInk(Gate g) => g.Passable ? ExitGreen : g.open ? Amber : Red;

        /// <summary>Exit sign: green with the runner (open), yellow with a navy runner (opening), red with a cross (shut).</summary>
        void ExitSign(Rect rect, Gate g)
        {
            if (g.Passable || g.open)
            {
                Fill(rect, g.Passable ? ExitGreen : Yellow);
                if (!g.Passable) Frame(rect, Yellow, Ink);
                if (Repaint)
                {
                    var old = GUI.color;
                    GUI.color = g.Passable ? Color.white : Ink;
                    GUI.DrawTexture(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4), Runner);
                    GUI.color = old;
                }
            }
            else
            {
                Fill(rect, Red);
                GUI.Label(rect, "×", crossStyle);
            }
        }

        // ---------------------------------------------------------------- Toolbar and hint

        /// <summary>Tools (and Start while planning) as menu-style slabs at the bottom centre, with a one-line hint above.</summary>
        void DrawToolbar()
        {
            if (phase == Phase.Results) return;
            bool planning = phase == Phase.Planning;
            var items = new List<(string number, string label, string extra, int kind, System.Action action)>
            {
                ("1", "COMMAND", null, tool == Tool.Command ? 1 : 0, () => SetTool(Tool.Command)),
                ("2", "BARRIERS", (barrierBudget - barriersUsed).ToString(), tool == Tool.Barrier ? 1 : 0, () => SetTool(Tool.Barrier)),
            };
            if (planning) items.Add(("ENTER", "OPEN THE VENUE", null, 2, () => deferred.Add(StartShow)));

            const float gap = 8;
            var widths = new float[items.Count];
            var numberWidths = new float[items.Count];
            float total = -gap;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                numberWidths[i] = Mathf.Max(12, TextWidth(it.number, toolNumberStyle));
                widths[i] = 12 + numberWidths[i] + 10 + 2 + 10 + TextWidth(it.label, toolLabelStyle) + (it.extra != null ? 8 + TextWidth(it.extra, toolLabelStyle) : 0) + 30;
                total += widths[i] + gap;
            }
            float x = vw * 0.5f - total * 0.5f, y = vh - 14 - ToolbarHeight;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var rect = new Rect(x, y, widths[i], ToolbarHeight);
                guiRects.Add(rect);
                Color fill = it.kind == 1 ? Red : it.kind == 2 ? Ink : Paper;
                Color ink = it.kind == 1 ? Color.white : it.kind == 2 ? Yellow : Ink;
                if (it.kind != 0) DropShadow(new Rect(rect.x + 6, rect.y + 6, rect.width - 24, rect.height - 12));
                DrawSlab(rect, Hovered(rect) ? HoverShade(fill) : fill, 16);
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) it.action();
                float tx = rect.x + 12;
                Label(new Rect(tx, rect.y, numberWidths[i] + 4, rect.height), it.number, toolNumberStyle, ink);
                tx += numberWidths[i] + 10;
                Fill(new Rect(tx, rect.y + 12, 2, rect.height - 24), it.kind == 0 ? Ink : new Color(ink.r, ink.g, ink.b, 0.7f));
                tx += 12;
                float labelWidth = TextWidth(it.label, toolLabelStyle);
                Label(new Rect(tx, rect.y, labelWidth + 4, rect.height), it.label, toolLabelStyle, ink);
                if (it.extra != null) Label(new Rect(tx + labelWidth + 8, rect.y, 60, rect.height), it.extra, toolLabelStyle, it.kind == 1 ? Yellow : Red);
                x += widths[i] + gap;
            }

            if (!hudHidden)
            {
                string hint = ToolHint();
                float w = TextWidth(hint, hintStyle) + 24;
                var hintRect = new Rect(vw * 0.5f - w * 0.5f, y - 30, w, 22);
                Frame(hintRect, PaperLight, Ink);
                GUI.Label(hintRect, hint, hintStyle);
            }
        }

        string ToolHint()
        {
            if (tool == Tool.Barrier) return "Drag on the map to draw a barrier line. Right-click a barrier to pick it up.";
            if (selectedUnit >= 0 && selectedUnit < units.Count)
            {
                var u = units[selectedUnit];
                return u.role == Role.Patrolman
                    ? "Click the map to move this patrol, or a drunk fan to walk them out. Right-click to turn it. Esc deselects."
                    : $"Click the map to move {u.Name}, or a drunk fan to walk them out. Esc deselects.";
            }
            return "Click a team member, then the map to send them. Click a gate to open or close it.";
        }

        // ---------------------------------------------------------------- 03 — Evacuate zone

        /// <summary>
        /// "03 — EVACUATE ZONE" panel for the selected patrol, just above the toolbar: Front/Back/Left/Right relative to
        /// where they face (arrow keys), each showing the door it would pick, and Escort in the middle. Plain GUI, and the
        /// unit is fixed at the Layout event.
        /// </summary>
        void DrawZoneOrderPanel()
        {
            if (phase == Phase.Results || orderPanelUnit < 0 || orderPanelUnit >= units.Count) return;
            var u = units[orderPanelUnit];
            if (u.role != Role.Patrolman || u.homeRegion < 0) return;
            var region = venue.regions[u.homeRegion];

            const float width = 372, height = 232, bh = 50, gap = 6;
            var rect = Panel(new Rect(vw * 0.5f - width * 0.5f, vh - 14 - ToolbarHeight - 36 - height, width, height));
            Tab(rect.x, rect.y, $"{u.homeRegion + 1:00} — EVACUATE {region.name.ToUpperInvariant()}", Yellow, Ink, Ink);
            Label(new Rect(rect.x, rect.y, rect.width - 12, 24), $"{sim.CountInRegion(u.homeRegion, venue)} PEOPLE", headerRightStyle, Ink);
            float cooldown = regionOrderCooldown.Length > u.homeRegion ? regionOrderCooldown[u.homeRegion] : 0f;
            string status = !incidentStarted ? "Orders unlock once a disaster starts."
                : cooldown > 0f ? $"Order given. Next one in {Mathf.CeilToInt(cooldown)} s."
                : "Send the whole zone out: arrow keys or the buttons. Right-click the map to turn the patrol.";
            Label(new Rect(rect.x + 12, rect.y + 28, rect.width - 24, 18), status, smallStyle, InkSoft);

            bool ready = CanOrderZone(u);
            float cw = (width - 24 - 2 * gap) / 3f, top = rect.y + 52, left = rect.x + 12;
            OrderButton(u, OrderSide.Front, new Rect(left + cw + gap, top, cw, bh), ready);
            OrderButton(u, OrderSide.Left, new Rect(left, top + bh + gap, cw, bh), ready);
            OrderButton(u, OrderSide.Right, new Rect(left + 2 * (cw + gap), top + bh + gap, cw, bh), ready);
            OrderButton(u, OrderSide.Back, new Rect(left + cw + gap, top + 2 * (bh + gap), cw, bh), ready);

            // Escorting drunk people out: they ignore orders, so someone has to walk them to a door.
            var escort = new Rect(left + cw + gap, top + bh + gap, cw, bh);
            int inZone = sim.UnescortedDrunksInRegion(u.homeRegion, venue);
            bool busy = u.escortPhase == EscortPhase.Escorting;
            string title = busy ? "ESCORTING" : u.escortPhase == EscortPhase.Fetching ? "FETCHING" : "ESCORT [F]";
            string sub = busy || u.escortPhase == EscortPhase.Fetching ? "a drunk fan" : sim.DrunkInsideCount == 0 ? "no drunks left" : $"{inZone} here, {sim.DrunkInsideCount} in all";
            bool enabled = !busy && sim.DrunkInsideCount > 0;
            Fill(escort, enabled ? Ink : Color.Lerp(Ink, Paper, 0.5f));
            int unitIndex = orderPanelUnit;
            bool wasEnabled = GUI.enabled;
            GUI.enabled = enabled;
            if (GUI.Button(escort, GUIContent.none, hoverStyle)) deferred.Add(() => EscortNearestDrunk(unitIndex));
            GUI.enabled = wasEnabled;
            Label(new Rect(escort.x, escort.y + 4, escort.width, 24), title, orderTitleStyle, Yellow);
            Label(new Rect(escort.x, escort.y + 26, escort.width, 18), sub, orderSubStyle, Paper);
        }

        void OrderButton(Unit u, OrderSide side, Rect rect, bool ready)
        {
            int door = sim.PickOrderDoor(u.homeRegion, SideDirection(u, side), venue);
            string arrow = side switch { OrderSide.Front => "↑", OrderSide.Back => "↓", OrderSide.Left => "←", _ => "→" };
            string target = door >= 0 ? venue.doors[door].name : "no open exit";
            var old = GUI.color;
            if (!ready) GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Frame(rect, PaperLight, Ink);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = ready;
            if (GUI.Button(rect, GUIContent.none, hoverStyle)) deferred.Add(() => OrderZone(u, side));
            GUI.enabled = wasEnabled;
            string word = side.ToString().ToUpperInvariant();
            float wordWidth = TextWidth(word, orderTitleStyle), arrowWidth = 14;
            float start = rect.center.x - (wordWidth + arrowWidth + 4) * 0.5f;
            GUI.Label(new Rect(start, rect.y + 4, arrowWidth, 24), arrow, arrowStyle);
            GUI.Label(new Rect(start + arrowWidth + 4, rect.y + 4, wordWidth + 4, 24), word, orderTitleStyle);
            GUI.Label(new Rect(rect.x, rect.y + 26, rect.width, 18), target, orderSubStyle);
            GUI.color = old;
        }

        // ---------------------------------------------------------------- Incident log

        /// <summary>The last few events, bottom left, as a log sheet. Lines fade 20 s (game time) after they were logged,
        /// so the log is gone when nothing is happening.</summary>
        void DrawEventFeed()
        {
            const int maxLines = 4;
            const float lineHeight = 24, visibleFor = 20f, fadeTime = 3f;
            int first = log.Count;
            while (first > 0 && log.Count - first < maxLines && feedClock - log[first - 1].at < visibleFor) first--;
            int lines = log.Count - first;
            if (lines == 0) return;

            float width = Mathf.Clamp(vw * 0.5f - 260f, 300f, 450f);
            var rect = Panel(new Rect(14, vh - 14 - (22 + lines * lineHeight + 6), width, 22 + lines * lineHeight + 6));
            Tab(rect.x, rect.y, "INCIDENT LOG", Ink, Paper, Yellow, 22);
            var oldColor = GUI.color;
            for (int i = 0; i < lines; i++)
            {
                var (text, severity, time, at) = log[first + i];
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((visibleFor - (feedClock - at)) / fadeTime) * (i == lines - 1 ? 1f : 0.55f + 0.15f * i));
                var row = new Rect(rect.x + 10, rect.y + 22 + i * lineHeight, rect.width - 20, lineHeight);
                Fill(new Rect(row.x, row.y + 8, 8, 8), SeverityColor(severity));
                float tx = row.x + 16;
                if (time.Length > 0)
                {
                    GUI.Label(new Rect(tx, row.y, 40, lineHeight), time, feedTimeStyle);
                    tx += 36;
                    Fill(new Rect(tx, row.y + 6, 2, lineHeight - 12), Ink);
                    tx += 10;
                }
                Label(new Rect(tx, row.y, row.xMax - tx, lineHeight), text, feedStyle, severity == Severity.Danger ? Red : Ink);
                if (i < lines - 1) Fill(new Rect(row.x, row.yMax - 1, row.width, 1), new Color(Ink.r, Ink.g, Ink.b, 0.2f));
            }
            GUI.color = oldColor;
        }

        // ---------------------------------------------------------------- View toggles and legend

        /// <summary>View toggles stacked bottom right as boxed "key | label" tags (the bottom one hides or shows the panels).</summary>
        void DrawViewToggles()
        {
            const float width = 132, height = 28, gap = 5;
            float x = vw - 14 - width, y = vh - 14 - height;
            if (ToggleButton(new Rect(x, y, width, height), "U", hudHidden ? "SHOW HUD" : "HIDE HUD", hudHidden))
                deferred.Add(() => hudHidden = !hudHidden);
            viewTogglesTop = y;
            if (hudHidden) return;
            y -= height + gap;
            if (ToggleButton(new Rect(x, y, width, height), "L", "LEGEND", showLegend)) deferred.Add(() => showLegend = !showLegend);
            y -= height + gap;
            if (ToggleButton(new Rect(x, y, width, height), "G", "ZONES", showRegions)) showRegions = !showRegions;
            if (crowdRenderer.UsesModels)
            {
                y -= height + gap;
                if (ToggleButton(new Rect(x, y, width, height), "M", "STATE TILES", stateMarkers)) stateMarkers = !stateMarkers;
            }
            y -= height + gap;
            if (ToggleButton(new Rect(x, y, width, height), "H", "PRESSURE", heatmap)) heatmap = !heatmap;
            viewTogglesTop = y;
        }

        bool ToggleButton(Rect rect, string key, string label, bool on)
        {
            guiRects.Add(rect);
            DropShadow(new Rect(rect.x + 4, rect.y + 4, rect.width - 8, rect.height - 8));
            BoxedTag(rect, key, label, on);
            return GUI.Button(rect, GUIContent.none, hoverStyle);
        }

        /// <summary>Colour key, opened with L or the Legend toggle; sits right above the view toggles.</summary>
        void DrawLegend()
        {
            if (!showLegend) return;
            const float width = 300, height = 316;
            var rect = Panel(new Rect(vw - 14 - width, viewTogglesTop - 10 - height, width, height));
            Tab(rect.x, rect.y, "KEY", Yellow, Ink, Ink);
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 32, rect.width - 24, rect.height - 40));
            LegendRow(AgentColors[0], "Calm: watching the show");
            LegendRow(AgentColors[1], "Knows which exits are open");
            LegendRow(AgentColors[2], "Alarmed");
            LegendRow(AgentColors[3], "Panicking: runs and pushes");
            LegendRow(AgentColors[5], "Panic attack: frozen for a while");
            LegendRow(AgentColors[6], "Choking: slowed, may collapse");
            LegendRow(AgentColors[7], "Drunk: needs an escort out");
            LegendRow(AgentColors[4], "Injured");
            LegendRow(PatrolColor, "Zone patrol: calms, then sends out");
            LegendRow(MegaphoneColor, "Police with megaphone");
            GUILayout.Space(6);
            GUILayout.Label("Ground: green = room to move, orange = packed, red/purple = crush danger, dark = smoke.", mutedStyle);
            GUILayout.EndArea();
        }

        void LegendRow(Color color, string text)
        {
            GUILayout.BeginHorizontal();
            var swatch = GUILayoutUtility.GetRect(14, 20, GUILayout.Width(14));
            Swatch(new Rect(swatch.x, swatch.y + 4, 12, 12), color);
            GUILayout.Space(4);
            GUILayout.Label(text, legendStyle);
            GUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------- Banner, map labels, tooltip

        void DrawBanner()
        {
            if (bannerTime <= 0f) return;
            bannerTime -= Repaint ? Time.unscaledDeltaTime : 0f;
            float alpha = Mathf.Clamp01(bannerTime);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            Color fill = bannerSeverity switch { Severity.Danger => Red, Severity.Warning => Yellow, Severity.Good => ExitGreen, _ => Ink };
            Color ink = bannerSeverity == Severity.Warning ? Ink : Color.white;
            var rect = new Rect(vw * 0.5f - 290, TopBarHeight + 16, 580, 74);
            DropShadow(rect);
            DrawSlab(rect, fill, 22);
            DrawStripes(new Rect(rect.x + 14, rect.y + 14, 28, rect.height - 28), new Color(ink.r, ink.g, ink.b, 0.45f));
            Label(new Rect(rect.x + 56, rect.y + 6, rect.width - 90, 38), bannerTitle, bannerTitleStyle, ink);
            Label(new Rect(rect.x + 56, rect.y + 42, rect.width - 90, 22), bannerText, bannerTextStyle, ink);
            GUI.color = old;
        }

        bool ScreenPoint(Vector3 world, out Vector2 p)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            p = new Vector2(sp.x, Screen.height - sp.y) / uiScale;
            return sp.z >= 0f;
        }

        /// <summary>A boxed "key | label" tag centred on a point, with an optional colour bar under it.</summary>
        void WorldTag(Vector2 center, string key, string label, Color? bar, bool redKey = false)
        {
            const float keyWidth = 26, height = 24;
            float width = keyWidth + 18 + TextWidth(label, worldTagStyle);
            var rect = new Rect(center.x - width * 0.5f, center.y - height * 0.5f, width, height);
            Frame(rect, PaperLight, Ink);
            if (redKey) Fill(new Rect(rect.x + 2, rect.y + 2, keyWidth - 2, height - 4), Red);
            Fill(new Rect(rect.x + keyWidth, rect.y + 2, 2, height - 4), Ink);
            Label(new Rect(rect.x, rect.y, keyWidth, height), key, worldKeyStyle, redKey ? Color.white : Ink);
            GUI.Label(new Rect(rect.x + keyWidth + 9, rect.y, width - keyWidth - 9, height), label, worldTagStyle);
            if (bar is Color c) Fill(new Rect(rect.x, rect.yMax, rect.width, 3), c);
        }

        void DrawWorldLabels()
        {
            if (showRegions)
                for (int i = 0; i < venue.regions.Count; i++)
                {
                    var r = venue.regions[i];
                    if (ScreenPoint(new Vector3(r.post.x + 0.5f, 0.1f, r.post.y + 0.5f), out var p))
                        WorldTag(p + new Vector2(0, 26), $"{i + 1:00}", r.name.ToUpperInvariant(), r.color);
                }
            foreach (var (text, position) in labels)
                if (ScreenPoint(position, out var p))
                    ShadowLabel(new Rect(p.x - 90f, p.y - 14f, 180f, 28f), text, landmarkStyle);
            foreach (var door in venue.doors)
            {
                if (door.gate == null && !door.blocked) continue; // entrances already have their landmark label
                var position = new Vector3(door.Center.x, door.blocked ? 3.4f : 2f, door.Center.y);
                if (!ScreenPoint(position, out var p)) continue;
                if (door.blocked)
                {
                    WorldTag(p, "!", $"{door.name.ToUpperInvariant()} BLOCKED: {door.blockReason.ToUpperInvariant()}", null, true);
                    if (door.clearProgress > 0f) ProgressBar(new Rect(p.x - 45f, p.y + 16f, 90f, 9f), door.clearProgress, ExitGreen);
                    continue;
                }
                // Gate: an exit sign with its state, and the name under it.
                var g = door.gate;
                string word = g.Passable ? "EXIT" : GateWord(g);
                float w = 26 + 5 + TextWidth(word, signStyle);
                var sign = new Rect(p.x - w * 0.5f, p.y - 13, 26, 26);
                Fill(new Rect(sign.x - 2, sign.y - 2, sign.width + 4, sign.height + 4), Paper);
                ExitSign(sign, g);
                var oldColor = signStyle.normal.textColor;
                SetTextColor(signStyle, GateInk(g));
                HaloLabel(new Rect(sign.xMax + 5, sign.y - 4, w, 34), word, signStyle, Paper);
                SetTextColor(signStyle, oldColor);
                HaloLabel(new Rect(p.x - 80f, sign.yMax + 2, 160f, 16f), door.name.ToUpperInvariant(), signNameStyle, Paper);
            }
        }

        void DrawHoverInfo()
        {
            var mouse = Mouse.current;
            if (mouse == null || phase == Phase.Results || phase == Phase.Menu) return;
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
                    : selectedUnit >= 0 ? $"<b>Drunk – needs escort</b>\nClick to have {units[selectedUnit].Name} walk them out."
                    : "<b>Drunk – needs escort</b>\nSelect a patrol or police officer, then click them.";
            }
            else if (venue.DoorNear(cell) is Door blockedDoor && blockedDoor.blocked)
            {
                text = $"<b>{blockedDoor.name}</b>\nBlocked: {blockedDoor.blockReason}. Send a patrol to clear it ({FestivalVenue.DoorClearTime:0} s).";
                if (blockedDoor.clearProgress > 0f) text += $"\nCleared {blockedDoor.clearProgress * 100f:0}%. A second person works twice as fast.";
                if (selectedUnit >= 0) text += $"\nClick to send {units[selectedUnit].Name}.";
                else if (blockedDoor.gate == null) text += "\nClick to send the nearest patrol.";
            }
            else if (gate != null) text = $"<b>{gate.name}</b>: {GateStatus(gate).ToLowerInvariant()}. Click to {(gate.open ? "close" : "open")} it.";
            else
            {
                text = venue.cells[cell] switch
                {
                    CellType.Open => $"{sim.density[cell]:0.0} people per m²   pressure {sim.pressure[cell]:0.0}" +
                                     (sim.pressure[cell] > crowd.crushThreshold * 0.8f ? $"  <color=#{Hex(Red)}><b>CRUSH RISK</b></color>" : "") +
                                     (venue.smoke[cell] > 0.1f ? $"\nSmoke {venue.smoke[cell] * 100f:0}%" + (venue.smoke[cell] > crowd.smokeHarmLevel ? $" <color=#{Hex(Red)}><b>TOXIC</b></color>" : "") : "") +
                                     (gasVisible && venue.gas[cell] > GasLeakIncident.DetectLevel ? $"\nGas {venue.gas[cell] * 100f:0}%" + (venue.gas[cell] > crowd.gasHarmLevel ? $" <color=#{Hex(Red)}><b>POISONOUS</b></color>" : "") : ""),
                    CellType.Fire => "Fire",
                    CellType.Barrier => "Barrier (right-click with the Barrier tool to remove)",
                    CellType.Outside => "Outside: safe",
                    _ => venue.DescribeLocation(cell),
                };
                var region = venue.RegionAt(cell);
                if (region != null) text += $"\n<color=#{Hex(Color.Lerp(region.color, Ink, 0.4f))}><b>Zone: {region.name}</b></color>";
                if (selectedUnit >= 0 && tool == Tool.Command && venue.cells[cell] == CellType.Open)
                    text += $"\nClick to send {units[selectedUnit].Name} here.";
            }

            var content = new GUIContent(text);
            float width = Mathf.Min(320f, tooltipStyle.CalcSize(content).x + 4f);
            float height = tooltipStyle.CalcHeight(content, width);
            var p = new Vector2(screen.x, Screen.height - screen.y) / uiScale;
            var rect = new Rect(Mathf.Min(p.x + 18, vw - width - 8), Mathf.Min(p.y + 14, vh - height - 8), width, height);
            DropShadow(rect);
            GUI.Box(rect, content, tooltipStyle);
        }

        // ---------------------------------------------------------------- Briefing and results cards

        void DrawWelcome()
        {
            var rect = Panel(new Rect(vw * 0.5f - 300, vh * 0.5f - 250, 600, 500));
            Tab(rect.x, rect.y, "00 — BRIEFING", Yellow, Ink, Ink);
            GUILayout.BeginArea(new Rect(rect.x + 28, rect.y + 38, rect.width - 56, rect.height - 56));
            GUILayout.Label("WELCOME, SAFETY COMMANDER", titleStyle);
            GUILayout.Space(2);
            GUILayout.Label($"{sim.count} people are at {MapTitle}. Soon something will go wrong: a fire, a falling light tower, a gas leak, a blocked exit... " +
                            "Every round is different. People head for the nearest gate; if it's shut, they try another, and the queues grow.", labelStyle);
            GUILayout.Space(4);
            GUILayout.Label($"Difficulty: <b>{DifficultyLabel}</b>. Esc (with nobody selected) goes back to the menu.", mutedStyle);
            GUILayout.Space(10);
            WelcomeStep("1", "PLAN", $"Every zone has a patrol ({venue.regions.Count} in all), plus {megaphoneOfficers} megaphone police. Move them by clicking them, then the map. Draw barriers to steer the crowd.");
            WelcomeStep("2", "REACT", "When disaster hits, open the emergency exits and send your team to the crowds. Select a patrol and press an arrow key to send its whole zone out that way. The megaphone police tell everyone in earshot where to go.");
            WelcomeStep("3", "KEEP THEM CALM", "Panicked crowds push and crush, and too much panic causes panic attacks. Put patrols where queues form: they line people up with room to breathe.");
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Camera: WASD, scroll to zoom, Q/E to rotate. U hides the HUD.", smallStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("LET'S GO", goodButtonStyle, GUILayout.Width(170), GUILayout.Height(46))) deferred.Add(() => welcomeDismissed = true);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void WelcomeStep(string number, string title, string text)
        {
            GUILayout.BeginHorizontal();
            var badge = GUILayoutUtility.GetRect(32, 32, GUILayout.Width(32), GUILayout.Height(32));
            Fill(badge, Red);
            GUI.Label(badge, number, numberStyle);
            GUILayout.Space(12);
            GUILayout.BeginVertical();
            GUILayout.Label(title, stepTitleStyle);
            GUILayout.Label(text, mutedStyle);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
        }

        void DrawResults()
        {
            var rect = Panel(new Rect(vw * 0.5f - 260, vh * 0.5f - 256, 520, 512));
            Tab(rect.x, rect.y, "RESULTS — DEBRIEF", Yellow, Ink, Ink);
            GUILayout.BeginArea(new Rect(rect.x + 28, rect.y + 36, rect.width - 56, rect.height - 54));
            GUILayout.Label(resultTitle.ToUpperInvariant(), bigTitleStyle);
            GUILayout.Space(4);

            // Earned stars are saved separately for each map and difficulty.
            var starsRow = GUILayoutUtility.GetRect(10, 34, GUILayout.ExpandWidth(true));
            DrawMenuStars(new Rect(starsRow.center.x - 57, starsRow.y, 114, 30), stars, 32);
            GUILayout.Label($"{MapTitle.ToUpperInvariant()}  |  {stars}/3 STARS  |  BEST {MapProgress.GetStars(selectedMap, difficultySettings.difficulty)}/3", starsCaptionStyle);
            GUILayout.Space(8);

            ResultRow("Safe", $"{finalSafe} / {sim.count}  ({finalSafe * 100f / sim.count:0}%)", ExitGreen);
            ResultRow("Injured", finalInjured == 0 ? "0" : $"{finalInjured}  ({sim.InjuredByCrush} crushed, {sim.InjuredByFire} burnt, {sim.InjuredBySmoke} smoke, {sim.InjuredByGas} gas)", finalInjured > 0 ? Red : Ink);
            if (sim.InjuredByCollapse > 0) ResultRow("Crushed by collapse", sim.InjuredByCollapse.ToString(), Red);
            ResultRow("Panic attacks", finalAttacks.ToString(), finalAttacks > 0 ? PanicInk : Ink);
            ResultRow("Still inside", finalTrapped.ToString(), finalTrapped > 0 ? WarningColor : Ink);
            ResultRow("Drunk left inside", finalDrunkInside == 0 ? "0" : $"{finalDrunkInside}  (-{finalDrunkInside * 50} points)", finalDrunkInside > 0 ? PanicInk : Ink);
            ResultRow("Evacuation time", FormatTime(evacuationTime), Ink);
            ResultRow("Difficulty", DifficultyLabel, Ink);
            ResultRow("Score", finalScore.ToString(), Red);
            GUILayout.Space(8);
            GUILayout.Label(resultTip, mutedStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("PLAY AGAIN  [R]", goodButtonStyle, GUILayout.Height(46))) deferred.Add(NewGame);
            if (GUILayout.Button("MAIN MENU", buttonStyle, GUILayout.Height(34)))
                deferred.Add(() =>
                {
                    NewGame();
                    OpenMenu();
                });
            GUILayout.EndArea();
        }

        void ResultRow(string caption, string value, Color color)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(caption, labelStyle, GUILayout.Width(170));
            GUILayout.FlexibleSpace();
            SetTextColor(resultValueStyle, color);
            GUILayout.Label(value, resultValueStyle);
            GUILayout.EndHorizontal();
            Fill(GUILayoutUtility.GetRect(10, 1, GUILayout.ExpandWidth(true)), new Color(Ink.r, Ink.g, Ink.b, 0.15f));
        }
    }
}
