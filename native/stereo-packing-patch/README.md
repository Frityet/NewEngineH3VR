# NewEngine separate-eye post-processing candidate

This documents the earlier getter-only candidate. Current XR source also has scope eye-pair lifetime hooks; prepare it with [the pair-aware tool](../scope-pair-patch/README.md). Existing getter-only candidate artifacts remain unchanged, and their verifier can still inspect those binaries independently.

The recovered post-processing policy getter is exactly `ldc.i4.1; ret` (packed single-pass stereo). NewEngine renders each eye to its own full-size target. The Unity 2021 adapter returns false only inside the existing `NativeEyeProjection.Begin` / `End` scope and preserves true outside it. This is an engine graphics adaptation; OptiH3VR CPU code remains shared and unchanged.

`NEWENGINEH3VR_PACKED_STEREO_FIX` is cached once per process: unset, empty, or `1` enables the correction; `0` retains the original constant-true policy everywhere for A/B comparison. Other values raise a configuration error. `NativeH3VR.NewEnginePostProcessing.Enabled` and `Describe()` expose the selected variant. AO and other authored effects remain enabled and unchanged. This candidate has not established a headset visual fix.

Prepare from a pristine game assembly, the currently accepted XR assembly, and Unity 2021 without installing or launching:

```sh
python3 native/prepare_stereo_packing.py \
  --input-game /absolute/path/to/pristine/NativeH3VR.Game.dll \
  --baseline-xr /absolute/path/to/accepted/NativeH3VR.XR.Managed.dll \
  --references /absolute/path/to/current/Managed \
  --unity-contents /absolute/path/to/Unity.app/Contents \
  --output build/new-engine-packed-stereo/pristine
```

The tool compiles the scoped helper with `NEWENGINE_UNITY_2021`, patches only the exact original getter, compares every other game method body and existing metadata, and proves all existing XR bodies and field layouts are unchanged. It records hashes, source hashes, compiler, references and engine version. Changed or already patched input is rejected. It adds one public scope accessor to the existing projection class and one helper type, without adding fields to existing types. Full composition uses the same patch class through `native/patch`.

Independently check an existing candidate without rewriting assemblies:

```sh
dotnet native/stereo-packing-patch/bin/Release/net10.0/StereoPackingPatch.dll \
  --verify INPUT_GAME CANDIDATE_XR CANDIDATE_GAME NEW_REPORT_JSON DEPENDENCY_DIRECTORY BASELINE_XR
```

The saved pristine game can predate unrelated accepted XR branding changes, so supply `BASELINE_XR` explicitly. The verifier preserves that accepted XR's existing bodies exactly. Method-only proof is semantic IL/metadata equality; it does not claim the rewritten PE file is otherwise byte-identical. ABI/serialized-layout checks and live graphics checks are separate acceptance gates.
