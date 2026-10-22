using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    public enum CellType : byte { Open, Wall, Obstacle, Barrier, Gate, Outside, Fire }

    public class Gate
    {
        public string name;
        public RectInt rect;
        public bool open;      // what the player asked for
        public float progress; // 0 = closed, 1 = fully open
        public bool Passable => progress >= 1f;
    }

    /// <summary>A way out through the fence: an entrance (always open) or an emergency gate.</summary>
    public class Door
    {
        public string name;
        public RectInt rect;
        public Gate gate; // null for entrances
        public FlowField field;
        public bool dirty = true;
        /// <summary>Inside cells right in front of the door, where people find out it's shut.</summary>
        public readonly List<int> approach = new();
        /// <summary>Safe-zone cells right behind the door.</summary>
        public readonly List<int> beyond = new();

        public bool IsOpen => gate == null || gate.Passable;
        public Vector2 Center => rect.center;
        public int Width => Mathf.Max(rect.width, rect.height);
    }

    public class Landmark
    {
        public string name;
        public RectInt rect;
        public Color color;
        public float height;
        public bool solid;
    }

    public class FlowField
    {
        public readonly float[] distance;
        public readonly Vector2[] direction;
        public FlowField(int size) { distance = new float[size]; direction = new Vector2[size]; }
        public bool Reachable(int cell) => !float.IsPositiveInfinity(distance[cell]);
    }

    /// <summary>
    /// Grid model of the festival grounds (1 cell = 1 m²): layout, doors, fire, and the route fields people follow.
    /// Everything outside the fence is the safe zone.
    /// </summary>
    public class FestivalVenue
    {
        public const int Width = 104, Height = 76, CellCount = Width * Height;
        public const int MaxFireDistance = 32;
        const int FenceLeft = 3, FenceRight = Width - 4, FenceBottom = 3, FenceTop = Height - 4;
        const float GateOpenTime = 2.5f, GateCloseTime = 1.5f, FireSpreadInterval = 0.8f;

        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };
        static readonly float[] StepLength = { 1f, 1f, 1f, 1f, 1.4142f, 1.4142f, 1.4142f, 1.4142f };

        public readonly CellType[] cells = new CellType[CellCount];
        public readonly sbyte[] gateOf = new sbyte[CellCount];
        public readonly bool[] flammable = new bool[CellCount];
        public readonly float[] blockHeight = new float[CellCount];
        public readonly int[] fireDistance = new int[CellCount];
        /// <summary>Per cell: the step that gets you further from the fire (zero when there's no fire nearby).</summary>
        public readonly Vector2[] fleeDirection = new Vector2[CellCount];
        public readonly bool[] seesOpenGate = new bool[CellCount];
        public readonly List<Gate> gates = new();
        public readonly List<Door> doors = new();
        public readonly List<Landmark> landmarks = new();
        public readonly List<(string name, Vector2Int cell)> fireSites = new();
        public readonly List<(RectInt rect, float weight, bool stageFacing)> crowdZones = new();
        public readonly List<int> fireCells = new();
        /// <summary>Spot fires started by embers so far, and the cell of the latest one.</summary>
        public int SpotFires { get; private set; }
        public int LastSpotFireCell { get; private set; } = -1;

        readonly MinHeap heap = new(CellCount * 2);
        readonly int[] queue = new int[CellCount];
        readonly int[] parent = new int[CellCount];
        readonly bool[] isSource = new bool[CellCount];
        float fireTimer;

        public FestivalVenue()
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = Index(x, y);
                    bool outside = x < FenceLeft || x > FenceRight || y < FenceBottom || y > FenceTop;
                    bool fence = !outside && (x == FenceLeft || x == FenceRight || y == FenceBottom || y == FenceTop);
                    cells[i] = outside ? CellType.Outside : fence ? CellType.Wall : CellType.Open;
                    blockHeight[i] = fence ? 1.2f : 0f;
                    gateOf[i] = -1;
                    fireDistance[i] = MaxFireDistance;
                }
            }

            // Entrances: always open, narrow like ticket lanes.
            AddEntrance("Main Entrance", new RectInt(50, 3, 4, 1));
            AddEntrance("West Entrance", new RectInt(3, 23, 1, 2));

            // Emergency gates: wider, but closed until the player opens them.
            AddGate("East Gate A", new RectInt(100, 47, 1, 5));
            AddGate("East Gate B", new RectInt(100, 16, 1, 5));
            AddGate("North Gate", new RectInt(14, 72, 5, 1));
            AddGate("Backstage Gate", new RectInt(84, 72, 5, 1));
            AddGate("West Gate", new RectInt(3, 52, 1, 5));
            AddGate("South Gate", new RectInt(80, 3, 5, 1));

            AddBlock("Main Stage", new RectInt(36, 63, 32, 9), new Color(0.22f, 0.2f, 0.3f), 3f, true);
            AddBlock("Sound Desk", new RectInt(49, 40, 6, 4), new Color(0.15f, 0.15f, 0.17f), 2.5f, false);
            AddBlock("Light Tower", new RectInt(30, 46, 2, 2), new Color(0.2f, 0.2f, 0.22f), 6f, false);
            AddBlock("Light Tower", new RectInt(72, 46, 2, 2), new Color(0.2f, 0.2f, 0.22f), 6f, false);
            var stall = new Color(0.65f, 0.42f, 0.22f);
            foreach (int sx in new[] { 10, 17, 24 })
            {
                AddBlock("Food Court", new RectInt(sx, 9, 4, 3), stall, 2f, true);
                AddBlock("Food Court", new RectInt(sx, 17, 4, 3), stall, 2f, true);
            }
            AddBlock("Bar Tent", new RectInt(82, 30, 11, 7), new Color(0.2f, 0.45f, 0.5f), 2.5f, true);
            AddBlock("Toilets", new RectInt(86, 8, 8, 4), new Color(0.25f, 0.35f, 0.7f), 2.2f, false);
            AddBlock("Merch Tent", new RectInt(64, 10, 8, 5), new Color(0.5f, 0.3f, 0.55f), 2.2f, true);
            AddBlock("Dance Tent", new RectInt(7, 34, 9, 10), new Color(0.55f, 0.2f, 0.45f), 3f, true);
            AddBlock("Generator", new RectInt(93, 58, 3, 3), new Color(0.6f, 0.55f, 0.2f), 1.8f, true);

            // Where an incident can start: an open cell next to something that burns.
            fireSites.Add(("the Food Court", new Vector2Int(21, 18)));
            fireSites.Add(("the Bar Tent", new Vector2Int(81, 33)));
            fireSites.Add(("the Generator", new Vector2Int(92, 59)));
            fireSites.Add(("Stage Left", new Vector2Int(35, 64)));
            fireSites.Add(("the Dance Tent", new Vector2Int(16, 39)));
            fireSites.Add(("the Merch Tent", new Vector2Int(68, 15)));

            crowdZones.Add((new RectInt(22, 46, 60, 17), 0.62f, true)); // in front of the stage
            crowdZones.Add((new RectInt(8, 6, 24, 18), 0.12f, false));  // food court
            crowdZones.Add((new RectInt(74, 24, 24, 18), 0.1f, false));  // bar
            crowdZones.Add((new RectInt(4, 4, 96, 68), 0.16f, false));   // anywhere

            foreach (var door in doors)
            {
                door.field = new FlowField(CellCount);
                foreach (var p in door.rect.allPositionsWithin)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        int x = p.x + DX[k], y = p.y + DY[k];
                        if (!InBounds(x, y) || door.rect.Contains(new Vector2Int(x, y))) continue;
                        int n = Index(x, y);
                        if (cells[n] == CellType.Outside) door.beyond.Add(n);
                        else if (cells[n] == CellType.Open) door.approach.Add(n);
                    }
                }
            }
            UpdateDoorFields(doors.Count);
        }

        void AddEntrance(string name, RectInt rect)
        {
            foreach (var p in rect.allPositionsWithin)
            {
                cells[Index(p.x, p.y)] = CellType.Open;
                blockHeight[Index(p.x, p.y)] = 0f;
            }
            landmarks.Add(new Landmark { name = name, rect = rect, solid = false });
            doors.Add(new Door { name = name, rect = rect });
        }

        void AddGate(string name, RectInt rect)
        {
            foreach (var p in rect.allPositionsWithin)
            {
                cells[Index(p.x, p.y)] = CellType.Gate;
                gateOf[Index(p.x, p.y)] = (sbyte)gates.Count;
            }
            var gate = new Gate { name = name, rect = rect };
            gates.Add(gate);
            doors.Add(new Door { name = name, rect = rect, gate = gate });
        }

        void AddBlock(string name, RectInt rect, Color color, float height, bool burns)
        {
            foreach (var p in rect.allPositionsWithin)
            {
                int i = Index(p.x, p.y);
                cells[i] = CellType.Obstacle;
                flammable[i] = burns;
                blockHeight[i] = height;
            }
            landmarks.Add(new Landmark { name = name, rect = rect, color = color, height = height, solid = true });
        }

        public static int Index(int x, int y) => y * Width + x;
        public static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
        public static Vector2 CellCenter(int i) => new(i % Width + 0.5f, i / Width + 0.5f);
        public static int CellAt(Vector2 p) =>
            Index(Mathf.Clamp(Mathf.FloorToInt(p.x), 0, Width - 1), Mathf.Clamp(Mathf.FloorToInt(p.y), 0, Height - 1));

        public bool IsWalkable(int i) => cells[i] switch
        {
            CellType.Open or CellType.Outside => true,
            CellType.Gate => gates[gateOf[i]].Passable,
            _ => false,
        };

        public bool IsWalkableAt(int x, int y) => InBounds(x, y) && IsWalkable(Index(x, y));

        /// <summary>Bit d set when doors[d] is open right now.</summary>
        public int OpenDoorMask
        {
            get
            {
                int mask = 0;
                for (int d = 0; d < doors.Count; d++)
                    if (doors[d].IsOpen) mask |= 1 << d;
                return mask;
            }
        }

        public bool TryPlaceBarrier(int i)
        {
            if (cells[i] != CellType.Open) return false;
            cells[i] = CellType.Barrier;
            MarkRoutesDirty();
            return true;
        }

        public bool TryRemoveBarrier(int i)
        {
            if (cells[i] != CellType.Barrier) return false;
            cells[i] = CellType.Open;
            MarkRoutesDirty();
            return true;
        }

        public Gate GateNear(int cell)
        {
            var p = new Vector2Int(cell % Width, cell / Width);
            foreach (var g in gates)
                if (new RectInt(g.rect.xMin - 1, g.rect.yMin - 1, g.rect.width + 2, g.rect.height + 2).Contains(p))
                    return g;
            return null;
        }

        /// <summary>Animates gates; fills <paramref name="changed"/> with gates whose passability flipped.</summary>
        public bool UpdateGates(float dt, List<Gate> changed)
        {
            changed.Clear();
            foreach (var g in gates)
            {
                bool was = g.Passable;
                g.progress = Mathf.MoveTowards(g.progress, g.open ? 1f : 0f, dt / (g.open ? GateOpenTime : GateCloseTime));
                if (was != g.Passable) changed.Add(g);
            }
            if (changed.Count == 0) return false;
            RecomputeGateSight();
            MarkRoutesDirty();
            return true;
        }

        void RecomputeGateSight()
        {
            const int range = 10;
            Array.Clear(seesOpenGate, 0, CellCount);
            foreach (var g in gates)
            {
                if (!g.Passable) continue;
                for (int y = g.rect.yMin - range; y < g.rect.yMax + range; y++)
                    for (int x = g.rect.xMin - range; x < g.rect.xMax + range; x++)
                        if (InBounds(x, y)) seesOpenGate[Index(x, y)] = true;
            }
        }

        // ---------------------------------------------------------------- Fire

        public void StartFire(Vector2Int origin)
        {
            Ignite(Index(origin.x, origin.y));
            for (int k = 0; k < 4; k++)
            {
                int x = origin.x + DX[k], y = origin.y + DY[k];
                if (InBounds(x, y) && flammable[Index(x, y)]) Ignite(Index(x, y));
            }
            RecomputeFireDistance();
        }

        bool Ignite(int i)
        {
            var type = cells[i];
            if (type != CellType.Open && type != CellType.Obstacle && type != CellType.Barrier) return false;
            cells[i] = CellType.Fire;
            fireCells.Add(i);
            return true;
        }

        public bool UpdateFire(float dt, System.Random rng, int maxCells)
        {
            if (fireCells.Count == 0) return false;
            fireTimer += dt;
            if (fireTimer < FireSpreadInterval) return false;
            fireTimer -= FireSpreadInterval;

            bool changed = false;
            int burning = fireCells.Count;
            for (int f = 0; f < burning && fireCells.Count < maxCells; f++)
            {
                int c = fireCells[f], cx = c % Width, cy = c / Width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + DX[k], ny = cy + DY[k];
                    if (!InBounds(nx, ny)) continue;
                    int n = Index(nx, ny);
                    float chance = cells[n] switch
                    {
                        CellType.Obstacle => flammable[n] ? 0.5f : 0.03f,
                        CellType.Barrier => 0.2f,
                        CellType.Open => 0.07f, // dry grass, litter, dropped drinks cups...
                        _ => 0f,
                    };
                    if (chance > 0f && rng.NextDouble() < chance && Ignite(n)) changed = true;
                }
            }

            // Embers: burning debris carried a few metres starts new spot fires. Bigger fire, more embers.
            float emberChance = Mathf.Min(0.45f, fireCells.Count / 60f);
            if (fireCells.Count < maxCells && rng.NextDouble() < emberChance)
            {
                int source = fireCells[rng.Next(fireCells.Count)];
                double angle = rng.NextDouble() * Math.PI * 2.0;
                int distance = rng.Next(3, 8);
                int x = source % Width + (int)Math.Round(Math.Cos(angle) * distance);
                int y = source / Width + (int)Math.Round(Math.Sin(angle) * distance);
                if (InBounds(x, y) && Ignite(Index(x, y)))
                {
                    SpotFires++;
                    LastSpotFireCell = Index(x, y);
                    changed = true;
                }
            }
            if (changed) RecomputeFireDistance();
            return changed;
        }

        void RecomputeFireDistance()
        {
            Array.Fill(fireDistance, MaxFireDistance);
            int head = 0, tail = 0;
            foreach (int c in fireCells)
            {
                fireDistance[c] = 0;
                queue[tail++] = c;
            }
            while (head < tail)
            {
                int c = queue[head++];
                int d = fireDistance[c] + 1;
                if (d >= MaxFireDistance) continue;
                int cx = c % Width, cy = c / Width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + DX[k], ny = cy + DY[k];
                    if (!InBounds(nx, ny)) continue;
                    int n = Index(nx, ny);
                    if (fireDistance[n] <= d) continue;
                    fireDistance[n] = d;
                    queue[tail++] = n;
                }
            }

            // Running away: step to the walkable neighbour furthest from the flames.
            for (int c = 0; c < CellCount; c++)
            {
                fleeDirection[c] = Vector2.zero;
                if (fireDistance[c] >= MaxFireDistance - 1 || !IsWalkable(c)) continue;
                int cx = c % Width, cy = c / Width, bestK = -1, best = fireDistance[c];
                for (int k = 0; k < 8; k++)
                {
                    if (CanStep(cx, cy, k, out int n) && fireDistance[n] > best)
                    {
                        best = fireDistance[n];
                        bestK = k;
                    }
                }
                if (bestK >= 0) fleeDirection[c] = new Vector2(DX[bestK], DY[bestK]).normalized;
            }
            MarkRoutesDirty();
        }

        // ---------------------------------------------------------------- Routes

        public void MarkRoutesDirty()
        {
            foreach (var door in doors) door.dirty = true;
        }

        /// <summary>Recomputes up to <paramref name="max"/> stale door fields (spread over steps to avoid spikes).</summary>
        public int UpdateDoorFields(int max)
        {
            int done = 0;
            foreach (var door in doors)
            {
                if (done >= max) break;
                if (!door.dirty) continue;
                ComputeDoorField(door);
                door.dirty = false;
                done++;
            }
            return done;
        }

        bool CanStep(int cx, int cy, int k, out int n)
        {
            n = -1;
            int nx = cx + DX[k], ny = cy + DY[k];
            if (!InBounds(nx, ny)) return false;
            n = Index(nx, ny);
            // Routes stay inside the fence; only a door's own safe-zone cells count as outside.
            if (!IsWalkable(n) || (cells[n] == CellType.Outside && !isSource[n])) return false;
            // No cutting corners diagonally past walls.
            return k < 4 || (IsWalkable(Index(nx, cy)) && IsWalkable(Index(cx, ny)));
        }

        /// <summary>
        /// Route to one door. An open door leads through to the safe zone behind it; a closed one only up to the
        /// front of it, which is where people discover it's shut. Cells near the fire cost more.
        /// </summary>
        void ComputeDoorField(Door door)
        {
            var dist = door.field.distance;
            Array.Fill(dist, float.PositiveInfinity);
            heap.Clear();
            var sources = door.IsOpen ? door.beyond : door.approach;
            foreach (int s in sources)
            {
                if (!IsWalkable(s)) continue;
                isSource[s] = true;
                dist[s] = 0f;
                heap.Push(s, 0f);
            }

            while (heap.TryPop(out int c, out float d))
            {
                if (d > dist[c]) continue;
                int cx = c % Width, cy = c / Width;
                for (int k = 0; k < 8; k++)
                {
                    if (!CanStep(cx, cy, k, out int n)) continue;
                    int fd = fireDistance[n];
                    float extra = fd < 5 ? (5 - fd) * 2f : 0f;
                    float nd = d + StepLength[k] * (1f + extra);
                    if (nd < dist[n])
                    {
                        dist[n] = nd;
                        heap.Push(n, nd);
                    }
                }
            }

            for (int c = 0; c < CellCount; c++)
            {
                door.field.direction[c] = Vector2.zero;
                if (float.IsPositiveInfinity(dist[c]) || isSource[c]) continue;
                int cx = c % Width, cy = c / Width, bestK = -1;
                float best = dist[c];
                for (int k = 0; k < 8; k++)
                {
                    if (CanStep(cx, cy, k, out int n) && dist[n] < best)
                    {
                        best = dist[n];
                        bestK = k;
                    }
                }
                if (bestK >= 0) door.field.direction[c] = new Vector2(DX[bestK], DY[bestK]).normalized;
            }
            foreach (int s in sources) isSource[s] = false;
        }

        /// <summary>Shortest walkable path (cell centres, ending exactly at <paramref name="to"/>), or null.</summary>
        public List<Vector2> FindPath(Vector2 from, Vector2 to)
        {
            int start = CellAt(from), goal = CellAt(to);
            if (!IsWalkable(goal) || cells[goal] == CellType.Outside) return null;

            Array.Fill(parent, -1);
            parent[start] = start;
            int head = 0, tail = 0;
            queue[tail++] = start;
            while (head < tail && parent[goal] == -1)
            {
                int c = queue[head++], cx = c % Width, cy = c / Width;
                for (int k = 0; k < 8; k++)
                {
                    if (!CanStep(cx, cy, k, out int n) || parent[n] != -1) continue;
                    parent[n] = c;
                    queue[tail++] = n;
                }
            }
            if (parent[goal] == -1) return null;

            var path = new List<Vector2>();
            for (int c = goal; c != start; c = parent[c]) path.Add(CellCenter(c));
            path.Reverse();
            if (path.Count > 0) path[^1] = to;
            else path.Add(to);
            return path;
        }

        public string DescribeLocation(int cell)
        {
            Vector2 p = CellCenter(cell);
            string best = "the open field";
            float bestSq = 14f * 14f;
            foreach (var l in landmarks)
            {
                float d = SqrDistance(p, l.rect);
                if (d < bestSq) { bestSq = d; best = l.name; }
            }
            foreach (var g in gates)
            {
                float d = SqrDistance(p, g.rect);
                if (d < bestSq) { bestSq = d; best = g.name; }
            }
            return best;
        }

        static float SqrDistance(Vector2 p, RectInt r)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
            float dy = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
            return dx * dx + dy * dy;
        }

        class MinHeap
        {
            int[] items;
            float[] keys;
            int count;

            public MinHeap(int capacity)
            {
                items = new int[capacity];
                keys = new float[capacity];
            }

            public void Clear() => count = 0;

            public void Push(int item, float key)
            {
                if (count == items.Length)
                {
                    Array.Resize(ref items, count * 2);
                    Array.Resize(ref keys, count * 2);
                }
                int i = count++;
                while (i > 0)
                {
                    int up = (i - 1) >> 1;
                    if (keys[up] <= key) break;
                    items[i] = items[up];
                    keys[i] = keys[up];
                    i = up;
                }
                items[i] = item;
                keys[i] = key;
            }

            public bool TryPop(out int item, out float key)
            {
                if (count == 0)
                {
                    item = 0;
                    key = 0f;
                    return false;
                }
                item = items[0];
                key = keys[0];
                if (--count == 0) return true;

                int lastItem = items[count];
                float lastKey = keys[count];
                int i = 0;
                while (true)
                {
                    int child = 2 * i + 1;
                    if (child >= count) break;
                    if (child + 1 < count && keys[child + 1] < keys[child]) child++;
                    if (keys[child] >= lastKey) break;
                    items[i] = items[child];
                    keys[i] = keys[child];
                    i = child;
                }
                items[i] = lastItem;
                keys[i] = lastKey;
                return true;
            }
        }
    }
}
