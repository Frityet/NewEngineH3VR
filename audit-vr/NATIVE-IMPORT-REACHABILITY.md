# Non-OpenVR native import reachability

The current composed DLLs were inspected with Cecil, including nested iterator
types. Evidence is `build/optional-hardware/native-import-audit.json`, containing
assembly hashes, every import's direct callers and callers of those wrappers,
Windows-media boundary calls and selected exact IL bodies. This is a static
original-content audit, not a claim that every runtime path was exercised.

| Import group | Current composed behavior | Action |
|---|---|---|
| `kernel32.dll` / `GetShortPathNameW` | Import metadata remains, but it has **zero callers**. `Helper.ConvertLongPathToShortDOS83Path` contains exactly `ldarg.0; ret`. Long paths therefore retain native POSIX spelling. | Already correctly handled by the existing video patch; no new patch. |
| `AVProVideo` / `AVProVideoWinRT` | 79 + 40 imports remain in legacy player classes. The only four calls into those classes from outside them are in private static `MediaPlayer.CreateMediaPlayer(OptionsWindows)`, which has **zero callers**. The live virtual no-argument factory contains only `new UnityMediaPlayer; ret`. Application quit has both Windows deinit calls removed. | No reachable old video-native call identified. No need to JIT-isolate unreachable player implementations. |
| `sdkencryptedappticket` | Eight imports each have one SDK wrapper; all eight wrappers have **zero callers** across the composed DLLs. No original recovered game caller was found either. These are separate from normal CSteamworks account ownership and SteamUser APIs. | Unused by original game. Not implemented or falsely reported working. |
| `ForceTubeVR_API_x32/x64` | Fourteen native calls now sit behind private non-inline thunks. Existing macOS guard returns for optional commands and rejects data queries. | Concrete eager-JIT issue fixed and native Mono fixture proved zero unavailable-path import attempts; see `platform/optional-hardware/README.md`. |
| `LIV_MR` | Five imports remain in SharedTextureProtocol. Its getters/output/disable methods have macOS guards, and its coroutine calls GetRenderEventFunc. Direct native calls remain in those guarded methods, so invoking them could reproduce eager resolution. However the original-content graph does not reach them on macOS, as detailed below. | No additional patch for an unobserved/unreachable original route. Optional mixed-reality capture remains unavailable. |
| `NativeH3VRFMOD`, `CSteamworks`, `NativeH3VRXR`, `NativeH3VRTracking` | These are intentional native macOS boundaries, with 548, 850, 18 and 2 imports respectively in this composition. | Existing ABI/export/native runtime validations apply; this scan alone does not retest them. |

The scan excludes Unity/framework OS portability concerns by reading only the
composed game/plugin DLL directory. The Unity player supplies its own native
framework and managed base class implementations.

## LIV control flow

The original export contains one LIV component asset, `Assets/GameObject/LIV.prefab`,
and zero serialized SharedTextureProtocol, MixedRealityRender or ExternalCamera
components. Their exact script-GUID search result is recorded in
`build/optional-hardware/liv-serialized-reachability.json`.

The current `LIV.OnEnable` guard calls `LIVUnavailable`, then disables the
component and returns on macOS. Its guarded `OnDisable` also returns before
the original cleanup branch. Thus the original `Update → IsActive →
OnCompositorActivated` route does not run, and the methods that dynamically add
SharedTextureProtocol/MixedRealityRender are not reached. This agrees with the
previous actual player log: LIV produced an explicit feature-unavailable warning
and no LIV_MR fallback-load attempt.

This does **not** mean a method guard alone cures native JIT resolution. A mod or
external caller invoking SharedTextureProtocol getters directly could encounter
the same eager-resolution limitation demonstrated for ForceTube. Such an
invocation is outside the recovered original-content route and is not presented
as supported. No dummy capture dimensions, texture handles or availability
success were added.

Regenerate the binary evidence with:

```
dotnet run --project audit-vr/native-imports/Imports.csproj -- build/composed/final build/optional-hardware/native-import-audit.json
```

No composition inputs or full-project files were changed during this audit.
