# Busy Winter Wasteland profile, 23 October 2026

The user confirmed that `TakeAndHold_WinterWasteland` was populated with active
Sosigs. NewEngineH3VR recorded **30.084 seconds in two separate intervals**,
with **4,172 scalar observations**. Both captures kept the same scene and
restored the prior profiler settings without errors.

The first requested 30-second trace reached its 512 MiB size limit after
20.080 seconds. After explaining that limit, a second 10.004-second trace was
recorded. There is a 65.302-second gap between the segments; they are not a
continuous 30-second sample or a controlled before/after comparison. The
gameplay and number of surviving Sosigs could change between them.

| Capture observation | Segment 1 | Segment 2 |
| --- | ---: | ---: |
| Duration | 20.080 s | 10.004 s |
| Scalar samples | 2,542 | 1,630 |
| Raw profile size | 537,467,102 bytes | 235,283,910 bytes |
| Stop reason | Binary size threshold | Requested duration |
| Main-loop mean / p95 | 7.893 / 10.428 ms | 6.127 / 9.649 ms |
| Physics simulation mean, when observed | 0.354 ms | 0.292 ms |
| Mean allocated bytes per observed frame | 22,664 | 21,647 |
| Observed stereo completions | 2,388 | 1,191 |

These are observations under binary profiling. Frame cadence and stereo
completion counters do not establish physical headset FPS. Missing fixed-update
timings are not converted to zero. Rendering, input, graphics settings and the
seven OptiH3VR gameplay CPU patches were identical across these captures and the
earlier [indoor-range session](LIVE_PROFILE_2026-10-23.md).

## What the raw traces show

An isolated Unity 2021.3.45f2 Editor analyzed 600 uniformly sampled frames per
segment through Unity MCP. Both analyses completed every selected frame with no
caps, malformed slices or errors. The loaded API ranges were 0–2544 and
0–1633; 3,810,114 and 2,515,833 samples were read. Both raw files were rehashed
unchanged, history settings were restored, and the analysis Editor was stopped.
Loaded API ranges are not an independent raw-file frame census.

| Recorded marker, ms per selected frame | Segment 1 | Segment 2 |
| --- | ---: | ---: |
| Main thread: stereo coroutine, exclusive / inclusive | 1.561 / 3.129 | 0.701 / 1.871 |
| Main thread: SteamVR input Update, exclusive | 0.578 | 0.696 |
| Main thread: profiler audio counters, exclusive | 1.012 | 0.943 |
| Main thread: profiler memory counters, exclusive | 0.675 | 0.667 |
| Main thread: Sosig.Update, exclusive | 0.080 | 0.052 |
| Main thread: Sosig.FixedUpdate, exclusive | 0.057 | 0.029 |
| Main thread: NavMeshManager, inclusive | 0.150 | 0.141 |
| Render thread: semaphore wait | 6.660 | 5.287 |
| Render thread: custom camera rendering, inclusive | 1.658 | 1.182 |

All averages use the 600 selected frames as denominator. Inclusive parents
overlap children; exclusive elapsed time can include native waits, scheduling
and uninstrumented work. Concurrent threads must not be summed as CPU work.
No GPU timing or unprofiled speedup is inferred.

The traces contain 10,183 and 5,990 `Sosig.Update` calls, plus 9,639 and 4,360
`Sosig.FixedUpdate` calls. This confirms recorded Sosig activity, but is not an
entity census or attribution of every unmarked AI method. These callbacks are
smaller than the leading recorded renderer and profiler intervals in this
workload.

Allocation metadata totals 13,713,135 and 13,032,091 bytes in the respective
600-frame samples. `SteamVR_Behaviour.Update` is the immediate recorded parent
of 8,346,466 bytes in each sample, approximately 61% and 64% of those totals.
The identical sampled amount reflects repeated callback allocation structure;
whole-capture totals are different. Camera pre-cull callbacks account for
another 1,408,086 and 1,060,380 bytes. These are recorded parent scopes, not
allocation object types or inferred inner managed stacks.

This led to the [validated input lookup caches](INPUT_RECOVERY_2026-10-23.md):
launch/session mode, registered action-name/set bindings and constant ABI sizes
are cached behind `NEWENGINE_UNITY_2021`. Shared gameplay CPU algorithms remain
in the portable OptiH3VR mod. Input edges, focus, pose freshness, polling and
retained snapshot ownership were preserved. The caches are now installed;
validation and installation are recorded separately. These busy-scene traces
predate the change and do not measure its effect.

## Evidence

- Session: `build/optih3vr/live-busy-20261023T175756Z/`.
- `busy-capture-summary.json`: exact durations, gap, user readiness, scalar
  metrics, raw hashes and both supervisor receipts.
- `profile-busy.json`, `profile-busy-segment2.json`: PID/run/scene-bound capture
  validation. The first explicitly has `completedAsRequested=false` because it
  ended at the size limit; its valid partial data is retained.
- Raw files are in
  `profiles/live-20261023T175944Z-88459-7762e83d/capture-01-4c9b2f92/` and
  `capture-02-097f6329/`.
- Raw SHA-256 values are
  `d2c67ffb7e495d6c8a978bb729794fd3bb84c75052fd73db046ac3f884dae8a1`
  and `9990a49a265e014e3a74569a6aa165cf38d3b927df4c01a21702d1432f5bcb30`.
- Analysis: `build/profile-analysis-tools/live-busy-analysis-20261023T181934Z/`,
  including both `analysis-segment*-uniform600.json` reports,
  `analysis-provenance.json`, `comparison.json`, `validation.json` and
  `findings.md`.
