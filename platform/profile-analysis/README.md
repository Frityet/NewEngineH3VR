# Offline Unity 2021 Profiler analysis

`NewEngineProfileAnalysis.cs` is an Editor-only reader for a completed binary
Profiler capture. It changes no game code and performs no player/Editor launch.
The source compiles against the local Unity 2021 Editor assembly APIs.

After capture has finished and the `.raw` file is closed, copy the source into an
`Assets/Editor/` folder of the intended analysis project. Use Unity MCP to select
the correct running Unity 2021 instance and discover its available tool schemas.
Wait for compilation, stop play/Profiler recording, then execute C# on that
instance:

```csharp
NewEngineH3VR.EditorTools.NewEngineProfileAnalysis.Analyze(
    "/absolute/path/busy-sandbox.raw",
    "/absolute/path/fresh-busy-sandbox-analysis.json");
```

The output must be a fresh path; existing files are preserved. Input size and
SHA-256 bind the analysis to the raw file. Input length and write time must remain
unchanged throughout analysis. Keep the separate capture log/manifest to bind it
to the player process, app, runtime settings and actual gameplay. Loading a
profile replaces this Editor's loaded Profiler history, so the method refuses to
run during Editor play or active Profiler recording. It does not change capture
settings or enable deep profiling.

To select the actual busy scene interval and change the analysis bounds:

```csharp
var options = new NewEngineH3VR.EditorTools.NewEngineProfileAnalysis.Options {
    firstFrame = 100,
    lastFrame = 900,
    maxFrames = 801,
    maxThreads = 256,
    topMarkers = 80,
    frameHistoryCapacity = 20000,
    maxAnalysisSeconds = 45
};
NewEngineH3VR.EditorTools.NewEngineProfileAnalysis.Analyze(
    "/absolute/path/busy-sandbox.raw",
    "/absolute/path/fresh-selected-analysis.json", options);
```

Frame indices are Unity's zero-based API indices; the Profiler UI may display
index + 1. No loading/warmup interval is silently removed. Defaults select at
most 600 uniformly spaced recorded frames, 256 threads, five million samples,
250,000 samples per thread/frame, a 2 GiB input and a 45-second analysis budget.
Unity's default Profiler history retains only the final 300 loaded frames. The
reader therefore requests a bounded 20,000-frame native history before loading,
using the locally verified Unity 2021 `SetMaxFrameHistoryLength` entry point. It
does not write shared `ProfilerUserSettings`/EditorPrefs. The previous Editor
history setting is restored in `finally`; inspect `historyCapacityRestored`.
Changing the native capacity replaces this analysis Editor's history only.
Keep this operation in a dedicated analysis Editor.

The native `LoadProfile` call is synchronous and cannot be interrupted by the
managed analysis budget. Reaching a bound produces a partial diagnostic report
with the reason recorded. Do not present its subset maximum or averages as exact
whole-capture statistics. Increase limits deliberately only when useful.

For a separately launched idle analysis Editor's `-executeMethod`, the static
entry point is
`NewEngineH3VR.EditorTools.NewEngineProfileAnalysis.AnalyzeFromEnvironment`.
Provide absolute `NEWENGINEH3VR_PROFILE_INPUT` and
`NEWENGINEH3VR_PROFILE_ANALYSIS` environment paths. This utility never launches
that Editor itself. Prefer the already selected Unity MCP instance when present.

The JSON contains source provenance, loaded/eligible/selected/analyzed frame
counts, individual analyzed frame durations and completeness flags, per-thread
identity and sample counts, global and per-thread marker rankings by inclusive
and exclusive time,
wait-name hints, and validity/completeness flags. `complete` means the selected
frames were analyzed without detected errors/caps; `fullLoadedHistoryAnalyzed`
also requires all eligible loaded frames and the entire loaded API range.
`fullCaptureAnalyzed` remains false because the API's loaded history does not
independently verify the raw file's total frame count. Report the exact
`loadedFirstFrame`/`loadedLastFrame`, eligible, selected and analyzed counts.
A nonzero loaded first frame identifies a missing prefix; reaching the requested
history capacity can also indicate truncation. A zero first frame below the
capacity is useful coverage evidence, not an independent raw-file frame census.
`completeFrames`, `partialFrames`, `framesWithoutData` and `capped` identify
incomplete coverage explicitly. A partial frame's marker totals contain only its
successfully read thread views; retain those rows for diagnostics, not an
unqualified workload comparison. Markers aggregate within each thread by name,
category and profiler flags, so identically named scopes with different flags
remain separate.

Root/unnamed spans remain in the original rankings and `rootOrUnnamedMarkers`;
they represent unattributed elapsed spans, not identified work. Additional named
rankings omit those spans, and separate name-hinted waits from other exclusive
markers. Existing per-thread rankings remain available for Main Thread and
Render Thread comparisons. Name hints alone do not identify scheduler states.

`topAllocationParents` aggregates `GC.Alloc` samples by their immediate recorded
parent and ancestor path. Byte totals accept only nonnegative metadata explicitly
declared `Int64` with unit `Bytes` (verified on the actual Unity 2021 capture).
Missing metadata remains unavailable through the known-byte call counts. These
are recorded scopes, not inferred managed call stacks or allocation object
types. Totals include all analyzed groups; the displayed top groups are bounded
by `topMarkers`.

All reported durations use `RawFrameDataView.GetSampleTimeMs`,
`GetSampleStartTimeMs` and `FrameDataView.frameTimeMs`. The local Unity 2021
`Managed/UnityEngine/UnityEditor.CoreModule.xml` explicitly defines these values
in milliseconds. Exclusive marker time subtracts immediate recorded children
using the raw preorder hierarchy. Parent/child time containment and child counts
and non-overlapping direct-sibling ordering are validated; malformed slices are reported rather than silently converted to
plausible exclusive times.

Interpret marker totals carefully:

- Inclusive scopes overlap children and recursive calls; never add all inclusive
  times as frame time.
- Exclusive scope duration still includes waits, scheduling delays and
  uninstrumented work. It is not measured CPU execution time.
- Threads run concurrently; summing their times is neither capture wall time nor
  CPU utilization.
- Wait/idle/present hints are based solely on names. Inspect frame timelines
  before deciding whether a large marker represents useful work or waiting.
- Without deep profiling or explicit markers, inner managed methods may be
  absent. The analyzer reports recorded names and never invents managed hotspots.
- CPU profile frame timing does not establish physical headset scanout, complete
  game parity or visual acceptance.

`RunAggregationSelfTest()` performs pure deterministic checks for nested and
repeated scopes, exclusive-time conservation, truncated/outside-parent hierarchy
rejection, overlapping-sibling rejection and range-preserving bounded frame
selection and recorded allocation-parent/byte attribution. Earlier Mono-only
compile/check evidence is retained in
`build/profile-analysis-tools/latest-checks.json`. The current source also
compiled without errors in the isolated actual Unity 2021 Editor, where
`RunAggregationSelfTest()` passed; its exact source hash and cleanup evidence
are in the run’s `validation.json` and `analysis-provenance.json`.
The completed 2026-10-23 IndoorRange capture was read through the isolated Unity
2021 Editor and MCP. The initial default-history result and corrected bounded
uniform-600 result are retained under
`build/profile-analysis-tools/live-mf2-analysis-20261023T174052Z/`; the directory
name does not imply the captured scene was MF2. Use that run's provenance and
findings for exact input/source hashes, coverage and limitations.

## Scene-aware live capture scheduler

For an already running, explicitly armed live session, the scheduler defaults
to `--scene auto`. It never launches the game or supplies input/scene controls:

```sh
python3 script/profile_live_session.py --session /absolute/path/session.json
```

Auto waits for fresh loaded gameplay-scene telemetry and 15 seconds of stable
scene name/handle with advancing Unity frames. Its allowlist covers all 45
non-menu destinations in the original full-build scene inventory, including
ranges, training, arenas, sandboxes and playable hub/starting areas. `MainMenu3`
and boot/loading scenes never trigger. A loaded unknown/custom scene produces
an `auto-rejected-loaded-scene-<name>` event with an explicit-selection hint;
it does not silently wait without explaining the rejected destination.

Use `--scene NAME` to select a specific original or custom gameplay map:

```sh
python3 script/profile_live_session.py --session /absolute/path/session.json \
  --scene IndoorRange_Updated --captures 1 --seconds 30
```

The default is at most three 30-second captures, a 45-second gap, and a bounded
15-minute scheduler lifetime. Durations remain limited to 1–60 seconds. Auto
restarts stability when the observed scene changes and may select another
gameplay destination between captures. Explicit mode keeps its selected scene
and retains its stop-on-transition policy after the first capture.

Immediately before publishing a request, the scheduler rechecks process
identity and fresh scene stability. Each request, unique label and receipt binds
the actual observed scene name/handle. The controller does not lock the scene:
start acknowledgment must match that scene, and completed summaries must match
the requested scene/handle across every recorded segment. A transition during
capture rejects that evidence instead of relabeling it as the new scene. PID,
game/profiler run IDs, process-start time, freshness, restoration and raw-file
checks remain required. The scheduler never sends a stop request; an accepted
bounded capture can finish if the scheduler exits.

Stable scene telemetry establishes scene entry, not that the player is busy or
that Sosigs are present. Inspect the actual captured interval before making a
workload or performance claim. Profiling overhead remains present throughout.

The scheduler's pure tests use temporary fake status/capture files and mocked
process/time/request functions; they never consult or signal a running game:

```sh
python3 -m unittest discover -s script -p 'test_profile_live_session.py'
```
