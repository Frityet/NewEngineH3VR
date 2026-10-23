# Native session focus and input rearming

The native bridge now records actual OpenXR session-state events and exports
`nh3vr_xr_focused()`. Running or visible alone does not imply input focus. The
export returns zero outside an active focused session. H3VR also explicitly
selects scene-client mode (`VI_OPENXR_HOME=0`) for both synthetic and live starts,
so an inherited Home-owner flag cannot make the game claim the resident role.

The raw broker input adapter gates controls on this state, while preserving
connection, valid device poses and velocities. Unfocused samples lose only
`InputCurrent`, buttons, contacts and analog channels. The original action
queries therefore produce no trigger/grip input or estimated finger summary;
the existing inactive-summary reset prevents a latched grab. Haptics are rejected
without focus, and `CVRSystem.IsInputAvailable` follows the same focus state.

After startup or focus loss, each hand requires a current neutral control sample
before rearming: no pressed buttons and trigger/grip/trackpad force magnitudes
below 0.05. Controls held while returning from the dashboard cannot become new
gameplay presses. Pose tracking is still retained while a hand awaits release.
Finger proximity alone does not block rearming; it also cannot initiate a grab
without a trigger/grip press. This is deliberate input-transition policy, not
claimed reproduction of proprietary SteamVR force thresholds.

## Regression evidence

- `build/native-focus-build.log`: compiled ARM64 native bridge and passed its
  tracking, ABI, stereo/GPU, pixel and lifecycle tests. The focus test acquires
  only `display-owner.lock` inside the bridge-created private synthetic directory.
  The unchanged pinned runtime emits actual FOCUSED → VISIBLE → FOCUSED events.
  Head pose remains valid, haptics fail while unfocused and resume after focus,
  and stopping clears focus. No physical Home endpoint, display or USB state is
  touched.
- `vr-probe/output/game-verification.json` and `game-player.log`: actual ARM64
  Unity player passes existing scene handoff, camera callbacks, fades, original
  action polling and original FVRViveHand Index hold/release tests with the new
  adapter. The additional focus fixture drives the same production snapshot
  filter and queries the original action contracts: held trigger/grip release
  on loss; valid pose retained; held controls stay inactive when focus returns;
  neutral then a new press/release yields exactly one edge. This deterministic
  input fixture does not claim a physical dashboard/controller test.

Reproduce without hardware:

```
python3 native/build.py --runtime .tools/native-vr-runtime-snapshot --test
python3 native/build_managed.py --input build/composed/base --output native/build/ported-vr-focus
# Stage the new adapter/XR managed/native libraries only into the isolated vr-probe.
NATIVEH3VR_DIAGNOSTIC_INPUT=1 python3 native/test_unity.py --game --refresh-scripts
```

Full-project assets were not changed. Root composition must rebuild from these
sources, stage the XR plugin together with both managed bridge DLLs, and rebuild
the player; an old native library lacks the new focus export. The full gameplay
acceptance route must then be rerun before physical testing.
