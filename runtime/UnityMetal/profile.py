#!/usr/bin/env python3
"""Summarize one comparable interval from Unity and MacVR frame traces."""
import argparse
import csv
import json
import math
from pathlib import Path
import statistics


def read(path):
    with path.open(newline="") as stream:
        return list(csv.DictReader(line for line in stream if not line.startswith("#")))


def percentile(values, fraction):
    point = (len(values) - 1) * fraction
    lower = math.floor(point)
    upper = math.ceil(point)
    return values[lower] + (values[upper] - values[lower]) * (point - lower)


def summary(values, budget):
    values = sorted(v for v in values if v > 0 and math.isfinite(v))
    if not values:
        return None
    return {
        "samples": len(values),
        "p50_ms": round(percentile(values, .50), 4),
        "p95_ms": round(percentile(values, .95), 4),
        "p99_ms": round(percentile(values, .99), 4),
        "over_budget": sum(v > budget for v in values),
    }


def numbers(rows, key, scale=1):
    return [float(row[key]) * scale for row in rows if row.get(key)]


def intervals(rows, key):
    ticks = [int(row[key]) for row in rows]
    return [(end - start) / 1e6 for start, end in zip(ticks, ticks[1:])]


def select_window(unity, runtime, warmup, seconds, frames):
    if frames is not None:
        first, last = frames
        unity = [row for row in unity if first <= int(row["unity_frame"]) <= last]
    timed = [int(row["xr_display_ns"]) for row in unity if int(row["xr_display_ns"]) > 0]
    if not timed:
        raise ValueError("No Unity rows have submitted OpenXR display times")
    start = timed[0] + int(warmup * 1e9)
    end = timed[-1]
    if seconds is not None:
        desired = start + int(seconds * 1e9)
        if desired > end:
            raise ValueError("There are fewer samples than requested for this window")
        end = desired
    unity = [row for row in unity if start <= int(row["xr_display_ns"]) <= end]
    runtime = [row for row in runtime if start <= int(row["display_ns"]) <= end]
    if not unity or not runtime:
        raise ValueError("Unity and MacVR are missing overlapping samples for this interval")
    return unity, runtime, start, end


def analyze(unity_path, runtime_path, warmup, seconds, frames):
    unity, runtime, start, end = select_window(read(unity_path), read(runtime_path), warmup, seconds, frames)
    budget = statistics.median(numbers(unity, "display_period_ns", 1e-6))
    submitted = [row for row in runtime if row["kind"] == "submit"]
    complete = [row for row in runtime if row["kind"] == "complete" and row["status"] == "4"]
    waits = [row for row in runtime if row["kind"] == "wait"]
    # Completion can be repeated across Unity loops while the render thread is
    # busy. Count each completed OpenXR sequence once for the plug-in timings.
    unique = {row["completed_sequence"]: row for row in unity if int(row["completed_sequence"]) > 0}
    memory = numbers(unity, "system_used_bytes")

    metrics = {
        "unity_loop": numbers(unity, "loop_ms"),
        "unity_main_thread": numbers(unity, "main_thread_ns", 1e-6),
        "unity_render_thread": numbers(unity, "render_thread_ns", 1e-6),
        "unity_eye_render_cpu": numbers(unity, "eye_render_cpu_ms"),
        "unity_openxr_wait": numbers(unity, "xr_wait_ms"),
        "unity_metal_command_buffer": numbers(unique.values(), "unity_metal_gpu_ns", 1e-6),
        "openxr_end_frame_cpu": numbers(unique.values(), "end_frame_ns", 1e-6),
        "macvr_submit_cpu": numbers(submitted, "submit_cpu_ns", 1e-6),
        "macvr_metal_gpu": numbers(complete, "gpu_ns", 1e-6),
        "macvr_gpu_completion_interval": intervals(complete, "event_ns"),
    }
    return {
        "window_display_start_ns": start,
        "window_display_end_ns": end,
        "headset_budget_ms": budget,
        "timings": {name: summary(values, budget) for name, values in metrics.items()},
        "unity_loop_count": len(unity),
        "openxr_stereo_completed": len(unique),
        "macvr_submitted": len(submitted),
        "macvr_gpu_completed": len(complete),
        "macvr_missed_wait_slots": sum(int(row["slot_misses"]) for row in waits),
        "native_display_gpu_completions": sum(int(row["native"]) for row in complete),
        "repeated_physical_presentations": None,
        "unity_system_used_bytes": {
            "first": int(memory[0]), "last": int(memory[-1]), "delta": int(memory[-1] - memory[0]),
            "peak": int(max(memory)),
        } if memory else None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", type=Path, required=True)
    parser.add_argument("--openxr", type=Path, required=True)
    parser.add_argument("--warmup-seconds", type=float, default=2)
    parser.add_argument("--window-seconds", type=float)
    parser.add_argument("--unity-frames", type=int, nargs=2, metavar=("START", "END"))
    args = parser.parse_args()
    if args.warmup_seconds < 0 or (args.window_seconds is not None and args.window_seconds <= 0):
        parser.error("Expected nonnegative warmup and a positive sample window")
    try:
        result = analyze(args.unity, args.openxr, args.warmup_seconds, args.window_seconds, args.unity_frames)
    except (OSError, KeyError, ValueError, statistics.StatisticsError) as error:
        parser.exit(1, str(error) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
