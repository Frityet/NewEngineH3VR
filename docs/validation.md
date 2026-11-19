# Validation and open gates

The macOS candidate targets Unity **2021.3.45f2**, Mono and ARM64 Metal. The
Unity test scene and native mod-host fixture use independently authored assets.

## Verified locally

- A fresh Steam extraction of app 450540 build 25002541 completed. The
  converter retargets the installed managed assemblies and preserves their
  serialized field layout. A private managed verification run passed with
  zero unresolved type, method or engine field references. Project migration
  resolved 4,387 original asset-bundle addresses across 18 bundles.
- The rebuilt Steam and FMOD shims resolved all 850 inspected CSteamworks and
  545 FMOD imports by symbol. The stock player initialized the running Steam
  client after the two native shims and their dependencies moved directly into
  the player's PlugIns directory. Spatial audio and video playback still need
  dedicated acceptance checks.
- A signed arm64 Unity fixture loaded the MacVR 0.4.0 runtime through the
  Khronos loader and ran on Apple M5 Max Metal. It acquired focus, read two
  tracked controller poses, reached the configured 2 m stage bounds and
  submitted accepted synthetic haptics to both hands. The captured
  compositor image showed upright markers and 22 px of binocular disparity
  at 800 × 800 per eye. The measured head height was 1.6 m with 64 mm IPD.
  Unity resolved its persistent data into the launcher's private state path.
- The generic native mod host registered an external assembly before the
  first scene and resolved that assembly's MonoBehaviour in an independently
  built Atlas scene bundle. Assembly and manifest rejection was checked.
- The shader exporter recovered 277 raw files from 390 serialized Shader
  objects in the installed Steam build. All 277 exported files match their
  original serialized identities and recovered project GUIDs. The complete
  generated set imported on Metal with zero shader errors. This isolated
  check covers default compilation. Exact stock variant checks included
  lighting, fog, LOD crossfade, instancing and SpeedTree geometry. The strict
  stock player build serialized 46 scenes and all 18 native asset bundles.
  [Shader recovery](shader-recovery.md)
- All 22 kernels translated from the extracted Direct3D compute programs
  compiled on Apple M5 Max Metal with their original thread-group sizes.
  Metal dispatch/readback verified a 128-entry histogram clear and linear
  interpolation over 64 texture voxels. The actual game effects still
  require integration testing. [Compute recovery](shader-recovery.md#compute-shader-recovery)

## Reproduce the Unity fixture

From this checkout, with a built MacVR runtime and a closed, isolated target
project:

```sh
dotnet run --project tools/UnityMetalCli -c Release -- smoke \
  --unity-editor "/path/to/Unity.app/Contents/MacOS/Unity" \
  --openxr-headers "/path/to/MacVR/extern/openxr/include" \
  --runtime "/path/to/macvr-openxr" --frames 1110 \
  --work "/path/to/new-metal-smoke"
```

The fixture writes the editor and player logs, the composited stereo image and
separate Unity and MacVR traces. The capture check verifies both eyes and the
vertical marker orientation. [Trace analysis command](platform-integrations.md)

## Open gates

The first cold validation loaded 46 scenes and inspected 8,293 prefabs. It
recorded 41 shader asset failures and 16 missing EventSystem components
across eight scenes. A targeted Unity retest confirmed all 16 bindings
through the original file IDs. The current isolated Metal import covers all
277 graphics shaders and the 11 compute assets with their original GUIDs
and 22 kernels. Full project validation combines these inputs and records
shader upgrades applied by Unity.

The assembled app verified as thin ARM64 with a strict deep signature. The
converter restored 1,962 original lighting probe sets across 15 scenes. The
stock player loaded through Steamworks and rendered the main menu at
1,440 × 1,600 per eye. Atlas loaded five external mod assemblies and registered
its map from the same signed player. Seeded gameplay, lighting appearance and
performance on the stock, Facility door and Level 0 routes need runtime
acceptance. Physical presentation, controller handling and player acceptance
require a live session.

Unity 2021.3.45f2 stays selected for this checkpoint. It produced the cold
player, rebuilt the stock bundles and ran the external mod smoke. A comparable
gameplay trace will inform any engine change.
