# AGENTS.md

Notes for coding agents working in this Unity project.

## Project

- Unity **6000.6.6f1**, **URP 17** (PC renderer: Forward+, depth priming off), **Input System only** (`activeInputHandler: 1`, so the legacy `Input` class throws). Read devices directly (`Keyboard.current`, `Mouse.current`, `Gamepad.current`).
- Git repo (GitHub, `.gitignore` excludes `Library/`, `Temp/`, `Logs/`, `UserSettings/`, `*.csproj`, `*.sln`); no asmdefs: runtime scripts compile into `Assembly-CSharp`, `Editor/` folders into `Assembly-CSharp-Editor`.
- **Crush Hour** concept: you are the safety commander at a big event. When disaster hits, you guide the crowd out with barriers, emergency gates, a patrolman in every zone and megaphone police. There is no PA: the user had the announcement buttons removed. Panic spreads, doorways jam and dense pockets crush. You're scored on people out safely, injuries, panic attacks and time.
- Third-party asset: **Low Poly People** by David Jalbert, in `Assets/DavidJalbert/LowPolyPeople/`.
  - 18 prefabs with Generic rigs. Animations live in `Prefabs/Animations/`: "normal/stout/strong" × "idle/walk/run/wave".
  - Materials are Built-in Standard palette textures.
  - Its `Example/Scripts/AnimationController.cs` uses the legacy `Input` class, so the example scene throws errors in this project. Our scenes don't use it.

## Scenes

| Scene | What it is |
|---|---|
| `Assets/Scenes/CrushHourFestival.unity` | **Main prototype.** Top-down crowd-safety sim at a music festival. First in Build Settings. |
| `Assets/Scenes/CrushHourFestival_Dev.unity` | Working copy of the festival scene for week 5 development, with a small crowd (`crowdSizeOverride: 400`, which wins over the difficulty's player count; set it to 0 there to see the real counts). First scene in Build Settings (the start menu loads it). |
| `Assets/Scenes/CrushHourControlRoom.unity` | **Week 6: first-person control room.** A copy of `CrushHourFestival.unity` plus a `Control Room` object (`ControlRoom`), made by **Tools > Crush Hour > Create Control Room Scene**. The start menu's PLAY loads it for every map. Second in Build Settings (after the scene the game starts in). |
| `Assets/Scenes/SampleScene.unity` | Earlier experiment: a voxel cube terrain plus a first-person controller. |

The scenes are set up by editor scripts (see below), not by hand.

## Code map

### Crush Hour (`Assets/Scripts/CrushHour/`, namespace `CrushHour`)

- `FestivalVenue.cs`: the grid model, at 1 cell = 1 m² (104×76).
  - Layout: fence, outside safe zone, landmarks, fire sites and crowd spawn zones.
  - `Door`s: 2 narrow entrances that are always open, and 6 emergency gates (`Gate`) that start closed.
  - Each door has its own Dijkstra `field` that stays inside the fence:
    - An open door's field leads through it to its `beyond` cells in the safe zone.
    - A closed door's field leads only to its `approach` cells in front of it, which is where people discover it's shut.
    - Cells near the fire cost more.
  - **Blocked doors:** `Door.blocked` and `blockReason` ("Delivery truck", "Collapsed scaffolding"). A blocked door is not open (`IsOpen` = `!blocked && (gate == null || gate.Passable)`) and its cells aren't walkable (`blockedDoorCell[]`, checked first in `IsWalkable`), so its field only leads to its `approach` cells and people find it shut there like a closed gate (`doorsFoundShut`) and pick another. `SetDoorBlocked(door, on, reason)` updates routes, `seesOpenGate` and flee directions. `ClearDoor(door, workers, dt)` adds `workers × dt / DoorClearTime` (8 s) to `clearProgress` and unblocks at 1. `CanReachToClear` (within `DoorClearReach`, 2 m, of the door rectangle), `DoorFrontCell` (where staff stand), `DoorNear(cell)`. `seesOpenGate` now also covers entrances that have been blocked and are open again (`everBlocked`), so people who found them shut notice; never-blocked entrances are left out, as before, which keeps the baseline unchanged.
  - **Gas** (`gas[]`, 0–1, `UpdateGas`, called by `IncidentSchedule.Update`; a no-op while `HasGas` is false): `gasSources` cells are kept at 1; every `GasSpreadInterval` (0.5 s, smoke: 0.2 s) each cell takes the max of itself ×`GasLinger` (0.995) and its 4 neighbours ×`GasSpreadFactor` (0.9), with no wind; outside the fence it blows away. So it creeps out about 2 m/s and settles at 0.9^distance. `ClearGas`, `GasNear(p, radius, level)`, `StartFires(cells)` (a flash: many cells at once).
  - **Collapses:** `lightTowers` (west 30,46 and east 72,46). `TowerFallCells(tower, direction)`: the strip a tower covers when it falls, `TowerFallLength` (8) cells long from its side and as wide as the tower (2 m). `AddWreckage(cells, reason, blockedDoors)`: open cells and barriers there become low steel obstacles (not flammable, 0.8 m), fire, fence and gates stay; a door under the wreck is blocked with `SetDoorBlocked` (cleared like any blocked door). `FireDistanceTo(rect)` for fire-proximity triggers.
  - Door fields are marked dirty by barrier, gate, block and fire changes. `UpdateDoorFields(max)` recomputes a few per step, to avoid frame spikes.
  - `fleeDirection` (per cell) is the step that increases `fireDistance`.
  - **Fire:**
    - It spreads every 0.8 s. Chance per neighbour each tick: 0.5 for burnable structures, 0.07 for grass, 0.2 for barriers, 0.03 for anything else.
    - Embers start spot fires 3–7 cells away (chance per tick: fire cells ÷ 60, at most 0.45), recorded in `SpotFires` and `LastSpotFireCell`.
    - The fire is capped at `maxFireCells` (400). Grass catches at 0.09 per neighbour per tick, and embers fly with a chance per tick of fire cells ÷ 50, at most 0.55.
  - **Smoke** (`smoke[]`, 0–1, updated every 0.2 s inside `UpdateFire`): it is 1 in the flames and spreads like the alarm field, but shaped by the wind: ×0.93 per cell downwind (+x), ×0.88 towards +y, ×0.75 against the wind, and ×0.96 lingering. The plume runs about 15 m downwind, matching the drift of the smoke visuals. Door fields add `smoke × 3` to the step cost, so routes avoid it.
  - Also handles gate animation, `seesOpenGate`, and unit pathfinding (`FindPath`).
  - **Regions** (`Region`, `regions`, `regionOf[]`): 9 named zones used for orders and labels. They don't change how people move.
    - The zones are Food Court, Drinks (Bar Tent), Toilets, Merch, Dance Tent, Main Stage, Backstage (Generator), Main Entrance and West Entrance.
    - Each `area` is its landmark's bounding rectangle grown by `RegionMargin` (5 m) and clipped to the inside of the fence. The Main Stage area also covers the crowd zone in front of the stage (22,46 60×17).
    - `AssignRegions` gives every cell strictly inside the fence a region: the only area covering it, otherwise (several areas or none) the region with the nearest area centre. Fence, entrance and gate cells and the outside are `-1`.
    - `post` is the open cell of the region nearest its area centre (where a patrol will stand). `RegionAt(cell)` returns the region or null.
    - `fireSites` entries carry their `region`.
- `CrowdSimulation.cs`: about 2,000 agents stored as parallel arrays. Pure simulation, no drawing, so it runs headless. `Step(dt, venue, incident)`.
  - **Awareness:** people notice the fire by seeing it (within `fireSightRange`, 25 m) or by hearing the `alarm` grid. The alarm is a shout field that spreads 1 m per 0.2 s from the fire and from everyone already evacuating, fading ×0.9 per metre.
    - Smoke and the sight of an open gate also make people aware.
    - **The music stops** `showStopsAfter` (15 s) after the fire starts (`ShowStopped`). From then on, unaware people start leaving at `showStoppedAwareness` (0.15/s). This replaced the PA as the way the last scattered people find out: without it, 50–370 people stayed inside until the time limit even with good play.
  - **Smoke:**
    - Smoke above `smokeNoticeLevel` (0.2) makes people aware, and adds fear (smoke × 0.6).
    - Above `smokeHarmLevel` (0.3) a dose builds up at up to `smokeDoseRate` (0.15/s) in the thickest smoke: about 7 s at the flames, about 25 s in a medium plume.
    - The dose wears off at `smokeRecovery` (0.02/s) in clean air.
    - People with a dose of at least `intoxicatedDose` (0.15) show as "choking" (display state 6, olive, stooped) and walk up to 50% slower. At dose 1 they collapse (`InjuredBySmoke`).
  - People near an open emergency gate (`seesOpenGate`) become aware. Before this fix, unaware people idled next to open gates and looked "stuck".
  - **Escape:**
    - Within `fleeDistance` (10 m) of the fire, people first follow `fleeDirection` until they're clear (`hasFled`).
    - Then they head for the cheapest door (`ChooseDoor`).
    - Within `closedDoorSight` (6 m) of a shut door, they set its bit in `doorsFoundShut` and pick another door.
    - Neighbours share `doorsFoundShut`, so "it's shut" passes back through a queue. Seeing an open gate clears that gate's bit.
  - **Informed people** (told by stewards or megaphone police) skip doors they know are closed and add queueing cost (`doorCrowd` × `doorCrowdWeight`).
  - **Zone evacuation orders** (`OrderRegionEvacuation(region, direction, venue)`, no UI yet):
    - It picks one open door that the region's `post` can reach. Score = cos(angle between `direction` and the line from the post to the door centre) − walking distance from the post × `orderDistanceWeight` (0.01 per metre). `direction` is in grid x, y (= world x, z).
    - Everyone active in the region is informed (`Inform`: evacuating, told which doors are open) and gets `orderedDoor[i]`. It returns the door, or -1 if no open door is reachable (people are still sent off, just without a door).
    - `PickOrderDoor(region, direction, venue)` is the same choice without ordering anyone (UI previews). `CountInRegion(region, venue)` counts active people standing in a region.
    - `ChooseDoor` returns the ordered door first, as long as it's open, reachable, not in `doorsFoundShut`, and the person's panic is at most 0.8. Otherwise the normal choice applies. The order isn't cleared: if a closed gate reopens, people follow it again.
    - People next to the fire still run from it first (`hasFled`), and panicked people don't reconsider their door (the 2 s retarget only happens below 0.8 panic), so an order mostly steers calm people.
  - **Drunk people** (`drunk[]`): each person is drunk with chance `drunkFraction` (0.02, about 45 people; it was 0.05 and left ~130 inside, more than the team can walk out), ×`drunkDrinksMultiplier` (3) in the Drinks zone (`AssignDrunks`, right after `Spawn`; it uses the rng, so it shifts every seed compared with older runs).
    - They ignore staff: `ApplyStewards` and `PoliceAnnouncement` skip them, `Inform` returns at once, and zone orders leave them out. They never become `evacuating` (the awareness rules and panic > 0.3 skip them).
    - Panic target × `drunkPanicFactor` (0.5).
    - Movement (`DrunkVelocity`): within `fleeDistance` of the fire they run along `fleeDirection`; otherwise they wander at half pace on a Perlin heading and drift back once more than 6 m from their spot. Always at `drunkSpeed` (60%) with a ±35° zig-zag. The noise is deterministic (no rng).
    - They don't leave on their own. `DrunkInsideCount` counts the ones still inside, and `SoberActiveCount` = `ActiveCount` − `DrunkInsideCount`. The game and the baseline end the round on sober people (all sober out, or stalled). **Stall rule** (`EvacuationProgress`, shared by the game, the baseline runner and tests): all sober people inside are evacuating and for 20 s nobody got out *and* the sober person closest to an open exit (`ClosestSoberToExit`, door-field distance) got no more than 1 m closer. The old rule only counted exits, so stragglers on a long walk (15 people walking 45 m along the stadium fence) were cut off and counted as "still inside", which cost points and the third star in about 1 round in 10.
    - Display state 7 ("Drunk", light purple `AgentColors[7]`); `CrowdRenderer.StateCount` is 8.
    - Drunk wandering uses `CrowdSimulation.SmoothNoise` (managed value noise). It was `Mathf.PerlinNoise`, which is native and crashed every headless run that stepped drunk people (the baseline, any full-round test); with it gone `verify.ps1` runs the full baseline again.
    - **Escorts:** `escortedBy[i]` is the game's unit index (-1 none). An escorted person walks to the point set by `SetEscortTarget` (1 m behind the unit) at the given speed, ×1.3 when more than 1.5 m behind, and stands still within 0.3 m. `EscortOut(i, door)` makes them Safe and counts the exit. `NearestDrunk(p, max)` finds the active drunk person nearest a point.
  - **Exit tracking:** `exitDoor[i]` is the door each person left through (nearest door centre when they reach the safe zone; -1 while inside or injured), and `exitsByDoor[d]` counts them.
  - Panicked people (above 0.8) don't reconsider their door (tunnel vision).
  - **Panic** rises towards a target, the highest of:
    - fear of the fire;
    - crush stress: density, plus how long they've been stuck (moving under 25% of walking pace while trying);
    - neighbours' panic × `contagionFalloff`.
    It decays slowly once the situation improves.
  - Fear of the fire is 1 only at touching distance (`fireDistance` ≤ 1). Further away it's capped at 0.85, so a big fire alone doesn't send everyone into panic attacks.
  - **Panic attack:** panic ≥ `panicAttackThreshold` (0.88) for `panicAttackDelay` (2.5 s) freezes the person for 6–12 s at panic 1, which spreads fear. Stewards shorten attacks.
  - **Escape fix:** fleeing stops once running away no longer helps: when the cell has no `fleeDirection` (cornered), or after 4 s of fleeing (`fleeTime`). Before this, people cornered against the fence 4–5 m from the fire shuffled there until the time limit.
  - **Steward calming:** after the incident, panicked people in a steward's ring stop and listen (`listeningTime`). The panic around them doesn't affect them while the steward brings their panic down at `stewardCalming`. At `calmedPanic` (0.35) they're informed and released, and stay reassured for `calmedMemory` (12 s): their panic target is capped at 0.45 unless the flames are next to them. Anyone near the flames or in smoke (`InDanger`: fire distance under 3, or smoke above the notice level) skips the stop-and-listen and keeps fleeing.
  - **Steward queues:** after the incident, everyone in a steward's ring also gets `orderlyTime` (`orderlyMemory`, 4 s), which is ignored in smoke or near the flames. While it lasts they:
    - keep `stewardQueueSpacing` (0.52 m) between bodies instead of 0.44 m;
    - give way fully (no panic shoving);
    - add only 30% of the usual panic speed boost;
    - don't build up stuck stress.
    A wider spacing (0.62 m) throttled the narrow entrances and pushed the jam, and the crushes, outside the ring.
  - Pressure is (standing density + 0.3 × fallen density) × panic factor, and crush damage builds above `crushThreshold`. Counting fallen people fully made crushes cascade, from 121 to 1,008 injured in one scenario. Fallen people push only weakly (0.15), so others can step over them.
  - **Gas dose** (`gasDose[]`, separate from `smokeDose`; settings `gasHarmLevel` 0.2, `gasDoseRate` 0.08/s, `gasRecovery` 0.03/s, `coughDose` 0.1): it builds up above the harm level whether or not the incident is known (computed before the `incident` block), collapses at 1 (`InjuredByGas`). Gas causes no fear and no fleeing: people only run when they see others fall (the existing panic from injured neighbours) or are told. `CoughingCount` = people inside with a dose ≥ `coughDose`. Display state 6 (intoxicated) covers smoke or gas.
  - Collapse effects: `CrushUnder(cells)` injures everyone standing there (`InjuredByCollapse`, counted in `InjuredCount`). `Shock(center, radius)` gives everyone in range `shock[i] = ShockFear` (0.85, the fear of standing 2 m from the flames), a floor under their fear target that fades to 0 over `ShockDuration` (10 s), and makes them evacuate (drunk people only get the fright). `shock` is 0 unless `Shock` is called, so other runs don't change.
  - Unit effects: `ApplyStewards` and `PoliceAnnouncement` (one megaphone pulse). Both use the spatial hash from the last `Step`. `ApplyStewards(positions, dt, incident, radius)` takes an optional ring radius; the game passes `patrolRadius` (6 m) for zone patrolmen, and without it the ring is `stewardRadius`.
- `Difficulty.cs` (pure, runs headless):
  - `Difficulty { Easy, Medium, Hard }` and `DifficultySettings.For(d)`: `playerCount` 600 / 1400 / 2000, `drunkFraction` 0.01 / 0.02 / 0.02, `maxBlockedDoors` 1 / 2 / 3, `disasterCount` 1 / 2 / 3, `scoreMultiplier` ×1 / ×1.5 / ×2, `patrolCount` 6 / 5 / 4 (zone patrols the player gets, for 9 zones), `gateCrews` 4 / 3 / 3 (how many emergency gates can be open or opening at once). `Stars(count, safe, injured, soberInside, drunkLeft, drunkTotal, evacuationTime, timeLimit)` (game, results, tests): 1 = 95% safe; 2 = also injured ≤ 1% and out within 50% of the limit (120 s); 3 = also nobody sober inside, injured ≤ 0.25%, at most a third of the drunk fans left, out within 40% (96 s). `StarSummary` is the menu text. `Score(...)`: the round score (+10 safe, −100 injured, −25 still inside, −50 drunk left inside, −10 per panic attack, +10 per second left), times the multiplier; used by the game and the tests.
  - `RoundGenerator.Build(difficulty or settings, venue, rng, schedule)`: `disasterCount` main disasters, each a random kind (fire, tower collapse, gas leak) at a random free place (never the same kind twice at one place: 6 fire sites, 2 towers, 2 gas sites); the first at 20–40 s (`FirstDisasterMin`/`Spread`; was 4–12 s, same rng calls), each next 20–40 s after the previous. Then 0..`maxBlockedDoors` `BlockDoorIncident`s (random door and reason, random time in 0–60 s), never both entrances, at least 3 doors left unblocked. With a fire or a gas leak in the round, every tower that isn't a main disaster gets `WhenFireNear`. `Describe(incident)` for logs and reports.
- `Incidents.cs`: disasters as objects, so a round can have several kinds, chained.
  - `Incident` (abstract): `name`, `region`, `startTime`, optional `trigger`, `Started`, `Age`, `Active`, plus the texts and camera cell used when it starts (`SiteName`, `Headline`, `LogText`, `Advice`, `FocusCell`). Subclasses override `Start(venue, sim)` (once) and `Update(dt, venue, sim)` (every later step).
  - `IncidentTrigger`: start counting `startTime` only once incident `after` has run `afterSeconds` and (optionally) the venue has `afterFireCells` burning cells.
  - `FireIncident`: a fire at a `fireSites` entry; with no site given, it picks one with the game's rng **at the moment it starts** (as the old `StartIncident` did). `FireIncident.At(venue, "the Food Court", rng)` fixes the site. Fire spreading stays in `FestivalVenue.UpdateFire` and is shared by all fires.
  - `IncidentSchedule.Update(dt, venue, sim, rng, started)`, every step while Live: pending incidents whose trigger is met count `startTime` down (`timer -= dt`, the same arithmetic as the old countdown) and start at 0. Then, **only if something had started before this step**, the venue's fire spreads once (`UpdateFire`) and every incident started before this step gets `Update`. It returns whether the fire changed. `StartNow` starts one at once (headless tests).
  - `BlockDoorIncident(venue, door, reason)` (`At(venue, "Main Entrance", reason)`, `RandomDoor(venue, rng)`): blocks the door when it starts (and evicts anyone standing in it). `Active` while the door stays blocked. `StartsEvacuation` is false (new `Incident` property, true by default): a blocked exit alone doesn't raise the alarm or start the evacuation clock.
  - `TowerCollapseIncident(venue, towerIndex, direction)` (`RandomDirection(venue, tower, rng)`): sways for `SwayTime` (6 s) after it starts (banner "LIGHT TOWER IS SWAYING!"; it starts the evacuation, so zone orders work), then in its `Update` falls: `CrushUnder(wreck)`, `AddWreckage` (reason "Collapsed light tower", doors hit go in `blockedDoors`), `Shock(WreckCenter, ShockRadius = 12)`. `Collapsed`, `Crushed`, `Shaken`, `SwayProgress`. `WhenFireNear(venue, tower, rng)` chains it to the fire: trigger "flames within 4 m of the tower" and a start time so it falls 10–20 s (sway included) after that.
  - `GasLeakIncident(rng, site)` (`Sites`: "the Generator", "the Food Court"; null = random when it starts): adds the site's cell to `gasSources` for `LeakDuration` (90 s). `Silent` (new `Incident` property): the game shows nothing when it starts, and `StartsEvacuation` is false; it's `Detect(by, time)`-ed later (`Detected`, `detectedBy`, `detectedAt`). `Smells(venue, p, radius)` = gas ≥ `DetectLevel` (0.05) in the ring. If gas ≥ `IgniteLevel` (0.25) is next to a flame, it catches (`Ignited`): cells with gas ≥ `FlashLevel` (0.6) flash into fire, the gas is cleared, and a `FireIncident(cell, "the gas cloud by …", region)` (new fixed-cell constructor) starts through `StartFollowUp`.
  - Follow-ups: `Incident.StartFollowUp(next)` queues an incident in its schedule (`owner`); `IncidentSchedule.Update` starts queued ones at the end of the step (reported in `started`, updated from the next step).
  - `IncidentTrigger` conditions can now be combined and used without `after`: `after`/`afterSeconds`, `afterFireCells`, `fireNear`/`fireNearRange`. `Incident.Announcement` is what the megaphone police shout ("Fire at the Food Court!").
  - This order matches the old single-fire code exactly (the step a fire starts it doesn't spread yet; the site is drawn from the rng at that moment), so every seed gives the same numbers as before the refactor.
- `CrowdRenderer.cs`:
  - `PersonModel.Build` instantiates a character prefab and merges all its parts into one mesh, with feet at the origin and height `personHeight`.
  - With clips and the shader, it bakes **vertex animation textures (VAT)**:
    - It plays each clip through a `PlayableGraph` (manual update), calls `BakeMesh` per frame at 30 fps, and writes positions and normals into RGBAHalf textures (row = frame, column = vertex).
    - Rows hold idle, walk, run and wave one after another (`clipStart`/`clipFrames`).
    - Clips are matched by name: "normal man a" gets "normal walk", and so on.
  - It needs mesh Read/Write enabled, which the editor command turns on.
  - `CrowdRenderer` draws animated models with `Graphics.RenderMeshPrimitives`: one GraphicsBuffer of `CrowdInstance {objectToWorld, anim}` per model, indexed by `SV_InstanceID` in the shader.
  - It picks walk or run by speed and panic, and plays them at speed ÷ stride. Fans cheer (wave) until they're aware, injured people lie flat, and people having a panic attack crouch and tremble.
  - Without the shader or clips it falls back to static instanced models; without models, to coloured cubes. With models, a state-coloured tile goes under each person (toggle with M).
- `CrowdVAT.shader` (`CrushHour/CrowdVAT`): a URP HLSL shader with UniversalForward and DepthOnly passes. It reads `_Instances[SV_InstanceID]` and loads the two neighbouring frames from `_PositionTex`/`_NormalTex` by `SV_VertexID`. Lighting is main light plus a flat `_Ambient`, with no shadows. It is referenced from `CrushHourGame.crowdAnimationShader` so it's included in builds.
- `CrushHourGame.cs`: a single MonoBehaviour that builds the whole scene at runtime.
  - **Control-room mode** (`InControlRoom`, when the scene has a `ControlRoom`): `Start` builds the room and gives the main camera to it; `cameraRig` is then a `TopDownCamera` on an empty child object, so the existing `cameraRig.Focus` calls move nothing visible. `HandleMouse` works through the screens (see `ControlRoom.Screens.cs`). `OnGUI` draws only the top strip, the banner, the hover tooltip, the briefing card and the debrief (no world labels, panels, toolbar, zone order panel, toggles, legend or hover). Keyboard controls are unchanged (Enter starts the show, Space/Tab pause/speed, Esc in Planning opens the menu).
  - **Scene flow:** PLAY in the start menu saves the map and difficulty, and, unless the game is already in the control room, loads `ControlRoomScene` ("CrushHourControlRoom", must be in Build Settings) with the static `skipMenuOnce`/`launchedFromMenu`, so it opens straight in Planning with the difficulty's real crowd size. If the scene isn't in Build Settings it logs a warning and plays top-down as before. "Main menu" in the control room opens the menu over the room; PLAY there starts a new game in place.
  - Round phases: Planning → Live (fire after a random delay) → Results.
  - **Start menu** (`CrushHourGame.Menu.cs`, a partial of the class; `Phase.Menu` comes before Planning): opens on `Start` (crowd idling behind it), from Results ("Main menu") and with Esc in Planning when nobody is selected. "SELECT MAP" lists `Maps` (title, scene name, description; for now only "Summer Music Festival" = `CrushHourFestival_Dev`); "SELECT DIFFICULTY" has Easy / Medium / Hard with the player count, drunk share, blocked exits and disasters of the chosen one; no locks. PLAY saves the difficulty; if the map is this scene it starts a new game in Planning, otherwise it loads the map's scene (`SceneManager.LoadScene`; it must be in Build Settings, otherwise the menu says so) with `skipMenuOnce` set, so that scene opens straight in Planning. Keyboard and mouse game controls are off in the menu.
  - Difficulty: chosen in the start menu, or in Planning with F1/F2/F3 (`SetDifficulty`, saved in PlayerPrefs `CrushHour.Difficulty`, loaded in `Start`, default Medium), which starts a new game. `NewGame` sets `crowd.agentCount` (the difficulty's `playerCount`, unless `crowdSizeOverride` > 0) and `crowd.drunkFraction`. The difficulty and its multiplier show in the top bar and in Results; the score is `difficultySettings.Score`.
  - Incidents: `NewGame` makes an empty `schedule`; `StartShow` fills it with `RoundGenerator.Build` for the difficulty (the rng gets a new seed every `NewGame`, so every round is different). `Simulate` calls `schedule.Update` while Live, then `OnIncidentStarted` for each incident that began (the first evacuation incident sets `incidentAnnouncement`, `incidentStarted` and resets the evacuation clock; each gets a feed line, banner and camera jump) and `OnFireChanged` when the fire spread (flame cubes, ember log).
  - Blocked doors: `OnIncidentStarted` builds the blocker from cubes (`CreateBlockerView`: a white truck with a red cab parked across the door outside, or fallen scaffolding poles and planks over it), with a Warning feed line and banner. Every frame `MoveUnits` sets `unit.clearingDoor` for a unit standing still (no path, no escort) within 2 m of a blocked door; such a unit's ring is off (patrol: not in `patrolPositions`; police: no announcements). `UpdateDoorClearing` counts the workers per door and calls `venue.ClearDoor` (one: 8 s, two: 4 s); when it's clear the blocker disappears and a Good feed line and banner say so. Clicking a blocked door sends the selected unit (`SendToClear`, to `DoorFrontCell`); with nobody selected, an entrance gets the nearest unit and a gate is still opened/closed. The door's world label says "BLOCKED: <REASON>" with a progress bar while it's being cleared; hover: "Blocked: Delivery truck. Send a patrol to clear it (8 s)."
  - Gas: `CheckGas` (every Live step) detects an unknown leak when a unit's ring has gas (each unit logs "<Zone> patrol smells gas!" / "Police N smells gas!" once) or someone coughs ("People are coughing near …"). `DetectGas` then shows the yellow-green layer on the ground (`gasVisible`, only cells above `DetectLevel`), starts the evacuation if nothing has, and logs/banners "GAS LEAK AT …". Hover shows "Gas N%" (POISONOUS above the harm level). `OnIncidentStarted` skips `Silent` incidents. Results and the injury feed line count gas; a tip when gas hurt the most. With two leaks in a round, the first one not noticed yet is the one reported.
  - Light towers: the round generator chains the towers to the fire (`WhenFireNear`). The tower blocks are kept in `towerViews`; when one starts swaying `HingeTower` puts it on a pivot at its foot on the fall side, `UpdateTowerViews` wobbles it (growing lean) and topples it in 0.8 s once it has collapsed. `OnTowerCollapsed` (polled after `schedule.Update`) adds rubble cubes and the lighting rig at the far end, a feed line (crushed, shaken), banner "LIGHT TOWER COLLAPSED!", and a warning per blocked door. Results show a "Crushed by collapse" row when there were any, and a tip when the collapse hurt the most people. The top bar and the police use `incidentAnnouncement` (from the first evacuation incident).
  - Units (`Unit`, `Role`): they're selected from the roster or the map, then sent by clicking the map.
    - **Zone patrolmen** (`Role.Patrolman`): `difficultySettings.patrolCount` of them (6 / 5 / 4 for 9 zones), spawned at the posts of the busiest zones at the start (`SpawnUnits` sorts zones by `CountInRegion`), named "Patrol N" (roster "P1"…). A patrol's `homeRegion` is the zone it stands in: `MoveUnits` calls `SetPatrolZone` when it walks into another zone (recolours the cap and facing arrow, drops the old order arrow), so zone orders and escort preference follow it. Same effect as the old stewards (`ApplyStewards`, ring `crowd.patrolRadius`, 6 m): calm people, then send them out in orderly queues. Look: yellow body, cap in the zone colour.
    - **Gate crews:** `ToggleGate` only opens a gate while `FreeGateCrews` > 0 (`gateCrews` minus gates open or opening); otherwise a warning line and banner ("NO FREE GATE CREW"). "Open all exits" is gone: picking gates is the decision.
    - **Warning signs** (`CheckWarningSigns`, every Live step): `Incident.WarningLead` (12 s) before an incident with a `WarningText` starts (a fire at a known site: "Smoke seen at …"; a stand-alone tower collapse: "… light tower … is creaking"), a Warning feed line, banner and a pulsing "! | SMOKE SEEN" / "! | TOWER CREAKING" map tag at `WarningCell` until it starts. Gas stays silent, blocked exits and chained collapses have none. `Incident.TimeToStart` exposes the countdown.
    - **Exit throughput** (`UpdateThroughput`, every step): once a second a snapshot of `sim.exitsByDoor`; `ExitRate(d)` = people per minute over the last 10 s. Shown in the Exits panel (every exit, entrances included) and under each open gate's sign / each entrance on the map ("42/MIN").
    - Each patrolman has a `facing` (grid x, y, unit length), drawn as a small arrow on the ground in the zone colour. It defaults to the zone's area centre, or the centre of the grounds when the post already is the area centre. Change it with `SetFacing(unit, direction)`, which also redraws the arrow. It doesn't affect the crowd yet (it will set the direction of zone evacuation orders).
    - 2 megaphone police (`Role.Megaphone`; navy with a cyan cap): announce every 5 s within 14 m, with a ripple.
    - The roster groups them: "ZONE PATROLS" (swatch in the zone colour), then "MEGAPHONE POLICE".
    - **Zone orders (UI):** with a patrol selected, the "EVACUATE REGION" panel (bottom centre, `DrawZoneOrderPanel`) shows the zone's head count and 4 buttons, Front / Left / Right / Back. Each is relative to the patrol's `facing` (`SideDirection`; left = 90° anticlockwise in grid x, y) and shows the door it would pick (`sim.PickOrderDoor`). The arrow keys do the same (`HandleKeyboard`).
      - `OrderZone` works only in the Live phase after the fire starts. Each zone has a 5 s cooldown (`regionOrderCooldown`, ticked in `Simulate`). It calls `sim.OrderRegionEvacuation`, logs "<Zone> patrol: evacuating via <door> (N people, side)", and stores `unit.orderDoor`.
      - `UpdateOrderArrow` draws a big ground arrow (zone colour, width 0.55) from the patrol to that door. It follows the patrol and is hidden while the door is shut.
      - Right-click on the map with a patrol selected turns it to face that spot (`SetFacing`); otherwise right-click deselects as before.
      - While a patrol is selected, the arrow keys don't pan the camera (`TopDownCamera.arrowKeysPan`, set every frame); WASD still does.
      - **Escorts** (`EscortPhase`, `UpdateEscorts`, run after `MoveUnits` every step). **Group escorts:** when a unit takes the fetched person (`BeginWalkOut`) it also takes every unescorted drunk fan inside its ring, up to `MaxEscortGroup` (6) in all (`unit.group`); they follow in a line (`EscortLineGap` 0.8 m apart); at the door it waits up to `EscortDoorWait` (6 s) for the line, then takes out everyone close enough and releases the rest: with a unit selected (patrol or police), clicking a drunk person (within 1.2 m of the click, and closer than any unit) calls `StartEscort`.
        - Within 3 m the unit takes them at once (`BeginWalkOut`). Otherwise it goes to fetch them first (`Fetching`, re-pathing every 1 s because they wander).
        - `RouteToNearestDoor` picks the open door with the shortest walk (`door.field.distance`) and paths to its approach cell nearest the door centre (`FindPath` can't end outside).
        - While `Escorting`, the unit moves at ×0.7 speed, its ring is hidden, and it calms nobody (a patrol isn't in `patrolPositions`; police don't announce). The person follows 1 m behind along `unit.heading`. If the door shuts, it re-routes.
        - At the door, once the person is within 3 m, `sim.EscortOut` and the unit returns to its `home` post (`Returning`).
        - A new move order cancels the escort (the person goes back to wandering). If the person gets hurt, the unit goes back. If another unit takes over, the first one drops it.
        - The zone order panel also has **"Escort nearest drunk [F]"** for the selected patrol (`EscortNearestDrunk`): the nearest drunk person nobody is escorting yet, in the patrol's own zone first (`sim.NearestUnescortedDrunk(p, region)`), otherwise anywhere. The button shows how many are in the zone (`UnescortedDrunksInRegion`) and inside overall, and is disabled while the patrol is already walking someone out.
        - The roster shows "fetching drunk" / "escorting" / "returning". Hovering a drunk person shows "Drunk – needs escort" (or who is walking them out).
      - The panel is plain `GUI` (no `GUILayout`), and the unit it shows (`orderPanelUnit`) is fixed at the Layout event, so selecting someone mid-frame can't break IMGUI. Buttons queue `OrderZone` through `deferred`.
  - Tools: command (units and gates) and barriers (budget 60). Also scoring (−10 per panic attack, −25 per sober person still inside, −50 per drunk person left inside) and stars (`DifficultySettings.Stars`). Results show "Still inside" (sober) and "Drunk left inside". Game speed is 1× or 2× (`Speeds`).
  - The simulation runs on a fixed 0.04 s step, at most 6 steps per frame.
  - Voxel smoke: a pool of up to 160 unlit grey cubes rising from random fire cells and drifting with `Wind`. Ember spot fires are logged.
  - `crowdSizeOverride` (0 = off): when above 0, `NewGame` sets `crowd.agentCount` to it (at least 100) before building the crowd, whatever the difficulty. It sits outside `crowd`, so `UpgradeSettings` never resets it; that's how the dev scene keeps 400 people even though its saved `settingsVersion` (3) makes the crowd settings reset to code defaults on load.
  - **Settings version:** every Inspector value is saved in the scene and overrides code defaults. When you retune defaults, bump `CurrentSettingsVersion`; `UpgradeSettings` (run from `OnValidate` and `Awake`) then resets the team size, `maxFireCells` and `crowd` in older scenes. Version 3 fixed a scene stuck on 6 stewards, a 160-cell fire and old crowd settings. Version 4 applies the 400-cell fire and the new crowd settings. Version 5: zone patrolmen replace the stewards (`stewardCount` is gone; `stewardSpeed` became `patrolSpeed` via `FormerlySerializedAs`), and `crowd.patrolRadius` is new. Version 6: drunk people (`crowd.drunkFraction` 0.02 and the other drunk settings).
  - **UI** (IMGUI) is scaled by `Screen.height / 900` through `GUI.matrix`. All layout uses the virtual `vw`×`vh` size, and `guiRects` are in virtual units (`IsOverGui` converts).
    - Rounded panels and buttons use procedurally generated textures (`Rounded`, cached).
    - Zones view (`showRegions`, on by default, toggle with G or the "Zones [G]" button): `DrawRegionOutlines` tints 1-cell region borders on the ground texture (only the cell whose right or upper neighbour is in another region, so lines stay 1 m wide), `DrawWorldLabels` adds "<NAME> ZONE" at each region's post, and the map tooltip ends with the zone name.
    - **Look: "Operations desk"**, matching the menu art (design mockup: board B of the "Crush Hour HUD Variants" canvas). Cream paper panels with soft drop shadows, navy ink (`Ink`), red accents (`Red`), yellow tabs with a slanted end and `///` (`Tab`), numbered rows where the active one is a red slanted slab (`DrawSlab`), boxed "key | label" tags (`BoxedTag`, `WorldTag`), green EXIT signs with a pixel-art runner (`ExitSign`). All textures are generated and cached (`MakeTexture`: `Solid`, `Framed` 9-sliced outlines, `Slab`, `Stripes`, `PaperGrid`, `ShadowTexture`, `Runner`). Fonts: Barlow Condensed (ExtraBold Italic, Bold Italic, Bold, SemiBold) and Barlow (Medium, SemiBold) in `Assets/Resources/Fonts/` (SIL OFL, `OFL.txt`), loaded with `Resources.Load` in `EnsureStyles`; missing fonts fall back to the default. The arrow glyphs on the order buttons use the default font (`arrowStyle`) in case Barlow lacks them. The old colour names (`TextColor`, `MutedText`, `SelectedColor`, `GoodColor`, ...) now map onto this palette, and `ButtonStyle(fill)` gives a framed button (light text on dark fills). The zone order ground arrow is red (no longer the zone colour).
    - Elements (laid out by hand with `GUI`, not `GUILayout`, except the legend and the cards):
      - a paper top strip (`TopBarHeight` 60): CRUSH HOUR logo with red stripes, a phase tab ("LIVE — EVACUATION ///") with one line under it, the evacuation clock and striped bar, pause/speed, and boxed stats ("1284 | PEOPLE INSIDE"). It lays out from the right so the stats never overlap;
      - "01 — YOUR TEAM" (top left; patrols numbered by zone, `homeRegion + 1`, and named by zone; status only when busy) and "02 — EXITS" (top right; every exit, entrances first: sign (green runner, yellow opening, red ×, red "!" blocked), name, people/min, Open/Close or "NO CREW" / ENTRY / BLOCKED; header "N/M CREWS FREE" and a crew line at the bottom). Both fold on their title row (`showTeam`, `showGates`);
      - the toolbar at the bottom centre (`DrawToolbar`): menu-style slabs "1 | COMMAND", "2 | BARRIERS 42", and "ENTER | START THE SHOW" while planning, with a boxed one-line hint above (`ToolHint`). The "NN — EVACUATE <ZONE>" panel sits above it: Front/Left/Right/Back around a navy Escort button in the middle;
      - the incident log (bottom left, navy tab): the last 4 lines, each fading out 20 s after it was logged (`feedClock`, game time, stops while paused), so it's gone when nothing is happening;
      - view toggles stacked bottom right as boxed tags (`DrawViewToggles`: H Pressure, M State tiles, G Zones, L Legend, U Hide/Show HUD); the legend ("KEY", closed by default) opens above them;
      - **U** (`hudHidden`) hides the roster, exits, log, legend and hint, leaving the top strip, toolbar, zone order panel and the toggle;
      - on the map (`DrawWorldLabels`): zone tags "06 | MAIN STAGE" with a zone-colour bar (Zones view), landmark names, exit signs with the gate name at every gate, and a red-keyed "! | <DOOR> BLOCKED: <REASON>" tag with the clearing bar at blocked doors;
      - plus the event banner (a slab in the severity colour), map tooltips (framed paper), the briefing card while planning, the debrief (results) card, and the start menu card ("02 — OPERATIONS DESK", CRUSH HOUR title, "01 | START GAME").
- `ControlRoom.cs` (week 6): the first-person control room.
  - `Build(camera)` (called by `CrushHourGame.Start` when the scene has a `ControlRoom`) instantiates the OBJ models from `Assets/Models/ControlRoom` under "Control Room (generated)" at `origin` (-400, 0, -400), far from the venue (0..104 × 0..76) so venue cameras never see it: room shell, door leaf, video wall (`VideoWall_Single`, bottom edge 1.55 m), desk with 9 monitors and the minimap (`ControlDesk_WithMonitors`), control board, operator chair, 2 decorative CCTV cameras. Model fields are assigned by the editor script.
  - Layout (room-local, Unity coordinates): origin = floor, centre of the video-wall wall's inner face; the room runs to z = 9 (door wall), x ±6, 6.5 m high. Desk front edge at z 3.05, eye at (0, 1.2, 3.6) facing -z. **Unity mirrors X when it imports OBJ files**, so positions are `ControlRoom_Layout.json` with x and yaw negated.
  - Materials: every imported material is replaced by a URP Lit clone of `litMaterial` with the colour from `MaterialColors` (the Kd values of the .mtl files), by material name, so the look doesn't depend on Unity's OBJ/MTL import. Names with "Screen" are dark, slightly glowing glass ("no signal") until the CCTV feeds arrive; lamps, LEDs and the exit sign are emissive; the switch covers are transparent.
  - Lights: 6 point lights under the ceiling lamps (`lampIntensity` 20, `lampRange` 10, no shadows). Everything in the room is on layer `RoomLayer` (29); the room lights light only that layer, and every other light in the scene (the sun) has it removed from its culling mask, so the sun doesn't shine through the ceiling.
  - Operator view: the main camera (near 0.03, far 60, so the venue isn't drawn by it). Seated; **WASD rolls the chair** (`chairSpeed` 1.6 m/s, relative to where you look, only in free look) in front of the desk: at least 0.46 m from its front edge, following the angled wings, x ±2.6, back to z 8; the chair model follows and turns with the view; mouse look (`lookSensitivity` 0.12°/pixel, slower while zoomed), yaw clamped to ±`maxYaw` (120°) from facing the wall, pitch -55..65°. Right mouse held = zoom (`zoomFieldOfView` 22° vs 60°). A small crosshair in the centre (`OnGUI`). The cursor is locked only while `lookEnabled`, which the game sets every frame: Live, or Planning once the briefing card is closed (free in the menu, on the briefing and on the debrief).
- `ControlRoom.Cctv.cs` (week 6 step 2): CCTV feeds.
  - `SetupCctv(venue)` (called at the end of `NewGame`, so it follows the map): one camera per region under "CCTV Cameras (generated)". Each stands on a **pole in the venue** (grey mast, base plate, arm, white camera housing; built from primitives under the CCTV root, so other cameras see the poles like in real life): the pole's foot is `cameraInset` (30 %) of the way from the region's `area` corner farthest from the venue centre towards the area centre; `cameraMinHeight`..`cameraMaxHeight` (6–9 m, 0.25 × the area's diagonal) high; the camera hangs 0.9 m along the arm towards the zone, looking at the area centre; the field of view is the smallest that fits the inner `framing` (60 %) of the zone (20–90°), so the picture is close on the zone. Cameras cull `RoomLayer` and `MinimapLayer`, near 0.5, far 250, no HDR/MSAA, and are disabled: `UpdateCctv` enables the ones whose turn it is (each `feedFps` 12, at most `maxFeedsPerFrame` 3 per frame), so they render into their `feedWidth`×`feedHeight` (480×270) RenderTexture that frame.
  - Desk monitors: the desk's `Monitor_xx` renderers sorted from the operator's left to right (+X in the room), zone 1 on the far left. Each gets its own screen material (the feed as base and emission map) and its own frame material (`M_Monitor_Plastic` clone) whose emission flashes: red (fast blink) for `NewIncidentFlash` 20 s after an incident starts in the zone, steady red while it's still active, yellow (slow blink) for pressure above 60 % of `crushThreshold` or detected gas (> 0.05) in the zone. Zone status is computed in `CrushHourGame.ControlRoom.cs` (`UpdateControlRoomFeeds`, every frame; the pressure/gas scan 4× a second; an incident's zone is `incident.region`, else the region of its `FocusCell`; undetected gas leaks don't count).
  - Caption "CAM 03 | FOOD COURT ● REC 00:42" (clock = `feedClock`, red dot blinking): a `TextMesh` (built-in LegacyRuntime font) 1 m in front of each lens, top-left of the picture, on `CaptionLayer` (28). `RenderPipelineManager.beginCameraRendering` enables only the caption of the camera being rendered, so each caption is seen only by its own camera; the operator camera culls layer 28.
- `ControlRoom.Wall.cs` (step 3): the video wall and the minimap.
  - Video wall: "Video Wall Camera" (1920×1080, `wallFps` 30) copies the selected zone's CCTV camera (position, rotation, field of view) and renders onto the `M_VW_Screen` submesh of the wall (its own glowing material). Caption like the desk feeds plus AUTO/MANUAL, and a second blinking yellow line "WARNING: …" for `warningTime` 12 s after `SignalWarning` (smoke, creaking tower; the wall doesn't switch). Switching shows a big centred "CAM 04 – FOOD COURT" for `switchTitleTime` 2.5 s and a short cut (blank 0.08 s, then over-bright fading in). Both captions are on `CaptionLayer`, enabled only while the wall camera renders.
  - `SelectWall(region, manual)`: manual picks turn AUTO off. `SetWallAuto()`: AUTO again. `OnIncident(region)` (from the game when an incident is announced, and when a gas leak is detected): switches only in AUTO. The wall starts in AUTO on zone 1 every new game. Step 5's board buttons will call `SelectWall`/`SetWallAuto`.
  - Camera picks (in `UpdateRoomInput`, see `ControlRoom.Screens.cs`): in free look a left click on a desk monitor puts its zone on the wall; while the pointer is on the wall, 1–9 (or numpad) pick a zone, 0 = AUTO (the game's 1/2 tool keys are ignored then).
  - Minimap: "Minimap Camera", orthographic straight down over the whole venue (size fits 104×76 in 16:9), 1280×720, `minimapFps` 10, onto the desk's `M_Minimap_Screen`. It sees everything but the room and the captions, including `MinimapLayer` (27), which every other camera culls. The markers are made by the game (`CrushHourGame.ControlRoom.cs`, under the world so `NewGame` clears them): zone tint (one pixel per cell in the zone colour, 30 % alpha) and big zone numbers (= CAM numbers), a disc per unit (patrols in their zone colour, police blue), a square per exit (green open, red closed, blinking red blocked), a blinking red disc per known active incident and a yellow one per warning sign not started yet.
- `ControlRoom.Screens.cs` (step 4): reaching the venue through the screens.
  - `BuildScreenSurfaces` (end of `SetupWallAndMinimap`): for every desk monitor, the video wall and the minimap, the triangles and UVs of its screen submesh in world space (needs readable meshes: `Editor/ControlRoomModelImport.cs` imports `Assets/Models/ControlRoom` with Read/Write on), plus its centre, size and facing (towards the operator).
  - `TryGroundRay(screenPoint)`: operator ray → `HitScreen` (ray/triangle, barycentric UV) → the source camera's `ViewportPointToRay(uv)` (the desk monitor's CCTV camera, the wall camera, or the orthographic minimap camera). `CrushHourGame.TryGetCellUnderMouse` uses it in the control room, so clicks, barrier drags and the hover tooltip all work through the screens with the existing logic.
  - `UpdateRoomInput()` (called by the game at the start of `HandleKeyboard`, every frame): `PointerScreen` is the crosshair (screen centre) in free look or the mouse in focus. **E** on a screen = focus: the view glides (0.25 s) to a point in front of the screen where the picture fills ~90 % of the view, the crosshair goes and the cursor is free. **Esc, right click or E** = back to free look (`EscapeTakenByRoom` stops the game's Esc from also opening the menu). Leaving Live/Planning ends focus.
  - Game side (`HandleMouse`): in the control room, left click = click (unless `ClickTakenByRoom`: a desk-monitor click in free look picks the wall camera); **Shift + left click** = the old right click (remove a barrier, turn a patrol / deselect), because right mouse is zoom in free look and "leave" in focus. `IsOverGui` isn't used there. `DrawHoverInfo` is drawn in the control room too, next to the crosshair or cursor when it's on a screen.
- `ControlRoom.Board.cs` (step 5): the exits board on the desk.
  - `SetupBoard` (end of `Build`): finds the board model's parts by name and hangs the moving ones on pivots computed from the board geometry (`ControlBoard_Layout.json`: panel tilted 12°, u mirrored to -x): `Switch_N_Lever` (rotates about the panel's x, ±24°), `Switch_N_Cover` (hinge at its back edge, lifts to -105°), `CamButton_1..9`, `CamButton_Auto`, `StartButton` (pressed 4 mm into the panel for 0.15 s). Lamps and buttons get their own materials (emission). Texts are `TextMesh`es lying on the panel: each exit's label (name + people/min, or "BLOCKED: reason" in red), digits/AUTO on the camera buttons, "EXITS", "WALL CAMERA", "START SHOW", and "CREWS 2/3" on the crew display (red when no crew is free).
  - Switch N = `venue.doors[N-1]`. Entrances (no gate) show only the lamp; switches beyond the door count are hidden. Lever on (and cover lifted) while the gate is wanted open. Lamps: green open, yellow opening, steady red closed, blinking red blocked. A refused flick (`onSwitch` returns false, i.e. no free gate crew: the game also shows its "NO FREE GATE CREW" banner) kicks the lever part way, springs back, and the lamp blinks red for 1.5 s (`RejectSwitch`).
  - Camera buttons: 1–9 = `SelectWall`, AUTO = `SetWallAuto`; the current wall camera's button (or AUTO) glows; buttons beyond the map's zones are hidden. START glows green while in Planning and calls `onStart` (the game's `StartShow`).
  - Input (`BoardInput`, from `UpdateRoomInput` in free look, before the screens): the crosshair ray against the parts' bounds (≤ 3 m); a hint under the crosshair ("North Gate: click to open (2/3 crews free)"); a click there is `ClickTakenByRoom` and the pointer doesn't reach the minimap behind the board.
  - Game side (`CrushHourGame.ControlRoom.cs`): `UpdateBoard` each frame sends every door's state, block reason and people/min (`exitRate`) plus free/total gate crews and whether it's Planning; `BoardSwitch(door)` → `ToggleGate` (now returns false when refused). Space/Tab (pause/speed) stay on the keyboard.
- `CrushHourGame.Radio.cs` (step 6): orders over the radio, CS2-buy-menu style (control room only).
  - `UpdateRadio()` runs first in `HandleKeyboard` in the control room (before `controlRoom.UpdateRoomInput()`). **Hold V** (free look, not in focus, not on the debrief): wheel 1 = targets, one slice per unit (patrols "P3" + zone name in the zone colour; police "Police 1/2" in blue). While a wheel is open the mouse moves a virtual pointer (the view stops turning: `controlRoom.radioOpen`); the slice is the pointer's direction (slice 0 at the top, clockwise; 22 px dead zone). Release V or click to choose; releasing V without pointing closes it.
  - Wheel 2 (orders; choose with a click): patrols: EVACUATE FRONT / RIGHT / BACK / LEFT (sub-line: the door `sim.PickOrderDoor` would pick, as on the old order panel) → `OrderZone`; ESCORT DRUNK → `EscortNearestDrunk`; CLEAR EXIT → wheel 3 of the blocked exits → `SendToClear`; GO TO ZONE → wheel 3 of the zones (people count) → a path to the zone's post; BACK TO POST → `ReturnToPost`. Police: the same without the evacuation slices. Greyed slices show why: "show not started", "before the incident", "cooldown N s", "already escorting", "no drunks left", "no blocked exit". Choosing a target also selects that unit.
  - Each order is answered in the log as on the radio: "P3: Copy, evacuating the zone via East Gate A." A short burst of static (an `AudioClip` made in code, `AudioSource` on the camera) plays on opening and on every choice. Right click, Esc (doesn't open the menu then) or V again cancels. `radioBusy` keeps that frame's clicks away from the room and the map; the hover tooltip is hidden while the radio is open. `DrawRadio` draws the wheel in the scaled GUI (slices as cards around the centre, hub with the title, pointer dot, hint line).
  - The walkie-talkie (`ControlRoom.walkieModel`, `WalkieTalkie.obj`) is a child of the operator camera, low left in the view (screen towards the eye); it rises and its LED glows red while the radio is open, and hides in focus or when a card/menu is up.
- `ControlRoom.Info.cs` (step 7): the HUD's information on room objects.
  - Two slanted info screens (0.50 × 0.22 m, tilted back 53°, low so the monitors behind stay visible) on the middle desk segment either side of the minimap console: **INCIDENT LOG** on the operator's left, **TEAM** on the right. Primitives (bezel cube + dark glowing quad) with `TextMesh` texts (`LogTitle`/`LogText`, `TeamTitle`/`TeamText`).
  - The wall clock: text on the room's `M_Clock_Screen` quad (side wall), facing into the room (`ClockText`).
  - The game fills them 4× a second (`UpdateInfoScreens` in `CrushHourGame.ControlRoom.cs`): the last 11 log lines, newest first, coloured by severity (time stamp once the incident started, cut at 52 characters); one line per unit (callsign + zone in its colour, then `UnitStatus`) and the free gate crews in the title; the clock shows the evacuation time once the incident started (before that the show clock), then PLANNING / LIVE (xN) / PAUSED / ENDED and the people inside.
  - The top strip is off in the control room; **U** shows/hides it there (`controlRoomTopBar`; elsewhere U still hides the HUD panels). Banner, hover, radio and the briefing/debrief cards still draw over the room (cursor free on the cards).
- `ControlRoom.Atmosphere.cs` (step 8): atmosphere and performance.
  - CCTV look: two transparent overlay quads in front of every screen (built after `BuildScreenSurfaces`, from each surface's centre/normal/size): scanlines (every other row darkened, `scanlineStrength` 0.35) + vignette, and white noise (`noiseStrength` 0.12) whose texture offset jumps every frame.
  - Alarm: while `alarm` (the game sets it: incident started and Live) the front-wall beacon (`M_Room_Alarm`) glows and pulses (1.2 Hz) and a red spot light next to it sweeps the room (220°/s, room layer only).
  - Sounds made in code: a looping monitor hum (50/100/150 Hz + a little noise, `humVolume` 0.08) all the time in the room; a two-tone siren (880/660 Hz, `alarmVolume` 0.18) fading in while the alarm is on; the radio static is in `CrushHourGame.Radio.cs`.
  - Performance: CCTV and minimap cameras render without shadows, post-processing or anti-aliasing (`CheapCamera`); the wall camera keeps shadows. `AdaptQuality`: if the smoothed frame time stays above `slowFrameTime` (1/40 s) for 2 s, it steps down (desk feeds 12 → 8 → 5 fps, wall 30 → 24 → 15, minimap 10 → 6 → 4, feeds per frame 3 → 2 → 1) and back up after 8 s below 70 % of it; each change is logged. `adaptiveQuality` turns it off. Not measured yet with 2,000 people: check with the Stats/Profiler window on Hard.
- `TopDownCamera.cs`: city-builder camera (WASD or arrow keys to pan, scroll to zoom, Q/E to rotate, middle-drag to pan). `arrowKeysPan` (not serialized) turns the arrow keys off; the game does that while a zone patrol is selected.

### Cube world (`Assets/Scripts/`)

- `CubeTerrain.cs`: an `[ExecuteAlways]` Perlin voxel terrain, chunked meshes with MeshColliders. The generated chunks use `HideFlags.DontSave`, so they never get saved into the scene. In edit mode, generation is deferred via `EditorApplication.delayCall` because hierarchy changes during activation are not allowed.
- `FirstPersonController.cs`: CharacterController-based player. WASD to move, mouse to look, Space to jump, Shift to sprint; also supports a gamepad.

### Editor setup (`Assets/Scripts/Editor/`)

- `CrushHourSetup.cs`:
  - **Tools > Crush Hour > Create Festival Scene** creates the materials in `Assets/CrushHour/`, then the scene with the game object (shader assigned) and camera, and adds the scene to the build.
  - **Tools > Crush Hour > Use Person Models** takes the prefabs or folders selected in the Project window. With nothing selected, it searches folders whose names contain "lowpolypeople" or "jalbert". It:
    - prefers real prefabs over FBX models (Unity's `t:Prefab` search also matches models);
    - turns on Read/Write for their meshes;
    - collects idle, walk, run and wave clips from the prefab folder and the folder above it;
    - assigns `personPrefabs`, `crowdClips` and `crowdAnimationShader`, then saves the scene.
  - Automatic runs, once each (flags in `EditorPrefs`): scene creation (`CrushHourSetup.Done:<dataPath>`), and an upgrade that applies the above to a scene that already had person prefabs but no clips (`CrushHourSetup.AnimatedCrowd:<dataPath>`).
- `CrushHourControlRoomSetup.cs`: **Tools > Crush Hour > Create Control Room Scene** copies `CrushHourFestival.unity` to `CrushHourControlRoom.unity` (same game settings, person models and materials), removes the `TopDownCamera` from the camera and the SampleScene leftovers (root objects with `FirstPersonController` or `CubeTerrain`, i.e. "Player" and "Cube Terrain"), sets `crowdSizeOverride` to 0, adds a "Control Room" object with `ControlRoom` and its models from `Assets/Models/ControlRoom` (`litMaterial` = the game's), saves, and puts the scene second in Build Settings (dropping entries whose scene file no longer exists). Menu only (no automatic run). Rerun it after changing the festival scene.
- `CubeWorldSetup.cs`: **Tools > Cube World > Build Scene** (once-only flag `CubeWorldSetup.Done:<dataPath>`).

## Conventions and gotchas

- **Control room models** live in `Assets/Models/ControlRoom` (OBJ + `ControlRoom.mtl` / `ControlDesk.mtl`; the Blender working copies are in `D:\CrushHour_Blender` on the dev machine). Units are metres, Y up, fronts face +Z. Every object has both an `o` and a `g` line so Unity keeps them as separate children (`Monitor_01`…`Monitor_09`, `Minimap_Screen`, `Switch_3_Cover`, `CamButton_7`, `CeilingLamp_4`…). Moving parts also exist as separate files with the pivot at the origin (`ControlBoard_SwitchLever`, `_SwitchCover`, `_Lamp`, `_CamButton`, `_AutoButton`, `_StartButton`, `WalkieTalkie_PTT`); `ControlBoard_Layout.json` in the Blender folder lists where each goes on the board. Every screen has its own material (`M_VW_Screen`, `M_Monitor_Screen`, `M_Minimap_Screen`, `M_Board_CrewScreen`, `M_Walkie_Screen`, `M_Clock_Screen`) with UVs 0..1 over the picture, for RenderTextures. Unity mirrors X on OBJ import (see `ControlRoom.cs`).

- Runtime-created materials clone `litMaterial` and `unlitMaterial` so the URP shaders get included in builds. Code falls back to `Shader.Find` when they're missing. Instanced materials need `enableInstancing = true`.
- In IMGUI, never change layout-affecting state mid-`OnGUI` (start, restart, welcome card, legend). Queue those with `deferred.Add(...)`, which runs in `Update`.
- The ground is one `Quad` rotated (90,0,0). Texture pixel `y*Width+x` maps to cell (x, y), and world z = grid y.
- Barrier lines are drawn 4-connected so agents can't slip through diagonal gaps.
- Door fields must not walk through the outside band, or a door's route could leak through another door: `CanStep` only allows the door's own `beyond` cells (`isSource`).
- Materials for custom shaders: `material.mainTexture` / `material.color` only reach `_BaseMap` / `_BaseColor` if the shader marks them `[MainTexture]` / `[MainColor]`. Without that, the VAT crowd rendered white. `CopyLook` sets the properties by name.
- The "stuck" test compares speed with walking pace, not with the density-slowed target speed. The latter never registers as stuck in a packed queue.

## Verifying without the editor

Unity holds the project lock while it's open, so batch mode isn't available. Instead, run the script, which does steps 1 and 2 below (it works with Unity open):

    powershell -ExecutionPolicy Bypass -File "Tools~\Verify\verify.ps1"                # compile + baseline (a few minutes)
    powershell -ExecutionPolicy Bypass -File "Tools~\Verify\verify.ps1" -SkipBaseline  # compile only

If Windows blocks the baseline program ("An Application Control policy has blocked this file", e.g. Smart App Control on the dev machine), the compile check still works; run the baseline in Unity with **Tools > Crush Hour > Run Balance Baseline** instead. It runs on a background thread (progress and Cancel in the bottom-right task bar), logs each run to the Console and writes the same `Tools~/Verify/baseline.md`. Don't enter Play mode while it runs (a domain reload kills it).

**Tools > Crush Hour > Run Zone Order Test** runs `BalanceBaselineRunner.RunZoneOrderTest` the same way and writes `Tools~/Verify/zone-order-test.md` (see "Zone order test" below).

`Tools~/` is ignored by Unity (trailing `~`). The script reads references and defines from `Assembly-CSharp-Editor.csproj` (or, if missing, `Assembly-CSharp.csproj`, skipping `Editor/` folders), adds package assemblies from `Library/ScriptAssemblies`, builds in `%TEMP%\CrushHourVerify`, and writes the baseline table to `Tools~/Verify/baseline.md`. The runs live in `Assets/Scripts/Editor/BalanceBaselineRunner.cs` (pure simulation, no Unity API; the menu is `BalanceBaselineMenu.cs`, and `Tools~/Verify/Baseline.cs` is only the standalone `Main`). They use the code defaults of `CrowdSettings`, not the scene's Inspector values, and mirror `CrushHourGame.Simulate` for three responses (no staff and gates shut; staff at their posts and gates shut; all gates opened when the fire starts plus staff), three fire sites and the given seeds.


1. **Compile check.** Use Unity's bundled Roslyn with every `HintPath` reference from `Assembly-CSharp-Editor.csproj`. Exclude `Assembly-CSharp.dll` and pass `-nostdlib -langversion:9.0 -define:UNITY_EDITOR`.
   - compiler: `"C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Data\DotNetSdk\dotnet.exe" ...\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll @refs.rsp <files>`
2. **Headless balance runs.** `FestivalVenue` and `CrowdSimulation` only use the managed parts of UnityEngine, so they run outside Unity:
   - Compile them with a small `Main` harness as `-target:exe`, using the same references.
   - Copy `UnityEngine.CoreModule.dll` next to the output and add a `net8.0` `runtimeconfig.json`.
   - Run it with the bundled `dotnet.exe`.
   - Per step, call `venue.UpdateGates`, `schedule.Update` (which spreads the fire), `venue.UpdateDoorFields(3)` and `sim.Step(dt, venue, true)`, then the unit effects. `BalanceBaselineRunner.Scenario` starts its fire with `schedule.StartNow(FireIncident.At(...))`.
   - The harness is `BalanceBaselineRunner` (see above).
3. The VAT shader and `PersonModel.Build` need the editor. Check `Logs/Editor.log` for shader errors and for the "Crush Hour: N person models loaded, M animated" line printed on Play.

## Balance baseline

### With drunk people (step 4)

**Not measured yet**: run Tools > Crush Hour > Run Balance Baseline and paste `Tools~/Verify/baseline.md` here. The table now has a "Drunk left inside" column, and "Still inside" counts sober people only.

### Settings version 5 (zone patrols; seeds 11, 21 and 33, 2,000 people)

Measured with **Tools > Crush Hour > Run Balance Baseline** (the same table is in `Tools~/Verify/baseline.md`). "Staff" = 9 zone patrolmen at their posts (one per region) and 2 megaphone police. "Gates + staff" = all 6 emergency gates opened the moment the fire starts. The first row of each fire site has no staff and no gates, so it isn't the same as the old "No action" rows below (those may have included other differences in the old harness).

| Fire at | Response | Injured | Panic attacks | Still inside | Time |
|---|---|---|---|---|---|
| Food Court | No staff, gates shut | 8–68 | 3–109 | 0–2 | 121–148s |
| Food Court | Staff at posts, gates shut | 0–48 | 0 | 1–4 | 123–159s |
| Food Court | Gates + staff | 0–1 | 0 | 0 | 61–94s |
| Stage Left | No staff, gates shut | 21–224 | 4–106 | 0–15 | 137–139s |
| Stage Left | Staff at posts, gates shut | 0–1 | 0 | 0 | 107–124s |
| Stage Left | Gates + staff | 0–1 | 0 | 0 | 70–78s |
| Bar Tent | No staff, gates shut | 2–568 | 0–95 | 19–45 | 94–98s |
| Bar Tent | Staff at posts, gates shut | 0–1 | 0–1 | 0 | 97–117s |
| Bar Tent | Gates + staff | 0–1 | 0 | 0 | 65–71s |

What changed against version 4:
- "Gates + staff" is as good as before (0–1 injured, 61–94 s against 68–88 s), so 9 patrolmen in place of 4 stewards don't change what a good response looks like.
- Staff at their posts with the gates still shut is now enough to keep Stage Left and the Bar Tent nearly injury-free (0–1 injured), where the old "No action" rows had 200–560 injured. The extra patrols help a lot even before the player does anything, so the game is **easier at the start** than before. Food Court is the exception (0–48 injured, 1–4 still inside).
- Without staff and gates, the Bar Tent still has 19–45 people stuck inside and up to 568 injured on one seed.
- Open question for tuning: with a patrol in every zone, the player's choices (which gates, where to send people) matter less. Zone evacuation orders (next step) and the drunk crowd should give them something to do.

### Settings version 4 (before zone patrols; headless, 2,000 people, seeds 11, 21 and 33)

"Staff" = 4 stewards and 2 megaphone police at their default posts. The fire reaches its 400-cell cap with ember spot fires, and the smoke drifts east.

| Fire at | Response | Injured | Panic attacks | Still inside | Time |
|---|---|---|---|---|---|
| Food Court | No action | 1–270 (one seed crushes at the entrances, two don't) | 2–156 | 0–13 | 124s – ran out |
| Food Court | Gates + staff | 0–1 | 0 | 0 | 68–88s |
| Stage Left | No action | 200–375 (crushes) | 206–277 | ~15 | ran out (240s) |
| Stage Left | Gates + staff | 0–4 | 0 | 0 | ~72s |
| Bar Tent | No action | 390–560 | 220–400 | ~20 | ran out (240s) |
| Bar Tent | Gates + staff | 0–2 | 0 | 0 | 63–85s |

Up to about 40 people are standing still listening to a steward at any one time.

Steward queues (measured before the PA was removed: food court fire, PA + staff, gates closed, seeds 11/21/33/47): inside the stewards' rings, cell-steps above the crush threshold fell from 31–200 to 2–16. Total injuries swing either way between seeds, because crushes move to jams outside the rings, so stewards have to be placed at the bottlenecks.

Rerun these after changing crowd settings, entrance widths, fire, staff effects, or the panic, pressure and injury logic. Each step takes about 1.2–2.5 ms on .NET 8. It is likely slower in Unity's Mono runtime; the game's top speed is 2×.

## Zone order test

Fire at the Food Court, staff at their posts, only East Gate A open (from the moment the fire starts). 5 s in, the Main Stage patrol orders its zone "towards East Gate A"; the control run gives no order. Run with **Tools > Crush Hour > Run Zone Order Test** (also saved to `Tools~/Verify/zone-order-test.md`).

| Seed | Order | Door chosen | People in zone | Zone people out via East Gate A | Everyone out via East Gate A | Safe | Injured | Still inside | Time |
|---|---|---|---|---|---|---|---|---|---|
| 11 | no | - | 1250 | 635 | 897 | 1988 | 5 | 7 | 122s |
| 11 | yes | East Gate A | 1250 | 1245 | 1505 | 1994 | 0 | 6 | 113s |
| 21 | no | - | 1184 | 628 | 904 | 1998 | 2 | 0 | 80s |
| 21 | yes | East Gate A | 1184 | 1170 | 1428 | 1984 | 14 | 2 | 118s |
| 33 | no | - | 1250 | 651 | 904 | 1913 | 81 | 6 | 117s |
| 33 | yes | East Gate A | 1250 | 1243 | 1492 | 1993 | 0 | 7 | 114s |

What it shows:
- The order works: the door chosen is East Gate A every time, and the share of the zone leaving through it goes from about 51–55% to 99%. In total, East Gate A takes about 1,430–1,505 people instead of about 900.
- It's a real decision, not a free win. Seeds 11 and 33 get better (5 → 0 and 81 → 0 injured). Seed 21 gets worse: 2 → 14 injured and 80 s → 118 s, because sending a whole 1,200-person zone through one gate overloads it, where without the order the crowd split between East Gate A and the Main Entrance.
- So the zone order UI should show where the order sends people, and splitting a big zone between exits (or ordering two zones to different gates) should pay off.

## Difficulty test

One random round (`RoundGenerator`) per difficulty × seed (9 runs), crowd size and drunk share from the difficulty. As in the game, the alarm and the evacuation clock start at the first fire or collapse, or when a gas leak is noticed (patrol/police ring or a cough); staff stand at their posts; every gate is opened at that moment. Reports each incident with its start time (gas: also when it was noticed; chained collapses only if they happened), blocked doors, injuries by cause, panic attacks, safe, still inside, drunk left inside, evacuation time and score. `Simulate` gained an optional `live` callback (null = alarm from the start, as before) and `Scenario.clockStart`, so the fire tests and the baseline are unchanged. Run with **Tools > Crush Hour > Run Difficulty Test** (saved to `Tools~/Verify/difficulty-test.md`). Not run yet.

## Gas leak test

Gas leak at the Generator from 0 s, no fire, staff at their posts, gates shut. Detected (as in the game) when a patrol or police ring has gas ≥ 0.05, or someone coughs. Per seed: no reaction, or the leak zone's patrol orders its zone out, away from the leak, at the moment of detection. Reports detection time and by whom, the door ordered, people in the zone then, injured by gas, injured total, safe, still inside and time. Note: the headless loop always runs the crowd with `incident = true`, so the band stops after 15 s and some people leave anyway; in the game the evacuation only starts at detection. Run with **Tools > Crush Hour > Run Gas Leak Test** (saved to `Tools~/Verify/gas-leak-test.md`). Not run yet.

## Tower collapse test

Staff at their posts, every gate opened at 0 s. The west light tower sways from 0 s and falls north into the stage crowd at 6 s, alone or together with a fire at Stage Left at 0 s; third case: only the Stage Left fire, with both towers chained to it. Reports when each tower fell, crushed by collapse, people shaken, injured (crush / fire / smoke), panic attacks, safe, still inside and time. Run with **Tools > Crush Hour > Run Tower Collapse Test** (saved to `Tools~/Verify/tower-collapse-test.md`). Not run yet.

## Blocked exit test

Fire at the Food Court, staff at their posts, gates shut (the two entrances are the only way out). Main Entrance blocked by a delivery truck at 0 s, never cleared, or cleared by the Main Entrance zone patrol starting at 20 s (it is put at the door at 20 s, works alone for 8 s with its ring off, then stays there); plus an unblocked control. Reports cleared time, safe, injured, still inside, drunk left inside, time and exits per door. Run with **Tools > Crush Hour > Run Blocked Exit Test** (saved to `Tools~/Verify/blocked-exit-test.md`). Not run yet.

## Status / next ideas

- Week 4 (branch `Sapt-4`):
  - Step 1: regions. The layout was checked against a Python replica (every interior cell assigned, 9 contiguous zones).
  - Step 2: a patrolman per zone replaces the stewards; `Tools~/Verify/verify.ps1` added.
  - Step 3a: `OrderRegionEvacuation` and `orderedDoor` in the simulation, plus per-door exit counts and the zone order test.
  - Step 3b: zone order UI (panel, arrow keys, right-click to turn, ground arrow, feed message, 5 s cooldown per zone). Not play-tested in the editor yet.
  - Step 4: drunk people (simulation, display, legend, results, round end on sober people). Baseline with drunks: see "Balance baseline". Not play-tested yet. A first test run at drunkFraction 0.05 left about 130 drunk people inside.
  - Steps 1 and 2 pass the compile check (`verify.ps1`). The version 5 baseline is in "Balance baseline" (run from the Unity menu, because Windows Application Control blocks the standalone program on the dev machine).
  - Step 5: patrols and police escort drunk people out (click them with a unit selected); drunkFraction lowered to 0.02 (settings version 6). Not play-tested yet.
  - Week 5, step 1: incident system (`Incidents.cs`); fire is now a `FireIncident` in an `IncidentSchedule`. Acceptance test: **Tools > Crush Hour > Run Incident Refactor Check** runs every baseline scenario through the old fire path (`Scenario.legacyFire`: `StartFire` + `UpdateFire` directly) and through the schedule, and must report PASS (all identical). It writes `Tools~/Verify/incident-refactor-check.md`. Once it passes, `legacyFire` and the check can be deleted.
  - Week 5, step 2: blocked exits (`BlockDoorIncident`, clearing by patrol/police, blocker visuals, hover, blocked exit test). Not compiled, run or play-tested yet.
  - Week 5, step 3: light tower collapse (`TowerCollapseIncident`, sway warning, wreckage, crush and fright, chained to the fire, door blocking through step 2, results row, tower collapse test). Not compiled, run or play-tested yet.
  - Week 5, step 4: gas leak (`GasLeakIncident`, `gas[]` and `gasDose[]`, detection by cough or patrol/police, yellow-green layer, ignition into a fire, gas leak test). Not compiled, run or play-tested yet.
  - Week 5, step 5: difficulties instead of fixed levels (`Difficulty.cs`: `DifficultySettings`, `RoundGenerator`; difficulty buttons and F1/F2/F3, PlayerPrefs, top bar and Results, multiplied score; `demoBlockedExit`/`demoGasLeak` removed; difficulty test). Not compiled, run or play-tested yet.
  - Week 5, step 6: start menu with map (scene) selection and difficulty, no locks; the difficulty buttons moved off the welcome card; `CrushHourFestival_Dev` added to Build Settings (first). Not compiled or play-tested yet.
  - Possible next: more maps in `Maps` (each a scene in Build Settings), best stars/score per map and difficulty, a pause menu.
- Week 6 (branch `saptamana-6`): first-person control room.
  - Step 0: models (room, video wall, desk with 9 monitors and the minimap, control board, walkie-talkie, chair, CCTV camera) in `Assets/Models/ControlRoom`.
  - Step 1: `ControlRoom.cs`, the control room scene (Tools > Crush Hour > Create Control Room Scene), PLAY loads it for every map, seated mouse look with crosshair and right-mouse zoom, top-down camera and map clicks off there, HUD reduced to the top strip, banner and cards. Not compiled or play-tested yet.
  - Step 2: CCTV camera per zone on the desk monitors with captions and alarm frames (`ControlRoom.Cctv.cs`, `CrushHourGame.ControlRoom.cs`). Not compiled or play-tested yet.
  - Step 3: video wall with AUTO/manual zone, switch effect and warnings; minimap with markers; CCTV cameras closer to their zone; video wall bottom edge at 1.6 m (`ControlRoom.Wall.cs`). Not compiled or play-tested yet.
  - Step 4: clicks, barriers and hover through the screens; E = focus on a screen (`ControlRoom.Screens.cs`, `Editor/ControlRoomModelImport.cs`). Not compiled or play-tested yet.
  - Fixes after the first play: CCTV cameras on poles closer to their zones; WASD moves the chair. Not compiled or play-tested yet.
  - Step 5: the exits board: switches with covers and lamps per exit, crew display, wall camera buttons + AUTO, START (`ControlRoom.Board.cs`). Not compiled or play-tested yet.
  - Step 6: radio wheels on V with radio replies and static, walkie-talkie in hand (`CrushHourGame.Radio.cs`). The scene tool now also assigns `walkieModel` (rerun it, or set the field by hand). Not compiled or play-tested yet.
  - Step 7: incident log and team screens on the desk, wall clock, top strip on U (`ControlRoom.Info.cs`). Not compiled or play-tested yet.
  - Step 8: CCTV overlays, alarm beacon/light/siren, monitor hum, cheaper CCTV cameras and adaptive refresh rates (`ControlRoom.Atmosphere.cs`). Not compiled or play-tested yet; performance not measured.
- Balance pass (branch `balance-pass`), after a headless bot playtest (216 rounds: doing nothing earned ~2 stars, no crush injuries at all, "open all gates" the only decision, 3 stars impossible above Easy):
  - Fixes: the stall rule (`EvacuationProgress`), and `Mathf.PerlinNoise` replaced so full rounds run headless.
  - Changes: fewer patrols (placed at the busiest zones, zone follows the patrol), gate crews, `injuryRate` 0.08 → 0.12 (settings version 7) and bigger crowds, new star rules, group escorts, first disaster at 20–40 s with warning signs, exit throughput on the HUD. Menu difficulty cards show patrols and gate crews, and `StarSummary`.
  - Bot result (3 maps × 6 seeds per row; stars avg): Easy passive 1.39 / random gates 1.89 / good gates 1.94 / active 2.67 (12 of 18 three-star); Medium 1.28 / 1.72 / 1.94 / 2.17 (5 three-star); Hard 0.94 (98 injured avg, catastrophic crushes) / 1.67 / 1.56 / 1.61 (no three-star yet). Good play beats doing nothing everywhere now.
  - Found: blanket zone orders (every zone at once) cause big crushes at the gates even when spread over exits: orders are a strong tool that can backfire. Worth play-testing; a softer order (people leave in waves) may be needed.
  - Not play-tested in Unity yet.
- The crowd simulation, UI and editor scripts compile and the simulation is tuned headless. The VAT shader and baking still need checking inside the editor (see "Verifying without the editor", step 3).
- Patrolmen and police are still drawn as cubes. They could use person models with a tinted material.
- The crowd casts no shadows in VAT mode (the shader has no ShadowCaster pass).
- Ideas from the concept not built yet: more venues (stadium derby, metro rush hour) as new layouts in `FestivalVenue`; other incident types (blocked exits, tower collapse and gas leak are in).
