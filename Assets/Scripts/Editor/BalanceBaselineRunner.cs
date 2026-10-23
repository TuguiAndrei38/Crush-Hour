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
            public int injured, attacks, inside;
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
            public float time;
            public int steps;
            public double milliseconds;

            public Scenario(string siteName, int seed, bool staff)
            {
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
                venue.StartFire(venue.fireSites.First(s => s.name == siteName).cell);
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
                s.venue.UpdateFire(Dt, s.rng, MaxFireCells);
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
                bool stalled = sim.EvacuatingCount == sim.ActiveCount && s.time - lastProgress > 20f;
                if (sim.ActiveCount == 0 || stalled || s.time >= TimeLimit) break;
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
                        log?.Invoke($"{site,-16} {Describe(response),-27} seed {seed,3}: injured {r.injured,4}  attacks {r.attacks,4}  inside {r.inside,4}  time {r.time,5:0}s  ({r.msPerStep:0.0} ms/step)");
                    }
                    rows.Add((site, response, runs));
                }
            }

            var md = new StringBuilder();
            md.AppendLine($"Seeds {string.Join(", ", seeds)}, {new CrowdSettings().agentCount} people.");
            md.AppendLine();
            md.AppendLine("| Fire at | Response | Injured | Panic attacks | Still inside | Time |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var (site, response, runs) in rows)
            {
                string name = site.StartsWith("the ") ? char.ToUpper(site[4]) + site.Substring(5) : site;
                bool anyRanOut = runs.Any(r => r.time >= TimeLimit - 0.01f);
                string time = !anyRanOut ? Range(runs.Select(r => (int)Math.Round(r.time)), "s")
                    : runs.All(r => r.time >= TimeLimit - 0.01f) ? $"ran out ({TimeLimit:0}s)"
                    : $"{runs.Min(r => r.time):0}s – ran out";
                md.AppendLine($"| {name} | {Describe(response)} | {Range(runs.Select(r => r.injured))} | {Range(runs.Select(r => r.attacks))} | {Range(runs.Select(r => r.inside))} | {time} |");
            }
            return md.ToString();
        }

        static Result Run(string siteName, Response response, int seed, Func<bool> cancelled)
        {
            var s = new Scenario(siteName, seed, response != Response.NoStaffNoGates);
            if (response == Response.GatesAndStaff)
                foreach (var gate in s.venue.gates) gate.open = true; // opened the moment the fire starts
            Simulate(s, cancelled);
            return new Result
            {
                injured = s.sim.InjuredCount,
                attacks = s.sim.PanicAttacksTotal,
                inside = s.sim.ActiveCount,
                time = s.time,
                msPerStep = s.milliseconds / Math.Max(1, s.steps),
            };
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
    }
}
