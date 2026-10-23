# Physical-test readiness: preparation only

This is a source inspection, not a live headset test. No tracking broker was
queried, hardware enumerated, device state changed, physical Home opened, or
headset presentation acquired for this report. The new session-focus tests use
private synthetic runtime state; see `SESSION-FOCUS.md`.

## Required software checkpoint

Before inviting a physical test, finish the corrected native bundle build with
its clean compiler receipt, packaged ARM64 dependency/signature audit, full
synthetic MainMenu3 → IndoorRange_Updated → M1911 load/grip/fire route, fresh
range/stereo image review, and stage the focus fix. The isolated Unity2020 focus
proof does not substitute for the final Unity2021 packaged-player route.

The focus changes must be staged together: `libNativeH3VRXR.dylib`,
`NativeH3VR.XR.Managed.dll`, and `NativeH3VR.SteamVR.Adapter.dll`. Run canonical
managed composition rather than copying isolated baseline Game/FirstPass DLLs.
`build/native-focus-proof.json` records the verified artifacts. Input now follows
actual OpenXR FOCUSED state; loss clears controls while retaining poses, and
held controls must return to neutral before rearming. H3VR explicitly selects
scene-client mode, never Home ownership. No further native code change was
identified as a prerequisite by this bounded source review.

The following package checks are read-only with respect to hardware, although
the auditor writes its separate local report. They are commands for the final
packaged checkpoint, not evidence that the current app has already passed:

```sh
python3 script/audit_native_app.py 'dist/NewEngineH3VR.app' \
  --report build/physical-preflight-package.json
lipo -archs 'dist/NewEngineH3VR.app/Contents/MacOS/NewEngineH3VRPlayer'
```

The expected architecture is only `arm64`; use the auditor's passing result and
hash-bound inventory, not architecture alone. The runtime and action files must
come from the package without development overrides. `script/run_native.py`
provides the existing strict synthetic route check; it cannot validate live VR.
`script/build_and_run.sh` is not a passive preflight: it builds/stages files and
terminates a matching player before building.

## Tracking and controller readiness

The existing `native/build/tracking-probe` calls only our broker-read C API:

```sh
native/build/tracking-probe
```

It never starts a driver, opens USB, sends haptics, changes calibration or
acquires output. A failed read should stop preparation rather than be worked
around by launching the game: the pinned OpenXR runtime can fall back to opening
the physical driver directly when the broker is absent.

The probe must show `running=1`, `simulation=0`, `playback=0`, `floor_state=2`, and head/left/right
roles 1/2/3. Check each required device's flags, rather than treating a successful
snapshot as successful tracking:

| Bit | Meaning | Useful physical readiness signal |
| --- | --- | --- |
| 0x01 | Connected | Device currently connected |
| 0x02 | Valid pose | Finite pose, age at most 0.25 seconds |
| 0x04 | Tracked pose | Pose age below 0.1 seconds |
| 0x10 | Current input | Fresh controller input source |
| 0x20 | Current optical sample | Optical age at most 0.25 seconds |
| 0x40 | Controller offsets | Calibrated grip/aim transform available |

Floor state 0 is unset, 1 is awaiting tracking, and 3 is a changed calibration;
none is the ready state. Check meaningful motion and sustained optical updates
in the eventual physical observation. IMU prediction alone cannot establish
room tracking.

The local probe now also prints the playback flag, buttons, touches and all 16
input axes. It was rebuilt for ARM64 with compiler warnings treated as errors;
no physical broker was contacted for that change. Do not use `simulation=0`
alone to rule out playback. If additional broker metadata is needed, the existing
runtime `indexctl status --seconds 2 --stream` command is a broker-only JSON
reader according to the pinned `native/cli.m`; it reports the full status and
state directory through `IVSnapshotJSON`. Use an already installed matching
runtime CLI after verifying its path/version: require `source="hardware"` (the
alternative values are `recording` and `simulation`) and `error=0`. Do not
substitute `indexctl track`, pairing, haptic, camera or output commands: those
have different effects. No sibling executable was invoked for this inspection.

Runtime/controller grip support is estimated skeletal input derived from actual
Index trigger, squeeze/force and finger-proximity channels. It is deliberately
reported as `VRSkeletalTracking_Estimated`. Original FVRViveHand grab/hold/release
passed isolated input fixtures; full optical finger tracking is not claimed.
The eventual physical test must check both hands, tracking loss/recovery and
held grip/trigger across dashboard focus changes. Those are still unverified
with physical sensors.

## Live launch selection and presentation signals

`platform/launch/NativeLauncher.c` sets the official Steam app ID, switches to
bundle Resources and executes the ARM64 Unity player. With no override the
packaged app selects `live`; the automated runner always selects `synthetic`.
For live mode the native bridge forces simulation and headless off, selects the
bundled OpenXR manifest and sets `VI_OPENXR_HOME=0`. Synthetic mode creates a
private Home-control directory.

Before a future live launch, inspect inherited `VR_TRACKING_SOCKET`,
`VR_STATE_DIR`, `INDEXMAC_STATE_DIR`, `VI_HOME_CONTROL_DIR` and `VR_OPENXR_TARGET_HZ`.
They must agree with the intended resident runtime, calibration and Home. Do not
blindly replace an intentional custom state directory. The usual state directory
is `~/Library/Application Support/Virtual Reality`; the broker's reported state
directory is more authoritative. The pinned runtime accepts target refresh only
90 or 120 Hz. Invalid values fail initialization.

Once the preceding checks are accepted and physical testing is requested, this
bounded command selects the packaged live runtime explicitly, removes all
NativeH3VR test/development overrides, writes a separate log and asks the
existing diagnostic component to quit after 30 seconds. It is **not** a passive
check: it can acquire physical headset output. It has not been executed here.

```sh
python3 - <<'PY'
import os, subprocess, uuid
from pathlib import Path
root = Path.cwd()
env = {k: v for k, v in os.environ.items() if not k.startswith('NATIVEH3VR_')}
for name in ('VR_OPENXR_CAPTURE', 'VR_OPENXR_CAPTURE_EVERY'):
    env.pop(name, None)
env.update(NATIVEH3VR_OPENXR='live', NATIVEH3VR_TEST_DURATION='30',
           NATIVEH3VR_RUN_ID=str(uuid.uuid4()))
subprocess.run(['/usr/bin/open', '-n', '-W',
                str(root / 'dist/NewEngineH3VR.app'), '--args',
                '-force-metal', '-logFile', str(root / 'build/native-live-player.log')],
               env=env, timeout=120, check=True)
PY
```

The 30-second quit runs after Unity initializes its diagnostic component. The
outer timeout bounds the waiting launcher, not a stalled child player; a timeout
must be investigated and the specific test instance closed, not reported as a
pass. A future dedicated physical runner could additionally supervise its PID.

Expected software evidence includes Metal initialization, the correlated run ID,
`NATIVE_STEAMVR_INITIALIZED backend=Metal/OpenXR synthetic=False`, actual eye
submission, and an error-free bounded observation. `home/runtime.txt` under the
active Home-control directory reports mode/active app/PID, but that only shows
Home coordination. It does not prove a display signal or an image in the HMD.

The runtime may log `native HMD output session acquired`; its own log explicitly
says physical imagery requires visual verification. A positive submitted
GPU-completed-frame count shows completed submission, not visible photons.
`native HMD output unavailable`, `rejected`, or `detached` requires diagnosis.
The desktop fallback requires a matching calibrated external display and usable
2880×1600 mode at least 90 Hz. The desktop mirror and stereo capture can be
correct even when physical presentation is absent. Verify both eyes, upright
orientation, color, head motion, floor height and comfort only in the eventual
physical test; no new display/calibration settings are prescribed here.

## Audio readiness

The FMOD bridge's original RuntimeManager startup already passed an isolated
ARM64 Unity2020 probe: real CoreAudio, 48 kHz, 32 software channels, seven original
banks and 24 events, without NOSOUND. See `platform/audio-spatial/README.md`.
That is engine/bank proof, not a listening test. Original Unity 3D spatial blend
and listener placement are retained.

A future read-only `system_profiler SPAudioDataType` inventory can show available
and default audio outputs. It was not executed here. The launcher does not select
an Index audio device; AUTODETECT uses the actual CoreAudio configuration. An
available/default endpoint does not prove that audio is audible in the headset.
The eventual physical test must confirm the intended sink, left/right balance,
head-relative sound and original firearm audio without changing global audio
routing merely to make an automated result pass.
