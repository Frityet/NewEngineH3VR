# What's been tested

The Mac build uses Unity **2021.3.45f2**, Mono and ARM64 Metal. The Unity test scene and the mod-host test use our own assets, not the game's.

## What works

All of this was tested on Steam app 450540, build 25002541.

**Extraction and code**
- A fresh extraction finished.
- The retargeted game code kept its saved field layout. A verification run found no unresolved types, methods or engine fields.
- 4,387 original asset bundle addresses across 18 bundles were mapped.

**Steam and FMOD**
- The rebuilt shims resolved all 850 CSteamworks imports and 545 FMOD imports.
- The game started up with the running Steam client once the shims and their dependencies were in the app's `PlugIns` folder.
- Spatial audio and video still need their own tests.

**VR (test scene)**
- A signed arm64 Unity test app loaded the MacVR 0.4.0 runtime and ran on an Apple M5 Max.
- It got focus, tracked both controllers, saw the 2 m play area and sent haptics to both hands.
- The captured image had both eyes upright, at 800 × 800 per eye with 22 px between them. Head height was 1.6 m and IPD 64 mm.
- Unity stored its save data in the launcher's private folder.

**Mods**
- The mod host loaded an outside assembly before the first scene, and used its scripts in a separately built Atlas scene bundle.
- Bad assemblies and bad manifests were rejected.

**Shaders** ([details](shader-recovery.md))
- 277 shader files were recovered from 390 shader objects. All 277 match their originals and keep the right project GUIDs.
- All of them imported on Metal with no errors (default variants).
- Specific variants were checked for lighting, fog, LOD crossfade, instancing and SpeedTree.
- The full game build packed all 46 scenes and all 18 asset bundles.

**Compute shaders** ([details](shader-recovery.md#compute-shader-recovery))
- All 22 translated kernels compiled on Metal with their original thread-group sizes.
- Two were run and checked on the GPU: a 128-entry histogram clear, and interpolation over 64 voxels.
- The in-game effects that use them haven't been tested yet.

## Running the VR test yourself

You need a built MacVR runtime and a new, closed Unity project folder:

```sh
dotnet run --project tools/UnityMetalCli -c Release -- smoke \
  --unity-editor "/path/to/Unity.app/Contents/MacOS/Unity" \
  --openxr-headers "/path/to/MacVR/extern/openxr/include" \
  --runtime "/path/to/macvr-openxr" --frames 1110 \
  --work "/path/to/new-metal-smoke"
```

It saves the editor and player logs, the stereo image, and Unity and MacVR traces. It checks that both eyes are there and right side up. To compare timings, see [profiling](platform-integrations.md#profiling).

## Still to do

**Project validation.** The first full check loaded 46 scenes and 8,293 prefabs. It found 41 broken shader assets and 16 missing EventSystem components across eight scenes. Since then:
- a follow-up test fixed all 16 EventSystem bindings
- all 277 shaders and 11 compute shaders (22 kernels) import on Metal with their original GUIDs

A full project check that combines these, and records Unity's own shader upgrades, is still needed.

**The game itself.**
- The app is a properly signed ARM64-only build.
- 1,962 original lighting probe sets were restored across 15 scenes.
- The game started through Steam and showed the main menu at 1,440 × 1,600 per eye.
- Atlas loaded five mods and registered its map in the same app.

Not yet tested:
- actual gameplay, lighting and performance on the stock game, the Facility door and Level 0
- a real headset: how it looks, how the controllers feel, and whether it's playable

**Unity version.** We're staying on 2021.3.45f2 for now, since it built the game, rebuilt the bundles and ran the mod test. A gameplay trace will decide whether to change engine versions.
