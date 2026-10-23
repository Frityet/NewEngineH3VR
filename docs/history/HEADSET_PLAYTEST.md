# Native headset MVP — 23 October 2026

**Result: playable indoor-range MVP confirmed by the user on the Valve Index.**
The app is the native ARM64 Unity 2021/Metal player, using the pinned runtime from
`~/Projects/ValveIndexMacOS`, without Wine.

The user confirmed:

- The range is visible in the headset and the visuals look fine.
- Both controllers align correctly after the reference-space fix.
- They spawned a 1911 and shot a target.
- Gunshots were audible through the headset and controller vibration worked.

The original attempt rendered correctly but displaced/rotated both controllers.
The controller adapter was mixing floor-adjusted broker coordinates with rendered
eyes in OpenXR STAGE. The fix locates controllers through the same session and
frame time as the eyes. It preserves the game's raw controller origin by undoing
the runtime grip offset in the action space. Calibration and game content were
preserved.

The updated native bridges passed synthetic pose, shifted-origin, focus, haptic,
ABI and Metal submission tests. Packaging passed the signature/dependency audit
for 25 ARM64 binaries and 125 managed assemblies; all managed hashes are unchanged.
The content gate revalidated 18 bundles and 4,387 original addresses.

Evidence:

- `build/headset-demo/controller-space-fix/verification.json`
- `build/headset-demo/controller-space-fix/accepted-player.log` — preserved live run `headset-demo-20261023-125409`, PID 98790
- `build/headset-controller-build.log`
- `build/headset-controller-package.log`
- `build/headset-controller-content-check.log`
- `build/headset-demo/controller-space-fix/before-player.log` — initial failure report

This is one physical indoor-range playtest. It does not establish full game
parity, every reload/action, or sustained performance. The brighter desktop mirror
remains a separate visual issue. The demo was left running for continued play.

Eleven obsolete empty synthetic-session directories were removed. The native fix
kept only small rollback files and did not duplicate the full game app. About
54 GiB of disk space remained at the final check.

## Subsequent wrist-menu, quickbelt and Take and Hold playtest

After the gameplay head-transform fix, the user accepted the wrist-menu and
quickbelt test and reported: "honestly everything works I was even playing take
and hold, seems like everything is good now".

The preserved live log for `headset-demo-20261023-132029`, PID 3086, records two
original Take and Hold hold-completion calls. A scan found no runtime exceptions,
logged Unity errors, failed loads or missing-script warnings. The player has
exited, and the installed signed app still matches the tested inventory.

See `PLAYER_SYSTEMS.md` and `build/player-systems/physical-acceptance/` for the
current checkpoint. Unlike the earlier controller-space fix, this update changes
two managed extension DLLs while preserving the original content bundles. It
establishes the user's tested session, not exhaustive coverage of every scene,
weapon or locomotion mode.
