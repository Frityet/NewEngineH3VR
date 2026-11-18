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
  545 FMOD imports by symbol. Steam client authorization, spatial audio and
  video playback still need stock-player runs.
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
  original serialized identities and recovered project GUIDs. An eight-file
  Unity Metal probe identified missing variants in three Alloy files. Those
  three passed the same probe after adjusting mutually exclusive keyword
  groups according to the original programs. Full material and scene
  validation remains pending. [Shader recovery](shader-recovery.md)

## Reproduce the Unity fixture

From this checkout, with a built MacVR runtime and a closed, isolated target
project:

```sh
python3 tests/UnityMetal/smoke.py \
  --unity-editor "/path/to/Unity.app/Contents/MacOS/Unity" \
  --openxr-headers "/path/to/MacVR/extern/openxr/include" \
  --runtime "/path/to/macvr-openxr" --frames 1110 \
  --work "/path/to/new-metal-smoke"
```

The fixture writes the editor and player logs, the composited stereo image and
separate Unity and MacVR traces. The capture check verifies both eyes and the
vertical marker orientation. [Trace analysis command](platform-integrations.md)

## Open gates

The stock player and its production mod bundle still need a compatible,
identity-matched shader overlay. That includes eleven Direct3D-only compute
assets whose 22 original kernels need compatible Metal programs. A successful
Unity validation reports the Metal device and records any shader changes
applied by Unity's source upgrader. The next Unity build must preserve original
scene/script bindings and bundle addresses, verify the assembled app
signature, and run through Steam authorization and stock startup. Actual
gameplay, seeded Atlas acceptance and performance on the stock, Facility
door and Level 0 routes remain open. Physical headset readiness, presentation,
controller handling and player acceptance require a live session.

Unity 2021.3.45f2 stays selected for this checkpoint. The Metal and external
mod smoke tests passed there. The stock shader gate currently precedes any
comparable game trace, so engine comparison is deferred.
