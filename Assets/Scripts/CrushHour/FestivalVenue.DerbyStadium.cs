using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// Derby Stadium (<see cref="VenueMap.DerbyStadium"/>): a football bowl on the same 104 x 76 grid as the other
    /// venues, but built differently from <c>BuildStadium</c> (the City Stadium). Here each stand is a thin ring
    /// wall with a few narrow vomitories cut into it, so leaving the stand is itself a bottleneck before people even
    /// reach the concourse or a gate - not just an open block people walk around.
    ///
    /// The away end (north) is deliberately smaller and has fewer vomitories than the other three sides, mirroring
    /// how a real derby segregates and under-serves the away end: it's the venue's main risk spot.
    ///
    ///   y=72  fence ...... NW Gate .. Away Turnstiles .. NE Gate ......
    ///   y=66  stand wall ## Away End (2 vomitories, segregated) ##
    ///         W wall    West Stand      [ PITCH ]      East Stand    E wall
    ///   y=9   stand wall ## Home End (3 vomitories) ##
    ///   y=3   fence ...... SW Gate .. Home Turnstiles .. SE Gate ......
    /// </summary>
    public partial class FestivalVenue
    {
        const int DerbyWallLeft = 9, DerbyWallRight = 94, DerbyWallBottom = 9, DerbyWallTop = 66;
        static readonly RectInt DerbyPitch = new(32, 30, 40, 18);
        static readonly Color DerbyStandColor = new(0.33f, 0.34f, 0.38f);

        void BuildDerbyStadium()
        {
            // Turnstiles: always open, narrow.
            AddEntrance("Home Turnstiles", new RectInt(44, 3, 6, 1));
            AddEntrance("Away Turnstiles", new RectInt(44, 72, 6, 1));

            // Emergency gates: closed until the player opens them.
            AddGate("SW Gate", new RectInt(14, 3, 5, 1));
            AddGate("SE Gate", new RectInt(84, 3, 5, 1));
            AddGate("NW Gate", new RectInt(14, 72, 5, 1));
            AddGate("NE Gate", new RectInt(84, 72, 5, 1));
            AddGate("West Gate", new RectInt(3, 30, 1, 6));
            AddGate("East Gate", new RectInt(100, 30, 1, 6));

            // Pitch: not walkable, fans crush against the perimeter fencing around it.
            AddBlock("Pitch", DerbyPitch, new Color(0.19f, 0.46f, 0.21f), 0.1f, false);

            // Stand walls with vomitories. The away end gets fewer and narrower gaps than the other three sides.
            DerbyWall(DerbyWallBottom, DerbyWallLeft, DerbyWallRight, true, "Home End", new[] { (25, 4), (50, 6), (75, 4) });
            DerbyWall(DerbyWallTop, DerbyWallLeft, DerbyWallRight, true, "Away End", new[] { (30, 5), (65, 5) });
            DerbyWall(DerbyWallLeft, DerbyWallBottom + 1, DerbyWallTop - 1, false, "West Stand", new[] { (20, 5), (38, 5), (56, 5) });
            DerbyWall(DerbyWallRight, DerbyWallBottom + 1, DerbyWallTop - 1, false, "East Stand", new[] { (25, 5), (45, 5) });

            // Concourse ring: food vans in the south, the club shop in the north, plant and first aid on the sides.
            var stall = new Color(0.68f, 0.43f, 0.2f);
            AddBlock("Food Court", new RectInt(20, 5, 6, 2), stall, 2f, true);
            AddBlock("Food Court", new RectInt(60, 5, 6, 2), stall, 2f, true);
            AddBlock("Club Shop", new RectInt(60, 68, 10, 3), new Color(0.5f, 0.3f, 0.55f), 2.3f, true);
            AddBlock("Generator", new RectInt(5, 30, 3, 3), new Color(0.6f, 0.55f, 0.2f), 1.8f, true);
            AddBlock("Press Box", new RectInt(96, 30, 3, 4), new Color(0.3f, 0.34f, 0.4f), 2.2f, false);
            AddBlock("First Aid", new RectInt(5, 55, 3, 4), new Color(0.82f, 0.85f, 0.87f), 2f, false);

            // Floodlight masts, opposite corners - these can collapse like the other venues' light towers.
            AddBlock("Floodlight", new RectInt(5, 10, 2, 2), new Color(0.2f, 0.2f, 0.22f), 8f, false);
            AddBlock("Floodlight", new RectInt(96, 60, 2, 2), new Color(0.2f, 0.2f, 0.22f), 8f, false);
            lightTowers.AddRange(landmarks.FindAll(l => l.name == "Floodlight"));

            // Regions: one patrol per stand and per concourse strip.
            AddRegion("Home End", new RectInt(10, 10, 84, 19), new Color(1f, 0.9f, 0.3f));
            AddRegion("Away End", new RectInt(10, 48, 84, 18), new Color(0.95f, 0.35f, 0.4f));
            AddRegion("West Stand", new RectInt(10, 11, 22, 55), new Color(0.35f, 0.85f, 0.55f));
            AddRegion("East Stand", new RectInt(72, 11, 22, 55), new Color(0.5f, 0.65f, 1f));
            AddRegion("South Concourse", new RectInt(4, 4, 96, 5), new Color(0.25f, 0.8f, 0.85f));
            AddRegion("North Concourse", new RectInt(4, 67, 96, 5), new Color(0.8f, 0.45f, 0.9f));
            AddRegion("West Concourse", new RectInt(4, 9, 5, 58), new Color(0.7f, 0.75f, 0.35f));
            AddRegion("East Concourse", new RectInt(95, 9, 5, 58), new Color(0.6f, 0.95f, 0.65f));
            AssignRegions();

            // Where an incident can start. "the Generator" and "the Food Court" must stay exactly named like this -
            // GasLeakIncident looks sites up by these names on every venue.
            AddFireSite("the Food Court", new Vector2Int(23, 8));
            AddFireSite("the Generator", new Vector2Int(8, 31));
            AddFireSite("the Home End", new Vector2Int(50, 15));
            AddFireSite("the Away End", new Vector2Int(50, 58));
            AddFireSite("the West Stand", new Vector2Int(18, 38));
            AddFireSite("the East Stand", new Vector2Int(85, 38));

            // Where people start (weights add up to 1). The away end holds fewer fans - it's the smaller, segregated side.
            crowdZones.Add((new RectInt(10, 10, 84, 19), 0.30f, false)); // Home End
            crowdZones.Add((new RectInt(10, 48, 84, 18), 0.22f, false)); // Away End
            crowdZones.Add((new RectInt(10, 11, 22, 55), 0.18f, false)); // West Stand
            crowdZones.Add((new RectInt(72, 11, 22, 55), 0.18f, false)); // East Stand
            crowdZones.Add((new RectInt(4, 4, 96, 68), 0.12f, false));   // anywhere (concourse)
        }

        /// <summary>
        /// A 1-cell-thick stand wall with vomitory gaps cut into it. <paramref name="horizontal"/> picks a row
        /// (<paramref name="fixedCoord"/> = y, <paramref name="from"/>/<paramref name="to"/> = x) or a column
        /// (<paramref name="fixedCoord"/> = x, <paramref name="from"/>/<paramref name="to"/> = y).
        /// <paramref name="gaps"/> are (start, length) pairs along the wall, in increasing order, each a vomitory.
        /// </summary>
        void DerbyWall(int fixedCoord, int from, int to, bool horizontal, string name, (int start, int length)[] gaps)
        {
            int pos = from;
            foreach (var (start, length) in gaps)
            {
                if (start > pos) AddBlock(name, horizontal ? new RectInt(pos, fixedCoord, start - pos, 1) : new RectInt(fixedCoord, pos, 1, start - pos), DerbyStandColor, 2.5f, false);
                pos = start + length;
            }
            if (pos <= to) AddBlock(name, horizontal ? new RectInt(pos, fixedCoord, to - pos + 1, 1) : new RectInt(fixedCoord, pos, 1, to - pos + 1), DerbyStandColor, 2.5f, false);
        }
    }
}
