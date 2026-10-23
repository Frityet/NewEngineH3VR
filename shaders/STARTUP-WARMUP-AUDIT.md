# Startup warmup review — retain current behavior

Decision: leave the current shader preload configuration, original collections, and accepted build checkpoint unchanged. The full player reached gameplay initialization after approximately 190.95 seconds, within its 600-second bound (parent/VR runtime observation). Its immediate blocker is a missing native XR entry point, not a timeout. Runtime acceptance comes before an optional startup optimization. This review made no source, project, app, shader, or receipt changes.

## Build retention and startup warming are separate

`shaders/NativeOriginalVariantFilter.cs` and its staged `unity-full/Assets/Editor/NativeBuildTools/NativeOriginalVariantFilter.cs` are byte-identical:

- Lines 32–50 generate `Assets/NativeShaders/CameraRelativeVariants/OriginalVariants.shadervariants`; line 40 also adds every recovered target shader to Always Included. The helper retains all fog/lightmap modes and instancing where needed.
- Line 51 appends that generated collection to `m_PreloadedShaders`. That is the additional startup warmup action.
- Lines 55–66 filter illegal combinations and throw if any expected reachable original key is absent from a shader/pass/stage callback. Single-pass stereo remains inactive for the separate-eye renderer.
- `script/NativePortBuild.cs:196` calls Configure before BuildPlayer. The latest log reports 109 recovered shaders and 28,298 generated SVC members.
- Original `recovered-full/ExportedProject/Assets/Scripts/Assembly-CSharp/FistVR/GM.cs:477` calls `ShaderVariants.WarmUp()` from Awake. This must remain unchanged.

Unity's [2021.3 Graphics settings documentation](https://docs.unity3d.com/2021.3/Documentation/Manual/class-GraphicsSettings.html#Always) says Always Included retains every keyword set, including shader_feature variants. Its [shader loading section](https://docs.unity3d.com/2021.3/Documentation/Manual/class-GraphicsSettings.html#shader-loading) describes Preloaded collections as startup prewarming. These support the inference that this generated collection need not be eagerly warmed to retain its compiled variants; that inference still requires an A/B build check before changing the port.

Unity's [2021.3 shader-loading documentation](https://docs.unity3d.com/2021.3/Documentation/Manual/shader-loading.html#prewarming-shader-variants) also states that Metal warmup without the exact vertex layout and render state can create an inaccurate GPU representation, with later rendering still able to stall. Therefore the generated SVC's broad startup warmup does not establish complete runtime pipeline readiness.

VR's independent packaged binary inspection identified original `H3_ShaderAsset3` at `globalgamemanagers.assets:2201` (94 shaders / 841 variants) and generated `OriginalVariants` at `:2200` (109 /28,298), both preloaded. Startup samples were initially `/tmp/nativeh3vr-player-86038.sample.txt` and `/tmp/nativeh3vr-metal-86064.sample.txt`, with archival planned under `audit-vr/startup-f13e1c6d/`. They implicate warmup/Metal compilation but do not time each collection separately; do not attribute the whole 190.95 seconds to the generated collection.

## Bound compilation evidence

The accepted player `build/native-build.log` contains 2,848 retention callback rows, 1,424 distinct shader-path/pass/stage identities across 109 shaders, and zero missing variants. Summed reachable and kept counts are both 66,860; callback repetitions mean that is not a unique runtime-variant count. Its direct compiler-log gate passed.

The accepted bundle log contains 690 callback rows / 690 distinct identities across 58 shaders, zero missing variants, and 20,244 reachable/kept entries. Its compiler receipt run ID is `fb871d397d8849a2b4c9e94fe48a6c51`, with 81,052 source files and 64 managed inputs. Both the variant helper and NativePortBuild are captured managed inputs: modifying either invalidates the current receipt's source fingerprint. No existing acceptance receipt should be relabeled after a change.

| Evidence path | SHA256 |
|---|---|
| `shaders/NativeOriginalVariantFilter.cs` and staged copy | `c32d025fcc2986033539702df3b596d9a932e0cb008469b27e441614f6c4179a` |
| `script/NativePortBuild.cs` | `f0a61d709de5a4ea7d0663ff2f6e3e81d9b8dc4934e11e4027a094559df3affc` |
| `build/native-build.log` | `83fac86d09b71cb39abeedb8da1ef161ce2be4c6a7c71646d797a0652b8a9bbd` |
| `build/native-player-compiler-verification.json` | `a5f1daba31febc6ae16460c96b7e05f1fc4536d5b7bbd89e5a1aaef3aebab724` |
| `build/StreamingAssets/native-bundle-compiler.log` | `0d45519321ca7090b553e1fd4298646fc22eafca433db96a8f6d0fea34f086c6` |
| `build/StreamingAssets/native-bundle-compiler-receipt.json` | `8736980807bf1682a57cccbbb004a8c080a73949dac5ebd94d9dd7e9feeda645` |
| Original `FistVR/GM.cs` path above | `1592351b018035cfb1bdcc6ba049f6a6ac660f519de15b3f7e19222fad6779bf` |

Counts can be reproduced from `NATIVE_ORIGINAL_VARIANTS` lines using `(asset path, pass identity, stage)` as the identity and reading their `reachable`, `kept`, and `missing` fields. Do not count keyword sets from SVC member count alone: SVCs omit stage and distinct named-pass identity.

## Smallest future candidate and proof strategy

If repeated cold startup remains problematic, the smallest defensible candidate is to remove **only the generated collection object** from the preload array after Configure builds it, preserving the asset and its members, all original preload entries in their original order, Always Included, stripping settings, callback validation, and GM.WarmUp. Merely removing line 51 is insufficient for an already-configured project: its existing generated entry would remain. Changing global preload timing would also change original warmup timing and is outside this narrow proposal.

Before accepting that candidate:

1. Preserve the current accepted build and create an isolated A/B build with exactly the generated-preload-reference difference. Assert original 841 members, original preload identities/order, GM bytecode, shader sources, keyword policies and Always Included are unchanged.
2. Compare every distinct shader/pass/stage callback identity against the current 1,424 player baseline, and assert the complete reachable key set remains present for every callback, not merely aggregate counts. Keep fail-closed missing-key and direct compiler-log checks. Verify actual packaged shader program/keyword inventory equivalence, allowing serialized layout/order differences only through exact identity mapping.
3. Draw/read back the existing complete mono variant manifests in the resulting native player, including paths beyond the first scene. Measure process launch to first camera frame and first-use rendering stalls under stated cache conditions. Preserve original warmup calls and compare original collection membership from the packaged result.
4. Create new accepted compiler/content receipts for changed inputs. A faster startup alone proves neither variant retention nor gameplay/visual parity.

This is a proposal and proof strategy, not an implemented or validated optimization.
