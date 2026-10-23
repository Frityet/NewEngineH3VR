# Four legacy SVC keyword entries — source-bound audit

Date: 2026-10-23. Read-only investigation; this report is the only file created. No shader source, build input, compiler receipt, or serialized SVC membership changed. Paths are relative to `/Users/frityet/Projects/NativeH3VR` unless absolute.

## Finding

Unity 2021's public `ShaderVariantCollection.Contains` rejected four entries that remain serialized in the actual native `anvil_shared` collection. Each exact keyword combination already lacks a corresponding DXBC program in the **specific original shader object referenced by the original collection**. This is evidence of stale stored entries relative to those objects, not newly lost recovered variants. It does not prove how Unity 5.6's public API or Windows runtime handled them.

**Keep all 137 serialized members.** The semantic collection comparator must preserve these entries despite public API rejection. No entry deletion, separate-to-combined lightmap alias, generic comparison waiver, or shader patch is justified by this finding.

## Exact identities and rejected rows

Original file: `/Users/frityet/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/StreamingAssets/anvil_shared`.

Original collection `H3_ShaderAsset_7_1_17`, path ID **-2019699470181617494**, stores direct same-file (`m_FileID: 0`) shader pointers:

| Shader | Original shader path ID | Preserved project GUID | Current source |
|---|---:|---|---|
| Alloy/Prototyping | 3703153026339993305 | cba33f7e00bc13f43b9645a5d4fd4a8d | `unity-full/Assets/alloy/shaders/sm3/core/prototyping.shader` |
| Alloy/Core | 7106693295248393750 | 492f536f9d3179f4e947e308cab3ad14 | `unity-full/Assets/alloy/shaders/sm3/core/core.shader` |

All four entries have `passType: 4` (**ForwardBase**, not positional pass index 4):

- Prototyping: `DIRECTIONAL EFFECT_BUMP`
- Prototyping: `DIRECTIONAL EFFECT_BUMP SHADOWS_SCREEN`
- Prototyping: `DIRECTIONAL EFFECT_BUMP _NORMALMAP`
- Core: `DIRECTIONAL DIRLIGHTMAP_SEPARATE EFFECT_BUMP LIGHTMAP_ON _DETAIL_MULX2`

The actual built shader object IDs are 2082722914982971059 (Prototyping) and -1185775654149637056 (Core); these are build-local IDs, not original object identities. `build/shader-collection-proof/source-bundle-membership.json` binds their names, addresses, source GUIDs, and exact retained variant lists. The VR fixture recorded 133 public `Contains` successes and these four misses; its serialized reconstruction preserved all 137 members. Public `Add` also rejected the four legacy rows, which is why public API reconstruction must not define serialization fidelity.

## Original compiled inventory

Both exact original shader objects contain **42 DXBC programs**. Their entire keyword union is:

`DIRECTIONAL DIRECTIONAL_COOKIE POINT POINT_COOKIE SHADOWS_CUBE SHADOWS_DEPTH SHADOWS_SCREEN SHADOWS_SOFT SPOT UNITY_HDR_ON VERTEXLIGHT_ON`

In original subshader 0, pass 0 (`FORWARD`, `LIGHTMODE=ForwardBase`):

| Stage | Original program indices and keyword sets |
|---|---|
| Vertex | 0: `DIRECTIONAL`; 1: `DIRECTIONAL SHADOWS_SCREEN`; 2: `DIRECTIONAL VERTEXLIGHT_ON`; 3: `DIRECTIONAL SHADOWS_SCREEN VERTEXLIGHT_ON` |
| Fragment | 4: `DIRECTIONAL`; 5: `DIRECTIONAL SHADOWS_SCREEN` |

None of `EFFECT_BUMP`, `_NORMALMAP`, `LIGHTMAP_ON`, `_DETAIL_MULX2`, or `DIRLIGHTMAP_SEPARATE` exists in either object's compiled keyword inventory. Decompressed original DXBC program-block SHA-256:

- Prototyping: `3e9576f406a1ec1da24b9d53141c4086745fe8e301cb37e97ce911932484a4f1`
- Core: `fb3c308be5cef3555c1fddcc97bd78d89feea3e3848fa2d4237d733bd7ca4598`

The existing `shaders/alloy-original-program-audit.json` additionally covers **19 original Core objects / 172,542 programs**, with **zero `DIRLIGHTMAP_SEPARATE` occurrences**.

## Current source and limits

The exact current anvil Prototyping source is recovered from its original 42-program object. Its ForwardBase pragmas declare `DIRECTIONAL` with optional `SHADOWS_SCREEN` and `VERTEXLIGHT_ON`, matching the original inventories. Property UI annotations still mention `EFFECT_BUMP` and `_NORMALMAP`; annotations alone do not introduce compiled variants. The three legacy strings therefore remain unrecognized by the public API without evidence of newly removed programs.

Current anvil Core uses the broader historical public source. It declares `EFFECT_BUMP`, `_DETAIL_MULX2`, `_NORMALMAP`, and `multi_compile_fwdbase`. Installed Unity 2021 GI includes support `DIRLIGHTMAP_COMBINED`; neither the current Core include path nor the inspected original Core programs supplies `DIRLIGHTMAP_SEPARATE`. Supporting combined directional lightmaps does **not** establish semantic equivalence with separate directional lightmaps.

Do not generalize the Prototyping result to every same-name object. Other original copies contain real feature programs: globalgamemanagers.assets:72 has 5,408 programs, including 3,192 with EFFECT_BUMP and 1,468 with _NORMALMAP; the toy/targets/tools copy has 1,776 programs, including 880 with EFFECT_BUMP and 884 with _NORMALMAP. The other four inspected bundle copies each have 892 programs / 440 EFFECT_BUMP / zero _NORMALMAP. Their distinct current project sources retain feature pragmas. Exact collection/object identity is essential.

This is a bounded original-program and serialized-membership audit. It is not universal pixel equivalence, complete warmup coverage, or full-game acceptance.

## Reproduction

Use `audit-assets/.venv/bin/python` with UnityPy. Read the original small `anvil_shared` bundle and inspect the ShaderVariantCollection's `m_Shaders` pairs to confirm the two direct pointers and four strings. Join the pointers against `audit-assets/all-shader-source-map.json` / `build/shader-identities/full-shader-identity-map.json`, not shader name alone.

For each exact shader in `audit-assets/all-shader-inputs/002-0-StreamingAssets_anvil_shared.assets`, select `platforms.index(4)`, decompress its `compressedBlob` slice using the matching offsets/compressedLengths/decompressedLengths and `lz4.block.decompress`, then parse with `UnityPy.export.ShaderConverter.ShaderProgram(EndianBinaryReader(block, endian='<'), object.version)`. `m_SubPrograms[*].m_Keywords` yields the inventory. Bind the pass to its `progVertex` / `progFragment` `m_SubPrograms[*].m_BlobIndex`, selecting DX11 program types. `shaders/audit_alloy_programs.py` documents the same decoder workflow; do not run its output-writing main merely to read this report.

Evidence hashes below were captured when writing this report. The prior program inspections were not rerun for report creation.

| Evidence/input | SHA-256 |
|---|---|
| `/Users/frityet/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/StreamingAssets/anvil_shared` | `9679bb90123eab9d3a4c9e07b84ecea310be8a3074f33302fc879ff2d1d6b350` |
| `audit-assets/all-shader-source-map.json` | `9d4f986d5916415882e92910544a13c9d87943e3f8d474aef967399ad5cb7e71` |
| `audit-assets/all-shader-inputs/002-0-StreamingAssets_anvil_shared.assets` | `7525ffb5f2b6bc69dc47eb3270ef6546129ff57293094645cb4add2b93dde987` |
| `build/shader-identities/full-shader-identity-map.json` | `945fa48daf35675a8f22d20467882f014cce38fe98fe169ccdc119872b4e075d` |
| `build/shader-collection-proof/source-bundle-membership.json` | `8ffdccca460f2ae167d24844c3bc854cfc49c497465557c795549298202183a5` |
| `svc-integrity-probe/output/actual-runtime-membership.json` | `bc9cd056d423f5aca7111208aa4773d1117998d621df6d29290731e031cf7bd8` |
| `svc-integrity-probe/output/result.json` | `cf107d986b74abd546d3d520359884bf75baa15c07f07c78408b368072178481` |
| `shaders/alloy-original-program-audit.json` | `051bb93acf24c8bdae3ba791ffa3a816a369a8d4a7efc7cfb5d0b3f52c676a42` |
| `unity-full/Assets/H3_ShaderAsset_7_1_17.shadervariants` | `04c4e7dcd28bd17dc2b994771ccb11caedc53f7fffbc9e9c0a9042274944413d` |
| `unity-full/Assets/alloy/shaders/sm3/core/prototyping.shader` | `d1fde5ad6eb1eb9bd3f4c7eb23ada9ddfd74cfcd56457a8fd8aa07a893eb163e` |
| `unity-full/Assets/alloy/shaders/sm3/core/core.shader` | `d0bf24b0eae94c307f5fe2e87a7e60773135d44c4059e34691d12ea456f92082` |
