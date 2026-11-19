using System;
using UnityEngine;

namespace CrushHour
{
    public partial class CrowdSimulation
    {
        enum Habit { Visiting, Watching, Commuting }

        struct Outing
        {
            public Habit habit;
            public Vector2 attraction, target, facing;
            public float nextDecision;
            public bool walking;
        }

        Outing[] outings;
        System.Random outingRandom;

        // An attendee's movements must not consume the round generator's random stream.
        void InitializeAmbient(FestivalVenue venue)
        {
            outings = new Outing[count];
            int firstCell = count > 0 ? CellOf(anchor[0]) : 0;
            outingRandom = new System.Random((firstCell * 397) ^ count ^ (int)venue.map);
            for (int i = 0; i < count; i++)
            {
                ref var outing = ref outings[i];
                var region = venue.RegionAt(CellOf(anchor[i]));
                string name = region?.name ?? "";
                string focus = null;
                if (venue.map == VenueMap.CityStadium && name.EndsWith(" Stand", StringComparison.Ordinal))
                    focus = "Playing Field";
                else if (venue.map == VenueMap.SummerFestival && (name == "Main Stage" || name == "Dance Tent"))
                    focus = name;
                else if (venue.map == VenueMap.MetroConcourse && name.EndsWith("Platform", StringComparison.Ordinal))
                    focus = "Metro Train";

                if (focus != null)
                {
                    outing.habit = venue.map == VenueMap.MetroConcourse ? Habit.Commuting : Habit.Watching;
                    float nearest = float.PositiveInfinity;
                    foreach (var landmark in venue.landmarks)
                    {
                        if (landmark.name != focus) continue;
                        Vector2 center = landmark.rect.center;
                        float distance = (center - anchor[i]).sqrMagnitude;
                        if (distance >= nearest) continue;
                        nearest = distance;
                        outing.attraction = center;
                    }
                }
                float angle = Range(0f, 2f * Mathf.PI);
                outing.facing = outing.attraction == Vector2.zero
                    ? new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) : (outing.attraction - anchor[i]).normalized;
                outing.nextDecision = Dwell(outing.habit) * Range(-0.6f, 1f);
            }
        }

        float Range(float low, float high) => Mathf.Lerp(low, high, (float)outingRandom.NextDouble());

        float Dwell(Habit habit) => habit switch
        {
            Habit.Watching => Range(7f, 20f),
            Habit.Commuting => Range(2f, 6f),
            _ => Range(3f, 10f)
        };

        public Vector2 RestFacing(int i) => outings[i].facing;
        public bool WatchingShow(int i) => outings[i].habit == Habit.Watching && !outings[i].walking
            && outings[i].nextDecision - simTime < 2.8f && !drunk[i] && !ShowStopped;

        Vector2 AmbientVelocity(int i, Vector2 p, FestivalVenue venue, float localDensity)
        {
            ref var outing = ref outings[i];
            if (!outing.walking && simTime >= outing.nextDecision)
            {
                outing.walking = TryWalk(i, p, venue, ref outing);
                if (!outing.walking) Rest(i, p, ref outing);
            }
            if (!outing.walking) return Vector2.zero;

            Vector2 to = outing.target - p;
            float distance = to.magnitude;
            if (distance < 0.35f || simTime >= outing.nextDecision)
            {
                Rest(i, p, ref outing);
                return Vector2.zero;
            }
            float pace = settings.walkSpeed * personalSpeed[i] * Mathf.Clamp(1.08f - 0.12f * localDensity, 0.5f, 1f);
            return to / distance * Mathf.Min(pace, 2f * distance);
        }

        bool TryWalk(int i, Vector2 from, FestivalVenue venue, ref Outing outing)
        {
            // Short uninterrupted paths are picked only when a stop ends. Platform walks follow the
            // track, spectators shift places, and visitors range farther around their part of the venue.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                Vector2 offset = outing.habit switch
                {
                    Habit.Commuting => new Vector2(Range(-14f, 14f), Range(-2.5f, 2.5f)),
                    Habit.Watching => new Vector2(Range(-3.5f, 3.5f), Range(-3f, 3f)),
                    _ => new Vector2(Range(-8f, 8f), Range(-8f, 8f))
                };
                Vector2 target = anchor[i] + offset;
                Vector2 delta = target - from;
                float length = delta.magnitude;
                if (length < 1.5f || !ClearStroll(venue, from, delta, length)) continue;
                outing.target = target;
                outing.nextDecision = simTime + 3f + length / Mathf.Max(0.25f, settings.walkSpeed * personalSpeed[i] * 0.55f);
                return true;
            }
            return false;
        }

        bool ClearStroll(FestivalVenue venue, Vector2 from, Vector2 delta, float length)
        {
            var shoulder = new Vector2(-delta.y, delta.x) * (settings.radius / length);
            int steps = Mathf.CeilToInt(length / 0.45f);
            for (int step = 1; step <= steps; step++)
            {
                Vector2 center = from + delta * ((float)step / steps);
                if (!CanLoiter(venue, center) || !CanLoiter(venue, center + shoulder) || !CanLoiter(venue, center - shoulder))
                    return false;
            }
            return true;
        }

        static bool CanLoiter(FestivalVenue venue, Vector2 p)
        {
            int cell = CellOf(p);
            return venue.regionOf[cell] >= 0 && venue.cells[cell] == CellType.Open && !venue.blockedDoorCell[cell];
        }

        void Rest(int i, Vector2 p, ref Outing outing)
        {
            outing.walking = false;
            outing.nextDecision = simTime + Dwell(outing.habit);
            if (outing.attraction != Vector2.zero && outingRandom.NextDouble() < 0.8)
            {
                outing.facing = (outing.attraction - p).normalized;
                return;
            }
            // Use the existing spatial hash to turn toward someone nearby during a pause.
            float nearest = 1.5f * 1.5f;
            int cell = CellOf(p);
            int cx = cell % W, cy = cell / W;
            for (int y = Mathf.Max(0, cy - 1); y <= Mathf.Min(H - 1, cy + 1); y++)
                for (int x = Mathf.Max(0, cx - 1); x <= Mathf.Min(W - 1, cx + 1); x++)
                {
                    int c = FestivalVenue.Index(x, y);
                    int end = cellStart[c] + cellCount[c];
                    for (int s = cellStart[c]; s < end; s++)
                    {
                        int other = sorted[s];
                        if (other == i || state[other] != AgentState.Active || evacuating[other]) continue;
                        Vector2 to = position[other] - p;
                        float sq = to.sqrMagnitude;
                        if (sq < 0.01f || sq >= nearest) continue;
                        nearest = sq;
                        outing.facing = to.normalized;
                    }
                }
        }
    }
}
