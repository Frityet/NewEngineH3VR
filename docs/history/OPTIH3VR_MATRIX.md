# OptiH3VR acceptance and frame measurement matrix

`script/optih3vr_matrix.py` runs the existing strict native runner sequentially and
preserves a unique raw-log, capture and JSON result directory for every run. It
plans by default; add `--execute` to launch. It does not build, patch, install or
publish the app. The matrix lock prevents overlapping matrix processes. Stop
other native players, Editor play sessions, builds and other GPU work before
controlled measurements. The human operator's contention note is recorded in the
plan and report; the harness cannot establish absence of all external contention.

The original successful scene baseline supplies all 46 scene names, including the
menu, ranges, game modes, legacy/testing scenes and mod templates. Every requested
scene is attempted. A load failure is recorded as failure, not a skipped pass.
The scene route exercises original loading, persistent camera handoff, five
seconds of observation and verified exit, with fresh captures during visual
acceptance runs; it does not complete
all gameplay in that scene. Original warnings remain governed by the existing
runner's exact scene baselines. Known historical original issues in
ReturnOfTheRotwieners, Grillhouse_2Story and WinterWasteland must remain visible.

Scene performance runs add the runner's `--no-captures` option, disabling both
desktop screenshots and periodic compositor PNG readbacks. Their result must
explicitly state `capturePolicy: "disabled-for-performance"`, contain no capture
records and leave no PNGs in the run directory. Separate `--no-perf` scene and
firearm acceptance runs retain `capturePolicy: "required"` and fresh images for
visual review. Periodic compositor readbacks introduce stalls and invalidate a
controlled performance comparison. Historical measurements made with those
readbacks enabled must remain labelled contaminated and cannot establish a
performance improvement.

The remaining seven cases exercise the original M1911, AKM, WebleyRevolver,
MosinM1891 and MB500Chopped one-shot fixtures; original wrist-menu and quickbelt
interactions; and the isolated original M1911 Vault round trip. The five firearm
IDs have historical diagnostic evidence, but this does not pre-accept the new
combined app. The fixtures cover handgun, closed-bolt single-shot,
double-action revolver, bolt-action and tube-fed shotgun mechanisms. Open-bolt,
single-action revolvers, reloading, cycling, attachments, melee, explosives,
locomotion variants, enemies, scoring and mode progression need additional
fixtures or live testing. A complete 53-case matrix is broad coverage rather
than complete game parity.

Commands below use the current default app. Supply `--app` for another generated
app under `dist/`. The runner's packaged-app audit must match that app. Each
execution requires a fresh output directory so earlier evidence is retained.

```sh
# Review the entire default 106-run baseline/optimized plan without launching.
python3 script/optih3vr_matrix.py --output-dir build/optih3vr-plan

# Broad optimized-only scene coverage, without extra frame timing windows.
python3 script/optih3vr_matrix.py --execute --groups scene --variants optimized \
  --no-perf --output-dir build/optih3vr-all-scenes

# Original firearm, player-system and Vault regression routes, both variants.
python3 script/optih3vr_matrix.py --execute --groups weapon systems vault \
  --no-perf --output-dir build/optih3vr-interactions

# Four independent runs of each variant per selected scene, alternating order.
python3 script/optih3vr_matrix.py --execute --groups scene \
  --scene IndoorRange_Updated --scene Institution --scene TakeAndHold_Lobby_2 \
  --repeats 4 --perf-warmup 5 --perf-seconds 15 --require-performance \
  --contention-note 'Editor play and builds stopped; stationary synthetic fixture.' \
  --output-dir build/optih3vr-benchmark

# Pure parser/supervision-contract tests; never launches a player.
python3 -m unittest discover -s script -p test_optih3vr_matrix.py -v
```

`plan.json` records the original scene baseline and runner hashes, exact case
schedule, settings and contention note. `summary.json` and `report.md` update
after every completed run. Every run has the original strict runner result plus
stdout/stderr. A runner timeout or unverified player exit stops the matrix so a
subsequent run cannot overlap. The outer timeout uses the existing exact
executable/log-path/PID-start cleanup mechanism and never stops pre-existing
players. Ordinary scene/route failures with verified exit do not abort coverage.

`OPTIH3VR_ENABLED=0/1` selects baseline/optimized using the same application and
content; `--variant-env` exists for an explicitly changed native contract. A run
needs exactly one process-bound `NATIVEH3VR_OPTI_CONFIG` JSON marker with matching
`runId`, integer `pid`, `available:true` and boolean `enabled` matching its
requested variant. A missing runtime does not count as a baseline integration
pass. Existing runner acceptance, variant verification and performance
availability are separate fields; no absent measurement is silently successful.

Only scene observation cases request timing windows. Firearm, player-system and
Vault cases always retain captured interaction acceptance and disable timing.
`--require-performance` evaluates requested scene comparisons; acceptance-only
cases do not count as missing benchmark windows.

With timing enabled, the harness requests
`NATIVEH3VR_PERF_WARMUP_SECONDS` and `NATIVEH3VR_PERF_CAPTURE_SECONDS` and sets
`NATIVEH3VR_PERF_ENABLED=1`. `--no-perf` removes timing-duration overrides and sets
that flag to zero. The runtime writes one `NATIVEH3VR_PERF_WINDOW` JSON marker
after its completed scene camera handoff. Required fields are `runId`, integer
`pid`, requested `scene`, `warmupSeconds`, `durationSeconds`, integer `frames`,
`frameMs` containing individual measured intervals, integer `stereoFrames` counting
completed native stereo submissions, nonempty
`settingsFingerprint`, `workload`, and optional descriptive `source`. An optional
`phase` must equal `steady`. The native probe currently declares Stopwatch
intervals between Unity coroutine frames, including runtime/render waits rather
than CPU execution time or physical display scanout.

The parser requires at least two seconds each of post-handoff warmup and actual
measurement, at least 30 finite positive intervals, at least two completed native
stereo frames, and matching interval count
and duration sum. Windows with wrong identities, scene, phase or configuration
are rejected. Raw player log hashes must match the runner's result. Performance
comparison requires three or more independently passing runs per variant, one
valid window per run, no telemetry errors, and identical settings fingerprint,
workload, source and scene. It reports median run mean frame time and its
percentage change, alongside median native stereo FPS and its change. Per-window
mean, median, p95, Unity-loop FPS and native stereo FPS are in raw JSON. Unity
coroutine FPS may rise while stereo rendering stays unchanged because the main
loop can poll a pending GPU submission more often; it must not be presented as
headset FPS. Total
startup/process time and loading frame counts never become performance metrics.
Synthetic stationary scene-entry windows do not measure combat performance;
random scene simulation and thermal/cache effects can still vary. Review the raw
samples and per-run distributions before attributing a small difference to the
optimization. Captures still require visual review, and physical headset
acceptance requires the actual headset.
