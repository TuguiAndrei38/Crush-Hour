using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// A disaster during a round (fire, collapse, gas leak...). Lives in an <see cref="IncidentSchedule"/>, which starts it
    /// <see cref="startTime"/> seconds after the show starts (or after its <see cref="trigger"/> is met) and updates it
    /// every simulation step after that. Pure simulation, no Unity scene API, so it runs headless.
    /// </summary>
    public abstract class Incident
    {
        public string name;
        /// <summary>Region (index into venue.regions) where it happens; -1 until known (e.g. a random fire site).</summary>
        public int region = -1;
        /// <summary>Seconds to wait before starting: counted from the show's start, or from the moment the trigger is met.</summary>
        public float startTime;
        /// <summary>Optional: only start counting <see cref="startTime"/> once this is met (chained disasters).</summary>
        public IncidentTrigger trigger;

        public bool Started { get; private set; }
        /// <summary>Seconds since it started.</summary>
        public float Age { get; private set; }
        public virtual bool Active => Started;

        /// <summary>Where it is, for messages ("the Food Court").</summary>
        public virtual string SiteName => name;
        /// <summary>Cell the camera jumps to when it starts.</summary>
        public virtual Vector2Int FocusCell => default;
        /// <summary>Banner title, event feed line and banner advice when it starts.</summary>
        public virtual string Headline => $"{name.ToUpperInvariant()}!";
        public virtual string LogText => Headline;
        public virtual string Advice => "";
        /// <summary>
        /// Does this incident set off the evacuation (alarm, evacuation clock, people heading for the exits)? A fire does;
        /// a blocked exit on its own doesn't, people only find out when they get there.
        /// </summary>
        public virtual bool StartsEvacuation => true;
        /// <summary>What the megaphone police shout about it (the first evacuation incident of the round).</summary>
        public virtual string Announcement => Headline;
        /// <summary>
        /// Nobody notices it when it starts (a gas leak): the game shows no banner or feed line then; the incident is
        /// announced later, when someone detects it.
        /// </summary>
        public virtual bool Silent => false;

        /// <summary>The schedule it's in (set by <see cref="IncidentSchedule.Add"/> / <see cref="IncidentSchedule.StartNow"/>).</summary>
        internal IncidentSchedule owner;

        /// <summary>Starts another incident caused by this one (a gas cloud catching fire) at the end of this step.</summary>
        protected void StartFollowUp(Incident next) => owner?.QueueStart(next);

        internal float timer;
        internal bool timerArmed;

        internal void Begin(FestivalVenue venue, CrowdSimulation sim)
        {
            Started = true;
            Age = 0f;
            Start(venue, sim);
        }

        internal void Tick(float dt, FestivalVenue venue, CrowdSimulation sim)
        {
            Age += dt;
            Update(dt, venue, sim);
        }

        /// <summary>Called once, the step it begins.</summary>
        protected abstract void Start(FestivalVenue venue, CrowdSimulation sim);

        /// <summary>Called every step after the one it began in.</summary>
        protected virtual void Update(float dt, FestivalVenue venue, CrowdSimulation sim) { }
    }

    /// <summary>
    /// "Start when incident X has run for N seconds", "... when the fire has reached N cells", "... when the fire is
    /// within N m of this rectangle", or any combination (all set conditions must hold; at least one must be set).
    /// </summary>
    public class IncidentTrigger
    {
        public Incident after;
        public float afterSeconds;
        /// <summary>Total burning cells in the venue needed (0 = don't care).</summary>
        public int afterFireCells;
        /// <summary>When <see cref="fireNearRange"/> &gt; 0: the flames must be within that many cells of <see cref="fireNear"/>.</summary>
        public RectInt fireNear;
        public int fireNearRange;

        public bool IsMet(FestivalVenue venue)
        {
            if (after != null && (!after.Started || after.Age < afterSeconds)) return false;
            if (afterFireCells > 0 && venue.fireCells.Count < afterFireCells) return false;
            if (fireNearRange > 0 && venue.FireDistanceTo(fireNear) > fireNearRange) return false;
            return after != null || afterFireCells > 0 || fireNearRange > 0;
        }
    }

    /// <summary>
    /// A fire at one of venue.fireSites (a random one, picked with the game's rng the moment it starts, unless a site is
    /// given). Spreading, embers and smoke are the venue's (<see cref="FestivalVenue.UpdateFire"/>), shared by every fire,
    /// so the schedule spreads the fire once per step rather than each FireIncident doing it.
    /// </summary>
    public class FireIncident : Incident
    {
        readonly System.Random rng;
        readonly int siteIndex;
        readonly bool fixedCell;
        public string siteName;
        public Vector2Int cell;

        /// <param name="siteIndex">Index into venue.fireSites, or -1 for a random site chosen when it starts.</param>
        public FireIncident(System.Random rng, int siteIndex = -1)
        {
            this.rng = rng;
            this.siteIndex = siteIndex;
            name = "Fire";
        }

        /// <summary>A fire at exactly <paramref name="at"/> (not a fire site), e.g. where a gas cloud met the flames.</summary>
        public FireIncident(Vector2Int at, string site, int region)
        {
            fixedCell = true;
            cell = at;
            siteName = site;
            this.region = region;
            name = "Fire";
        }

        /// <summary>A fire at the fire site called <paramref name="site"/> (e.g. "the Food Court").</summary>
        public static FireIncident At(FestivalVenue venue, string site, System.Random rng) =>
            new(rng, venue.fireSites.FindIndex(s => s.name == site));

        public override string SiteName => siteName ?? "an unknown spot";
        public override Vector2Int FocusCell => cell;
        public override string Headline => $"FIRE AT {SiteName.ToUpperInvariant()}!";
        public override string LogText => $"FIRE at {SiteName}! People nearby are running from it.";
        public override string Advice => "Open the gates and send your team to the crowds.";
        public override string Announcement => $"Fire at {SiteName}!";

        protected override void Start(FestivalVenue venue, CrowdSimulation sim)
        {
            if (fixedCell)
            {
                venue.StartFire(cell);
                return;
            }
            int index = siteIndex >= 0 ? siteIndex : rng.Next(venue.fireSites.Count);
            var site = venue.fireSites[index];
            siteName = site.name;
            cell = site.cell;
            region = site.region;
            venue.StartFire(cell);
        }
    }

    /// <summary>
    /// Something blocks one door (a delivery truck parked across it, collapsed scaffolding): it can't be used, and people
    /// heading for it find it shut and pick another, as with a closed gate. Staff clear it (see
    /// <see cref="FestivalVenue.ClearDoor"/>): one patrolman or police officer within 2 m takes 8 s, two take 4 s.
    /// </summary>
    public class BlockDoorIncident : Incident
    {
        public static readonly string[] Reasons = { "Delivery truck", "Collapsed scaffolding" };

        public readonly Door door;
        public readonly string reason;

        public BlockDoorIncident(FestivalVenue venue, Door door, string reason)
        {
            this.door = door;
            this.reason = reason;
            name = "Blocked exit";
            int front = venue.DoorFrontCell(door);
            region = front >= 0 ? venue.regionOf[front] : -1;
        }

        /// <summary>Blocks the door called <paramref name="doorName"/> (e.g. "Main Entrance").</summary>
        public static BlockDoorIncident At(FestivalVenue venue, string doorName, string reason) =>
            new(venue, venue.doors.Find(d => d.name == doorName), reason);

        /// <summary>A random door and reason, picked with <paramref name="rng"/>.</summary>
        public static BlockDoorIncident RandomDoor(FestivalVenue venue, System.Random rng) =>
            new(venue, venue.doors[rng.Next(venue.doors.Count)], Reasons[rng.Next(Reasons.Length)]);

        /// <summary>Still blocked (it ends when staff clear it).</summary>
        public override bool Active => Started && door.blocked;
        public override bool StartsEvacuation => false;
        public override string SiteName => door.name;
        public override Vector2Int FocusCell => Vector2Int.FloorToInt(door.Center);
        public override string Headline => $"{door.name.ToUpperInvariant()} BLOCKED!";
        public override string LogText => $"{door.name} is blocked: {reason}. Nobody can get out that way until it's cleared.";
        public override string Advice => $"{reason}. Send a patrol or police officer to clear it ({FestivalVenue.DoorClearTime:0} s).";

        protected override void Start(FestivalVenue venue, CrowdSimulation sim)
        {
            venue.SetDoorBlocked(door, true, reason);
            foreach (var p in door.rect.allPositionsWithin) sim.Evict(FestivalVenue.Index(p.x, p.y), venue);
        }
    }

    /// <summary>
    /// A light tower comes down. When it starts it sways for <see cref="SwayTime"/> s (warning: banner, wobble; zone
    /// orders work), then falls in <see cref="direction"/>: the <see cref="FestivalVenue.TowerFallLength"/> × 2 m strip
    /// under it becomes wreckage (low obstacles; a door under it is blocked, see <see cref="BlockDoorIncident"/>), everyone
    /// standing there is injured (<see cref="CrowdSimulation.InjuredByCollapse"/>) and everyone within
    /// <see cref="ShockRadius"/> m gets a big fright (<see cref="CrowdSimulation.Shock"/>) and heads out.
    /// <see cref="WhenFireNear"/> chains it to the fire: it starts falling 10-20 s after the flames get within 4 m.
    /// </summary>
    public class TowerCollapseIncident : Incident
    {
        public const float SwayTime = 6f, ShockRadius = 12f;
        public const int FireNearRange = 4;
        public const string WreckReason = "Collapsed light tower";
        static readonly Vector2Int[] Directions = { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };

        public readonly int towerIndex;
        public readonly Landmark tower;
        /// <summary>Which way it falls (grid x, y; one of the 4 axis directions).</summary>
        public readonly Vector2Int direction;
        /// <summary>Cells the wreckage lands on.</summary>
        public readonly List<int> wreck;
        /// <summary>Doors the wreckage blocked.</summary>
        public readonly List<Door> blockedDoors = new();
        readonly string side, regionName;

        public bool Collapsed { get; private set; }
        public int Crushed { get; private set; }
        public int Shaken { get; private set; }
        /// <summary>Set by the game once it has shown the collapse (feed line, banner, wreckage).</summary>
        public bool collapseShown;

        /// <param name="towerIndex">Index into venue.lightTowers: 0 = west (30,46), 1 = east (72,46).</param>
        public TowerCollapseIncident(FestivalVenue venue, int towerIndex, Vector2Int direction)
        {
            this.towerIndex = towerIndex;
            this.direction = direction;
            tower = venue.lightTowers[towerIndex];
            wreck = FestivalVenue.TowerFallCells(tower, direction);
            name = "Light tower collapse";
            side = tower.rect.center.x < FestivalVenue.Width * 0.5f ? "west" : "east";
            region = venue.regionOf[FestivalVenue.CellAt(tower.rect.center)];
            regionName = region >= 0 ? venue.regions[region].name : "the stage";
        }

        /// <summary>Falls in a random direction (picked now, with <paramref name="rng"/>).</summary>
        public static TowerCollapseIncident RandomDirection(FestivalVenue venue, int towerIndex, System.Random rng) =>
            new(venue, towerIndex, Directions[rng.Next(Directions.Length)]);

        /// <summary>Chained to the fire: falls 10-20 s (sway included) after the flames get within 4 m of it.</summary>
        public static TowerCollapseIncident WhenFireNear(FestivalVenue venue, int towerIndex, System.Random rng)
        {
            var t = RandomDirection(venue, towerIndex, rng);
            float fallsAfter = 10f + (float)rng.NextDouble() * 10f;
            t.startTime = Mathf.Max(0f, fallsAfter - SwayTime);
            t.trigger = new IncidentTrigger { fireNear = t.tower.rect, fireNearRange = FireNearRange };
            return t;
        }

        /// <summary>0 when it starts swaying, 1 when it falls.</summary>
        public float SwayProgress => Collapsed ? 1f : Mathf.Clamp01(Age / SwayTime);
        public override string SiteName => $"the {side} light tower";
        public override Vector2Int FocusCell => Vector2Int.FloorToInt(tower.rect.center);
        public override string Headline => "LIGHT TOWER IS SWAYING!";
        public override string LogText => $"The {side} light tower ({regionName} zone) is swaying! It will come down in {SwayTime:0} s.";
        public override string Advice => "Get people away from it: select the zone patrol and order the zone out.";
        public override string Announcement => $"A light tower is coming down by {regionName}!";

        /// <summary>Middle of the strip it lands on.</summary>
        public Vector2 WreckCenter
        {
            get
            {
                Vector2 sum = Vector2.zero;
                foreach (int c in wreck) sum += FestivalVenue.CellCenter(c);
                return wreck.Count > 0 ? sum / wreck.Count : tower.rect.center;
            }
        }

        protected override void Start(FestivalVenue venue, CrowdSimulation sim) { } // swaying: the warning

        protected override void Update(float dt, FestivalVenue venue, CrowdSimulation sim)
        {
            if (Collapsed || Age < SwayTime) return;
            Collapsed = true;
            Crushed = sim.CrushUnder(wreck);
            venue.AddWreckage(wreck, WreckReason, blockedDoors);
            Shaken = sim.Shock(WreckCenter, ShockRadius);
        }
    }

    /// <summary>
    /// A gas leak (the Generator, or the gas bottles at the Food Court): the venue's <see cref="FestivalVenue.gas"/> field
    /// spreads from the leak (slower than smoke, no wind) for <see cref="LeakDuration"/> s, then thins out. It's
    /// <see cref="Silent"/>: no flames, nobody sees it, nobody runs from it (CrowdSimulation.gasDose: people cough, then
    /// collapse). The crowd only reacts to people falling around them or to staff telling them. It's
    /// <see cref="Detected"/> when someone coughs or a patrol/police officer has gas in their ring (<see cref="Smells"/>);
    /// the game then raises the alarm. If gas of <see cref="IgniteLevel"/> reaches the flames it catches: the thick part
    /// flashes into fire and a <see cref="FireIncident"/> starts where it met the flames; the gas is gone.
    /// </summary>
    public class GasLeakIncident : Incident
    {
        public static readonly string[] Sites = { "the Generator", "the Food Court" };
        public const float LeakDuration = 90f, DetectLevel = 0.05f, IgniteLevel = 0.25f, FlashLevel = 0.6f;

        readonly int siteIndex;
        readonly System.Random rng;
        public string siteName;
        public Vector2Int cell;

        public bool Detected { get; private set; }
        /// <summary>Who noticed it ("the Backstage patrol", "coughing people"); set by <see cref="Detect"/>.</summary>
        public string detectedBy;
        public float detectedAt = -1f;
        public bool Ignited { get; private set; }
        /// <summary>The fire it started, once it caught.</summary>
        public FireIncident ignition;

        /// <param name="site">One of <see cref="Sites"/>, or null for a random one (picked when it starts).</param>
        public GasLeakIncident(System.Random rng, string site = null)
        {
            this.rng = rng;
            siteIndex = site == null ? -1 : System.Array.IndexOf(Sites, site);
            name = "Gas leak";
        }

        public override bool StartsEvacuation => false; // the game starts the evacuation when it's detected
        public override bool Silent => true;
        public override string SiteName => siteName ?? "an unknown spot";
        public override Vector2Int FocusCell => cell;
        public override string Headline => $"GAS LEAK AT {SiteName.ToUpperInvariant()}!";
        public override string LogText => $"GAS LEAK at {SiteName}! People in it cough and collapse; nobody runs from it on their own.";
        public override string Advice => "Order the zones nearby out and keep people out of the yellow cloud. Keep it away from the flames.";
        public override string Announcement => $"Gas leak at {SiteName}!";

        /// <summary>Marks it as noticed (once).</summary>
        public void Detect(string by, float time)
        {
            if (Detected) return;
            Detected = true;
            detectedBy = by;
            detectedAt = time;
        }

        /// <summary>Would a patrolman or police officer at <paramref name="p"/> with ring <paramref name="radius"/> smell it?</summary>
        public static bool Smells(FestivalVenue venue, Vector2 p, float radius) => venue.GasNear(p, radius, DetectLevel);

        protected override void Start(FestivalVenue venue, CrowdSimulation sim)
        {
            int index = siteIndex >= 0 ? siteIndex : rng.Next(Sites.Length);
            var site = venue.fireSites.Find(s => s.name == Sites[index]);
            siteName = site.name;
            cell = site.cell;
            region = site.region;
            venue.gasSources.Add(FestivalVenue.Index(cell.x, cell.y));
        }

        protected override void Update(float dt, FestivalVenue venue, CrowdSimulation sim)
        {
            if (Ignited) return;
            int source = FestivalVenue.Index(cell.x, cell.y);
            if (Age >= LeakDuration) venue.gasSources.Remove(source);
            if (venue.fireCells.Count == 0 || !venue.HasGas) return;

            // Gas thick enough to burn, next to the flames?
            int contact = -1;
            for (int c = 0; c < FestivalVenue.CellCount && contact < 0; c++)
                if (venue.gas[c] >= IgniteLevel && venue.fireDistance[c] <= 1 && venue.cells[c] != CellType.Fire) contact = c;
            if (contact < 0) return;

            Ignited = true;
            var flash = new List<int>();
            for (int c = 0; c < FestivalVenue.CellCount; c++)
                if (venue.gas[c] >= FlashLevel) flash.Add(c);
            venue.ClearGas();
            venue.StartFires(flash);
            var at = new Vector2Int(contact % FestivalVenue.Width, contact / FestivalVenue.Width);
            ignition = new FireIncident(at, $"the gas cloud by {venue.DescribeLocation(contact)}", venue.regionOf[contact]);
            StartFollowUp(ignition);
        }
    }

    /// <summary>
    /// The incidents of a round. <see cref="Update"/> runs every simulation step while the show is live:
    /// 1. pending incidents whose trigger is met count down their <see cref="Incident.startTime"/> and start at 0;
    /// 2. if anything had already started before this step, the venue's fire spreads once and every incident started
    ///    before this step is updated. (An incident's first step is only its Start; the old single-fire code worked the
    ///    same way, which keeps the rng sequence, and so every seed's result, unchanged.)
    /// </summary>
    public class IncidentSchedule
    {
        public readonly List<Incident> incidents = new();
        public int maxFireCells;
        public bool AnyStarted { get; private set; }
        readonly List<Incident> startedThisStep = new();
        readonly List<Incident> queued = new();

        public IncidentSchedule(int maxFireCells) => this.maxFireCells = maxFireCells;

        public void Add(Incident incident)
        {
            incident.owner = this;
            incidents.Add(incident);
        }

        /// <summary>Starts <paramref name="incident"/> at the end of the current <see cref="Update"/> (follow-ups).</summary>
        internal void QueueStart(Incident incident) => queued.Add(incident);

        /// <summary>Starts <paramref name="incident"/> right away (headless tests). From the next step on it's updated as usual.</summary>
        public void StartNow(Incident incident, FestivalVenue venue, CrowdSimulation sim)
        {
            if (!incidents.Contains(incident)) Add(incident);
            incident.Begin(venue, sim);
            AnyStarted = true;
        }

        /// <summary>
        /// One simulation step. Fills <paramref name="started"/> (when given) with the incidents that began this step.
        /// Returns true when the fire changed (spread or new spot fires), like <see cref="FestivalVenue.UpdateFire"/>.
        /// </summary>
        public bool Update(float dt, FestivalVenue venue, CrowdSimulation sim, System.Random rng, List<Incident> started = null)
        {
            started?.Clear();
            startedThisStep.Clear();
            bool wasRunning = AnyStarted;

            foreach (var incident in incidents)
            {
                if (incident.Started) continue;
                if (incident.trigger != null && !incident.trigger.IsMet(venue)) continue;
                if (!incident.timerArmed)
                {
                    incident.timer = incident.startTime;
                    incident.timerArmed = true;
                }
                incident.timer -= dt;
                if (incident.timer > 0f) continue;
                incident.Begin(venue, sim);
                AnyStarted = true;
                startedThisStep.Add(incident);
                started?.Add(incident);
            }

            if (!wasRunning) return false;
            bool fireChanged = venue.UpdateFire(dt, rng, maxFireCells);
            venue.UpdateGas(dt); // no-op without gas
            foreach (var incident in incidents)
                if (incident.Started && !startedThisStep.Contains(incident)) incident.Tick(dt, venue, sim);

            // Incidents caused by others this step (gas catching fire) start now; they're updated from the next step.
            if (queued.Count > 0)
            {
                foreach (var incident in queued)
                {
                    Add(incident);
                    incident.Begin(venue, sim);
                    started?.Add(incident);
                }
                queued.Clear();
                fireChanged = true;
            }
            return fireChanged;
        }
    }
}
