# Estimated Index curls and original grab semantics

The native bridge now supplies active, explicitly **Estimated** skeletal summaries
from controller inputs already carried by the pinned native tracking broker.
This is a procedural hand estimate, not Valve's proprietary hand solver or
measured bone-joint tracking. No physical controller calibration or subjective
control-feel equivalence has been established.

## Input evidence and mapping

The read-only source `ValveIndexMacOS/extern/libsurvive/include/libsurvive/survive_types.h`
defines middle/ring/pinky/index proximity at axes 4/5/6/7, grip force at 8 and
trigger travel at 1. `extern/libsurvive/src/driver_vive.c` decodes the four separate
proximity bytes and contact-force bytes as normalized values divided by 255.
The pinned runtime's `native/index_core.m` carries the axes and touch bits into
its broker state; `native/src/tracking.m` preserves all 16 axes for managed use.

`native/managed/NativeEstimatedHand.cs` applies this documented estimate:

| Summary | Inputs |
|---|---|
| Middle/ring/pinky curl | Maximum of that finger's measured proximity and grip force |
| Index curl | Maximum of index proximity and trigger travel; trigger contact establishes a 0.35 lower bound |
| Thumb curl | 0.7 while touching trackpad, thumbstick, A or B; otherwise 0 |
| Four splay values | Neutral 0; no splay measurement is available |

Values are clamped to 0–1; non-finite samples contribute zero. The force lower
bound represents a finger pressing the controller. Proximity can retain a curled
hold after squeeze force ends. Thumb contact is only an estimate of curl, not a
thumb-position measurement. The constants and proximity-to-curl mapping have not
been calibrated against the original SteamVR solver.

Actions resolve their left/right identity from the shipped manifest's `skeleton`
path and respect active action sets and fresh connected input. Root pose comes
from the actual controller pose. `GetSkeletalTrackingLevel` reports Estimated,
never Partial or Full. The original SteamVR actions run in their existing
summary-only mode. Bone count, hierarchy, names, transforms and reference poses
remain explicitly unsupported; there is no invented 31-bone success response.

Original SteamVR retains its last curl arrays when a binding becomes inactive.
A native-only postfix clears those arrays after inactive updates, allowing the
original Index state machine to release a stale hold. No input timestamp is
refreshed by a skeletal query.

## Actual original-game fixture

`vr-probe/Assets/NativeIndexGrabProbe.cs` executes the original
`FVRViveHand.PollInput` inside the real ARM64 Unity player. It supplies the original
hand's required singleton/options/transforms and original SteamVR actions, using
inactive setup objects to avoid unrelated game lifecycle and save effects.
The fixture uses the explicitly opted-in, expiring diagnostic input channel;
it does not access or send frames to the physical headset or live Home session.

The original method reads five skeleton curls and uses a middle-weighted
middle/ring average for its Index hold/release state. The test passed:

1. Open hand has an active Estimated summary and no grab.
2. Grip force starts a grab and its down edge.
3. Distinct middle/ring/pinky proximities 0.8/0.7/0.6 preserve the hold after force,
   trigger and all touch inputs have been released.
4. Opening fingers releases the grab and produces its up edge.
5. Curl alone while released does not invent a new press.
6. Trigger travel can start a grab; proximity retains it after trigger release.
7. Expired diagnostic samples deactivate the binding, clear curls and release.

Reproduction:

```sh
python3 native/build_managed.py --output native/build/ported-vr-estimated
# Stage these managed outputs in the isolated vr-probe plugin directory.
NATIVEH3VR_DIAGNOSTIC_INPUT=1 python3 native/test_unity.py --game --refresh-scripts
```

Evidence: `vr-probe/output/game-verification.json` and `game-player.log` contain
`NATIVE_INDEX_GRAB_PASS`; the same run passed original pose/trigger/grip edges,
camera callback and scene-handoff checks, and actual grid/black/restored Metal
capture checks. The executable is Mach-O arm64, running on Apple M5 Max Metal.
The composed patch manifest contains 370 guarded entrypoints. The static usage
scan now counts 32 native CVR adapters and 55 remaining unsupported signatures.

This proves the original hand's input state machine with controlled sensor
samples. It does not prove physical finger calibration, grabbing an actual
loaded firearm, full gameplay, or the optional meat-hand visual animation. The
latter consumes the same original `Input.FingerCurl_*` fields, but its rendered
appearance has not been checked. The separate full-game acceptance harness is
still required for firearm acquisition and trigger/firing behavior.
