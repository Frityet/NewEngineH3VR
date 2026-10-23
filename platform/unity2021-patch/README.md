# Target Unity custom-reflection API

The composed game has exactly one Unity 2020→2021 API mismatch:
`FistVR.PhotoStudioControlPanel.UpdateAll` calls
`RenderSettings.set_customReflection(Cubemap)`. Unity 2021.3.45f2 exposes the
same setter with a `Texture` argument. A Cubemap is a Texture, so the patch
imports the actual selected engine method without changing the object or its
behavior. Unity 2020 retains its original setter unchanged.

Run the patch on the fresh composed Game assembly, with a different output
path and the target `Unity.app/Contents` directory. It requires exactly one
original call and refuses ambiguous or already-patched input. The manifest
records the exact original and replacement signatures.

`script/compose_managed.py --unity-contents <path>` (or `UNITY_CONTENTS`) applies
this after all existing platform/VR/lifecycle patches. Target selection prefers `--unity-contents` / `UNITY_CONTENTS`, then
`UNITY_EDITOR`, then the locally extracted official Unity 2021 editor if present,
and finally the installed Unity 2020 editor. The patch inspects the selected
engine API and retains the old Cubemap setter unchanged for Unity 2020. A
separate Unity 2020-targeted patch run confirmed zero changed calls.
Composition now runs the metadata/API verifier against the selected target;
`audit-managed/verify` also accepts that directory as its optional second arg.

The Unity 2021 audit checks 1,991 type references, 99,277 engine/compat call sites
(4,778 distinct methods), and 14,782 fields, with zero unresolved references.
This is API linkage validation, not proof of full game behavior across engine
versions.
