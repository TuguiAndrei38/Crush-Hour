// Headless layout and random-round smoke check. Run check-maps.ps1; no baseline reports are changed.
using System;
using System.Globalization;
using System.Linq;
using CrushHour;

static class MapLayoutCheck
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static bool EntranceReachable(FestivalVenue venue, int cell) =>
        venue.doors.Any(d => d.gate == null && d.field.Reachable(cell));

    static ulong Fingerprint(FestivalVenue venue)
    {
        ulong hash = 14695981039346656037UL;
        Action<string> add = text => { foreach (char ch in text) { hash ^= ch; hash *= 1099511628211UL; } };
        for (int c = 0; c < FestivalVenue.CellCount; c++)
            add($"{(int)venue.cells[c]},{venue.gateOf[c]},{venue.flammable[c]},{venue.blockHeight[c]}|");
        foreach (var door in venue.doors)
            add($"{door.name},{door.rect.x},{door.rect.y},{door.rect.width},{door.rect.height},{door.gate != null}|");
        foreach (var region in venue.regions)
            add($"{region.name},{region.area.x},{region.area.y},{region.area.width},{region.area.height},{region.post.x},{region.post.y}|");
        foreach (var site in venue.fireSites) add($"{site.name},{site.cell.x},{site.cell.y},{site.region}|");
        foreach (var zone in venue.crowdZones)
            add($"{zone.rect.x},{zone.rect.y},{zone.rect.width},{zone.rect.height},{zone.weight},{zone.stageFacing}|");
        return hash;
    }

    static void CheckAdmissions(VenueMap map, Difficulty difficulty)
    {
        var venue = new FestivalVenue(map);
        var settings = DifficultySettings.For(difficulty);
        CrowdSimulation NewCrowd() => new CrowdSimulation(
            new CrowdSettings { agentCount = settings.playerCount, drunkFraction = settings.drunkFraction },
            venue, new Random(11));
        var sim = NewCrowd();
        var edited = NewCrowd(); // The same walk across a cinematic time cut, then a skip.
        sim.BeginArrivals(venue);
        edited.BeginArrivals(venue);
        Require(!sim.ArrivalsComplete && sim.ArrivalDuration > 0, $"{map}/{difficulty}: no entrance routes");
        Require(sim.position.All(p => venue.cells[FestivalVenue.CellAt(p)] == CellType.Outside),
            $"{map}/{difficulty}: attendees must begin outside");

        var last = (UnityEngine.Vector2[])sim.position.Clone();
        var entered = new bool[sim.count];
        var entranceAt = new int[FestivalVenue.CellCount];
        var visits = new int[venue.doors.Count];
        for (int d = 0; d < venue.doors.Count; d++)
            if (venue.doors[d].gate == null)
                foreach (var p in venue.doors[d].rect.allPositionsWithin)
                    entranceAt[FestivalVenue.Index(p.x, p.y)] = d + 1;

        const float dt = 0.4f; // Even the fastest walker needs more than one tick to cross the fence.
        int ticks = 0;
        int cut = (int)(sim.ArrivalDuration * 0.35f / dt);
        while (!sim.ArrivalsComplete && ticks * dt <= sim.ArrivalDuration + 1f)
        {
            ticks++;
            sim.Step(dt, venue, false);
            for (int i = 0; i < sim.count; i++)
            {
                var p = sim.position[i];
                if ((p - last[i]).magnitude > sim.settings.walkSpeed * sim.personalSpeed[i] * dt + 0.01f)
                    throw new InvalidOperationException($"{map}/{difficulty}: attendee {i} jumped at tick {ticks}");
                last[i] = p;
                int cell = FestivalVenue.CellAt(p);
                if (!venue.IsWalkable(cell))
                    throw new InvalidOperationException($"{map}/{difficulty}: attendee {i} entered a solid cell {cell}");
                if (venue.cells[cell] == CellType.Outside) continue;
                if (!sim.IsVisible(i)) throw new InvalidOperationException($"{map}/{difficulty}: hidden indoor attendee {i}");
                if (entered[i]) continue;
                int entrance = entranceAt[cell];
                if (entrance == 0) throw new InvalidOperationException($"{map}/{difficulty}: attendee {i} bypassed a public entrance");
                visits[entrance - 1]++;
                entered[i] = true;
            }
            if (ticks == cut)
            {
                edited.StepArrivals(dt * cut, venue);
                for (int i = 0; i < sim.count; i++)
                    if ((sim.position[i] - edited.position[i]).sqrMagnitude > 0.0025f || sim.IsVisible(i) != edited.IsVisible(i))
                        throw new InvalidOperationException($"{map}/{difficulty}: camera time cut changed attendee {i}'s route");
                edited.FinishArrivals(venue);
            }
        }
        Require(sim.ArrivalsComplete && edited.ArrivalsComplete && entered.All(v => v), $"{map}/{difficulty}: incomplete admissions");
        Require(sim.SafeCount == 0 && sim.InjuredCount == 0 && sim.EvacuatingCount == 0 &&
            sim.ActiveCount == sim.count && sim.exitsByDoor.All(n => n == 0), $"{map}/{difficulty}: admissions affected scoring");
        for (int i = 0; i < sim.count; i++)
            Require(sim.position[i] == sim.anchor[i] && sim.position[i] == edited.position[i] && sim.IsVisible(i) &&
                sim.drunk[i] == edited.drunk[i] && sim.exitDoor[i] == -1, "Original crowd did not reach its destinations");
        for (int d = 0; d < visits.Length; d++)
            Require((visits[d] > 0) == (venue.doors[d].gate == null), $"{map}/{difficulty}: wrong entry door {d}");
        Console.WriteLine($"PASS {map}/{difficulty}: {sim.count} admissions through public entrances in {sim.ArrivalDuration:F0} simulated seconds.");
    }

    static void CheckLayout(VenueMap map)
    {
        var venue = new FestivalVenue(map);
        Require(venue.map == map, $"{map}: wrong map identity");
        Require(venue.doors.Count == 8 && venue.gates.Count == 6, $"{map}: expected 2 entrances and 6 gates");
        Require(venue.lightTowers.Count == 2 && venue.regions.Count == 9, $"{map}: expected 2 towers and 9 patrol regions");
        for (int y = 4; y < FestivalVenue.Height - 4; y++)
            for (int x = 4; x < FestivalVenue.Width - 4; x++)
            {
                int cell = FestivalVenue.Index(x, y);
                Require(venue.regionOf[cell] >= 0, $"{map}: unassigned region at {x},{y}");
                if (venue.cells[cell] == CellType.Open)
                    Require(EntranceReachable(venue, cell), $"{map}: trapped open cell at {x},{y}");
            }
        foreach (var region in venue.regions)
        {
            int cell = FestivalVenue.Index(region.post.x, region.post.y);
            Require(venue.cells[cell] == CellType.Open && EntranceReachable(venue, cell), $"{map}: invalid patrol post {region.name}");
            Require(venue.regions[venue.regionOf[cell]] == region, $"{map}: patrol outside its zone {region.name}");
        }
        foreach (var door in venue.doors)
        {
            Require(door.approach.Count > 0 && door.beyond.Count > 0, $"{map}: inaccessible door {door.name}");
            Require(door.approach.All(c => EntranceReachable(venue, c)), $"{map}: isolated door approach {door.name}");
            foreach (var region in venue.regions)
                Require(door.field.Reachable(FestivalVenue.Index(region.post.x, region.post.y)), $"{map}: {region.name} cannot reach {door.name}");
        }
        foreach (var site in venue.fireSites)
        {
            int cell = FestivalVenue.Index(site.cell.x, site.cell.y);
            // The original festival includes a Food Court site inside its stall; keep that baseline intact.
            if (map != VenueMap.SummerFestival)
                Require(venue.cells[cell] == CellType.Open && EntranceReachable(venue, cell), $"{map}: invalid incident site {site.name}");
            Require(site.region >= 0, $"{map}: incident outside a patrol region");
        }
        foreach (string gasSite in GasLeakIncident.Sites)
            Require(venue.fireSites.Any(site => site.name == gasSite), $"{map}: missing gas site {gasSite}");
        Console.WriteLine($"PASS {map}: all open cells, patrols and gate approaches connected; fingerprint {Fingerprint(venue):X16}");
    }

    static void CheckRound(VenueMap map, Difficulty difficulty, int seed)
    {
        var venue = new FestivalVenue(map);
        var settings = DifficultySettings.For(difficulty);
        var rng = new Random(seed);
        var sim = new CrowdSimulation(new CrowdSettings { agentCount = settings.playerCount, drunkFraction = settings.drunkFraction }, venue, rng);
        Require(sim.count == settings.playerCount, $"{map}/{difficulty}: wrong crowd count");
        foreach (var position in sim.position)
            Require(venue.cells[FestivalVenue.CellAt(position)] == CellType.Open, $"{map}/{difficulty}: invalid crowd spawn");
        var schedule = new IncidentSchedule(400);
        RoundGenerator.Build(settings, venue, rng, schedule);
        var main = schedule.incidents.Where(i => i.trigger == null && !(i is BlockDoorIncident)).ToArray();
        Require(main.Length == settings.disasterCount, $"{map}/{difficulty}: wrong main disaster count");
        var blocks = schedule.incidents.OfType<BlockDoorIncident>().ToArray();
        Require(blocks.Length <= settings.maxBlockedDoors && venue.doors.Count - blocks.Length >= 3, $"{map}/{difficulty}: unsafe blocked-door count");
        Require(blocks.Count(b => b.door.gate == null) <= 1, $"{map}/{difficulty}: both permanent entrances blocked");
        // Exercise site resolution, gas sources and tower wreckage. Drunk movement uses native PerlinNoise,
        // so CrowdSimulation.Step itself must be verified inside Unity rather than this standalone runner.
        foreach (var incident in main) schedule.StartNow(incident, venue, sim);
        schedule.Update(TowerCollapseIncident.SwayTime + 0.04f, venue, sim, rng);
        venue.UpdateDoorFields(venue.doors.Count);
        foreach (var position in sim.position)
            Require(!float.IsNaN(position.x) && !float.IsNaN(position.y), $"{map}/{difficulty}: invalid simulated position");
    }

    static void Run()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        TurningPointCheck.Run();
        // Captured before extracting the legacy festival constructor; verifies cells, routes' geometry and spawn data.
        Require(Fingerprint(new FestivalVenue()) == 0x667B39CBF31A1580UL, "Default festival layout changed");
        foreach (VenueMap map in Enum.GetValues(typeof(VenueMap)))
        {
            CheckLayout(map);
            foreach (Difficulty difficulty in Enum.GetValues(typeof(Difficulty)))
            {
                CheckAdmissions(map, difficulty);
                foreach (int seed in new[] { 11, 21, 33 }) CheckRound(map, difficulty, seed);
            }
        }
        Console.WriteLine("PASS all 9 map/difficulty combinations across 3 seeds (27 rounds); legacy festival unchanged.");
    }

    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }
}
