# Active indoor-range profile, 23 October 2026

The user played for an additional 30 seconds after confirming that the C7A2
scope and peripheral lighting looked fixed. The signed NewEngineH3VR player
recorded **30.0018 seconds** in `IndoorRange_Updated`, with **6,015 scalar
observations** and a **462,385,443-byte Unity binary profile**. Recording stopped
at the requested duration and restored the previous profiler settings. The
scene remained constant throughout the capture.

This is an active indoor-range session. The directory name mentions MF2 because
that was the originally planned map; the receipts identify the actual range.
No measured Sosig count or busy-Sosig/MF2 performance result follows from this
capture. The user provided visual acceptance, while the separate automated
[scope checks](SCOPES_AND_STEREO.md) establish rendering sanity.

## Scalar observations

The player used Unity 2021.3.45f2 on Apple M5 Max, with real OpenXR input/output.
The eye targets were 2036 × 2263 each at the configured 200% render scale.
The summary's 1280 × 720 dimensions describe the desktop window.

| Recorded observation | Result | Interpretation |
| --- | --- | --- |
| Main-loop interval | Mean 4.986 ms; median 4.080 ms; p95 9.159 ms; p99 13.155 ms; maximum 93.506 ms | Wall cadence while profiling; not headset FPS |
| Main-thread elapsed marker | Mean 4.980 ms; p95 9.694 ms | Includes nested work, waits and recording overhead |
| Script update elapsed marker | Mean 1.808 ms; median 0.633 ms; p95 5.356 ms | Inclusive callback time, not a single method |
| Physics simulation | Mean 0.137 ms across 3,468 observations | No positive timing on other frames; these are not zero-duration simulations |
| Allocated bytes per observed frame | Mean 21,260 bytes | Whole recorded frame, including instrumentation; source attribution is separate |
| Render thread waiting for commands | Mean 4.366 ms | Waiting, not evidence of expensive rendering work |
| Observed stereo completions | 3,350 over the 29.980-second accepted observation window | Completion-counter observations, not physical scanout |

The first two scalar warmup frames were excluded. Binary profiling remained
enabled throughout the recorded window. Scalar recorder placement is estimated
as the previous completed frame; the raw profile supplies authoritative marker
placement. Draw-call, batch and SetPass counters were unavailable because this
controller rejected their `Int32` recorder type; they are not treated as zero.
The `GC.Alloc` timing recorder is measured in nanoseconds; allocated bytes come
from the distinct `GC Allocated In Frame` counter.

## Raw marker analysis

The completed file was read through Unity MCP in an isolated, idle Unity
2021.3.45f2 analysis Editor. The first read exposed only the last 300 frames
because of the Editor's default history capacity. That initial report is
preserved; its old `fullCaptureAnalyzed` flag incorrectly meant loaded-history
coverage and must not be used as a whole-file coverage claim.

The corrected analyzer temporarily requested a bounded native history capacity
of 20,000, restored the prior value of 300, and did not write shared Editor
preferences. Unity exposed API frame indices **0–6018**. The analysis selected
**600 uniformly spaced frames**, read **1,320,611 samples across 61 threads**,
and completed every selected frame in 6.095 seconds without errors or caps.
The raw file hash remained unchanged. This is a sample across the loaded range,
not every frame: both `fullCaptureAnalyzed` and `fullLoadedHistoryAnalyzed` are
false. The analyzer separately reports loaded-range coverage because the API
does not provide an independently verified total raw-file frame count.

| Recorded marker | Exclusive ms / sampled frame | Inclusive ms / sampled frame |
| --- | ---: | ---: |
| Main thread: `SteamVR_Behaviour.Update` | 1.348 | 1.353 |
| Main thread: separate-eye renderer coroutine | 0.858 | 1.304 |
| Main thread: `Profiler.CollectAudioStats` | 0.794 | 0.795 |
| Main thread: `Profiler.FlushMemoryCounters` | 0.514 | 0.514 |
| Render thread: `Semaphore.WaitForSignal` | 4.475 | 4.475 |
| Render thread: `PrepareRenderTarget` | 0.160 | 0.160 |
| Render thread: ambient occlusion | 0.065 | 0.082 |
| Render thread: bloom pyramid | 0.029 | 0.096 |

These averages divide totals by all 600 selected frames, including frames
without a particular marker. Exclusive elapsed time subtracts immediate
recorded children; it still includes uninstrumented calls, waits and preemption.
Concurrent threads and nested inclusive totals must not be added together.
Root/unnamed thread spans are retained as unknown elapsed time rather than
classified as work. This is a CPU marker trace, not a measurement of GPU cost.

Unity's audio and memory profiler counters themselves account for approximately
**1.308 ms of recorded exclusive elapsed time per sampled frame**. That
establishes material recording overhead. Subtracting this from frame duration
does not reconstruct unprofiled gameplay performance.

A separate read of sampled frame 622 localized its 93.203 ms elapsed duration
to the separate-eye renderer coroutine (90.500 ms inclusive, 89.648 ms
exclusive). The render thread waited for 92.183 ms, while the recorded custom
camera-render children totaled 0.852 ms. This locates the delay outside those
children but does not distinguish native waits, scheduling delays or
uninstrumented work. The single-frame analysis also completed without caps or
errors.

The raw `GC.Alloc` samples expose separate metadata explicitly typed
`Int64/Bytes`. All 239,470 allocation samples had readable byte metadata,
totaling 12,811,559 bytes. Of that total, 8,346,466 bytes (65.15%) have the
immediate recorded parent `SteamVR_Behaviour.Update`, with another 893,412 bytes
under its `LateUpdate`. These are recorded parent scopes, not inferred inner
managed call stacks. The capture controller's own `Update` accounts for 18,548
bytes across five sampled frames (0.145% of the sampled total), consistent with
occasional polling and boundary bookkeeping rather than the recurring
allocation floor. Source inspection confirms that its regular sampling path
uses preallocated arrays and value types; once-per-second polling constructs
`FileInfo` and obtains a profiler-path string.

The strongest next investigation is the input compatibility adapter. Source
inspection found repeated environment-variable reads in its enable guard,
per-query action-name/path substrings, and repeated ABI-size queries. Their
individual costs have not been isolated, so this report does not assign the
whole SteamVR callback cost to those operations or claim a resulting speedup.
Any adapter change should retain action edges, focus, pose freshness and
original input polling, with engine-specific implementation gated by `#if`.
Shared gameplay CPU algorithms continue to belong to OptiH3VR.

Future profiling should also test temporary suppression of unused Audio
profiler collection, with exact prior-state restoration and emitted-call
verification: Unity's setter is conditional on `ENABLE_PROFILER`, which the
helper compiler currently does not define. Keep Memory collection available
for allocation evidence. The current capture policy and running player have
not been changed on the strength of this proposal.

## Evidence

Session directory:
`build/optih3vr/live-mf2-optics-20261023T173211Z/`

- `session.json`, `accepted-app-audit.json`, `player.log`: player/run identity,
  signed package inventory, runtime configuration and original log.
- `user-playtest-feedback.json`: user confirmation and agreement to active play.
- `profile-current-map.json`: successful PID/run/scene-bound supervision.
- `profiles/live-20261023T173216Z-83641-4fdeb209/capture-01-d78aa0f7/`:
  `started.json`, `summary.json`, `frames.csv`, and `unity.raw`.
- Raw SHA-256:
  `84b124fdc51c44470dab72797f5a13b2429fa530433e06e1860ee09953fa8264`.
- `build/profile-analysis-tools/live-mf2-analysis-20261023T174052Z/`:
  preserved `analysis-initial.json`, corrected `analysis-uniform600.json`,
  `analysis-frame622.json`, `analysis-provenance.json`, `validation.json` and
  `findings.md`. Actual Unity compilation and aggregation selftests passed. The
  isolated analysis Editor was stopped and its absence verified before the next
  live session.

The initial MF2-only supervisor missed the indoor-range interval; its receipt
remains preserved. The scheduler now defaults to all 45 original non-menu
destinations and retains explicit scene selection, with 19 passing pure tests.
The new automatic selection has not been used for another live capture.

The player log contains an original `OptionsPanel_ButtonSet.Awake`
`NullReferenceException` during menu startup. Gameplay and this capture later
completed. This report does not classify the entire session as error-free.

## Subsequent live-session issue

After the completed range capture, the user requested a much busier scene and
reported seeing only Home. The player remained alive, but its log contained
`Native OpenXR: eye views invalid (-1017)` escaping the stereo coroutine. This
is subsequent runtime evidence, not a failure of the earlier closed capture.
At the user's request, the old process was stopped and its exit verified, then
the same signed build was relaunched with fresh hardware/floor/2-controller
preflight and an idle profiler. The new session is
`build/optih3vr/live-busy-20261023T175756Z/`; its requested workload must not be
confused with this earlier indoor-range capture. A subsequent
[busy Winter Wasteland capture](BUSY_PROFILE_2026-10-23.md) completed in two
valid segments after the user confirmed active Sosigs.

Source investigation found that an auxiliary eye-calibration query latched
`lastStatus=-1017` when view validity is unavailable. Its managed caller keeps
the previous calibrated eye transforms, but the stereo coroutine subsequently
throws on the latched status outside its cleanup protection. This explains a
software failure-propagation path; it does not establish why headset views
became invalid. The subsequent [recovery update](INPUT_RECOVERY_2026-10-23.md)
preserves the query's negative return and cached calibration while leaving
transient invalid-view queries non-latching. Genuine session/GPU errors remain
fatal, and startup still rejects invalid eye calibration. The source regression
and synthetic player checks pass, and the tested files are installed. Physical
tracking-loss recovery has not yet been tested on the headset.
