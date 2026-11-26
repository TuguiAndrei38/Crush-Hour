using System;
using UnityEngine;

namespace CrushHour
{
    /// <summary>Once per evacuation second, keep five seconds of lead-in and five of follow-through
    /// around the strongest observed event. The stored clip owns its data even as the round continues.</summary>
    public sealed class TurningPointReplay
    {
        public const int CellSize = 4;
        public const int Columns = (FestivalVenue.Width + CellSize - 1) / CellSize;
        public const int Rows = (FestivalVenue.Height + CellSize - 1) / CellSize;
        const int WindowSeconds = 5;
        const float ApproachRadius = 8f;

        public sealed class Frame
        {
            public float Time { get; internal set; }
            public int OpenMask { get; internal set; }
            public int Safe { get; internal set; }
            public int Injured { get; internal set; }
            // Bottom-left origin. Density counts active people, Hurt counts fallen people.
            public readonly ushort[] Density = new ushort[Columns * Rows], Hurt = new ushort[Columns * Rows];
            public readonly int[] Approaching, Exited;

            internal Frame(int doors)
            {
                Approaching = new int[doors];
                Exited = new int[doors];
            }

            internal void CopyFrom(Frame source)
            {
                Time = source.Time;
                Safe = source.Safe;
                Injured = source.Injured;
                OpenMask = source.OpenMask;
                Array.Copy(source.Density, Density, Density.Length);
                Array.Copy(source.Hurt, Hurt, Hurt.Length);
                Array.Copy(source.Approaching, Approaching, Approaching.Length);
                Array.Copy(source.Exited, Exited, Exited.Length);
            }
        }

        // Adverse outcomes take priority over flow; counts break ties within each category.
        enum Highlight { Crowd, Flow, Queue, Injuries }

        readonly FestivalVenue venue;
        readonly Frame[] history, clip;
        readonly int[] slowAtExit, injuryTotals;
        readonly int[][] regionInjuries;
        bool[] seenInjured;
        int samples, bestCount = -1;
        Highlight bestKind;
        bool finished;
        float nextSample;

        public int FrameCount { get; private set; }
        public float MomentTime { get; private set; }
        public Vector2 Focus { get; private set; }
        public int FocusDoor { get; private set; } = -1;
        public int AlternateDoor { get; private set; } = -1;
        public string Title { get; private set; }
        public string Caption { get; private set; }
        public Frame GetFrame(int index) => index >= 0 && index < FrameCount
            ? clip[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public TurningPointReplay(FestivalVenue venue)
        {
            this.venue = venue;
            history = new Frame[WindowSeconds + 1];
            // Allow for a last, fractional-second frame at the end of a short round.
            clip = new Frame[2 * WindowSeconds + 2];
            regionInjuries = new int[history.Length][];
            slowAtExit = new int[venue.doors.Count];
            injuryTotals = new int[venue.regions.Count];
            for (int i = 0; i < history.Length; i++)
            {
                history[i] = new Frame(venue.doors.Count);
                regionInjuries[i] = new int[venue.regions.Count];
            }
            for (int i = 0; i < clip.Length; i++) clip[i] = new Frame(venue.doors.Count);
        }

        public void Record(float time, CrowdSimulation sim)
        {
            if (finished || time < nextSample) return;
            Capture(time, sim);
            nextSample = Mathf.Floor(time) + 1f;
        }

        public void Finish(float time, CrowdSimulation sim)
        {
            if (finished) return;
            if (samples == 0 || time > history[(samples - 1) % history.Length].Time)
                Capture(time, sim);
            finished = true;
        }

        void Capture(float time, CrowdSimulation sim)
        {
            int index = samples % history.Length;
            Frame now = history[index];
            now.Time = time;
            now.OpenMask = venue.OpenDoorMask;
            now.Safe = now.Injured = 0;
            Array.Copy(sim.exitsByDoor, now.Exited, now.Exited.Length);
            Array.Clear(now.Density, 0, now.Density.Length);
            Array.Clear(now.Hurt, 0, now.Hurt.Length);
            Array.Clear(now.Approaching, 0, now.Approaching.Length);
            Array.Clear(slowAtExit, 0, slowAtExit.Length);
            seenInjured ??= new bool[sim.count];
            for (int i = 0; i < sim.count; i++)
            {
                if (sim.state[i] == AgentState.Safe) { now.Safe++; continue; }
                Vector2 position = sim.position[i];
                int cell = FestivalVenue.CellAt(position);
                int bin = (cell / FestivalVenue.Width / CellSize) * Columns + (cell % FestivalVenue.Width / CellSize);
                if (sim.state[i] == AgentState.Injured)
                {
                    now.Hurt[bin]++;
                    now.Injured++;
                    // A closing gate can displace a fallen person. Count an injury just once.
                    if (!seenInjured[i])
                    {
                        seenInjured[i] = true;
                        int region = venue.regionOf[cell];
                        if (region >= 0) injuryTotals[region]++;
                    }
                    continue;
                }
                now.Density[bin]++;
                int door = sim.targetDoor[i];
                if (door < 0 || !sim.evacuating[i] || (position - venue.doors[door].Center).sqrMagnitude > ApproachRadius * ApproachRadius) continue;
                now.Approaching[door]++;
                if (sim.velocity[i].sqrMagnitude < 0.5f * 0.5f) slowAtExit[door]++;
            }
            Array.Copy(injuryTotals, regionInjuries[index], injuryTotals.Length);

            samples++;
            int first = Math.Max(0, samples - history.Length);
            int oldest = first % history.Length;
            if (SelectMoment(now, history[oldest], regionInjuries[index], regionInjuries[oldest]))
            {
                MomentTime = now.Time;
                FrameCount = 0;
                for (int i = first; i < samples; i++)
                    clip[FrameCount++].CopyFrom(history[i % history.Length]);
            }
            else if (time <= MomentTime + WindowSeconds + 0.05f && FrameCount < clip.Length)
                clip[FrameCount++].CopyFrom(now);
        }

        bool Improves(Highlight kind, int strength) =>
            kind > bestKind || kind == bestKind && strength > bestCount;

        bool SelectMoment(Frame now, Frame before, int[] injuredNow, int[] injuredBefore)
        {
            string interval = null;
            bool changed = false;
            for (int r = 0; r < injuredNow.Length; r++)
            {
                int gain = injuredNow[r] - injuredBefore[r];
                if (gain <= 0 || !Improves(Highlight.Injuries, gain)) continue;
                var region = venue.regions[r];
                interval ??= (now.Time - before.Time).ToString("0.#");
                bestKind = Highlight.Injuries;
                bestCount = gain;
                FocusDoor = AlternateDoor = -1;
                Focus = region.post + new Vector2(0.5f, 0.5f);
                Title = $"Injuries in {region.name}";
                Caption = $"{(gain == 1 ? "One person was" : $"{gain} people were")} injured here in {interval}s. On a retry, check this zone early and direct people toward an open route.";
                changed = true;
            }

            for (int d = 0; d < now.Approaching.Length; d++)
            {
                int approaching = now.Approaching[d];
                int growth = approaching - before.Approaching[d];
                if (approaching >= 12 && growth >= 6 && slowAtExit[d] >= Math.Max(4, approaching / 3) && Improves(Highlight.Queue, approaching))
                {
                    bestKind = Highlight.Queue;
                    bestCount = approaching;
                    FocusDoor = d;
                    Focus = venue.doors[d].Center;
                    AlternateDoor = FindQuieterExit(now, d);
                    Title = $"Queue at {venue.doors[d].name}";
                    Caption = $"The approach grew from {before.Approaching[d]} to {approaching} people. {slowAtExit[d]} were moving below 0.5 m/s. ";
                    Caption += AlternateDoor >= 0
                        ? $"{venue.doors[AlternateDoor].name} was open with {now.Approaching[AlternateDoor]} approaching. Consider splitting the next zone order."
                        : "Try stationing a patrol on this approach.";
                    changed = true;
                }
                int departures = now.Exited[d] - before.Exited[d];
                if (departures <= 0 || !Improves(Highlight.Flow, departures)) continue;
                interval ??= (now.Time - before.Time).ToString("0.#");
                bestKind = Highlight.Flow;
                bestCount = departures;
                FocusDoor = d;
                AlternateDoor = -1;
                Focus = venue.doors[d].Center;
                Title = $"People reached safety at {venue.doors[d].name}";
                Caption = $"{departures} {(departures == 1 ? "person left" : "people left")} through this exit in {interval}s. Keep this approach clear when directing the next zone.";
                changed = true;
            }

            if (bestKind != Highlight.Crowd) return changed;
            int peak = 0;
            for (int bin = 1; bin < now.Density.Length; bin++)
                if (now.Density[bin] > now.Density[peak]) peak = bin;
            if (!Improves(Highlight.Crowd, now.Density[peak])) return false;
            bestCount = now.Density[peak];
            if (bestCount == 0)
            {
                Focus = new Vector2(FestivalVenue.Width * 0.5f, FestivalVenue.Height * 0.5f);
                Title = "Evacuation snapshot";
                Caption = $"{now.Safe} reached safety and {now.Injured} were injured by this point.";
                return true;
            }
            Focus = new Vector2((peak % Columns + 0.5f) * CellSize, (peak / Columns + 0.5f) * CellSize);
            string zone = venue.RegionAt(FestivalVenue.CellAt(Focus))?.name ?? "the venue boundary";
            Title = "Crowd concentration";
            Caption = $"{bestCount} {(bestCount == 1 ? "person was" : "people were")} in a {CellSize} m square near {zone}. Watch this area when positioning patrols.";
            return true;
        }

        int FindQuieterExit(Frame now, int source)
        {
            int closest = -1;
            float best = float.PositiveInfinity;
            int front = venue.DoorFrontCell(venue.doors[source]);
            if (front < 0) return -1;
            for (int d = 0; d < venue.doors.Count; d++)
            {
                var door = venue.doors[d];
                if (d == source || (now.OpenMask & (1 << d)) == 0 || now.Approaching[d] * 3 > now.Approaching[source]) continue;
                float distance = door.field.distance[front];
                if (distance >= best) continue;
                best = distance;
                closest = d;
            }
            return closest;
        }
    }
}
