# Wrist menu and quickbelt checkpoint — 23 October 2026

The native player now keeps the original gameplay eye-camera transform at the
tracked centre-head pose between stereo renders. Legacy SteamVR disables its old
tracked-head child and expects Unity XR to move the camera. Previously our native
renderer applied eye poses only while rendering, then restored a stale transform.
The original wrist menu, player body and quickbelt therefore read the wrong head
reference even when the headset image followed the user.

The fix updates that camera during Update, FixedUpdate, camera binding and frame
submission. Original wrist-menu and quickbelt logic, assets and saved options are
retained. The user has accepted this checkpoint and reports successful Take and
Hold gameplay; no additional locomotion change was needed for that session.

## Automated verification

`python3 script/run_native.py --player-systems --output-dir build/player-systems/attempt-02`
passed with no unresolved log errors and a clean exit. The isolated synthetic
fixture used the original scene loader, hand poses, grip/trigger input and menu
colliders to verify:

- Gameplay EyeCam agrees with the native head pose outside eye rendering.
- Presenting an empty wrist opens the original wrist menu.
- The opposite hand hovers and clicks the original MoveMode section button.
- Turning the wrist away closes the menu.
- The original range creates 13 quickbelt slots.
- An original Anvil-loaded M1911 is grabbed, stored, retrieved and stored again
  through the original hand and quickbelt code.

The fixture does not call menu activation/click or inventory interaction methods
directly. The first attempt exposed a test-driver timing issue: a one-frame
trigger assertion ran before input polling. The passing attempt holds input until
the original game reports the result and waits for stable slot hover before grip
edges. Neither fixture changes player options or hardware calibration.

Evidence: `build/player-systems/attempt-02/native-player-systems-run.json` and its
player log. Stereo capture belongs to the same process at frame 480, after camera
handoff at frame 179, with zero physical-headset submissions. This is synthetic
interaction evidence, not physical acceptance or proof of full gameplay parity.

## Packaging

Only `NativeH3VR.XR.Managed.dll` and `NativeH3VR.GameplayProbe.dll` were replaced in
the generated app. Existing type/field layouts and method signatures passed the
Cecil extension contract check; its negative fixtures reject field and type
removal and field additions. The signed package still passes the architecture,
dependency and plugin audit for 25 ARM64 binaries and 125 managed assemblies.

The Unity project's DLLs and content proof remain a frozen build baseline. The
runtime extensions differ from that proof's `managedInputs`; the old proof is not
being presented as fresh verification of the new runtime code. All 18 bundle
payloads remain unchanged. Update receipts, small rollback DLLs and current audit
bindings are in `build/player-systems/update-01/` and `update-02/`.

## Headset retest

The live range was reopened as `headset-demo-20261023-132029`, PID 3086, and reached
`NATIVE_HEADSET_DEMO_READY` with live tracking and player-driven controls. Headset,
both controllers and measured floor passed preflight. **The user accepted the
wrist-menu and quickbelt test and also reported playing Take and Hold:**

> honestly everything works I was even playing take and hold, seems like everything is good now

The same live session log records two `TNH_HoldPoint.CompleteHold` calls, with no
runtime exceptions, logged Unity errors, failed loads or missing-script warnings
found in the log scan. The player has since exited. The installed app still
matches the accepted signed binary inventory
`1f698d56fc883813b840741a8505ad13aadb125677be69aa33d89d5bef491cae`.

The log, audit and user acceptance are preserved in
`build/player-systems/physical-acceptance/`. This establishes the tested wrist
menu, quickbelt and Take and Hold session. Individual locomotion modes, every
scene/firearm and long-duration stability have not been exhaustively tested.

The earlier accepted shooting/audio/haptics playtest remains documented in
`HEADSET_PLAYTEST.md`; its log is preserved as
`build/headset-demo/controller-space-fix/accepted-player.log`.

No duplicate full app was created. About 54 GiB remained free after installation;
synthetic test processes exited and only small DLL backups/logs/captures remain.
