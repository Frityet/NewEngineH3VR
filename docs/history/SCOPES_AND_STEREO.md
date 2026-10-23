# C7A2 optics and separate-eye rendering

The reported C7A2 optic is the original **EC79A2** (`EC79_PIPScope`), loaded
separately from the rifle. Its black image was reproduced in the signed
NewEngineH3VR player and corrected by restoring the shader's depth-clipping
default. CPU optimizations remain in the architecture/OS-independent OptiH3VR
mod; these changes belong to the port's graphics path.

## Cause and isolated correction

The original Unity 5.6 shader has no serialized `zClip` field. AssetRipper's
legacy-field fallback converted that absence to zero (`Off`) and emitted an
explicit `ZClip Off`. Unity 2021 [documents clipping as the default](https://docs.unity3d.com/2021.3/Documentation/Manual/SL-ZClip.html);
disabling it clamps geometry outside the near/far planes and can give incorrect
depth ordering. The EC79A2's own geometry then blocked its magnified view.

The original optic was tested through its normal callbacks and the actual
separate-eye renderer. No test calls `Camera.Render` or forces visibility.
Colored targets are owned diagnostic objects. These are rendering checks, not
physical headset acceptance or performance benchmarks.

| Actual-player experiment | Scope target / final lens |
| --- | --- |
| Original state, axial eye offsets −1 mm, +1 mm and +30 mm | Black despite seven scope-camera renders |
| Original state, hide only owned optic geometry during the scope-camera pass | RGB targets and reticle visible; geometry restored before the eye render |
| Depth-clip correction alone, +1 mm, hiding disabled | RGB targets and reticle visible; original managed rendering binaries unchanged |
| Corrected state plus eye-pair lifetime handling, left/right, −1/+1/+30 mm | RGB targets visible in every tested case |
| Original C7A2 + EC79A2, original top-rail attachment API, both eyes | RGB targets and reticle visible; front sight also visible in the magnified image |
| Original Classic 3–12×42 scope | RGB targets and reticle visible |

The mounted fixture uses the original `CanAttach`, `isMountableOn`,
`ScaleToMount` when applicable, and `AttachToMount`/`DetachFromMount` APIs. It
verifies the original asset GUIDs, unique top rail, hierarchy and registration,
then aligns the complete owned rifle/optic assembly. It does not exercise hand
interaction or firing. Cleanup and deregistration passed in both eye cases.

The app-only asset overlay changes exactly two `zClip.val` values, `0→1`, in
the EC79A2 PIP shader's `FORWARDBASE` and `SHADOWCASTER` passes. Independent
fresh parsing verifies four changed uncompressed bytes, two recompressed blocks,
12,508 untouched compressed blocks, 21,845 preserved object identities/metadata,
and byte-identical payloads for the other 21,844 objects. The 2,694,845-byte
compiled shader program blob and all resource bytes are unchanged.

`script/install_shader_state_overlay.py` preserves the original bundle, verifies
the candidate against the frozen Unity build and original shader, and records a
separate signed overlay manifest. It reseals and audits the app, with rollback
on a failed update. The historical Unity bundle proof and its adjacent Unity
manifest hashes describe the original build and are deliberately unchanged;
the separate overlay SHA256 binds the delivered corrected bundle. The full
build entry point applies this correction after baseline verification and before
packaging. A future source-corrected bundle requires retiring the historical
Off-to-On overlay; the tool refuses an already corrected or differently authored
state instead of guessing.

The isolated source repair tool, tests and provenance are documented in
[`shaders/LEGACY_DEPTH_CLIP.md`](../shaders/LEGACY_DEPTH_CLIP.md). It produces
reviewable candidates without changing the frozen staging project or Steam
installation. The current runtime overlay is limited to EC79A2; it is not a
claim that all recovered shaders have been repaired.

## Scope texture lifetime and post-processing

The original activation pass runs separately for each native eye. A scope
selected for one eye could be disabled by the other eye, invoking
`DestroyRT`/`CreateRT` again every frame. The port now keeps a scope selected by
either eye active until the pair ends; scopes selected by neither eye still
disable. GameObject disable/destruction and normal options-driven recreation
remain original behavior. Pair cleanup is protected by a `finally` block.

An actual-player A/B with the same corrected shader and managed binaries used
seven distinct scope targets with `NEWENGINEH3VR_SCOPE_PAIR_FIX=0`, and one
target with the fix enabled, across seven observed scope renders. Both modes
showed RGB targets in the final lens. This demonstrates avoided resource churn;
it does not quantify an FPS gain. Host lifecycle checks also found no allocation
in 10,000 warmed stable pairs, including expanded scope capacity. Unity Mono's
allocation counter is unreliable on this build, so no runtime zero-allocation
claim is made.

The original post-processing `isSinglePassStereoEnabled` getter always returned
true. NewEngineH3VR renders complete, separate eye targets. The engine adapter
now returns false during those eye renders and preserves the original policy
outside them. `NEWENGINEH3VR_PACKED_STEREO_FIX=0` restores the old policy for
diagnosis. This corrects a demonstrated target-layout mismatch. The user later
confirmed that the view-edge lighting looked fixed during the live range test.

Both managed helpers are gated by `#if NEWENGINE_UNITY_2021`. The prepared
engine baseline changes only that getter and three exact scope activation
setter call sites. Independent checks preserve 20,761 other game method bodies,
existing serialized layouts, and 32 other XR method bodies. The seven OptiH3VR
CPU patches are reapplied over that independently checked engine baseline.

## Evidence and remaining validation

- `build/optih3vr/scope-ec79a2-{minus1,plus1,plus30,detailed,hide-own}/`:
  preserved failing cases and geometry-isolation experiment.
- `build/optih3vr/scope-ec79a2-zclip-only/`: shader-only actual-player comparison;
  hiding is disabled, full eye and lens images manually reviewed.
- `build/new-engine-depth-clip/overlay-ec79-v1/`: original bundle, candidate,
  independent object/program/block proofs, signed installation receipt.
- `build/new-engine-scope-pair/pristine/`: engine candidates, 55,124 preservation
  assertions, lifecycle/admission tests, negative CLI fixtures and source hashes.
- `build/optih3vr/scope-render-matrix/`: eight completed range cases, app audit,
  exact case environments, logs and scope/eye/lens PNGs. Mounted left/right and
  Classic lens images were manually reviewed. Independent verification passed
  all eight cases plus the earlier shader-only case (9/9). The checker decodes
  PNGs, checks nontrivial RGB presence in the scope target and final lens, binds
  hashes/PIDs/run IDs, checks freshness and clean exit, rejects own-mesh hiding,
  and verifies original mounted-API registration and cleanup. It has 12 passing
  fixture tests. `scope-render-verification-summary.json` links each fresh
  verification receipt. These are rendering-sanity checks, not optical parity.
- `build/optih3vr/scope-mounted-v6-extensions/` and
  `install-20261023T172527Z-504c927c/`: installed managed extensions and OptiH3VR
  composition. The final app audit contains 25 ARM64 binaries and 126 managed
  assemblies.

The exact mathematical eye-relief plane remains an original singular case:
the original visibility calculation rejects a zero vignette scale. The fixture
at precisely zero offset is retained as a failure and is not used as successful
coverage. This correction does not rewrite that original optical algorithm.

The user confirmed tracking reset, re-established floor height, and then
reported **"Finished — both look fixed"** for the C7A2 scope and view-edge
lighting. This is physical visual feedback from the user, distinct from the
synthetic checks. The recorded live gameplay scene was the indoor range; no MF2
performance or busy-Sosig workload claim follows from this session.

The initial scene supervisor was restricted to MF2 and therefore did not fire
when the user entered `IndoorRange_Updated`. That missed interval is retained
as an unsuccessful capture attempt. After explaining the miss, the user agreed
to another 30 seconds of active play. A new PID/run-bound request recorded the
current range scene for 30.0018 seconds, with 6,015 scalar observation samples
and a 462,385,443-byte Unity binary trace. The scene remained constant, the
duration limit ended recording normally, and the previous profiler settings
were restored. The independent supervisor accepted the completed capture.

Evidence is under `build/optih3vr/live-mf2-optics-20261023T173211Z/`, specifically
`user-playtest-feedback.json`, `profile-current-map.json`, and
`profiles/live-20261023T173216Z-83641-4fdeb209/capture-01-d78aa0f7/`. The directory
name reflects the originally planned map; the receipts and samples correctly
identify the actual indoor-range workload. The [live profile report](LIVE_PROFILE_2026-10-23.md)
records raw marker analysis, instrumentation overhead and input-path findings.
The scheduler now defaults to automatic selection of all
45 original non-menu destinations and also accepts an explicit map name.
Pure tests cover the indoor-range request, acknowledgement and completed
capture, as well as rejection of an unknown map with an actionable selection
hint. This scheduler change has not been used for another live capture.
Profiling overhead was present throughout the capture;
Unity frame and submission observations are not physical headset scanout/FPS.
