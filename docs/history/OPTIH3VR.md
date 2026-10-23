# NewEngineH3VR and OptiH3VR

Checkpoint: 23 October 2026. The signed local player is
`dist/NewEngineH3VR.app`, with `NewEngineH3VR` launcher,
`NewEngineH3VRPlayer` Unity executable and `StreamingAssets/NewEngine` runtime.
Its audit contains 25 ARM64 binaries and 126 managed assemblies. Existing save,
serialized assembly and asset identities are retained. The original Steam
installation was not modified.

## Implementation

The sibling `../OptiH3VR` mod owns the CPU algorithms. Its 0.3.0 regular BepInEx
package and the port share the same source for lazy melee sets, squad debug-draw
removal, cover-path reuse, patrol short circuit, dead sonic-queue cleanup and
the new scalar four-value maximum. The latter removes a temporary `float[4]`
in player and Sosig updates while preserving the original ordered comparisons,
including NaN payloads and signed zero. None of those algorithms depends on
macOS or ARM64.

`tools/prepare_new_engine.py` in the mod prepares the port's seven target
methods offline. It retains a private original-method clone for each target;
`OPTIH3VR_ENABLED=0` selects those baselines within the same package, while the
default and `=1` select the optimizations. The helper is
`OptiH3VR.NewEngine.dll`, with engine-specific selector code gated by
`#if OPTIH3VR_NEW_ENGINE`. The player needs no runtime Harmony detours. Experimental
physics-overlap and gun-sight patches remain disabled.

The port's graphics change caches the two persistent eye texture pointers and
the render-event function pointer. It refreshes an eye pointer after recreation
or a size replacement, and prevents the desktop mirror from recreating a lost
target ahead of that invalidation. `NATIVEH3VR_CACHE_EYE_POINTERS=0` retains the
previous per-submission pointer queries. Resolution, materials, image effects,
physics and AI cadence are unchanged by this graphics optimization.

`script/build_and_run.sh` installs the OptiH3VR overlay after packaging a fresh
player. `script/install_optih3vr.py` can update the recorded package independently;
it verifies source/output hashes, unchanged serialized contracts and signatures,
preserves the unmodified game assembly and rolls back a failed installation.

## Verification completed

| Evidence | Result and scope |
| --- | --- |
| Shared maximum tests | 258,650 host assertions against original Unity implementations and game initializer layouts; bitwise edge and random cases |
| Offline adapter | 925,361 preservation assertions; 881,385 assertions that explicit compile gating retained emitted behavior; original method bodies and untouched members retained |
| Mod test/build suites | 42 Python checks, seven C# suites, regular BepInEx build/package and separate runtime-fixture build passed |
| Final scene-script proof | All 46 shipped scene mappings pass, zero script-reference errors; `build/scene-integrity/full-player-script-proof.json` |
| Broad runtime sweep | 46 optimized scene attempts: 30 strict passes, 16 failures; all 16 also fail with patches disabled, 15 with identical error lists |
| Final optimized range | Core and maximum fixtures, cached eye-resource recreation, scene rendering, captures and verified exit pass |
| Final disabled range | Original dispatch, maximum fixture, uncached eye-resource recreation, scene rendering, captures and verified exit pass |
| Final Institution | 36 real NavMesh path cases, up to 102 corners, 31 saturation/undersized probes, six complete cover-search comparisons and cleanup pass |
| Profiler plumbing | A two-second synthetic binary capture records 331 observer samples, writes 17.9 MB and restores profiler settings; this is diagnostic validation, not a benchmark |

The 62 broad sweep/replay attempts used one identical pre-rename package and
the same seven CPU algorithms. Their logs, run IDs and binary inventories are
bound in the receipts; all process exits were verified. The final public-name
and graphics-lifetime changes were then checked with the final range and
Institution runs. The sweep is loading/rendering coverage, not completion of
every game mode, weapon or interaction. See [failure triage](OPTIH3VR_FAILURE_TRIAGE.md)
for authored reference defects, convex cooking failures and the differing
Grillhouse audio/convex failures. None was converted into a pass.

Actual Unity Mono microbenchmarks in the isolated range measured the original
four-value maximum at 3.002 ms and the scalar helper at 0.512 ms per 100,000
calls (medians of six alternating pairs). This is a method-level result, not an
FPS gain. The cover-search benchmark was effectively unchanged: 96.72 versus
96.91 ms per calibrated batch. Unity Mono exposed an allocation-counter API
that always returned zero, so a retained-64-KiB calibration correctly marked
allocation measurement unavailable. No zero-allocation runtime claim is derived
from that broken counter.

The cached renderer made zero additional pointer queries during stable-frame
observation and exactly one after each eye was independently released. Both
cached and uncached modes recovered the same texture wrappers, dimensions,
camera and tracking origin with continued stereo submissions.

The first renamed-package attempt hit a 120-second cold-start timeout while
warming Metal shaders. A process sample identified active Apple shader
compilation; a retry with a 600-second startup allowance completed all range
checks. The failed attempt remains recorded. A separate initial cover fixture
failed before any NavMesh operation because it searched only private methods;
the helper is public after offline preparation. Its old DLL and receipt were
preserved, the exact binding was fixed, and the real rerun passed.

## Evidence and live profiling

- `build/optih3vr/all-scenes/`, `failed-scenes-baseline/` and
  `scene-failure-differential.json`: full scene attempts and differential failures.
- `build/new-engine-app-migration/`: public-name/runtime-layout migration and
  byte-preserving adapter path proof.
- `build/optih3vr/installed.json`: current mod install, pristine baseline and hashes.
- `build/optih3vr/new-engine-final-extensions/`: final graphics, telemetry and
  profiler assembly contracts, source hashes and signature audit.
- `build/optih3vr/new-engine-validation-warm/`,
  `new-engine-validation-baseline/`, `new-engine-cover-fixed/`: final actual-player
  checks and images.

The opt-in Unity 2021 profiling controller is gated by
`#if NEWENGINE_UNITY_2021`. Setting an absolute `NEWENGINEH3VR_PROFILE_DIR` arms
it without recording. Its ready file provides a unique PID/run-bound request
path. A request records 1–60 seconds of Unity binary profiling, scalar recorder
metrics and observed stereo cadence, then restores the prior settings without
closing the game. It never supplies controls or changes scenes. Recording has
overhead, and Unity elapsed marker times include waits; they are not OS CPU
consumption or physical headset scanout. The offline analyzer and invocation
instructions are in `platform/profile-analysis/`.

The first live session exited before a capture request was sent: the controller
remained armed while awaiting a ready signal. The user reported that performance
was great and identified peripheral lighting in Meat Fortress 2 that followed
the edges of the view while turning. They subsequently reported that magnified
scopes display black instead of a view, identifying the C7A2's EC79A2 optic.
Smoothness is user-reported rather than measured FPS evidence. The session log and feedback are retained
under `build/optih3vr/live-sandbox-20261023T145350Z/`.

After the user's tracking reset, the controller was extended to
publish the active scene and a timestamp once per second while idle, allowing
a bounded supervisor to start captures after scene entry. It pauses periodic
status writes during capture. Existing field layouts and the signed package
passed the extension check. A completed live range capture subsequently
validated the scene-aware workflow. The initial MF2-only supervisor missed the
range session; that unsuccessful attempt remains recorded. The scheduler now
defaults to selecting any of the 45 original non-menu destinations, with an
explicit scene option for custom maps. Its 19 pure tests pass; the automatic
selection update has not yet been exercised in a live capture.
Synthetic A/B frame windows must exclude PNG readback, test fixtures and profiler
instrumentation. No measured overall FPS gain is claimed at this checkpoint.

The EC79A2 black view has since been reproduced and corrected in the actual
player: a recovered `ZClip Off` default caused self-occlusion. Eight subsequent
scope cases passed their rendering observations, including the original C7A2
mounted assembly in both eyes and a Classic scope regression check. Independent
PNG/receipt verification passed all eight plus the shader-only case (9/9).
A separate
engine-gated lifetime fix reduced observed scope targets from seven to one in
the same seven-render A/B. The post-processing packed-stereo policy now matches
the port's separate eye targets. See [scope and stereo evidence](SCOPES_AND_STEREO.md)
for precise scope and unchanged-byte proofs. The user subsequently confirmed
both visual issues look fixed. A valid 30-second active-play capture of the
indoor range completed with profiler settings restored. The user subsequently
populated Winter Wasteland Take and Hold, and
[two valid busy-scene segments](BUSY_PROFILE_2026-10-23.md) recorded 30.084
seconds total with settings restored. These are separate workloads; MF2 was
not profiled.

The [live profile report](LIVE_PROFILE_2026-10-23.md) records the full-session
scalar observations and a 600-frame sample across the raw file's loaded range.
It identifies SteamVR input callbacks as the leading named allocation parent,
while separating substantial Unity profiler overhead from gameplay costs.
No unprofiled or overall FPS gain is inferred from that capture.

The subsequent [input and recovery update](INPUT_RECOVERY_2026-10-23.md) is now
installed. Its engine-gated adapter caches passed semantic and ABI comparisons,
two actual-player interaction routes, and four controlled synthetic Unity
allocation captures. Recorded input callback allocations fell from 268 to 188
bytes per update; two other SteamVR callbacks had no recorded allocations in
the candidate samples. Inactive synthetic controllers limit those observations
to the tested paths. Live action-query savings and headset FPS remain unmeasured.
Shared gameplay CPU algorithms remain in OptiH3VR, and the engine adapter retains
the previously tested scope/stereo code and assets.
