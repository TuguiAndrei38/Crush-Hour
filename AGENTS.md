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
| `Assets/Scenes/SampleScene.unity` | Earlier experiment: a voxel cube terrain plus a first-person controller. |

Both scenes are set up by editor scripts (see below), not by hand.

## Code map

### Crush Hour (`Assets/Scripts/CrushHour/`, namespace `CrushHour`)

- `FestivalVenue.cs`: the grid model, at 1 cell = 1 m² (104×76).
  - Layout: fence, outside safe zone, landmarks, fire sites and crowd spawn zones.
  - `Door`s: 2 narrow entrances that are always open, and 6 emergency gates (`Gate`) that start closed.
  - Each door has its own Dijkstra `field` that stays inside the fence:
    - An open door's field leads through it to its `beyond` cells in the safe zone.
    - A closed door's field leads only to its `approach` cells in front of it, which is where people discover it's shut.
    - Cells near the fire cost more.
  - Door fields are marked dirty by barrier, gate and fire changes. `UpdateDoorFields(max)` recomputes a few per step, to avoid frame spikes.
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
  - Unit effects: `ApplyStewards` and `PoliceAnnouncement` (one megaphone pulse). Both use the spatial hash from the last `Step`. `ApplyStewards(positions, dt, incident, radius)` takes an optional ring radius; the game passes `patrolRadius` (6 m) for zone patrolmen, and without it the ring is `stewardRadius`.
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
  - Round phases: Planning → Live (fire after a random delay) → Results.
  - Units (`Unit`, `Role`): they're selected from the roster or the map, then sent by clicking the map.
    - **Zone patrolmen** (`Role.Patrolman`), one per region, spawned at `Region.post` with `homeRegion` set, named "<Zone> – Patrol". They replaced the 4 stewards and have the same effect (`ApplyStewards`, ring `crowd.patrolRadius`, 6 m): calm people, then send them out in orderly queues. Look: yellow body, cap in the zone colour.
    - Each patrolman has a `facing` (grid x, y, unit length), drawn as a small arrow on the ground in the zone colour. It defaults to the zone's area centre, or the centre of the grounds when the post already is the area centre. Change it with `SetFacing(unit, direction)`, which also redraws the arrow. It doesn't affect the crowd yet (it will set the direction of zone evacuation orders).
    - 2 megaphone police (`Role.Megaphone`; navy with a cyan cap): announce every 5 s within 14 m, with a ripple.
    - The roster groups them: "ZONE PATROLS" (swatch in the zone colour), then "MEGAPHONE POLICE".
  - Tools: command (units and gates) and barriers (budget 60). Also scoring (−10 per panic attack) and stars. Game speed is 1× or 2× (`Speeds`).
  - The simulation runs on a fixed 0.04 s step, at most 6 steps per frame.
  - Voxel smoke: a pool of up to 160 unlit grey cubes rising from random fire cells and drifting with `Wind`. Ember spot fires are logged.
  - **Settings version:** every Inspector value is saved in the scene and overrides code defaults. When you retune defaults, bump `CurrentSettingsVersion`; `UpgradeSettings` (run from `OnValidate` and `Awake`) then resets the team size, `maxFireCells` and `crowd` in older scenes. Version 3 fixed a scene stuck on 6 stewards, a 160-cell fire and old crowd settings. Version 4 applies the 400-cell fire and the new crowd settings. Version 5: zone patrolmen replace the stewards (`stewardCount` is gone; `stewardSpeed` became `patrolSpeed` via `FormerlySerializedAs`), and `crowd.patrolRadius` is new.
  - **UI** (IMGUI) is scaled by `Screen.height / 900` through `GUI.matrix`. All layout uses the virtual `vw`×`vh` size, and `guiRects` are in virtual units (`IsOverGui` converts).
    - Rounded panels and buttons use procedurally generated textures (`Rounded`, cached).
    - Zones view (`showRegions`, on by default, toggle with G or the "Zones [G]" button): `DrawRegionOutlines` tints 1-cell region borders on the ground texture (only the cell whose right or upper neighbour is in another region, so lines stay 1 m wide), `DrawWorldLabels` adds "<NAME> ZONE" at each region's post, and the map tooltip ends with the zone name.
    - Elements: top bar (phase chip, timer bar, speed buttons, stat cards), team roster (left), gates panel and view toggles (right), colour-coded event feed (bottom left), collapsible legend (bottom right, L), event banner, map tooltips, a welcome card while planning, and a results card.
- `TopDownCamera.cs`: city-builder camera (WASD to pan, scroll to zoom, Q/E to rotate, middle-drag to pan).

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
- `CubeWorldSetup.cs`: **Tools > Cube World > Build Scene** (once-only flag `CubeWorldSetup.Done:<dataPath>`).

## Conventions and gotchas

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

`Tools~/` is ignored by Unity (trailing `~`). The script reads references and defines from `Assembly-CSharp-Editor.csproj` (or, if missing, `Assembly-CSharp.csproj`, skipping `Editor/` folders), adds package assemblies from `Library/ScriptAssemblies`, builds in `%TEMP%\CrushHourVerify`, and writes the baseline table to `Tools~/Verify/baseline.md`. The runs live in `Assets/Scripts/Editor/BalanceBaselineRunner.cs` (pure simulation, no Unity API; the menu is `BalanceBaselineMenu.cs`, and `Tools~/Verify/Baseline.cs` is only the standalone `Main`). They use the code defaults of `CrowdSettings`, not the scene's Inspector values, and mirror `CrushHourGame.Simulate` for three responses (no staff and gates shut; staff at their posts and gates shut; all gates opened when the fire starts plus staff), three fire sites and the given seeds.


1. **Compile check.** Use Unity's bundled Roslyn with every `HintPath` reference from `Assembly-CSharp-Editor.csproj`. Exclude `Assembly-CSharp.dll` and pass `-nostdlib -langversion:9.0 -define:UNITY_EDITOR`.
   - compiler: `"C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Data\DotNetSdk\dotnet.exe" ...\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll @refs.rsp <files>`
2. **Headless balance runs.** `FestivalVenue` and `CrowdSimulation` only use the managed parts of UnityEngine, so they run outside Unity:
   - Compile them with a small `Main` harness as `-target:exe`, using the same references.
   - Copy `UnityEngine.CoreModule.dll` next to the output and add a `net8.0` `runtimeconfig.json`.
   - Run it with the bundled `dotnet.exe`.
   - Per step, call `venue.UpdateGates`, `venue.UpdateFire`, `venue.UpdateDoorFields(3)` and `sim.Step(dt, venue, true)`, then the unit effects.
   - The harness is `BalanceBaselineRunner` (see above).
3. The VAT shader and `PersonModel.Build` need the editor. Check `Logs/Editor.log` for shader errors and for the "Crush Hour: N person models loaded, M animated" line printed on Play.

## Balance baseline

**Settings version 5 (zone patrols): not measured yet.** Run **Tools > Crush Hour > Run Balance Baseline** (or `verify.ps1`) and paste `Tools~/Verify/baseline.md` here. "Staff" there = 9 zone patrolmen at their posts and 2 megaphone police.

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

## Status / next ideas

- Week 4 (branch `Sapt-4`):
  - Step 1: regions. The layout was checked against a Python replica (every interior cell assigned, 9 contiguous zones).
  - Step 2: a patrolman per zone replaces the stewards; `Tools~/Verify/verify.ps1` added.
  - Steps 1 and 2 pass the compile check (`verify.ps1`). The baseline program is blocked by Windows Application Control on the dev machine, so the version 5 baseline still has to be run from the Unity menu.
  - Next: zone evacuation orders with a direction relative to the patrolman, drunk people who need an escort, difficulty presets, a start menu.
- The crowd simulation, UI and editor scripts compile and the simulation is tuned headless. The VAT shader and baking still need checking inside the editor (see "Verifying without the editor", step 3).
- Patrolmen and police are still drawn as cubes. They could use person models with a tinted material.
- The crowd casts no shadows in VAT mode (the shader has no ShadowCaster pass).
- Ideas from the concept not built yet: more venues (stadium derby, metro rush hour) as new layouts in `FestivalVenue`; other incident types besides fire.
