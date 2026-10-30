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

    /// <summary>"Start when incident X has run for N seconds (and/or the fire has reached N cells)."</summary>
    public class IncidentTrigger
    {
        public Incident after;
        public float afterSeconds;
        /// <summary>Total burning cells in the venue needed (0 = don't care).</summary>
        public int afterFireCells;

        public bool IsMet(FestivalVenue venue) =>
            after != null && after.Started && after.Age >= afterSeconds &&
            (afterFireCells <= 0 || venue.fireCells.Count >= afterFireCells);
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
        public string siteName;
        public Vector2Int cell;

        /// <param name="siteIndex">Index into venue.fireSites, or -1 for a random site chosen when it starts.</param>
        public FireIncident(System.Random rng, int siteIndex = -1)
        {
            this.rng = rng;
            this.siteIndex = siteIndex;
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

        protected override void Start(FestivalVenue venue, CrowdSimulation sim)
        {
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

        public IncidentSchedule(int maxFireCells) => this.maxFireCells = maxFireCells;

        public void Add(Incident incident) => incidents.Add(incident);

        /// <summary>Starts <paramref name="incident"/> right away (headless tests). From the next step on it's updated as usual.</summary>
        public void StartNow(Incident incident, FestivalVenue venue, CrowdSimulation sim)
        {
            if (!incidents.Contains(incident)) incidents.Add(incident);
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
            foreach (var incident in incidents)
                if (incident.Started && !startedThisStep.Contains(incident)) incident.Tick(dt, venue, sim);
            return fireChanged;
        }
    }
}
