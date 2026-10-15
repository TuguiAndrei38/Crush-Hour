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
        [Tooltip("Distance (m) at which people notice the fire.")]
        public float fireSightRange = 16f;
        [Tooltip("How fast panic spreads from person to person.")]
        public float panicContagion = 0.6f;
        [Tooltip("Panic passed on is scaled by this, so panic fades with distance from its source.")]
        [Range(0f, 1f)] public float contagionFalloff = 0.85f;
        public float panicDecay = 0.02f;
        [Tooltip("Crowd pressure (density x panic factor) above which people get hurt.")]
        public float crushThreshold = 8.5f;
        public float injuryRate = 0.08f;
        public float stewardRadius = 6f;
        public float stewardCalming = 0.5f;
    }

    public enum AgentState : byte { Active, Safe, Injured }

    /// <summary>
    /// The crowd: a few thousand people with panic, knowledge of exits and crush damage.
    /// Movement = flow-field steering + soft body separation, with density slowing people down.
    /// </summary>
    public class CrowdSimulation
    {
        const int W = FestivalVenue.Width, H = FestivalVenue.Height, N = FestivalVenue.CellCount;
        const int InstanceBatch = 1023;
        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        public readonly CrowdSettings settings;
        public readonly int count;
        public readonly Vector2[] position, velocity, anchor;
        public readonly float[] panic, damage, personalSpeed, stuckTime;
        public readonly AgentState[] state;
        public readonly bool[] informed, evacuating;

        // Spatial hash over the venue grid, rebuilt every step.
        readonly int[] agentCell, sorted;
        readonly int[] cellCount = new int[N], cellActive = new int[N], cellStart = new int[N], cellCursor = new int[N];
        readonly float[] cellPanic = new float[N];
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
        public int InjuredByCrush { get; private set; }
        public int InjuredByFire { get; private set; }

        readonly Matrix4x4[][] buckets;
        readonly int[] bucketCounts;

        public CrowdSimulation(CrowdSettings settings, FestivalVenue venue, System.Random rng)
        {
            this.settings = settings;
            count = settings.agentCount;
            position = new Vector2[count];
            velocity = new Vector2[count];
            anchor = new Vector2[count];
            panic = new float[count];
            damage = new float[count];
            personalSpeed = new float[count];
            stuckTime = new float[count];
            state = new AgentState[count];
            informed = new bool[count];
            evacuating = new bool[count];
            agentCell = new int[count];
            sorted = new int[count];
            buckets = new Matrix4x4[5][];
            for (int b = 0; b < buckets.Length; b++) buckets[b] = new Matrix4x4[count];
            bucketCounts = new int[buckets.Length];
            Spawn(venue, rng);
            UpdateCounts();
        }

        void Spawn(FestivalVenue venue, System.Random rng)
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

        public void Step(float dt, FestivalVenue venue, FlowField known, FlowField all, bool incident)
        {
            BuildSpatialHash();
            float diameter = settings.radius * 2f, diameterSq = diameter * diameter;
            float sight = settings.fireSightRange;

            for (int i = 0; i < count; i++)
            {
                if (state[i] != AgentState.Active) continue;
                Vector2 p = position[i];
                int c = agentCell[i], cx = c % W, cy = c / W;

                // Neighbours: body contact + who is panicking around me.
                Vector2 push = Vector2.zero;
                float panicSum = 0f;
                int panicN = 0;
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
                            if (dsq < diameterSq)
                            {
                                float dist = Mathf.Sqrt(dsq);
                                Vector2 n = dist > 1e-4f ? d / dist : new Vector2(Mathf.Cos(i), Mathf.Sin(i));
                                // The fallen can be stepped over, slowly.
                                push += n * ((diameter - dist) * (state[j] == AgentState.Injured ? 0.15f : 0.5f));
                            }
                            if (dsq < 2.25f)
                            {
                                panicSum += state[j] == AgentState.Injured ? 0.6f : panic[j];
                                panicN++;
                            }
                        }
                    }
                }

                float localDensity = density[c];
                float pv = panic[i];
                if (incident)
                {
                    int fd = venue.fireDistance[c];
                    if (fd < sight)
                    {
                        pv += 0.45f * (1f - fd / sight) * dt;
                        if (fd <= 2) pv += 0.6f * dt;
                        evacuating[i] = true;
                    }
                    if (panicN > 0)
                    {
                        float target = panicSum / panicN * settings.contagionFalloff;
                        if (target > pv) pv += (target - pv) * settings.panicContagion * dt;
                    }
                    if (localDensity > 4.5f) pv += 0.03f * (localDensity - 4.5f) * dt;
                    if (stuckTime[i] > 3f) pv += 0.05f * dt;
                    pv = Mathf.Clamp01(pv - settings.panicDecay * dt);
                    panic[i] = pv;
                    if (pv > 0.3f) evacuating[i] = true;
                    if (venue.seesOpenGate[c]) informed[i] = true;
                }

                Vector2 desired;
                if (!evacuating[i])
                {
                    // Still enjoying the show: drift back to my spot.
                    Vector2 to = anchor[i] - p;
                    float m = to.magnitude;
                    desired = m > 1.2f ? to * (0.6f / m) : Vector2.zero;
                }
                else
                {
                    // Panicked people forget directions and head for the way they came in.
                    bool useAll = informed[i] && pv < 0.8f;
                    FlowField field = useAll ? all : known;
                    if (!field.Reachable(c)) field = useAll ? known : all;
                    Vector2 dir;
                    if (field.Reachable(c) && field.direction[c] != Vector2.zero)
                        dir = (FestivalVenue.CellCenter(c) + field.direction[c] - p).normalized;
                    else
                        dir = velocity[i].sqrMagnitude > 0.01f ? velocity[i].normalized : Vector2.zero;

                    float slow = Mathf.Clamp(1.1f - 0.17f * localDensity, 0.12f, 1f);
                    slow = Mathf.Lerp(slow, 1f, 0.5f * pv); // panic makes people push on regardless
                    desired = dir * (settings.walkSpeed * personalSpeed[i] * (1f + settings.panicSpeedBoost * pv) * slow);
                }

                velocity[i] = Vector2.MoveTowards(velocity[i], desired, settings.acceleration * dt);
                // Panicked people give way less, so they compress the crowd around them.
                Vector2 next = p + velocity[i] * dt + Vector2.ClampMagnitude(push * Mathf.Lerp(1f, 0.55f, pv), 0.15f);
                next = ResolveWalls(venue, p, next);
                // Blocked people lose their momentum instead of building up endless push.
                velocity[i] = Vector2.Lerp(velocity[i], (next - p) / dt, 0.5f);

                float wanted = desired.magnitude;
                if (evacuating[i] && wanted > 0.2f && (next - p).magnitude / dt < 0.3f * wanted) stuckTime[i] += dt;
                else stuckTime[i] = Mathf.Max(0f, stuckTime[i] - 2f * dt);
                position[i] = next;

                int nextCell = CellOf(next);
                var type = venue.cells[nextCell];
                if (type == CellType.Outside)
                {
                    state[i] = AgentState.Safe;
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
        }

        void UpdateCounts()
        {
            int active = 0, safe = 0, injured = 0, panicked = 0, leaving = 0;
            for (int i = 0; i < count; i++)
            {
                switch (state[i])
                {
                    case AgentState.Active:
                        active++;
                        if (panic[i] > 0.7f) panicked++;
                        if (evacuating[i]) leaving++;
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
        }

        /// <summary>Stewards calm people near them and, once there is an incident, tell them where to go.</summary>
        public void ApplyStewards(List<Vector2> stewards, float dt, bool incident)
        {
            float r = settings.stewardRadius, rSq = r * r;
            foreach (var s in stewards)
            {
                int minX = Mathf.Max(0, Mathf.FloorToInt(s.x - r)), maxX = Mathf.Min(W - 1, Mathf.FloorToInt(s.x + r));
                int minY = Mathf.Max(0, Mathf.FloorToInt(s.y - r)), maxY = Mathf.Min(H - 1, Mathf.FloorToInt(s.y + r));
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int c = y * W + x, end = cellStart[c] + cellCount[c];
                        for (int k = cellStart[c]; k < end; k++)
                        {
                            int i = sorted[k];
                            if (state[i] != AgentState.Active || (position[i] - s).sqrMagnitude > rSq) continue;
                            float calming = settings.stewardCalming * (panic[i] > 0.9f ? 0.4f : 1f);
                            panic[i] = Mathf.Max(0f, panic[i] - calming * dt);
                            if (incident)
                            {
                                informed[i] = true;
                                evacuating[i] = true;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Everyone starts leaving; a share of those still listening learn about the open emergency exits.</summary>
        public int AnnounceEvacuation(System.Random rng, float reach)
        {
            int newlyInformed = 0;
            for (int i = 0; i < count; i++)
            {
                if (state[i] != AgentState.Active) continue;
                evacuating[i] = true;
                if (!informed[i] && panic[i] < 0.8f && rng.NextDouble() < reach)
                {
                    informed[i] = true;
                    newlyInformed++;
                }
                if (panic[i] < 0.5f) panic[i] += 0.1f; // an emergency announcement is alarming in itself
            }
            return newlyInformed;
        }

        public void AnnounceCalm(float strength)
        {
            for (int i = 0; i < count; i++)
                if (state[i] == AgentState.Active)
                    panic[i] = Mathf.Max(0f, panic[i] - strength * (panic[i] > 0.85f ? 0.35f : 1f));
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

        /// <summary>Draws everyone as instanced cubes. Params order: calm, informed, alarmed, panicked, injured.</summary>
        public void Render(Mesh mesh, RenderParams[] renderParams)
        {
            Array.Clear(bucketCounts, 0, bucketCounts.Length);
            var upright = new Vector3(0.42f, 1f, 0.42f);
            var lying = new Vector3(0.42f, 0.18f, 0.9f);
            for (int i = 0; i < count; i++)
            {
                if (state[i] == AgentState.Safe) continue;
                Vector2 p = position[i];
                int b;
                Matrix4x4 m;
                if (state[i] == AgentState.Injured)
                {
                    b = 4;
                    m = Matrix4x4.TRS(new Vector3(p.x, 0.09f, p.y), Quaternion.Euler(0f, i * 37f % 180f, 0f), lying);
                }
                else
                {
                    float pv = panic[i];
                    b = pv > 0.7f ? 3 : pv > 0.3f ? 2 : informed[i] && evacuating[i] ? 1 : 0;
                    m = Matrix4x4.TRS(new Vector3(p.x, 0.5f, p.y), Quaternion.identity, upright);
                }
                buckets[b][bucketCounts[b]++] = m;
            }
            for (int b = 0; b < buckets.Length; b++)
                for (int start = 0; start < bucketCounts[b]; start += InstanceBatch)
                    Graphics.RenderMeshInstanced(renderParams[b], mesh, 0, buckets[b], Mathf.Min(InstanceBatch, bucketCounts[b] - start), start);
        }
    }
}
