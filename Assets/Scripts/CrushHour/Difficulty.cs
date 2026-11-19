using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    public enum Difficulty { Easy, Medium, Hard }

    /// <summary>
    /// What a difficulty changes: crowd size, share of drunk people, how many exits can get blocked, how many main
    /// disasters a round has, and the score multiplier. Pure data (no Unity scene API), so it runs headless.
    /// </summary>
    public sealed class DifficultySettings
    {
        public Difficulty difficulty;
        public int playerCount;
        public float drunkFraction;
        public int maxBlockedDoors;
        /// <summary>Main disasters per round (fires, tower collapses, gas leaks); blocked doors and chained collapses come on top.</summary>
        public int disasterCount;
        public float scoreMultiplier;
        /// <summary>Zone patrols the player gets (they start at the posts of the busiest zones; the rest is uncovered).</summary>
        public int patrolCount;
        /// <summary>Gate crews: how many emergency gates can be open (or opening) at once.</summary>
        public int gateCrews;

        public string Name => difficulty.ToString();

        public static DifficultySettings For(Difficulty d) => d switch
        {
            Difficulty.Easy => new DifficultySettings { difficulty = d, playerCount = 600, drunkFraction = 0.01f, maxBlockedDoors = 1, disasterCount = 1, scoreMultiplier = 1f, patrolCount = 6, gateCrews = 4 },
            Difficulty.Medium => new DifficultySettings { difficulty = d, playerCount = 1400, drunkFraction = 0.02f, maxBlockedDoors = 2, disasterCount = 2, scoreMultiplier = 1.5f, patrolCount = 5, gateCrews = 3 },
            _ => new DifficultySettings { difficulty = Difficulty.Hard, playerCount = 2000, drunkFraction = 0.02f, maxBlockedDoors = 3, disasterCount = 3, scoreMultiplier = 2f, patrolCount = 4, gateCrews = 3 },
        };

        // Star thresholds (shared by the game, the menu text and the headless tests).
        public const float OneStarSafe = 0.95f, TwoStarInjured = 0.01f, TwoStarTime = 0.5f;
        public const float ThreeStarInjured = 0.0025f, ThreeStarDrunkLeft = 0.34f, ThreeStarTime = 0.4f;

        /// <summary>The star rules in two short lines (menu difficulty page).</summary>
        public static string StarSummary =>
            $"1 star: {OneStarSafe * 100f:0}% safe.  2 stars: injuries at most {TwoStarInjured * 100f:0}%, out within {TwoStarTime * 240f:0} s.\n" +
            $"3 stars: nobody sober left, injuries at most {ThreeStarInjured * 100f:0.##}%, {(1f - ThreeStarDrunkLeft) * 100f:0}% of drunk fans out, within {ThreeStarTime * 240f:0} s.";

        /// <summary>
        /// Stars for a finished round: 1 = <see cref="OneStarSafe"/> of the crowd safe; 2 = also injuries ≤ 1% and done
        /// within half the time limit; 3 = also nobody sober left inside, injuries ≤ 0.25%, at most a third of the drunk
        /// fans left behind, and done within 40% of the time limit. Times count from the alarm.
        /// </summary>
        public static int Stars(int count, int safe, int injured, int soberInside, int drunkLeft, int drunkTotal, float evacuationTime, float timeLimit)
        {
            int stars = 0;
            if (safe >= count * OneStarSafe) stars = 1;
            if (stars == 1 && injured <= count * TwoStarInjured && evacuationTime <= timeLimit * TwoStarTime) stars = 2;
            if (stars == 2 && soberInside == 0 && injured <= count * ThreeStarInjured && drunkLeft <= drunkTotal * ThreeStarDrunkLeft &&
                evacuationTime <= timeLimit * ThreeStarTime) stars = 3;
            return stars;
        }

        /// <summary>
        /// Round score (shared by the game and the headless tests): +10 per person safe, -100 per injured, -25 per
        /// person still inside, -50 per drunk left inside, -10 per panic attack, +10 per second left on the clock;
        /// then × <see cref="scoreMultiplier"/>.
        /// </summary>
        public int Score(int safe, int injured, int trapped, int drunkInside, int attacks, float evacuationTime, float timeLimit)
        {
            int raw = safe * 10 - injured * 100 - trapped * 25 - drunkInside * 50 - attacks * 10
                      + Mathf.Max(0, Mathf.RoundToInt(timeLimit - evacuationTime)) * 10;
            return Mathf.RoundToInt(raw * scoreMultiplier);
        }
    }

    /// <summary>
    /// Builds a random round for a difficulty (a new one every game; the game passes its freshly seeded rng):
    /// <list type="bullet">
    /// <item><see cref="DifficultySettings.disasterCount"/> main disasters, each a random type (fire, tower collapse, gas
    /// leak) at a random place, never two of the same type at the same place. The first starts 20-40 s into the show,
    /// each next one 20-40 s after the one before.</item>
    /// <item>0 to <see cref="DifficultySettings.maxBlockedDoors"/> blocked doors (random door and reason), each at a random
    /// moment in the first 60 s. Never both entrances, and at least 3 doors always stay unblocked.</item>
    /// <item>In a round with a fire (or a gas leak, which can catch fire), every light tower that isn't already a main
    /// disaster is chained to the flames (<see cref="TowerCollapseIncident.WhenFireNear"/>).</item>
    /// </list>
    /// </summary>
    public static class RoundGenerator
    {
        const int MinUnblockedDoors = 3;
        public const float FirstDisasterMin = 20f, FirstDisasterSpread = 20f;
        enum Kind { Fire, Tower, Gas }

        public static void Build(Difficulty difficulty, FestivalVenue venue, System.Random rng, IncidentSchedule schedule) =>
            Build(DifficultySettings.For(difficulty), venue, rng, schedule);

        public static void Build(DifficultySettings settings, FestivalVenue venue, System.Random rng, IncidentSchedule schedule)
        {
            // Every (type, place) still free.
            var free = new Dictionary<Kind, List<int>>
            {
                [Kind.Fire] = Range(venue.fireSites.Count),
                [Kind.Tower] = Range(venue.lightTowers.Count),
                [Kind.Gas] = Range(GasLeakIncident.Sites.Length),
            };
            var towersUsed = new HashSet<int>();
            bool fireRisk = false;
            // The first disaster comes 20-40 s in (was 4-12 s), so there is a calm spell to set up, and early warning
            // signs (smoke, a creaking tower) have time to show. Same rng calls as before, so seeds build the same rounds.
            float time = FirstDisasterMin + (float)rng.NextDouble() * FirstDisasterSpread;
            for (int n = 0; n < settings.disasterCount; n++)
            {
                var kinds = new List<Kind>();
                foreach (var kv in free)
                    if (kv.Value.Count > 0) kinds.Add(kv.Key);
                if (kinds.Count == 0) break;
                // Fixed order of kinds (Fire, Tower, Gas), so a seed always builds the same round.
                kinds.Sort();
                var kind = kinds[rng.Next(kinds.Count)];
                var places = free[kind];
                int pick = rng.Next(places.Count);
                int place = places[pick];
                places.RemoveAt(pick);

                Incident incident;
                switch (kind)
                {
                    case Kind.Fire:
                        incident = new FireIncident(rng, place) { siteName = venue.fireSites[place].name };
                        fireRisk = true;
                        break;
                    case Kind.Tower:
                        incident = TowerCollapseIncident.RandomDirection(venue, place, rng);
                        towersUsed.Add(place);
                        break;
                    default:
                        incident = new GasLeakIncident(rng, GasLeakIncident.Sites[place]) { siteName = GasLeakIncident.Sites[place] };
                        fireRisk = true;
                        break;
                }
                incident.startTime = time;
                schedule.Add(incident);
                time += 20f + (float)rng.NextDouble() * 20f;
            }

            // Blocked doors.
            int blockCount = rng.Next(settings.maxBlockedDoors + 1);
            blockCount = Mathf.Min(blockCount, venue.doors.Count - MinUnblockedDoors);
            var candidates = new List<Door>(venue.doors);
            for (int n = 0; n < blockCount && candidates.Count > 0; n++)
            {
                var door = candidates[rng.Next(candidates.Count)];
                candidates.Remove(door);
                if (door.gate == null) candidates.RemoveAll(d => d.gate == null); // never both entrances
                var block = new BlockDoorIncident(venue, door, BlockDoorIncident.Reasons[rng.Next(BlockDoorIncident.Reasons.Length)])
                {
                    startTime = (float)rng.NextDouble() * 60f,
                };
                schedule.Add(block);
            }

            // Chained collapses.
            if (fireRisk)
                for (int t = 0; t < venue.lightTowers.Count; t++)
                    if (!towersUsed.Contains(t)) schedule.Add(TowerCollapseIncident.WhenFireNear(venue, t, rng));
        }

        /// <summary>A "find it on the cameras" side task (control room only): what, where (zone index) and when (s after the show starts).</summary>
        public struct CameraTaskPlan
        {
            public bool lostChild;   // false = a fight
            public int region;
            public float time;
        }

        /// <summary>
        /// 0–2 camera tasks for a round: Easy 0–1, Medium 1–2, Hard 2. Two tasks are of different kinds and in different
        /// zones. A lost child is reported 15–50 s into the show, a fight 25–80 s. Uses its own random stream (seeded
        /// from <paramref name="seed"/>), so it never changes the disasters of a round.
        /// </summary>
        public static List<CameraTaskPlan> BuildCameraTasks(DifficultySettings settings, int zoneCount, int seed)
        {
            var rng = new System.Random(seed);
            var tasks = new List<CameraTaskPlan>();
            if (zoneCount <= 0) return tasks;
            int count = settings.difficulty switch
            {
                Difficulty.Easy => rng.Next(0, 2),
                Difficulty.Medium => rng.Next(1, 3),
                _ => 2,
            };
            bool child = rng.Next(2) == 0;
            int firstZone = -1;
            for (int t = 0; t < count; t++, child = !child)
            {
                int zone = rng.Next(zoneCount);
                if (zone == firstZone && zoneCount > 1) zone = (zone + 1 + rng.Next(zoneCount - 1)) % zoneCount;
                if (t == 0) firstZone = zone;
                float time = child ? 15f + (float)rng.NextDouble() * 35f : 25f + (float)rng.NextDouble() * 55f;
                tasks.Add(new CameraTaskPlan { lostChild = child, region = zone, time = time });
            }
            return tasks;
        }

        // ---------------------------------------------------------------- Evening phases (control room, week 8)

        /// <summary>DOORS OPEN, WARM-UP, HEADLINER, ENCORE + FIREWORKS, EXODUS.</summary>
        public const int PhaseCount = 5, Doors = 0, WarmUp = 1, Headliner = 2, Encore = 3, Exodus = 4;
        public static readonly string[] PhaseNames = { "DOORS OPEN", "WARM-UP", "HEADLINER", "ENCORE + FIREWORKS", "EXODUS" };
        public static readonly string[] PhaseShort = { "DOORS", "WARM-UP", "HEADLINER", "ENCORE", "EXODUS" };

        public enum MomentKind { TicketWave, GateCrush, BarRush, Rain, Drop, MoshPit, Surfer, Fireworks, StrayRocket, LostChild, Fight }

        /// <summary>
        /// Something that happens at a set time of the evening. <see cref="time"/> is seconds after the show starts (for a
        /// drop: when the countdown starts; the crowd surges <see cref="DropWarning"/> s later). <see cref="region"/> is the
        /// zone, <see cref="spot"/> a door (gate crush) or landmark (rain shelter) index, else -1.
        /// </summary>
        public struct MomentPlan
        {
            public MomentKind kind;
            public int phase, region, spot;
            public float time;
        }

        public sealed class EveningPlan
        {
            /// <summary>When each phase starts (s after the show starts); EXODUS = after ENCORE (or as soon as an evacuation starts).</summary>
            public readonly float[] phaseStart = new float[PhaseCount];
            /// <summary>The phase the round's first disaster happens in, and when.</summary>
            public int disasterPhase;
            public float disasterTime;
            public readonly List<MomentPlan> moments = new();
        }

        /// <summary>Seconds of warning (countdown on the wall) before a drop.</summary>
        public const float DropWarning = 20f;

        /// <summary>
        /// The evening of a control room round: phase start times from <paramref name="durations"/> (DOORS, WARM-UP,
        /// HEADLINER, ENCORE), the moments of each phase and the phase of the first disaster (Easy: WARM-UP, Medium:
        /// HEADLINER, Hard: ENCORE). Its own random stream, like <see cref="BuildCameraTasks"/>.
        /// </summary>
        public static EveningPlan BuildEvening(DifficultySettings settings, FestivalVenue venue, float[] durations, int seed)
        {
            var rng = new System.Random(seed);
            var plan = new EveningPlan();
            float t = 0f;
            for (int ph = 0; ph < PhaseCount; ph++)
            {
                plan.phaseStart[ph] = t;
                if (ph < durations.Length) t += Mathf.Max(10f, durations[ph]);
            }
            float Dur(int ph) => plan.phaseStart[ph + 1] - plan.phaseStart[ph];
            float At(int ph, float from, float to) => plan.phaseStart[ph] + Dur(ph) * (from + (float)rng.NextDouble() * (to - from));
            int level = settings.difficulty switch { Difficulty.Easy => 0, Difficulty.Medium => 1, _ => 2 };
            int stage = EveningSpots.StageRegion(venue);

            // DOORS OPEN: four waves through the entrances; on one of them the crowd at a gate gets dangerous.
            var entrances = new List<int>();
            for (int d = 0; d < venue.doors.Count; d++) if (venue.doors[d].gate == null) entrances.Add(d);
            for (int w = 0; w < 4; w++)
                plan.moments.Add(new MomentPlan { kind = MomentKind.TicketWave, phase = Doors, time = plan.phaseStart[Doors] + Dur(Doors) * (0.06f + 0.23f * w), region = -1, spot = -1 });
            if (entrances.Count > 0 && (level > 0 || rng.Next(2) == 0))
            {
                int door = entrances[rng.Next(entrances.Count)];
                int wave = 1 + rng.Next(2);
                plan.moments.Add(new MomentPlan
                {
                    kind = MomentKind.GateCrush, phase = Doors, spot = door,
                    region = EveningSpots.RegionNear(venue, venue.doors[door].Center),
                    time = plan.phaseStart[Doors] + Dur(Doors) * (0.06f + 0.23f * wave) + 4f,
                });
            }

            // WARM-UP: the rush for drinks before the headliner, and (one round in two) a downpour.
            var bars = EveningSpots.BarRegions(venue);
            if (bars.Count > 0)
                plan.moments.Add(new MomentPlan { kind = MomentKind.BarRush, phase = WarmUp, region = bars[rng.Next(bars.Count)], spot = -1, time = At(WarmUp, 0.1f, 0.4f) });
            var shelters = EveningSpots.Shelters(venue);
            if (shelters.Count > 0 && rng.Next(2) == 0)
            {
                int shelter = shelters[rng.Next(shelters.Count)];
                plan.moments.Add(new MomentPlan
                {
                    kind = MomentKind.Rain, phase = WarmUp, spot = shelter,
                    region = EveningSpots.RegionNear(venue, venue.landmarks[shelter].rect.center),
                    time = At(WarmUp, 0.5f, 0.75f),
                });
            }

            // HEADLINER: 2–3 drops, a mosh pit, a crowd surfer (Easy: one of the two). They overlap.
            int drops = level == 2 ? 3 : 2;
            for (int k = 0; k < drops; k++)
            {
                float slot = (k + 0.15f + 0.5f * (float)rng.NextDouble()) / drops;
                float time = plan.phaseStart[Headliner] + Mathf.Max(0f, Dur(Headliner) * slot - DropWarning);
                plan.moments.Add(new MomentPlan { kind = MomentKind.Drop, phase = Headliner, region = stage, spot = -1, time = time });
            }
            bool mosh = level > 0 || rng.Next(2) == 0, surfer = level > 0 || !mosh;
            if (mosh) plan.moments.Add(new MomentPlan { kind = MomentKind.MoshPit, phase = Headliner, region = stage, spot = -1, time = At(Headliner, 0.08f, 0.6f) });
            if (surfer) plan.moments.Add(new MomentPlan { kind = MomentKind.Surfer, phase = Headliner, region = stage, spot = -1, time = At(Headliner, 0.2f, 0.75f) });

            // ENCORE: fireworks the whole phase, maybe a stray rocket, and camera tasks twice as often.
            plan.moments.Add(new MomentPlan { kind = MomentKind.Fireworks, phase = Encore, region = stage, spot = -1, time = plan.phaseStart[Encore] + 2f });
            float rocket = level switch { 0 => 0.1f, 1 => 0.3f, _ => 0.5f };
            if (rng.NextDouble() < rocket)
                plan.moments.Add(new MomentPlan { kind = MomentKind.StrayRocket, phase = Encore, region = stage, spot = -1, time = At(Encore, 0.25f, 0.8f) });
            int extra = level + rng.Next(2); // 0–1, 1–2, 2–3
            bool child = rng.Next(2) == 0;
            for (int k = 0; k < extra && venue.regions.Count > 0; k++, child = !child)
                plan.moments.Add(new MomentPlan
                {
                    kind = child ? MomentKind.LostChild : MomentKind.Fight, phase = Encore,
                    region = rng.Next(venue.regions.Count), spot = -1, time = At(Encore, 0.05f, 0.6f),
                });

            plan.disasterPhase = level == 0 ? WarmUp : level == 1 ? Headliner : Encore;
            plan.disasterTime = At(plan.disasterPhase, 0.3f, 0.8f);
            plan.moments.Sort((a, b) => a.time.CompareTo(b.time));
            return plan;
        }

        /// <summary>
        /// Moves the round's disasters (fire, tower, gas; not blocked exits or chained collapses) so the first one starts at
        /// <paramref name="firstTime"/>; the others keep their spacing after it.
        /// </summary>
        public static void ShiftDisasters(IncidentSchedule schedule, float firstTime)
        {
            float first = float.PositiveInfinity;
            foreach (var incident in schedule.incidents)
                if (incident.trigger == null && !(incident is BlockDoorIncident)) first = Mathf.Min(first, incident.startTime);
            if (float.IsInfinity(first)) return;
            foreach (var incident in schedule.incidents)
                if (incident.trigger == null && !(incident is BlockDoorIncident)) incident.startTime += firstTime - first;
        }

        static List<int> Range(int count)
        {
            var list = new List<int>(count);
            for (int i = 0; i < count; i++) list.Add(i);
            return list;
        }

        /// <summary>One line per incident of a round: "Fire (Stage Left) 7.2 s", for logs and test reports.</summary>
        public static string Describe(Incident incident)
        {
            string kind = incident switch
            {
                FireIncident => "Fire",
                TowerCollapseIncident t => t.trigger != null ? "Tower collapse (chained)" : "Tower collapse",
                GasLeakIncident => "Gas leak",
                BlockDoorIncident b => $"Blocked exit ({b.reason})",
                _ => incident.name,
            };
            return $"{kind} at {incident.SiteName}";
        }
    }

    /// <summary>Places the evening's moments use, found from the map's landmarks and zones (works on every map).</summary>
    public static class EveningSpots
    {
        /// <summary>What the crowd faces: the Main Stage, else the playing field, else the train, else the busiest crowd area.</summary>
        public static RectInt StageRect(FestivalVenue venue)
        {
            foreach (string name in new[] { "Main Stage", "Playing Field", "Metro Train" })
            {
                RectInt? found = null;
                foreach (var l in venue.landmarks)
                    if (l.name == name) found = found is RectInt r ? Enclose(r, l.rect) : l.rect;
                if (found != null) return found.Value;
            }
            foreach (var z in venue.crowdZones) if (z.stageFacing) return z.rect;
            return venue.crowdZones.Count > 0 ? venue.crowdZones[0].rect : new RectInt(FestivalVenue.Width / 2 - 2, FestivalVenue.Height / 2 - 2, 4, 4);
        }

        static RectInt Enclose(RectInt a, RectInt b)
        {
            int xMin = Mathf.Min(a.xMin, b.xMin), yMin = Mathf.Min(a.yMin, b.yMin);
            return new RectInt(xMin, yMin, Mathf.Max(a.xMax, b.xMax) - xMin, Mathf.Max(a.yMax, b.yMax) - yMin);
        }

        /// <summary>The point of the stage nearest <paramref name="p"/>.</summary>
        public static Vector2 NearestStagePoint(RectInt stage, Vector2 p) =>
            new(Mathf.Clamp(p.x, stage.xMin, stage.xMax), Mathf.Clamp(p.y, stage.yMin, stage.yMax));

        /// <summary>The zone with the most open ground within 14 m of the stage.</summary>
        public static int StageRegion(FestivalVenue venue)
        {
            var stage = StageRect(venue);
            var counts = new int[venue.regions.Count];
            for (int c = 0; c < FestivalVenue.CellCount; c++)
            {
                int r = venue.regionOf[c];
                if (r < 0 || venue.cells[c] != CellType.Open) continue;
                if ((NearestStagePoint(stage, FestivalVenue.CellCenter(c)) - FestivalVenue.CellCenter(c)).sqrMagnitude <= 14f * 14f) counts[r]++;
            }
            int best = 0;
            for (int r = 1; r < counts.Length; r++) if (counts[r] > counts[best]) best = r;
            return counts.Length == 0 ? -1 : best;
        }

        /// <summary>The zone of the open cell nearest <paramref name="p"/> (within 8 m), or -1.</summary>
        public static int RegionNear(FestivalVenue venue, Vector2 p)
        {
            int best = -1;
            float bestSq = 64f;
            int cx = Mathf.FloorToInt(p.x), cy = Mathf.FloorToInt(p.y);
            for (int y = cy - 8; y <= cy + 8; y++)
                for (int x = cx - 8; x <= cx + 8; x++)
                {
                    if (x < 0 || y < 0 || x >= FestivalVenue.Width || y >= FestivalVenue.Height) continue;
                    int c = FestivalVenue.Index(x, y);
                    if (venue.regionOf[c] < 0 || venue.cells[c] != CellType.Open) continue;
                    float sq = (FestivalVenue.CellCenter(c) - p).sqrMagnitude;
                    if (sq < bestSq) { bestSq = sq; best = venue.regionOf[c]; }
                }
            return best;
        }

        /// <summary>Zones people rush to for a drink or food.</summary>
        public static List<int> BarRegions(FestivalVenue venue)
        {
            var list = new List<int>();
            for (int r = 0; r < venue.regions.Count; r++)
            {
                string n = venue.regions[r].name;
                if (n.Contains("Drink") || n.Contains("Food") || n.Contains("Concession") || n.Contains("Bar") || n.Contains("Cafe")) list.Add(r);
            }
            return list;
        }

        /// <summary>Landmarks people run to when it rains: tents, else bars, cafes, food stalls, stands, first aid.</summary>
        public static List<int> Shelters(FestivalVenue venue)
        {
            var list = new List<int>();
            for (int i = 0; i < venue.landmarks.Count; i++)
                if (venue.landmarks[i].solid && venue.landmarks[i].name.Contains("Tent")) list.Add(i);
            if (list.Count > 0) return list;
            for (int i = 0; i < venue.landmarks.Count; i++)
            {
                string n = venue.landmarks[i].name;
                if (venue.landmarks[i].solid && (n.Contains("Bar") || n.Contains("Cafe") || n.Contains("Food") || n.Contains("Stand") || n.Contains("Aid") || n.Contains("Hall"))) list.Add(i);
            }
            return list;
        }

        /// <summary>Open cells in a ring up to <paramref name="ring"/> m around <paramref name="rect"/> (its doorways and sides).</summary>
        public static List<int> RingCells(FestivalVenue venue, RectInt rect, int ring)
        {
            var list = new List<int>();
            for (int y = rect.yMin - ring; y < rect.yMax + ring; y++)
                for (int x = rect.xMin - ring; x < rect.xMax + ring; x++)
                {
                    if (x < 0 || y < 0 || x >= FestivalVenue.Width || y >= FestivalVenue.Height || rect.Contains(new Vector2Int(x, y))) continue;
                    int c = FestivalVenue.Index(x, y);
                    if (venue.cells[c] == CellType.Open && venue.regionOf[c] >= 0) list.Add(c);
                }
            return list;
        }

        /// <summary>Open cells of a zone.</summary>
        public static List<int> RegionCells(FestivalVenue venue, int region)
        {
            var list = new List<int>();
            for (int c = 0; c < FestivalVenue.CellCount; c++)
                if (venue.regionOf[c] == region && venue.cells[c] == CellType.Open) list.Add(c);
            return list;
        }
    }
}
