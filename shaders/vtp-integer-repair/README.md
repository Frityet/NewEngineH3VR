# VTP Glass additive integer repair — isolated, not staged

The candidate repairs 374 existing recovered includes for original `globalgamemanagers.assets:45`, `VTP/Glass/2 Texture blend (2-sided)`. It changes no top-level shader, material, property, pass, keyword, dependency path, or `.meta` file. Full-project shader GUID is `7a3470a5ec5753f4182da7d2e7da18a3`; main-project GUID is `a17052d3e88ef4542830e0f4dd4cc711`. Both working projects still match every candidate **before** hash at preparation time. The parent bundle build is active; this report authorizes no mutation.

The actual original game preloads this shader through `GraphicsSettings` → `H3_ShaderAsset3` → shader 45, with `DIRECTIONAL` in pass types 4/5 and `SHADOWS_DEPTH` in pass 8. No original visible material/draw consumer has been established. See [binary reachability correction](../../build/shader-resource-audit/vtp-glass-reachability-correction.json). Earlier claims of no references omitted `.shadervariants`; preload is a real reference.

## Exact repair and original evidence

[Original-to-candidate map](original-to-candidate-map.json) binds all 378 stage-pair includes to 366 original pixel programs; 374 changed includes correspond to 362 affected pixel programs. Each affected original program has three integer-one AND gates, three xor 2/sign/half setups, and six integer loops. The other four programs require no repair. [Original manifest](original/manifest.json) binds the DXBC and disassembly; [audit_original_patterns.py](audit_original_patterns.py) and [prepare_candidates.py](prepare_candidates.py) enforce those pattern counts and original integer uniform metadata.

The recovered code numerically converted bit masks and stored integer counters in float temporaries. The additive repair preserves the original operations:

- Predicate AND literal integer 1 becomes numeric 0/1 for the following integer-one equality, instead of the broken conditional with both outcomes 0.
- `_ParallaxInterpolation` remains its reflected integer type. xor 2, sign-bit selection, wraparound negation, unsigned shift and signed half use typed integer operations; ordinary float uniforms are not reinterpreted.
- Both loops per layer use integer comparisons and wraparound counters, retaining the existing float mirrors where downstream floating arithmetic consumes them.

There are 1,122 typed setups, 1,122 gates and 2,244 loop-counter repairs. The 379 candidate files comprise the unchanged shader, 378 includes, and 374 changed includes. Candidate manifest's original `validated:false` and pending-oracle text are frozen preparation-time state; the subsequent hash-bound proofs below establish the completed validation without rewriting that historical input.

## Native proof

[Arithmetic receipt](../../vtp-integer-probe/output/verification.json), SHA256 `26b05530d92234847cdd6f76e0c3901685857861a6d6e13d11a7ad6dc2d45bee`, binds 420 files. On ARM64 / Apple M5 Max / Metal:

- 324 full parallax cases execute the exact candidate block against an independent interpreter of original pixel program 114's DXBC instruction slice; maximum absolute error is 2.9802322387695312e-08.
- 32 signed setup cases and 8 bounded integer-counter cases match exactly, including integer extremes and values beyond float's exact integer range.
- Exact old-source negatives fail 88 parallax, 23 setup and 3 counter cases, demonstrating sensitivity to the repaired defects.

[Whole-family receipt](../../vtp-family-probe/output/verification.json), SHA256 `cabab9c34f956bdbbc1f0be6219bafccfc9ac51097f77f20d719dfde3f827b17`, binds 1,364 files, including the arithmetic receipt. The actual complete shader and its subshader clone pass 376/376 mono draw/readback branches; 40 retention callbacks report zero missing branches. Build and player direct compiler-log gates are clean. Four META branches are excluded from gameplay draws; 376 single-pass-stereo branches are inactive for the current separate-eye-camera renderer. All 379 candidate files are byte-identical to the tested copies.

`prepare_staging.py` rechecked all 420 arithmetic and 1,364 family fingerprints when creating the plan. Arithmetic proof covers program 114's parallax block and the setup/counter slices, not complete lighting equivalence for every original variant. The initial fixture's exact height/depth ties exposed CPU/GPU rounding-dependent branch boundaries; rejected evidence is retained and the final vectors avoid those ties. No claim of equality-boundary parity, complete gameplay acceptance, or physical VR hardware validation is made.

## Staging plan and bundle boundary

[staging-plan.json](staging-plan.json), SHA256 `72680388f5ece88df1d1f34bb4164bcf4218b94394d6aa2f4a7d375401c9d6c6`, contains exactly 374 before/after include changes and five unchanged files, the preserved shader/meta fingerprints, original map/proof hashes and both project identities. `before/` snapshots the 374 baseline includes. Full project has 378 existing include metas; the main project currently lacks those generated include metas. Staging must preserve both existing bytes and that absence, with no refresh or new asset paths.

The two archived compiler receipts both contain 81,052 source files and 64 managed inputs. Neither contains any of the 374 modified paths, nor the unchanged VTP top-level shader. Their recorded hashes are:

| Archived attempt | Compiler receipt SHA256 | Changed-path overlap |
|---|---|---:|
| `semantic-collection-order` | `fc9c4133d58312e683036ce7205876ac926063cda90a9824904019efc31ca069` | 0 |
| `semantic-manifest-lifetime` | `aae519a6536c3edea1dbc3ee326123227e3ec347014116c45d762935e99f0de0` | 0 |

These are historical disjointness observations, not acceptance of the active build. At the parent checkpoint, the staging gate must verify the **fresh accepted** bundle proof and compiler receipt, reject overlap with source/managed inputs, recheck dependency closure and every before/candidate/proof hash, then write a separate additive staging receipt. Preserve the original SemanticV2 manifest and staging-verification receipt unchanged. The isolated IEEE repair is a separate six-file delta and shares no changed path with this repair.

Reproduction without staging: `python3 shaders/vtp-integer-repair/prepare_staging.py` verifies the frozen before state, proofs and archived receipts, then reproduces only this isolated snapshot/plan. Do not rerun it after applying the repair. The separate `stage.py` gate defaults to a read-only dry run; `--apply` requires the parent checkpoint.
