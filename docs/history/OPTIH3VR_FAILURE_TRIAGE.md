# NewEngineH3VR scene failure triage

The completed optimized sweep observed all 46 original scenes: **30 passed strict
scene observation and 16 failed**. All 16 failing scenes were replayed with
OptiH3VR disabled; all 16 failed again. Fifteen pairs have identical deduplicated
issue lists. The checked exception pairs also retain the same failing method and
IL offset. These are baseline runtime failures in the tested native package,
with source provenance established for the authored defects described below.

`Grillhouse_2Story` is the exception to identical cause: its optimized attempt
stopped on an audio-device reset in the menu, while its disabled attempt reached
convex bush-mesh cooking warnings. Both failed, but these observations do not
establish that its optimized audio failure is reproduced or caused by the mod.

No failure is converted into a passing scene. Thirty passing load/render/capture
routes do not establish complete gameplay across every mode, weapon, enemy,
interaction or physical-headset condition. Full-gameplay parity remains
unresolved.

## Completed evidence and package binding

The machine-readable differential is
`build/optih3vr/scene-failure-differential.json`. Original optimized evidence is
under `build/optih3vr/all-scenes/`; the 16 disabled replays are under
`build/optih3vr/failed-scenes-baseline/`. Both matrix summaries are complete.
Historical records use `dist/NativeH3VR Experimental.app`; the public app is now
named NewEngineH3VR. Historical paths and runtime marker names remain unchanged
as evidence identities.

All 62 run records have distinct run IDs, ARM64 architecture, capture policy
`required`, verified process exit, zero cleanup errors and no remaining tracked
PIDs. This policy does not imply that a failed run produced accepted captures. All 62
raw player-log SHA-256 values were rechecked against their result JSON and match.
Every requested optimization state passed its process-bound configuration check.
Each optimized/disabled pair records the same executable and managed/native
binary inventory, so replay changes the selector within the tested package.

| Recorded identity shared by all 62 runs | SHA-256 |
|---|---|
| Player executable | `8b5d9fbd5f19ac3733a6a43c346cfa4777795ba6bcc86e6d544a0594e1a7891c` |
| Binary inventory (25 native, 126 managed) | `b7e7dad00d8b9cf83ff23827c1b0e72346e7c3bfbdc7ae5c0b10d9d8c87131f3` |
| Recorded application-audit receipt | `2dce3af1aed20e8cf0eb4369059233afebcaeef464f713d91f70367553eb9bb7` |

The runner's issue lists are deduplicated message lists, not complete warning
multiplicity or proof that every later code path is equivalent. The supervisor
stops at the first unresolved load/runtime issue; later issues may be unobserved.

| Reproduced failure group | Scenes | Disabled replay |
|---|---|---|
| Missing-script load/prefab warnings | Testing3_LaserSword; Friendly45_New | Same issue lists |
| Invalid authored ladder or Anvil reference | BreachAndClear_TestScene1; WinterWasteland | Same issue lists and exception method/offset |
| AnimalNoiseMaker partial convex hull | MeatGrinder; Wurstwurld1; SamplerPlatter; RotWienersStagingScene; ReturnOfTheRotwieners; MF2_MainScene; BBQRange | Same issue lists |
| Boomskee chemical partial convex hulls | Boomskee | Same issue list |
| Template supply-sequence index | ModBlank_Take&Hold | Same issue list and exception stack/offsets |
| Invalid authored preview UI references | Institution_Preview | Same issue lists and exception stacks/offsets |
| Bush partial convex hulls | Grillhouse_2Story_Night | Same issue list |
| Menu audio reset / scene bush cooking | Grillhouse_2Story | Different observed issues; both fail |

## Testing3_LaserSword

Observed: missing-script warnings appear during delayed original
`Anvil.AnvilPrefabSpawn.InstantiateAndZero`, including `Cyber_Handgun_Magazine`,
empty-name and `<null>` objects. The supervisor stops before completed camera
handoff/scene observation, so captures do not establish scene acceptance.

Original evidence: the pristine prefab
`recovered-full/ExportedProject/Assets/weaponry/handguns/magazines/Cyber_Handgun_Magazine.prefab`
contains GameObject `1709254077376921`, component index 4 / local component
`114634108722742392`, whose `m_Script` is `{fileID: 0}`. The existing successful
prefab baseline records this exact null and `workingIdentitiesMatch: true`,
GUID `8c0f71e9fc940664e9557eb95adb55c7`, pristine SHA-256
`cf4f5b3cb827262c5c101a3f75fb2ff7c6db975d0fef1359ed8d5f955c4bcae7`.

The pristine Testing123 scene has two original
`__SpawnOnLoad_Magazine CyberHandgun` objects (421 and 1788), with Anvil spawner
components 7875 and 9519. Both reference FVRObject asset GUID
`9261d966c6283ef44be5a72eefa2ae91`, which resolves to
`Assets/Resources/objectids/weaponry_pistols/_magazines/Magazine_CyberHandgun.asset`.
That asset's ItemID is `MagazineCyberHandgun`; its original Anvil payload is
`Cyber_Handgun_Magazine` in `assets_resources_objectids_weaponry_pistols`, address
GUID `de8a9ebd88eb5564ab0613f2179ccdbe`.

Interpretation: the named magazine warning has original prefab evidence. The
empty-name and `<null>` warnings remain unidentified, and the exact shipped
spawned-prefab topology/count must be bound before any narrow warning allowance
is justified. The existing scene-only warning gate correctly does not consume
these delayed prefab warnings. Do not allow every missing-script warning or
assert this scene passed. This failure is unrelated to a missing mode-selection
global on the evidence inspected. The disabled replay reproduces the same three
issue messages; the unnamed warning origins still need exact asset binding.

## BreachAndClear_TestScene1

Observed: repeated `NullReferenceException` in
`FistVR.NavMeshLinkExtension.Start()` at offset `0x1d` during scene initialization.

Original evidence: in
`recovered-full/ExportedProject/Assets/Scenes/BreachAndClear/Tests/BreachAndClear_TestScene1.unity`,
eight original NavMeshLinkExtension components have Type 2 (Ladder), with both
`LadderPoint_Top` and `LadderPoint_Bottom` set to `{fileID: 0}`. Their component
IDs are 17543, 19309, 19440, 19632, 19693, 21327, 22255 and 22286. The corresponding
GameObject IDs are 160, 1638, 1741, 1921, 1966, 3359, 4162 and 4185.

The original decompiled implementation at
`recovered-full/ExportedProject/Assets/Scripts/Assembly-CSharp/FistVR/NavMeshLinkExtension.cs`
reads both ladder transforms' `.position` without a guard in Start. That method
is outside the seven OptiH3VR targets listed by the installed overlay receipt.
The ladder data and dereference explain the observed exception. The disabled
replay reproduces the same issue and NavMeshLinkExtension.Start offset `0x1d`,
confirming this native baseline runtime failure.

Interpretation: original legacy test-scene invalid ladder configuration. No
mode-initialization fixture can supply authored ladder endpoints without
changing the scene's meaning. Do not invent endpoints or suppress the exception.

## Friendly45_New

Observed: a single empty-name missing-script warning immediately after the
original scene-transition marker. The bounded supervisor stops before a useful
stack trace, so the originating object/prefab remains unidentified. The
structural original scene baseline lists zero original null scripts for this
scene. The disabled replay reproduces the same warning, establishing a native
baseline runtime failure while its exact object/asset origin remains unresolved.
Do not attribute it to the known Cyber magazine warning solely because both
messages have an empty name. Scene transition/load and dynamic prefab loading
are possible origins; the current evidence cannot distinguish them.

## AnimalNoiseMaker convex cooking failures

Observed in MeatGrinder, Wurstwurld1, SamplerPlatter,
RotWienersStagingScene, ReturnOfTheRotwieners, MF2_MainScene and BBQRange: Unity
reports it cannot create the requested convex mesh within its 256-polygon limit and will
use a partial hull. These are strict load failures in the current runner. The
warning's source mesh name alone does not uniquely identify a prefab or asset
GUID. All seven disabled replays reproduce the same partial-hull issue message,
confirming a native baseline runtime condition without establishing a unique
asset identity for each warning.

Source evidence: the pristine export contains three mesh assets
`Assets/Mesh/AnimalNoiseMaker.asset`, `_0.asset` and `_1.asset`, with the same
internal name, 863 vertices and 3,240 triangle indices each. Their GUIDs are
`8ce31b9cee3b5e24fbc2e6008b8e4374`, `2c14a95c0cdf9ff4bbb5938f081cd95a` and
`aa44fcbcd3fd4414598697c970a1952d`. Original animal-noisemaker prefabs request
convex MeshColliders using these meshes. For example,
`Assets/virtualobjects/animalnoisemakers/prefabs/AnimalNoiseMaker_Cow.prefab`
has an enabled convex trigger collider 64243736805655561 referencing the third
mesh GUID. This proves the original convex source inputs exist; it does not prove
which asset triggered each scene's warning or that original Windows cooking
produces the same partial hull. The visible triangle count is not a count of the
resulting convex hull's polygons. Per-prefab loaded asset identity remains the
next useful source-binding check. Do not silently substitute a simpler
collider or ignore the partial-hull warning to make the sweep green.

## WinterWasteland

Observed: `NullReferenceException` in original `Anvil.AnvilPrefabSpawn.Awake()`
at offset `0xb` during loading. Source provenance identifies an authored invalid
reference: pristine
`Assets/Scenes/WinterWasteland/WinterWasteland.unity` contains enabled Anvil
spawner component 69211 on GameObject 13078,
`__SpawnOnLoad_Scope NXS 8-32x`, with `Prefab: {fileID: 0}`. The original
`Assets/Scripts/Assembly-CSharp/Anvil/AnvilPrefabSpawn.cs` calls
`Prefab.GetGameObjectAsync()` without a guard in Awake. This method is outside
the installed seven optimization targets. These original data and source facts
explain the observed dereference. The disabled replay reproduces the same issue
and AnvilPrefabSpawn.Awake offset `0xb`, confirming the native baseline runtime
failure. Do not invent a scope asset or suppress the
exception; both would change what is tested.

## Boomskee

Observed: partial convex-hull warnings for both `Boomskee_Chemical` and
`Boomskee_Chemical_Shard3`. This is a distinct source-mesh family from the
AnimalNoiseMaker failures. The pristine meshes live under `Assets/Mesh/` with
GUIDs `9ef11085fc2d88041a5c642001fb3c9d` and
`fa303b5bd5d59944d8d336df9320d867`. Their original references appear in
`Assets/Scenes/Boomskee/Boomskee.unity` and
`Assets/GameObject/BSTarget_Chemical.prefab`. Source provenance identifies the
requested collision inputs. The disabled replay reproduces both partial-hull
messages, confirming the native baseline runtime failure. Keep
both partial-hull messages visible rather than grouping them as an accepted
AnimalNoiseMaker warning.

## ModBlank_Take&Hold

Observed: `ArgumentOutOfRangeException` in original
`TNH_Manager.InitPlayerPosition()`, reached through DelayedInit and Update.
Original InitPlayerPosition immediately indexes
`SupplyPoints[m_curPointSequence.StartSupplyPointIndex]`. In pristine
`Assets/Scenes/ModdingBlanks/ModBlank_Take&Hold.unity`, manager component 7959
(GameObject 1307) has exactly one SupplyPoints element (component 6942), while
its ten PossibleSequnces reference `PointSequence_Core_0` through `_9` under
`Assets/Resources/takenhold/pointsequences/level_classic/`. Their start-supply
indices are respectively 4, 10, 0, 6, 2, 3, 8, 12, 9 and 5. Only sequence 2's
index is within the template's one-element supply list; random/selected sequence
therefore matters. This is source-proven authored template incompatibility that
explains the observed index failure. The disabled replay reproduces the same
exception and InitPlayerPosition/DelayedInit/Update offsets, establishing a
native baseline runtime failure. This is not evidence of a missing menu global.
An uncontrolled future run could choose a different sequence and mask this
condition; record the sequence/seed when investigating intermittent outcomes. Do not silently clamp the index or fill
the scene with invented supply points.

## Grillhouse_2Story audio initialization attempt

Observed: `Default audio device was changed, but the audio system failed to
initialize it. Attempting to reset sound system.` The warning occurs while still
in the main menu near `MainMenuScreen.Start`, before the requested Grillhouse
scene route. This attempt therefore does not establish a Grillhouse-specific
failure. The host audio-device event and Unity initialization failure remain
visible as a failed run. The specific device/event source is not established.
The completed disabled replay instead reaches three partial convex-hull
messages for `CG_Outdoor_Foliage_Bush_Tallsquare_1_ConvexHull_0`, `_2_ConvexHull_0`
and `_3_ConvexHull_0`. That confirms a native baseline scene-cooking failure,
but not an identical cause for the optimized attempt. A controlled optimized
retry with stable audio is still needed to resolve the audio-event differential.
Retain both attempts; do not attribute the audio event to optimization from this
single differing pair.

## Institution_Preview

Observed: null dereferences in `Scenario_SpawnerPanel.RefreshUI()` from Awake and
`OptionsPanel_ButtonSet.UpdateButtonVisual()` from Awake.

Original authored references explain both paths. In pristine
`Assets/Scenes/Institution/Institution_Preview.unity`, panel component 17725 on
GameObject 1533 (`_ScenarioPanel_InstitutionPreview`) has all six `OBS_*` UI
references and all displayed `LBL_*` references set to fileID 0. The original
RefreshUI immediately uses `LBL_SceneName.text`, then calls the `OBS_*` objects.
Several original OptionsPanel_ButtonSet components also request pointable
buttons while containing null button entries: 18524 (`BTNSet_Type`) has two null
entries, 18955 (`BTNSet_TagCategories`) has fifteen, and 19471
(`BTNSet_DetailListMode`) has two. Original UpdateButtonVisual loops over those
entries and reads/writes each button without a null guard. These are
source-proven authored preview-scene invalid references. The disabled replay
reproduces both issue messages and the same RefreshUI/UpdateButtonVisual
exception paths and offsets, confirming native baseline runtime failures. Do not
fabricate UI controls or suppress the original callbacks to make the direct-load route pass.

## Grillhouse_2Story_Night

Both optimized and disabled attempts report the same three partial-hull messages
for the `CG_Outdoor_Foliage_Bush_Tallsquare_{1,2,3}_ConvexHull_0` mesh family.
This is a reproduced native baseline runtime cooking failure. Exact originating
asset identities and collision behavior remain unvalidated. It is separate from
the menu audio event that preempted the optimized daytime Grillhouse attempt.

## Further differential investigation

The initial same-package disabled replays are complete. Preserve the sealed run
records and original package identities above for any further reproduction. A
new controlled pair should use the same stationary synthetic head/controller
state, captured scene route and a fresh evidence directory. The matrix supports
`--groups scene --scene <name> --variants baseline --no-perf`; supply the exact
preserved app explicitly if defaults have changed during the public rename.
Avoid simultaneously running an Editor, player or benchmark.

Compare the first exception's type, method and IL offset; warning name/count
multiset and original object identity evidence; last completed gameplay stage;
requested scene; exit/cleanup proof; and actual runtime configuration. A matching
failure in both variants establishes a baseline runtime failure, not a passing
scene. An optimized-only failure needs a second controlled pair and inspection
of the affected patched method/caller before attributing it to the mod. A
baseline-only failure likewise needs repeatability rather than being dismissed.

For a genuine missing menu-selection global, trace the failing dereference to
its original owner and use the original menu/lobby API to initialize the mode.
Require explicit mode/selection evidence and run the same fixture in both
variants. Do not fabricate values merely to avoid a null. Keep any direct-entry
failure recorded alongside the separately named initialization-route result.

Performance comparisons are a separate protocol with captures disabled, warmed
post-handoff windows and both Unity cadence and completed stereo throughput.
Captured broad-sweep FPS and the first readback-contaminated measurement cannot
establish an optimization gain.
