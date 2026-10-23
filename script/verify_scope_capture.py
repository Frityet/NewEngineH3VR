#!/usr/bin/env python3
"""Verify saved scope rendering sanity only; never launch or alter a player.

Use audit-assets/.venv/bin/python (Pillow). Default output is stdout; --output
requires a fresh filename inside the case directory. No existing evidence is
overwritten. RGB presence is not optical equivalence or headset acceptance.
"""

from __future__ import annotations
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import tempfile
from PIL import Image

MAX_PIXELS = 16 * 1024 * 1024
GUIDS = dict(C7A2="995587646fa94f443aab67ca2e5c0a8f", EC79A2="4691ee3e16bb8fa4ab64ab4b8f43a260")
IMAGE_NAMES = dict(
    scopeImage="scope-rt.png", finalEye="final-eye.png", lensRegion="final-lens-region.png"
)


def copy_dimensions(width, height):
    require(
        type(width) is int and type(height) is int and width > 0 and height > 0,
        "Invalid observed render-target dimensions",
    )
    scale = min(1, 2048 / max(width, height))
    return max(1, round(width * scale)), max(1, round(height * scale))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def timestamp(value):
    require(isinstance(value, str), "Missing UTC timestamp")
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(result.tzinfo is not None, "Timestamp has no timezone")
    return result.astimezone(timezone.utc).timestamp()


def contained(root, path):
    path = Path(path)
    if not path.is_absolute():
        path = root / path
    path = path.resolve()
    require(
        path.is_relative_to(root) and path.is_file(),
        "Missing evidence or path escapes case: " + str(path),
    )
    return path


def file_binding(path, maximum=128 * 1024 * 1024):
    stat = path.stat()
    require(0 < stat.st_size <= maximum, "Empty/oversized evidence: " + str(path))
    raw = path.read_bytes()
    require(
        len(raw) == stat.st_size and path.stat().st_mtime_ns == stat.st_mtime_ns,
        "Evidence changed while reading: " + str(path),
    )
    return raw, dict(
        path=str(path),
        bytes=len(raw),
        sha256=hashlib.sha256(raw).hexdigest(),
        modifiedUtc=datetime.fromtimestamp(stat.st_mtime, timezone.utc).isoformat(),
    )


def read_json(path):
    raw, binding = file_binding(path, 16 * 1024 * 1024)
    value = json.loads(raw)
    require(isinstance(value, dict), "Expected a JSON object: " + str(path))
    return value, binding


def decode_image(path, stats=False):
    raw, binding = file_binding(path)
    require(raw.startswith(b"\x89PNG\r\n\x1a\n"), "Evidence is not a PNG: " + str(path))
    # verify() checks the encoded PNG; load() independently decodes its pixels.
    with Image.open(path) as image:
        require(
            image.format == "PNG" and 0 < image.width * image.height <= MAX_PIXELS,
            "Unsupported/oversized PNG",
        )
        if stats:
            require(image.mode == "RGB", "Scope fixture PNG must be opaque RGB24")
        image.verify()
    with Image.open(path) as image:
        image.load()
        binding.update(width=image.width, height=image.height, pixels=image.width * image.height)
        if stats:
            counts = dict(nonBlackPixels=0, redPixels=0, greenPixels=0, bluePixels=0)
            minimum, maximum, total = 255, 0, 0
            for r, g, b in image.convert("RGB").getdata():
                peak = max(r, g, b)
                minimum = min(minimum, peak)
                maximum = max(maximum, peak)
                total += peak
                counts["nonBlackPixels"] += peak >= 11  # max(channel)/255 > .04
                counts["redPixels"] += r > 70 and 2 * r > 3 * g and 2 * r > 3 * b
                counts["greenPixels"] += g > 70 and 2 * g > 3 * r and 2 * g > 3 * b
                counts["bluePixels"] += b > 70 and 2 * b > 3 * r and 2 * b > 3 * g
            binding.update(
                counts,
                minimum=minimum / 255,
                maximum=maximum / 255,
                mean=total / (255 * binding["pixels"]),
                contrast=(maximum - minimum) / 255,
            )
    require(
        hashlib.sha256(path.read_bytes()).hexdigest() == binding["sha256"],
        "PNG changed during decode",
    )
    return binding


def mounted_evidence(report, required):
    value = report.get("mountedFixture")
    if value is None:
        require(not required, "Mounted C7A2 fixture report is missing")
        return dict(requested=False, verified=False)
    require(
        isinstance(value, dict) and type(value.get("requested")) is bool,
        "Invalid mounted fixture declaration",
    )
    require(not required or value["requested"], "Mounted C7A2 fixture was not requested")
    if not value["requested"]:
        return dict(requested=False, verified=False)
    require(
        report.get("mode") == "api-mounted-synthetic-original-prefabs",
        "Mounted fixture mode differs",
    )
    for key in (
        "requested",
        "apiAttached",
        "verified",
        "detachedForCleanup",
        "deregisteredForCleanup",
        "ownedGunDestroyed",
    ):
        require(value.get(key) is True, "Mounted fixture failed " + key)
    for key in ("error", "cleanupError"):
        require(key in value and not value[key], "Mounted fixture reports/misses " + key)
    require(
        value.get("mountItem") == "C7A2"
        and value.get("opticItem") == "EC79A2"
        and report.get("item") == "EC79A2",
        "Mounted fixture is not exact C7A2 + EC79A2",
    )
    for key, item in [("mountItemGuid", "C7A2"), ("opticItemGuid", "EC79A2")]:
        require(
            str(value.get(key, "")).lower() == GUIDS[item],
            "Mounted fixture original GUID differs: " + key,
        )
    observations = value.get("observations")
    require(isinstance(observations, list), "Mounted fixture observations are missing")
    attached = [
        row for row in observations if row.get("phase") in ("after-attach", "assembly-aligned")
    ]
    require(bool(attached), "No mounted registration observation")
    for row in attached:
        require(
            row.get("mountName") == "_RailMount_Top" and row.get("mountType") == "Picatinny",
            "Wrong C7A2 mount",
        )
        for key in (
            "curMountMatches",
            "registeredOnMount",
            "registeredOnGun",
            "rootObjectMatchesGun",
            "controllerMatchesOptic",
        ):
            require(row.get(key) is True, "Mounted registration failed " + key)
        for key in ("gunHeld", "attachmentHeld", "gunHandAssigned", "attachmentHandAssigned"):
            require(
                row.get(key) is False,
                "API-mounted diagnostic unexpectedly used hand interaction: " + key,
            )
    return dict(
        requested=True,
        verified=True,
        mountItem="C7A2",
        opticItem="EC79A2",
        apiPreparedMount=True,
        handInteractionValidated=False,
        cleanupVerified=True,
    )


def verify_scope_capture(case_directory, expected_item=None, eye=None, require_mounted=False):
    root = Path(case_directory).resolve()
    result = dict(
        schemaVersion=1,
        kind="newengine-scope-render-sanity",
        passed=False,
        caseDirectory=str(root),
        scope="Rendering sanity only; not optical equivalence, full gameplay parity, physical input or headset acceptance.",
        rgbRule="channel > 70 and channel > 1.5 * each other channel; each RT/lens color >= max(16, ceil(pixels * .001))",
        evidence={},
        failures=[],
    )
    try:
        require(root.is_dir(), "Case directory is missing")
        run_path = contained(root, "native-gameplay-run.json")
        report_path = contained(root, "optics/scope-probe.json")
        run, run_binding = read_json(run_path)
        report, report_binding = read_json(report_path)
        result["evidence"].update(run=run_binding, scopeReport=report_binding)
        require(
            run.get("verificationPassed") is True
            and run.get("sceneObservationPassed") is True
            and run.get("scenePassMarker") is True,
            "Native scene run did not pass",
        )
        require(
            run.get("failures") == []
            and run.get("issues") == []
            and run.get("timedOut") is False
            and type(run.get("launchServicesExitCode")) is int
            and run["launchServicesExitCode"] == 0,
            "Native run has errors/timeout/nonzero exit",
        )
        cleanup = run.get("cleanup", {})
        require(
            cleanup.get("verifiedExited") is True
            and cleanup.get("errors") == []
            and cleanup.get("remainingPids") == []
            and cleanup.get("termPids") == []
            and cleanup.get("killPids") == [],
            "Native run cleanup/clean exit was not verified",
        )
        require(
            run.get("testMode") == "synthetic isolated OpenXR"
            and run.get("architecture") == "arm64"
            and run.get("capturePolicy") == "required",
            "Expected captured ARM64 synthetic run",
        )
        run_id = run.get("runId")
        pid = report.get("pid")
        require(
            isinstance(run_id, str) and bool(run_id) and type(pid) is int and pid > 0,
            "Missing run ID/scope PID",
        )
        log_path = contained(root, run["log"])
        raw_log, log_binding = file_binding(log_path, 64 * 1024 * 1024)
        log = raw_log.decode(errors="replace")
        require(
            hashlib.sha256(log.encode()).hexdigest() == run.get("logSha256"),
            "Run log hash is stale/changed",
        )
        identities = re.findall(r"^NATIVEH3VR_RUN_ID (\S+) pid=(\d+)$", log, re.M)
        require(
            identities == [(run_id, str(pid))],
            "Scope PID does not match the exact run log identity",
        )
        starts = re.findall(r"^NEWENGINEH3VR_SCOPE_PROBE_START mode=(\S+) scope=(.+)$", log, re.M)
        stops = re.findall(r"^NEWENGINEH3VR_SCOPE_PROBE_STOP status=(\S+) report=(.+)$", log, re.M)
        require(
            starts == [(report.get("mode"), report.get("scopeName"))]
            and len(stops) == 1
            and stops[0][0] == "complete"
            and Path(stops[0][1]).resolve() == report_path.parent,
            "Scope report lacks a matching complete lifecycle in this run log",
        )
        require(
            "NEWENGINEH3VR_SCOPE_PROBE_STOP status=failed" not in log
            and "NATIVE_GAMEPLAY_PROBE_FAIL" not in log,
            "Run log reports a scope/gameplay failure",
        )
        scene_passes = re.findall(
            r"^NATIVE_SCENE_PROBE_PASS scene="
            + re.escape(str(run.get("requestedScene")))
            + r" observationSeconds=5 stereoFrames=(\d+)$",
            log,
            re.M,
        )
        require(
            len(scene_passes) == 1 and int(scene_passes[0]) > 2,
            "No matching completed scene observation in this log",
        )
        result["evidence"]["log"] = log_binding
        require(
            report.get("schema") == "newengineh3vr-scope-probe-1"
            and "error" in report
            and not report["error"],
            "Scope report failed/unsupported",
        )
        for key in (
            "synthetic",
            "finalEyeCopied",
            "sourceMainBindingObserved",
            "sourceCallbacksObserved",
            "scopeCameraRendered",
            "cleanupComplete",
        ):
            require(report.get(key) is True, "Scope report did not establish " + key)
        for key in ("ownMeshExperimentRequested", "ownMeshExperimentApplied"):
            require(report.get(key) is False, "Own-mesh hiding experiment present/unknown: " + key)
        require(
            report.get("ownMeshExperimentRestored") is True
            and "ownMeshRestoreError" in report
            and not report["ownMeshRestoreError"]
            and report.get("ownMeshRendererCount") == 0
            and report.get("ownMeshScopePasses") == 0,
            "Own-mesh experiment/cleanup evidence is inconsistent",
        )
        for key in ("sourceRenders", "scopeRenders"):
            require(type(report.get(key)) is int and report[key] > 0, "No observed renders: " + key)
        require(
            report.get("scene") == run.get("requestedScene"), "Scope scene differs from native run"
        )
        item = report.get("item")
        require(
            isinstance(item, str)
            and bool(item)
            and (expected_item is None or item == expected_item),
            "Wrong/missing original scope item",
        )
        require(
            re.fullmatch(r"[0-9a-fA-F]{32}", str(report.get("itemGuid", ""))) is not None,
            "Original scope GUID is missing",
        )
        if item in GUIDS:
            require(
                report["itemGuid"].lower() == GUIDS[item], "Original scope GUID was substituted"
            )
        selected = report.get("selectedEye", "").split(" native eye", 1)[0]
        require(
            selected in ("left", "right") and (eye is None or selected == eye),
            "Wrong/missing selected eye",
        )
        started, finished = (
            timestamp(report.get("startedUtc")),
            timestamp(report.get("finishedUtc")),
        )
        require(
            started <= finished and finished - started <= 180,
            "Invalid/unbounded scope observation timestamps",
        )
        duration = run.get("duration")
        require(
            type(duration) in (int, float) and math.isfinite(duration) and 0 < duration <= 600,
            "Missing/invalid run duration",
        )
        log_stat = log_path.stat()
        lower = log_stat.st_mtime - duration
        if hasattr(log_stat, "st_birthtime"):
            lower = max(lower, log_stat.st_birthtime)
        require(
            lower - 2 <= started <= finished <= log_stat.st_mtime + 2,
            "Scope timestamps are stale/outside this run",
        )
        require(
            abs(report_path.stat().st_mtime - finished) <= 2, "Scope report file timestamp is stale"
        )
        captures = run.get("captures")
        require(
            isinstance(captures, list) and len(captures) >= 2,
            "Native run capture receipts are missing",
        )
        captured = []
        matching_pid = False
        expected_final_dimensions = set()
        for capture in captures:
            path = contained(root, capture["path"])
            actual = decode_image(path)
            for key in ("sha256", "bytes", "width", "height"):
                require(actual[key] == capture.get(key), "Native capture receipt differs: " + key)
            compositor = capture.get("compositor")
            if compositor:
                metadata_path = contained(root, compositor["metadataPath"])
                metadata, binding = read_json(metadata_path)
                require(
                    binding["sha256"] == compositor.get("metadataSha256")
                    and compositor.get("processId") == pid
                    and metadata.get("process_id") == pid,
                    "Compositor metadata/receipt differs from scope process",
                )
                require(
                    compositor.get("nativeHmdSubmittedFrames") == 0
                    and metadata.get("native_hmd_submitted_frames") == 0,
                    "Synthetic scope evidence claims physical headset submission",
                )
                require(
                    metadata.get("width") == actual["width"]
                    and metadata.get("height") == actual["height"]
                    and actual["width"] % 2 == 0
                    and compositor.get("composedFrames") == metadata.get("composed_frames")
                    and type(compositor.get("minimumSceneFrame")) is int
                    and metadata.get("composed_frames", -1) >= compositor["minimumSceneFrame"] > 0,
                    "Compositor dimensions/completed-frame evidence differs",
                )
                expected_final_dimensions.add(
                    copy_dimensions(actual["width"] // 2, actual["height"])
                )
                actual["compositorMetadata"] = binding
                matching_pid = True
            captured.append(actual)
        require(matching_pid, "No process-bound compositor capture receipt")
        result["evidence"]["nativeCaptures"] = captured
        observations = report.get("observations")
        require(
            isinstance(observations, list) and observations,
            "Original scope callback observations are missing",
        )
        scope_dimensions = {
            copy_dimensions(row["targetWidth"], row["targetHeight"])
            for row in observations
            if row.get("targetCreated") is True
            and type(row.get("targetWidth")) is int
            and type(row.get("targetHeight")) is int
            and row["targetWidth"] > 0
            and row["targetHeight"] > 0
        }
        require(
            any(
                row.get("phase") == "source.onPreRender"
                and row.get("instanceMatches") is True
                and row.get("sourceId") == row.get("mainId")
                and row.get("shaderSupported") is True
                and row.get("mainTextureMatchesScopeTarget") is True
                and row.get("targetCreated") is True
                for row in observations
            ),
            "No original source-eye callback binds the rendered scope target",
        )
        images = {}
        for key, filename in IMAGE_NAMES.items():
            declared = report.get(key, {})
            require(
                declared.get("available") is True
                and not declared.get("unavailable")
                and declared.get("path") == filename,
                "Missing/unavailable/renamed scope image: " + key,
            )
            path = contained(root, Path("optics") / filename)
            actual = decode_image(path, stats=True)
            require(started - 2 <= path.stat().st_mtime <= finished + 2, "Stale scope PNG: " + key)
            for field in (
                "width",
                "height",
                "pixels",
                "nonBlackPixels",
                "redPixels",
                "greenPixels",
                "bluePixels",
            ):
                require(
                    type(declared.get(field)) is int and declared[field] == actual[field],
                    "Decoded PNG disagrees with report " + key + "." + field,
                )
            for field in ("minimum", "maximum", "contrast"):
                value = declared.get(field)
                require(
                    type(value) in (int, float)
                    and math.isfinite(value)
                    and abs(value - actual[field]) <= 0.000002,
                    "Decoded PNG extrema disagree with report " + key + "." + field,
                )
            if key in ("scopeImage", "lensRegion"):
                threshold = max(16, math.ceil(actual["pixels"] * 0.001))
                require(
                    all(actual[c] >= threshold for c in ("redPixels", "greenPixels", "bluePixels")),
                    "Insufficient RGB rendering sanity in " + key,
                )
                actual["minimumPixelsPerColor"] = threshold
            images[key] = actual
        require(
            (images["scopeImage"]["width"], images["scopeImage"]["height"]) in scope_dimensions,
            "Scope PNG dimensions do not match an observed original scope target",
        )
        require(
            (images["finalEye"]["width"], images["finalEye"]["height"])
            in expected_final_dimensions,
            "Final eye PNG dimensions do not match the native stereo capture",
        )
        require(
            images["lensRegion"]["width"] <= images["finalEye"]["width"]
            and images["lensRegion"]["height"] <= images["finalEye"]["height"]
            and all(
                images["lensRegion"][c] <= images["finalEye"][c]
                for c in ("redPixels", "greenPixels", "bluePixels")
            ),
            "Lens image counts/dimensions cannot be a region of the final eye",
        )
        result["evidence"]["scopeImages"] = images
        mounted = mounted_evidence(report, require_mounted)
        offset = report.get("requestedReliefOffsetMM")
        require(
            type(offset) in (int, float) and math.isfinite(offset),
            "Missing/nonfinite requested relief offset",
        )
        result.update(
            passed=True,
            runId=run_id,
            pid=pid,
            scene=report["scene"],
            item=item,
            selectedEye=selected,
            requestedReliefOffsetMM=offset,
            exactReliefOriginalSingularityCase=offset == 0,
            mountedFixture=mounted,
            diagnosticImageMeanNote="Mean is recomputed using integer peak sums; original Unity float accumulation is not an exact comparison gate.",
        )
    except (OSError, ValueError, TypeError, KeyError, AttributeError) as error:
        result["failures"].append(str(error))
    return result


def write_output(case_directory, path, result):
    root = Path(case_directory).resolve()
    path = Path(path).resolve()
    require(
        path.is_relative_to(root) and not path.exists(),
        "Output must be a fresh filename inside the case directory",
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".scope-verification-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w") as stream:
            json.dump(result, stream, indent=2, allow_nan=False)
            stream.write("\n")
        os.link(temporary, path)  # Exclusive atomic publish; never overwrite history.
    finally:
        Path(temporary).unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("case_directory", type=Path)
    parser.add_argument("--expected-item")
    parser.add_argument("--eye", choices=("left", "right"))
    parser.add_argument("--require-mounted", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = verify_scope_capture(
        args.case_directory, args.expected_item, args.eye, args.require_mounted
    )
    if args.output:
        write_output(args.case_directory, args.output, result)
    print(json.dumps(result, indent=2, allow_nan=False))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
