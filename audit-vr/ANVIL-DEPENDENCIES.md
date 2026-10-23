# Anvil bundle dependency audit

## Result and limit

The recovered runtime loads only the explicitly requested bundle. It does not read an AssetBundleManifest or recursively load its dependencies. **All 18 generated native manifests now exactly match the original graph: two edges, zero added or removed edges.** This independently confirms the earlier serialized-reference graph. There is no newly introduced dependency on `anvil_shared`, `assets`, or another content bundle, and no new loader requirement is demonstrated by this rebuild.

This comparison uses the first completed full bundle build, which returned success despite nine shader-error diagnostics. Its bundle content is **not accepted** by this dependency result. The manifest comparison is hash-bound, and copies of those first-build manifests are preserved under `build/anvil-dependencies/first-build-manifests` for review after subsequent rebuilds.

The existing `toytargetstools → weaponry_support` edge has **no guaranteed explicit prewarm**. This is an original loading-order risk, not a demonstrated new port regression. An incidental earlier support-asset request could satisfy it; the audit does not assume such a request always happens. A successful verifier which loads every bundle in dependency order does not establish that the game's original loader covers this path.

## Original loading behavior

Paths below are relative to `recovered-full/ExportedProject/Assets/Scripts/Assembly-CSharp/`.

- `AnvilManager.cs:74–94`: `GetAssetBundleAsyncInternal` caches one callback per name and calls `AssetBundle.LoadFromFileAsync` for that one file.
- `AnvilManager.cs:97–127`: `LoadAsync` waits for that bundle callback, then calls its `LoadAssetAsync`. Its callback dependency is the containing bundle, not a graph of AssetBundle dependencies.
- `AnvilPrewarmCallback.cs:37–59`: prewarming loads every address in the one named bundle; it does not discover other bundles.
- `AnvilManager.cs:138–143`: pumps those callbacks. `StopAll` only stops coroutines; it does not unload cached bundles.
- `FistVR/GM.cs:478`: startup prewarms ammunition. `FistVR/MainMenuScreen.cs:120–168` waits before offering normal scene selection and provides explicit force completion. Wrist menu transitions also check completion. The gameplay acceptance harness independently waits for `GM.LoadingCallback.IsCompleted` before calling `SteamVR_LoadLevel.Begin`.
- `FistVR/MF_GameManager.cs:72`: a scene-specific MF prewarm loads `weaponry_mf2`.

These are the only direct `PreloadBundleAsync` call sites across the full recovered Scripts and Plugins source. No manifest dependency reader or explicit `anvil_shared`/`assets`/support prewarm was found. Shader variant warmup in `GM.Awake` is not a request to load a bundle file.

## Exact graph evidence

All 18 original individual manifests were read from the installed game's `h3vr_Data/StreamingAssets`. The master manifest lists 17 containers; the separate `anvil_shared.manifest` supplies the eighteenth. Absolute Windows dependency paths were normalized to their final filenames.

| Bundle | Dependency | Explicit startup coverage |
|---|---|---|
| `assets_resources_objectids__sosig` | `assets_resources_objectids_weaponry_ammunition` | Ammunition prewarm precedes normal menu scene transition |
| `assets_resources_objectids_toytargetstools` | `assets_resources_objectids_weaponry_support` | None guaranteed |

The native plan contains 18 bundles and 4,387 explicit addresses with no duplicate planned GUID ownership. Traversal indexed 88,809 GUIDs and decoded references through 32,893 assets, with zero decode errors. Its only two cross-bundle edges are the same pair above. Concrete chains:

1. `Assets/agents/prefabs/turburgert/Turburgert_Flamethrower.prefab` → `Assets/weaponry/_ammunition/prefabs/45acp/Ammo_45ACP_Tracer.prefab`.
2. `Assets/environment/_scenespecific/institution/prefabs/brutbarrels/Brut_Barrel_Mustard.prefab` → `Assets/GameObject/BrutBarrelNoDamExplosion_MustardGas.prefab` → `Assets/weaponry/explosives/pinnedgrenades/grenades/PinnedGrenade_M18SmokeGrenade_Freedom.prefab`.

The pistol bundle, including the M1911 acceptance route, has no cross-bundle dependency in either the original manifests or the native serialized graph. This does not prove successful firearm loading, which still needs the real player acceptance run.

## Reproducible artifacts and pending decision

- `build/anvil-dependencies/original-graph.json`: shipped manifest graph.
- `build/anvil-dependencies/native-plan-direct-edges.json`: direct explicit asset references.
- `build/anvil-dependencies/native-plan-transitive-edges.json`: transitive serialized graph and example chains.
- `build/anvil-dependencies/manifest-comparison.json`: complete generated-manifest comparison with original/native manifest SHA-256 hashes.
- `build/anvil-dependencies/first-build-comparison.json`: preserved first-build comparison, explicitly labelled as dependency evidence from a shader-failed build.
- `audit-vr/compare_anvil_manifests.py`: rerun with `audit-assets/.venv/bin/python` after subsequent builds; missing manifests remain an explicit incomplete result.

The serialized traversal intentionally does not claim to model shader source/importer dependencies, built-in importer behavior, or arbitrary code-driven asset requests. The final native manifest graph is authoritative for bundle dependencies.

The native graph is identical, so there is no demonstrated new loader requirement. The uncovered original support edge still deserves a fresh-process test loading the mustard barrel before any support item. If future rebuilds add dependencies, the original loader must ensure those dependencies are loaded before loading affected assets; loading all bundles in a separate verification step would conceal this runtime requirement. No game code or project asset was changed for this audit.
