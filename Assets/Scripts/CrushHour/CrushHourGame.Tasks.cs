using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// Week 8: "find it on camera" tasks, control room only (0–2 per round, planned by
    /// <see cref="RoundGenerator.BuildCameraTasks"/>).
    /// <list type="bullet">
    /// <item>Lost child: a small person in a bright red shirt wanders in one zone. Find them on a camera, click them while
    /// focused, then radio a unit "PICK UP CHILD": it fetches the child and walks them to Medical/Info (or out of the
    /// nearest exit). Bonus when found within 90 s.</item>
    /// <item>Fight: 2–4 people stand and shove. Orange marker only on the focused camera zoomed in more than ×2. Click to
    /// spot it, then "BREAK UP FIGHT". Not handled in 60 s → panic rises around them.</item>
    /// </list>
    /// Nothing about these shows on the minimap: they have to be found on the cameras.
    /// </summary>
    public partial class CrushHourGame
    {
        enum CameraTaskKind { LostChild, Fight, MoshPit, Surfer }
        enum CameraTaskState { Waiting, Open, Found, Assigned, Escorting, Solved, Failed, Cancelled }

        class CameraTask
        {
            public CameraTaskKind kind;
            public CameraTaskState state;
            public int region;
            public float startAt, openedAt, foundAt;
            /// <summary>Lost child: the person; fight: the people shoving.</summary>
            public readonly List<int> people = new();
            public Vector2 center;
            /// <summary>Unit sent on it (-1 none), where it was last sent, and the place the child is taken to.</summary>
            public int unit = -1;
            public Vector2 goal, destination;
            public bool destinationIsExit;
            public float repath;
            public int score;
            public GameObject marker;
            /// <summary>Evening phase it opened in (for the grades); mosh pit radius and its spreading after the time limit.</summary>
            public int phaseOf;
            public float radius, growUntil, nextHurt;
            public bool growing;
            public bool Done => state is CameraTaskState.Solved or CameraTaskState.Failed or CameraTaskState.Cancelled;
        }

        const float ChildBonusTime = 90f, FightTimeLimit = 60f, FightResolveRange = 3f, ChildPickupRange = 2f,
                    TaskClickRange = 1.4f, FightPanicRadius = 12f, FightPanic = 0.45f;
        const int ChildScore = 300, ChildFastBonus = 200, FightScore = 200, MoshScore = 250, SurferScore = 250;
        const float MoshTimeLimit = 45f, MoshGrowTime = 12f, SurferCatchRange = 2.5f, SurferClickRange = 2.6f;
        static readonly Color MoshMarkerColor = new(1f, 0.3f, 0.6f), SurferMarkerColor = new(0.4f, 0.85f, 1f);

        static readonly Color ChildFoundColor = new(0.3f, 1f, 0.45f), FightMarkerColor = new(1f, 0.55f, 0.1f);

        readonly List<CameraTask> cameraTasks = new();
        float taskClock;

        int CameraTasksSolved
        {
            get { int n = 0; foreach (var t in cameraTasks) if (t.state == CameraTaskState.Solved) n++; return n; }
        }

        /// <summary>Tasks that count (opened and not called off by the evacuation).</summary>
        int CameraTasksCounted
        {
            get { int n = 0; foreach (var t in cameraTasks) if (t.state != CameraTaskState.Cancelled) n++; return n; }
        }

        /// <summary>Tasks still waiting to be dealt with (for the evening's HEAT).</summary>
        int OpenCameraTasks
        {
            get { int n = 0; foreach (var t in cameraTasks) if (t.state is CameraTaskState.Open or CameraTaskState.Found or CameraTaskState.Assigned) n++; return n; }
        }

        /// <summary>Evening moment (mosh pit, crowd surfer, extra lost child / fight in the encore): a task that opens now.</summary>
        void StartEveningCameraTask(CameraTaskKind kind, int region)
        {
            if (region < 0 || region >= venue.regions.Count) return;
            cameraTasks.Add(new CameraTask { kind = kind, region = region, startAt = taskClock });
        }

        /// <summary>The show stopped: mosh pits and surfers are called off (people have other worries now).</summary>
        void CancelEveningCameraTasks()
        {
            foreach (var t in cameraTasks)
            {
                if (t.Done || (t.kind != CameraTaskKind.MoshPit && t.kind != CameraTaskKind.Surfer && t.state != CameraTaskState.Waiting)) continue;
                CancelTask(t);
            }
        }

        void CancelTask(CameraTask t)
        {
            if (EveningOn && t.state is CameraTaskState.Open or CameraTaskState.Found or CameraTaskState.Assigned)
                phaseMoments[Mathf.Clamp(t.phaseOf, 0, RoundGenerator.PhaseCount - 1)]--;
            foreach (int p in t.people)
            {
                if (t.kind == CameraTaskKind.MoshPit) sim.moshRadius[p] = 0f;
                if (t.kind == CameraTaskKind.Surfer) { sim.surfer[p] = false; sim.anchor[p] = sim.position[p]; }
            }
            if (t.unit >= 0) ReturnToPost(units[t.unit]);
            t.unit = -1;
            if (t.marker != null) Destroy(t.marker);
            t.marker = null;
            t.state = CameraTaskState.Cancelled;
        }

        /// <summary>NewGame: forget the old round's tasks (their markers went with <c>world</c>).</summary>
        void ResetCameraTasks()
        {
            foreach (var t in cameraTasks)
                if (t.marker != null) Destroy(t.marker);
            cameraTasks.Clear();
            taskClock = 0f;
        }

        /// <summary>StartShow: plan this round's tasks (control room only).</summary>
        void PlanCameraTasks()
        {
            ResetCameraTasks();
            if (!InControlRoom || venue.regions.Count == 0) return;
            foreach (var plan in RoundGenerator.BuildCameraTasks(difficultySettings, venue.regions.Count, rng.Next()))
                cameraTasks.Add(new CameraTask
                {
                    kind = plan.lostChild ? CameraTaskKind.LostChild : CameraTaskKind.Fight,
                    region = plan.region,
                    startAt = plan.time,
                });
        }

        /// <summary>Every simulation step while Live (after the escorts).</summary>
        void UpdateCameraTasks(float dt)
        {
            if (cameraTasks.Count == 0 || phase != Phase.Live) return;
            taskClock += dt;
            foreach (var t in cameraTasks)
            {
                if (t.Done) continue;
                if (t.state == CameraTaskState.Waiting)
                {
                    if (taskClock >= t.startAt) OpenTask(t);
                    continue;
                }
                switch (t.kind)
                {
                    case CameraTaskKind.LostChild: UpdateChildTask(t, dt); break;
                    case CameraTaskKind.Fight: UpdateFightTask(t, dt); break;
                    case CameraTaskKind.MoshPit: UpdateMoshTask(t, dt); break;
                    default: UpdateSurferTask(t, dt); break;
                }
            }
        }

        // ---------------------------------------------------------------- Opening

        void OpenTask(CameraTask t)
        {
            var region = venue.regions[t.region];
            if (t.kind == CameraTaskKind.MoshPit || t.kind == CameraTaskKind.Surfer)
            {
                if (!(t.kind == CameraTaskKind.MoshPit ? OpenMosh(t) : OpenSurfer(t))) { t.state = CameraTaskState.Cancelled; return; }
            }
            else if (t.kind == CameraTaskKind.LostChild)
            {
                int child = PickCalmPerson(t.region, -1, 0f);
                if (child < 0) { t.state = CameraTaskState.Failed; return; }
                sim.child[child] = true;
                t.people.Add(child);
                t.center = sim.position[child];
                Announce(t, $"Parent at {region.Code}: lost child, red shirt.", "LOST CHILD", $"Parent at {region.Label}: red shirt. Find them on camera.");
            }
            else
            {
                int first = PickCalmPerson(t.region, -1, 0f);
                if (first < 0) { t.state = CameraTaskState.Failed; return; }
                int want = 2 + rng.Next(3); // 2–4
                t.people.Add(first);
                while (t.people.Count < want)
                {
                    int other = PickCalmPerson(t.region, first, 4f);
                    if (other < 0) break;
                    t.people.Add(other);
                }
                if (t.people.Count < 2) { t.state = CameraTaskState.Failed; return; }
                Vector2 sum = Vector2.zero;
                foreach (int p in t.people)
                {
                    sim.fighting[p] = true;
                    sum += sim.position[p];
                }
                t.center = sum / t.people.Count;
                t.marker = new GameObject("Fight marker");
                t.marker.transform.SetParent(world, false);
                t.marker.transform.localPosition = new Vector3(t.center.x, 0f, t.center.y);
                var ring = AddCircle(t.marker, 2.2f, FightMarkerColor);
                ring.widthMultiplier = 0.3f;
                t.marker.layer = ControlRoom.TaskMarkerLayer;
                Announce(t, $"{region.Code}: reports of a fight somewhere in the zone.", "FIGHT REPORTED", $"{region.Label}: find it on camera (zoom in) and send a unit.");
            }
            t.state = CameraTaskState.Open;
            t.openedAt = taskClock;
            t.phaseOf = EveningCameraTaskOpened();
        }

        void Announce(CameraTask t, string log, string title, string text)
        {
            Log(log, Severity.Warning);
            ShowBanner(title, text, Severity.Warning);
            controlRoom.SignalWarning(t.region, title);
            PlayRadioClick();
        }

        /// <summary>
        /// A calm, sober, unescorted person in <paramref name="region"/> not taken by another task; with
        /// <paramref name="near"/> ≥ 0, one within <paramref name="range"/> m of them.
        /// </summary>
        int PickCalmPerson(int region, int near, float range)
        {
            var candidates = new List<int>();
            for (int i = 0; i < sim.count; i++)
            {
                if (i == near || sim.state[i] != AgentState.Active || sim.drunk[i] || sim.child[i] || sim.fighting[i] || sim.escortedBy[i] >= 0 || sim.evacuating[i]
                    || sim.surfer[i] || sim.moshRadius[i] > 0f) continue;
                if (venue.regionOf[FestivalVenue.CellAt(sim.position[i])] != region) continue;
                if (near >= 0 && (sim.position[i] - sim.position[near]).sqrMagnitude > range * range) continue;
                candidates.Add(i);
            }
            return candidates.Count == 0 ? -1 : candidates[rng.Next(candidates.Count)];
        }

        // ---------------------------------------------------------------- Lost child

        void UpdateChildTask(CameraTask t, float dt)
        {
            int child = t.people[0];
            if (sim.state[child] != AgentState.Active)
            {
                Fail(t, sim.state[child] == AgentState.Safe ? "The lost child left the venue without a guardian." : "The lost child was hurt.");
                return;
            }
            if (t.state == CameraTaskState.Open && incidentStarted)
            {
                Fail(t, "The lost child was swept up in the evacuation before anyone found them.");
                return;
            }
            if (t.marker != null) t.marker.transform.localPosition = new Vector3(sim.position[child].x, 0f, sim.position[child].y);
            if (t.unit < 0) return;

            var u = units[t.unit];
            if (Redirected(t, u))
            {
                ReleaseChild(t);
                Log($"{u.Name} was sent elsewhere: the lost child is waiting again.", Severity.Warning);
                return;
            }
            if (t.state == CameraTaskState.Assigned)
            {
                if ((sim.position[child] - u.position).sqrMagnitude <= ChildPickupRange * ChildPickupRange)
                {
                    sim.SetEscort(child, t.unit);
                    if (!RouteTo(u, t, t.destination)) { ReleaseChild(t); Log($"{u.Name} can't find a way to take the child.", Severity.Warning); return; }
                    t.state = CameraTaskState.Escorting;
                    Log($"{RadioName(u)}: got the child, taking them to {(t.destinationIsExit ? "the exit" : DestinationName(t))}.", Severity.Good);
                    return;
                }
                t.repath -= dt;
                if (t.repath <= 0f || u.path.Count == 0)
                {
                    t.repath = 1f;
                    RouteTo(u, t, sim.position[child]);
                }
            }
            else if (t.state == CameraTaskState.Escorting)
            {
                sim.SetEscortTarget(child, u.position - u.heading * EscortFollowGap, UnitSpeed(u) * 1.1f); // small legs, but keeps up
                if (u.path.Count > 0) return;
                if ((sim.position[child] - u.position).sqrMagnitude > ChildPickupRange * ChildPickupRange * 4f) return; // wait for them
                sim.EscortOut(child, -1);
                sim.child[child] = false;
                Solve(t, $"{RadioName(u)}: child handed over at {DestinationName(t)}. Parent's on the way.", ChildScore + (t.foundAt - t.openedAt <= ChildBonusTime ? ChildFastBonus : 0));
                t.unit = -1;
                ReturnToPost(u);
            }
        }

        void ReleaseChild(CameraTask t)
        {
            int child = t.people[0];
            if (sim.escortedBy[child] >= 0) sim.SetEscort(child, -1);
            t.unit = -1;
            t.state = CameraTaskState.Found;
        }

        string DestinationName(CameraTask t)
        {
            if (t.destinationIsExit) return "the exit";
            int r = venue.regionOf[FestivalVenue.CellAt(t.destination)];
            return r >= 0 ? venue.regions[r].Label : "the info point";
        }

        /// <summary>Medical or Info zone post if the map has one, otherwise just inside the open exit nearest the child.</summary>
        void PickChildDestination(CameraTask t)
        {
            foreach (var region in venue.regions)
            {
                if (region.name.IndexOf("Medical", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    region.name.IndexOf("Info", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                t.destination = FestivalVenue.CellCenter(FestivalVenue.Index(region.post.x, region.post.y));
                t.destinationIsExit = false;
                return;
            }
            int cell = FestivalVenue.CellAt(sim.position[t.people[0]]), bestDoor = -1;
            float best = float.PositiveInfinity;
            for (int d = 0; d < venue.doors.Count; d++)
            {
                var door = venue.doors[d];
                if (!door.IsOpen || !door.field.Reachable(cell) || door.field.distance[cell] >= best) continue;
                best = door.field.distance[cell];
                bestDoor = d;
            }
            t.destinationIsExit = true;
            t.destination = bestDoor >= 0 ? venue.doors[bestDoor].Center : sim.position[t.people[0]];
            if (bestDoor < 0) return;
            foreach (int a in venue.doors[bestDoor].approach)
                if (venue.IsWalkable(a)) { t.destination = FestivalVenue.CellCenter(a); break; }
        }

        // ---------------------------------------------------------------- Fight

        void UpdateFightTask(CameraTask t, float dt)
        {
            t.people.RemoveAll(p => sim.state[p] != AgentState.Active);
            if (t.people.Count == 0) { EndFight(t); t.state = CameraTaskState.Failed; Log($"{venue.regions[t.region].Code}: the fight broke up by itself.", Severity.Info); return; }

            // Any unit getting there calms it down, sent or not.
            foreach (var u in units)
                if ((u.position - t.center).sqrMagnitude <= FightResolveRange * FightResolveRange)
                {
                    EndFight(t);
                    Solve(t, $"{RadioName(u)}: fight broken up at {venue.regions[t.region].Code}. All calm.", FightScore);
                    if (t.unit >= 0) ReturnToPost(units[t.unit]);
                    t.unit = -1;
                    return;
                }

            if (t.unit >= 0 && Redirected(t, units[t.unit])) t.unit = -1;
            if (t.unit < 0 && t.state == CameraTaskState.Assigned) t.state = CameraTaskState.Found;

            if (taskClock - t.openedAt >= FightTimeLimit)
            {
                // Nobody came: it spills over, people around get scared.
                for (int i = 0; i < sim.count; i++)
                    if (sim.state[i] == AgentState.Active && (sim.position[i] - t.center).sqrMagnitude <= FightPanicRadius * FightPanicRadius)
                        sim.panic[i] = Mathf.Max(sim.panic[i], FightPanic);
                EndFight(t);
                Fail(t, $"{venue.regions[t.region].Label}: the fight got out of hand. People nearby are panicking.");
            }
        }

        void EndFight(CameraTask t)
        {
            foreach (int p in t.people) sim.fighting[p] = false;
            if (t.marker != null) Destroy(t.marker);
            t.marker = null;
        }

        // ---------------------------------------------------------------- Shared

        /// <summary>The player gave the unit another job (escort, exit, move): the task lets go of it.</summary>
        bool Redirected(CameraTask t, Unit u)
        {
            if (u.escortPhase != EscortPhase.None && u.escortPhase != EscortPhase.Returning) return true;
            if (u.clearingDoor >= 0) return true;
            return u.path.Count > 0 && (u.path[u.path.Count - 1] - t.goal).sqrMagnitude > 4f;
        }

        bool RouteTo(Unit u, CameraTask t, Vector2 target)
        {
            var path = venue.FindPath(u.position, target);
            if (path == null) return false;
            if (u.escortPhase == EscortPhase.Returning) u.escortPhase = EscortPhase.None;
            u.path.Clear();
            u.path.AddRange(path);
            t.goal = path.Count > 0 ? path[path.Count - 1] : target;
            return true;
        }

        void Solve(CameraTask t, string reply, int points)
        {
            t.state = CameraTaskState.Solved;
            t.score = EveningCameraTaskSolved(t.phaseOf, points, taskClock - t.openedAt);
            if (t.marker != null) Destroy(t.marker);
            t.marker = null;
            Log($"{reply} (+{t.score})", Severity.Good);
            PlayRadioClick();
        }

        void Fail(CameraTask t, string text)
        {
            if (t.kind == CameraTaskKind.LostChild && t.people.Count > 0)
            {
                int child = t.people[0];
                if (sim.escortedBy[child] >= 0) sim.SetEscort(child, -1);
                sim.child[child] = false;
            }
            if (t.unit >= 0) ReturnToPost(units[t.unit]);
            t.unit = -1;
            if (t.marker != null) Destroy(t.marker);
            t.marker = null;
            t.state = CameraTaskState.Failed;
            EveningCameraTaskFailed();
            Log($"Control: {text}", Severity.Danger);
            PlayRadioClick();
        }

        // ---------------------------------------------------------------- Clicks and radio

        /// <summary>
        /// HandleMouse, control room, focused on a camera: a click on the lost child or the fight marks it found.
        /// True when the click was used (no move order then).
        /// </summary>
        bool CameraTaskClick(Vector2 ground)
        {
            if (!InControlRoom || !controlRoom.Focused) return false;
            var feed = controlRoom.FocusedFeed();
            foreach (var t in cameraTasks)
            {
                if (t.state != CameraTaskState.Open) continue;
                if (t.kind == CameraTaskKind.LostChild)
                {
                    int child = t.people[0];
                    if ((sim.position[child] - ground).sqrMagnitude > TaskClickRange * TaskClickRange) continue;
                    t.state = CameraTaskState.Found;
                    t.foundAt = taskClock;
                    t.marker = new GameObject("Lost child (found)");
                    t.marker.transform.SetParent(world, false);
                    t.marker.transform.localPosition = new Vector3(sim.position[child].x, 0f, sim.position[child].y);
                    AddCircle(t.marker, 0.8f, ChildFoundColor).widthMultiplier = 0.18f;
                    PickChildDestination(t);
                    bool fast = t.foundAt - t.openedAt <= ChildBonusTime;
                    Log($"Lost child found in {venue.regions[venue.regionOf[FestivalVenue.CellAt(sim.position[child])]].Label}{(fast ? " (in time for the bonus)" : "")}. Radio a unit: PICK UP CHILD.", Severity.Good);
                    PlayRadioClick();
                    return true;
                }
                bool zoomed = feed != null && feed.Zoom > controlRoom.taskMarkerZoom;
                if (t.kind == CameraTaskKind.Surfer)
                {
                    // Up over the heads: the click lands on the ground a little behind them, so the range is wider.
                    if ((sim.position[t.people[0]] - ground).sqrMagnitude > SurferClickRange * SurferClickRange) continue;
                    t.state = CameraTaskState.Found;
                    t.foundAt = taskClock;
                    if (t.marker != null) t.marker.layer = 0;
                    Log($"Crowd surfer spotted in {venue.regions[t.region].Label}, heading for the front. Radio a unit: CATCH SURFER.", Severity.Warning);
                    PlayRadioClick();
                    return true;
                }
                // The fight / mosh pit: only once its marker can be seen (zoomed in), or by clicking right on the people.
                float reach = t.kind == CameraTaskKind.MoshPit ? t.radius + (zoomed ? 1.5f : 0f) : zoomed ? 2.5f : TaskClickRange;
                if ((t.center - ground).sqrMagnitude > reach * reach) continue;
                t.state = CameraTaskState.Found;
                t.foundAt = taskClock;
                if (t.marker != null) t.marker.layer = 0; // spotted: everyone can see it now
                if (t.kind == CameraTaskKind.MoshPit)
                    Log($"Mosh pit spotted in {venue.regions[t.region].Label}. Radio a unit: BREAK UP MOSH PIT ({Mathf.CeilToInt(Mathf.Max(0f, MoshTimeLimit - (taskClock - t.openedAt)))} s left).", Severity.Warning);
                else
                    Log($"Fight spotted in {venue.regions[t.region].Label}. Radio a unit: BREAK UP FIGHT ({Mathf.CeilToInt(FightTimeLimit - (taskClock - t.openedAt))} s left).", Severity.Warning);
                PlayRadioClick();
                return true;
            }
            return false;
        }

        /// <summary>Radio command wheel: PICK UP CHILD / BREAK UP FIGHT for any unit, once the task was found on camera.</summary>
        void AddTaskCommands(int unitIndex)
        {
            var u = units[unitIndex];
            foreach (var task in cameraTasks)
            {
                if (task.state != CameraTaskState.Found && task.state != CameraTaskState.Assigned) continue;
                var t = task;
                bool mine = t.unit == unitIndex;
                radioItems.Add(new RadioItem
                {
                    label = t.kind switch
                    {
                        CameraTaskKind.LostChild => "PICK UP CHILD",
                        CameraTaskKind.MoshPit => "BREAK UP MOSH PIT",
                        CameraTaskKind.Surfer => "CATCH SURFER",
                        _ => "BREAK UP FIGHT",
                    },
                    sub = venue.regions[t.region].Code + (mine ? " (on it)" : t.unit >= 0 ? $" ({RadioName(units[t.unit])} on it)" : ""),
                    color = t.kind switch
                    {
                        CameraTaskKind.LostChild => ChildFoundColor,
                        CameraTaskKind.MoshPit => MoshMarkerColor,
                        CameraTaskKind.Surfer => SurferMarkerColor,
                        _ => FightMarkerColor,
                    },
                    enabled = !mine,
                    reason = mine ? "already on it" : null,
                    action = () =>
                    {
                        AssignTask(t, unitIndex);
                        CloseRadio();
                    },
                });
            }
        }

        void AssignTask(CameraTask t, int unitIndex)
        {
            var u = units[unitIndex];
            if (t.unit >= 0 && t.unit != unitIndex)
            {
                if (t.kind == CameraTaskKind.LostChild && sim.escortedBy[t.people[0]] >= 0) sim.SetEscort(t.people[0], -1);
                ReturnToPost(units[t.unit]);
            }
            foreach (var other in cameraTasks) // one task per unit
                if (other != t && other.unit == unitIndex && !other.Done)
                {
                    if (other.kind == CameraTaskKind.LostChild) ReleaseChild(other);
                    else { other.unit = -1; other.state = CameraTaskState.Found; }
                }
            if (u.escortPhase != EscortPhase.None)
            {
                CancelEscort(u);
                u.escortPhase = EscortPhase.None;
            }
            u.clearingDoor = -1;
            var target = t.kind switch
            {
                CameraTaskKind.LostChild => sim.position[t.people[0]],
                CameraTaskKind.Surfer => t.destination,   // wait for them at the front, where they'll come down
                _ => t.center,
            };
            if (!RouteTo(u, t, target))
            {
                Log($"{u.Name} can't get there.", Severity.Warning);
                return;
            }
            t.unit = unitIndex;
            t.repath = 1f;
            t.state = CameraTaskState.Assigned;
            RadioReply(u, t.kind switch
            {
                CameraTaskKind.LostChild => "going to get the child.",
                CameraTaskKind.Surfer => "heading to the front to catch the surfer.",
                CameraTaskKind.MoshPit => "on my way to calm the pit down.",
                _ => "on my way to break it up.",
            });
        }

        /// <summary>EndGame: points for the tasks solved (already × the difficulty multiplier).</summary>
        int CameraTaskScore
        {
            get { int n = 0; foreach (var t in cameraTasks) if (t.state == CameraTaskState.Solved) n += t.score; return n; }
        }

        /// <summary>Debrief row text ("1 / 2 solved (+500)"), or null when the round had none.</summary>
        string CameraTaskSummary => CameraTasksCounted == 0 ? null : $"{CameraTasksSolved} / {CameraTasksCounted} solved" + (CameraTaskScore > 0 ? $"  (+{CameraTaskScore})" : "");

        // ---------------------------------------------------------------- Mosh pit (evening, HEADLINER)

        /// <summary>A ring of 6–8 m near the stage: everyone in it runs round. Marker only on the focused camera zoomed in.</summary>
        bool OpenMosh(CameraTask t)
        {
            var stage = EveningSpots.StageRect(venue);
            var spots = new List<int>();
            for (int i = 0; i < sim.count; i++)
            {
                if (sim.state[i] != AgentState.Active || sim.drunk[i] || sim.evacuating[i] || sim.child[i] || sim.surfer[i] || sim.moshRadius[i] > 0f) continue;
                float d = (EveningSpots.NearestStagePoint(stage, sim.position[i]) - sim.position[i]).magnitude;
                if (d >= 5f && d <= 13f) spots.Add(i);
            }
            if (spots.Count == 0) return false;
            t.center = sim.position[spots[rng.Next(spots.Count)]];
            t.radius = 3f + (float)rng.NextDouble();
            RecruitMosh(t, 18);
            if (t.people.Count < 6) { foreach (int p in t.people) sim.moshRadius[p] = 0f; t.people.Clear(); return false; }
            t.marker = new GameObject("Mosh pit marker");
            t.marker.transform.SetParent(world, false);
            t.marker.transform.localPosition = new Vector3(t.center.x, 0f, t.center.y);
            AddCircle(t.marker, t.radius, MoshMarkerColor).widthMultiplier = 0.3f;
            t.marker.layer = ControlRoom.TaskMarkerLayer;
            var region = venue.regions[t.region];
            Announce(t, $"{region.Code}: a mosh pit is opening up somewhere near the stage.", "MOSH PIT", $"{region.Label}: find it on camera (zoom in), click it and send a unit within {MoshTimeLimit:0} s.");
            return true;
        }

        void RecruitMosh(CameraTask t, int max)
        {
            for (int i = 0; i < sim.count && t.people.Count < max; i++)
            {
                if (sim.state[i] != AgentState.Active || sim.drunk[i] || sim.evacuating[i] || sim.child[i] || sim.surfer[i] || sim.fighting[i] || sim.escortedBy[i] >= 0) continue;
                if (sim.moshRadius[i] > 0f && !t.people.Contains(i)) continue;
                if ((sim.position[i] - t.center).sqrMagnitude > t.radius * t.radius) continue;
                if (!t.people.Contains(i)) t.people.Add(i);
            }
            foreach (int p in t.people)
            {
                sim.moshCenter[p] = t.center;
                sim.moshRadius[p] = t.radius;
                sim.dancing[p] = false;
            }
        }

        void UpdateMoshTask(CameraTask t, float dt)
        {
            t.people.RemoveAll(p => sim.state[p] != AgentState.Active);
            foreach (var u in units)
                if ((u.position - t.center).sqrMagnitude <= (t.radius + 1.5f) * (t.radius + 1.5f))
                {
                    EndMosh(t);
                    if (t.growing) Log($"{RadioName(u)}: pit's broken up, but people got hurt.", Severity.Warning);
                    else Solve(t, $"{RadioName(u)}: mosh pit broken up at {venue.regions[t.region].Code}, nobody hurt.", MoshScore);
                    if (t.growing) t.state = CameraTaskState.Failed;
                    if (t.unit >= 0) ReturnToPost(units[t.unit]);
                    t.unit = -1;
                    return;
                }
            if (t.unit >= 0 && Redirected(t, units[t.unit])) { t.unit = -1; if (t.state == CameraTaskState.Assigned) t.state = CameraTaskState.Found; }

            if (!t.growing && taskClock - t.openedAt >= MoshTimeLimit)
            {
                // Nobody came: it spreads and people start going down.
                t.growing = true;
                t.radius *= 1.5f;
                t.growUntil = taskClock + MoshGrowTime;
                t.nextHurt = taskClock;
                RecruitMosh(t, 32);
                if (t.marker != null) Destroy(t.marker);
                t.marker = new GameObject("Mosh pit marker");
                t.marker.transform.SetParent(world, false);
                t.marker.transform.localPosition = new Vector3(t.center.x, 0f, t.center.y);
                AddCircle(t.marker, t.radius, MoshMarkerColor).widthMultiplier = 0.35f;
                Log($"{venue.regions[t.region].Label}: the mosh pit is getting out of hand, people are going down!", Severity.Danger);
                EveningCameraTaskFailed();
                PlayRadioClick();
            }
            if (!t.growing) return;
            if (taskClock >= t.nextHurt && t.people.Count > 0)
            {
                t.nextHurt = taskClock + 4f;
                sim.InjureByCrush(t.people[rng.Next(t.people.Count)]);
            }
            if (taskClock >= t.growUntil)
            {
                EndMosh(t);
                t.state = CameraTaskState.Failed;
                if (t.unit >= 0) ReturnToPost(units[t.unit]);
                t.unit = -1;
                Log($"Control: the mosh pit at {venue.regions[t.region].Code} burnt itself out, with people hurt.", Severity.Danger);
            }
        }

        void EndMosh(CameraTask t)
        {
            foreach (int p in t.people) { sim.moshRadius[p] = 0f; if (sim.state[p] == AgentState.Active) sim.orderlyTime[p] = 6f; }
            if (t.marker != null) Destroy(t.marker);
            t.marker = null;
        }

        // ---------------------------------------------------------------- Crowd surfer (evening, HEADLINER)

        /// <summary>Someone 12–20 m back is lifted up and passed towards the front, where they'll drop if nobody's there.</summary>
        bool OpenSurfer(CameraTask t)
        {
            var stage = EveningSpots.StageRect(venue);
            var spots = new List<int>();
            for (int i = 0; i < sim.count; i++)
            {
                if (sim.state[i] != AgentState.Active || sim.drunk[i] || sim.evacuating[i] || sim.child[i] || sim.fighting[i] || sim.moshRadius[i] > 0f || sim.escortedBy[i] >= 0) continue;
                float d = (EveningSpots.NearestStagePoint(stage, sim.position[i]) - sim.position[i]).magnitude;
                if (d >= 12f && d <= 20f) spots.Add(i);
            }
            if (spots.Count == 0) return false;
            int surfer = spots[rng.Next(spots.Count)];
            var p = sim.position[surfer];
            var front = EveningSpots.NearestStagePoint(stage, p);
            var dir = (p - front).normalized;
            // The first walkable spot 1.5 m or more in front of the stage.
            t.destination = front + dir * 1.5f;
            for (float k = 1.5f; k < 6f && venue.cells[FestivalVenue.CellAt(t.destination)] != CellType.Open; k += 0.5f) t.destination = front + dir * k;
            sim.surfer[surfer] = true;
            sim.dancing[surfer] = false;
            sim.anchor[surfer] = t.destination;
            t.people.Add(surfer);
            t.center = p;
            t.marker = new GameObject("Surfer marker");
            t.marker.transform.SetParent(world, false);
            t.marker.transform.localPosition = new Vector3(p.x, 0f, p.y);
            AddCircle(t.marker, 1.2f, SurferMarkerColor).widthMultiplier = 0.2f;
            t.marker.layer = ControlRoom.TaskMarkerLayer;
            var region = venue.regions[t.region];
            Announce(t, $"{region.Code}: crowd surfer on the way to the front!", "CROWD SURFER", $"{region.Label}: someone is being passed over the heads. Find them on camera, click, then CATCH SURFER before they reach the barrier.");
            return true;
        }

        void UpdateSurferTask(CameraTask t, float dt)
        {
            int s = t.people[0];
            if (sim.state[s] != AgentState.Active) { t.state = CameraTaskState.Failed; if (t.marker != null) Destroy(t.marker); t.marker = null; return; }
            var p = sim.position[s];
            if (t.marker != null) t.marker.transform.localPosition = new Vector3(p.x, 0f, p.y);
            foreach (var u in units)
                if ((u.position - p).sqrMagnitude <= SurferCatchRange * SurferCatchRange)
                {
                    sim.surfer[s] = false;
                    sim.anchor[s] = p;
                    Solve(t, $"{RadioName(u)}: caught the surfer and set them down safely.", SurferScore);
                    if (t.unit >= 0) ReturnToPost(units[t.unit]);
                    t.unit = -1;
                    return;
                }
            if (t.unit >= 0 && Redirected(t, units[t.unit])) { t.unit = -1; if (t.state == CameraTaskState.Assigned) t.state = CameraTaskState.Found; }
            // Reached the front (or carried about too long): dropped.
            if ((p - t.destination).sqrMagnitude <= 1f || taskClock - t.openedAt > 75f)
            {
                sim.surfer[s] = false;
                sim.InjureByCrush(s);
                Fail(t, $"The crowd surfer at {venue.regions[t.region].Code} was dropped at the barrier and is hurt.");
            }
        }
    }
}
