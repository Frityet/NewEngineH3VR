# Original missing-script baseline

Run the read-only audit after scene serialization has finished:

```sh
audit-assets/.venv/bin/python platform/scene-integrity/audit_missing_scripts.py
```

The report is `build/scene-integrity/missing-script-baseline.json`. The script
compares each original Steam `levelN` binary, its pristine AssetRipper scene YAML,
and the current working scene, accepting either binary or YAML serialization.
It writes only its report and never edits the game or project.

All 46 scenes passed the first audit. The original game contains 688 attached
MonoBehaviours with null script pointers: ArizonaTargets has 3,
ArizonaTargets_Night has 1, NewSnowGlobe has 683, and Cappocolosseum has 1. The
other 42 scenes have none. All original GameObject IDs, component IDs and
component indices survived export and Unity 2021 binary serialization unchanged.

Each `scenes[]` entry has `path`, `originalFile`, hashes of all three inputs,
`expectedMissingComponentCount`, and `baseline[]`. Every baseline item records
`gameObjectFileID`, `componentFileID`, `componentIndex`, `gameObjectName` and
`reason`. Component indices are zero-based positions in `GameObject.m_Component`
and therefore in `GetComponents<Component>()`, including the Transform.

Schema version 2 distinguishes `sceneGuid` (the expected working asset GUID)
from `pristineSceneGuid` (the exported GUID). AssetRipper reused baked occlusion
scene GUIDs for asset metadata, causing three duplicate groups among seven
scenes. `script/repair_scene_asset_guids.py` intentionally assigned new asset
GUIDs to four later scenes: both ModdingBlanks, Grillhouse_2Story_Night, and
TakeAndHoldDark. The audit requires the exact duplicate groups, the recorded
`build/scene-guid-repair.json` entries, and the independent deterministic
SHA-256-derived GUID for each changed path. It does not accept arbitrary current
metadata as the baseline. Original baked occlusion GUIDs must remain unchanged.

The expanded audit also verifies all GameObject ID sets and component-list
topology against the original binaries and pristine export. Across all 46 scenes
the only topology differences are removal of 30 class-ID 92 `GUILayer`
components; every remaining component retains its ID and order. No gameplay
MonoBehaviour was removed. Each difference is recorded with its owning object,
before/after component lists, removed IDs and original class IDs. Any other
component removal, addition, reordering or GameObject identity change fails the
audit. Scenes without original occlusion settings may gain an empty settings
object whose GUID is all zeros; this is recorded separately from baked data.

The Editor inspection should match each null component against its exact scene
path, `GlobalObjectId.GetGlobalObjectIdSlow(gameObject).targetObjectId`, and
component index. Each baseline entry must match exactly once. Reject any extra
missing component, unmatched baseline slot, or invalid lighting; retain the
serialized original component ID as an independent static audit check. Matching
only counts could conceal a newly missing component alongside a removed original
one. A non-null serialized script reference that fails to load is not covered by
this baseline and must fail inspection. Do not delete original null components.

This establishes that these gaps predate the port. It does not assert that the
original missing components implement any behavior or that every non-null script
successfully instantiates; the live Editor inspection remains necessary for that.

## Prefab preflight

`audit_prefab_missing_scripts.py` records all 8,293 pristine exported prefabs.
Only 17 have null-script slots (29 total), and all those slots match the working
prefabs. Unlike scene IDs, prefab IDs here are AssetRipper export IDs, so this
report explicitly identifies its evidence as the pristine export. It also
retains candidate prefab/GameObject matches for names in the failed bundle log;
names alone are not unique identities.

`script/NativeAssetIntegrity.cs` consumes
`build/scene-integrity/prefab-missing-script-baseline.json`, or an explicit
`NATIVEH3VR_PREFAB_BASELINE` path. `InspectPrefabs(string[] assetPaths)` includes
recursive prefab dependencies, verifies GUIDs, loads the actual Editor objects,
and permits a missing component only at its recorded GameObject fileID and slot.
Every allowed slot must be seen exactly once. It rejects unresolved previously
valid scripts, changed identities, and missing prefabs before bundle creation.
It writes `build/scene-integrity/prefab-editor-inspection.json` without changing
assets.


## Bundle content acceptance (schema 2)

Canonical Editor helpers are compiled for Unity 2021 by `EditorCheck.csproj`.
`NativeBundleBuild.Build` and `NativeBundleBuild.VerifyExisting` both invalidate old
markers before preflight, require the complete original prefab inventory, validate
managed scripts' macOS player importer compatibility, then load every planned
address with its manifest dependency closure. The latter entry point reuses
completed bundle bytes and does not invoke shader compilation or BuildAssetBundles.
Set `NATIVEH3VR_BUNDLE_PLAN` and `NATIVEH3VR_BUNDLE_OUTPUT` to the existing plan and
output directory before invoking it from the intended Unity project.
Loaded bundle objects are destroyed after each batch of at most 16 addresses;
source snapshots contain strings, not retained Unity object references.

Loaded prefab hierarchy/component types and source non-null serialized references
must survive. Internal prefab references use structural object/component identity;
pre-existing source nulls remain legal. Non-prefab main types and runtime subasset
inventories are compared. Editor-only hierarchy and authoring state graph objects
are excluded; runtime animation clip inventories are compared instead. The current
plan contains 4,378 prefab addresses, eight shaders and one shader variant collection.

The enhanced `native-bundle-content-verification.json` records the plan SHA-256,
managed input hashes, Unity source dependency hashes plus raw source/meta hashes,
and every bundle/manifest output hash. Input fingerprints are checked again after
content loading. The old five-field summary remains byte-schema compatible but
is only written after the enhanced proof succeeds.

`script/verify_native_bundles.py --directory <StreamingAssets> --project <project>
--plan <plan.json>` is a read-only staging/player-build gate. It requires both
markers, exact output inventory/counts and matching plan, managed, source and output
hashes. `stage_native_bundles.py` validates before moving any file and removes target
acceptance markers before moving, so interrupted staging cannot retain stale success.
The 17 temporary-fixture tests in `test_bundle_proof.py` cover valid staging, old
proof rejection, tampering, deleted/new source metadata, missing output inventory,
interrupted moves, missing or mismatched compiler receipts, dirty snapshots,
changed diagnostic policy, delayed compiler errors and replacement of the raw
build log. They do not exercise Unity deserialization.

Unity can return a non-null bundle manifest and exit successfully despite shader
compiler errors. The helper therefore captures reported errors and independently
scans the actual Unity log using `script/unity_compiler_errors.json`, also consumed
by `shaders/verify_shader_proof_logs.py` and the Python acceptance gate. A successful
compilation writes `native-bundle-compiler.log` and a hash-bound
`native-bundle-compiler-receipt.json` before content validation. The receipt binds
the run marker, policy, input hashes and every output hash. The enhanced proof
binds both receipt and snapshot. The original raw build log must remain clean and
retain the exact snapshot prefix, including its completed-build marker; later
compiler diagnostics invalidate acceptance even if an earlier snapshot was clean.

`Build` removes old compiler evidence before preflight. `VerifyExisting` requires
matching evidence and cannot certify older outputs that lack it. Give
`VerifyExisting` a distinct `-logFile`: overwriting the originating build log
invalidates its receipt. Keep that original log available through staging and
player acceptance; the copied snapshot alone is insufficient. A new intentional
build may reuse the original log path because it invalidates and recreates its
receipt.

Limits: same-name/type external asset references are not distinguished by original
file IDs in this check; scalar values and GPU/audio bytes have separate audits.
Prefab scripts are deserialized but never instantiated, so this does not test
Awake/Start, behavior, rendering, or physical VR. Current source expectations detect
bundle-generation loss, not pre-existing original-to-export losses. Actual execution
of the enhanced Editor verifier is required in addition to compilation and Python
gate tests before declaring a bundle build accepted.


## Final native application audit

`script/audit_native_app.py <app> --report <json>` reads all shipped files, identifies
Mach-O binaries by magic rather than extension, requires ARM64-only slices and strict
individual/app signatures, and rejects native/mixed Windows PE while accepting valid
IL-only CLR assemblies. It parses Mach-O load commands, rejects non-system absolute
external libraries, and resolves app-local symbolic dependencies to actual Mach-O
files. System framework/shared-cache references are allowed.

`package_native.py` changes only generated app copies: same-directory `@rpath`
dependencies become explicit `@loader_path` references, then every native binary
(including nonstandard StreamingAssets paths) and the containing app are ad-hoc
signed. A failed audit prevents packaging success. The report binds hashes for all
native/managed binaries, Info.plist, CodeResources, and any packaged content proof.
`check_saved_audit(app, report)` re-audits and requires the same inventory for a run.

The existing 6.6GB main-menu app was inspected read-only: 16 ARM64 binaries, 121
managed assemblies, no native Windows code, all signatures valid. Five symbolic
links depended on existing runtime preload order. A disposable 116MB copy of all
binaries passed after normalization; negative fixtures rejected native PE, Intel
Mach-O, new/tampered files, and an externally linked signed binary. Results are in
`build/native-app-audit-tests.json`; the original generated app was not modified.
The audit cannot infer dynamic dlopen/PInvoke strings and does not replace gameplay
or physical VR tests. The completed full app still needs its own packaging audit.


## Shipped scene script closure

`script/verify_player_scene_scripts.py` independently examines player scene files
with UnityPy and actual shipped DLL definitions with Mono.Cecil. It uses the
player's own BuildSettings scene order to locate each `levelN` file. Run it after
the player build/GI restoration, before packaging:

```sh
audit-assets/.venv/bin/python script/verify_player_scene_scripts.py \
  --player-data "dist/NewEngineH3VR.app/Contents/Resources/Data" \
  --project unity-full --require-full \
  --report build/scene-integrity/full-player-script-proof.json
```

Player object IDs are renumbered, so direct source/player fileID equality is
invalid. The audit requires unique root name/local-pose/ordered-subtree signatures,
then maps children by their preserved order. Root ambiguity fails rather than
inventing an identity. Source EditorOnly subtrees are explicitly excluded. All
remaining GameObjects must map exactly once, with identical ordered component
class/script-type/null-slot sequences. Source nulls must first match the exact
original GO fileID/component fileID/slot baseline. Any removed original null slot
or newly missing component fails the strict comparison.

Source MonoScripts resolve through the preserved GUID/fileID migration bindings.
Player MonoScripts resolve from actual serialized PPtrs; their assembly-qualified
class identities must match source and exist in the actual shipped managed DLLs.
The report hashes the baseline, bindings, source/player scenes, player BuildSettings
and the full managed type inventory. `--require-full` requires the exact original
46-scene path set; without it, a known subset supports the existing boot app.
The report is invalidated before a new run and success is never based on an empty
scene list.

The untouched old main-menu player passes: 2,826 uniquely mapped GameObjects,
77 distinct script types, no unexpected nulls. Nine in-memory negative/positive
fixtures verify renumbered IDs with an exact original null, new null rejection,
missing shipped type, same-named classes from the wrong assembly, relocated original
null slots, removed/changed components, unbaselined source nulls and ambiguous roots. Evidence: `build/scene-integrity/old-menu-player-script-proof.json`
and `build/scene-integrity/player-scene-script-tests.log`. Full 46-scene execution
is still required on the new player. This checks serialized script closure and
component topology, not script execution or unchanged arbitrary field values.
