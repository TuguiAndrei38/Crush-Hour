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
}
