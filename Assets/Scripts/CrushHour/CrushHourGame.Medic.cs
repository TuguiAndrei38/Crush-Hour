using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// Medics (<see cref="CrushHourGame.Role.Medic"/>): once an incident starts, each free medic walks to the
    /// nearest badly hurt person nobody else is already going to and stabilises them in place, turning an injury
    /// that would otherwise sit at -100 on the final score into a save. Fully automatic - unlike patrols and police,
    /// the player doesn't give medics orders, so this needed no new tool, button or panel: they're wired in next to
    /// <see cref="CrushHourGame.UpdateMegaphones"/>, which already runs the same way.
    /// </summary>
    public partial class CrushHourGame
    {
        /// <summary>Seconds a medic spends treating someone once they reach them.</summary>
        const float MedicTreatSeconds = 4f;

        partial class Unit
        {
            /// <summary>Medics: the injured person (index into sim.position/sim.state) being walked to or treated, -1 none.</summary>
            public int treating = -1;
            /// <summary>Medics: counts down while standing over <see cref="treating"/>; reaching 0 stabilises them.</summary>
            public float treatTimer;
        }

        /// <summary>Every simulation step once the incident has started: walk to someone hurt, treat them, repeat.</summary>
        void UpdateMedics(float dt)
        {
            foreach (var u in units)
            {
                if (u.role != Role.Medic) continue;

                if (u.treating < 0)
                {
                    if (u.path.Count > 0) continue; // still walking back from the last save
                    int person = NearestUntreatedInjured(u.position);
                    if (person < 0) continue;
                    var path = venue.FindPath(u.position, sim.position[person]);
                    if (path == null) continue; // no way to reach them right now (e.g. behind a closed gate)
                    u.path.Clear();
                    u.path.AddRange(path);
                    u.treating = person;
                    u.treatTimer = MedicTreatSeconds;
                    Log($"{u.Name} is going to help someone hurt near {venue.DescribeLocation(FestivalVenue.CellAt(sim.position[person]))}.");
                    continue;
                }

                // Someone else already saved them, or they're not injured any more for some other reason: drop it.
                if (sim.state[u.treating] != AgentState.Injured) { u.treating = -1; continue; }
                if (u.path.Count > 0) continue; // still walking over

                u.treatTimer -= dt;
                if (u.treatTimer > 0f) continue;
                int saved = u.treating;
                sim.Stabilize(saved);
                Log($"{u.Name} stabilised someone near {venue.DescribeLocation(FestivalVenue.CellAt(sim.position[saved]))}.", Severity.Good);
                u.treating = -1;
            }
        }

        /// <summary>The nearest injured person no medic is already walking to or treating, or -1 if there isn't one.</summary>
        int NearestUntreatedInjured(Vector2 from)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < sim.count; i++)
            {
                if (sim.state[i] != AgentState.Injured) continue;
                bool claimed = false;
                foreach (var u in units)
                    if (u.role == Role.Medic && u.treating == i) { claimed = true; break; }
                if (claimed) continue;
                float sq = (sim.position[i] - from).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            return best;
        }
    }
}
