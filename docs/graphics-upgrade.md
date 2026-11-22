# Optional URP renderer

`--graphics-upgrade` builds the game and its stock bundles with Unity 6.7 and the editor’s bundled Universal Render Pipeline 17.7.0. The converter installs the rendering code into the generated project and migrates materials from the local Steam import. The option is also available in the terminal wizard. To return to the stock rendering path, launch the player from your original workspace.

The renderer, shaders and menu ship as regular source under `runtime/GraphicsUpgrade`. The converter patches the camera, shader warmup and menu hooks in the privately imported game assemblies. Those assemblies and the game assets stay in the local workspace.

```sh
dotnet run --project src/H3VR.Converter -c Release -- --graphics-upgrade
```

The default graphics-upgrade workspace is `.local/workspace-graphics-upgrade`. Supply `--workspace` to use another private directory. If you already have a verified extraction from the same converter inputs, add `--reuse-extraction /path/to/workspace`. Add `--compact-player-data` to pack the player data with chunked LZ4. On macOS, launch the generated app through the MacVR launcher. [macOS launch instructions](platform-integrations.md)

## One macOS app

Combine three signed local builds of the same Steam and Unity versions. Base is the original player, Built-in is the updated stock renderer, and URP uses the graphics upgrade. Each mode keeps its Unity data and mod receipt. On APFS the package can share disk blocks for identical files.

```sh
dotnet run --project tools/UnityMetalCli -c Release -- combine \
  --base "$BASE" --builtin "$BUILTIN" --urp "$URP" \
  --share-assets --output "$PWD/.local/play-combined/NativeH3VR.app"
```

`--share-assets` also shares identical content inside Unity 6.7 player archives and asset bundles on APFS. The repacker verifies the decoded bytes and archive directory against each original and signs the result. Each renderer's archive has its own view of the shared data, so Finder's apparent file size can exceed the space allocated on disk.

Put the absolute MacVR export path in `macvr-runtime.txt` next to the app. Open the app and choose a renderer. The picker starts on URP and remembers your selection. Once in URP, use **Options → Graphics Options → Preset** to change between Balanced and Quality while the game is running. Choose Base or Built-in when you next open the app.

For a scripted launch, add `--renderer base`, `--renderer builtin` or `--renderer urp` to `UnityMetalCli run`. The command defaults to Base for existing tools and Base mods. For tools that read a specific renderer's build receipt or managed assemblies, the original layouts are under `Contents/Helpers/Base.bundle`, `Contents/Helpers/BuiltIn.bundle`, and `Contents/Helpers/URP.bundle`. A mod is still tied to the receipt for its renderer.

## Graphics Options

Open **Graphics Options** from the game’s options panel. **Balanced** is the initial preset. The first page controls resolution, sampling, lighting and shadows. **Effects** opens bloom, sharpening, exposure and tone mapping. **Game Settings** opens the stock controls for game quality, scene color grading, texture filtering, particle collisions and scope MSAA. Changes take effect immediately and persist across launches.

The launcher saves the graphics choices in `graphics-options.json` inside its `--state-dir`. Each new state directory starts with Balanced. Direct player launches use Unity’s player preferences.

| Setting | Performance | Balanced | Quality |
| --- | --- | --- | --- |
| Render scale | 85% | 100% | 100% |
| MSAA | Off | 2× | 4× |
| Shadow distance | 15 m | 30 m | 60 m |
| Main shadow atlas / cascades | 512 / 1 | 1024 / 2 | 2048 / 4 |
| Additional lights per object | 2 | 4 | 8 |
| HDR rendering | Off | On | On |
| Bloom | Off | Subtle | Full |
| Bloom radius | Tight | Standard | Wide |
| Sharpening | Off | Low | Low |
| Specular filtering | Off | Low | High |
| Exposure compensation | 0 EV | 0 EV | 0 EV |
| Tone mapping | Off | Game | Game |
| Ambient occlusion | Off | Off | On |

Render scale ranges from 70% to 130% of the output dimensions. On MacVR the compositor continues to receive the headset’s requested eye size and projection. HDR controls the scene color buffer. Bloom, tone mapping and ambient occlusion can each be disabled independently. Exposure ranges from −1 to +1 EV in quarter-stop steps. Saves from before the exposure setting use 0 EV until you change exposure or select a preset.

**Game** follows the active scene's authored color grading and the stock color-grading switch. Northest Dakota uses Neutral with +0.8 EV. Other scenes have their own curves, exposure and color adjustments. Exposure adds compensation to the selected look. Selecting **Neutral**, **ACES** or **Off** overrides the scene grade. Existing custom choices retain their exposure and tone curve. A saved preset uses its updated defaults.

## Rendering

The renderer uses URP Forward with SRP batching. The material conversion preserves the stock asset identities used by scenes, prefabs and stock bundle addresses. Common Alloy surfaces use an authored URP PBR shader for their original packed metal, occlusion, specularity and roughness channels. Specialized effects retain recovered shader programs. Friendly45's ground and Northest Dakota's snow keep their original vertex-blended surface layers and receive the URP main light's shadows. The generated project records material-family counts and changed assets in `ProjectSettings/NativeH3VRGraphicsMaterials.json`.

Supported Alloy detail layers retain their original textures, mask, normal strength and UV set. In Warehouse this lets the floor and the building use URP lighting while keeping their different detail mappings. The original window cutouts cast the skylight pattern onto the floor.

Bloom extracts highlights at quarter width and height and filters them at that resolution. The full-resolution pass samples the filtered glow once and combines it with bounded sharpening, exposure and the selected scene grade or tone curve. Bloom keeps the hue of bright HDR sources as it limits their intensity. The three radii control the spread independently of the strength. With bloom off, the renderer skips its buffers and filtering. With all player-view effects off at 0 EV, it skips composition as well. The near-field ambient occlusion option reconstructs normals from depth and runs at half resolution. These effects operate on player views and bypass offscreen scope, reflection and preview renders.

Specular filtering reduces flicker on narrow highlights by increasing the specular roughness where neighboring shading normals change rapidly. On the stock P210 barrel, a moving glancing highlight had a temporal luminance error of 18.07 RMS with filtering off, 16.75 on Low and 15.47 on High against a 3× supersampled reference. The matte control retained identical pixels. The flat Hold cover showed no measurable change in that test.

macOS renders both tracked views through URP render requests. When HDR or MSAA needs a separate scene texture, the bridge reuses one allocation for consecutive eyes and resolves into two persistent compositor textures. The stock reflection-probe culler checks each authored probe sphere against both tracked eye views in the macOS built-in and URP players. Windows uses Unity’s OpenXR rendering path.

Stock scope and spectator cameras keep their per-camera culling and cleanup callbacks under URP, including nested renders. The player’s stereo camera dispatches stock controller input once per frame. The game’s procedural trails and temporary view feedback have their own render passes. The renderer skips those passes when no effect is active.

The paper targets keep their original paint, hit and tear commands in an offscreen texture. Vertex-lit surfaces and smoke receive the scene lights and main-light shadows. Both renderers decode legacy normal maps in either red/green or alpha/green packing and retain the eight runtime scope postprocess passes. The retail comparison includes material charts, offscreen pass readbacks and stereo captures from a live Hold. [Shader parity checks](shader-parity.md)

The stock game requests the same shader collection during engine startup and when its game manager awakens. Both converted renderers queue that collection and warm at most one variant per frame while the game has paused rendering for a level transition. Work carries forward to the next transition. The URP build also filters legacy vertex-lit, forward and lightmapping-only entries from the warmup collection. On the tested Steam import this reduces it from 842 serialized entries to 155. A complete copy retains the original variants for build inclusion.

Both rendering paths reuse a pair of eye matrices after Unity copies them and defer allocation of the camera-relative material property block until a renderer writes to it. In a Unity 6.7 editor benchmark of the imported stock callback, the median with one initialized renderer fell from 1.54 to 0.54 µs. Measured heap growth fell from about 159 bytes per call to zero after warm-up.

Both pipelines also restore 12 environment reverb controls from the supported Steam mixer during conversion. The game checks any unavailable reverb control once per mixer, which eliminates repeated failed parameter writes and log entries during scene playback.

On an Apple M5 Max with synthetic MacVR, the optimized built-in player reached sustained stereo 5.66 s after Unity initialized. Two control runs with the original shader warmup took 96.28 and 101.10 s. Unity skipped zero stereo frames across six samples covering the main menu, Warehouse and the return to the menu. These timings exclude process startup before Unity initialized.

## Performance in stock scenes

These initial menu and range measurements used signed macOS players before the scene-grading and specular-filtering updates. They used 1440 × 1600 per eye and a 90 Hz target on the M5 Max, with the same simulated headset pose in each scene. The table gives median CPU time in milliseconds for rendering both eyes and scheduling submission.

| Scene | Optimized built-in | URP Performance | URP Balanced | URP Quality |
| --- | ---: | ---: | ---: | ---: |
| Main menu | 0.966 | 1.150 | 0.823 | 1.197 |
| Warehouse | 0.966 | 0.919 | 0.975 | 1.239 |
| Friendly 45 | 1.463 | 0.950 | 1.048 | 1.228 |

The twelve measurement windows contained 1,195 completed stereo frames, with zero skipped stereo pairs or missed compositor wait slots. At the Quality preset, enabling shadows darkened about 27% of the sampled Warehouse floor and 10% of the sampled Friendly 45 ground by more than eight levels of image luma in each eye. Repeating the same Balanced settings produced zero pixels above that threshold in those regions.

## Testing an imported macOS build

The test runner uses a separate preferences directory and a diagnostic mod bound to the selected build. Set `APP`, `LAUNCHER` and `MACVR` to the generated app, UnityMetalCli DLL and local MacVR runtime export. Choose fresh output directories.

```sh
python3 tests/GraphicsUpgrade/run.py --app "$APP" --launcher "$LAUNCHER" \
  --runtime "$MACVR" --out "$PWD/.local/graphics-ui-check"
python3 tests/GraphicsUpgrade/run.py --profile --scene WarehouseRange_Rebuilt --scene Friendly45_New \
  --app "$APP" --launcher "$LAUNCHER" --runtime "$MACVR" --out "$PWD/.local/graphics-profile"
uv run tests/GraphicsUpgrade/measure.py "$PWD/.local/graphics-profile"
```

The interaction check exercises the stock panel, controller ray, preset and preference restore. The profile run captures each submitted eye, switches presets at a fixed pose and records renderer settings, stereo CPU submission and compositor frame intervals. The Metal GPU counter times the Unity command buffer that owns the OpenXR submission event. Earlier command buffers can contribute additional GPU work. When the player supplies GPU frame timings, the profiler also saves Unity’s independent GPU duration for each engine frame. This includes engine updates between stereo submissions. The analyzer reports both distributions separately and leaves unavailable counters blank.

## Take & Hold comparison

The profiler can enter Classic Hallways (`Classic`), Northest Dakota (`NorthestDakota`) and Institution (`Institution`) through the stock lobby. Each session measures the opening Take in the starting supply area and the first Hold during an active hacking wave. Stock route sequences 0, 1 and 3 give three different starting areas and first objectives on each map.

Use one renderer per fresh session. The first session records the chosen stock Hold marker and headset heading. Pass that state to the other two sessions for the same map and sequence. For example, to measure Classic Hallways on sequence 0, set `BUILTIN` and `URP` to the two generated apps, and `CAPTURES` to the output root.

```sh
python3 tests/GraphicsUpgrade/run.py --profile --tnh Classic --tnh-seed 0 --builtin \
  --app "$BUILTIN" --launcher "$LAUNCHER" --runtime "$MACVR" --out "$CAPTURES/r01/Classic-stock"
observer="$CAPTURES/r01/Classic-stock/TakeAndHoldClassic-hold-stock-state.json"
for preset in balanced quality; do
  python3 tests/GraphicsUpgrade/run.py --profile --tnh Classic --tnh-seed 0 --preset "$preset" \
    --tnh-observer-from "$observer" --app "$URP" --launcher "$LAUNCHER" --runtime "$MACVR" \
    --out "$CAPTURES/r01/Classic-$preset"
done
```

Store sequences 1 and 3 in `r02` and `r03`. Name the session directories `<map ID>-stock`, `<map ID>-balanced` and `<map ID>-quality`. The scene names used in the state filenames are `TakeAndHoldClassic`, `TakeAndHold_WinterWasteland` and `Institution`.

```sh
uv run tests/GraphicsUpgrade/compare_tnh.py "$CAPTURES" "$CAPTURES/report"
```

When the two phases come from different sessions, create both a `take` and a `hold` subdirectory or symlink inside the map and renderer directory. Point each one at the full original session. The analyzer keeps each phase on its own Unity and compositor clocks.

The analysis checks the active renderer, gameplay phase, objective, both submitted eyes and headset pose. It pools the original timing samples for each map, phase and renderer into CSV and JSON. The live NPC counts, individual route timings and scene poses stay available in the JSON. Delivered frequency measures completed synthetic stereo pairs over scheduled display time.

For a connected headset, add `--headset` to the session command and use a separate output root. When MacVR Home owns the panel, also pass `--headset-home`. The witness checks that Home selected the running game and records the headset period, input tracking flags, actual eye size and game-side frame timings. The Home compositor owns final presentation in this mode.

Northest Dakota’s SnowGround meshes use the original Alloy/Weighted Blend material, a non-directional lightmap and a baked shadow mask. The URP bridge fades realtime directional shadows into that mask, following the recovered Steam lighting path. At the same opening Take pose, the cargo container’s shadow was 91–92 display-luma levels darker than nearby snow in Steam and 126–127 in Unity 6.7 Built-in. The earlier URP player showed less than one level of contrast. In the installed URP player the shadow measured 125.55 and 125.73 levels in the left and right eyes on Balanced, and 125.57 and 126.14 on Quality. The Steam capture has a softer shadow edge.

At the first Hold on sequence 1, the tree casts a shadow on snow 49–52 m from the observer. Quality includes that footprint. Balanced uses a 30 m shadow range with a 1024-pixel atlas. To include the tree with the other Balanced settings, select **Balanced** and set **Graphics Options → Shadow distance** to **60 m**. Changing just this control in the same frozen Hold restored 57 and 63 levels of contrast in the two eyes, against 71 and 68 in Built-in. Returning to 30 m removed the footprint again. Three short timing windows gave overlapping GPU times for the two ranges.

Institution uses its own scene color profile. In an earlier Quality test, a custom Neutral curve retained more shadow detail than ACES on the same camera pose. Balanced and Quality now follow the game’s active profile by default. The Graphics Options tone-mapping control can override it.

### Measured Take & Hold results

The M5 Max ran the signed players with synthetic MacVR at 1440 × 1600 per eye and a 90 Hz target. Each cell gives delivered stereo FPS, p95 stereo CPU milliseconds and p95 Unity GPU milliseconds. Each map and phase pools three stock route sequences. Stereo CPU covers both eye renders and submission. Unity GPU comes from Frame Timing Manager.

| Map | Phase | Optimized built-in | URP Balanced | URP Quality |
| --- | --- | ---: | ---: | ---: |
| Classic Hallways | Opening Take | 80.5 / 1.63 / 5.17 | 87.2 / 1.24 / 7.03 | 88.8 / 1.41 / 8.27 |
| Classic Hallways | First Hold | 71.7 / 1.29 / 6.46 | 72.2 / 1.38 / 7.75 | 76.7 / 1.52 / 7.32 |
| Northest Dakota | Opening Take | 87.0 / 1.65 / 8.07 | 85.3 / 1.57 / 6.36 | 85.2 / 1.70 / 7.00 |
| Northest Dakota | First Hold | 79.1 / 1.46 / 7.49 | 71.0 / 1.68 / 7.08 | 75.2 / 1.68 / 7.63 |
| Institution | Opening Take | 72.9 / 0.89 / 6.83 | 77.0 / 0.93 / 7.00 | 81.5 / 1.00 / 8.39 |
| Institution | First Hold | 76.1 / 1.47 / 6.99 | 73.8 / 1.39 / 6.87 | 74.8 / 1.42 / 8.00 |

The analysis covers 14,562 completed stereo pairs in 54 gameplay windows from 27 sessions. Nine Built-in sessions use the unchanged signed player from the preceding sweep. Both URP presets were recaptured after the lightmapped snow fix. The process monitor observed zero concurrent H3VR players during these windows. First Hold windows recorded 3–4 active objective NPCs. Timing uses a simulated headset.

The longest sampled gap between compositor completions was 67.8 ms in Northest Dakota’s first Hold on Balanced, sequence 0. Across the benchmark history, nine preliminary attempts overlapped Unity build activity and were excluded. None of the 27 selected sessions had sampled Unity build activity within one second of a measured window.

A historical Balanced run recorded a 502.6 ms Institution opening Take gap on sequence 0 and 68.2 FPS during its first Hold on sequence 3. Those runs used the earlier presets. The cause remains undetermined.

### Connected Valve Index results

The same M5 Max ran all three renderers with a Valve Index connected to MacVR Home at 120.02 Hz and its saved 150% render setting, 1764 × 1960 per eye. Each cell gives game-submitted stereo pairs per second and p95 Unity GPU time in milliseconds. Each renderer ran the opening Take and first Hold once per map on sequence 0.

| Map | Phase | Unity 6.7 Built-in | URP Balanced | URP Quality |
| --- | --- | ---: | ---: | ---: |
| Classic Hallways | Opening Take | 118.5 / 8.42 | 120.0 / 7.85 | 120.0 / 9.75 |
| Classic Hallways | First Hold | 119.9 / 7.51 | 119.9 / 9.33 | 120.0 / 8.71 |
| Northest Dakota | Opening Take | 118.9 / 5.41 | 120.1 / 4.59 | 119.6 / 5.10 |
| Northest Dakota | First Hold | 120.0 / 6.53 | 120.0 / 5.51 | 119.9 / 5.55 |
| Institution | Opening Take | 118.2 / 4.20 | 119.6 / 7.25 | 119.6 / 6.92 |
| Institution | First Hold | 119.9 / 4.57 | 120.1 / 6.77 | 119.9 / 7.26 |

The game submitted 4,860 stereo pairs across 18 measured windows. MacVR Home owned the display and selected the running game at both ends of each window. The measurements cover submission to Home. Physical panel scanout and controller play were outside this test.
