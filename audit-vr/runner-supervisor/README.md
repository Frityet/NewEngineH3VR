# Native runner startup budget

`script/run_native.py` now accepts `--startup-timeout` (default 600 seconds; allowed 1–3600). The existing runtime allowance remains 210 seconds for gameplay, or `--seconds + 90` for menu observation. The supervisor's absolute maximum is startup allowance plus runtime allowance; readiness is latched once, so repeated markers cannot extend it.

Startup readiness requires all four existing signals in the fresh log:

1. Exact current `NATIVEH3VR_RUN_ID` with process ID.
2. Metal `NATIVEH3VR_RUNTIME` initialization.
3. Native synthetic stereo initialization.
4. First native Metal eye-submission marker.

The diagnostics markers are emitted before scene loading and are insufficient by themselves. First eye submission indicates that the rendering path has reached the compositor; it is not a promise that all shader compilation is complete, because that marker precedes command encoding/completion. The runtime allowance covers subsequent work. Missing correlation or missing stereo submission exhausts the startup allowance and fails. Runtime hangs exhaust the runtime allowance and fail. Genuine log errors stop the run immediately, including errors after a success marker. Early process exit returns immediately to the existing strict success checks.

Existing architecture/package integrity, exact run correlation, original route/ballistic evidence, log error, and fresh PNG gates are retained. Cleanup snapshots pre-existing exact executable instances before launch. After supervision it matches the exact executable path and this launch's exact `-logFile` argv value; same-named executables, different logs, and pre-existing instances are excluded. It uses no broad `pkill` pattern. PID/start identities are rechecked before each signal. It sends TERM, grants each observed instance a full grace interval, then sends SIGKILL only to that tracked identity if still alive. Cleanup runs even when `open -W` already exited and requires no diagnostic PID. The launcher is independently reaped. Argument inspection parses only argv, never logs process environment data.

The receipt records configured startup/runtime/absolute supervision budgets, observed startup/runtime/total supervision durations, stop reason, cleanup time, matched/TERM/KILL/remaining PIDs and cleanup errors. Failed exit verification or cleanup errors fail acceptance. If LaunchServices exited while its native player remained alive, forced cleanup also fails acceptance. Cleanup has an explicit 14-second maximum with default grace/wait settings; remaining late instances or inspection failures are reported as failure rather than treated as successful exit.

The original gameplay harness still has its independent 180-second limit, started in `Start()` before waiting for the menu. This runner-only change does not reset or bypass that limit. A shader stall after the harness starts could therefore still cause a genuine reported harness timeout; that requires separate evidence before changing the harness.

Reproduce supervisor tests:

```
python3 audit-vr/runner-supervisor/test_supervisor.py -v
```

Twelve tests cover a 240-second simulated cold start (beyond the old total limit), diagnostics before delayed first rendering, missing/incorrect correlation, missing stereo, a real gameplay hang with repeated markers, normal completion, early exit, immediate startup/late errors, deadline-boundary readiness, 12 preserved error classifications, and actual delayed subprocess/log I/O. Tests use no Unity project, game assets, graphics, VR runtime or hardware. This is supervisor validation, not an actual full-player launch.

Cleanup regression tests:

```
python3 audit-vr/runner-supervisor/test_cleanup.py -v
```

Five additional tests use a compiled temporary native C process that ignores TERM. They prove escalation/reaping while a pre-existing identical executable, a new identical executable using another log, and a different executable remain alive; cleanup after LaunchServices early exit without diagnostics; a no-player no-op; PID/start-identity changes preventing signals; and inventory exceptions producing failed cleanup while still reaping the launcher. No actual Unity or VR process is used.
