using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// Headless balance runs (see AGENTS.md, "Balance baseline"). Pure simulation, no Unity API calls, so it runs on a
    /// background thread in the editor (Tools > Crush Hour > Run Balance Baseline) or in a plain .NET program
    /// (Tools~/Verify/Baseline.cs). Mirrors CrushHourGame.Simulate: gates, fire, door fields, crowd step, patrol rings,
    /// megaphone pulses. Uses the code defaults of <see cref="CrowdSettings"/>, not the scene's Inspector values.
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

        static string Range(IEnumerable<int> values, string unit = "")
        {
            int min = values.Min(), max = values.Max();
            return min == max ? $"{min}{unit}" : $"{min}–{max}{unit}";
        }

        static Result Run(string siteName, Response response, int seed, Func<bool> cancelled)
        {
            var rng = new System.Random(seed);
            var venue = new FestivalVenue();
            var settings = new CrowdSettings();
            var sim = new CrowdSimulation(settings, venue, rng);
            var site = venue.fireSites.First(s => s.name == siteName);

            // Staff stand at their default posts: one patrolman per zone, plus the megaphone police.
            var patrols = new List<Vector2>();
            var police = new List<Vector2>();
            if (response != Response.NoStaffNoGates)
            {
                foreach (var region in venue.regions) patrols.Add(new Vector2(region.post.x + 0.5f, region.post.y + 0.5f));
                police.AddRange(PolicePosts);
            }
            if (response == Response.GatesAndStaff)
                foreach (var gate in venue.gates) gate.open = true; // opened the moment the fire starts

            venue.StartFire(site.cell);
            var changed = new List<Gate>();
            float t = 0f, announce = 0f, lastProgress = 0f;
            int lastSafe = 0, steps = 0;
            var watch = Stopwatch.StartNew();
            while (!cancelled())
            {
                venue.UpdateGates(Dt, changed);
                venue.UpdateFire(Dt, rng, MaxFireCells);
                venue.UpdateDoorFields(3);
                sim.Step(Dt, venue, true);
                if (patrols.Count > 0) sim.ApplyStewards(patrols, Dt, true, settings.patrolRadius);
                announce -= Dt;
                if (announce <= 0f)
                {
                    announce = AnnounceInterval;
                    foreach (var p in police) sim.PoliceAnnouncement(p);
                }
                t += Dt;
                steps++;
                if (sim.SafeCount != lastSafe)
                {
                    lastSafe = sim.SafeCount;
                    lastProgress = t;
                }
                bool stalled = sim.EvacuatingCount == sim.ActiveCount && t - lastProgress > 20f;
                if (sim.ActiveCount == 0 || stalled || t >= TimeLimit) break;
            }
            return new Result
            {
                injured = sim.InjuredCount,
                attacks = sim.PanicAttacksTotal,
                inside = sim.ActiveCount,
                time = t,
                msPerStep = watch.Elapsed.TotalMilliseconds / Math.Max(1, steps),
            };
        }
    }
}
