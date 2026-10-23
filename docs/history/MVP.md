# NewEngineH3VR headset demo

The immediate target is the original indoor range on the Valve Index, using the
native ARM64 Unity player, Metal and the runtime from `~/Projects/ValveIndexMacOS`.
The headset MVP passed its first physical playtest on 23 October 2026. The user
confirmed good visuals, correct controller alignment, spawning a 1911 and shooting
a target, audible headset gunshots and controller vibration. See
[HEADSET_PLAYTEST.md](HEADSET_PLAYTEST.md) for the bounded acceptance record.

The current wrist-menu/quickbelt checkpoint keeps the gameplay head transform
tracked between eye renders. Original menu clicks and quickbelt store/retrieve
passed synthetic checks and the user accepted the physical retest, reporting
successful Take and Hold gameplay as well. See
[PLAYER_SYSTEMS.md](PLAYER_SYSTEMS.md) for this update's separate evidence.

Those physical acceptance records describe the earlier tested checkpoints.
The current renamed package includes OptiH3VR optimizations; its scoped checks
and remaining physical/performance validation are tracked separately in
[OPTIH3VR.md](OPTIH3VR.md).

The controller issue exposed a reference-space mismatch: rendered eyes used the
OpenXR session's STAGE origin, while controller poses only subtracted floor height
from raw broker coordinates. The native bridge now locates controllers in the
same STAGE and at the same frame time as the eyes, cancelling the runtime's grip
offset to preserve H3VR's original `pose/raw` binding. No fixed 180-degree offset
or calibration change was applied. Native synthetic checks cover ordinary poses,
a shifted session origin, focus changes and input preservation. The user confirmed
the corrected alignment in the physical retest; evidence is in `build/headset-demo/controller-space-fix/` and
`build/headset-controller-build.log`.

## Start the demo

The current app is `dist/NewEngineH3VR.app`, with the `NewEngineH3VR` launcher
and preserved `NewEngineH3VRPlayer` Unity executable. The script below uses this
package and retains the original game/save identities.

1. Open **Virtual Reality**, start hardware tracking and open Home for headset output.
2. In **Setup → Floor height**, set the floor from a measured headset height. For
   **Set while wearing**, enable Spoken instructions and follow the countdown.
3. Wake both Index controllers in view of the base stations.
4. From this project directory, run:

   ```sh
   ./script/run_headset_demo.sh
   ```

The launcher checks live tracking and the measured floor, then enters
`IndoorRange_Updated` through the game's original scene loader. Use the original
range item spawner and Index controls to select, grab, load and fire a gun. The
demo keeps running for player interaction; quit the app normally when finished.

To check tracking without opening the game:

```sh
./script/run_headset_demo.sh --check
```

The game log is `build/headset-demo/player.log`. A successful range startup writes
`NATIVE_HEADSET_DEMO_READY` with `tracking=live`. That marker establishes submitted
stereo frames, not a human check of the headset image or controller behavior.

## Validation

The fresh build passed both compiler gates, all 46 scene-integrity checks and the
package audit (25 ARM64 native binaries, 125 managed assemblies). All 18 bundles,
4,387 original asset addresses and 474,497 references passed verification.

| Check | Result and evidence |
| --- | --- |
| Original menu | Clean 30-second observation and exit; `build/player-attempts/mvp-menu/` |
| Actual demo startup | Entered `IndoorRange_Updated`, submitted stereo frames and completed a 60-second observation with no controller input fixture; `build/player-attempts/mvp-demo/` |
| Original M1911 interaction | Original Anvil load, grip/hold, one shot with a moving ballistic projectile and open-hand release; `build/player-attempts/mvp-m1911-current/` |
| Capture and runner checks | 29 tests pass. Stereo sidecars bind the image to the player process and a frame after camera handoff; `build/mvp-capture-checks.json` |

The three automated runs above used isolated synthetic tracking, reported zero physical-headset frame
submissions, and exited cleanly. `build/mvp-summary.json` binds the results to the
build evidence. These checks do not establish physical headset operation.

Visual review of the menu, range and held M1911 stereo captures found the prior
severe green/cyan corruption absent. The desktop mirror is still visibly brighter
and more washed out than the stereo capture. Full pixel equivalence is unproven.
The first M1911 run's periodic stereo image was from the scene transition; it is
retained in `build/player-attempts/mvp-m1911/` and rejected as range visual evidence.
The replacement run captures after handoff and shows the firearm in both eyes.

Previous diagnostic builds also completed grab/fire/release with AKM,
WebleyRevolver, MosinM1891 and MB500Chopped. Those builds retained the old color
path; those additional firearm mechanisms have not been repeated in this build.

The new build integrates the recovered shader uniform-swizzle repair, calibrated
eye projections, the thread-safe video PCM backend and the current pinned native
runtime. The demo does not automatically operate the player's hands or weapons.
The gameplay acceptance command separately enables a synthetic controller fixture.

## Physical acceptance and remaining scope

The user accepted the headset visuals and controller alignment and successfully
spawned and fired a 1911 at a target, with audio and vibration. This establishes
the requested playable indoor-range MVP. It is not a full-game parity or extended
performance/stability test; additional firearm mechanisms and reload workflows
remain future validation.

The physical session started with a saved measured floor and fresh tracking for
the headset and both controllers. An unset floor prevents this runtime from
creating H3VR's standing reference space. Room dimensions are separate: no
dimensions were invented, and play-area bounds remain unavailable until configured
in the runtime.

Broader scene parity, Vault validation and the known convex collision warnings in
some other scenes are outside this demo milestone. Original game files and runtime
calibration have not been changed.

Obsolete generated apps, inactive probe caches and rejected partial bundles were
removed while preserving sources and proof logs. The native controller fix updates
two small plugins and retains rollback copies, without duplicating the full app.
