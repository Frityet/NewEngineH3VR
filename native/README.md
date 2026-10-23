# Native tracking foundation

`build/libNativeH3VRTracking.dylib` is an arm64 Mach-O library with a plain C ABI
for Unity/Mono/IL2CPP P/Invoke. It reads the running Virtual Reality application's
tracking broker. It links the sibling native XR bridge and Apple system libraries, opens no USB
interfaces, does not start or stop the runtime, does not change calibration,
and does not acquire headset output. It is not an OpenVR implementation or a
complete H3VR port.

Build and run the non-invasive checks:

```sh
python3 native/build.py --test
native/build/tracking-probe
```

The build reads `../ValveIndexMacOS` by default, or `--runtime /absolute/path`.
It compiles that runtime's existing room-floor validator and controller-pose
offset header, and records dependency SHA-256 hashes in `build/source-sha256.json`.
It writes only this directory. Generated outputs are ad-hoc signed locally.

The public header is `include/nativeh3vr_tracking.h`. Call
`nh3vr_tracking_snapshot_size` and compare with the managed marshalled size,
then call `nh3vr_read_tracking`. All public structures contain fixed-width
integers, floats/doubles, and fixed buffers; no Objective-C types or C `bool`
cross the boundary. `managed/NativeTracking.cs` supplies the game binding.

Positions use Unity's Y-up/Z-forward coordinates. Quaternions are x/y/z/w;
velocities include the angular-vector handedness correction. Controller grip
and aim offsets use the runtime's established Index transforms. No prediction
or recenter is invented. The headset tracking pose is its tracking origin,
not a calibrated eye pose: stereo camera transforms must come from OpenXR.

Outside an XR session, the floor is subtracted only when the existing saved calibration validates
against the broker's current station transforms. Missing or mismatched floors
are explicit. Device presence, usable pose, recently tracked pose, velocity,
optical freshness, and current input have separate flags. Stale controller
input and pose values are withheld. Raw button bits and axes use the runtime's
Index mappings described in `ValveIndexMacOS/docs/ARCHITECTURE.md`.

During an XR session, `nh3vr_xr_locate_device` supplies each pose and velocity in
the same STAGE and at the same display time as the rendered eyes. In particular,
STAGE includes the runtime's initial headset X/Z and heading, which cannot be
reproduced by merely subtracting the floor from broker positions. Controller
action spaces cancel the runtime's IMU-to-grip offset to preserve `pose/raw`.
The broker still supplies device identity, input samples and freshness flags.
This fixes the controller displacement/rotation found in the first physical
playtest; the user confirmed alignment and successful 1911 firing afterward.

For native-only bridge changes, keep managed sources unchanged, run the native
tests, and stage an ABI-compatible update before packaging the existing player:

```sh
python3 native/build.py --test
python3 script/update_native_bridges.py --receipt build/native-update-NEW-NAME
python3 script/package_native.py 'dist/NewEngineH3VR.app'
```

Use a new receipt directory each time. Stop the player before repackaging. The
update checks managed source fingerprints, native build fingerprints and export
compatibility, preserves small rollback files and updates all staged copies.
Managed or content changes still require the normal composition/build workflow.

The IPC contract is runtime protocol 3 / IndexCore ABI 2. Its source headers and
message sizes are checked, but changing the sibling protocol requires revisiting
`src/broker_protocol.h`. A read has a 200 ms transfer deadline; callers should
not perform it on the Unity render thread. The bridge trusts the same-user local
broker and reads only the state directory that broker returns.

## Verified on 22 October 2026

- Build succeeds with Apple clang, `-arch arm64`, `-Wall -Wextra -Werror`.
- Native tests pass for ABI size, coordinate transforms, angular velocities,
  controller offsets, fresh/stale inputs, room-anchor mismatch, protocol mismatch,
  and an unavailable broker. Tests use a private mock socket and temporary room
  fixture; no hardware is opened.
- `file` identifies the plugin and three tools as arm64 Mach-O.
- `otool -L` confirms the plugin links only Foundation/CoreFoundation/Objective-C
  and libSystem, with no Wine or Rosetta dependencies.
- Read-only live broker probe received three devices, a ready floor at -0.650 m,
  and a fresh headset pose (~1 ms age). Both controller poses were unavailable
  and correctly withheld. This does not establish playable game integration.
- `openxr-capabilities` loaded the existing arm64 OpenXR loader/runtime and
  enumerated `XR_KHR_metal_enable` version 3. It creates no XR instance, system,
  session, swapchain, or headset output.

## Next integration boundary

Implement an arm64 render-thread bridge that owns a real OpenXR instance/session
using `XR_KHR_metal_enable`, accepts Unity Metal textures, copies/submits both
eyes to real swapchains, and returns calibrated view poses/FOVs. Begin with the
runtime's explicit synthetic/headless mode so no live HMD session is taken.
Use `ValveIndexMacOS/tests/openxr_frames.m` and `native/openxr/IVXRGraphics.m`
as the actual API examples. Then connect SteamVR's managed interface to this
bridge or implement the required OpenVR function tables. Texture submission is
not provided by this tracking library; it never claims that unsupported work
succeeded.

## Real Metal/OpenXR bridge (22 October 2026)

`build/libNativeH3VRXR.dylib` now implements the next integration boundary above.
Its public C ABI is `include/nativeh3vr_xr.h`, and its managed P/Invoke binding and
explicit stereo camera component are `managed/NativeH3VRXR.cs`. This is a native
arm64 Metal path; no Wine/OpenComposite/Windows graphics library is involved.

The bridge dynamically loads the supplied native OpenXR loader and manifest,
creates a Metal session, calibrated STAGE reference space, two real sRGB RGBA8
swapchains, and obtains each eye's pose/FOV at the predicted display time. Unity
positions/quaternions are converted once from OpenXR coordinates. Invalid view
poses suppress rendering rather than inventing tracking.

After `nh3vr_xr_begin`, render two non-MSAA, same-size RGBA8 sRGB textures and call
`nh3vr_xr_set_textures`, then `GL.IssuePluginEvent(nh3vr_xr_render_event(), 1)`.
The plugin ends Unity's current encoder and encodes both copies into Unity's
actual current Metal command buffer through the installed Unity PluginAPI.
Only after GPU completion does a separate submission queue release both images
and call `xrEndFrame`. This separate queue is necessary: doing compositor GPU
waits from Metal's completion queue deadlocked and was diagnosed/fixed in the
first real test. Frames are serialized; this implementation does not claim
asynchronous reprojection or low-latency pipelining.

`synthetic_headless=1` explicitly configures this process's runtime for synthetic
tracking, headless output, and a unique private Home control directory. The latter
prevents synthetic frames reaching the separate live Home compositor. It still
creates real Metal swapchains and executes
the real compositor. `0` enables live tracking/output and must only be used for
an intended physical headset session. No hardware test has been run here.
The default managed component is synthetic/headless and never auto-attaches.

`python3 native/build.py --test` now also validates native session creation,
calibrated stereo eye ordering/FOV, frame call order, GPU-completed submissions,
shutdown/restart, actual captured compositor pixels (red left, green right), and
the exported Unity callback with a host-interface fixture and real Metal command
buffer. The fixture confirms no frame is completed before the host commits its
buffer and shutdown returns busy while ownership is outstanding. The resulting
image is `build/xr-stereo.png`. Managed sources also compile against installed
Unity 2020.3.49f1 references. The host fixture is not itself a Unity player test;
`../vr-probe` is the separate real-player verification project.
The same command includes a source-injected eye-query regression test for
transient validity loss, subsequent recovery and preservation of fatal errors;
this unit test creates no OpenXR or Metal session.

### Integration limits

- The component requires an explicit source game camera and tracking-space root.
  It renders the original source camera for each eye, preserving its camera and
  image-effect callbacks. The original SteamVR lifecycle, action input, haptic
  commands and camera rig have the guarded integration described below. Estimated
  finger-curl summaries are implemented; measured bone joints, generic overlays
  and generic controller-model loading remain unavailable.
- An orderly teardown uses `RequestStop()` and allows its coroutine to drain.
  Destroying the component with a pending GPU buffer warns and retains native
  session ownership instead of freeing in-flight objects. The host must drain
  rendering before unloading the native plugin.
- One XR instance/session is supported per process. The first loader image stays
  loaded for process lifetime because the OpenXR loader has global dispatch state.
- The runtime's existing STAGE calibration and optical/view parameters are used;
  measured physical stereo orientation, image flipping, latency, and distortion
  must still be confirmed on the headset before claiming gameplay parity.


## Original managed SteamVR integration

Build guarded copies of the original retargeted game assemblies:

```sh
python3 native/build_managed.py --input audit-managed/ported --output native/build/ported-vr
```

The input directory can instead be a composition of the platform/Steam, video,
and FMOD patches. All input DLLs are copied, with VR patches applied last to the
output `NativeH3VR.FirstPass.dll` and `NativeH3VR.Game.dll`; the input is unchanged.
`vr-patch-manifest.json` distinguishes actual adapters from explicit unsupported
paths. The original fallback code remains when native mode is not enabled.

Add the output DLLs and both native dylibs as macOS arm64 plugins. Copy
`native/build/runtime/build` to `StreamingAssets/NewEngine/build`, and the
installed game's original actions manifest to `StreamingAssets/SteamVR/actions.json`.
Opt in at launch with `NATIVEH3VR_OPENXR=synthetic` or `live`. Override the runtime
root with `NATIVEH3VR_RUNTIME` and manifest with `NATIVEH3VR_ACTIONS` if needed.
Synthetic mode never fabricates connected controller inputs in the game; tests
use explicitly labelled in-memory fixtures separately.

With `NEWENGINE_UNITY_2021`, the adapter snapshots the OpenXR mode at first use
and refreshes it when creating a session. Changing the environment during an
existing session does not switch backends. The same gate caches manifest-derived
action names and set handles after each registration batch, plus constant input
structure sizes. Input polling, focus and freshness checks, action edges and
snapshot ownership retain their original behavior. The managed producer defines
the gate for both XR and SteamVR adapter compilations when using Unity 2021.
See [input cache tests](input-lookup-tests/README.md) and the
[candidate validation](../reports/INPUT_RECOVERY_2026-10-23.md).

The implemented path reuses the real game's `SteamVR.Initialize`, input action
objects, event system, tracked objects and original `SteamVR_Camera` rig expansion.
The managed instance receives calibrated eye transforms/FOV from OpenXR and the
source camera gets a native stereo renderer. HMD poses come from OpenXR's VIEW
space relative to STAGE, rather than misusing the headset's raw tracking origin.
An eye-transform query that temporarily lacks valid orientation or position
keeps the previously calibrated transforms without latching a fatal submission
status. Initialization still requires valid eyes; API failures, invalid stereo
counts and missing session state remain errors. This does not change the
existing frame-submission tracking-validity checks.
The original `FVRPlayArea.TryGetPlayArea` reads the runtime's actual STAGE bounds
through the chaperone adapter; unavailable bounds remain invalid. Display frequency
comes from the runtime frame period, including the game's physics-timestep query.
Controller poses use the manifest's `pose/raw` binding. Action handles come from
the actual manifest, active action sets are observed, and stale device samples
are withheld. Primary axes map to the Index trackpad and secondary axes to the
thumbstick, matching the shipped Knuckles bindings. Force clicks use the
runtime's established 0.5 grip/trackpad threshold; custom SteamVR rebinding and
per-user click thresholds are not implemented. One sample is read per Unity
frame so action-edge history survives multiple consumers. The `Any` source
currently selects an available controller; it does not implement the complete
OpenVR multi-device aggregation/priority rules.

Haptic output is bound as a real OpenXR vibration action with Index left/right
subaction paths, synchronized before submission. Absent/stale controllers return
NoData, invalid values/delayed starts are rejected. Synthetic tests verify runtime
command acceptance only; no physical tactile behavior has been tested.

Unavailable interfaces return explicit errors or throw when no error channel
exists. No OpenVR function tables or success stubs are fabricated. Skeleton
bindings provide estimated curl summaries from measured finger proximity and
trigger/grip/contact inputs, plus the actual controller root pose. Tracking level
is explicitly Estimated. Original actions use summary-only updates; bone queries
remain unsupported, and expired input clears cached curls to prevent a latched
grab. See `../audit-vr/ESTIMATED-SKELETON.md` for the original-game hold/release
proof and the remaining physical-calibration limits.
The optional ForceTube gunstock subsystem is explicitly
disabled on macOS with one warning because its supplied native binary is Windows
only; commands are not sent and data queries are unsupported. This is separate
from the genuine Index-controller haptic path.
Optional LIV mixed-reality capture is also disabled on macOS with one warning:
the supplied `LIV_MR` shared-texture interface has no native Metal implementation.
Both original component lifecycles are guarded, capture reports inactive, and
capture dimensions explicitly report unsupported instead of inventing a size.

### Actual Unity player verification

`vr-probe` builds an actual arm64 macOS Unity 2020.3.49f1 player. The stereo probe
renders red/green eye textures with `Camera.Render`, passes their actual native
pointers, and invokes the plugin through `GL.IssuePluginEvent`. The game bridge
probe calls the original SteamVR lifecycle and camera classes, initializes all
21 original actions, and completes five native stereo frames. It uses a plain
blue scene; this demonstrates integration, not gameplay or shader parity.
It also verifies native STAGE bounds through the original FVRPlayArea method and
enables/destroys both original LIV components without attempting unavailable imports.

```sh
python3 native/test_unity.py         # Stereo textures + deterministic action fixtures
python3 native/test_unity.py --game  # Original SteamVR lifecycle and camera rig
```

The probe's plugin directory must contain the composed managed output and both
dylibs first. `output/*-verification.json` records architecture, markers and native
plugin hashes; `*-player.log` and `*-stereo.png` hold logs and actual compositor
captures. All final tests use synthetic tracking, headless output and the private
Home control directory. An earlier shared-runtime rebuild caused a real capture
regression, which pixel assertions detected; a subsequent passing binary was
pinned inside this project by `pin_runtime.py` with its native dependencies,
source snapshot and SHA-256 provenance. Tests now use that pinned runtime.
