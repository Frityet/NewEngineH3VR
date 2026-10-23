# H3VR installed-asset recovery audit — 22 October 2026

## Completed work

- Read-only inspection of `/Users/frityet/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data`; original Steam installation and H3VRMacOS untouched.
- Native arm64 UnityPy 1.25.4 inventory across all 116 serialized containers, including 19 StreamingAssets bundles and 46 scene files. Total: 1,663,101 serialized objects. Detailed object counts: `inventory.json`; original scene paths and build graphics settings: `build-settings.json`.
- AssetRipper 2.0.0 native arm64 recovered the **main player data**, excluding StreamingAssets, into `../recovered/ExportedProject`. Export log ends with `Finished exporting assets`, `Saving game assemblies...`, and `Finished post-export` after 41,384/41,384 collections. Total output approximately 16 GB, 46 `.unity` scenes, 3,545 `.cs`, 22,609 `.asset`, 7,438 `.ogg`, 6,627 `.png`, 2,213 `.mat`, 2,165 `.prefab`, and 125 `.shader` files. These counts precede any subsequent root-agent changes.
- **Recovery is not a runnable port.** The 2.0.0 free export deliberately uses dummy shaders and retains version 5.6.7f1. Compilation/import/upgrading are separate tasks. Main export settings are recorded in `assetripper-settings.json`.
- Export logged 67 `RenderHeads.Media.AVProVideo.MediaPlayer` layout/EndOfStream errors, recorded verbatim in `export-errors.txt`; no other `[Error]`/`[Warning]` signatures. These fields need repair/preservation using original serialized bytes and the corresponding managed type layout.
- Original script identity mapping: `monoscript-map.json` contains 2,550 player MonoScript records / 2,487 unique assembly+namespace+class identities, with 2,285 records matched to exported C# paths and `.meta` GUIDs. Original third-party DLL and engine script identities are also retained even when there is no recovered C# mapping.

## Asset facts

- Player Unity: **5.6.7f1 (e80cc3114ac1)**, confirmed by original `output_log.txt` and serialized headers. All files use serialized format **17**, build platform **19 (Windows 64-bit)**.
- 114 containers report 5.6.7f1; `StreamingAssets/anvil_shared` reports 5.5.0p3; `Resources/unity default resources` reports 5.6.4p1.
- 19 streaming bundles retain type trees; the remaining 97 serialized files have stripped type trees. UnityPy can reconstruct engine layouts from versioned type data; managed component layouts still need assemblies. A successful metadata parse does not establish editable component parity.
- BuildSettings has 46 scenes, OpenVR enabled, graphics API `[2]` (D3D11), and preload plugins `AudioPluginMsHRTF`, `AudioPluginOculusSpatializer`, `libAudioPluginOculusSpatializer`.
- Shader inventory: **390 objects, 203 distinct names**, including duplicate shader copies across bundles. **385 game shader objects are Windows-only**: 351 D3D11 and 34 D3D11+D3D11 level 9. The remaining five stock engine/error/UI shaders include Metal and other platforms. No serialized original shader-source text exists in these Shader objects. `shaders.json` gives exact per-file IDs, names, platform sets, and compressed byte lengths.
- There are also 11 ComputeShader objects in `sharedassets0.assets` and six CGProgram objects in `resources.assets`; these have not yet been fully audited/decompiled.

## Real shader recovery proof

Current AssetRipper 2.0.0 and tag 1.3.14 make shader decompilation a premium feature. **Public tag 1.0.15 contains the open UltraShaderConverter** and has a native arm64 release. This was downloaded and executed without Wine.

Four real custom shaders were copied into a new 31,728-byte proof serialized file, preserving original raw bytes and parsed metadata separately:

- `Unlit/ScopeShader` — 12 DXBC subprograms, recovered shader has two CGPROGRAM passes.
- `H3VR/HolograpicSight` — eight DXBC subprograms, recovered shader has three CGPROGRAM passes.
- `Custom/ReticleShader` — four DXBC subprograms, recovered shader has one CGPROGRAM pass.
- `Custom/OpticLensShader` — 12 DXBC subprograms, recovered shader has one CGPROGRAM pass.

The older exporter produced **actual HLSL**, including alpha clipping, glow multiplication, texture sampling, world/projection matrices, original properties, stencil tests, and depth states; no dummy/placeholder markers occur in these four outputs. They are in:

`shader-proof/assetripper-1.0.15/ExportedProject/Assets/Shader/`

**They have not yet been compiled in Unity or validated against Metal rendering.** The exporter can select subsets of variants; full keyword/pass preservation and exact rendering still need verification. Original subprogram list/keywords are in `shader-proof/manifest.json`; full serialized Shader metadata per sample and raw bytes are alongside it.

## Direct HLSLcc result

Unity's open HLSLcc was built as native arm64 at `../.tools/HLSLcc/build/libhlslcc.dylib`; a small driver is `hlslcc_driver.cpp`. The four custom shaders' DXBC records contain ISGN/OSGN/SHDR but omit RDEF reflection. A direct translation attempt of `Custom__ReticleShader.0.dxbc` crashes in `ToMetal::TranslateDeclaration`; **direct conversion is not working**. Unity stores constant/texture bindings in the surrounding shader serialization. A future direct converter must reconstruct that reflection or use a decoder that understands Unity's subprogram metadata. The older AssetRipper proof already handles that metadata and is the currently demonstrated source-recovery route.

## Reproduction and next handoff

- Inventory: `audit-assets/.venv/bin/python audit-assets/inventory.py`.
- Sample DXBC extraction: `audit-assets/.venv/bin/python audit-assets/extract_shader_proof.py`.
- Script mapping: `audit-assets/.venv/bin/python audit-assets/monoscript_map.py`.
- AssetRipper 2.0.0 executable: `../.tools/assetripper/AssetRipper.GUI.Free`, running headless at `http://127.0.0.1:58947` (exec session 55762).
- AssetRipper 1.0.15 executable: `../.tools/assetripper-1.0.15/AssetRipper.GUI.Free`, running `--launch-browser false --port 58948` (exec session 77945). It has the four-shader sample loaded, settings set to shader `Decompile`.
- Newer tool uses POST `/Export/UnityProject` with form `path`; **1.0.15 uses POST `/Export` with form `Path` and requires an existing output directory**. Its export removes/recreates that directory, so use a fresh, empty destination. `/LoadFile` and `/Settings/Update` work with the known form fields.
- Old export log: `assetripper-1.0.15.log`. Its initial missing-output-directory error precedes a successful retry ending in `Finished post-export`.
- Old 1.0.15 has static mesh separation enabled and available. New 2.0.0 free edition disables it. Combined static meshes in the main recovery require inspection for correct transform/submesh behavior; no scene geometry parity is claimed.
- StreamingAssets bundles still need native rebuild/recovery. They were fully inventoried but excluded from the main project export. Keep original GUID/path-ID relationships and bundle dependencies when restoring them.

## Primary sources checked

- AssetRipper support/range: https://github.com/AssetRipper/AssetRipper
- AssetRipper macOS builds: https://assetripper.github.io/AssetRipper/articles/RunningOnMac.html
- Open older shader implementation: https://github.com/AssetRipper/AssetRipper/blob/1.0.15/Source/AssetRipper.Export.UnityProjects/Shaders/USCShaderExporter.cs
- Unity HLSLcc DXBC-to-Metal interface: https://github.com/Unity-Technologies/HLSLcc
- UnityPy type-tree handling: https://github.com/K0lb3/UnityPy/blob/master/UnityPy/files/ObjectReader.py
