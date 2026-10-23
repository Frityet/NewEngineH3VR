# Headset refresh targeting — 23 October 2026

The game previously used the bundled OpenXR runtime's fixed 90 Hz default. A
read-only query of the connected Index reports 2880 × 1600 at 144 Hz. Unity's
original SteamVR loop already disables desktop VSync and its own frame cap;
`xrWaitFrame` supplies the rendering cadence. The original Index setup also
derives the physics step from the runtime's reported frequency.

The game-owned runtime now reads the matching headset's current mode once at
session startup. It prefers the attached native HMD, supports extended-display
readback, and preserves fractional refresh rates. Unknown/invalid display rates
fall back to 90 Hz with an explicit log marker. Synthetic sessions retain their
90 Hz default. An explicit `VR_OPENXR_TARGET_HZ` override accepts 80, 90, 120 or
144 for diagnostics; normal launches need no override. Restart the game after a
headset mode change.

Only read-only mode getters are used. No physical display mode is selected or
changed. The source snapshot and the sibling Virtual Reality project remain
unchanged; `native/runtime/headset-refresh.patch` carries the narrow runtime
changes, including the existing guarded macOS 27 mode-reader implementation.

## Verification

- Refresh selection checks cover native/extended displays, 80/90/120/144 Hz,
  fractional rates, invalid telemetry, missing/wrong/disconnected displays,
  native-output precedence, explicit overrides and synthetic isolation.
- Actual Metal/OpenXR bridge tests pass at the default rate and all four explicit
  rates, including stereo pixels, controller reference spaces, focus/input
  preservation and session restart. Evidence: `build/refresh-rate-build.log`.
- The full player's original wrist-menu and quickbelt acceptance test passes at
  144 Hz. It reports `Index detected with 144 frequency` and a 0.006944444-second
  physics step. All 697 recorded frame waits request a 6,944,444 ns display
  period. Evidence: `build/refresh-rate/player-144/` and `player-144-frames.csv`.
- The current-mode readback and actual live player startup both select 144 Hz
  without an override. The live player also reports a 6.94 ms physics step.
  This run is **not a clean headset playtest**: it later logged `eye views invalid
  (-1017)`. Immediately afterward Virtual Reality was no longer running and
  `indexctl status` reported that its tracking service was unavailable. The
  tracking failure is preserved in `build/refresh-rate/live-auto/result.json`;
  it has not been attributed to or repaired by the refresh-rate change.

This verifies the requested frame cadence, not sustained 144 FPS or physical
scanout timing. The separate resident Virtual Reality Home compositor still has
its own frame pacing; this game-local change does not update the running Home
process. Rendering cost and compositor throughput can limit delivered frames.

## Build and installation

```sh
python3 native/build_refresh_runtime.py
python3 native/install_refresh_runtime.py
```

The build verifies the immutable source snapshot, applies the patch in a generated
directory, builds the ARM64 runtime, and runs the selection and native regressions.
The installer requires the game to be closed, checks candidate hashes, keeps small
rollback copies, updates the game and both Unity staging copies, and verifies the
signed package. It automatically rolls back if a package check fails.

All 125 managed assemblies, original content bundles and the Unity player remain
unchanged. The app contains the updated OpenXR library and a refreshed launcher
signature. Build hashes and installation receipts are in `build/refresh-rate/`;
the current package audit remains `build/native-app-audit.json`.
