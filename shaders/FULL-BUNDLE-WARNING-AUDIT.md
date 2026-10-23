# Native bundle shader warning audit — 2026-10-22

Read-only findings during the source-frozen full bundle build. No shader, project, build input, or compiler policy changed. This report is outside both working Unity projects. Paths below are relative to `/Users/frityet/Projects/NativeH3VR` unless absolute. These are bounded warning analyses, **not full-game acceptance, complete rendering parity, or proof that every runtime branch is correct**.

## Alloy/Weighted Blend: pass macro redefinitions

`unity-full/Assets/Shader/Alloy_Weighted Blend.shader` defines `UNITY_PASS_FORWARDBASE` (252), `UNITY_PASS_FORWARDADD` (294), `UNITY_PASS_SHADOWCASTER` (320), `UNITY_PASS_DEFERRED` (362), and `UNITY_PASS_META` (391), each with an empty replacement. Unity also injects pass macros. `build/native-bundles-build.log` around 12809 onward reports the duplicate definitions; its META compiler context lists `UNITY_PASS_META`. The log does not expose the injected macro's literal replacement, so this audit does not assume it equals 1.

The prior read-only scan found 47 occurrences of these five macros across staged Alloy/AlloyUnity56 includes and installed 2021 CGIncludes. Their conditional consumers test definition/presence (`defined`, `ifdef`, `ifndef`), with no numeric-value consumers found. Both the injected and empty definitions satisfy those conditions. Thus the warned redefinitions do not change those observed branches. No macro removal was needed or applied.

Reproduce the bounded scan:
```sh
rg -n 'UNITY_PASS_(META|FORWARDBASE|FORWARDADD|DEFERRED|SHADOWCASTER)' unity-full/Assets/Alloy unity-full/Assets/AlloyUnity56 .tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents/CGIncludes
rg -n 'UNITY_PASS_' 'unity-full/Assets/Shader/Alloy_Weighted Blend.shader'
```

## Particles/Vertex Lit Smooth Blended H3VR: conditional temporary reads

The current build reports eight potential-uninitialized warnings across four SemanticV2 includes (listed in the hash inventory below), at line pairs 179/213, 192/226, 191/225, and 181/215 respectively. These are the four `_RECEIVE_SHADOWS` combinations with optional `FOG_EXP2` and `SOFTPARTICLES_ON`.

`shaders/particle-uninitialized-flow-audit.json` preserves the original DXBC evidence and earlier source mapping. The current four includes were also inspected: eight light-selection sites per include, 32 total, retain the pattern `if (predicate) temporary = 1.0; ... predicate ? temporary : computedCone`. The predicate and selected temporary lane are unchanged before selection. A predicate register that is also the final assignment destination is read on the RHS before the LHS write. Thus the potentially undefined temporary is not selected on the path where it was not assigned. Original DXBC has the corresponding conditional `mov` followed by `movc` with the unchanged predicate. No blanket initialization or semantic modification was made.

This supports the reported attenuation selections only; it does not prove all particle arithmetic or all compilers equivalent. Reinspect the exact four files against the audit JSON before extending this conclusion to a new source revision.

## WFX transparent shaders: missing legacy fallback

Both pristine recovered WFX Transparent Diffuse/Specular sources already specify `Fallback "Particles/Alpha Blended"`. The installed 2021 `unity_builtin_extra` contains `Legacy Shaders/Particles/Alpha Blended` (path ID 203), not the unprefixed name. Current staged sources retain the original fallback at lines 107/109. Their primary subshaders have LOD 200.

The prior full pristine-reference audit found zero standalone materials or serialized reference chains for these two shaders and no runtime shader-name literals. No `Shader.globalMaximumLOD` or shader `maximumLOD` setter was found in recovered/port C# sources. Original `Assets/Scripts/Assembly-CSharp/FistVR/GM.cs:546–548` calls `QualitySettings.SetQualityLevel`; all seven original quality rows have mesh `maximumLODLevel: 0`, and Turbo Potato changes mesh `lodBias` to 0.7. Those mesh controls do not impose a shader-LOD cap. The isolated native material proof exercised the supported primary shaders; Diffuse had 24 actual/clone draws spanning 12 mono keyword sets.

Consequently the warning is a genuine unresolved fallback-name mismatch, but the observed native primary path does not use it, and there is no demonstrated original gameplay dependency. An optional later compatibility fix could resolve the exact legacy name; it is not justified as a build blocker by current evidence. This conclusion does not promise behavior on another unsupported GPU or under a newly introduced shader-LOD cap.

Supporting Unity documentation previously consulted:
- https://docs.unity3d.com/2021.3/Documentation/Manual/SL-ShaderLOD.html
- https://docs.unity3d.com/2021.3/Documentation/Manual/SL-Fallback.html

## Nature/SpeedTree Billboard: missing TEXCOORD2, dynamic GI/META disabled

This is a real capability omission, not dismissed merely because it is a builtin. Installed 2021 `CGIncludes/SpeedTreeBillboardCommon.cginc:23–32` declares `SpeedTreeBillboardData` with TEXCOORD0/1 but no TEXCOORD2. The full build log at 2406 explicitly says dynamic GI and META generation are disabled; 2407 raises the crossfade target to 3.0. This is installed builtin shader ID 14001, not a replacement project shader.

The complete original inventory covers 116 serialized containers with zero recorded read errors and 390 shader objects. It contains **zero Terrain, TerrainData, BillboardRenderer, or BillboardAsset objects** and **zero shaders named Nature/SpeedTree Billboard**. Pristine full YAML has no BillboardRenderer component and no material referencing builtin shader ID 14001. Recovered C# has no SpeedTree/BillboardRenderer/BillboardAsset or exact shader-name usage. The five `Material/Billboard_*.mat` files are unrelated signs using Alloy/Core (GUID `0a95665b1127f0846a689976f5839d7e`). Original GraphicsSettings always-included list does not contain 14001.

Direct original-binary inspection distinguished other shaders: original `globalgamemanagers.assets:38` Nature/SpeedTree and `:53` Alloy/Nature/SpeedTree (Forward) both contain META passes; neither is the warned billboard shader. `:23` BillboardWavingDoublePass and `:26` BillboardTree are also distinct. Original shader source text is absent (`m_Script` empty), so their parsed pass lists are stronger evidence than a guessed source substitution.

The local Unity 2020 clone and installed Unity 2021 billboard common includes are byte-identical and both omit TEXCOORD2. **Exact Unity 5.6 builtin billboard source was not found locally.** Historical Alloy SpeedTreeBillboard sources are different implementations and do not establish builtin equivalence. Therefore this report cannot prove the omission existed in 5.6, or explain why Unity includes this otherwise unreferenced builtin. No affected gameplay content or necessary migration patch is evidenced; future runtime evidence could change that conclusion.

Cheap inventory/reference reproduction (no Unity process required):
```sh
audit-assets/.venv/bin/python - <<'PYCODE'
import json
from pathlib import Path
j=json.loads(Path('audit-assets/inventory.json').read_text())
print('containers',len(j['files']),'errors',j['errors'])
print({k:v for k,v in j['totals'].items() if any(s in k for s in ('Terrain','Billboard'))})
s=json.loads(Path('audit-assets/all-shader-source-map.json').read_text())
print('shader objects',len(s),'billboard matches',[x for x in s if x['name']=='Nature/SpeedTree Billboard'])
PYCODE
rg -l '^BillboardRenderer:|^--- !u!228 ' recovered-full/ExportedProject/Assets --glob '*.prefab' --glob '*.unity'
rg -l 'm_Shader: \{fileID: 14001,|Nature/SpeedTree Billboard' recovered-full/ExportedProject/Assets --glob '*.mat' --glob '*.shader' --glob '*.prefab' --glob '*.unity'
rg -n 'SpeedTree|BillboardRenderer|BillboardAsset' recovered-full/ExportedProject/Assets/Scripts --glob '*.cs'
cmp .tools/UnityNativeBuild.app/Contents/CGIncludes/SpeedTreeBillboardCommon.cginc .tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents/CGIncludes/SpeedTreeBillboardCommon.cginc
```
Empty `rg` results return status 1. The original binary root is `/Users/frityet/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data`; it remains read-only. Inventory extraction provenance is `audit-assets/inventory.py` and `audit-assets/extract_all_shader_assets.py`.

## Evidence identity

SHA-256 values captured when writing this report. The build log remains active, so it is intentionally not assigned a whole-file hash; the cited line positions refer to the current build snapshot and should be relocated by warning text if the log is replaced. Native execution evidence is retained in `shaders/semantic-materials-execution-proof.json` and `shaders/semantic-optics-execution-proof.json`; those proofs do not establish universal pixel equivalence.

| Path | SHA-256 |
|---|---|
| `audit-assets/inventory.json` | `ea5970a18cf36b71fb11d7b59df8b1918b3ae06860082052b09da0c4eed8873a` |
| `audit-assets/all-shader-source-map.json` | `9d4f986d5916415882e92910544a13c9d87943e3f8d474aef967399ad5cb7e71` |
| `shaders/particle-uninitialized-flow-audit.json` | `4380a5941bb4a9761ed4e4e707c13cdaba92684181ca7eaa5457031af86e3bf2` |
| `shaders/semantic-v2-staging-verification.json` | `5bc9bb06422f3a4c330a7d919dad04c3cf89dbe8b8478e3a8e238bd310637bfe` |
| `unity-full/Assets/Shader/Alloy_Weighted Blend.shader` | `c8c2fdde7ebc8bc8395afd55fd20dd4ff16fd2d666b8aefb0b2c868207361c25` |
| `unity-full/Assets/Shader/WFX_Transparent Diffuse.shader` | `d6c50acc94254113d53a3c19fd9a5a11eed335dad06de43d038869df20102dc6` |
| `unity-full/Assets/Shader/WFX_Transparent Specular.shader` | `a0b55a3ec369bfb2b429112d8a2333abb37650480b6ef4d7f04ab6f189f825ff` |
| `.tools/UnityNativeBuild.app/Contents/CGIncludes/SpeedTreeBillboardCommon.cginc` | `d1fa679b6dc494c1e3e8d53a0fbe97811b519922bc5663e449a36d156d476911` |
| `.tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents/CGIncludes/SpeedTreeBillboardCommon.cginc` | `d1fa679b6dc494c1e3e8d53a0fbe97811b519922bc5663e449a36d156d476911` |
| `unity-full/Assets/NativeShaders/SemanticV2Variants/1329b43bf96ff9983aec311c3424a35ed16c0eca299c7ec85b79098df8b1705b.cginc` | `1329b43bf96ff9983aec311c3424a35ed16c0eca299c7ec85b79098df8b1705b` |
| `unity-full/Assets/NativeShaders/SemanticV2Variants/33a299d192a16ea93db9f11bce8974af7824a22d4f5bdc5edf7398743becd4f0.cginc` | `33a299d192a16ea93db9f11bce8974af7824a22d4f5bdc5edf7398743becd4f0` |
| `unity-full/Assets/NativeShaders/SemanticV2Variants/490be2e6ccf35538d9dc33949a535069987f3478af2ec05ac0e4bfd003c1173b.cginc` | `490be2e6ccf35538d9dc33949a535069987f3478af2ec05ac0e4bfd003c1173b` |
| `unity-full/Assets/NativeShaders/SemanticV2Variants/ae4ad7424910e635750569ab5da201b7b089bd1bdcf09e3d6e89ce24dd34f201.cginc` | `ae4ad7424910e635750569ab5da201b7b089bd1bdcf09e3d6e89ce24dd34f201` |
