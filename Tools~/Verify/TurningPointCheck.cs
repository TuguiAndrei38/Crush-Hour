using System;
using System.Linq;
using CrushHour;
using UnityEngine;

static class TurningPointCheck
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Turning point: " + message);
    }

    public static void Run()
    {
        foreach (VenueMap map in Enum.GetValues(typeof(VenueMap)))
        {
            var venue = new FestivalVenue(map);
            var sim = new CrowdSimulation(new CrowdSettings { agentCount = 100 }, venue, new System.Random(17));
            var clip = new TurningPointReplay(venue);
            for (int t = 0; t < 8; t++) clip.Record(t, sim);

            int door = venue.doors.FindIndex(d => d.gate == null);
            int alternate = venue.doors.FindIndex(d => d.gate == null && d != venue.doors[door]);
            var approach = FestivalVenue.CellCenter(venue.DoorFrontCell(venue.doors[door]));
            for (int i = 0; i < 18; i++)
            {
                sim.position[i] = approach;
                sim.targetDoor[i] = door;
                sim.evacuating[i] = true;
            }
            clip.Record(8f, sim);
            // Close the alternate after the selected moment and run long past the recording window.
            venue.SetDoorBlocked(venue.doors[alternate], true, "Test");
            sim.exitsByDoor[door] = 12;
            for (int t = 9; t <= 300; t++) clip.Record(t, sim);
            clip.Finish(300f, sim);
            Require(clip.MomentTime == 8f && clip.FocusDoor == door && clip.AlternateDoor == alternate, "wrong crowded exit or alternate");
            Require(clip.FrameCount == 11 && clip.GetFrame(0).Time == 3f && clip.GetFrame(10).Time == 13f, "the lead-in or follow-through was overwritten");
            var moment = clip.GetFrame(5);
            Require(moment.Approaching[door] == 18 && moment.Exited[door] == 0 && moment.Density.Sum(x => (int)x) == sim.count, "wrong recorded crowd or departures");
            Require((moment.OpenMask & (1 << alternate)) != 0 && (clip.GetFrame(6).OpenMask & (1 << alternate)) == 0, "door state wasn't recorded per frame");
            Require(clip.GetFrame(6).Exited[door] == 12, "later safe exits missing");

            var safeRound = new TurningPointReplay(venue);
            safeRound.Record(0f, sim);
            sim.exitsByDoor[door] += 9;
            safeRound.Record(0.25f, sim); // Normal capture waits a full second.
            Require(safeRound.FrameCount == 1, "capture ran faster than the one-second cadence");
            safeRound.Finish(0.4f, sim);
            safeRound.Finish(1f, sim);
            safeRound.Record(2f, sim);
            Require(safeRound.FrameCount == 2 && safeRound.MomentTime == 0.4f && safeRound.Caption.Contains("9 people left"), "short successful round or repeated finish");

            var incident = new TurningPointReplay(venue);
            incident.Record(0f, sim);
            var injuryCell = venue.regions[0].post;
            for (int i = 0; i < 4; i++)
            {
                sim.position[i] = injuryCell + new Vector2(0.5f, 0.5f);
                sim.state[i] = AgentState.Injured;
            }
            sim.exitsByDoor[door] += 60;
            incident.Record(1f, sim);
            for (int i = 0; i < 5; i++)
            {
                sim.position[i] = venue.regions[1].post + new Vector2(0.5f, 0.5f);
                sim.state[i] = AgentState.Injured;
            }
            incident.Finish(2f, sim);
            Require(incident.FocusDoor == -1 && incident.Title.Contains(venue.regions[0].name) && incident.Caption.Contains("4 people"), "local injuries should outrank exits");
            Require(incident.MomentTime == 1f && incident.GetFrame(1).Injured == 4 && incident.GetFrame(1).Hurt.Sum(x => (int)x) == 4, "wrong injury map or relocated injuries counted twice");

            var immediate = new TurningPointReplay(venue);
            immediate.Finish(0f, sim);
            Require(immediate.FrameCount == 1 && !string.IsNullOrEmpty(immediate.Caption), "missing immediate-round fallback");
        }
        Console.WriteLine("PASS turning-point replay on all venues: crowd, queue, injuries, safe exits, short rounds and retained history.");
    }
}
