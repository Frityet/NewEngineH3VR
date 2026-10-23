# Recovered shader sampling audit

Read-only source/bytecode inspection; no shader or full-project assets changed.
The absence of explicit unsupported-opcode markers does not establish recovered
texture-sampling semantics. Four current staged examples demonstrably omit
original result-channel permutations or immediate texel offsets. Compilation and
GPU draw success cannot validate those operations.

## Scope and method

The exact exporter GUID/collection/path-ID map contains 277 shader identities.
Recursive local includes identify 153 current bodies with recovered temporary
register code. Their original DXBC was streamed through the existing
DXDecompiler disassembler, deduplicating 10,678 program hashes with zero decode
errors. Original program records retain subshader/pass/stage, hardware tier,
keywords, raw instruction text and bytecode SHA256.

28 identities across 18 names contain original texture instructions requiring
nontrivial result-channel selection, immediate offsets or gather semantics. This
is an original-program risk inventory, **not** a claim that every listed original
variant is selected by the currently staged baseline. `resultSwizzleLoss` in the
machine report denotes the difference from the emitter's unswizzled implicit
truncation; it does not independently match every instruction to staged HLSL.
The concrete examples below establish current staged losses.

The check interprets a sample destination mask through the resource operand's
result swizzle, then compares it with HLSL's unswizzled prefix truncation. Gather
and texel-offset operations are recorded separately. Comparison samples are not
misclassified as four-channel color samples. This is bounded texture-instruction
analysis, not a complete disassembler/emitter equivalence proof.

`unity-full/Assets/Shader` and `Assets/NativeShaders` have zero explicit
`unsupported_` or `No subprograms found` markers at scan time. Archived
`shaders/uncovered-packaged` includes still contain `//unsupported_gather4` in
FinalPass FXAA branches; those archived files are not evidence of current active
include references. FinalPass correction is owned by the shader agent.

## Concrete current examples

| Shader identity | Exact original program | Current loss |
| --- | --- | --- |
| Lut2DBaker, sharedassets0.assets:611, GUID cc904e60101dab24bac9ca7297c8746c | Subshader0/pass0, pixel SM4 program2, no keywords: `sample_l r5.xyzw, r3.xyxx, t0.wxyz, s0, l(0)` | `Hidden_PostProcessing_Lut2DBaker.shader:122` assigns raw `_Curves` sample to tmp5; `.wxyz` is absent. Same pass also loses `.yxzw` and `.zxyw`. |
| TemporalAntialiasing, sharedassets0.assets:608, GUID 2e221ddcb47b77042a4515a432b2c7aa | Subshader0/pass0, pixel SM4 program2, no keywords: `sample r0.xyzw, r0.xyxx, t2.yzxw, s2` | `Hidden_PostProcessing_TemporalAntialiasing.shader:70` assigns raw `_CameraDepthTexture`; three samples require `.yzxw`. |
| SubpixelMorphologicalAntialiasing, sharedassets0.assets:612, GUID d575ff8ca6e48804d8435ccdc0f2613a | Subshader0/pass1, pixel SM4 program6, no keywords: `sample_l_aoffimmi(-1,0,0) r5.xyzw, r4.xyxx, t0.xyzw, s0, l(0)` and `(0,-1,0) r6.xyzw, r4.xyxx, t0.yzxw` | Shader lines302–303 sample identical `_MainTex` coordinates for these different neighbours and omit the second result permutation. Original program has16 immediate-offset sample instructions. |
| Unlit/PIPScope, rifle bundle:-3051070195593120036, GUID 4d2543749b188f340a62b346a4b7c819 | Subshader0/pass0, pixel SM4 program1552, `CHROMATIC_ABERRATION_HIGH USE_NIGHTVISION`: `sample r8.xyzw, r0.zwzz, t2.zxyw, s2` | Active `PIPScopeVariants/variant-1520.cginc:702` assigns raw `_NightVisionStreakRT` to tmp8, without `.zxyw`; subsequent `.zzz` and `.x` therefore select different channels. Root shader line3136 includes this variant. |

The original DXBC, human-readable disassembly, staged hashes/line excerpts and
identities for these cases are under `build/shader-sampling/examples/`. These
cases are independent of the already audited particle conditional temporary
warnings; no particle warning is reclassified as missing semantics here.

## Original-program risk inventory

The 18 names are: Alloy/Core (Camera Relative), CynCliff HeightBlend,
Hidden/PostProcessing/FinalPass, Lut2DBaker, SubpixelMorphologicalAntialiasing,
TemporalAntialiasing, NatureManufacture Cross Model Shader, Tree Bark Metalic,
Tree_Leaves_Specular, Unlit/PIPScope, VTP Glass 2 Texture blend (2-sided), VTP
Metallic 3 Texture Blend (Advanced), its Wetness/NoFlow variant, four AVPro
resource shader identities, and the `prototyping` resource shader. Exact names,
paths and per-program details are in `build/shader-sampling/summary.json` and
`original-instruction-audit.json`.

Repeated PIP and camera-relative identities account for the difference between
28 identities and18 names. Many material-family findings are point-light soft
cube-shadow variants using `.yzwx`; AVPro findings involve USE_YPCBCR and `.xwyz`.
Their original instruction requirements are proven, while runtime reachability
and exact current variant selection require their individual branch mapping.
Historical Alloy/Core and other public-source replacements are not inferred to
inherit this emitter defect merely because original programs contain swizzles.

The shared recovery emitter's `HandleSample`, `HandleSampleLOD`, and
`HandleSampleDerivative` omit the already decoded resource result mask. Immediate
texel offsets are also absent from generated sample coordinates/arguments. A
shared recovery correction and regeneration need original-instruction semantic
checks, followed by the existing Metal compilation/draw checks. Fixing FinalPass
gather4 alone does not cover the staged cases above. The shader owner received
these findings before any staging recommendation.

Reproduce the bounded inventory with:

```
dotnet build audit-vr/shader-sampling/Sampling.csproj -c Release
audit-assets/.venv/bin/python audit-vr/shader-sampling/audit.py
audit-assets/.venv/bin/python audit-vr/shader-sampling/extract_examples.py
```
