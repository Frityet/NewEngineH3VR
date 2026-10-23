# Synthetic gameplay versus physical readiness

This snapshot reads existing source and evidence only. It does not launch the
game, inspect live hardware, or change unity-full. It is not gameplay acceptance.

## Ready inputs and test machinery

The latest composed Game, FirstPass, XR managed bridge and SteamVR adapter are
byte-identical to their copies staged in unity-full. The native XR library and
latest gameplay harness DLL also exactly match their staged copies. The composed
Unity2021 API audit reports zero errors across 2,002 type references, 99,290 call
sites and 14,782 field accesses. Session-focus handling and held-control rearming
already passed isolated native/runtime and actual ARM64 Unity tests.

The final sampling producer is
`943cf5047a12e9dd1484e3113af26fb474fa56bcee414869c0b0ad31233db088`.
Its original-instruction verification covers 48 programs, 976 sample operations
and 3,900 mask cases. The freshly rebuilt ARM64 Unity2021/Metal pixel probe has
12 exact CPU matches and 39 detected negative controls. Its receipt now binds
the actual final Release producer and canonical GUI module. All 71 recorded
artifact/compiler/policy hashes independently match current files. That is
sampling-operation proof, not acceptance of every regenerated shader or scene.

The gameplay harness and strict runner are implemented and staged. The route
uses original MainMenu3 scene transition into IndoorRange_Updated, original
Anvil asynchronous M1911 prefab loading, collision hover, original hand grip and
hold/release behavior, trigger input and optional original firearm shot. Shot
acceptance requires a newly created original ballistic projectile with valid
muzzle origin, positive initial velocity and actual travelled distance. Fresh
range and stereo captures are required. The runner uses private synthetic VR,
requires a matching run ID/Metal/eye submission markers and rejects unresolved
load errors or exceptions. A route pass remains a bounded acceptance test, not
complete gameplay or visual parity.

## Evidence still required before that route can pass

The available first bundle summary reports 18 bundles and 4,387 addresses, but
its build log is the rejected first build with shader errors. At inspection,
there is no new clean compiler receipt/content proof from the corrected rebuild.
The first-build summary cannot satisfy the current strict schema-2 bundle gate.
The final shared shader regeneration/staging, corrected bundle compilation and
actual dependency-ordered content verification must finish first.

There is also no current `build/native-app-audit.json`, full-player GI restoration
report, full-player scene-script proof or full-player compiler receipt. The older
`native-app-audit-existing.json` is a failed audit of the earlier app; it is not
evidence that current packaging code still has those defects. Current packaging
code localizes dependencies, signs the app and runs the strict binary audit.
Its output must actually pass on the new built app.

`build/native-run.json` is the older menu observation with missing ammunition
bundle and eager ForceTube import errors. Those defects have isolated fixes,
but this old run does not prove the fixes reached a newly packaged full player.
`build/native-gameplay-run.json` does not yet exist. The historical status table
is not a substitute for fresh, hash-bound full-build/run evidence.

The intended sequence after corrected sources are staged is:

1. Complete the clean full bundle build and its source/output-bound content proof.
2. Build the full ARM64 player through the existing orchestration, including
   compiler gate, post-build original GI restoration, scene-script proof,
   packaged bundle verification and dependency/signature audit.
3. Run `python3 script/run_native.py --seconds 30` for a clean packaged menu boot.
4. Run `python3 script/run_native.py --gameplay --fire` for the bounded original
   asynchronous load/grip/fire route, then review its fresh range/stereo images.

These are proposed next steps, not commands executed by this audit. Scripts
intentionally fail closed when required evidence is absent or stale.

## Physical steps remain separate

No physical headset, controller or audio-sink readiness was assessed here.
The local broker-only tracking probe now prints playback, buttons, touches and
all 16 axes; its source preserves the no-driver/no-USB/no-haptic/no-output read
contract. Before a future physical launch, require real non-playback broker
tracking, calibrated floor and current head/left/right poses and input. A live
launch without a broker may fall back to starting a driver, so it is not a
passive readiness query.

After synthetic acceptance and an explicitly requested physical test, the
remaining observations are actual images in both eyes, correct orientation/color,
head/hand motion and floor height, input recovery across focus loss with held
controls, and audio through the intended output. GPU submissions, Home active
PID, a desktop mirror, or CoreAudio initialization cannot prove those outcomes.
Estimated Index skeletal input remains labelled estimated. No display settings,
calibration, audio route or sibling runtime changes are prescribed by this
snapshot. Exact preparation commands and signal interpretation remain in
`PHYSICAL-READINESS.md`.
