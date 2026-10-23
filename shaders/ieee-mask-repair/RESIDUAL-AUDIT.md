# Final staged emitter limits and narrow IEEE repair

2026-10-23. This audit read the frozen SemanticV2 checkpoint and original DXBC evidence. Only isolated candidates/evidence under this directory were created; no working Unity project or compiler receipt was changed.

## Bounded inventory

`shaders/semantic-v2-validated/manifest.json` selects 153 recovered shader objects and 17,869 dependencies: 18,022 files inspected. The preserved 124 historical/manual shader objects are excluded. Current selected files have **zero `unsupported_` markers**. Existing `build/shader-sampling/original-instruction-audit.json` identifies original gather instructions only in FinalPass (16 affected pixel programs); that family's narrowly repaired gathers and independent pixel oracle are already bound by the SemanticV2 manifest. No additional active generic-gather loss was found in this scope. The old `shaders/unsupported-operations.json` describes earlier exports and must not be treated as the final checkpoint.

Raw bitwise syntax remains in 4,690 selected files / 13,652 lines, mostly ORs of predicates. This syntax count is not a defect count. Narrowing to raw AND/XOR/NOT, the only matches are:

- Six IEEE sign-clearing mask sites: Copy and CopyStd inline pass 1, and four Overlays pass-3 includes.
- 374 unique VTP Glass includes, containing 2,244 AND/XOR lines across three repeated parallax layers.
- No raw `~uint` sites remain in the selected files.

No additional concrete nonpredicate OR mismatch was established. This audit does not claim that every possible type-flow combination is proven by textual syntax; it reports the concrete mismatches below.

## Reachable normal-camera issue: IEEE infinity masks

Original identities: globalgamemanagers.assets:59 CopyStd, sharedassets0.assets:617 Copy, sharedassets0.assets:615 Debug/Overlays. `original/manifest.json` and adjacent `.dxbc`/`.asm` files retain exact source identity, pass, program index, keywords, and SHA-256. Copy/CopyStd pass 1 pixel programs 4/5 and Overlays pass 3 programs 24–27 execute:

```text
and sampledBits, 0x7fffffff
ieq magnitudeBits, 0x7f800000
or component masks
movc output
```

This **detects positive/negative infinity only**. NaN payloads are not equal to 0x7f800000 after sign clearing. Public historical `isnan`/`isinf` source intent is not substituted for the actual compiled program.

The frozen emitted code numerically converts both the sampled float and `asfloat(0x7FFFFFFF)` to uint before AND, losing the IEEE representation. The isolated candidate replaces exactly those two statements with:

```hlsl
uint4 nativeMagnitudeBits = asuint(tmp0) & 0x7FFFFFFFu;
tmp1 = nativeMagnitudeBits == 0x7F800000u;
```

Existing reductions, sampled values, outputs, pass states, and keywords remain unchanged. `candidate-manifest.json` binds all 35 candidate files; only six differ from the frozen project. `prepare_candidates.py` checks exact before hashes and exactly six pattern occurrences. `staging-plan.json` binds the six before/after hashes and all three shader GUID/meta identities in each working graph. The `before/` snapshot and original SemanticV2 proofs remain immutable. Staging has not occurred.

`build/shader-collection-proof/postprocessing-nan-reachability.json` establishes 78 original/current PostProcessLayer components, all with stopNaNPropagation=1. Each of 46 scenes has an initially active head-camera layer. Built-in rendering selects CopyStd pass 1 before effects; Copy pass 1 is the alternate path when the first copy has not run. This issue is reachable in normal gameplay. Overlay pass 3 is opt-in diagnostics; no serialized PostProcessDebug component was found.

The VR agent's isolated `ieee-mask-probe` owns the native pixel receipt and direct-log gates. Test requirements include raw signed zero, subnormals, finite extremes, both infinities, signed quiet/signaling NaNs, mixed NaN+Inf, actual Copy/CopyStd pass 1, and Overlays pass 3 color-space/stereo branches. Required negatives are the original broken numeric conversion and an incorrect widened >=Inf test. Copy NaNs must remain classified as non-infinite; texture/RT transport and overlay arithmetic canonicalization must be reported separately. Consult the final probe receipt rather than infer acceptance from this source audit.

## Debug motion-vector masks: no remaining instance of this defect

Original Overlays pass 2 programs 20–23 contain four AND operations each. Original comparisons establish predicate masks; AND selects a float constant/value or combines two predicates. Current four pass-2 includes use conditional selections instead of numeric bitwise conversion. For example original `and r0.w, predicate, l(0xc0490fdb)` becomes `predicate ? -3.141593 : 0.0`; original AND of two masks becomes predicate gating before the final movc. This is the prior predicate repair operating as intended, not the unrepaired IEEE-mask path.

Concrete current mono pass-2 include:
`unity-full/Assets/NativeShaders/SemanticV2Variants/6b9c5fb5ebab7c8e7e769c677cdb45ecde65b1af558dd5bb471265440ccc84ed.cginc`.

## Unresolved VTP Glass integer path; preloaded, visible use not established

Original identity globalgamemanagers.assets:45, pixel program 114, keywords DIRECTIONAL, ForwardBase. `original/VTP_Glass_2 Texture blend (2-sided)-114.asm` provides a concrete mono example. The corresponding current mono code is in:
`unity-full/Assets/NativeShaders/SemanticV2Variants/455c7c59b7698aef6befdce4775289ed79112e9158863aa547680845a7641dbe.cginc`.

Three repeated parallax layers contain:

1. Original `and r2.w, predicate, l(1)` stores integer 0/1; later `ieq ...,l(1)` selects sampled height versus inverted height. Current line 203 emits `tmp2.w = tmp1.w ? 0.0 : 0.0`, followed by comparison to integer 1. The true branch is lost. The same pattern repeats at 289 and 371. This is integer-literal/type-use loss even though the AND was converted into a conditional expression.
2. Original XOR with integer 2 is emitted as numeric uint XOR with 0.0 (216/302/384).
3. Original AND with sign bit 0x80000000 is emitted as numeric uint AND with -0.0 (220/306/388), hence zero. XOR's low-bit difference alone does not change the later sign test; the sign-mask conversion does.
4. Original signed maximum, unsigned shift, integer negation and loop comparisons surround these expressions. Numeric-float temporary conventions require a coherent typed-integer repair; a blanket asuint substitution is not justified.

The earlier `build/shader-resource-audit/serialized-reachability.json` zero-reference conclusion omitted `.shadervariants` and is insufficient. The platform follow-up found a real preload path: original `H3_ShaderAsset3.shadervariants:1738` references VTP Glass GUID `7a3470a5ec5753f4182da7d2e7da18a3` with DIRECTIONAL pass 4/pass 5 and SHADOWS_DEPTH pass 8; original GraphicsSettings preloads that collection (GUID `f0443be541f961a4f80dc3fef0eae136`). Thus this shader is preloaded. No material/visible-rendering use has yet been established, and the dynamic reachability audit is ongoing. **The VTP integer path remains explicitly unresolved; it must not be called globally unused.** No VTP source was changed as part of the normal-camera IEEE repair.

## Reproduction and limits

Use the final manifest paths when scanning, not every archived export directory. Search non-comment/non-preprocessor lines for `~uint`, single `&`, `^`, and then inspect immediate producers/consumers against original decoded DXBC. `original/manifest.json` binds the small decoded fixture set. Existing `audit-vr/shader-sampling` and `shaders/particle-bytecode-proof` tools supplied original instruction decoding; only isolated fixture outputs were written.

Neither execution-only shader proofs nor this narrow audit establish full arithmetic parity for all game shaders. The immediate normal-camera repair requires the separate hash-bound native pixel proof and a root staging checkpoint. Ordered source/proof history and original serialized membership are preserved.
