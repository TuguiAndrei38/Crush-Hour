using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    public partial class CrowdSimulation
    {
        sealed class EntryLane
        {
            public Vector2 street;
            public int inside;
            public int[] previous;
            public float nextArrival;
        }

        struct ArrivalWalk
        {
            public Vector2[] route;
            public int next;
            public float startsAt, speed;
        }

        ArrivalWalk[] arrivals;
        float arrivalTime;
        public float ArrivalDuration { get; private set; }
        public bool ArrivalsComplete => arrivals == null;
        public bool IsVisible(int person) => arrivals == null || arrivalTime >= arrivals[person].startsAt;

        /// <summary>Bring the round's actual attendees through the map's public entrances to their original spots.</summary>
        public void BeginArrivals(FestivalVenue venue)
        {
            var lanes = new List<EntryLane>();
            foreach (Door door in venue.doors)
            {
                if (door.gate != null || !door.IsOpen) continue;
                foreach (int exterior in door.beyond)
                {
                    int x = exterior % W, y = exterior / W;
                    int dx = Mathf.Clamp(x, door.rect.xMin, door.rect.xMax - 1) - x;
                    int dy = Mathf.Clamp(y, door.rect.yMin, door.rect.yMax - 1) - y;
                    int inside = FestivalVenue.Index(x + 2 * dx, y + 2 * dy);
                    if (!venue.IsWalkable(inside)) continue;
                    lanes.Add(new EntryLane
                    {
                        inside = inside,
                        street = FestivalVenue.CellCenter(exterior) - new Vector2(dx, dy) * 2.2f,
                        previous = venue.WalkablePathTree(inside),
                        nextArrival = ((lanes.Count * 3) % 7) * 0.14f
                    });
                }
            }

            arrivalTime = ArrivalDuration = 0f;
            arrivals = new ArrivalWalk[count];
            var turns = new List<Vector2>();
            for (int i = 0; i < count; i++)
            {
                int goal = CellOf(anchor[i]);
                EntryLane lane = null;
                float best = float.PositiveInfinity;
                foreach (EntryLane candidate in lanes)
                {
                    if (candidate.previous[goal] < 0) continue;
                    float cost = candidate.nextArrival + 0.07f * Vector2.Distance(candidate.street, anchor[i]);
                    if (cost >= best) continue;
                    best = cost;
                    lane = candidate;
                }
                // A custom map with an isolated spawn zone can still start its normal round.
                if (lane == null) { FinishArrivals(venue); return; }

                Vector2[] route = EntryRoute(lane, goal, anchor[i], turns);
                float speed = Mathf.Max(0.35f, settings.walkSpeed) * personalSpeed[i];
                float distance = 0f;
                for (int j = 1; j < route.Length; j++) distance += Vector2.Distance(route[j - 1], route[j]);
                arrivals[i] = new ArrivalWalk { route = route, next = 1, startsAt = lane.nextArrival, speed = speed };
                ArrivalDuration = Mathf.Max(ArrivalDuration, lane.nextArrival + distance / speed);
                lane.nextArrival += Mathf.Lerp(0.75f, 1.18f, Mathf.InverseLerp(0.8f, 1.2f, personalSpeed[i]));
                position[i] = route[0];
                velocity[i] = Vector2.zero;
            }
        }

        // Reverse the lane's shortest-path tree. Keep only the turns and offset the line
        // within its cells so people have some personal space on a shared path.
        static Vector2[] EntryRoute(EntryLane lane, int goal, Vector2 destination, List<Vector2> turns)
        {
            Vector2 offset = (destination - FestivalVenue.CellCenter(goal)) * 0.54f;
            turns.Clear();
            turns.Add(destination);
            int lastStep = 0;
            for (int cell = goal; cell != lane.inside; cell = lane.previous[cell])
            {
                int step = lane.previous[cell] - cell;
                if (step != lastStep) turns.Add(FestivalVenue.CellCenter(cell) + offset);
                lastStep = step;
            }
            turns.Add(FestivalVenue.CellCenter(lane.inside) + offset);
            turns.Add(lane.street + offset);
            turns.Reverse();
            return turns.ToArray();
        }

        /// <summary>Advance admissions alone. Large deltas are supported for elapsed time between camera cuts.</summary>
        public void StepArrivals(float dt, FestivalVenue venue)
        {
            if (arrivals == null || !(dt > 0f)) return;
            float previousTime = arrivalTime;
            arrivalTime += dt;
            if (arrivalTime >= ArrivalDuration) { FinishArrivals(venue); return; }
            for (int i = 0; i < count; i++)
            {
                ref ArrivalWalk walk = ref arrivals[i];
                if (arrivalTime < walk.startsAt || walk.next >= walk.route.Length) continue;
                float budget = (arrivalTime - Mathf.Max(previousTime, walk.startsAt)) * walk.speed;
                while (walk.next < walk.route.Length)
                {
                    Vector2 delta = walk.route[walk.next] - position[i];
                    float distance = delta.magnitude;
                    if (distance > budget)
                    {
                        Vector2 heading = delta / distance;
                        position[i] += heading * budget;
                        velocity[i] = heading * walk.speed;
                        break;
                    }
                    position[i] = walk.route[walk.next++];
                    budget -= distance;
                    velocity[i] = Vector2.zero;
                }
            }
        }

        public void FinishArrivals(FestivalVenue venue)
        {
            if (arrivals == null) return;
            Array.Copy(anchor, position, count);
            Array.Clear(velocity, 0, count);
            arrivals = null;
            BuildSpatialHash();
        }
    }
}
