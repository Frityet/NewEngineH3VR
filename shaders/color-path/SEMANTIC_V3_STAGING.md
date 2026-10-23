# Semantic v3 shader candidate staging

`stage_semantic_v3_candidate.py` prepares and optionally applies only the corrected
Semantic v3 ShaderLab sources and the includes those sources reference. Its default
mode is plan-only. It does not invoke Unity or touch bundles/player files.

From the repository root, produce a fresh review plan with:

```sh
python3 shaders/color-path/stage_semantic_v3_candidate.py
```

Review `semantic-v3-stage-plan.json` before integration. The current reviewed plan
contains 153 shader sources, 17,869 content-addressed includes, and 380 final overlay
replacements (six IEEE repairs and 374 VTP include repairs). It lists 18,022 unique
targets: 11,219 would be written and 6,803 already match. Of the include targets,
9,298 are new paths and 1,848 existing paths need updated bytes; four further
existing paths already match their final overlay bytes. The overlay layer replaces
bytes at those exact referenced paths; it does not introduce a second include
namespace.

Before any write, `--apply` rechecks the candidate/overlay/selection manifest hashes,
all staged-source hashes, every target's captured pre-stage hash, the original
ShaderLab identity, and the full `.meta` hash plus GUID for all 153 shader assets.
Shader metadata is never written. The planner also requires the staged include set
to equal the references of the 153 candidate shaders and scans `unity-full/Assets`
to reject any candidate include path referenced by a retained shader outside that
set. Package bytes are the first layer; overlays are anchored to the exact package
hashes and become the final layer at the same paths. Missing include files are
created only at their content-addressed paths.

The plan was originally generated in plan-only mode. After reviewing it, the parent
integration owner applied that saved plan. The resulting receipt is
`semantic-v3-stage-receipt.json`: it records 11,219 writes and confirms no bundle or
player files were touched. The saved plan and receipt retain the original target
hashes and GUID evidence.

The apply command used for that one-time staging operation was:

```sh
python3 shaders/color-path/stage_semantic_v3_candidate.py --apply
```

Do not rerun `--apply` against the now-staged tree: its captured pre-stage hashes are
expected to fail, preventing an accidental second application. Unity 2021 shader
compilation and bundle/player validation remain separate required steps.
