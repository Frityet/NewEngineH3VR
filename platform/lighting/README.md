# Scene linkage for recovered lighting data

The recovered Unity 5 lighting assets contain baked lightmap/probe/reflection
references and renderer/light mappings, but omit their owning SceneAsset PPtr.
Unity 2020 rejects them with “the scene it was baked for was not serialized”.
This is fixed by adding the serialized `m_Scene` field, without rebaking.

Run the read-only plan first, or add `--apply` to update the generated project:

```sh
python3 platform/lighting/link_scenes.py --project unity-full \
  --report build/lighting/full-plan.json
```

The tool follows each scene's actual `m_LightingDataAsset` reference, resolves
its asset GUID, and uses the CURRENT owning `.unity.meta` GUID for `m_Scene`.
The pointer is `{fileID: 102900000, guid: ..., type: 3}`. Internal occlusion
scene IDs remain untouched. Duplicate owners and unexpected asset formats
are rejected. Before writing, it proves that deleting only `m_Scene` leaves
the entire original baked payload byte-for-byte unchanged. It is idempotent.

Actual installed Unity 2020.3.49f1 validation:

- The original MainMenu lighting asset reports `isValid=False` without a scene.
- Setting the actual serialized property `m_Scene` reports `isValid=True`.
- EditorJsonUtility output before/after differs only in `m_Scene`; all baked
  arrays, ambient coefficients, mappings and Enlighten bytes remain identical.
- Minimal offline YAML insertion on isolated copies of all 46 linked scene
  lighting assets loads successfully with `isValid=True` and no validity error.
- Evidence is in `build/lighting/`; the small `lighting-probe` project contains
  the verification script. The probe did not import the game's large texture
  dependencies or rebake lighting.

This repairs asset validity and preserves existing baked data. It does not
establish rendered lighting parity across all game scenes or reconstruct any
data that was already absent from the recovery export.

## Realtime GI payload recovery and corrected scope

Full-project import exposed a separate issue: AssetRipper exported an empty
UnityFS `m_EnlightenData` archive while retaining the original probe-set hashes.
Unity checks files for **all** build platforms, reports four missing files per
probe set, then clears the incompatible Enlighten mapping in memory. A subsequent
save/format conversion can persist that cleared mapping. Therefore `isValid=True`
and the earlier scene-link check do **not** establish GI preservation.

The original game ships 1,962 probe sets across 15 scenes, with 3,924 original
`.rpc.sse` / `.pos` files. All 15 original scenes disable realtime lightmaps but
still load these probe-set cores. The Windows build does not ship the two other
platform variants required by the editor's consistency check. We neither invent
those variants nor rebake or discard original data to silence warnings.

`restore_player_gi.py` restores each original mapping directly into its matching
built player's `LightmapSettings` object and copies the exact original GI files.
It joins scenes using the original and built `BuildSettings.scenes` paths. It
supports only the actually observed probeset-only mappings; renderer/system
mappings are rejected until their object references can be remapped safely.
Only that serialized field changes; all other object bytes are checked unchanged.
Run this **after** building the player and **before** final signing:

```sh
audit-assets/.venv/bin/python platform/lighting/restore_player_gi.py \
  --player-data /absolute/Game.app/Contents/Resources/Data \
  --report build/lighting/player-gi-plan.json
```

Add `--apply` to write the mapping and original files. `--source-level level38
--target-level level0` is an explicit override for the isolated runtime probe.

Actual Unity **2021.3.45f2 ARM64 / Apple M5 Max / Metal** validation used the
original GP_Sandtrap_Night probe set in an isolated player with eight baked light
probes. The restored player ran 90 frames, loaded the original SSE-format core
without GI errors and sampled the preserved baked probe coefficients. Removing
only the GI directory produced both exact missing-file messages and
`Error adding Enlighten probeset ... Data not available`. Restoring it removed
those errors on a second run. This proves that the ARM64 runtime actually enters
the GI-loading path and accepts the original files; it does not claim full-scene
rendered lighting parity or realtime-lighting changes (disabled in the original).
Evidence: `build/lighting/gi-runtime-verification.json` and its three player logs.

`enlighten_archive.py` is a lossless archive experiment, **not** the production
repair. Providing the two available variants reduces the editor's missing-file
count from four to two, but still causes mapping rejection. Do not stage that
partial archive as a completed editor fix.
