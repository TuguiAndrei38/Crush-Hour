using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// Headless balance runs and scenario tests (see AGENTS.md, "Balance baseline"). Pure simulation, no Unity API
    /// calls, so they run on a background thread in the editor (Tools > Crush Hour > Run ...) or in a plain .NET
    /// program (Tools~/Verify/Baseline.cs). <see cref="Simulate"/> mirrors CrushHourGame.Simulate: gates, fire, door
    /// fields, crowd step, patrol rings, megaphone pulses. Uses the code defaults of <see cref="CrowdSettings"/>, not
    /// the scene's Inspector values.
    /// </summary>
    public static class BalanceBaselineRunner
    {
        const float Dt = 0.04f, TimeLimit = 240f, AnnounceInterval = 5f;
        const int MaxFireCells = 400;
        static readonly Vector2[] PolicePosts = { new(40, 52), new(64, 52) }; // CrushHourGame.MegaphonePosts
        static readonly string[] Sites = { "the Food Court", "Stage Left", "the Bar Tent" };

        enum Response { NoStaffNoGates, StaffAtPosts, GatesAndStaff }
        const int ResponseCount = 3;

        static string Describe(Response r) => r switch
        {
            Response.NoStaffNoGates => "No staff, gates shut",
            Response.StaffAtPosts => "Staff at posts, gates shut",
            _ => "Gates + staff",
        };

        struct Result
        {
            public int injured, attacks, inside, drunkInside;
            public float time;
            public double msPerStep;
        }

        // ================================================================ Shared scenario loop

        /// <summary>One fire scenario: a fresh venue and crowd for <paramref name="seed"/>, the fire at <paramref name="siteName"/>.</summary>
        sealed class Scenario
        {
            public System.Random rng;
            public FestivalVenue venue;
            public CrowdSettings settings;
            public CrowdSimulation sim;
            public readonly List<Vector2> patrols = new(), police = new();
            public readonly IncidentSchedule schedule = new(MaxFireCells);
            public float time;
            public int steps;
            public double milliseconds;

            /// <summary>Pre-refactor fire path (StartFire + UpdateFire directly), kept only for <see cref="RunIncidentRefactorCheck"/>.</summary>
            public readonly bool legacyFire;

            public Scenario(string siteName, int seed, bool staff, bool legacyFire = false)
            {
                this.legacyFire = legacyFire;
                rng = new System.Random(seed);
                venue = new FestivalVenue();
                settings = new CrowdSettings();
                sim = new CrowdSimulation(settings, venue, rng);
                if (staff)
                {
                    // Staff stand at their default posts: one patrolman per zone, plus the megaphone police.
                    foreach (var region in venue.regions) patrols.Add(new Vector2(region.post.x + 0.5f, region.post.y + 0.5f));
                    police.AddRange(PolicePosts);
                }
                // Same path as the game: the fire is an incident in a schedule, started at once here.
                if (legacyFire) venue.StartFire(venue.fireSites.First(s => s.name == siteName).cell);
                else schedule.StartNow(FireIncident.At(venue, siteName, rng), venue, sim);
            }
        }

        /// <summary>Runs until everyone is out, the evacuation stalls, or time runs out. <paramref name="beforeStep"/> gets the time so far.</summary>
        static void Simulate(Scenario s, Func<bool> cancelled, Action<float> beforeStep = null)
        {
            var changed = new List<Gate>();
            float announce = 0f, lastProgress = 0f;
            int lastSafe = 0;
            var watch = Stopwatch.StartNew();
            var sim = s.sim;
            while (!cancelled())
            {
                beforeStep?.Invoke(s.time);
                s.venue.UpdateGates(Dt, changed);
                if (s.legacyFire) s.venue.UpdateFire(Dt, s.rng, MaxFireCells);
                else s.schedule.Update(Dt, s.venue, sim, s.rng);
                s.venue.UpdateDoorFields(3);
                sim.Step(Dt, s.venue, true);
                if (s.patrols.Count > 0) sim.ApplyStewards(s.patrols, Dt, true, s.settings.patrolRadius);
                announce -= Dt;
                if (announce <= 0f)
                {
                    announce = AnnounceInterval;
                    foreach (var p in s.police) sim.PoliceAnnouncement(p);
                }
                s.time += Dt;
                s.steps++;
                if (sim.SafeCount != lastSafe)
                {
                    lastSafe = sim.SafeCount;
                    lastProgress = s.time;
                }
                // Like the game: drunk people never leave on their own, so the run ends on the sober ones.
                bool stalled = sim.EvacuatingCount == sim.SoberActiveCount && s.time - lastProgress > 20f;
                if (sim.SoberActiveCount == 0 || stalled || s.time >= TimeLimit) break;
            }
            s.milliseconds = watch.Elapsed.TotalMilliseconds;
        }

        static string Range(IEnumerable<int> values, string unit = "")
        {
            int min = values.Min(), max = values.Max();
            return min == max ? $"{min}{unit}" : $"{min}–{max}{unit}";
        }

        // ================================================================ Balance baseline

        public static int RunCount(int seedCount) => Sites.Length * ResponseCount * seedCount;

        /// <summary>
        /// Runs every fire site × response × seed. Calls <paramref name="log"/> once per finished run.
        /// Returns the Markdown table for AGENTS.md (or null when <paramref name="cancelled"/> turned true).
        /// </summary>
        public static string RunAll(int[] seeds, Action<string> log, Func<bool> cancelled)
        {
            var rows = new List<(string site, Response response, List<Result> runs)>();
            foreach (var site in Sites)
            {
                for (int k = 0; k < ResponseCount; k++)
                {
                    var response = (Response)k;
                    var runs = new List<Result>();
                    foreach (int seed in seeds)
                    {
                        if (cancelled()) return null;
                        var r = Run(site, response, seed, cancelled);
                        runs.Add(r);
                        log?.Invoke($"{site,-16} {Describe(response),-27} seed {seed,3}: injured {r.injured,4}  attacks {r.attacks,4}  inside {r.inside,4}  drunk inside {r.drunkInside,3}  time {r.time,5:0}s  ({r.msPerStep:0.0} ms/step)");
                    }
                    rows.Add((site, response, runs));
                }
            }

            var md = new StringBuilder();
            var defaults = new CrowdSettings();
            md.AppendLine($"Seeds {string.Join(", ", seeds)}, {defaults.agentCount} people, drunk fraction {defaults.drunkFraction:0.##} (x{defaults.drunkDrinksMultiplier:0} in Drinks). \"Still inside\" counts sober people only.");
            md.AppendLine();
            md.AppendLine("| Fire at | Response | Injured | Panic attacks | Still inside | Drunk left inside | Time |");
            md.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var (site, response, runs) in rows)
            {
                string name = site.StartsWith("the ") ? char.ToUpper(site[4]) + site.Substring(5) : site;
                bool anyRanOut = runs.Any(r => r.time >= TimeLimit - 0.01f);
                string time = !anyRanOut ? Range(runs.Select(r => (int)Math.Round(r.time)), "s")
                    : runs.All(r => r.time >= TimeLimit - 0.01f) ? $"ran out ({TimeLimit:0}s)"
                    : $"{runs.Min(r => r.time):0}s – ran out";
                md.AppendLine($"| {name} | {Describe(response)} | {Range(runs.Select(r => r.injured))} | {Range(runs.Select(r => r.attacks))} | {Range(runs.Select(r => r.inside))} | {Range(runs.Select(r => r.drunkInside))} | {time} |");
            }
            return md.ToString();
        }

        static Result Run(string siteName, Response response, int seed, Func<bool> cancelled, bool legacyFire = false)
        {
            var s = new Scenario(siteName, seed, response != Response.NoStaffNoGates, legacyFire);
            if (response == Response.GatesAndStaff)
                foreach (var gate in s.venue.gates) gate.open = true; // opened the moment the fire starts
            Simulate(s, cancelled);
            return new Result
            {
                injured = s.sim.InjuredCount,
                attacks = s.sim.PanicAttacksTotal,
                inside = s.sim.SoberActiveCount,
                drunkInside = s.sim.DrunkInsideCount,
                time = s.time,
                msPerStep = s.milliseconds / Math.Max(1, s.steps),
            };
        }

        // ================================================================ Incident refactor check

        public static int RefactorCheckRunCount(int seedCount) => 2 * RunCount(seedCount);

        /// <summary>
        /// Week 5 step 1 acceptance test: every baseline scenario is run twice, once through the old fire code (StartFire +
        /// UpdateFire) and once through IncidentSchedule/FireIncident, and every result must be identical. Returns a
        /// Markdown report (null when cancelled).
        /// </summary>
        public static string RunIncidentRefactorCheck(int[] seeds, Action<string> log, Func<bool> cancelled)
        {
            var md = new StringBuilder();
            md.AppendLine($"Old fire path vs IncidentSchedule, every baseline scenario, seeds {string.Join(", ", seeds)}.");
            md.AppendLine();
            md.AppendLine("| Fire at | Response | Seed | Old: injured / attacks / inside / drunk / time | New | Same |");
            md.AppendLine("|---|---|---|---|---|---|");
            int differences = 0, runs = 0;
            foreach (var site in Sites)
            {
                for (int k = 0; k < ResponseCount; k++)
                {
                    var response = (Response)k;
                    foreach (int seed in seeds)
                    {
                        if (cancelled()) return null;
                        var before = Run(site, response, seed, cancelled, legacyFire: true);
                        log?.Invoke($"{site} / {Describe(response)} / seed {seed}: old path done");
                        if (cancelled()) return null;
                        var after = Run(site, response, seed, cancelled);
                        bool same = before.injured == after.injured && before.attacks == after.attacks && before.inside == after.inside &&
                                    before.drunkInside == after.drunkInside && Math.Abs(before.time - after.time) < 1e-4f;
                        if (!same) differences++;
                        runs++;
                        string Fmt(Result r) => $"{r.injured} / {r.attacks} / {r.inside} / {r.drunkInside} / {r.time:0.00}s";
                        string line = $"| {site} | {Describe(response)} | {seed} | {Fmt(before)} | {Fmt(after)} | {(same ? "yes" : "**NO**")} |";
                        md.AppendLine(line);
                        log?.Invoke(line);
                    }
                }
            }
            md.Insert(0, differences == 0
                ? $"**PASS**: all {runs} scenarios identical.\n\n"
                : $"**FAIL**: {differences} of {runs} scenarios differ.\n\n");
            return md.ToString();
        }

        // ================================================================ Zone order test

        const string OrderSite = "the Food Court", OrderRegion = "Main Stage", OrderGate = "East Gate A";
        const float OrderDelay = 5f;

        public static int ZoneOrderRunCount(int seedCount) => 2 * seedCount;

        /// <summary>
        /// Zone evacuation order test: fire at the Food Court, staff at their posts, only East Gate A opened when the fire
        /// starts. <see cref="OrderDelay"/> seconds in, the Main Stage patrol orders its zone out "towards East Gate A"
        /// (or, in the control run, does nothing). Compares how many people leave through East Gate A, overall and among
        /// the people who were in the Main Stage zone at that moment. Returns a Markdown table (null when cancelled).
        /// </summary>
        public static string RunZoneOrderTest(int[] seeds, Action<string> log, Func<bool> cancelled)
        {
            var md = new StringBuilder();
            md.AppendLine($"Fire at the Food Court, staff at their posts, only {OrderGate} open. At {OrderDelay:0} s the {OrderRegion} patrol orders its zone towards {OrderGate} (or not). Seeds {string.Join(", ", seeds)}.");
            md.AppendLine();
            md.AppendLine($"| Seed | Order | Door chosen | People in zone | Zone people out via {OrderGate} | Everyone out via {OrderGate} | Safe | Injured | Still inside | Time |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (int seed in seeds)
            {
                foreach (bool order in new[] { false, true })
                {
                    if (cancelled()) return null;
                    var s = new Scenario(OrderSite, seed, true);
                    var venue = s.venue;
                    var sim = s.sim;
                    int gateDoor = venue.doors.FindIndex(d => d.name == OrderGate);
                    int region = venue.regions.FindIndex(r => r.name == OrderRegion);
                    venue.doors[gateDoor].gate.open = true;

                    var zonePeople = new List<int>();
                    string chosen = "-";
                    bool done = false;
                    Simulate(s, cancelled, t =>
                    {
                        if (done || t < OrderDelay) return;
                        done = true;
                        for (int i = 0; i < sim.count; i++)
                            if (sim.state[i] == AgentState.Active && venue.regionOf[CrowdSimulation.CellOf(sim.position[i])] == region)
                                zonePeople.Add(i);
                        if (!order) return;
                        var post = venue.regions[region].post;
                        var direction = venue.doors[gateDoor].Center - new Vector2(post.x + 0.5f, post.y + 0.5f);
                        int d = sim.OrderRegionEvacuation(region, direction, venue);
                        chosen = d >= 0 ? venue.doors[d].name : "none";
                    });

                    int zoneViaGate = zonePeople.Count(i => sim.exitDoor[i] == gateDoor);
                    string line = $"| {seed} | {(order ? "yes" : "no")} | {chosen} | {zonePeople.Count} | {zoneViaGate} | {sim.exitsByDoor[gateDoor]} | {sim.SafeCount} | {sim.InjuredCount} | {sim.ActiveCount} | {s.time:0}s |";
                    md.AppendLine(line);
                    log?.Invoke(line);
                }
            }
            return md.ToString();
        }

        // ================================================================ Blocked exit test

        const string BlockSite = "the Food Court", BlockDoor = "Main Entrance", BlockReason = "Delivery truck";
        const float ClearStart = 20f;

        public static int BlockedExitRunCount(int seedCount) => 3 * seedCount;

        /// <summary>
        /// Blocked exit test: fire at the Food Court, staff at their posts, gates shut (so the two entrances are the only
        /// way out and the block matters most). Three cases per seed: Main Entrance not blocked; blocked at t = 0 and never
        /// cleared; blocked at t = 0 and, from t = <see cref="ClearStart"/> s, cleared by the Main Entrance zone patrol
        /// standing at it (one worker: 8 s; its ring is off meanwhile, then it stays there). Reports injuries, time and
        /// exits per door. Returns a Markdown table (null when cancelled).
        /// </summary>
        public static string RunBlockedExitTest(int[] seeds, Action<string> log, Func<bool> cancelled)
        {
            var doorNames = new FestivalVenue().doors.Select(d => d.name).ToList();
            var md = new StringBuilder();
            md.AppendLine($"Fire at the Food Court, staff at their posts, gates shut. {BlockDoor} blocked ({BlockReason}) at 0 s; in the \"cleared\" case the {BlockDoor} zone patrol starts clearing it at {ClearStart:0} s (alone: {FestivalVenue.DoorClearTime:0} s). Seeds {string.Join(", ", seeds)}.");
            md.AppendLine();
            md.AppendLine($"| Seed | Case | Cleared at | Safe | Injured | Still inside | Drunk left inside | Time | {string.Join(" | ", doorNames)} |");
            md.AppendLine($"|---|---|---|---|---|---|---|---|{string.Concat(doorNames.Select(_ => "---|"))}");
            string[] cases = { "Not blocked", "Blocked, never cleared", $"Blocked, cleared from {ClearStart:0} s" };
            foreach (int seed in seeds)
            {
                for (int k = 0; k < cases.Length; k++)
                {
                    if (cancelled()) return null;
                    var s = new Scenario(BlockSite, seed, true);
                    var venue = s.venue;
                    var sim = s.sim;
                    var door = venue.doors.Find(d => d.name == BlockDoor);
                    if (k > 0) s.schedule.StartNow(BlockDoorIncident.At(venue, BlockDoor, BlockReason), venue, sim);

                    // The worker: the zone patrol of the region in front of the door (patrols are listed in region order).
                    int front = venue.DoorFrontCell(door);
                    int worker = front >= 0 ? venue.regionOf[front] : -1;
                    Vector2 workerSpot = front >= 0 ? FestivalVenue.CellCenter(front) : default;
                    bool working = false;
                    float clearedAt = -1f;
                    Simulate(s, cancelled, t =>
                    {
                        if (k != 2 || worker < 0 || clearedAt >= 0f || t < ClearStart) return;
                        if (!working)
                        {
                            working = true;
                            s.patrols.RemoveAt(worker); // ring off while clearing
                        }
                        if (!venue.ClearDoor(door, 1, Dt)) return;
                        clearedAt = t + Dt;
                        s.patrols.Insert(worker, workerSpot); // back on duty, standing at the door
                    });

                    string cleared = k == 0 ? "-" : clearedAt >= 0f ? $"{clearedAt:0.0}s" : "never";
                    string exits = string.Join(" | ", venue.doors.Select((d, i) => sim.exitsByDoor[i].ToString()));
                    string line = $"| {seed} | {cases[k]} | {cleared} | {sim.SafeCount} | {sim.InjuredCount} | {sim.SoberActiveCount} | {sim.DrunkInsideCount} | {s.time:0}s | {exits} |";
                    md.AppendLine(line);
                    log?.Invoke(line);
                }
            }
            return md.ToString();
        }
    }
}
