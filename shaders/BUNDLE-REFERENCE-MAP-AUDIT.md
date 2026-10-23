# Bundle reference-map audit — 2026-10-22

Read-only audit following the ShaderVariantCollection array-index mismatch. No checker, shader, working-project, or bundle inputs changed. This report does not replace full native bundle-content validation.

## Scope and result

`build/native-bundle-plan.json` contains **4,387 addresses: 4,378 prefabs, 8 shaders, 1 ShaderVariantCollection**. There are no direct Material, AnimationClip, or AnimatorController addresses.

All **4,378 planned source prefabs** were inspected: **4,369 binary** files through UnityPy and **9 YAML** files through their document class IDs. Binary decoding produced **zero errors**. All contained objects were hierarchy objects/components; there were **zero owned Material, AnimationClip, AnimatorController, ShaderVariantCollection, or other non-hierarchy subassets**. The nine YAML prefabs contained class IDs 1, 4, 114, 33, 23, 65, 1001, 64, 54, 198, 199, 82, and 136; none was Material (21).

Across all **29 distinct non-MonoBehaviour binary prefab type-tree schemas**, no `map`, `pair`, or `set` nodes occurred. The only legacy Animation component was in `Assets/virtualobjects/flipzo/prefabs/Flipzo.prefab`, path ID `111642988549198181`; its `m_Animations` array was empty.

Actual native bundle `m_Container` samples corroborated the address scope:

| Native bundle under `build/StreamingAssets/` | Addresses | Main-object types |
|---|---:|---|
| `assets` | 74 | 74 GameObjects |
| `assets_resources_objectids__meatmas` | 19 | 19 GameObjects |

This is a **93-address native sample**, not an exhaustive native `LoadAssetWithSubAssets` inspection.

## Traversal boundary and conclusion

`script/NativeAssetIntegrity.cs`:

- `InspectReferences`/`References` (approximately lines 234–276) serializes the current described object. It records a referenced object's property path, type, name, and local key; **it does not recursively inspect that referenced object**.
- `Capture` (approximately lines 286–309) describes prefab hierarchy objects and genuinely additional owned subassets returned by `LoadAllAssetsAtPath` / `LoadAssetWithSubAssets`.
- `Matches` compares recorded reference keys, including property paths.

Therefore renderer material pointers are checked, but their dependent Materials' `m_SavedProperties.m_TexEnvs` maps do not enter this comparator for any current source address. No present Material texture-map ordering false positive was demonstrated. The ShaderVariantCollection map remains the concrete identified map-order issue; its semantic fix is independently owned/tested.

**No generic array-index waiver is supported.** Script/gameplay arrays remain ordered. The type-tree scan excludes MonoBehaviour schemas and does not establish arbitrary user-script data semantics. Full bundle verification must still detect changed/null pointers, missing objects, reordered ordered arrays, or unexpected subassets. Future plans containing Material addresses or owned Material subassets would require a fresh targeted audit.

## Reproduction paths and method

Working directory: `/Users/frityet/Projects/NativeH3VR`.

Python environment: `audit-assets/.venv/bin/python` (UnityPy). Input paths are resolved from the plan's `project` plus each entry's `asset`; original game assets are not modified or needed by these checks.

1. Count plan addresses by `Path(asset).suffix`.
2. For each `.prefab`, inspect its first five bytes. For `%YAML`, collect anchored document headers `^--- !u!(\d+) `. Otherwise call `UnityPy.load(path)` and count every `env.objects` object's `type.name`. This produced 4,369 decoded binary prefabs and zero exceptions.
3. For each distinct non-MonoBehaviour `(type.name, bytes(serialized_type.old_type_hash))`, recursively inspect `serialized_type.node.m_Children`; record nodes whose `m_Type` is `map`, `pair`, or `set`. The observed set had 29 schemas and zero matching nodes.
4. To repeat the small native sample:

```python
import UnityPy
from collections import Counter
for name in ('assets', 'assets_resources_objectids__meatmas'):
    env = UnityPy.load('build/StreamingAssets/' + name)
    rows = []
    for obj in env.objects:
        if obj.type.name == 'AssetBundle':
            for address, info in obj.read().m_Container:
                rows.append((address, info.asset.deref().type.name))
    print(name, len(rows), Counter(kind for _, kind in rows))
```

These checks were already executed; writing this report did not rerun them. The audit establishes the current bounded comparator surface, not universal Unity serialization order or complete game acceptance.
