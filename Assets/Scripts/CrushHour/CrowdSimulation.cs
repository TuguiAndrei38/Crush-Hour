using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    [Serializable]
    public class CrowdSettings
    {
        [Min(100)] public int agentCount = 2000;
        public float radius = 0.22f;
        public float walkSpeed = 1.3f;
        [Tooltip("Extra speed multiplier at full panic: panicked people run and push.")]
        public float panicSpeedBoost = 0.9f;
        public float acceleration = 6f;

        [Header("Awareness & escape")]
        [Tooltip("Distance (m) at which people notice the fire (flames, smoke, screams).")]
        public float fireSightRange = 25f;
        [Tooltip("Chance per second that someone who hears the alarm (shouting, running people) realises there's a fire.")]
        public float awarenessSpread = 1f;
        [Tooltip("People closer than this (m) to the fire first run straight away from it, then head for a door.")]
        public float fleeDistance = 10f;
        [Tooltip("Distance (m) at which people can see that the door they're heading for is shut.")]
        public float closedDoorSight = 6f;
        [Tooltip("Metres of extra walking an informed person accepts per person-per-metre-of-door queueing at a door.")]
        public float doorCrowdWeight = 1f;

        [Header("Panic")]
        [Tooltip("How fast panic rises towards what the situation warrants (fire close by, crush, panicking neighbours).")]
        public float panicContagion = 0.8f;
        [Tooltip("Panic passed on is scaled by this, so panic fades with distance from its source.")]
        [Range(0f, 1f)] public float contagionFalloff = 0.85f;
        public float panicDecay = 0.02f;
        [Tooltip("Panic level at which a panic attack starts building up.")]
        [Range(0.5f, 1f)] public float panicAttackThreshold = 0.88f;
        [Tooltip("Seconds at that panic level before the attack hits.")]
        public float panicAttackDelay = 2.5f;
        [Tooltip("How long a panic attack lasts (s), min and max.")]
        public Vector2 panicAttackDuration = new(6f, 12f);

        [Header("Crush")]
        [Tooltip("Crowd pressure (density x panic factor) above which people get hurt.")]
        public float crushThreshold = 8.5f;
        public float injuryRate = 0.08f;

        [Header("Staff")]
        public float stewardRadius = 6f;
        public float stewardCalming = 0.5f;
        [Tooltip("Body-to-body spacing (m) stewards make people keep in their queues (normal bodies are 2 x radius).")]
        public float stewardQueueSpacing = 0.52f;
        [Tooltip("Seconds people keep queueing in order after leaving a steward's ring.")]
        public float orderlyMemory = 4f;
        [Tooltip("Reach (m) of a megaphone officer's announcement.")]
        public float policeAnnounceRadius = 14f;
        [Tooltip("Chance per announcement that someone still listening learns which doors are open.")]
        [Range(0f, 1f)] public float policeInformChance = 0.8f;
    }

    public enum AgentState : byte { Active, Safe, Injured }

    /// <summary>
    /// The crowd: a few thousand people who notice the fire, run from it, then try the nearest door
    /// (and the next one if it's shut). Panic spreads, crowds compress, and too much panic causes panic attacks.
    /// </summary>
    public class CrowdSimulation
    {
        const int W = FestivalVenue.Width, H = FestivalVenue.Height, N = FestivalVenue.CellCount;
        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        public readonly CrowdSettings settings;
        public readonly int count;
        public readonly Vector2[] position, velocity, anchor;
        public readonly float[] panic, damage, personalSpeed, stuckTime;
        public readonly AgentState[] state;
        /// <summary>Knows the fire is happening and is trying to get out.</summary>
        public readonly bool[] evacuating;
        /// <summary>Has been told (by staff/PA) which doors are open, and avoids crowded ones.</summary>
        public readonly bool[] informed;
        /// <summary>Got far enough from the fire; now heading for a door.</summary>
        public readonly bool[] hasFled;
        /// <summary>Door being headed for (-1 none), doors found shut (bit mask), time until reconsidering.</summary>
        public readonly int[] targetDoor, doorsFoundShut;
        readonly float[] retargetTime;
        /// <summary>Seconds left of a panic attack (0 = none), and build-up towards one.</summary>
        public readonly float[] attackTime, attackBuildUp;
        /// <summary>Seconds left of queueing in order because a steward is organising them (spaced out, no pushing).</summary>
        public readonly float[] orderlyTime;
        readonly List<int> nearby = new();
        readonly System.Random rng;
        float[] doorCrowd = Array.Empty<float>();
        int openDoorMask, crowdCountdown;

        // Spatial hash over the venue grid, rebuilt every step.
        readonly int[] agentCell, sorted;
        readonly int[] cellCount = new int[N], cellActive = new int[N], cellStart = new int[N], cellCursor = new int[N];
        readonly float[] cellPanic = new float[N];
        /// <summary>"FIRE!" being shouted: strongest at the flames and around people running, fading with distance.</summary>
        public readonly float[] alarm = new float[N];
        readonly float[] alarmNext = new float[N];
        float alarmTimer;
        /// <summary>Smoothed people per m².</summary>
        public readonly float[] density = new float[N];
        /// <summary>Density amplified by panic: the crush-risk metric.</summary>
        public readonly float[] pressure = new float[N];

        public float MaxPressure { get; private set; }
        public int MaxPressureCell { get; private set; }
        public int ActiveCount { get; private set; }
        public int SafeCount { get; private set; }
        public int InjuredCount { get; private set; }
        public int PanickedCount { get; private set; }
        public int EvacuatingCount { get; private set; }
        public int PanicAttackCount { get; private set; }
        public int PanicAttacksTotal { get; private set; }
        public int InjuredByCrush { get; private set; }
        public int InjuredByFire { get; private set; }

        public CrowdSimulation(CrowdSettings settings, FestivalVenue venue, System.Random rng)
        {
            this.settings = settings;
            this.rng = rng;
            count = settings.agentCount;
            position = new Vector2[count];
            velocity = new Vector2[count];
            anchor = new Vector2[count];
            panic = new float[count];
            damage = new float[count];
            personalSpeed = new float[count];
            stuckTime = new float[count];
            state = new AgentState[count];
            evacuating = new bool[count];
            informed = new bool[count];
            hasFled = new bool[count];
            targetDoor = new int[count];
            doorsFoundShut = new int[count];
            retargetTime = new float[count];
            attackTime = new float[count];
            attackBuildUp = new float[count];
            orderlyTime = new float[count];
            agentCell = new int[count];
            sorted = new int[count];
            Array.Fill(targetDoor, -1);
            Spawn(venue);
            UpdateCounts();
        }

        void Spawn(FestivalVenue venue)
        {
            float totalWeight = 0f;
            foreach (var zone in venue.crowdZones) totalWeight += zone.weight;
            var placed = new int[N];

            for (int i = 0; i < count; i++)
            {
                Vector2 p = Vector2.zero;
                for (int attempt = 0; attempt < 400; attempt++)
                {
                    float pick = (float)rng.NextDouble() * totalWeight;
                    var zone = venue.crowdZones[0];
                    foreach (var z in venue.crowdZones)
                    {
                        zone = z;
                        if ((pick -= z.weight) <= 0f) break;
                    }
                    float x = zone.rect.xMin + (float)rng.NextDouble() * zone.rect.width;
                    // Fans pack in towards the stage.
                    float t = zone.stageFacing ? Mathf.Sqrt((float)rng.NextDouble()) : (float)rng.NextDouble();
                    float y = zone.rect.yMin + t * zone.rect.height;
                    p = new Vector2(x, y);
                    int c = FestivalVenue.CellAt(p);
                    if (venue.cells[c] == CellType.Open && (placed[c] < 3 || attempt > 300))
                    {
                        placed[c]++;
                        break;
                    }
                }
                position[i] = anchor[i] = p;
                personalSpeed[i] = 0.8f + 0.4f * (float)rng.NextDouble();
            }
        }

        public static int CellOf(Vector2 p) => FestivalVenue.CellAt(p);

        void BuildSpatialHash()
        {
            Array.Clear(cellCount, 0, N);
            Array.Clear(cellActive, 0, N);
            Array.Clear(cellPanic, 0, N);
            for (int i = 0; i < count; i++)
            {
                if (state[i] == AgentState.Safe) continue;
                int c = CellOf(position[i]);
                agentCell[i] = c;
                cellCount[c]++;
                if (state[i] != AgentState.Active) continue;
                cellActive[c]++;
                cellPanic[c] += panic[i];
            }
            int run = 0;
            for (int c = 0; c < N; c++)
            {
                cellStart[c] = cellCursor[c] = run;
                run += cellCount[c];
            }
            for (int i = 0; i < count; i++)
                if (state[i] != AgentState.Safe) sorted[cellCursor[agentCell[i]]++] = i;

            MaxPressure = 0f;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int people = 0, active = 0;
                    float panicSum = 0f;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int ny = y + oy;
                        if (ny < 0 || ny >= H) continue;
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int nx = x + ox;
                            if (nx < 0 || nx >= W) continue;
                            people += cellCount[ny * W + nx];
                            active += cellActive[ny * W + nx];
                            panicSum += cellPanic[ny * W + nx];
                        }
                    }
                    int c = y * W + x;
                    density[c] = 0.5f * cellCount[c] + 0.5f * people / 9f;
                    // People lying on the ground take up room but don't push.
                    float avgPanic = active > 0 ? panicSum / active : 0f;
                    pressure[c] = density[c] * (1f + 2f * Mathf.Max(0f, avgPanic - 0.3f));
                    if (pressure[c] > MaxPressure)
                    {
                        MaxPressure = pressure[c];
                        MaxPressureCell = c;
                    }
                }
            }
        }

        /// <summary>How many people are queueing at each door, per metre of door width.</summary>
        void UpdateDoorCrowding(FestivalVenue venue)
        {
            if (doorCrowd.Length != venue.doors.Count) doorCrowd = new float[venue.doors.Count];
            for (int d = 0; d < venue.doors.Count; d++)
                doorCrowd[d] = ActiveWithin(venue.doors[d].Center, 8f).Count / (float)venue.doors[d].Width;
        }

        /// <summary>The alarm spreads one metre per tick (5 m/s) from the fire and from everyone already fleeing.</summary>
        void UpdateAlarm(FestivalVenue venue, float dt)
        {
            alarmTimer -= dt;
            if (alarmTimer > 0f) return;
            alarmTimer = 0.2f;
            for (int c = 0; c < N; c++) alarm[c] *= 0.6f;
            foreach (int c in venue.fireCells) alarm[c] = 1f;
            for (int i = 0; i < count; i++)
                if (state[i] == AgentState.Active && evacuating[i])
                    alarm[agentCell[i]] = Mathf.Max(alarm[agentCell[i]], 0.6f + 0.4f * panic[i]);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int c = y * W + x;
                    float loudest = 0f;
                    if (x > 0) loudest = Mathf.Max(loudest, alarm[c - 1]);
                    if (x < W - 1) loudest = Mathf.Max(loudest, alarm[c + 1]);
                    if (y > 0) loudest = Mathf.Max(loudest, alarm[c - W]);
                    if (y < H - 1) loudest = Mathf.Max(loudest, alarm[c + W]);
                    alarmNext[c] = Mathf.Max(alarm[c], loudest * 0.9f);
                }
            }
            Array.Copy(alarmNext, alarm, N);
        }

        public void Step(float dt, FestivalVenue venue, bool incident)
        {
            BuildSpatialHash();
            openDoorMask = venue.OpenDoorMask;
            if (incident) UpdateAlarm(venue, dt);
            if (--crowdCountdown <= 0)
            {
                UpdateDoorCrowding(venue);
                crowdCountdown = 10;
            }
            float diameter = settings.radius * 2f, diameterSq = diameter * diameter;
            float sight = settings.fireSightRange;

            for (int i = 0; i < count; i++)
            {
                if (state[i] != AgentState.Active) continue;
                Vector2 p = position[i];
                int c = agentCell[i], cx = c % W, cy = c / W;
                bool orderly = orderlyTime[i] > 0f;
                if (orderly) orderlyTime[i] -= dt;
                // A steward's queue: keep a gap to the people around you instead of pressing into them.
                float space = orderly ? Mathf.Max(diameter, settings.stewardQueueSpacing) : diameter;
                float spaceSq = space * space;

                // Neighbours: body contact, who is panicking, and who knows which doors are shut.
                Vector2 push = Vector2.zero;
                float panicSum = 0f;
                int neighbours = 0, heardShut = 0;
                for (int oy = -1; oy <= 1; oy++)
                {
                    int ny = cy + oy;
                    if (ny < 0 || ny >= H) continue;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = cx + ox;
                        if (nx < 0 || nx >= W) continue;
                        int nc = ny * W + nx, end = cellStart[nc] + cellCount[nc];
                        for (int s = cellStart[nc]; s < end; s++)
                        {
                            int j = sorted[s];
                            if (j == i) continue;
                            Vector2 d = p - position[j];
                            float dsq = d.sqrMagnitude;
                            bool fallen = state[j] == AgentState.Injured;
                            float reach = fallen ? diameter : space;
                            if (dsq < (fallen ? diameterSq : spaceSq))
                            {
                                float dist = Mathf.Sqrt(dsq);
                                Vector2 n = dist > 1e-4f ? d / dist : new Vector2(Mathf.Cos(i), Mathf.Sin(i));
                                // The fallen can be stepped over, slowly.
                                push += n * ((reach - dist) * (fallen ? 0.15f : 0.5f));
                            }
                            if (dsq < 2.25f)
                            {
                                panicSum += state[j] == AgentState.Injured ? 0.6f : panic[j];
                                heardShut |= doorsFoundShut[j]; // "it's shut up there!" passes back through the queue
                                neighbours++;
                            }
                        }
                    }
                }

                float localDensity = density[c];
                int fd = venue.fireDistance[c];
                float pv = panic[i];
                bool inAttack = attackTime[i] > 0f;

                if (incident && !inAttack)
                {
                    // Becoming aware: seeing the fire, or hearing people shout and seeing them run.
                    if (fd < sight) evacuating[i] = true;
                    else if (!evacuating[i] && alarm[c] > 0.3f && rng.NextDouble() < settings.awarenessSpread * dt) evacuating[i] = true;

                    // Panic rises towards what the situation warrants, and fades slowly once it's better.
                    // Touching distance of the flames is terrifying; further away it is frightening but bearable.
                    float fear = fd <= 1 ? 1f : fd < sight ? 0.85f * Mathf.Clamp01(1f - (fd - 2f) / sight) : 0f;
                    // Being squeezed, and being stuck in a jam for a long time, wears people down.
                    float crush = Mathf.Clamp01((localDensity - 3f) / 4f) + Mathf.Clamp01((stuckTime[i] - 1f) / 6f) * 0.8f;
                    float social = neighbours > 0 ? panicSum / neighbours * settings.contagionFalloff : 0f;
                    float target = Mathf.Min(1f, Mathf.Max(fear, Mathf.Max(crush, social)));
                    pv = target > pv ? pv + (target - pv) * settings.panicContagion * dt : Mathf.Max(target, pv - settings.panicDecay * dt);
                    if (pv > 0.3f) evacuating[i] = true;
                    if (venue.seesOpenGate[c]) doorsFoundShut[i] &= ~openDoorMask; // "that gate's open now!"
                    else doorsFoundShut[i] |= heardShut & ~openDoorMask;

                    // Too much panic for too long: a panic attack.
                    if (pv >= settings.panicAttackThreshold)
                    {
                        attackBuildUp[i] += dt;
                        if (attackBuildUp[i] >= settings.panicAttackDelay)
                        {
                            attackTime[i] = Mathf.Lerp(settings.panicAttackDuration.x, settings.panicAttackDuration.y, (float)rng.NextDouble());
                            attackBuildUp[i] = 0f;
                            inAttack = true;
                            PanicAttacksTotal++;
                        }
                    }
                    else attackBuildUp[i] = Mathf.Max(0f, attackBuildUp[i] - dt);
                    panic[i] = pv;
                }

                Vector2 desired;
                if (inAttack)
                {
                    // Frozen to the spot (and spreading fear); only the crowd moves them.
                    panic[i] = 1f;
                    attackTime[i] -= dt;
                    if (attackTime[i] <= 0f)
                    {
                        attackTime[i] = 0f;
                        panic[i] = 0.6f;
                    }
                    desired = Vector2.zero;
                }
                else if (!evacuating[i])
                {
                    // Still enjoying the show: drift back to my spot.
                    Vector2 to = anchor[i] - p;
                    float m = to.magnitude;
                    desired = m > 1.2f ? to * (0.6f / m) : Vector2.zero;
                }
                else
                {
                    // First get away from the flames, then make for a door.
                    if (fd >= settings.fleeDistance) hasFled[i] = true;
                    else if (fd <= 2) hasFled[i] = false;
                    Vector2 dir;
                    if (!hasFled[i] && venue.fleeDirection[c] != Vector2.zero)
                        dir = (FestivalVenue.CellCenter(c) + venue.fleeDirection[c] - p).normalized;
                    else
                        dir = DoorDirection(i, c, p, venue, dt);

                    float slow, urgency;
                    if (orderly)
                    {
                        // Organised by a steward: keep moving at the queue's pace, walk briskly but don't run or shove.
                        slow = Mathf.Clamp(1.1f - 0.17f * localDensity, 0.12f, 1f);
                        urgency = 1f + settings.panicSpeedBoost * pv * 0.3f;
                    }
                    else
                    {
                        slow = Mathf.Clamp(1.1f - 0.17f * localDensity, 0.12f, 1f);
                        slow = Mathf.Lerp(slow, 1f, 0.5f * pv); // panic makes people push on regardless
                        urgency = 1f + settings.panicSpeedBoost * pv;
                    }
                    desired = dir * (settings.walkSpeed * personalSpeed[i] * urgency * slow);
                }

                velocity[i] = Vector2.MoveTowards(velocity[i], desired, settings.acceleration * dt);
                // Panicked people give way less, so they compress the crowd around them; organised queues don't.
                float giveWay = orderly ? 1f : Mathf.Lerp(1f, 0.55f, panic[i]);
                Vector2 next = p + velocity[i] * dt + Vector2.ClampMagnitude(push * giveWay, 0.15f);
                next = ResolveWalls(venue, p, next);
                // Blocked people lose their momentum instead of building up endless push.
                velocity[i] = Vector2.Lerp(velocity[i], (next - p) / dt, 0.5f);

                // Stuck = trying to get out but barely moving compared with walking pace.
                // (Waiting in a steward's orderly queue doesn't wear people down the same way.)
                bool trying = evacuating[i] && !inAttack && !orderly && desired != Vector2.zero;
                if (trying && (next - p).magnitude / dt < 0.25f * settings.walkSpeed) stuckTime[i] += dt;
                else stuckTime[i] = Mathf.Max(0f, stuckTime[i] - dt);
                position[i] = next;

                int nextCell = CellOf(next);
                var type = venue.cells[nextCell];
                if (type == CellType.Outside)
                {
                    state[i] = AgentState.Safe;
                    attackTime[i] = 0f;
                    continue;
                }
                if (type == CellType.Fire)
                {
                    Injure(i);
                    InjuredByFire++;
                    continue;
                }
                float pr = pressure[nextCell];
                if (pr > settings.crushThreshold) damage[i] += (pr - settings.crushThreshold) * settings.injuryRate * dt;
                else damage[i] = Mathf.Max(0f, damage[i] - 0.1f * dt);
                if (damage[i] >= 1f)
                {
                    Injure(i);
                    InjuredByCrush++;
                }
            }

            UpdateCounts();
        }

        /// <summary>Head for the chosen door; on finding it shut, remember that and pick the next nearest.</summary>
        Vector2 DoorDirection(int i, int c, Vector2 p, FestivalVenue venue, float dt)
        {
            var doors = venue.doors;
            retargetTime[i] -= dt;
            int d = targetDoor[i];
            bool invalid = d < 0 || (doorsFoundShut[i] & (1 << d)) != 0 || !doors[d].field.Reachable(c);
            // Calm people reconsider now and then; panicked people have tunnel vision.
            if (invalid || (retargetTime[i] <= 0f && panic[i] < 0.8f))
            {
                d = targetDoor[i] = ChooseDoor(i, c, venue);
                retargetTime[i] = 2f;
            }

            for (int attempt = 0; attempt < 2 && d >= 0; attempt++)
            {
                var door = doors[d];
                if (door.IsOpen || door.field.distance[c] > settings.closedDoorSight) break;
                // Got there and it's shut: try another one.
                doorsFoundShut[i] |= 1 << d;
                panic[i] = Mathf.Min(1f, panic[i] + 0.08f);
                d = targetDoor[i] = ChooseDoor(i, c, venue);
            }

            if (d < 0)
            {
                // Every door seems shut or cut off: forget what we know and look again.
                doorsFoundShut[i] = 0;
                return velocity[i].sqrMagnitude > 0.01f ? velocity[i].normalized : Vector2.zero;
            }
            Vector2 step = doors[d].field.direction[c];
            return step == Vector2.zero ? Vector2.zero : (FestivalVenue.CellCenter(c) + step - p).normalized;
        }

        int ChooseDoor(int i, int c, FestivalVenue venue)
        {
            var doors = venue.doors;
            bool knowsDoors = informed[i];
            for (int pass = 0; pass < 2; pass++)
            {
                int best = -1;
                float bestCost = float.PositiveInfinity;
                for (int d = 0; d < doors.Count; d++)
                {
                    if ((doorsFoundShut[i] & (1 << d)) != 0 || !doors[d].field.Reachable(c)) continue;
                    // Informed people know which doors are open and avoid the queues.
                    if (knowsDoors && pass == 0 && !doors[d].IsOpen) continue;
                    float cost = doors[d].field.distance[c];
                    if (knowsDoors && d < doorCrowd.Length) cost += doorCrowd[d] * settings.doorCrowdWeight;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = d;
                    }
                }
                if (best >= 0) return best;
            }
            return -1;
        }

        Vector2 ResolveWalls(FestivalVenue venue, Vector2 from, Vector2 to)
        {
            if (!venue.IsWalkable(CellOf(to)))
            {
                var slideX = new Vector2(to.x, from.y);
                var slideY = new Vector2(from.x, to.y);
                if (venue.IsWalkable(CellOf(slideX))) to = slideX;
                else if (venue.IsWalkable(CellOf(slideY))) to = slideY;
                else return from;
            }
            // Keep the body's radius out of blocked neighbouring cells.
            float r = settings.radius;
            int x = Mathf.FloorToInt(to.x), y = Mathf.FloorToInt(to.y);
            float fx = to.x - x, fy = to.y - y;
            if (fx < r && !venue.IsWalkableAt(x - 1, y)) to.x = x + r;
            else if (fx > 1f - r && !venue.IsWalkableAt(x + 1, y)) to.x = x + 1f - r;
            if (fy < r && !venue.IsWalkableAt(x, y - 1)) to.y = y + r;
            else if (fy > 1f - r && !venue.IsWalkableAt(x, y + 1)) to.y = y + 1f - r;
            return to;
        }

        void Injure(int i)
        {
            state[i] = AgentState.Injured;
            velocity[i] = Vector2.zero;
            attackTime[i] = 0f;
        }

        void UpdateCounts()
        {
            int active = 0, safe = 0, injured = 0, panicked = 0, leaving = 0, attacks = 0;
            for (int i = 0; i < count; i++)
            {
                switch (state[i])
                {
                    case AgentState.Active:
                        active++;
                        if (panic[i] > 0.7f) panicked++;
                        if (evacuating[i]) leaving++;
                        if (attackTime[i] > 0f) attacks++;
                        break;
                    case AgentState.Safe: safe++; break;
                    default: injured++; break;
                }
            }
            ActiveCount = active;
            SafeCount = safe;
            InjuredCount = injured;
            PanickedCount = panicked;
            EvacuatingCount = leaving;
            PanicAttackCount = attacks;
        }

        /// <summary>
        /// Stewards calm people near them, help them through panic attacks, line them up in orderly spaced queues
        /// (so nobody gets squeezed) and, once there is an incident, tell them which doors are open.
        /// </summary>
        public void ApplyStewards(List<Vector2> stewards, float dt, bool incident)
        {
            foreach (var s in stewards)
            {
                foreach (int i in ActiveWithin(s, settings.stewardRadius))
                {
                    if (incident) orderlyTime[i] = settings.orderlyMemory;
                    if (attackTime[i] > 0f)
                    {
                        attackTime[i] = Mathf.Max(0.01f, attackTime[i] - 1.5f * dt); // recover faster with help
                        continue;
                    }
                    float calming = settings.stewardCalming * (panic[i] > 0.9f ? 0.4f : 1f);
                    panic[i] = Mathf.Max(0f, panic[i] - calming * dt);
                    if (incident) Inform(i);
                }
            }
        }

        /// <summary>
        /// A megaphone officer tells everyone in earshot what happened: they all start leaving, most learn which doors
        /// are open, and clear information from the police steadies the most frightened a little.
        /// </summary>
        public int PoliceAnnouncement(Vector2 officer)
        {
            int newlyInformed = 0;
            foreach (int i in ActiveWithin(officer, settings.policeAnnounceRadius))
            {
                evacuating[i] = true;
                if (!informed[i] && panic[i] < 0.8f && attackTime[i] <= 0f && rng.NextDouble() < settings.policeInformChance)
                {
                    Inform(i);
                    newlyInformed++;
                }
                panic[i] = panic[i] > 0.5f ? panic[i] - 0.1f : panic[i] + 0.05f;
            }
            return newlyInformed;
        }

        /// <summary>Everyone starts leaving; a share of those still listening learn which doors are open.</summary>
        public int AnnounceEvacuation(float reach)
        {
            int newlyInformed = 0;
            for (int i = 0; i < count; i++)
            {
                if (state[i] != AgentState.Active) continue;
                evacuating[i] = true;
                if (!informed[i] && panic[i] < 0.8f && attackTime[i] <= 0f && rng.NextDouble() < reach)
                {
                    Inform(i);
                    newlyInformed++;
                }
                if (panic[i] < 0.5f) panic[i] += 0.1f; // an emergency announcement is alarming in itself
            }
            return newlyInformed;
        }

        void Inform(int i)
        {
            informed[i] = true;
            evacuating[i] = true;
            doorsFoundShut[i] &= ~openDoorMask; // they're told which doors are open now
            retargetTime[i] = 0f;
        }

        public void AnnounceCalm(float strength)
        {
            for (int i = 0; i < count; i++)
                if (state[i] == AgentState.Active && attackTime[i] <= 0f)
                    panic[i] = Mathf.Max(0f, panic[i] - strength * (panic[i] > 0.85f ? 0.35f : 1f));
        }

        /// <summary>Active people within <paramref name="radius"/>, from the spatial hash built by the last step.</summary>
        List<int> ActiveWithin(Vector2 center, float radius)
        {
            nearby.Clear();
            float rSq = radius * radius;
            int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius)), maxX = Mathf.Min(W - 1, Mathf.FloorToInt(center.x + radius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - radius)), maxY = Mathf.Min(H - 1, Mathf.FloorToInt(center.y + radius));
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int c = y * W + x, end = cellStart[c] + cellCount[c];
                    for (int k = cellStart[c]; k < end; k++)
                    {
                        int i = sorted[k];
                        if (state[i] == AgentState.Active && (position[i] - center).sqrMagnitude <= rSq) nearby.Add(i);
                    }
                }
            }
            return nearby;
        }

        /// <summary>Moves everybody standing in <paramref name="cell"/> to a free neighbouring cell (a barrier or gate closed on them).</summary>
        public void Evict(int cell, FestivalVenue venue)
        {
            int cx = cell % W, cy = cell / W;
            for (int i = 0; i < count; i++)
            {
                if (state[i] == AgentState.Safe || CellOf(position[i]) != cell) continue;
                for (int k = 0; k < DX.Length; k++)
                {
                    int nx = cx + DX[k], ny = cy + DY[k];
                    if (!venue.IsWalkableAt(nx, ny)) continue;
                    position[i] = new Vector2(nx + 0.5f + Mathf.Cos(i) * 0.3f, ny + 0.5f + Mathf.Sin(i) * 0.3f);
                    break;
                }
            }
        }

        /// <summary>Display bucket: 0 calm, 1 informed, 2 alarmed, 3 panicking, 4 injured, 5 panic attack, -1 safe (not shown).</summary>
        public int DisplayState(int i)
        {
            if (state[i] == AgentState.Safe) return -1;
            if (state[i] == AgentState.Injured) return 4;
            if (attackTime[i] > 0f) return 5;
            float pv = panic[i];
            return pv > 0.7f ? 3 : pv > 0.3f ? 2 : informed[i] && evacuating[i] ? 1 : 0;
        }
    }
}
