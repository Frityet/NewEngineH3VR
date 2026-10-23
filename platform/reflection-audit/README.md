# Assembly rename and save reflection audit

The audit did not find a concrete base-game reflection/save failure caused by
`Assembly-CSharp` → `NativeH3VR.Game` or the FirstPass/UnityScript renames.
Evidence is in `build/reflection-audit/summary.json`, with SHA-256-bound managed
inputs, detailed IL call sites, source hits and imported-asset counts alongside it.

- The retargeter rewrites assembly references and old-name IL strings. Original and
  final selected game/support assemblies contain zero old-name IL string literals.
  The six game `Type.GetType` calls belong to LIV and use unqualified names: legacy
  global names are absent in both builds and their `Valve.VR` fallbacks exist in both.
- All 472 original serialized old assembly names occur in Unity 5 UnityEvent
  `m_TypeName` fields, across 53 scene/prefab assets. Current imported binary files
  contain none of these old names. No old assembly-qualified strings were found in
  the exported asset/controller/JSON inventory or shipped StreamingAssets text.
- Ordinary ES2 saves use type hashes from `Type.ToString()`, not assembly-qualified
  names. All 35 inspected ES2 type-hash, initializer and custom serializer methods
  preserve their IL instruction/operand sequence. All 103 concrete ES2 generic type
  arguments are preserved. SavedGun, SavedGunComponent, OmniScore and the other
  custom serializers use direct type/member references. Their Resources initializer
  prefab exists and was already rebound by the normal MonoScript migration.
- ES2's optional AutoSave `assemblyQualifiedTypeName` property uses `Type.GetType`;
  no such fields occur in the original scene/prefab/asset inventory. This optional
  format would need explicit migration if supplied by external content later.
- UGC/save JSON uses the library's default `TypeNameHandling.None` and direct custom
  converters. No base-game code enables assembly-qualified `$type` resolution.
  The Newtonsoft default binder can resolve assembly strings when explicitly
  enabled, but it is not exercised by these base-game settings.
- All 234 post-processing custom-order arrays are empty, so the assembly-qualified
  custom effect ordering path has no persisted old-name entries to lose.

`NativePortBuild.Build` now explicitly disables managed stripping with the Unity
2021 API immediately after selecting Mono, preserving reflection-discovered types
and members. The canonical helper compiles with zero warnings/errors. No full
project files, original game files, or user save data were modified for this audit.
This audit does not claim full save/load gameplay acceptance.

Reproduce managed metadata scan:

```sh
dotnet run --project platform/reflection-audit/Audit.csproj -c Release -- \
  recovered/AuxiliaryFiles/GameAssemblies build/composed/final \
  build/reflection-audit/managed.json
```
