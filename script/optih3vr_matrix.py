#!/usr/bin/env python3
"""Sequential OptiH3VR acceptance matrix. Planning is the default; --execute launches.

Never infer frame rates from startup/load or total process duration. Runtime window
telemetry is optional for acceptance, mandatory for performance comparisons. See
docs/history/OPTIH3VR_MATRIX.md for the process-bound JSON marker contract.
"""

from pathlib import Path
import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import fcntl
import hashlib
import json
import math
import os
import re
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
WEAPONS = [
    ("M1911", "handgun"),
    ("AKM", "closed-bolt single shot"),
    ("WebleyRevolver", "double-action revolver"),
    ("MosinM1891", "bolt-action"),
    ("MB500Chopped", "tube-fed shotgun"),
]
SAFE_NAME = re.compile(r"[A-Za-z0-9_().&-]+")
LIMITS = [
    "Synthetic stationary tracking and diagnostic controllers; no physical headset acceptance.",
    "Scene observations cover loading and rendering, not game-mode completion or all interactions.",
    "Firearm fixtures prove one prepared shot, grab/hold/release, not reloading/cycling/attachments.",
    "Unsupported open-bolt and single-action mechanisms are not claimed as tested.",
    "GPU/CPU/frame timings are only the telemetry source declared by each runtime window.",
    "External editor, build, GPU, thermal and background contention can affect measurements.",
    "Captured images require visual review; passing automation does not establish visual parity.",
]


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def atomic_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n")
    temporary.replace(path)


def load_scenes(path):
    baseline = json.loads(path.read_text())
    if not baseline.get("passed") or baseline.get("errors"):
        raise ValueError("A successful original scene baseline is required")
    scenes = [Path(row["path"]).stem for row in baseline["scenes"]]
    if len(scenes) != 46 or len(set(scenes)) != len(scenes):
        raise ValueError("Expected exactly 46 unique original scenes")
    if any(not SAFE_NAME.fullmatch(scene) for scene in scenes):
        raise ValueError("Scene catalog contains unsafe names")
    return scenes


def build_cases(scenes, groups):
    cases = []
    if "scene" in groups:
        cases.extend(
            dict(
                id="scene-" + name,
                scene=name,
                group="scene",
                arguments=["--scene-smoke", "--scene", name],
            )
            for name in scenes
        )
    if "weapon" in groups:
        cases.extend(
            dict(
                id="fire-" + item,
                scene="IndoorRange_Updated",
                item=item,
                mechanism=mechanism,
                group="weapon",
                arguments=[
                    "--gameplay",
                    "--fire",
                    "--scene",
                    "IndoorRange_Updated",
                    "--item",
                    item,
                ],
            )
            for item, mechanism in WEAPONS
        )
    if "systems" in groups:
        cases.append(
            dict(
                id="player-systems",
                scene="IndoorRange_Updated",
                group="systems",
                arguments=["--player-systems"],
            )
        )
    if "vault" in groups:
        cases.append(
            dict(
                id="vault-M1911",
                scene="IndoorRange_Updated",
                item="M1911",
                group="vault",
                arguments=["--gameplay", "--fire", "--vault", "--item", "M1911"],
            )
        )
    return cases


def schedule(cases, repeats, variants=("baseline", "optimized")):
    # Reverse order every repeat to reduce systematic cache/thermal ordering bias.
    return [
        dict(case=case, repeat=repeat + 1, variant=variant, enabled=variant == "optimized")
        for repeat in range(repeats)
        for case in cases
        for variant in (variants if repeat % 2 == 0 else tuple(reversed(variants)))
    ]


def run_options(case, perf_enabled, warmup, duration):
    """Timing scenes omit all image readback; interaction routes retain captures."""
    timing = perf_enabled and case["group"] == "scene"
    return dict(
        timingRequested=timing,
        arguments=["--no-captures"] if timing else [],
        capturePolicy="disabled-for-performance" if timing else "required",
        environment={
            "NATIVEH3VR_PERF_ENABLED": "1" if timing else "0",
            "NATIVEH3VR_PERF_WARMUP_SECONDS": str(warmup) if timing else None,
            "NATIVEH3VR_PERF_CAPTURE_SECONDS": str(duration) if timing else None,
        },
    )


def json_markers(text, prefix):
    rows, errors = [], []
    for index, line in enumerate(text.splitlines()):
        if line.startswith(prefix + " "):
            try:
                value = json.loads(line[len(prefix) + 1 :])
                if not isinstance(value, dict):
                    raise ValueError("marker is not a JSON object")
                rows.append((index, value))
            except (ValueError, TypeError) as error:
                errors.append(f"{prefix} line {index + 1}: {error}")
    return rows, errors


def telemetry(text, result, case, enabled, min_warmup=2.0, min_duration=2.0):
    """Fail closed on identity, load-contaminated or incomplete timing windows."""
    errors = []
    identities = re.findall(r"^NATIVEH3VR_RUN_ID (\S+) pid=(\d+)$", text, re.M)
    expected_id = result.get("runId")
    if len(identities) != 1 or identities[0][0] != expected_id:
        return dict(
            variantVerified=False, windows=[], errors=["Ambiguous/missing process run identity"]
        )
    expected_pid = int(identities[0][1])
    configs, parse_errors = json_markers(text, "NATIVEH3VR_OPTI_CONFIG")
    errors.extend(parse_errors)
    configs = [row for _, row in configs]
    verified = (
        len(configs) == 1
        and configs[0].get("runId") == expected_id
        and type(configs[0].get("pid")) is int
        and configs[0]["pid"] == expected_pid
        and configs[0].get("available") is True
        and type(configs[0].get("enabled")) is bool
        and configs[0]["enabled"] == enabled
    )
    if not verified:
        errors.append("Missing or mismatched process-bound OptiH3VR configuration")
    windows, parse_errors = json_markers(text, "NATIVEH3VR_PERF_WINDOW")
    errors.extend(parse_errors)
    valid = []
    handoffs = [
        i
        for i, line in enumerate(text.splitlines())
        if line.startswith("NATIVE_GAMEPLAY_STAGE native-camera-handoff ")
    ]
    for index, row in windows:
        try:
            if (
                row.get("runId") != expected_id
                or type(row.get("pid")) is not int
                or row.get("pid") != expected_pid
            ):
                raise ValueError("window process/run identity mismatch")
            if row.get("scene") != case["scene"]:
                raise ValueError("window scene does not match requested scene")
            if not handoffs or index <= handoffs[0]:
                raise ValueError("window precedes completed scene camera handoff")
            if row.get("phase", "steady") != "steady":
                raise ValueError("window is not explicitly steady state")
            for field, minimum in [
                ("warmupSeconds", min_warmup),
                ("durationSeconds", min_duration),
            ]:
                value = row.get(field)
                if type(value) not in (int, float) or not math.isfinite(value) or value < minimum:
                    raise ValueError(field + " is absent/too short/nonfinite")
            samples = row.get("frameMs")
            if not isinstance(samples, list) or len(samples) < 30:
                raise ValueError("window has fewer than 30 actual frame samples")
            if any(type(v) not in (int, float) or not math.isfinite(v) or v <= 0 for v in samples):
                raise ValueError("window frame times are invalid")
            if type(row.get("frames")) is not int or row["frames"] != len(samples):
                raise ValueError("window frame count differs from sample count")
            if abs(sum(samples) / 1000 - row["durationSeconds"]) > max(
                0.1, row["durationSeconds"] * 0.05
            ):
                raise ValueError("window duration does not match frame interval sum")
            row.setdefault("source", "Unity frame delta")
            for field in ("settingsFingerprint", "workload", "source"):
                if not isinstance(row.get(field), str) or not row[field]:
                    raise ValueError("window lacks " + field)
            if type(row.get("stereoFrames")) is not int or row["stereoFrames"] < 2:
                raise ValueError("window lacks at least two completed native stereo frames")
            sorted_samples = sorted(samples)
            valid.append(
                dict(
                    row,
                    meanMs=statistics.mean(samples),
                    medianMs=statistics.median(samples),
                    p95Ms=sorted_samples[math.ceil(len(samples) * 0.95) - 1],
                    fps=1000 / statistics.mean(samples),
                    nativeStereoFps=row["stereoFrames"] / row["durationSeconds"],
                )
            )
        except ValueError as error:
            errors.append("Invalid frame window: " + str(error))
    if not windows:
        errors.append("No runtime steady-state frame window; no performance claim available")
    return dict(variantVerified=verified, windows=valid, errors=errors)


def assess(result, log, returncode, case, enabled, *, timing_requested=False):
    failures = list(result.get("failures", []))
    if returncode != 0:
        failures.append("Runner returned " + str(returncode))
    if result.get("verificationPassed") is not True:
        failures.append("Runner verification did not pass")
    if result.get("cleanup", {}).get("verifiedExited") is not True:
        failures.append("Runner did not verify player exit")
    if result.get("timedOut") is not False:
        failures.append("Runner timed out or omitted timeout status")
    if not log.exists() or result.get("logSha256") != digest(log):
        failures.append("Raw player log missing or hash mismatch")
    if result.get("requestedScene") != case["scene"]:
        failures.append("Runner result does not identify the requested scene")
    if case.get("item") and result.get("requestedItem") != case["item"]:
        failures.append("Runner result does not identify the requested firearm")
    player_log = log.read_text(errors="replace") if log.exists() else ""
    evidence = telemetry(player_log, result, case, enabled)
    if timing_requested:
        if (
            result.get("capturePolicy") != "disabled-for-performance"
            or result.get("captures") != []
        ):
            evidence["errors"].append("Performance run did not prove image captures were disabled")
        if list(log.parent.glob("*.png")):
            evidence["errors"].append(
                "Performance evidence directory contains unexpected PNG readbacks"
            )
        if any(
            marker in player_log
            for marker in (
                "NATIVE_OPTIH3VR_TEST_",
                "NATIVE_GRAPHICS_TEXTURE_",
                "NEWENGINEH3VR_PROFILE_",
                "NEWENGINEH3VR_SCOPE_PROBE_",
            )
        ):
            evidence["errors"].append(
                "Performance run contains a separate profiler or diagnostic workload"
            )
    return dict(
        acceptancePassed=not failures, failures=list(dict.fromkeys(failures)), telemetry=evidence
    )


def compare(records, min_repeats=3):
    records = [row for row in records if row.get("timingRequested")]
    comparisons = []
    for case_id in dict.fromkeys(row["caseId"] for row in records):
        pair = {}
        reasons = []
        for variant in ("baseline", "optimized"):
            rows = [r for r in records if r["caseId"] == case_id and r["variant"] == variant]
            good = []
            for row in rows:
                windows = row["telemetry"]["windows"]
                if (
                    not row["acceptancePassed"]
                    or not row["telemetry"]["variantVerified"]
                    or row.get("capturePolicy") != "disabled-for-performance"
                    or row["telemetry"]["errors"]
                    or len(windows) != 1
                ):
                    reasons.append(
                        variant
                        + ": each repeat needs passing acceptance, configuration and one timing window"
                    )
                else:
                    good.append(windows[0])
            if len(good) < min_repeats:
                reasons.append(variant + f": fewer than {min_repeats} valid independent repeats")
            pair[variant] = good
        windows = pair["baseline"] + pair["optimized"]
        for key in ("settingsFingerprint", "workload", "source", "scene"):
            if len({w[key] for w in windows}) > 1:
                reasons.append("Different " + key + " across compared runs")
        record = dict(caseId=case_id, comparable=not reasons, reasons=list(dict.fromkeys(reasons)))
        if not reasons:
            base = statistics.median(w["meanMs"] for w in pair["baseline"])
            opti = statistics.median(w["meanMs"] for w in pair["optimized"])
            base_stereo = statistics.median(w["nativeStereoFps"] for w in pair["baseline"])
            opti_stereo = statistics.median(w["nativeStereoFps"] for w in pair["optimized"])
            record.update(
                baselineMedianRunMeanMs=base,
                optimizedMedianRunMeanMs=opti,
                baselineMedianNativeStereoFps=base_stereo,
                optimizedMedianNativeStereoFps=opti_stereo,
                nativeStereoFpsChangePercent=(opti_stereo / base_stereo - 1) * 100,
                frameTimeChangePercent=(opti / base - 1) * 100,
                interpretation="Stationary fixture measurement, subject to recorded external contention",
            )
        comparisons.append(record)
    return comparisons


@contextmanager
def exclusive_lock(path):
    with path.open("a") as stream:
        try:
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError("Another OptiH3VR matrix holds the workspace test lock")
        try:
            yield
        finally:
            fcntl.flock(stream, fcntl.LOCK_UN)


def write_report(output, summary):
    atomic_json(output / "summary.json", summary)
    lines = [
        "# OptiH3VR matrix results",
        "",
        "Timing runs disable screenshot/compositor PNG readbacks. Separate captured acceptance runs provide visual evidence.",
        "",
        f"Planned runs: {summary['plannedRuns']}; recorded: {len(summary['runs'])}.",
        f"Acceptance passes: {sum(r['acceptancePassed'] for r in summary['runs'])}.",
        "",
        "| Case | Repeat | Variant | Acceptance | Config verified | Frame windows |",
        "|---|---:|---|---|---|---:|",
    ]
    for row in summary["runs"]:
        lines.append(
            f"| {row['caseId']} | {row['repeat']} | {row['variant']} | {row['acceptancePassed']} | "
            f"{row['telemetry']['variantVerified']} | {len(row['telemetry']['windows'])} |"
        )
    lines.extend(["", "## Failures and unavailable measurements", ""])
    for row in summary["runs"]:
        errors = row["failures"] + row["telemetry"]["errors"]
        if errors:
            lines.append(
                f"- {row['caseId']} / {row['repeat']} / {row['variant']}: " + "; ".join(errors)
            )
    lines.extend(
        [
            "",
            "## Frame-time comparisons",
            "",
            "| Case | Comparable | Unity frame time change | Native stereo FPS change | Reason |",
            "|---|---|---:|---:|---|",
        ]
    )
    for row in summary["comparisons"]:
        change = f"{row['frameTimeChangePercent']:+.2f}%" if row["comparable"] else "unavailable"
        stereo_change = (
            f"{row['nativeStereoFpsChangePercent']:+.2f}%" if row["comparable"] else "unavailable"
        )
        lines.append(
            f"| {row['caseId']} | {row['comparable']} | {change} | {stereo_change} | "
            + "; ".join(row["reasons"])
            + " |"
        )
    lines.extend(["", "## Limits", ""] + ["- " + text for text in summary["limits"]])
    (output / "report.md").write_text("\n".join(lines) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--execute", action="store_true")
    parser.add_argument("--app", type=Path, default=ROOT / "dist/NewEngineH3VR.app")
    parser.add_argument(
        "--baseline", type=Path, default=ROOT / "build/scene-integrity/missing-script-baseline.json"
    )
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build/optih3vr-matrix")
    parser.add_argument(
        "--groups",
        nargs="+",
        choices=["scene", "weapon", "systems", "vault"],
        default=["scene", "weapon", "systems", "vault"],
    )
    parser.add_argument(
        "--scene", action="append", help="Limit scene observations to catalog names (repeatable)"
    )
    parser.add_argument("--repeats", type=int, default=1)
    parser.add_argument("--startup-timeout", type=int, default=600)
    parser.add_argument("--variant-env", default="OPTIH3VR_ENABLED")
    parser.add_argument(
        "--variants",
        nargs="+",
        choices=["baseline", "optimized"],
        default=["baseline", "optimized"],
    )
    parser.add_argument(
        "--no-perf",
        action="store_true",
        help="Disable optional timing capture for broad acceptance sweeps",
    )
    parser.add_argument("--perf-warmup", type=float, default=2.0)
    parser.add_argument("--perf-seconds", type=float, default=3.0)
    parser.add_argument("--require-performance", action="store_true")
    parser.add_argument(
        "--contention-note",
        default="External editor/build/GPU/thermal contention was not controlled.",
    )
    args = parser.parse_args()
    if not 1 <= args.repeats <= 100 or not 1 <= args.startup_timeout <= 3600:
        parser.error("Repeat count must be 1..100 and startup timeout 1..3600")
    if args.variant_env != "OPTIH3VR_ENABLED" and not re.fullmatch(
        r"NATIVEH3VR_[A-Z0-9_]+", args.variant_env
    ):
        parser.error("--variant-env must be OPTIH3VR_ENABLED or an explicit NATIVEH3VR_ variable")
    if len(set(args.variants)) != len(args.variants):
        parser.error("--variants must be unique")
    if args.no_perf and args.require_performance:
        parser.error("--no-perf conflicts with --require-performance")
    if not all(math.isfinite(v) and 0 < v <= 120 for v in (args.perf_warmup, args.perf_seconds)):
        parser.error("Performance warmup and capture must be finite positive seconds <=120")
    output, app = args.output_dir.resolve(), args.app.resolve()
    if not output.is_relative_to(ROOT / "build") or output == ROOT / "build":
        parser.error("Output must be a dedicated subdirectory inside build/")
    if not app.is_relative_to(ROOT / "dist"):
        parser.error("App must be generated inside dist/")
    scenes = load_scenes(args.baseline)
    if args.scene:
        unknown = set(args.scene) - set(scenes)
        if unknown:
            parser.error("Unknown original scenes: " + ", ".join(sorted(unknown)))
        scenes = [scene for scene in scenes if scene in args.scene]
    cases = build_cases(scenes, args.groups)
    jobs = schedule(cases, args.repeats, tuple(args.variants))
    output.mkdir(parents=True, exist_ok=True)
    if (output / "summary.json").exists():
        parser.error(
            "Output contains previous results; choose a fresh directory to preserve evidence"
        )
    plan = dict(
        schemaVersion=1,
        generatedAt=datetime.now(timezone.utc).isoformat(),
        app=str(app),
        baselineSha256=digest(args.baseline),
        runnerSha256=digest(ROOT / "script/run_native.py"),
        variantEnv=args.variant_env,
        variants=args.variants,
        perfEnabled=not args.no_perf,
        perfWarmupSeconds=args.perf_warmup,
        perfCaptureSeconds=args.perf_seconds,
        timingCapturePolicy="disabled-for-performance",
        acceptanceCapturePolicy="required",
        contentionNote=args.contention_note,
        cases=cases,
        jobs=jobs,
        limits=LIMITS,
        plannedRuns=len(jobs),
        execute=args.execute,
    )
    atomic_json(output / "plan.json", plan)
    print(
        f"Matrix: {len(cases)} cases, {len(jobs)} sequential runs; plan: {output / 'plan.json'}",
        flush=True,
    )
    if not args.execute:
        return 0
    import run_native

    exe = run_native.player_binary(app)
    summary = dict(
        schemaVersion=1,
        plannedRuns=len(jobs),
        runs=[],
        comparisons=[],
        complete=False,
        limits=LIMITS + [args.contention_note],
        planSha256=digest(output / "plan.json"),
    )
    with exclusive_lock(ROOT / "build/.optih3vr-matrix.lock"):
        if run_native.player_instances(exe):
            raise SystemExit(
                "A player already runs; stop it before starting sequential measurements"
            )
        for job in jobs:
            case = job["case"]
            run_dir = output / case["id"] / f"repeat-{job['repeat']:02d}" / job["variant"]
            run_dir.mkdir(parents=True, exist_ok=False)
            options = run_options(case, not args.no_perf, args.perf_warmup, args.perf_seconds)
            command = (
                [
                    sys.executable,
                    str(ROOT / "script/run_native.py"),
                    "--app",
                    str(app),
                    "--startup-timeout",
                    str(args.startup_timeout),
                    "--output-dir",
                    str(run_dir),
                ]
                + case["arguments"]
                + options["arguments"]
            )
            env = dict(os.environ)
            env[args.variant_env] = "1" if job["enabled"] else "0"
            for key, value in options["environment"].items():
                if value is None:
                    env.pop(key, None)
                else:
                    env[key] = value
            before = run_native.player_instances(exe)
            if before:
                summary["interruptedReason"] = "Player found between sequential runs"
                break
            log_name = "native-player-systems" if case["group"] == "systems" else "native-gameplay"
            log = run_dir / (log_name + ".log")
            outer_cleanup = None
            with (
                (run_dir / "runner.stdout.log").open("w") as stdout,
                (run_dir / "runner.stderr.log").open("w") as stderr,
            ):
                process = subprocess.Popen(command, env=env, stdout=stdout, stderr=stderr)
                try:
                    code = process.wait(timeout=args.startup_timeout + 210 + 90)
                except (subprocess.TimeoutExpired, KeyboardInterrupt):
                    outer_cleanup = run_native.stop_player(process, exe, log, before)
                    code = process.returncode
                    if isinstance(sys.exc_info()[1], KeyboardInterrupt):
                        summary["interruptedReason"] = "User interrupted matrix"
            result_file = run_dir / (log_name + "-run.json")
            try:
                result = json.loads(result_file.read_text())
            except (OSError, ValueError):
                result = dict(failures=["Runner produced no readable result JSON"])
            row = dict(
                caseId=case["id"],
                repeat=job["repeat"],
                variant=job["variant"],
                outputDir=str(run_dir),
                command=command,
                runnerReturncode=code,
                resultPath=str(result_file),
                outerCleanup=outer_cleanup,
                timingRequested=options["timingRequested"],
                capturePolicy=result.get("capturePolicy"),
                **assess(
                    result,
                    log,
                    code,
                    case,
                    job["enabled"],
                    timing_requested=options["timingRequested"],
                ),
            )
            if outer_cleanup is not None:
                row["acceptancePassed"] = False
                row["failures"].append("Outer supervisor interrupted the runner")
            summary["runs"].append(row)
            summary["comparisons"] = compare(summary["runs"])
            write_report(output, summary)
            print(
                f"{case['id']} repeat={job['repeat']} {job['variant']} acceptance={row['acceptancePassed']}",
                flush=True,
            )
            if outer_cleanup is not None or not result.get("cleanup", {}).get("verifiedExited"):
                summary["interruptedReason"] = summary.get(
                    "interruptedReason",
                    "Cannot safely continue after timeout/unverified player exit",
                )
                break
        summary["complete"] = len(summary["runs"]) == len(jobs) and not summary.get(
            "interruptedReason"
        )
        summary["acceptancePassed"] = summary["complete"] and all(
            row["acceptancePassed"] for row in summary["runs"]
        )
        summary["variantsVerified"] = summary["complete"] and all(
            row["telemetry"]["variantVerified"] for row in summary["runs"]
        )
        summary["performanceAvailable"] = bool(summary["comparisons"]) and all(
            row["comparable"] for row in summary["comparisons"]
        )
        write_report(output, summary)
    return (
        0
        if summary["acceptancePassed"]
        and summary["variantsVerified"]
        and (not args.require_performance or summary["performanceAvailable"])
        else 1
    )


if __name__ == "__main__":
    raise SystemExit(main())
