# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy", "pillow"]
# ///
"""Pool matched Take & Hold stereo capture windows into summary.json and results.csv."""

import argparse
import csv
import json
import math
from pathlib import Path
from statistics import median

from measure import EYES, health, pixels

MAPS = {
    "Classic": ("Classic Hallways", "TakeAndHoldClassic"),
    "NorthestDakota": ("Northest Dakota", "TakeAndHold_WinterWasteland"),
    "Institution": ("Institution", "Institution"),
}
MODES = ("stock", "balanced", "quality")
PHASES = ("take", "hold")
REPEATS = ("r01", "r02", "r03")
POSE_POSITION_METERS = 0.01
POSE_ROTATION_DEGREES = 0.25
METRICS = ("stereoCpu", "unityMetalCommandBuffer", "compositorCompletionInterval")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_csv(path):
    with path.open(newline="") as source:
        rows = list(csv.DictReader(line for line in source if not line.startswith("#")))
    require(rows and all(None not in row and None not in row.values() for row in rows),
            f"Empty or malformed trace: {path}")
    return rows


def values(rows, key, scale=1):
    result = [float(row[key]) * scale for row in rows if row.get(key)]
    return [number for number in result if math.isfinite(number) and number > 0]


def distribution(samples):
    if not samples:
        return None
    samples = sorted(samples)
    point = (len(samples) - 1) * 0.95
    low, high = math.floor(point), math.ceil(point)
    p95 = samples[low] + (samples[high] - samples[low]) * (point - low)
    return {"samples": len(samples), "p50Ms": round(median(samples), 4), "p95Ms": round(p95, 4)}


def window_trace(path, stem, unity_trace, runtime):
    first, last = map(int, (path / f"{stem}-frames.txt").read_text().split())
    timings = json.loads((path / f"{stem}-timings.json").read_text())
    unity = [row for row in unity_trace
             if first <= int(row["unity_frame"]) <= last and int(row["xr_display_ns"]) > 0]
    require(unity, "No OpenXR display times in Unity frame window")
    start, end = int(unity[0]["xr_display_ns"]), int(unity[-1]["xr_display_ns"])
    require(start < end and (start, end) == (timings["window_display_start_ns"],
                                            timings["window_display_end_ns"]),
            "Timing receipt and Unity frame window differ")
    unity = [row for row in unity if start <= int(row["xr_display_ns"]) <= end]
    runtime = [row for row in runtime if start <= int(row["display_ns"]) <= end]
    complete = [row for row in runtime if row["kind"] == "complete" and row["status"] == "4"]
    require(len(complete) >= 2, "No usable compositor completion interval")

    # A completed Unity command buffer can be reported by several update loops.
    # Keep the final observation, as in UnityMetalCli TraceProfile.
    unique = {int(row["completed_sequence"]): row for row in unity
              if int(row["completed_sequence"]) > 0}
    intervals = [(int(b["event_ns"]) - int(a["event_ns"])) / 1e6
                 for a, b in zip(complete, complete[1:])]
    require(all(interval > 0 for interval in intervals), "Non-monotonic compositor completions")

    samples = {
        "stereoCpu": values(unity, "eye_render_cpu_ms"),
        "unityMetalCommandBuffer": values(unique.values(), "unity_metal_gpu_ns", 1e-6),
        "compositorCompletionInterval": intervals,
    }
    summaries = {key: distribution(series) for key, series in samples.items()}
    for key, receipt_key in zip(METRICS, ("unity_eye_render_cpu", "unity_metal_command_buffer",
                                          "macvr_gpu_completion_interval")):
        measured, recorded = summaries[key], timings["timings"][receipt_key]
        require(measured and recorded and measured["samples"] >= 80
                and measured["samples"] == recorded["samples"], f"Invalid {key} sample count")
        require(all(abs(measured[f"p{rank}Ms"] - recorded[f"p{rank}_ms"]) <= 0.00011 for rank in (50, 95)),
                f"{key} percentiles differ from timing receipt")
    missed_slots = [int(row["slot_misses"]) for row in runtime if row["kind"] == "wait"]
    missed = sum(missed_slots)
    # run.py takes counter endpoints from the entire trace. Several Unity
    # frames can share either boundary timestamp and advance the counter.
    skip_counts = [int(row["stereo_skipped"]) for row in unity_trace
                   if start <= int(row["xr_display_ns"]) <= end]
    require(len(skip_counts) >= 2, "Missing Unity skip counter readings")
    skipped = skip_counts[-1] - skip_counts[0]
    require(skipped >= 0 and skipped == timings["unity_stereo_skipped"]
            and missed == timings["macvr_missed_wait_slots"]
            and len(unique) == timings["openxr_stereo_completed"]
            and len(complete) == timings["macvr_gpu_completed"], "Trace counts differ from receipt")

    # Bounds are inclusive in both traces. Add one display period to count the
    # final scheduled slot, then divide the completed pairs by that interval.
    periods = values(unity, "display_period_ns")
    require(periods, "Missing headset frame period")
    period = median(periods)
    seconds = (end - start + period) / 1e9
    memory = [int(row["system_used_bytes"]) for row in unity if row.get("system_used_bytes")]

    main_thread = max(values(unity, "main_thread_ns", 1e-6), default=None)

    return samples, {
        "firstUnityFrame": first, "lastUnityFrame": last, "displayDurationSeconds": seconds,
        "syntheticHeadsetHz": round(1e9 / period, 4),
        "deliveredHz": round(len(complete) / seconds, 4),
        "completedPairs": len(complete), "unitySkipped": skipped, "missedWaitSlots": missed,
        "maxCompositorCompletionGapMs": round(max(intervals), 4),
        "maxMissedWaitSlotsPerEvent": max(missed_slots, default=0),
        "maxUnityMainThreadMs": round(main_thread, 4) if main_thread is not None else None,
        "peakUnitySystemUsedBytes": max(memory, default=None),
        "metrics": summaries,
    }


def verify_profile(path, stem, mode):
    asset_path = path / f"{stem}-asset.txt"
    if mode == "stock":
        require(not asset_path.exists(), "The Built-in witness unexpectedly reported a URP asset")
        return {"pipeline": "Built-in"}
    asset, *settings = asset_path.read_text().splitlines()
    actual = dict(line.split("=", 1) for line in settings)
    require("NativeH3VR" in asset, f"Unexpected pipeline name: {asset}")
    factor = 2 if mode == "balanced" else 4
    expected = {"renderScale": 1, "msaaSampleCount": factor, "shadowDistance": 15 * factor,
                "mainLightShadowmapResolution": 512 * factor, "maxAdditionalLightsCount": 2 * factor}
    require(all(math.isclose(float(actual[key]), number, abs_tol=0.001) for key, number in expected.items())
            and actual["supportsHDR"].lower() == "true", f"Unexpected {mode} pipeline settings")
    return {"pipeline": asset, **expected, "supportsHDR": True}


def pose_delta(reference, candidate):
    position = math.dist(reference[:3], candidate[:3])
    a, b = reference[3:], candidate[3:]
    require(len(a) == len(b) == 4 and abs(math.hypot(*a) - 1) <= 0.01
            and abs(math.hypot(*b) - 1) <= 0.01, "Invalid headset quaternion")
    cosine = abs(sum(x * y for x, y in zip(a, b))) / (math.hypot(*a) * math.hypot(*b))
    return position, math.degrees(2 * math.acos(min(1.0, cosine)))


def require_pose(reference, candidate, context):
    meters, degrees = pose_delta(reference, candidate)
    require(meters <= POSE_POSITION_METERS and degrees <= POSE_ROTATION_DEGREES,
            f"{context}: headset pose changed by {meters:.4f} m / {degrees:.3f} degrees")
    return meters, degrees


def verify_state(path, stem, map_id, phase, pose):
    map_name, scene = MAPS[map_id]
    state = json.loads((path / f"{stem}-state.json").read_text())
    expected = {
        "mapId": map_id, "mapName": map_name, "scene": scene, "phase": phase.title(),
        "holdStage": "Hacking" if phase == "hold" else "",
        "character": "h3vr:Generic_0_BeginnerBlake", "gameMode": "Classic",
        "healthMode": "CustomHealth", "healthMultiplier": "NighInvulnerable",
        "equipmentSeed": 42, "simulationSeed": 14796, "holdLayer": 0,
    }
    for edge in ("begin", "end"):
        sample = state[edge]
        for key, value in expected.items():
            require(sample[key] == value, f"{edge} gameplay {key}={sample[key]!r}, expected {value!r}")
        require(sample["stockHoldIndex"] >= 0 and sample["sequenceSeed"] >= 0 and sample["activeNpcCount"] > 0,
                "Missing live NPCs, sequence seed or stock hold")
        require(0 <= sample["objectiveNpcCount"] <= sample["activeNpcCount"]
                and (phase != "hold" or sample["objectiveNpcCount"] > 0), "Missing active objective NPCs")
        sample_pose = [sample["headPosition"][axis] for axis in "xyz"]
        sample_pose.extend(sample["headRotation"][axis] for axis in "xyzw")
        require_pose(pose, sample_pose, edge)
    begin, end = state["begin"], state["end"]
    require(end["phaseElapsedSeconds"] > begin["phaseElapsedSeconds"] >= 0
            and end["roundElapsedSeconds"] > begin["roundElapsedSeconds"] >= 0
            and begin["stockHoldIndex"] == end["stockHoldIndex"]
            and begin["sequenceSeed"] == end["sequenceSeed"], "Gameplay stalled or changed objective or seed")
    return state


def verify_eyes(path, scene, label):
    result = {}
    for eye in EYES:
        metrics = health(pixels(path, scene, label, eye))
        require(metrics["blackFraction"] <= 0.95, f"{eye} capture is nearly all black")
        require(metrics["errorPinkFraction"] <= 0.002, f"{eye} capture contains shader error pink")
        result[eye] = metrics
    require((result["left"]["width"], result["left"]["height"]) ==
            (result["right"]["width"], result["right"]["height"]), "Stereo eye dimensions differ")
    return result


def numeric_range(numbers):
    return [min(numbers), max(numbers)]


def pool(windows):
    complete = sum(window["completedPairs"] for window in windows)
    seconds = sum(window["displayDurationSeconds"] for window in windows)
    states = [window["gameplay"][edge] for window in windows for edge in ("begin", "end")]
    memory = [window["peakUnitySystemUsedBytes"] for window in windows
              if window["peakUnitySystemUsedBytes"] is not None]
    main_thread = [window["maxUnityMainThreadMs"] for window in windows
                   if window["maxUnityMainThreadMs"] is not None]
    eye_checks = [window["eyes"][eye] for window in windows for eye in EYES]
    return {
        "runs": len(windows), "completedPairs": complete, "displayDurationSeconds": round(seconds, 4),
        "deliveredHz": round(complete / seconds, 4),
        "deliveredHzRange": numeric_range([window["deliveredHz"] for window in windows]),
        "unitySkipped": sum(window["unitySkipped"] for window in windows),
        "missedWaitSlots": sum(window["missedWaitSlots"] for window in windows),
        "maxCompositorCompletionGapMs": max(window["maxCompositorCompletionGapMs"] for window in windows),
        "maxMissedWaitSlotsPerEvent": max(window["maxMissedWaitSlotsPerEvent"] for window in windows),
        "maxUnityMainThreadMs": max(main_thread, default=None),
        "peakUnitySystemUsedBytes": max(memory, default=None),
        "peakUnitySystemUsedBytesRange": numeric_range(memory) if memory else None,
        "activeNpcCountRange": numeric_range([state["activeNpcCount"] for state in states]),
        "objectiveNpcCountRange": numeric_range([state["objectiveNpcCount"] for state in states]),
        "phaseElapsedSecondsRange": numeric_range([state["phaseElapsedSeconds"] for state in states]),
        "maxBlackFraction": max(eye["blackFraction"] for eye in eye_checks),
        "maxErrorPinkFraction": max(eye["errorPinkFraction"] for eye in eye_checks),
        "maxStrongMagentaFraction": max(eye["strongMagentaFraction"] for eye in eye_checks),
        **{key: distribution([number for window in windows for number in window["_samples"][key]])
           for key in METRICS},
    }


def capture_sources(group):
    by_phase = {phase: group / phase for phase in PHASES}
    if not any(path.exists() or path.is_symlink() for path in by_phase.values()):
        return {phase: group for phase in PHASES}
    require(all(path.is_dir() for path in by_phase.values()),
            "Phase-specific layout requires both take and hold session directories")
    return by_phase


def inspect(root):
    report = {
        "schemaVersion": 1, "captures": str(root.resolve()),
        "method": {
            "deliveredHz": "Synthetic compositor completed pairs divided by inclusive XR display-slot duration",
            "timings": "Percentiles pooled from positive raw samples in the checked intervals, never across run boundaries",
            "stereoCpu": "CPU time for both eye renders and submit scheduling",
            "compositorCompletionInterval": "Time between synthetic MacVR compositor completion callbacks",
            "unityMetalCommandBuffer": "GPU duration of Unity's final OpenXR-event Metal command buffer. Earlier buffers may contribute to the frame",
            "tailHitches": {
                "maxCompositorCompletionGapMs": "Longest interval between compositor callbacks within a capture window",
                "maxMissedWaitSlotsPerEvent": "Largest synthetic slot loss reported by one runtime wait event",
                "maxUnityMainThreadMs": "Largest positive Unity main-thread profiler reading, when available",
            },
            "memory": "Unity System Used Memory profiler counter, when available",
            "npcCounts": "Live endpoint observations while scenes continue simulating with matched seeds",
            "poseToleranceMeters": POSE_POSITION_METERS, "poseToleranceDegrees": POSE_ROTATION_DEGREES,
        },
        "results": [], "failures": [],
    }
    for map_id, (map_name, scene) in MAPS.items():
        windows = {(phase, mode): [] for phase in PHASES for mode in MODES}
        for repeat in REPEATS:
            references = {}
            for mode in MODES:
                group = root / repeat / f"{map_id}-{mode}"
                try:
                    sources = capture_sources(group)
                except ValueError as error:
                    report["failures"].append(f"{group.relative_to(root)}: {error}")
                    continue

                traces = {}
                for phase, path in sources.items():
                    if path not in traces:
                        try:
                            require((path / "profile-complete.txt").is_file(), "Missing successful gameplay witness receipt")
                            traces[path] = read_csv(path / "unity.csv"), read_csv(path / "openxr.csv")
                        except (OSError, ValueError, csv.Error) as error:
                            report["failures"].append(f"{path.relative_to(root)}: {error}")
                            traces[path] = None
                    if traces[path] is None:
                        continue
                    unity, runtime = traces[path]
                    label = f"{phase}-{mode}"
                    stem = f"{scene}-{label}"
                    try:
                        samples, timing = window_trace(path, stem, unity, runtime)
                        pose = list(map(float, (path / f"{stem}-pose.txt").read_text().split()))
                        require(len(pose) == 7 and all(map(math.isfinite, pose)), "Invalid headset pose")
                        gameplay = verify_state(path, stem, map_id, phase, pose)
                        eyes = verify_eyes(path, scene, label)
                        profile = verify_profile(path, stem, mode)
                        shape = (eyes["left"]["width"], eyes["left"]["height"])
                        objective = (gameplay["begin"]["stockHoldIndex"], gameplay["begin"]["sequenceSeed"])
                        reference_pose, reference_shape, reference_objective = references.setdefault(
                            phase, (pose, shape, objective))
                        meters, degrees = require_pose(reference_pose, pose, "Matched capture")
                        require(shape == reference_shape and objective == reference_objective,
                                "Eye resolution, selected stock objective or sequence seed differs")
                        windows[phase, mode].append({
                            "repeat": repeat, "directory": str(path.relative_to(root)), **timing,
                            "pose": pose, "poseDeltaMeters": round(meters, 6), "poseDeltaDegrees": round(degrees, 6),
                            "eyes": eyes, "gameplay": gameplay, "profile": profile, "_samples": samples,
                        })
                    except (OSError, ValueError, KeyError, TypeError) as error:
                        report["failures"].append(f"{path.relative_to(root)} {phase}: {error}")

        for (phase, mode), matching in windows.items():
            if matching:
                summary = pool(matching)
                for window in matching:
                    del window["_samples"]
                report["results"].append({"mapId": map_id, "mapName": map_name, "scene": scene,
                                          "phase": phase, "mode": mode, **summary, "windows": matching})
    return report


def write_csv(path, report):
    metadata = ("mapId", "mapName", "scene", "phase", "mode", "runs", "completedPairs",
                "displayDurationSeconds", "deliveredHz", "unitySkipped", "missedWaitSlots",
                "maxCompositorCompletionGapMs", "maxMissedWaitSlotsPerEvent", "maxUnityMainThreadMs",
                "peakUnitySystemUsedBytes", "maxBlackFraction", "maxErrorPinkFraction",
                "maxStrongMagentaFraction")
    ranges = ("deliveredHzRange", "activeNpcCountRange", "objectiveNpcCountRange", "phaseElapsedSecondsRange")
    metrics = ("samples", "p50Ms", "p95Ms")
    with path.open("w", newline="") as output:
        writer = csv.writer(output)
        writer.writerow([*metadata, *(field + bound for field in ranges for bound in ("Min", "Max")),
                         *(key + field[0].upper() + field[1:] for key in METRICS for field in metrics)])
        for result in report["results"]:
            writer.writerow([*(result[key] for key in metadata),
                             *(number for key in ranges for number in result[key]),
                             *(result[key][field] for key in METRICS for field in metrics)])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", type=Path,
                        help="Root containing r01, r02 and r03, optionally with take/hold sessions per group")
    parser.add_argument("report", type=Path, help="Destination for summary.json and results.csv")
    args = parser.parse_args()
    report = inspect(args.captures)
    args.report.mkdir(parents=True, exist_ok=True)
    (args.report / "summary.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n")
    write_csv(args.report / "results.csv", report)
    if report["failures"]:
        parser.exit(1, "\n".join(report["failures"]) + "\n")
    print(f"{len(report['results'])} matched map/phase/mode comparisons in {args.report}")


if __name__ == "__main__":
    main()
