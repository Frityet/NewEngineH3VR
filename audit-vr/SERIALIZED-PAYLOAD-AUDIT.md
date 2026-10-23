# Serialized gameplay payload preservation audit

The sampled weapon/manager prefabs, every scene hand and every Resources
FVRObject retain their original serialized gameplay fields and values after
binary compaction. This audit found no evidence of payload loss caused by the
now-repaired managed assembly self references. It is a decoded field comparison,
not a byte-hash, object-count or null-slot comparison.

## Coverage and results

| Scope | Components | Original top-level fields compared | Result |
|---|---:|---:|---|
| M1911, its magazine and object definition; AKM; Python; M249; Mosin M91/30; break-action 12g; `_GameManager` | 95 | 3,247 | All source fields retained; only 324 added Unity curve metadata fields |
| FVRViveHand in all 46 scenes | 92 | 8,372 | Zero payload differences |
| All Resources FVRObject assets | 4,339 | 216,950 | Zero payload differences |

The first scope includes the M1911 object definition also counted in Resources:
there are 4,525 distinct verified components in total. All checked script pointers
match the expected DLL GUID/fileID in the full-project rewiring report. No
component was missing or undecodable.

M1911's main Handgun component preserves all 191 compared fields, including
inherited interactive/physical/firearm settings. Its other seven components and
all four magazine components match. `FistVR.IM` in `_GameManager` preserves all
22 compared fields and their contents, including referenced item collections.
The comparison walks nested structs, arrays and references as well as top-level
field names.

The only differences in the representative scope are added AnimationCurve key
metadata: neutral tangent/weighted modes and in/out weights near one-third.
Original key times, values, slopes and curve settings match. No original field
was removed, no array length changed and no existing scalar/reference value
changed beyond the documented float comparison tolerance.

## Method and limits

`serialized_payload_audit.py` reads pristine YAML under
`recovered-full/ExportedProject` and current binary files under `unity-full`,
matching objects by original pathID. UnityPy decodes the binary embedded type
 trees. A scalar-preserving YAML loader avoids YAML 1.1's incorrect octal/bool
interpretation of Unity strings and packed arrays. Packed numerical arrays are
decoded from their original little-endian hexadecimal representation.

External pointers are compared by asset GUID plus pathID, resolving each binary
file's external table and Unity GUID nibble ordering. Local pointers retain the
original pathID. Script pointers are separately checked against
`build/unity-full-preparation/rewiring.json`. Standard prefab-link metadata,
serializedVersion and editor class identifiers are excluded because Unity's
asset schema and assembly packaging changed. Float values allow relative 2e-6
and absolute 1e-7 tolerance for export/float32 formatting; this is not a bitwise
floating-point parity claim.

Reports:

- `build/serialized-payload-audit/representative-prefabs-and-so.json`
- `build/serialized-payload-audit/all-scene-hands.json`
- `build/serialized-payload-audit/all-resource-fvrobjects.json`

This audit does not verify every component in all 8,293 prefabs, runtime method
behavior, native asset payloads or full-game playability. A retained serialized
value can still be consumed incorrectly by engine/runtime behavior; the real
scene/firearm acceptance route remains necessary. No working-project asset was
modified by this audit.

## Complete prefab pass (supersedes the representative-only limit above)

The follow-up pass covers **all 8,293 pristine exported prefabs**, decoding
**51,449 MonoBehaviour components** and comparing **1,080,948 original top-level
fields**, recursively including their values, arrays and object references.
The working set contains 8,276 binary prefabs and 17 still in YAML; both formats
are now explicitly decoded. All original component IDs were found and decoded,
and every script pointer matches either the verified managed/UGUI rewiring or
its unchanged original pointer. This includes 29 already-null original script
references and the unchanged ES2Init DLL script pointer; they are not newly lost
scripts.

There are **zero changed existing values, changed array lengths, unexplained
field removals or unreviewed differences**, within the float tolerance above.
No change to an existing gameplay value was waived. All observed schema
migrations are retained in the detailed report and classified explicitly:

| Observed difference | Count | Evidence/constraint |
|---|---:|---|
| Neutral AnimationCurve key fields | 18,821 | Only added tangent/weighted modes 0 and weights 1/3 |
| UGUI layout, masking, raycast and image defaults | 25,229 | Exact neutral defaults in the installed official UGUI runtime source |
| New selected-state fields | 6,711 | Each value exactly matches that component's original highlighted-state value |
| Removed UnityEvent `m_TypeName` | 9,234 | Absent from the installed Unity 2021 UnityEventBase schema; no callbacks, targets, methods or argument values changed |
| Added persistent-call target assembly metadata | 2,772 | Empty lazy metadata; installed getter derives it from the existing non-null target |
| Previously restored original AVPro fields | 129 | Exact per-component comparison with the validated restoration plan's staged payload |
| Restored AVPro event fields plus event schema migration | 3 | Same validated original empty persistent-call lists; only obsolete type metadata removed |

The UnityEvent finding is checked against the **actual installed engine IL**,
not inferred from field names. The original Unity 5 UnityEventBase contains
`m_TypeName`; the installed Unity 2021 version does not. Its PersistentCall
adds `m_TargetAssemblyTypeName`, and its getter fills an empty value using
`m_Target.GetType().AssemblyQualifiedName` when the target is non-null.
The serialized target/method/argument/call-state payloads are unchanged.
Evidence is `build/serialized-payload-audit/unity-event-schema-evidence.json`.
The remaining custom event types are confirmed UnityEvent subclasses in the
recovered source (SteamVR pose events, UnityEventInt and MediaPlayerEvent).

UGUI source evidence comes from the installed editor's
`Contents/Resources/PackageManager/BuiltInPackages/com.unity.ugui/Runtime/UI/Core`
fields in Graphic, MaskableGraphic, Image, Navigation, LayoutElement,
HorizontalOrVerticalLayoutGroup and CanvasScaler. The verified 41 original
UI script IDs in `build/ugui/old-script-ids.txt` establish the explicit old
built-in DLL GUID to retained replacement DLL GUID migration.

The three AVPro components are in ItemSpawnerPanelV2 and ToolPanel_ItemSpawner.
Their exact fileIDs are joined to
`build/video/serialization/restoration-plan.json`; each of the 44 restored
fields is compared with that plan's original-data-derived staged YAML. This
avoids treating all new MediaPlayer fields as generic defaults.

Reproduce, streaming one prefab at a time:

```sh
audit-assets/.venv/bin/python audit-vr/audit_all_prefab_payloads.py
audit-assets/.venv/bin/python audit-vr/classify_prefab_payload_migrations.py
```

Reports are JSONL so the audit and review do not retain the full project graph
in memory:

- `build/serialized-payload-audit/all-prefabs.jsonl`: raw decoded comparisons.
- `build/serialized-payload-audit/all-prefabs-classified.jsonl`: every difference with its explicit classification.
- `build/serialized-payload-audit/all-prefabs-classified-summary.json`: final counts and zero remaining review items.
- `build/serialized-payload-audit/all-prefabs-unreviewed.json`: empty after review.

The full pass took approximately 42 seconds. It only read both Unity projects;
reports and comparer code are outside them. This strengthens serialized-payload
preservation evidence, but does not establish runtime behavior, complete scene
component payload preservation, visuals or gameplay parity.
