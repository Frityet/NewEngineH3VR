# Input lookup caching and transient eye-query recovery

The live [busy-scene traces](BUSY_PROFILE_2026-10-23.md) identified SteamVR input
callbacks as the largest recorded allocation parent. The new engine adapter
caches launch/session mode, manifest-derived action names and set handles, and
input structure sizes behind `#if NEWENGINE_UNITY_2021`. Shared gameplay CPU
algorithms remain in the architecture- and OS-independent OptiH3VR mod.

The cache retains the original input polling, focus and freshness rules, action
edges, active-set ownership and snapshot copies. Manifest reloads rebuild cached
bindings after successful or partial registration, preserving old handles and
types. Malformed paths retain the original query exceptions. New cache fields
live in nested types, preserving existing adapter layouts. The normal managed
build now passes the engine gate to the SteamVR adapter as well as the XR helper.

`NATIVEH3VR_OPENXR` is a launch/session choice in this gated path. A new session
refreshes the choice; an environment change during an existing session does not
switch its backend. The off-gate branch retains the previous behavior.

## Eye-query recovery

The earlier user session returned to Home after `eye views invalid (-1017)`.
The query had latched an error into the asynchronous submission status even
though its managed caller already retains the last calibrated eye transforms
when the query fails. The renderer subsequently read that status outside its
frame-handling try block, terminating its coroutine. This establishes a software
failure path, not the cause of the initial tracking validity loss.

The native query now returns its transient error without changing output views,
the previous error string or any existing fatal status when a successful
`xrLocateViews` temporarily lacks valid orientation or position. Invalid session
state, wrong stereo counts and API failures remain fatal. Initial calibration
still requires valid views. The frame-submission validity policy is unchanged.

## Checks completed

- The input candidate passed 8,015 assertions; the off-gate version passed 8,008.
  Cases include action transitions, restricted sources, missing devices, focus
  loss/neutral rearm, retained snapshots, manifest reloads and malformed inputs.
- Forty-nine IL/layout checks passed, including unchanged input gating and
  tracking-refresh bodies. The installed-to-candidate ABI check retained all
  eight existing types and their assembly references.
- A calibrated .NET 10 counter measured 5.6 MB for 50,000 original warm digital
  queries and zero for the cached candidate, in four alternating batches. Two
  guard checks plus a query measured 12 MB versus zero per 50,000 iterations.
  These are host microbenchmarks, not Unity Mono allocation or FPS results.
- The native regression test exercises the production query with an injected
  `xrLocateViews`. It passes transient/recovery, bad-count, API-error and
  initialization cases. The same test fails against the exact old source.
  The ARM64 dylib builds with warnings treated as errors.
- A separate application was cloned using APFS copy-on-write with no full-copy
  fallback. Only the adapter, two native XR plugin copies and signature data
  changed. Its audit passes 25 ARM64 binaries, 126 managed assemblies, retained
  adapter ABI and strict signatures. Main game assemblies, scope fixes and
  assets remain byte-identical to the accepted build.

Two actual candidate player routes passed sequentially with synthetic OpenXR:
original range loading, pistol grip, a live-round shot and moving projectile,
release, wrist-menu interaction, and quickbelt store/retrieve. Both processes
exited normally without cleanup signals or unresolved errors. Root reviewed all
four desktop/compositor images; the periodic stereo images prove rendered eye
views, not the exact shot or menu-interaction moment. The wrist button test uses
the original `OnPoint` route and does not establish a physical trigger press.

The first startup spent 193 seconds before readiness; a bounded process sample
located the delay in Metal shader compilation before adapter initialization.
The second startup took 12 seconds. These are diagnostic observations, not an
optimization comparison. Both app inventories remained unchanged after testing.

## Actual Unity allocation comparison

Four sequential, eight-second captures ran in A/B/B/A order, using the accepted
app for A and the candidate for B. Each used the original indoor range, identical
recorded settings, synthetic OpenXR and inactive diagnostic controllers.
Screenshot readback was disabled. Captures finished during a 45-second yield-only
warmup, before the performance fixture allocated its sample array. All four
captures completed for the requested duration, retained their scene and restored
profiler settings; all players exited cleanly.

The unchanged analyzer read 600 uniformly selected frames per raw file through
the intended isolated Unity Editor and MCP. Every selected frame completed with
no cap, partial frame or issue; all `GC.Alloc` samples had declared Int64/Bytes
metadata. The profiler history setting was restored after analysis.

| Main Thread callback | A1 and A2: allocation calls / bytes per callback | B1 and B2: allocation calls / bytes per callback |
| --- | ---: | ---: |
| `SteamVR_Behaviour.Update` | 5 / 268 | 3 / 188 |
| `SteamVR_Render.Update` | 6 / 240 | 0 / 0 observed |
| `SteamVR_Behaviour.LateUpdate` | 18 / 836 | 0 / 0 observed |

Each arm recorded 599 `SteamVR_Behaviour.Update` callbacks, 1,198
`SteamVR_Render.Update` callbacks and 599 `SteamVR_Behaviour.LateUpdate`
callbacks. The first callback's bytes fell 29.85%, with 40% fewer allocation
calls, identically in both repeats. The other zero observations are supported by
complete allocation-parent groups: their summed counts and bytes equal each
report's global allocation totals. Both recorded ancestor paths are included for
the baseline LateUpdate total.

These measurements cover the exercised synthetic callback paths. Inactive
controllers do not reproduce the much larger live-controller workload or prove
zero allocations for every path in those callbacks. Inner digital/analog query
counts and allocation object types are not recorded. The separate host tests
cover warm action lookups; no live action-query savings or overall FPS gain is
extrapolated from this synthetic comparison. A physical test of the transient
eye-query recovery remains outstanding.

Both app inventories remained unchanged during all four runs and analysis.
The isolated analysis Editor then exited, with no test player or Editor left
running.

## Installed build

After reviewing the complete comparison, the exact tested files were installed
into `dist/NewEngineH3VR.app`. Its final signature and inventory audit matches
the tested candidate exactly. The change consists of the adapter, two XR plugin
copies and existing signed launcher/resource signature data. Launcher code
outside its embedded signature is unchanged. No re-signing or rebuild occurred
during promotion, so the delivered code is the code exercised by the tests.

The previous five files and original audit are saved under
`build/optih3vr/input-recovery-promotion-20261023T183928Z/`. The promotion receipt
binds the successful A/B receipt and is bound in `build/native-app-audit.json`.
The original game, OptiH3VR helper, scope/stereo fixes and assets are unchanged.
The standalone candidate remains available. The updated main app has not been
relaunched on the physical headset; the earlier physical scope/lighting
acceptance applies to its unchanged visual fixes.

## Reproducible evidence

- Input tests: `native/input-lookup-tests/`; frozen originals, candidate and
  off-gate outputs and hashes: `build/input-lookup-candidate-3/`.
- Candidate adapter SHA-256:
  `1722f5ab53abad64388b03964d07faec196ba996ea5bf9784b763849f5e95eac`.
- Eye-query test: `native/tests/eye_view_query_test.mm`; compiler commands,
  original source, regression failure and candidate: `build/new-engine-eye-view-query/`.
- Candidate app: `dist/NewEngineH3VR-InputRecoveryCandidate.app`.
- App receipts: `build/optih3vr/input-recovery-candidate/`, including
  `candidate.json`, `clone.json`, `adapter-abi-check.json`, `candidate-audit.json`,
  `synthetic-tests.json`, `post-tests-candidate-audit.json` and
  `root-visual-review.json`.
- Candidate inventory SHA-256:
  `a1d4d046dc519b52d2eb0856e2bb9c4c9d909c9e65a8c52d7d06bc4d34cc6ae9`.
- Accepted baseline inventory SHA-256:
  `a41bec73f013dc99de7dce7a95e45ff560d22b971c9cab14016b6dc8e251bf61`.
- Actual Unity comparison: `build/optih3vr/input-recovery-ab-20261023T183828Z/`;
  each arm retains its scene/run/capture validation, raw file, frame CSV,
  runner receipt and `analysis-uniform600.json`. The final `ab-verification.json`
  SHA-256 is `35b8202446fbecc1ff6dc89a85aad7e47bdbcc1a571c2d406e6a3512c291d5ca`.

The runner accepts `--app` and `--audit-report` for a generated application and
its exact saved audit, keeping candidate tests separate from the accepted app.
Historical source-bound engine and live-profile receipts remain unchanged.
