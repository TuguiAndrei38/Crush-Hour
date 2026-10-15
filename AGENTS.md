# AGENTS.md

Notes for coding agents working in this Unity project.

## Project

- Unity **6000.6.6f1**, **URP 17**, **Input System only** (`activeInputHandler: 1`, so the legacy `Input` class throws). Read devices directly (`Keyboard.current`, `Mouse.current`, `Gamepad.current`).
- No git repo, no asmdefs: runtime scripts compile into `Assembly-CSharp`, `Editor/` folders into `Assembly-CSharp-Editor`.
- **Crush Hour** concept: you are the safety commander at a big event. When disaster hits, you guide the crowd out with barriers, emergency gates, stewards and PA announcements. Panic spreads, doorways jam and dense pockets crush. You're scored on people out safely and time.

## Scenes

| Scene | What it is |
|---|---|
| `Assets/Scenes/CrushHourFestival.unity` | **Main prototype.** Top-down crowd-safety sim at a music festival, built from cubes. First in Build Settings. |
| `Assets/Scenes/SampleScene.unity` | Earlier experiment: a voxel cube terrain plus a first-person controller. |

Both scenes are set up by editor scripts (see below), not by hand.

## Code map

### Crush Hour (`Assets/Scripts/CrushHour/`, namespace `CrushHour`)

- `FestivalVenue.cs`: the grid model, at 1 cell = 1 m² (104×76).
  - Layout: fence, outside safe zone, two known entrances, six emergency `Gate`s, landmarks, fire sites and crowd spawn zones.
  - Flow fields: a Dijkstra search from the safe zone. Cells near the fire cost more, and so do crowded cells when density is passed in.
  - Also handles fire spread, a fire-distance search, gate animation and steward pathfinding.
- `CrowdSimulation.cs`: about 2,000 agents stored as parallel arrays.
  - Movement: a per-cell spatial hash each step, soft-body separation, sliding along walls, and walking speed that drops as density rises.
  - Behaviour: panic (from fire, contagion, density and being stuck), whether the person is evacuating, and whether they've been informed of all exits.
  - Pressure is density × panic factor. Crush damage builds when it stays above the threshold.
  - Agents are drawn as instanced cubes with `Graphics.RenderMeshInstanced`, using five colour buckets.
- `CrushHourGame.cs`: a single MonoBehaviour that builds the whole scene at runtime.
  - Round phases: Planning → Live (fire after a random delay) → Results.
  - Tools: command (stewards and gates) and barriers. Also the PA announcements, scoring and stars, and the IMGUI interface.
  - The simulation runs on a fixed 0.04 s step, at most 6 steps per frame.
- `TopDownCamera.cs`: city-builder camera (WASD to pan, scroll to zoom, Q/E to rotate, middle-drag to pan).

**Key rule:** uninformed people use the `knownField`, which treats emergency gates as walls. Informed people with panic below 0.8 use the `allField`, which includes open gates and avoids congestion. So panicked people forget directions and head back to the way they came in.

### Cube world (`Assets/Scripts/`)

- `CubeTerrain.cs`: an `[ExecuteAlways]` Perlin voxel terrain, chunked meshes with MeshColliders. The generated chunks use `HideFlags.DontSave`, so they never get saved into the scene. In edit mode, generation is deferred via `EditorApplication.delayCall` because hierarchy changes during activation are not allowed.
- `FirstPersonController.cs`: CharacterController-based player. WASD to move, mouse to look, Space to jump, Shift to sprint; also supports a gamepad.

### Editor setup (`Assets/Scripts/Editor/`)

- `CrushHourSetup.cs`: **Tools > Crush Hour > Create Festival Scene**. It creates `Assets/CrushHour/CrushHour_Lit.mat` (instancing on) and `CrushHour_Unlit.mat`, creates the scene with the game object and camera, and adds the scene to the build.
- `CubeWorldSetup.cs`: **Tools > Cube World > Build Scene**. Creates the materials and block texture in `Assets/CubeWorld/`, the terrain and the player.
- Both scripts also run automatically **once** after the first compile. The once-only flags are stored in `EditorPrefs` (`CrushHourSetup.Done:<dataPath>` and `CubeWorldSetup.Done:<dataPath>`). Delete a key to make that setup run again.

## Conventions and gotchas

- Runtime-created materials clone `litMaterial` and `unlitMaterial` so the URP shaders get included in builds. Code falls back to `Shader.Find` when they're missing. Instanced materials need `enableInstancing = true`.
- In IMGUI, never change layout-affecting state mid-`OnGUI`. Queue those actions with `deferred.Add(...)`, which runs in `Update`, as is already done for Restart and Start.
- The ground is one `Quad` rotated (90,0,0). Texture pixel `y*Width+x` maps to cell (x, y), and world z = grid y.
- Barrier lines are drawn 4-connected so agents can't slip through diagonal gaps.
- Fallen (injured) agents push only weakly (0.15), so others can step over them. Treating them as solid walls trapped panicked agents forever.

## Verifying without the editor

Unity holds the project lock while it's open, so batch mode isn't available. Instead:

1. **Compile check.** Use Unity's bundled Roslyn with every `HintPath` reference from `Assembly-CSharp-Editor.csproj`. Exclude `Assembly-CSharp.dll` and pass `-nostdlib -langversion:9.0 -define:UNITY_EDITOR`.
   - compiler: `"C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Data\DotNetSdk\dotnet.exe" ...\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll @refs.rsp <files>`
2. **Headless balance runs.** `FestivalVenue` and `CrowdSimulation` only use the managed parts of UnityEngine, so they run outside Unity:
   - Compile them with a small `Main` harness as `-target:exe`, using the same references.
   - Copy `UnityEngine.CoreModule.dll` next to the output and add a `net8.0` `runtimeconfig.json`.
   - Run it with the bundled `dotnet.exe`.
   - The harness used during tuning is not kept in the repo; it was a scratch file, so write a new one.

## Balance baseline (headless, 2,000 people)

| Fire at | Response | Injured | Still inside | Time |
|---|---|---|---|---|
| Food Court | No action | 0 | 1,705 | ran out (240s) |
| Food Court | PA only | ~100 (crush at West Entrance) | 0 | ~98s |
| Food Court | PA + all gates | 0 | 0 | ~88s |
| Stage Left | PA + gates | ~160 | 0 | ~73s |
| Stage Left | + stewards and "stay calm" | ~11 | 0 | ~95s |

Rerun these after changing crowd settings, entrance widths or pressure and injury logic. Each step takes about 1.5–2 ms on .NET 8. It is likely slower in Unity's Mono runtime, so 4× speed may struggle.

## Status / next ideas

- Not yet play-tested inside the Unity editor; only compiled and run headless.
- Ideas from the concept not built yet: more venues (stadium derby, metro rush hour) as new layouts in `FestivalVenue`; other incident types besides fire.
