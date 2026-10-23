# Unity 2020 UI runtime replacement

The recovered Unity 5.6 UGUI `Text.OnPopulateMesh` indexes a generated text vertex before checking whether the list is empty. Unity 2020 can return no vertices for empty text, so menu labels raised `ArgumentOutOfRangeException`. Unity 2020's matching official UGUI implementation handles this contract and also removes the obsolete trailing-quad assumption.

`runtime/UnityEngine.UI.csproj` compiles all 85 runtime C# sources directly from the **installed Unity 2020.3.49f1 editor's bundled `com.unity.ugui` package**, without modifying vendor sources. This is the runtime build (no `UNITY_EDITOR`), with physics, physics2D, animation and tilemap package defines matching the recovered main project. Assembly identity remains `UnityEngine.UI, Version=1.0.0.0`. No editor DLL is referenced. No modern package should be added alongside this DLL.

Run from the repository root:

```sh
platform/ugui/build.sh
platform/ugui/test.sh
```

Stage `build/ugui/runtime/UnityEngine.UI.dll` over the old working project's UI DLL, **retaining its existing `.meta`** (GUID `ed9b1d1b51bdfb25cebd363a2869dd80`). Include the replacement in subsequent managed composition. Main Unity project and installed game inputs are not changed by these scripts.

Verification:

- `build/ugui/composed/verification.json`: original game plus replacement resolves 1,980 type references, 99,234 engine/compat method accesses and 14,766 fields, with zero errors.
- `serialization-audit.json`: zero removed or changed serialized fields; only obsolete compiler-generated iterator/helper types disappeared.
- `old-script-ids.txt` and `new-script-ids.txt`: all 41 MonoScript type/GUID/fileID triples are identical, measured by Unity's AssetDatabase. The probe saves a scene containing the old DLL's Text component, replaces only DLL bytes, and reloads that scene successfully.
- `result.json`, `player.log`, `text.png`: native ARM64 Mono/Metal player exercises seven text cases (empty, whitespace, normal text, line breaks, rich text, trailing newline and overflow), then checks rendered glyph vertices and central screenshot pixels. These validate text and preserved scene references, not every UGUI interaction or game scene.
- `provenance.json`: individual official-source SHA256s, bundled package metadata, output hash and empty source-modifications list.

The first game's separate `MainMenuScreen.Start` null reference dereferenced `SteamVR.instance` after the action manifest bootstrap failed. It is a VR startup issue, not repaired by replacing UGUI. The later startup log inspected during this task contained only the three old-UI text exceptions; Steam initialized successfully.
