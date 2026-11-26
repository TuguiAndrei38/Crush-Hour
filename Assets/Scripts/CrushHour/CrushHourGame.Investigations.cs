using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    public partial class CrushHourGame
    {
        sealed class FieldReport
        {
            public Incident incident;
            public Vector2 target, destination;
            public int region, officer = -1;
            public string message, detail;
            public bool resolved, observed;
            public float age, inspection, resolvedAge;
        }

        readonly List<FieldReport> fieldReports = new();
        readonly HashSet<int> earlyOrderRegions = new();
        float gasReportScan, warningPuffTimer;
        const float InspectionSeconds = 1.5f, GasBackupSeconds = 14f;

        FieldReport OpenReport(Incident incident, Vector2Int cell, string message)
        {
            int site = FestivalVenue.Index(cell.x, cell.y);
            int region = incident.region >= 0 ? incident.region : venue.regionOf[site];
            var report = new FieldReport
            {
                incident = incident, region = region, target = FestivalVenue.CellCenter(site),
                message = message, detail = "Awaiting investigation."
            };
            fieldReports.Add(report);
            string zone = region >= 0 ? venue.regions[region].name : incident.SiteName;
            string line = $"{zone} to Control: {message}";
            Log(line, Severity.Warning);
            if (InControlRoom)
            {
                controlRoom.SignalWarning(region, message);
                PlayRadioClick();
            }
            return report;
        }

        bool CurrentReport(FieldReport report) => phase == Phase.Live && !paused &&
            report != null && !report.resolved && fieldReports.Contains(report);

        void ObserveReport(FieldReport report)
        {
            if (!CurrentReport(report) || !TryViewReport(report.region, report.target)) return;
            if (report.observed) return;
            report.observed = true;
            if (report.incident is GasLeakIncident leak)
            {
                int coughing = LocalCoughs(leak);
                string observation = coughing > 0 ? $"{coughing} coughing near {leak.SiteName}." : "Source unclear on camera.";
                report.detail = observation + (report.officer >= 0 ? $" {units[report.officer].Name} en route." : " Send an officer to check.");
                Log($"Visual check at {leak.SiteName}: {report.detail}");
            }
            else ConfirmReport(report, "Visual check");
        }

        bool AvailableForReport(int index)
        {
            var unit = units[index];
            if (unit.escortPhase != EscortPhase.None || unit.clearingDoor >= 0 || unit.path.Count != 0) return false;
            foreach (var report in fieldReports)
                if (report.officer == index) return false;
            return true;
        }

        bool CanDispatchReport(FieldReport report)
        {
            if (!CurrentReport(report) || report.officer >= 0) return false;
            for (int i = 0; i < units.Count; i++)
                if (AvailableForReport(i)) return true;
            return false;
        }

        void DispatchReport(FieldReport report)
        {
            if (!CanDispatchReport(report)) return;
            List<Vector2> bestPath = null;
            int best = -1;
            float shortest = float.PositiveInfinity;
            for (int i = 0; i < units.Count; i++)
            {
                if (!AvailableForReport(i)) continue;
                var from = units[i].position;
                var path = venue.FindInspectionPath(from, report.target);
                if (path == null) continue;
                float distance = 0f;
                foreach (Vector2 step in path) { distance += Vector2.Distance(from, step); from = step; }
                if (distance >= shortest) continue;
                shortest = distance;
                bestPath = path;
                best = i;
            }
            if (best < 0)
            {
                report.detail = "No available officer can reach this site.";
                return;
            }
            units[best].path.AddRange(bestPath);
            report.officer = best;
            report.destination = bestPath[^1];
            report.inspection = 0f;
            report.detail = $"{units[best].Name} en route.";
            Log($"{units[best].Name} to Control: checking the report near {report.incident.SiteName}.");
            if (InControlRoom) PlayRadioClick();
        }

        void ResolveReport(FieldReport report, string detail)
        {
            // A player can reassign the officer while they're walking. Only cancel our own route.
            if (report.officer >= 0)
            {
                var u = units[report.officer];
                if (u.escortPhase == EscortPhase.None && u.path.Count > 0 && u.path[^1] == report.destination) u.path.Clear();
            }
            report.officer = -1;
            report.resolved = true;
            report.detail = detail;
        }

        void ConfirmReport(FieldReport report, string source)
        {
            if (report.incident is GasLeakIncident leak)
            {
                ResolveReport(report, $"{source} confirmed gas at {leak.SiteName}.");
                DetectGas(leak, source);
                return;
            }
            string finding = report.incident is TowerCollapseIncident tower
                ? $"The tower is leaning {FallDirection(tower.direction)}." : $"Smoke is rising at {report.incident.SiteName}.";
            if (report.region >= 0)
            {
                earlyOrderRegions.Add(report.region);
                finding += $" The {venue.regions[report.region].name} zone can evacuate now.";
            }
            ResolveReport(report, finding);
            Log($"{source}: {finding}", Severity.Warning);
            if (InControlRoom) PlayRadioClick();
        }

        static string FallDirection(Vector2Int direction) => direction.x != 0 ?
            (direction.x > 0 ? "east" : "west") : (direction.y > 0 ? "north" : "south");

        void UpdateInvestigations(float dt)
        {
            for (int i = fieldReports.Count - 1; i >= 0; i--)
            {
                var report = fieldReports[i];
                if (report.resolved)
                {
                    report.resolvedAge += dt;
                    if (report.resolvedAge >= 6f) fieldReports.RemoveAt(i);
                    continue;
                }
                report.age += dt;
                var leak = report.incident as GasLeakIncident;
                if (leak != null && leak.Ignited)
                {
                    ResolveReport(report, "The gas cloud has ignited.");
                    continue;
                }
                if (leak == null && report.incident.Started)
                {
                    ResolveReport(report, $"Emergency confirmed at {report.incident.SiteName}.");
                    continue;
                }
                if (report.officer >= 0)
                {
                    var u = units[report.officer];
                    if (u.escortPhase != EscortPhase.None || (u.path.Count > 0 && u.path[^1] != report.destination))
                    {
                        report.officer = -1;
                        report.detail = "Officer reassigned. Another officer can investigate.";
                    }
                    else if (u.path.Count == 0 && (u.position - report.destination).sqrMagnitude < 0.25f)
                    {
                        if (report.inspection == 0f) report.detail = $"{u.Name} inspecting the site.";
                        report.inspection += dt;
                        if (report.inspection >= InspectionSeconds) { ConfirmReport(report, u.Name); continue; }
                    }
                    else if (u.path.Count == 0 || !venue.IsWalkable(FestivalVenue.CellAt(u.path[0])))
                    {
                        u.path.Clear();
                        report.officer = -1;
                        report.detail = "Route interrupted. Dispatch again when the site is accessible.";
                    }
                }
                if (leak != null && report.age >= GasBackupSeconds) ConfirmReport(report, "Venue safety team");
            }
        }

        GasLeakIncident ClosestGasSource(Vector2 point)
        {
            GasLeakIncident closest = null;
            float nearest = float.PositiveInfinity;
            foreach (var incident in schedule.incidents)
            {
                if (incident is not GasLeakIncident leak || !leak.Started || leak.Ignited) continue;
                float distance = ((Vector2)leak.cell + Vector2.one * 0.5f - point).sqrMagnitude;
                if (distance >= nearest) continue;
                nearest = distance;
                closest = leak;
            }
            return closest;
        }

        bool KnownGasAt(int cell) => gasVisible && venue.gas[cell] > GasLeakIncident.DetectLevel &&
            ClosestGasSource(FestivalVenue.CellCenter(cell)) is { Detected: true };

        int LocalCoughs(GasLeakIncident leak)
        {
            if (sim.CoughingCount == 0) return 0;
            int locals = 0;
            Vector2 site = (Vector2)leak.cell + Vector2.one * 0.5f;
            for (int i = 0; i < sim.count; i++)
                if (sim.state[i] == AgentState.Active && sim.gasDose[i] >= crowd.coughDose &&
                    (sim.position[i] - site).sqrMagnitude <= 20f * 20f && ClosestGasSource(sim.position[i]) == leak) locals++;
            return locals;
        }

        void CheckGas(float dt)
        {
            if (!venue.HasGas) return;
            gasReportScan -= dt;
            if (gasReportScan > 0f) return;
            gasReportScan = 0.5f;
            foreach (var incident in schedule.incidents)
            {
                if (incident is not GasLeakIncident leak || !leak.Started || leak.Ignited || leak.Detected ||
                    warnedIncidents.Contains(leak)) continue;
                string message = null;
                if (LocalCoughs(leak) > 0) message = $"People coughing near {leak.SiteName}. Source unclear.";
                else
                {
                    Vector2 site = (Vector2)leak.cell + Vector2.one * 0.5f;
                    foreach (var u in units)
                    {
                        float radius = RoleRadius(u.role);
                        if ((u.position - site).magnitude > radius + 5f || ClosestGasSource(u.position) != leak ||
                            !GasLeakIncident.Smells(venue, u.position, radius)) continue;
                        message = $"{u.Name} reports a strong smell near {leak.SiteName}.";
                        break;
                    }
                    // A quiet or isolated site still gets its scheduled equipment check.
                    if (message == null && leak.Age >= 8f) message = $"Pressure dropping at {leak.SiteName}. Request a site check.";
                }
                if (message == null) continue;
                warnedIncidents.Add(leak);
                OpenReport(leak, leak.cell, message);
            }
        }
    }
}
