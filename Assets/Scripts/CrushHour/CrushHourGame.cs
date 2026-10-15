using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>
    /// Crush Hour prototype: top-down crowd safety at a summer music festival.
    /// Plan with barriers and stewards, start the show, then get everyone out when the incident hits,
    /// using emergency gates, stewards and PA announcements. Scored on people saved, injuries and time.
    /// </summary>
    public class CrushHourGame : MonoBehaviour
    {
        enum Phase { Planning, Live, Results }
        enum Tool { Command, Barrier }

        class Steward
        {
            public Vector2 position;
            public readonly List<Vector2> path = new();
            public Transform root;
            public LineRenderer ring;
        }

        [Header("Materials (fallbacks are created when empty)")]
        public Material litMaterial;
        public Material unlitMaterial;

        [Header("Rules")]
        public int barrierBudget = 60;
        public int stewardCount = 6;
        public float evacuationTimeLimit = 240f;
        public float announcementCooldown = 12f;
        [Range(0f, 1f)] public float announcementReach = 0.7f;
        public int maxFireCells = 160;
        public float stewardSpeed = 3.5f;

        public CrowdSettings crowd = new();

        const int W = FestivalVenue.Width, H = FestivalVenue.Height;
        const float SimStep = 0.04f;
        const int MaxStepsPerFrame = 6;
        static readonly float[] Speeds = { 1f, 2f, 4f };
        static readonly Vector2[] StewardPosts = { new(52, 7), new(7, 24), new(30, 58), new(74, 58), new(21, 14), new(78, 38) };

        static readonly Color[] AgentColors =
        {
            new(0.3f, 0.55f, 0.95f),  // calm / unaware
            new(0.15f, 0.85f, 0.75f), // following directions
            new(1f, 0.6f, 0.15f),     // alarmed
            new(0.95f, 0.1f, 0.1f),   // panicking
            new(0.3f, 0.05f, 0.3f),   // injured
        };
        static readonly Color StewardColor = new(1f, 0.95f, 0.2f);
        static readonly Color FenceColor = new(0.25f, 0.25f, 0.27f);
        static readonly Color BarrierColor = new(0.9f, 0.9f, 0.85f);
        static readonly Color GateClosedColor = new(0.85f, 0.15f, 0.15f);
        static readonly Color GateOpeningColor = new(1f, 0.6f, 0.1f);
        static readonly Color GateOpenColor = new(0.2f, 0.8f, 0.3f);
        static readonly Color FireColor = new(1f, 0.35f, 0f);
        static readonly Color Grass = new(0.3f, 0.48f, 0.24f);
        static readonly Color Asphalt = new(0.28f, 0.28f, 0.3f);
        static readonly Color Scorched = new(0.12f, 0.08f, 0.05f);
        static readonly Color Blocked = new(0.18f, 0.18f, 0.18f);

        FestivalVenue venue;
        CrowdSimulation sim;
        FlowField knownField, allField;
        System.Random rng;
        int seed;

        Phase phase;
        Tool tool;
        bool paused, heatmap = true, fieldsDirty, incidentStarted;
        int speedIndex, barriersUsed, lastInjured, gatesOpened, selectedSteward = -1;
        float simAccumulator, fieldTimer, groundTimer, warningTimer;
        float incidentCountdown, evacuationTime, announcementTimer, calmStrength, lastProgressTime;
        int lastSafe;
        string incidentSite;

        // Results
        int finalSafe, finalInjured, finalTrapped, finalScore, stars;
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
        readonly List<Steward> stewards = new();
        readonly List<Vector2> stewardPositions = new();
        readonly List<Gate> changedGates = new();
        readonly List<(string text, Vector3 position)> labels = new();
        readonly List<string> log = new();
        readonly Dictionary<string, float> lastWarning = new();
        readonly List<Rect> guiRects = new();
        readonly List<Action> deferred = new();
        GUIStyle labelStyle, smallStyle, headerStyle, titleStyle, rightStyle, worldLabelStyle;

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

            NewGame();
        }

        // ================================================================ Setup

        void NewGame()
        {
            if (world != null) Destroy(world.gameObject);
            if (groundTexture != null) Destroy(groundTexture);
            if (groundMaterial != null) Destroy(groundMaterial);
            world = new GameObject("Festival (generated)").transform;
            barrierViews.Clear();
            fireViews.Clear();
            gateViews.Clear();
            stewards.Clear();
            labels.Clear();
            log.Clear();
            lastWarning.Clear();

            seed = Environment.TickCount & int.MaxValue;
            rng = new System.Random(seed);
            venue = new FestivalVenue();
            sim = new CrowdSimulation(crowd, venue, rng);
            knownField = new FlowField(FestivalVenue.CellCount);
            allField = new FlowField(FestivalVenue.CellCount);

            phase = Phase.Planning;
            tool = Tool.Command;
            paused = incidentStarted = dragging = false;
            selectedSteward = -1;
            barriersUsed = lastInjured = gatesOpened = 0;
            simAccumulator = evacuationTime = announcementTimer = warningTimer = lastProgressTime = 0f;
            lastSafe = 0;
            calmStrength = 0.35f;
            incidentSite = null;
            dragLine.enabled = false;

            BuildVenueViews();
            SpawnStewards();
            ComputeFields();
            cameraRig.Focus(new Vector3(W * 0.5f, 0f, H * 0.45f), 85f);

            Log($"Welcome, safety commander. {sim.count} fans are at the Summer Music Festival.");
            Log("People only know the entrances they came in by. The red emergency gates are closed.");
            Log("Plan: draw barriers [2], move stewards [1]. Then START THE SHOW - something will go wrong.");
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

        void SpawnStewards()
        {
            for (int i = 0; i < stewardCount; i++)
            {
                Vector2 post = i < StewardPosts.Length ? StewardPosts[i] : new Vector2(rng.Next(10, 94) + 0.5f, rng.Next(10, 60) + 0.5f);
                if (!venue.IsWalkable(FestivalVenue.CellAt(post))) post = StewardPosts[0];

                var root = new GameObject($"Steward {i + 1}").transform;
                root.SetParent(world, false);
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(root, false);
                body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                body.transform.localScale = new Vector3(0.6f, 1.8f, 0.6f);
                body.GetComponent<MeshRenderer>().sharedMaterial = Lit(StewardColor);

                // Influence radius.
                var ring = root.gameObject.AddComponent<LineRenderer>();
                ring.useWorldSpace = false;
                ring.loop = true;
                ring.positionCount = 48;
                ring.widthMultiplier = 0.12f;
                ring.sharedMaterial = Unlit(StewardColor);
                ring.shadowCastingMode = ShadowCastingMode.Off;
                for (int k = 0; k < ring.positionCount; k++)
                {
                    float a = k * Mathf.PI * 2f / ring.positionCount;
                    ring.SetPosition(k, new Vector3(Mathf.Cos(a) * crowd.stewardRadius, 0.08f, Mathf.Sin(a) * crowd.stewardRadius));
                }

                root.position = new Vector3(post.x, 0f, post.y);
                stewards.Add(new Steward { position = post, root = root, ring = ring });
            }
        }

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

            if (phase != Phase.Results && !paused)
            {
                simAccumulator += Time.deltaTime * Speeds[speedIndex];
                int steps = 0;
                while (simAccumulator >= SimStep && steps < MaxStepsPerFrame && phase != Phase.Results)
                {
                    Simulate(SimStep);
                    simAccumulator -= SimStep;
                    steps++;
                }
                if (steps == MaxStepsPerFrame) simAccumulator = 0f; // can't keep up: run slower rather than spiral
            }

            UpdateViews();
            sim.Render(cubeMesh, agentRenderParams);
        }

        void Simulate(float dt)
        {
            if (venue.UpdateGates(dt, changedGates))
            {
                foreach (var g in changedGates)
                {
                    if (!g.Passable)
                        foreach (var p in g.rect.allPositionsWithin) sim.Evict(FestivalVenue.Index(p.x, p.y), venue);
                    Log(g.Passable ? $"{g.name} is open." : $"{g.name} is closed.");
                }
                fieldsDirty = true;
            }

            if (phase == Phase.Live && !incidentStarted)
            {
                incidentCountdown -= dt;
                if (incidentCountdown <= 0f) StartIncident();
            }
            else if (incidentStarted && venue.UpdateFire(dt, rng, maxFireCells))
            {
                SyncFireViews();
                fieldsDirty = true;
            }

            MoveStewards(dt);
            sim.Step(dt, venue, knownField, allField, incidentStarted);
            sim.ApplyStewards(stewardPositions, dt, incidentStarted);

            fieldTimer -= dt;
            if (fieldsDirty || fieldTimer <= 0f) ComputeFields();

            if (!incidentStarted) return;
            evacuationTime += dt;
            announcementTimer = Mathf.Max(0f, announcementTimer - dt);
            CheckWarnings(dt);
            if (sim.SafeCount != lastSafe)
            {
                lastSafe = sim.SafeCount;
                lastProgressTime = evacuationTime;
            }
            // Everyone left is trying to get out but nobody has made it for a while: they're trapped.
            bool stalled = sim.EvacuatingCount == sim.ActiveCount && evacuationTime - lastProgressTime > 20f;
            if (sim.ActiveCount == 0 || stalled || evacuationTime >= evacuationTimeLimit) EndGame();
        }

        void ComputeFields()
        {
            // Unaware people: only the entrances they know, and they follow the herd.
            venue.ComputeFlowField(knownField, true, null, 0f);
            // People following directions: every open exit, avoiding congestion.
            venue.ComputeFlowField(allField, false, sim.density, 0.35f);
            fieldsDirty = false;
            fieldTimer = 0.5f;
        }

        void StartShow()
        {
            if (phase != Phase.Planning) return;
            phase = Phase.Live;
            incidentCountdown = 4f + (float)rng.NextDouble() * 8f;
            Log("The headliner is on stage. Stay sharp...");
        }

        void StartIncident()
        {
            var site = venue.fireSites[rng.Next(venue.fireSites.Count)];
            incidentSite = site.name;
            incidentStarted = true;
            evacuationTime = 0f;
            venue.StartFire(site.cell);
            SyncFireViews();
            fieldsDirty = true;
            Log($"FIRE at {site.name}! Get everyone out. Open gates, send stewards, use the PA.");
            cameraRig.Focus(new Vector3(site.cell.x, 0f, site.cell.y));
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

        void MoveStewards(float dt)
        {
            stewardPositions.Clear();
            foreach (var s in stewards)
            {
                float budget = stewardSpeed * dt;
                while (budget > 0f && s.path.Count > 0)
                {
                    Vector2 delta = s.path[0] - s.position;
                    float length = delta.magnitude;
                    if (length <= budget)
                    {
                        s.position = s.path[0];
                        s.path.RemoveAt(0);
                        budget -= length;
                    }
                    else
                    {
                        s.position += delta / length * budget;
                        budget = 0f;
                    }
                }
                stewardPositions.Add(s.position);
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
                    Log($"Dangerous crowd pressure near {where}!");
                }
            }
            if (sim.InjuredCount > lastInjured)
            {
                Log($"{sim.InjuredCount - lastInjured} more people injured (total {sim.InjuredCount}).");
                lastInjured = sim.InjuredCount;
            }
        }

        void EndGame()
        {
            phase = Phase.Results;
            CancelDrag();
            finalSafe = sim.SafeCount;
            finalInjured = sim.InjuredCount;
            finalTrapped = sim.ActiveCount;
            finalScore = finalSafe * 10 - finalInjured * 100 - finalTrapped * 25
                         + Mathf.Max(0, Mathf.RoundToInt(evacuationTimeLimit - evacuationTime)) * 10;

            float safeRatio = finalSafe / (float)sim.count;
            stars = 0;
            if (safeRatio >= 0.9f) stars = 1;
            if (stars == 1 && finalInjured <= sim.count / 100) stars = 2;
            if (stars == 2 && finalInjured == 0 && finalTrapped == 0 && evacuationTime <= evacuationTimeLimit * 0.5f) stars = 3;

            resultTitle = finalTrapped == 0 ? "FESTIVAL EVACUATED" : evacuationTime >= evacuationTimeLimit ? "TIME'S UP" : "EVACUATION STALLED";
            if (gatesOpened == 0)
                resultTip = "No emergency gate was opened. The two entrances alone can't take the whole crowd.";
            else if (finalInjured > 0)
                resultTip = "Crushes happen where panicking crowds hit bottlenecks. Calm them (stewards, PA) and spread them over more exits.";
            else if (finalTrapped > 0)
                resultTip = "Too slow. Announce the evacuation earlier: people far from the fire keep watching the show.";
            else
                resultTip = "Clean evacuation! The crowd and the incident change every run. Try again.";
            Log($"{resultTitle}: {finalSafe} safe, {finalInjured} injured, {finalTrapped} still inside.");
        }

        // ================================================================ Player actions

        void SetTool(Tool newTool)
        {
            tool = newTool;
            CancelDrag();
            if (tool == Tool.Barrier) selectedSteward = -1;
        }

        bool CanAnnounce() => phase == Phase.Live && incidentStarted && announcementTimer <= 0f;

        void AnnounceEvacuation()
        {
            if (!CanAnnounce()) return;
            int reached = sim.AnnounceEvacuation(rng, announcementReach);
            announcementTimer = announcementCooldown;
            Log($"PA: \"Please leave calmly and follow staff directions.\" ({reached} more people now follow directions)");
        }

        void AnnounceCalm()
        {
            if (!CanAnnounce()) return;
            sim.AnnounceCalm(calmStrength);
            Log($"PA: \"Stay calm. Walk, don't run.\" (effect {calmStrength / 0.35f * 100f:0}%: people tune out repeats)");
            calmStrength *= 0.75f;
            announcementTimer = announcementCooldown;
        }

        void CommandClick(Vector2 ground, int cell)
        {
            int nearest = NearestSteward(ground, 1.5f);
            if (nearest >= 0)
            {
                selectedSteward = nearest;
                return;
            }

            var gate = venue.GateNear(cell);
            if (gate != null)
            {
                gate.open = !gate.open;
                if (gate.open) gatesOpened++;
                Log(gate.open ? $"Opening {gate.name}..." : $"Closing {gate.name}...");
                return;
            }

            int index = selectedSteward >= 0 ? selectedSteward : NearestSteward(ground, float.MaxValue);
            if (index < 0) return;
            var path = venue.FindPath(stewards[index].position, ground);
            if (path == null)
            {
                Log("A steward can't get there.");
                return;
            }
            stewards[index].path.Clear();
            stewards[index].path.AddRange(path);
        }

        int NearestSteward(Vector2 point, float maxDistance)
        {
            int best = -1;
            float bestSq = maxDistance * maxDistance;
            for (int i = 0; i < stewards.Count; i++)
            {
                float d = (stewards[i].position - point).sqrMagnitude;
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
            bool placed = false, outOfBudget = false;
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
                placed = true;
            }
            if (placed) fieldsDirty = true;
            if (outOfBudget) Log($"Out of barriers ({barrierBudget} sections). Right-click a barrier to pick it up.");
        }

        void RemoveBarrier(int cell)
        {
            if (!venue.TryRemoveBarrier(cell)) return;
            if (barrierViews.Remove(cell, out var view)) Destroy(view);
            barriersUsed--;
            fieldsDirty = true;
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
            if (keyboard == null) return;
            if (keyboard.rKey.wasPressedThisFrame)
            {
                NewGame();
                return;
            }
            if (keyboard.hKey.wasPressedThisFrame) heatmap = !heatmap;
            if (phase == Phase.Results) return;
            if (keyboard.spaceKey.wasPressedThisFrame) paused = !paused;
            if (keyboard.tabKey.wasPressedThisFrame) speedIndex = (speedIndex + 1) % Speeds.Length;
            if (keyboard.digit1Key.wasPressedThisFrame) SetTool(Tool.Command);
            if (keyboard.digit2Key.wasPressedThisFrame) SetTool(Tool.Barrier);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) StartShow();
            if (keyboard.fKey.wasPressedThisFrame) AnnounceEvacuation();
            if (keyboard.cKey.wasPressedThisFrame) AnnounceCalm();
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
                if (mouse.rightButton.wasPressedThisFrame) selectedSteward = -1;
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

        bool IsOverGui(Vector2 screen)
        {
            var guiPoint = new Vector2(screen.x, Screen.height - screen.y);
            foreach (var r in guiRects)
                if (r.Contains(guiPoint)) return true;
            return false;
        }

        // ================================================================ Views

        void UpdateViews()
        {
            for (int i = 0; i < stewards.Count; i++)
            {
                stewards[i].root.position = new Vector3(stewards[i].position.x, 0f, stewards[i].position.y);
                stewards[i].ring.widthMultiplier = i == selectedSteward ? 0.35f : 0.12f;
            }

            for (int i = 0; i < venue.gates.Count; i++)
            {
                var g = venue.gates[i];
                var t = gateViews[i].transform;
                float h = Mathf.Lerp(1.4f, 0.12f, g.progress);
                t.localPosition = new Vector3(t.localPosition.x, h * 0.5f, t.localPosition.z);
                t.localScale = new Vector3(t.localScale.x, h, t.localScale.z);
                var material = Lit(g.Passable ? GateOpenColor : g.open ? GateOpeningColor : GateClosedColor);
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

        void UpdateGroundTexture()
        {
            for (int i = 0; i < groundPixels.Length; i++)
            {
                groundPixels[i] = venue.cells[i] switch
                {
                    CellType.Outside => Asphalt,
                    CellType.Open => heatmap ? PressureColor(sim.pressure[i]) : Grass,
                    CellType.Fire => Scorched,
                    _ => Blocked,
                };
            }
            groundTexture.SetPixels32(groundPixels);
            groundTexture.Apply(false);
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

        void Log(string message)
        {
            float t = incidentStarted ? evacuationTime : 0f;
            log.Add(incidentStarted ? $"[{FormatTime(t)}] {message}" : message);
            if (log.Count > 40) log.RemoveAt(0);
        }

        static string FormatTime(float seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

        // ================================================================ UI

        void OnGUI()
        {
            if (sim == null) return;
            EnsureStyles();
            guiRects.Clear();
            DrawWorldLabels();
            DrawTopBar();
            DrawSidePanel();
            DrawLog();
            DrawHoverInfo();
            if (phase == Phase.Results) DrawResults();
        }

        void EnsureStyles()
        {
            if (labelStyle != null) return;
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            labelStyle.normal.textColor = Color.white;
            smallStyle = new GUIStyle(labelStyle) { fontSize = 11, wordWrap = true };
            smallStyle.normal.textColor = new Color(0.82f, 0.82f, 0.82f);
            headerStyle = new GUIStyle(labelStyle) { fontSize = 14, fontStyle = FontStyle.Bold };
            titleStyle = new GUIStyle(headerStyle) { fontSize = 22 };
            rightStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.UpperRight };
            worldLabelStyle = new GUIStyle(labelStyle) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }

        void Panel(Rect rect)
        {
            GUI.Box(rect, GUIContent.none);
            GUI.Box(rect, GUIContent.none);
            guiRects.Add(rect);
        }

        void ShadowLabel(Rect rect, string text, GUIStyle style)
        {
            var color = style.normal.textColor;
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
            style.normal.textColor = color;
            GUI.Label(rect, text, style);
        }

        void DrawWorldLabels()
        {
            foreach (var (text, position) in labels) WorldLabel(position, text);
            foreach (var g in venue.gates)
            {
                string status = g.Passable ? "OPEN" : g.open ? "opening..." : "closed";
                WorldLabel(new Vector3(g.rect.center.x, 2f, g.rect.center.y), $"{g.name.ToUpperInvariant()}\n{status}");
            }
        }

        void WorldLabel(Vector3 position, string text)
        {
            Vector3 sp = cam.WorldToScreenPoint(position);
            if (sp.z < 0f) return;
            ShadowLabel(new Rect(sp.x - 80f, Screen.height - sp.y - 16f, 160f, 32f), text, worldLabelStyle);
        }

        void DrawTopBar()
        {
            Panel(new Rect(0, 0, Screen.width, 50));
            GUI.Label(new Rect(12, 5, 420, 22), "CRUSH HOUR  -  Summer Music Festival", headerStyle);
            string phaseText = phase switch
            {
                Phase.Planning => "PLANNING: place barriers and stewards, then start the show.",
                Phase.Live when !incidentStarted => "Show in progress... something is about to go wrong.",
                Phase.Live => $"EVACUATION  {FormatTime(evacuationTime)} / {FormatTime(evacuationTimeLimit)}   Fire at {incidentSite}",
                _ => "RESULTS",
            };
            GUI.Label(new Rect(12, 26, 640, 20), phaseText, labelStyle);
            GUI.Label(new Rect(Screen.width - 700, 5, 688, 20),
                $"Inside {sim.ActiveCount}    Safe {sim.SafeCount}    Injured {sim.InjuredCount}    Panicking {sim.PanickedCount}    Peak pressure {sim.MaxPressure:0.0}",
                rightStyle);
            GUI.Label(new Rect(Screen.width - 700, 26, 688, 20), $"Speed x{Speeds[speedIndex]:0}{(paused ? "   PAUSED" : "")}    Seed {seed}", rightStyle);
        }

        void DrawSidePanel()
        {
            var rect = new Rect(8, 58, 270, 600);
            Panel(rect);
            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 8, rect.width - 20, rect.height - 16));

            if (phase == Phase.Planning)
            {
                if (GUILayout.Button("START THE SHOW  [Enter]", GUILayout.Height(32))) deferred.Add(StartShow);
                GUILayout.Space(6);
            }

            GUILayout.Label("TOOLS", headerStyle);
            if (GUILayout.Toggle(tool == Tool.Command, "[1] Command: stewards & gates", "Button") && tool != Tool.Command) SetTool(Tool.Command);
            if (GUILayout.Toggle(tool == Tool.Barrier, $"[2] Barriers  ({barrierBudget - barriersUsed} left)", "Button") && tool != Tool.Barrier) SetTool(Tool.Barrier);
            GUILayout.Label(tool == Tool.Command
                    ? "Click ground: send the selected (or nearest) steward. Click a steward to select it, right-click to deselect. Click a gate to open/close it."
                    : "Drag to draw a barrier line. Right-click a barrier to pick it up.",
                smallStyle);

            GUILayout.Space(6);
            GUILayout.Label("PA ANNOUNCEMENTS", headerStyle);
            GUI.enabled = CanAnnounce();
            if (GUILayout.Button("[F] \"Evacuate, follow staff\"")) AnnounceEvacuation();
            if (GUILayout.Button("[C] \"Stay calm, walk, don't run\"")) AnnounceCalm();
            GUI.enabled = true;
            GUILayout.Label(!incidentStarted ? "Available once something happens."
                : announcementTimer > 0f ? $"PA busy: ready in {Mathf.CeilToInt(announcementTimer)}s" : "PA ready.", smallStyle);

            GUILayout.Space(6);
            GUILayout.Label("SIMULATION", headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(paused ? "Resume [Space]" : "Pause [Space]")) paused = !paused;
            if (GUILayout.Button($"Speed x{Speeds[speedIndex]:0} [Tab]")) speedIndex = (speedIndex + 1) % Speeds.Length;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            heatmap = GUILayout.Toggle(heatmap, " Pressure map [H]");
            if (GUILayout.Button("Restart [R]")) deferred.Add(NewGame);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("LEGEND", headerStyle);
            LegendRow(AgentColors[0], "Calm: unaware / watching the show");
            LegendRow(AgentColors[1], "Following staff directions");
            LegendRow(AgentColors[2], "Alarmed");
            LegendRow(AgentColors[3], "Panicking: pushes, ignores staff");
            LegendRow(AgentColors[4], "Injured (crushed or burnt)");
            LegendRow(StewardColor, "Steward: calms & directs people in ring");
            LegendRow(GateClosedColor, "Emergency gate (closed)");
            GUILayout.Label("Ground: green = space, orange = dense, red/purple = crush danger. Camera: WASD, scroll, Q/E, middle-drag.", smallStyle);
            GUILayout.EndArea();
        }

        void LegendRow(Color color, string text)
        {
            GUILayout.BeginHorizontal();
            var swatch = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14), GUILayout.Height(14));
            if (Event.current.type == EventType.Repaint)
            {
                var old = GUI.color;
                GUI.color = color;
                GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                GUI.color = old;
            }
            GUILayout.Label(text, smallStyle);
            GUILayout.EndHorizontal();
        }

        void DrawLog()
        {
            int lines = Mathf.Min(6, log.Count);
            if (lines == 0) return;
            var rect = new Rect(8, Screen.height - 20 - lines * 20, 640, lines * 20 + 12);
            Panel(rect);
            for (int i = 0; i < lines; i++)
                GUI.Label(new Rect(rect.x + 10, rect.y + 6 + i * 20, rect.width - 20, 20), log[log.Count - lines + i], labelStyle);
        }

        void DrawHoverInfo()
        {
            var mouse = Mouse.current;
            if (mouse == null || phase == Phase.Results) return;
            Vector2 screen = mouse.position.ReadValue();
            if (IsOverGui(screen) || !TryGetCellUnderMouse(screen, out int cell, out _)) return;

            string text = venue.cells[cell] switch
            {
                CellType.Open => $"{sim.density[cell]:0.0} people/m²   pressure {sim.pressure[cell]:0.0}",
                CellType.Fire => "Fire",
                CellType.Barrier => "Barrier (right-click with Barrier tool to remove)",
                CellType.Outside => "Outside: safe zone",
                _ => venue.DescribeLocation(cell),
            };
            if (dragging)
            {
                int free = 0;
                foreach (int c in LineCells(dragStartCell, cell))
                    if (venue.cells[c] == CellType.Open) free++;
                text = $"{free} barrier sections ({barrierBudget - barriersUsed} left)";
            }
            ShadowLabel(new Rect(screen.x + 18, Screen.height - screen.y + 6, 420, 22), text, labelStyle);
        }

        void DrawResults()
        {
            var rect = new Rect(Screen.width * 0.5f - 230, Screen.height * 0.5f - 170, 460, 340);
            Panel(rect);
            Panel(rect);
            GUILayout.BeginArea(new Rect(rect.x + 20, rect.y + 16, rect.width - 40, rect.height - 32));
            GUILayout.Label(resultTitle, titleStyle);
            GUILayout.Space(8);
            GUILayout.Label($"Safe:  {finalSafe} / {sim.count}  ({finalSafe * 100f / sim.count:0}%)", labelStyle);
            GUILayout.Label($"Injured:  {finalInjured}", labelStyle);
            GUILayout.Label($"Still inside:  {finalTrapped}", labelStyle);
            GUILayout.Label($"Evacuation time:  {FormatTime(evacuationTime)}", labelStyle);
            GUILayout.Space(6);
            GUILayout.Label($"SCORE  {finalScore}      RATING  {new string('*', stars)}{new string('-', 3 - stars)}", headerStyle);
            GUILayout.Space(6);
            GUILayout.Label(resultTip, smallStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Play again: new crowd, new incident  [R]", GUILayout.Height(32))) deferred.Add(NewGame);
            GUILayout.EndArea();
        }
    }
}
