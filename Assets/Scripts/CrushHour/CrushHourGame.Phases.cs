using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using MomentKind = CrushHour.RoundGenerator.MomentKind;

namespace CrushHour
{
    /// <summary>
    /// Week 8, "action packed" evening (control room only; top-down scenes are unchanged). The show runs through
    /// DOORS OPEN → WARM-UP → HEADLINER → ENCORE + FIREWORKS → EXODUS (<see cref="phaseDurations"/>), planned by
    /// <see cref="RoundGenerator.BuildEvening"/>. Each phase has its moments (ticket waves and a gate crush, the bar rush
    /// and a downpour, drops, a mosh pit and a crowd surfer, fireworks and a stray rocket). The round's first disaster
    /// falls in a phase chosen by difficulty; once an evacuation starts the show stops and it's EXODUS. HEAT rises with
    /// every unresolved moment and the crowd pressure; fast fixes build a combo ("ON FIRE" ×2). The debrief grades each
    /// phase A–F.
    /// </summary>
    public partial class CrushHourGame
    {
        [Header("Evening phases (control room)")]
        [Tooltip("Seconds of DOORS OPEN, WARM-UP, HEADLINER and ENCORE + FIREWORKS. EXODUS lasts until the venue is empty or time runs out.")]
        public float[] phaseDurations = { 90f, 90f, 180f, 60f };
        [Tooltip("Share of the crowd still queueing outside the entrances when the doors open (they come in in 4 waves).")]
        [Range(0f, 0.5f)] public float arrivingShare = 0.25f;
        [Tooltip("Beats per minute of the bass pulse.")]
        public float musicBpm = 126f;

        enum MomentState { Pending, Active, Solved, Failed, Over, Cancelled }

        class Moment
        {
            public RoundGenerator.MomentPlan plan;
            public MomentState state;
            public float openedAt, deadline, endAt, preparedAt = -1f;
            public bool prepared, surged;
            public int lastShown = -1;
            public Vector2 point;
            public readonly List<int> people = new();
            public GameObject marker;
            public int phaseIndex;
            public MomentKind Kind => plan.kind;
            /// <summary>Counts for HEAT, score, combo and the grades (the others are just events).</summary>
            public bool Scored => Kind is MomentKind.GateCrush or MomentKind.BarRush or MomentKind.Rain or MomentKind.Drop;
        }

        const float FastFix = 15f, GateCrushTime = 20f, BarRushTime = 30f, BarRushLength = 45f, RainLength = 50f, ShelterTime = 20f,
                    DropSurgeTime = 6f, StageReach = 14f;
        static readonly Color PhaseYellow = new(1f, 0.82f, 0.2f), HeatRed = new(1f, 0.3f, 0.2f), FireOrange = new(1f, 0.55f, 0.1f);
        static readonly Color[] FireworkColors = { new(1f, 0.3f, 0.3f), new(0.3f, 0.8f, 1f), new(1f, 0.85f, 0.2f), new(0.6f, 1f, 0.4f), new(1f, 0.4f, 0.95f), Color.white };

        RoundGenerator.EveningPlan evening;
        readonly List<Moment> moments = new();
        int eveningPhase = -1;
        float showClock, momentClock, heat, chatterTimer, timelineTimer, fireworkTimer, beatClock, beatAccumulator, lightBlend;
        int streak, bestStreak, momentScore, initialDrunks;
        bool onFire, calmExodus;
        Vector2[] homeAnchor;
        int[] arrivalWave;
        RectInt stageRect;
        int stageRegion = -1;
        readonly int[] phaseInjured = new int[RoundGenerator.PhaseCount], phaseMoments = new int[RoundGenerator.PhaseCount], phaseSolved = new int[RoundGenerator.PhaseCount];
        readonly float[] phaseHeat = new float[RoundGenerator.PhaseCount], phaseTime = new float[RoundGenerator.PhaseCount];
        readonly bool[] phaseReached = new bool[RoundGenerator.PhaseCount];
        int injuredAtPhaseStart;

        // venue lighting / effects (under the world, so NewGame clears them)
        Light sun;
        Color sunColor0;
        float sunIntensity0 = -1f, ambientIntensity0;
        Color ambientLight0;
        ParticleSystem rain, fireworks;
        Light fireworkLight;
        readonly List<Light> strobes = new();
        float rainUntil = -1f;
        AudioSource musicSource;
        AudioClip kickClip;
        Material particleMaterial;

        /// <summary>The show ended without an incident: people leave calmly (no siren).</summary>
        bool CalmExodus => calmExodus;
        bool EveningOn => InControlRoom && evening != null;
        int MomentScore => momentScore;

        // ================================================================ Setup

        /// <summary>NewGame (control room, after the units): the late arrivals queue inside the entrances; everything else resets.</summary>
        void SetupEvening()
        {
            ResetEvening();
            if (!InControlRoom) return;
            stageRect = EveningSpots.StageRect(venue);
            stageRegion = EveningSpots.StageRegion(venue);
            homeAnchor = new Vector2[sim.count];
            arrivalWave = new int[sim.count];
            for (int i = 0; i < sim.count; i++)
            {
                homeAnchor[i] = sim.anchor[i];
                arrivalWave[i] = -1;
                if (sim.drunk[i]) initialDrunks++;
            }
            QueueArrivals();
        }

        void ResetEvening()
        {
            evening = null;
            foreach (var m in moments) if (m.marker != null) Destroy(m.marker);
            moments.Clear();
            eveningPhase = -1;
            showClock = momentClock = heat = chatterTimer = timelineTimer = fireworkTimer = beatClock = beatAccumulator = lightBlend = 0f;
            streak = bestStreak = momentScore = initialDrunks = 0;
            onFire = calmExodus = false;
            homeAnchor = null;
            arrivalWave = null;
            System.Array.Clear(phaseInjured, 0, phaseInjured.Length);
            System.Array.Clear(phaseMoments, 0, phaseMoments.Length);
            System.Array.Clear(phaseSolved, 0, phaseSolved.Length);
            System.Array.Clear(phaseHeat, 0, phaseHeat.Length);
            System.Array.Clear(phaseTime, 0, phaseTime.Length);
            System.Array.Clear(phaseReached, 0, phaseReached.Length);
            rain = fireworks = null;          // destroyed with the old world
            fireworkLight = null;
            strobes.Clear();
            rainUntil = -1f;
            RestoreLighting();
            if (crowdRenderer != null) crowdRenderer.beatTime = -1f;
            if (controlRoom != null)
            {
                controlRoom.TimelineText = "";
                controlRoom.heatAlarm = false;
                controlRoom.roomTint = Color.white;
            }
        }

        /// <summary>
        /// <see cref="arrivingShare"/> of the sober crowd (not already by an entrance) waits in the open ground just inside
        /// the nearest entrance, 2 per m², first wave nearest the door. Each is given a wave (0–3).
        /// </summary>
        void QueueArrivals()
        {
            var entrances = new List<int>();
            for (int d = 0; d < venue.doors.Count; d++) if (venue.doors[d].gate == null) entrances.Add(d);
            if (entrances.Count == 0 || arrivingShare <= 0f) return;
            var queueCells = new List<int>[entrances.Count];
            for (int e = 0; e < entrances.Count; e++) queueCells[e] = QueueCells(venue.doors[entrances[e]], 400);
            var used = new int[entrances.Count];

            var pick = new List<int>();
            for (int i = 0; i < sim.count; i++)
            {
                if (sim.drunk[i] || rng.NextDouble() >= arrivingShare) continue;
                bool byDoor = false;
                foreach (int d in entrances) byDoor |= (venue.doors[d].Center - sim.position[i]).sqrMagnitude < 20f * 20f;
                if (!byDoor) pick.Add(i);
            }
            // first come, first served: wave 0 stands nearest the door
            for (int w = 0, k = 0; k < pick.Count; k++)
            {
                int i = pick[k];
                w = k * 4 / Mathf.Max(1, pick.Count);
                int e = 0;
                float best = float.MaxValue;
                for (int n = 0; n < entrances.Count; n++)
                {
                    float sq = (venue.doors[entrances[n]].Center - homeAnchor[i]).sqrMagnitude;
                    if (sq < best && used[n] < queueCells[n].Count * 2) { best = sq; e = n; }
                }
                if (used[e] >= queueCells[e].Count * 2) continue; // no room left: they're already in
                int cell = queueCells[e][used[e]++ / 2];
                var spot = FestivalVenue.CellCenter(cell) + new Vector2((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.6f;
                sim.position[i] = sim.anchor[i] = spot;
                sim.velocity[i] = Vector2.zero;
                arrivalWave[i] = w;
            }
        }

        /// <summary>Open cells inside the fence nearest a door (breadth first from its approach), up to <paramref name="max"/>.</summary>
        List<int> QueueCells(Door door, int max)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (int a in door.approach)
                if (venue.cells[a] == CellType.Open && seen.Add(a)) queue.Enqueue(a);
            while (queue.Count > 0 && result.Count < max)
            {
                int c = queue.Dequeue();
                result.Add(c);
                int x = c % W, y = c / W;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    int n = FestivalVenue.Index(nx, ny);
                    if (venue.cells[n] != CellType.Open || venue.regionOf[n] < 0 || !seen.Add(n)) continue;
                    queue.Enqueue(n);
                }
            }
            return result;
        }

        /// <summary>StartShow (control room): plan the evening and move the disasters into their phase.</summary>
        void PlanEvening()
        {
            if (!InControlRoom || homeAnchor == null) return;
            evening = RoundGenerator.BuildEvening(difficultySettings, venue, phaseDurations, rng.Next());
            RoundGenerator.ShiftDisasters(schedule, evening.disasterTime);
            foreach (var plan in evening.moments)
                moments.Add(new Moment { plan = plan, state = MomentState.Pending });
            FindSun();
            EnterPhase(RoundGenerator.Doors);
        }

        // ================================================================ Every step

        /// <summary>Every simulation step while Live (Simulate, after the camera tasks).</summary>
        void UpdateEvening(float dt)
        {
            if (!EveningOn || phase != Phase.Live) return;
            showClock += dt;
            momentClock += dt * (heat >= 70f && eveningPhase < RoundGenerator.Exodus ? 1.5f : 1f);

            // An evacuation stops the show; otherwise the phases follow the clock.
            if (incidentStarted && eveningPhase < RoundGenerator.Exodus) StopShow();
            while (eveningPhase < RoundGenerator.Exodus && showClock >= evening.phaseStart[eveningPhase + 1])
            {
                if (eveningPhase + 1 == RoundGenerator.Exodus) { StartCalmExodus(); break; }
                EnterPhase(eveningPhase + 1);
            }

            foreach (var m in moments)
            {
                if (m.state == MomentState.Pending)
                {
                    if (eveningPhase >= RoundGenerator.Exodus) { m.state = MomentState.Cancelled; continue; }
                    if (momentClock >= m.plan.time && eveningPhase >= m.plan.phase) Open(m);
                    continue;
                }
                bool endPending = m.endAt > 0f && m.endAt != float.MaxValue;
                if (m.state == MomentState.Active || endPending) Tick(m, dt);
            }
            UpdateFireworks(dt);
            UpdateHeat(dt);
            UpdateChatter(dt);
            if (eveningPhase >= 0 && eveningPhase < RoundGenerator.PhaseCount)
            {
                phaseHeat[eveningPhase] += heat * dt;
                phaseTime[eveningPhase] += dt;
            }
        }

        /// <summary>Every frame (UpdateControlRoomFeeds): music, lights, timeline, minimap markers.</summary>
        void UpdateEveningFrame()
        {
            if (!EveningOn) return;
            float dt = paused || phase != Phase.Live ? 0f : Time.deltaTime * Speeds[speedIndex];
            UpdateMusic(dt);
            UpdateLighting();
            UpdateMomentMarkers();
            timelineTimer -= Time.unscaledDeltaTime;
            if (timelineTimer <= 0f)
            {
                timelineTimer = 0.25f;
                UpdateTimeline();
            }
            controlRoom.heatAlarm = phase == Phase.Live && heat >= 90f;
        }

        // ================================================================ Phases

        void EnterPhase(int index)
        {
            if (eveningPhase >= 0 && eveningPhase < RoundGenerator.PhaseCount)
                phaseInjured[eveningPhase] += sim.InjuredCount - injuredAtPhaseStart;
            injuredAtPhaseStart = sim.InjuredCount;
            eveningPhase = index;
            phaseReached[index] = true;
            lightBlend = 0f;
            string name = RoundGenerator.PhaseNames[index];
            string headline = index switch
            {
                RoundGenerator.Doors => "DOORS OPEN",
                RoundGenerator.WarmUp => "WARM-UP ACT ON STAGE",
                RoundGenerator.Headliner => "HEADLINER ON STAGE",
                RoundGenerator.Encore => "ENCORE + FIREWORKS",
                _ => "EXODUS",
            };
            string advice = index switch
            {
                RoundGenerator.Doors => "The queues are coming in in waves. Watch the entrances.",
                RoundGenerator.WarmUp => "Everyone wants a drink before the headliner. The bar will get busy.",
                RoundGenerator.Headliner => "Watch the front: drops, mosh pits, crowd surfers.",
                RoundGenerator.Encore => "Fireworks over the stage. Keep an eye on the cameras.",
                _ => "",
            };
            controlRoom.WallBanner(headline, PhaseYellow, 4f);
            ShowBanner(headline, advice, Severity.Info);
            Log($"Show: {name.ToLowerInvariant()}. {advice}", Severity.Info);
            PlayRadioClick();
            bool dancing = index == RoundGenerator.Headliner || index == RoundGenerator.Encore;
            for (int i = 0; i < sim.count; i++)
                sim.dancing[i] = dancing && sim.state[i] == AgentState.Active && NearStage(sim.position[i]);
        }

        /// <summary>An evacuation started (an incident, a stray rocket): the show stops, whatever was going on is called off.</summary>
        void StopShow()
        {
            CancelMoments();
            EnterPhase(RoundGenerator.Exodus);
            controlRoom.WallBanner("SHOW STOPPED – EVACUATE", HeatRed, 5f);
        }

        /// <summary>The encore is over and nothing went wrong: everyone goes home at once. The evacuation rules apply from here.</summary>
        void StartCalmExodus()
        {
            CancelMoments();
            EnterPhase(RoundGenerator.Exodus);
            calmExodus = true;
            incidentStarted = true;
            evacuationTime = 0f;
            incidentAnnouncement = "The show is over. Please leave calmly through the nearest open exit.";
            sim.StartExodus();
            controlRoom.WallBanner("SHOW'S OVER – EXODUS", PhaseYellow, 5f);
            ShowBanner("SHOW'S OVER", "Everyone is leaving at once. Open the emergency gates and spread them over every exit.", Severity.Warning);
            Log("The show is over: everyone is heading for the exits at once. Open the emergency gates and order the zones out.", Severity.Warning);
        }

        void CancelMoments()
        {
            foreach (var m in moments)
            {
                if (m.state == MomentState.Active && m.Scored) phaseMoments[m.phaseIndex]--;
                if (m.state is MomentState.Pending or MomentState.Active) m.state = MomentState.Cancelled;
                m.endAt = float.MaxValue;
                RestorePeople(m);
                if (m.marker != null) { Destroy(m.marker); m.marker = null; }
            }
            for (int i = 0; i < sim.count; i++)
            {
                sim.dancing[i] = false;
                sim.hurry[i] = 0f;
                sim.moshRadius[i] = 0f;
                sim.surfer[i] = false;
            }
            CancelEveningCameraTasks();
        }

        // ================================================================ Moments

        void Open(Moment m)
        {
            m.state = MomentState.Active;
            m.openedAt = momentClock;
            m.phaseIndex = eveningPhase;
            int region = m.plan.region;
            string zone = region >= 0 && region < venue.regions.Count ? venue.regions[region].Code : "";
            switch (m.Kind)
            {
                case MomentKind.TicketWave:
                {
                    int wave = 0;
                    foreach (var other in moments) if (other != m && other.Kind == MomentKind.TicketWave && other.state != MomentState.Pending) wave++;
                    int n = 0;
                    if (arrivalWave != null)
                        for (int i = 0; i < sim.count; i++)
                        {
                            if (arrivalWave[i] != wave || sim.state[i] != AgentState.Active) continue;
                            sim.anchor[i] = homeAnchor[i];
                            sim.hurry[i] = 35f;
                            sim.surge[i] = 0.25f;
                            n++;
                        }
                    if (n > 0) Log($"Doors: wave {wave + 1} of 4 is coming in ({n} people).", Severity.Info);
                    m.state = MomentState.Over;
                    return;
                }
                case MomentKind.GateCrush:
                {
                    var door = venue.doors[m.plan.spot];
                    m.point = door.Center;
                    Vector2 inside = door.approach.Count > 0 ? FestivalVenue.CellCenter(door.approach[0]) : door.Center;
                    for (int i = 0; i < sim.count; i++)
                    {
                        if (!Free(i) || (sim.position[i] - door.Center).sqrMagnitude > 10f * 10f) continue;
                        m.people.Add(i);
                        sim.anchor[i] = inside;
                        sim.hurry[i] = GateCrushTime;
                        sim.surge[i] = 1f;
                    }
                    m.deadline = momentClock + GateCrushTime;
                    m.endAt = float.MaxValue;
                    Announce(m, $"CRUSH AT {door.name.ToUpperInvariant()}", $"{zone}: the queue is crushing at {door.name}. Open a reserve gate or send a unit within {GateCrushTime:0} s.");
                    m.preparedAt = gatesOpened; // gates opened so far: one more solves it
                    break;
                }
                case MomentKind.BarRush:
                {
                    var cells = EveningSpots.RegionCells(venue, region);
                    if (cells.Count == 0) { m.state = MomentState.Cancelled; return; }
                    m.point = venue.regions[region].Center;
                    for (int i = 0; i < sim.count; i++)
                    {
                        if (!Free(i) || venue.regionOf[FestivalVenue.CellAt(sim.position[i])] == region || rng.NextDouble() > 0.35) continue;
                        m.people.Add(i);
                        sim.anchor[i] = FestivalVenue.CellCenter(cells[rng.Next(cells.Count)]);
                        sim.hurry[i] = 25f;
                        sim.surge[i] = 0.4f;
                    }
                    m.deadline = momentClock + BarRushTime;
                    m.endAt = showClock + BarRushLength;
                    Announce(m, $"BAR STAMPEDE @ {zone}", $"Everyone is rushing to {venue.regions[region].Label} before the headliner. Send a unit to keep the queue within {BarRushTime:0} s.");
                    break;
                }
                case MomentKind.Rain:
                {
                    StartRain(RainLength);
                    var shelters = EveningSpots.Shelters(venue);
                    var ring = new Dictionary<int, List<int>>();
                    foreach (int s in shelters) ring[s] = EveningSpots.RingCells(venue, venue.landmarks[s].rect, 2);
                    var target = venue.landmarks[m.plan.spot].rect;
                    m.point = target.center;
                    for (int i = 0; i < sim.count; i++)
                    {
                        if (!Free(i) || rng.NextDouble() > 0.4) continue;
                        int best = -1;
                        float bestSq = float.MaxValue;
                        foreach (int s in shelters)
                        {
                            if (ring[s].Count == 0) continue;
                            float sq = (venue.landmarks[s].rect.center - sim.position[i]).sqrMagnitude;
                            if (sq < bestSq) { bestSq = sq; best = s; }
                        }
                        if (best < 0) continue;
                        var cells = ring[best];
                        sim.anchor[i] = FestivalVenue.CellCenter(cells[rng.Next(cells.Count)]);
                        sim.hurry[i] = 20f;
                        bool here = best == m.plan.spot;
                        sim.surge[i] = here ? 0.9f : 0.4f;
                        m.people.Add(i);
                    }
                    m.deadline = momentClock + ShelterTime;
                    m.endAt = showClock + RainLength;
                    Announce(m, $"RAIN! CRUSH AT {venue.landmarks[m.plan.spot].name.ToUpperInvariant()}", $"{zone}: everyone is running for cover. The way into {venue.landmarks[m.plan.spot].name} is jammed: send a unit within {ShelterTime:0} s.");
                    break;
                }
                case MomentKind.Drop:
                {
                    m.point = EveningSpots.NearestStagePoint(stageRect, venue.regions[Mathf.Max(0, region)].Center);
                    m.deadline = momentClock + RoundGenerator.DropWarning;
                    m.endAt = float.MaxValue;
                    Announce(m, $"DROP INCOMING @ {zone}", $"The DJ is building up to the drop in {RoundGenerator.DropWarning:0} s. Radio STEP BACK to the front rows, or put barriers in front of the stage.");
                    break;
                }
                case MomentKind.MoshPit:
                    StartEveningCameraTask(CameraTaskKind.MoshPit, region);
                    m.state = MomentState.Over;
                    return;
                case MomentKind.Surfer:
                    StartEveningCameraTask(CameraTaskKind.Surfer, region);
                    m.state = MomentState.Over;
                    return;
                case MomentKind.LostChild:
                    StartEveningCameraTask(CameraTaskKind.LostChild, region);
                    m.state = MomentState.Over;
                    return;
                case MomentKind.Fight:
                    StartEveningCameraTask(CameraTaskKind.Fight, region);
                    m.state = MomentState.Over;
                    return;
                case MomentKind.Fireworks:
                    fireworkTimer = 0.5f;
                    m.state = MomentState.Over;
                    controlRoom.WallBanner("FIREWORKS!", FireOrange, 3f);
                    Log("Fireworks over the stage!", Severity.Info);
                    return;
                case MomentKind.StrayRocket:
                {
                    m.state = MomentState.Over;
                    if (incidentStarted) return;
                    var cell = StrayRocketCell();
                    if (cell < 0) return;
                    var at = new Vector2Int(cell % W, cell / W);
                    schedule.Add(new FireIncident(at, "where a stray rocket came down", venue.regionOf[cell]) { startTime = 0f });
                    Log("A firework rocket went astray and came down in the crowd!", Severity.Danger);
                    return;
                }
            }
            phaseMoments[m.phaseIndex]++;
            if (region >= 0) controlRoom.OnIncident(region);
        }

        void Announce(Moment m, string title, string text)
        {
            Log(text, Severity.Warning);
            ShowBanner(title, text, Severity.Warning);
            controlRoom.WallBanner(title, HeatRed, 3.5f);
            if (m.plan.region >= 0) controlRoom.SignalWarning(m.plan.region, title);
            PlayRadioClick();
        }

        void Tick(Moment m, float dt)
        {
            switch (m.Kind)
            {
                case MomentKind.GateCrush:
                    if (m.state != MomentState.Active) break;
                    if (gatesOpened > m.preparedAt || UnitNear(m.point, 6f))
                    {
                        Calm(m, homeAfter: true);
                        Solve(m, 250, gatesOpened > m.preparedAt ? "Reserve gate open: the queue at the entrance is easing." : "Unit at the gate: they're letting people through in order.");
                    }
                    else if (momentClock >= m.deadline)
                    {
                        InjureNearest(m.point, 2 + Level);
                        Calm(m, homeAfter: true);
                        Fail(m, 0, $"People went down in the crush at {venue.doors[m.plan.spot].name}.");
                    }
                    break;
                case MomentKind.BarRush:
                    if (m.state == MomentState.Active)
                    {
                        if (UnitInRegion(m.plan.region))
                        {
                            Calm(m, homeAfter: false);
                            Solve(m, 200, $"Unit at {venue.regions[m.plan.region].Label}: the queue is organised.");
                        }
                        else if (momentClock >= m.deadline)
                        {
                            InjureNearest(DensestCell(m.plan.region), 1 + Level);
                            Fail(m, 0, $"People got hurt in the scrum at {venue.regions[m.plan.region].Label}.");
                        }
                    }
                    if (showClock >= m.endAt) EndBarRush(m);
                    break;
                case MomentKind.Rain:
                    if (m.state == MomentState.Active)
                    {
                        if (UnitNear(m.point, 9f))
                        {
                            Calm(m, homeAfter: false);
                            Solve(m, 200, $"Unit at {venue.landmarks[m.plan.spot].name}: people are getting in out of the rain in order.");
                        }
                        else if (momentClock >= m.deadline)
                        {
                            InjureNearest(m.point, 2 + Level);
                            Fail(m, 0, $"People were crushed getting into {venue.landmarks[m.plan.spot].name}.");
                        }
                    }
                    if (showClock >= m.endAt)
                    {
                        RestorePeople(m);
                        m.endAt = float.MaxValue;
                        if (m.state == MomentState.Active) m.state = MomentState.Over;
                        Log("The rain has stopped. People are going back to their spots.", Severity.Info);
                    }
                    break;
                case MomentKind.Drop:
                    TickDrop(m);
                    break;
            }
        }

        // ---------------------------------------------------------------- The drop

        void TickDrop(Moment m)
        {
            if (m.state == MomentState.Active)
            {
                float left = m.deadline - momentClock;
                int shown = Mathf.CeilToInt(left);
                if (left <= 5f && left > 0f && shown != m.lastShown)
                {
                    m.lastShown = shown;
                    controlRoom.WallBanner($"DROP IN {shown}…", m.prepared ? new Color(0.5f, 1f, 0.6f) : HeatRed, 1.1f);
                }
                if (left > 0f) return;

                bool barriers = BarriersAtStage() >= 8;
                if (m.prepared || barriers)
                {
                    controlRoom.WallBanner("DROP! FRONT HELD", new Color(0.5f, 1f, 0.6f), 2.5f);
                    float response = m.prepared ? m.preparedAt - m.openedAt : 0f;
                    Solve(m, 250, barriers && !m.prepared ? "The barriers held the front on the drop." : "Front rows stepped back: the drop went off safely.", response);
                }
                else
                {
                    // Everyone near the stage throws themselves forward.
                    controlRoom.WallBanner("DROP!", HeatRed, 2.5f);
                    for (int i = 0; i < sim.count; i++)
                    {
                        if (!Free(i) || !NearStage(sim.position[i])) continue;
                        Vector2 to = EveningSpots.NearestStagePoint(stageRect, sim.position[i]) - sim.position[i];
                        float d = to.magnitude;
                        if (d > 0.1f) sim.anchor[i] = sim.position[i] + to / d * Mathf.Min(3f, Mathf.Max(0f, d - 0.8f));
                        sim.hurry[i] = DropSurgeTime;
                        sim.surge[i] = 1f;
                        m.people.Add(i);
                    }
                    InjureFrontRows(2 + Level);
                    Fail(m, 0, "The crowd surged on the drop: people at the front were crushed against the barrier.");
                }
                m.surged = true;
                m.endAt = showClock + DropSurgeTime + 2f;
                return;
            }
            if (m.endAt != float.MaxValue && showClock >= m.endAt)
            {
                RestorePeople(m);
                m.endAt = float.MaxValue;
            }
        }

        /// <summary>Radio STEP BACK: the front rows move back from the stage and stay calm through the drop.</summary>
        void StepBack(Moment m)
        {
            if (m.prepared) return;
            m.prepared = true;
            m.preparedAt = momentClock;
            for (int i = 0; i < sim.count; i++)
            {
                if (!Free(i) || !NearStage(sim.position[i])) continue;
                Vector2 from = sim.position[i] - EveningSpots.NearestStagePoint(stageRect, sim.position[i]);
                float d = from.magnitude;
                if (d < 0.1f) continue;
                var target = sim.position[i] + from / d * 3f;
                if (venue.cells[FestivalVenue.CellAt(target)] != CellType.Open) continue;
                sim.anchor[i] = target;
                sim.hurry[i] = 4f;
                sim.orderlyTime[i] = 10f;
                m.people.Add(i);
            }
        }

        int BarriersAtStage()
        {
            int n = 0;
            for (int c = 0; c < FestivalVenue.CellCount; c++)
                if (venue.cells[c] == CellType.Barrier && NearStage(FestivalVenue.CellCenter(c))) n++;
            return n;
        }

        void InjureFrontRows(int count)
        {
            var front = new List<(float d, int i)>();
            for (int i = 0; i < sim.count; i++)
                if (Free(i) && NearStage(sim.position[i]))
                    front.Add(((EveningSpots.NearestStagePoint(stageRect, sim.position[i]) - sim.position[i]).sqrMagnitude, i));
            front.Sort((a, b) => a.d.CompareTo(b.d));
            for (int k = 0; k < count && k < front.Count; k++) sim.InjureByCrush(front[k].i);
        }

        // ---------------------------------------------------------------- Helpers

        int Level => difficultySettings.difficulty switch { Difficulty.Easy => 0, Difficulty.Medium => 1, _ => 2 };

        /// <summary>Someone the evening can move about: active, sober, calm, not on a camera task, not being escorted.</summary>
        bool Free(int i) => sim.state[i] == AgentState.Active && !sim.drunk[i] && !sim.evacuating[i] && !sim.child[i] && !sim.fighting[i]
                            && !sim.surfer[i] && sim.moshRadius[i] <= 0f && sim.escortedBy[i] < 0;

        bool NearStage(Vector2 p) => (EveningSpots.NearestStagePoint(stageRect, p) - p).sqrMagnitude <= StageReach * StageReach;

        bool UnitNear(Vector2 p, float range)
        {
            foreach (var u in units) if ((u.position - p).sqrMagnitude <= range * range) return true;
            return false;
        }

        bool UnitInRegion(int region)
        {
            foreach (var u in units)
                if (venue.regionOf[FestivalVenue.CellAt(u.position)] == region) return true;
            return false;
        }

        void InjureNearest(Vector2 p, int count)
        {
            var near = new List<(float d, int i)>();
            for (int i = 0; i < sim.count; i++)
                if (sim.state[i] == AgentState.Active && !sim.drunk[i])
                {
                    float sq = (sim.position[i] - p).sqrMagnitude;
                    if (sq < 10f * 10f) near.Add((sq, i));
                }
            near.Sort((a, b) => a.d.CompareTo(b.d));
            for (int k = 0; k < count && k < near.Count; k++) sim.InjureByCrush(near[k].i);
        }

        Vector2 DensestCell(int region)
        {
            float best = -1f;
            int cell = -1;
            for (int c = 0; c < FestivalVenue.CellCount; c++)
                if (venue.regionOf[c] == region && sim.density[c] > best) { best = sim.density[c]; cell = c; }
            return cell >= 0 ? FestivalVenue.CellCenter(cell) : venue.regions[region].Center;
        }

        /// <summary>A unit got there: no more shoving, orderly for a while (and on to their spot when <paramref name="homeAfter"/>).</summary>
        void Calm(Moment m, bool homeAfter)
        {
            foreach (int i in m.people)
            {
                if (sim.state[i] != AgentState.Active) continue;
                sim.surge[i] = 0f;
                sim.orderlyTime[i] = 8f;
                if (homeAfter && homeAnchor != null)
                {
                    sim.anchor[i] = homeAnchor[i];
                    sim.hurry[i] = 20f;
                }
            }
        }

        /// <summary>Everyone the moment moved goes back to where they were standing before (walking).</summary>
        void RestorePeople(Moment m)
        {
            if (homeAnchor == null) return;
            foreach (int i in m.people)
            {
                if (sim.state[i] != AgentState.Active) continue;
                sim.anchor[i] = homeAnchor[i];
                sim.hurry[i] = 0f;
            }
            m.people.Clear();
        }

        void EndBarRush(Moment m)
        {
            m.endAt = float.MaxValue;
            // A round at the bar: half again as many drunk fans as the evening started with.
            var bar = new List<int>();
            for (int i = 0; i < sim.count; i++)
                if (Free(i) && venue.regionOf[FestivalVenue.CellAt(sim.position[i])] == m.plan.region) bar.Add(i);
            int extra = Mathf.Min(bar.Count, Mathf.Max(2, Mathf.RoundToInt(initialDrunks * 0.5f)));
            for (int k = 0; k < extra; k++)
            {
                int j = rng.Next(bar.Count);
                sim.drunk[bar[j]] = true;
                sim.dancing[bar[j]] = false;
                bar.RemoveAt(j);
            }
            RestorePeople(m);
            if (m.state == MomentState.Active) m.state = MomentState.Over;
            if (extra > 0) Log($"{extra} more fans have had one too many at {venue.regions[m.plan.region].Label}.", Severity.Warning);
        }

        int StrayRocketCell()
        {
            var cells = new List<int>();
            for (int c = 0; c < FestivalVenue.CellCount; c++)
            {
                if (venue.cells[c] != CellType.Open || venue.regionOf[c] < 0) continue;
                float sq = (EveningSpots.NearestStagePoint(stageRect, FestivalVenue.CellCenter(c)) - FestivalVenue.CellCenter(c)).sqrMagnitude;
                if (sq >= 6f * 6f && sq <= 16f * 16f) cells.Add(c);
            }
            return cells.Count > 0 ? cells[rng.Next(cells.Count)] : -1;
        }

        // ================================================================ HEAT, combo, score

        void UpdateHeat(float dt)
        {
            if (eveningPhase >= RoundGenerator.Exodus && !calmExodus)
            {
                heat = Mathf.MoveTowards(heat, Mathf.Clamp01(sim.MaxPressure / crowd.crushThreshold) * 100f, 5f * dt);
                return;
            }
            int open = OpenCameraTasks;
            foreach (var m in moments) if (m.state == MomentState.Active && m.Scored) open++;
            float pressure = sim.MaxPressure / Mathf.Max(0.1f, crowd.crushThreshold);
            heat += dt * open * 1.2f;
            if (pressure > 0.6f) heat += dt * (pressure - 0.6f) * 25f;
            if (open == 0 && pressure <= 0.6f) heat -= dt * 1.5f;
            heat = Mathf.Clamp(heat, 0f, 100f);
        }

        void UpdateChatter(float dt)
        {
            if (heat < 70f || eveningPhase >= RoundGenerator.Exodus || units.Count == 0) return;
            chatterTimer -= dt;
            if (chatterTimer > 0f) return;
            chatterTimer = 6f + (float)rng.NextDouble() * 4f;
            var u = units[rng.Next(units.Count)];
            string[] lines =
            {
                "getting tight here!", "need backup, it's packed!", "can barely hear you over the music!",
                "people climbing the barrier!", "they're pushing at the front!", "somebody's down, checking!",
                "it's getting rowdy over here!", "we're stretched thin!",
            };
            Log($"{RadioName(u)}: {lines[rng.Next(lines.Length)]}", Severity.Warning);
            PlayRadioClick();
        }

        /// <summary>
        /// A moment (or camera task) fixed: points × difficulty × combo (×1.5 within 15 s, ×2 while ON FIRE after three
        /// fast fixes in a row). Lowers HEAT. Returns the points.
        /// </summary>
        int ScoreFix(int basePoints, float responseTime)
        {
            bool fast = responseTime <= FastFix;
            if (fast)
            {
                streak++;
                bestStreak = Mathf.Max(bestStreak, streak);
                if (streak >= 3 && !onFire)
                {
                    onFire = true;
                    controlRoom.WallBanner("ON FIRE  x2", FireOrange, 3f);
                    Log("Three fast fixes in a row: ON FIRE! Double points until something goes wrong.", Severity.Good);
                }
            }
            else streak = 0;
            float mult = onFire ? 2f : fast ? 1.5f : 1f;
            heat = Mathf.Max(0f, heat - 15f);
            return Mathf.RoundToInt(basePoints * difficultySettings.scoreMultiplier * mult);
        }

        void MissFix(int penalty)
        {
            streak = 0;
            if (onFire)
            {
                onFire = false;
                Log("Combo lost.", Severity.Warning);
            }
            heat = Mathf.Min(100f, heat + 12f);
            momentScore -= penalty;
        }

        void Solve(Moment m, int points, string text, float responseTime = -1f)
        {
            m.state = MomentState.Solved;
            int earned = ScoreFix(points, responseTime >= 0f ? responseTime : momentClock - m.openedAt);
            momentScore += earned;
            phaseSolved[m.phaseIndex]++;
            Log($"{text} (+{earned})", Severity.Good);
            PlayRadioClick();
        }

        void Fail(Moment m, int penalty, string text)
        {
            m.state = MomentState.Failed;
            MissFix(penalty);
            Log($"Control: {text}", Severity.Danger);
            PlayRadioClick();
        }

        // Camera tasks feed the same HEAT / combo / grades.
        int EveningCameraTaskOpened()
        {
            int p = Mathf.Clamp(eveningPhase, 0, RoundGenerator.PhaseCount - 1);
            if (EveningOn) phaseMoments[p]++;
            return p;
        }

        int EveningCameraTaskSolved(int phaseIndex, int basePoints, float responseTime)
        {
            if (!EveningOn) return Mathf.RoundToInt(basePoints * difficultySettings.scoreMultiplier);
            phaseSolved[Mathf.Clamp(phaseIndex, 0, RoundGenerator.PhaseCount - 1)]++;
            return ScoreFix(basePoints, responseTime);
        }

        void EveningCameraTaskFailed()
        {
            if (EveningOn) MissFix(0);
        }

        // ================================================================ Radio

        /// <summary>Radio wheel: STEP BACK while a drop is counting down.</summary>
        void AddEveningCommands(int unitIndex)
        {
            if (!EveningOn) return;
            var u = units[unitIndex];
            foreach (var moment in moments)
            {
                if (moment.Kind != MomentKind.Drop || moment.state != MomentState.Active || moment.prepared) continue;
                var m = moment;
                float left = Mathf.Max(0f, m.deadline - momentClock);
                radioItems.Add(new RadioItem
                {
                    label = "STEP BACK",
                    sub = $"{venue.regions[Mathf.Max(0, m.plan.region)].Code} front, drop in {Mathf.CeilToInt(left)} s",
                    color = new Color(1f, 0.45f, 0.35f),
                    action = () =>
                    {
                        StepBack(m);
                        RadioReply(u, "moving the front rows back from the stage.");
                        CloseRadio();
                    },
                });
            }
        }

        // ================================================================ Timeline, markers

        void UpdateTimeline()
        {
            if (evening == null) return;
            var sb = new System.Text.StringBuilder();
            for (int p = 0; p < RoundGenerator.PhaseCount; p++)
            {
                if (p > 0) sb.Append("<color=#7080a0>  >  </color>");
                string name = RoundGenerator.PhaseShort[p];
                if (p == eveningPhase)
                {
                    sb.Append("<color=#ffd030><b>").Append(name);
                    if (p < RoundGenerator.Exodus && phase == Phase.Live)
                        sb.Append(' ').Append(FormatTime(Mathf.Max(0f, evening.phaseStart[p + 1] - showClock)));
                    sb.Append("</b></color>");
                }
                else sb.Append(p < eveningPhase ? "<color=#607080>" : "<color=#c8d0dc>").Append(name).Append("</color>");
            }
            sb.Append('\n');
            var next = NextMoment();
            if (next != null)
            {
                string where = next.plan.region >= 0 && next.plan.region < venue.regions.Count ? $" @ {venue.regions[next.plan.region].Code}" : "";
                sb.Append("<color=#c8d0dc>NEXT: </color><color=#ffffff>").Append(MomentLabel(next.Kind)).Append(where).Append("</color>   ");
            }
            string heatColor = heat >= 90f ? "#ff4a3a" : heat >= 70f ? "#ffa030" : "#7fe08a";
            sb.Append("<color=").Append(heatColor).Append(">HEAT ").Append(Mathf.RoundToInt(heat)).Append("%</color>");
            if (onFire) sb.Append("   <color=#ff9a20><b>ON FIRE x2</b></color>");
            else if (streak > 0) sb.Append("   <color=#ffd030>COMBO ").Append(streak).Append("</color>");
            controlRoom.TimelineText = sb.ToString();

            if (controlRoom.LogTitle != null)
            {
                string current = eveningPhase >= 0 ? RoundGenerator.PhaseShort[eveningPhase] : "";
                string title = $"INCIDENT LOG   <color=#ffd030>{current}</color>   <color={heatColor}>HEAT {Mathf.RoundToInt(heat)}%</color>";
                if (controlRoom.LogTitle.text != title) controlRoom.LogTitle.text = title;
            }
        }

        Moment NextMoment()
        {
            Moment next = null;
            foreach (var m in moments)
                if (m.state == MomentState.Pending && m.Kind is not (MomentKind.TicketWave or MomentKind.StrayRocket) && (next == null || m.plan.time < next.plan.time)) next = m;
            return next;
        }

        static string MomentLabel(MomentKind kind) => kind switch
        {
            MomentKind.GateCrush => "GATE SURGE",
            MomentKind.BarRush => "BAR RUSH",
            MomentKind.Rain => "RAIN",
            MomentKind.Drop => "DROP",
            MomentKind.MoshPit => "MOSH PIT",
            MomentKind.Surfer => "CROWD SURFER",
            MomentKind.Fireworks => "FIREWORKS",
            MomentKind.LostChild => "LOST CHILD",
            MomentKind.Fight => "FIGHT",
            _ => kind.ToString().ToUpperInvariant(),
        };

        /// <summary>A blinking red disc on the minimap at each active moment (not the find-on-camera ones).</summary>
        void UpdateMomentMarkers()
        {
            bool blink = Mathf.Repeat(Time.unscaledTime, 0.7f) < 0.45f;
            foreach (var m in moments)
            {
                bool show = m.state == MomentState.Active && m.Scored;
                if (show && m.marker == null && minimapRoot != null)
                {
                    m.marker = MinimapMarker(PrimitiveType.Cylinder, MinimapIncident, new Vector3(10f, 0.2f, 10f));
                    m.marker.name = $"Moment {m.Kind}";
                    m.marker.transform.position = new Vector3(m.point.x, 5f, m.point.y);
                }
                if (m.marker == null) continue;
                if (!show) { Destroy(m.marker); m.marker = null; continue; }
                if (m.marker.activeSelf != blink) m.marker.SetActive(blink);
            }
        }

        // ================================================================ Lights, rain, fireworks, music

        void FindSun()
        {
            if (sun == null)
                foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (light.type == LightType.Directional && (controlRoom == null || !light.transform.IsChildOf(controlRoom.transform))) { sun = light; break; }
            if (sun != null && sunIntensity0 < 0f)
            {
                sunColor0 = sun.color;
                sunIntensity0 = sun.intensity;
                ambientIntensity0 = RenderSettings.ambientIntensity;
                ambientLight0 = RenderSettings.ambientLight;
            }
        }

        void RestoreLighting()
        {
            if (sun != null && sunIntensity0 >= 0f)
            {
                sun.color = sunColor0;
                sun.intensity = sunIntensity0;
                RenderSettings.ambientIntensity = ambientIntensity0;
                RenderSettings.ambientLight = ambientLight0;
            }
        }

        void UpdateLighting()
        {
            if (eveningPhase < 0) return;
            (Color color, float sunK, float ambientK, Color room) = eveningPhase switch
            {
                RoundGenerator.Doors => (new Color(1f, 0.86f, 0.66f), 0.95f, 0.95f, new Color(1f, 0.92f, 0.8f)),
                RoundGenerator.WarmUp => (new Color(1f, 0.55f, 0.35f), 0.6f, 0.8f, new Color(1f, 0.7f, 0.5f)),
                RoundGenerator.Headliner => (new Color(0.45f, 0.55f, 1f), 0.3f, 0.62f, new Color(0.55f, 0.6f, 1f)),
                RoundGenerator.Encore => (new Color(0.55f, 0.45f, 1f), 0.28f, 0.6f, new Color(0.8f, 0.5f, 1f)),
                _ => (new Color(0.65f, 0.7f, 1f), 0.35f, 0.65f, new Color(0.85f, 0.88f, 1f)),
            };
            if (rain != null && rain.isEmitting) { sunK *= 0.65f; ambientK *= 0.85f; }
            lightBlend = Mathf.MoveTowards(lightBlend, 1f, Time.unscaledDeltaTime / 4f);
            float k = lightBlend * Time.unscaledDeltaTime * 3f;
            if (sun != null && sunIntensity0 >= 0f)
            {
                sun.color = Color.Lerp(sun.color, sunColor0 * color, k);
                sun.intensity = Mathf.Lerp(sun.intensity, sunIntensity0 * sunK, k);
                RenderSettings.ambientIntensity = Mathf.Lerp(RenderSettings.ambientIntensity, ambientIntensity0 * ambientK, k);
                RenderSettings.ambientLight = Color.Lerp(RenderSettings.ambientLight, ambientLight0 * ambientK, k);
            }
            controlRoom.roomTint = room;

            // Stage strobes on the beat (HEADLINER, ENCORE).
            bool party = phase == Phase.Live && (eveningPhase == RoundGenerator.Headliner || eveningPhase == RoundGenerator.Encore);
            if (party && strobes.Count == 0) BuildStrobes();
            for (int s = 0; s < strobes.Count; s++)
            {
                var light = strobes[s];
                if (light == null) continue;
                if (!party) { light.intensity = 0f; continue; }
                float beat = Mathf.Repeat(beatClock, 1f);
                int bar = Mathf.FloorToInt(beatClock);
                light.color = FireworkColors[(bar + s) % FireworkColors.Length];
                light.intensity = 6f + (beat < 0.12f && (bar + s) % 2 == 0 ? 40f : 0f);
            }
        }

        void BuildStrobes()
        {
            var crowdSide = venue.regions[Mathf.Max(0, stageRegion)].Center;
            var front = EveningSpots.NearestStagePoint(stageRect, crowdSide);
            Vector2 along = Mathf.Abs(front.y - stageRect.center.y) > Mathf.Abs(front.x - stageRect.center.x) ? Vector2.right : Vector2.up;
            float span = along == Vector2.right ? stageRect.width : stageRect.height;
            for (int s = 0; s < 4; s++)
            {
                var light = new GameObject($"Stage Strobe {s + 1}").AddComponent<Light>();
                light.transform.SetParent(world, false);
                var p = front + along * ((s - 1.5f) / 3f * span * 0.8f);
                light.transform.localPosition = new Vector3(p.x, 7f, p.y);
                light.type = LightType.Point;
                light.range = 30f;
                light.intensity = 0f;
                light.shadows = LightShadows.None;
                strobes.Add(light);
            }
        }

        Material ParticleMaterial()
        {
            if (particleMaterial != null) return particleMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            particleMaterial = shader != null ? new Material(shader) : new Material(Unlit(Color.white));
            particleMaterial.name = "Evening particles";
            if (particleMaterial.HasProperty("_BaseColor")) particleMaterial.SetColor("_BaseColor", Color.white);
            return particleMaterial;
        }

        void StartRain(float seconds)
        {
            if (rain == null)
            {
                var go = new GameObject("Rain");
                go.transform.SetParent(world, false);
                go.transform.SetLocalPositionAndRotation(new Vector3(W * 0.5f, 28f, H * 0.5f), Quaternion.Euler(90f, 0f, 0f));
                rain = go.AddComponent<ParticleSystem>();
                rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = rain.main;
                main.loop = true;
                main.playOnAwake = false;
                main.startLifetime = 1.3f;
                main.startSpeed = 24f;
                main.startSize = 0.06f;
                main.startColor = new Color(0.75f, 0.82f, 1f, 0.7f);
                main.maxParticles = 7000;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = rain.emission;
                emission.rateOverTime = 4000f;
                var shape = rain.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(W, H, 0.1f);
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.lengthScale = 6f;
                r.velocityScale = 0.02f;
                r.sharedMaterial = ParticleMaterial();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            rain.Play();
            rainUntil = showClock + seconds;
            controlRoom.WallBanner("RAIN", new Color(0.6f, 0.8f, 1f), 3f);
        }

        void UpdateFireworks(float dt)
        {
            if (rain != null && rain.isEmitting && showClock >= rainUntil) rain.Stop();
            if (fireworkLight != null) fireworkLight.intensity = Mathf.MoveTowards(fireworkLight.intensity, 0f, dt * 200f);
            if (eveningPhase != RoundGenerator.Encore || fireworkTimer <= 0f) return;
            fireworkTimer -= dt;
            if (fireworkTimer > 0f) return;
            fireworkTimer = 2.5f + (float)rng.NextDouble() * 2.5f;
            LaunchFirework();
        }

        void LaunchFirework()
        {
            if (fireworks == null)
            {
                var go = new GameObject("Fireworks");
                go.transform.SetParent(world, false);
                fireworks = go.AddComponent<ParticleSystem>();
                fireworks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = fireworks.main;
                main.loop = true;          // keeps simulating; bursts come from Emit
                main.playOnAwake = false;
                main.startLifetime = 1.6f;
                main.startSpeed = new ParticleSystem.MinMaxCurve(8f, 14f);
                main.startSize = 0.5f;
                main.gravityModifier = 0.5f;
                main.maxParticles = 3000;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = fireworks.emission;
                emission.rateOverTime = 0f;
                var shape = fireworks.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.3f;
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = ParticleMaterial();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                fireworkLight = new GameObject("Firework Flash").AddComponent<Light>();
                fireworkLight.transform.SetParent(world, false);
                fireworkLight.type = LightType.Point;
                fireworkLight.range = 70f;
                fireworkLight.intensity = 0f;
                fireworkLight.shadows = LightShadows.None;
                fireworks.Play();
            }
            var c = stageRect.center;
            var at = new Vector3(c.x + ((float)rng.NextDouble() - 0.5f) * stageRect.width, 22f + (float)rng.NextDouble() * 8f, c.y + ((float)rng.NextDouble() - 0.5f) * stageRect.height);
            var color = FireworkColors[rng.Next(FireworkColors.Length)];
            var emit = new ParticleSystem.EmitParams { position = at, startColor = color, applyShapeToPosition = true };
            fireworks.Emit(emit, 160);
            fireworkLight.transform.localPosition = at;
            fireworkLight.color = color;
            fireworkLight.intensity = 45f;
            controlRoom.Flash(0.6f);
            // A bang overhead: everyone flinches a little.
            for (int i = 0; i < sim.count; i++)
                if (sim.state[i] == AgentState.Active && !sim.drunk[i]) sim.panic[i] = Mathf.Max(sim.panic[i], 0.12f);
        }

        /// <summary>A bass pulse made in code, louder each phase; stops at EXODUS. Drives the dancers' beat.</summary>
        void UpdateMusic(float dt)
        {
            bool playing = phase == Phase.Live && eveningPhase >= 0 && eveningPhase < RoundGenerator.Exodus;
            crowdRenderer.beatTime = playing ? beatClock : -1f;
            if (!playing || dt <= 0f) return;
            float beats = dt * musicBpm / 60f / Mathf.Max(1f, Speeds[speedIndex]); // the music doesn't speed up with the game
            beatClock += beats;
            beatAccumulator += beats;
            if (beatAccumulator < 1f) return;
            beatAccumulator -= Mathf.Floor(beatAccumulator);
            if (musicSource == null)
            {
                musicSource = gameObject.AddComponent<AudioSource>();
                musicSource.playOnAwake = false;
                musicSource.spatialBlend = 0f;
            }
            if (kickClip == null) kickClip = MakeKick();
            float volume = eveningPhase switch { RoundGenerator.Doors => 0.12f, RoundGenerator.WarmUp => 0.2f, RoundGenerator.Headliner => 0.32f, _ => 0.38f };
            musicSource.PlayOneShot(kickClip, volume);
        }

        static AudioClip MakeKick()
        {
            const int rate = 22050;
            int length = rate * 3 / 10;
            var data = new float[length];
            float phaseAcc = 0f;
            for (int n = 0; n < length; n++)
            {
                float t = n / (float)rate;
                float freq = Mathf.Lerp(45f, 120f, Mathf.Exp(-t * 30f));
                phaseAcc += 2f * Mathf.PI * freq / rate;
                data[n] = Mathf.Sin(phaseAcc) * Mathf.Exp(-t * 9f) * 0.9f;
            }
            var clip = AudioClip.Create("Bass pulse", length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ================================================================ Debrief

        /// <summary>EndGame: closes the last phase's injury count.</summary>
        void CloseEvening()
        {
            if (!EveningOn || eveningPhase < 0) return;
            phaseInjured[eveningPhase] += sim.InjuredCount - injuredAtPhaseStart;
            injuredAtPhaseStart = sim.InjuredCount;
            controlRoom.heatAlarm = false;
            crowdRenderer.beatTime = -1f;
        }

        /// <summary>A–F for a phase: moments fixed (50), calm (30, from the average HEAT) and nobody hurt (20).</summary>
        string PhaseGrade(int p)
        {
            if (!phaseReached[p]) return "–";
            float ratio = phaseMoments[p] > 0 ? phaseSolved[p] / (float)phaseMoments[p] : 1f;
            if (p == RoundGenerator.Exodus) ratio = finalSafe / Mathf.Max(1f, sim.count);
            float avgHeat = phaseTime[p] > 0f ? phaseHeat[p] / phaseTime[p] : 0f;
            float score = 50f * ratio + 30f * (1f - avgHeat / 100f) + Mathf.Max(0f, 20f - 4f * phaseInjured[p]);
            return score >= 85f ? "A" : score >= 70f ? "B" : score >= 55f ? "C" : score >= 40f ? "D" : score >= 25f ? "E" : "F";
        }

        string EveningGrades
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                for (int p = 0; p < RoundGenerator.PhaseCount; p++)
                {
                    if (p > 0) sb.Append("  ");
                    sb.Append(RoundGenerator.PhaseShort[p].Substring(0, Mathf.Min(4, RoundGenerator.PhaseShort[p].Length))).Append(' ').Append(PhaseGrade(p));
                }
                return sb.ToString();
            }
        }

        string MomentSummary
        {
            get
            {
                int total = 0, solved = 0;
                for (int p = 0; p < RoundGenerator.PhaseCount; p++) { total += phaseMoments[p]; solved += phaseSolved[p]; }
                return $"{solved} / {total} fixed   best combo {bestStreak}" + (momentScore != 0 ? $"   ({(momentScore > 0 ? "+" : "")}{momentScore})" : "");
            }
        }
    }
}
