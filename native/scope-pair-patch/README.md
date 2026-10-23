# NewEngine scope target lifetime candidate

This engine graphics integration retains scope activation over a complete native left/right eye pair. `PIPScope.UpdateAllScopes` still makes its original selection, visibility, projection and render decisions separately for each eye. Three exact `Behaviour.enabled` setter calls are adapted: activation happens immediately; deactivation waits until pair completion and occurs only for scopes selected by neither eye. Ordinary disable/destroy callbacks and option-triggered texture recreation remain intact.

The exact EC79A2 fixture alternated enabled/created target on the left and disabled/released target on the right. Its `OnDisable` calls `Release`/`Destroy`, and the next `OnEnable` creates a new target. This candidate tests and addresses that lifetime churn. A separate fixture with a target retained for both eyes still produced a black image, so this is **not an established fix for the black optic**.

`NEWENGINEH3VR_SCOPE_PAIR_FIX` is cached per process: unset, empty or `1` enables pair activation; `0` preserves original per-eye assignments; other values raise a configuration error. `NativeH3VR.NewEngineScopeActivation.Enabled`, `PairActive` and `Describe()` expose the variant/state. Outside a pair, assignments behave exactly as before. The renderer closes the pair in `finally`, including rendering failures. Destroyed queued objects are skipped during cleanup; callback failures do not prevent remaining queued objects from being cleaned up.

Prepare a separate candidate without installing or launching:

```sh
python3 native/prepare_scope_pair.py \
  --input-game /absolute/path/to/pristine/NativeH3VR.Game.dll \
  --baseline-xr /absolute/path/to/accepted/NativeH3VR.XR.Managed.dll \
  --references /absolute/path/to/current/Managed \
  --unity-contents /absolute/path/to/Unity.app/Contents \
  --output build/new-engine-scope-pair/pristine
```

The candidate also contains the existing scoped packed-stereo getter correction. Its independent `--verify` mode follows the same argument contract as the previous getter-only tool, through `native/scope-pair-patch/bin/Release/net10.0/ScopePairPatch.dll`. The Game whitelist is exactly two methods: the getter and three instructions in `PIPScope.UpdateAllScopes` (331, 374, 580). Original scope method IL is fingerprinted and changed/repeated input is rejected. Every other game method body, serialized field/layout and existing metadata is checked.

The XR whitelist allows two new helper types, the projection scope accessor, and exactly four instructions plus one bounded finally region in the existing `Start` iterator. All 433 prior iterator instructions must map unchanged, and the before/after body fingerprints are pinned. Every other existing XR body and existing field/layout/signature is checked. Tests model lifecycle callbacks and measure warmed host allocations; actual Unity graphics images and headset results require separate runtime validation. CPU optimization algorithms remain in OptiH3VR.
