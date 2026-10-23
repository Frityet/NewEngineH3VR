#!/usr/bin/env python3
"""Bounded, opt-in scheduler for an already running NewEngineH3VR profiler.

Never launches, stops, or controls a game. Publishes only duration-bounded start
requests after process identity and fresh idle scene telemetry match the session.
"""

from __future__ import annotations
import argparse
from dataclasses import dataclass
from datetime import datetime, timezone, timedelta
import hashlib
import json
import math
import os
from pathlib import Path
import plistlib
import re
import subprocess
import tempfile
import time
import uuid
from audit_native_app import player_binary

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "newengineh3vr-live-profile-1"
# Original scene names from the recovered full build. Auto is deliberately
# fail-closed: unknown/mod scenes require explicit --scene NAME. Playable
# hub/staging/starting areas are included; only the authored main menu is omitted.
AUTO_GAMEPLAY_SCENES = frozenset(
    {
        "WarehouseRange_Rebuilt",
        "Testing3_LaserSword",
        "IndoorRange_Updated",
        "ArizonaTargets",
        "GrenadeSkeeball",
        "BreachAndClear_TestScene1",
        "ObstacleCourseScene1",
        "ObstacleCourseScene2",
        "ArizonaTargets_Night",
        "SniperRange",
        "Friendly45_New",
        "MeatGrinder",
        "NewSnowGlobe",
        "MeatGrinder_StartingScene",
        "OmnisequencerTesting3",
        "Wurstwurld1",
        "TileSetTest1",
        "TileSetTest_BigHallPerfTest",
        "SamplerPlatter",
        "TakeAndHold_1",
        "ProvingGround",
        "RotWienersStagingScene",
        "ReturnOfTheRotwieners",
        "Cappocolosseum",
        "TakeAndHold_Lobby_2",
        "TakeAndHoldClassic",
        "MF2_MainScene",
        "WinterWasteland",
        "Boomskee",
        "TakeAndHold_WinterWasteland",
        "ModBlank_Simple",
        "ModBlank_Take&Hold",
        "Grillhouse_2Story",
        "GP_Hangar",
        "Institution_Preview",
        "Institution",
        "GP_Sandtrap",
        "GP_Sandtrap_Night",
        "Friendly45_Dark",
        "Grillhouse_2Story_Night",
        "FrankFurther",
        "QualificationRange",
        "TakeAndHoldDark",
        "PhotoStudio",
        "BBQRange",
    }
)


def eligible_scene(scene, automatic=False):
    if not isinstance(scene, str) or not scene or scene != scene.strip():
        return False
    if scene in AUTO_GAMEPLAY_SCENES:
        return True
    compact = re.sub(r"[^a-z0-9]", "", scene.lower())
    if (
        "menu" in compact
        or compact.startswith(("boot", "loading", "splash", "startup", "initializ"))
        or compact in ("init", "loader", "loadscene", "persistent", "persistentmanager")
    ):
        return False
    return scene in AUTO_GAMEPLAY_SCENES if automatic else True


class IdentityError(RuntimeError):
    pass


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def parse_utc(value):
    if not isinstance(value, str):
        raise ValueError("UTC timestamp is missing")
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("Timestamp has no timezone")
    return parsed.astimezone(timezone.utc)


def fresh(status, now, maximum_age=5.0):
    try:
        age = (now - parse_utc(status.get("statusUpdatedUtc"))).total_seconds()
        return -2 <= age <= maximum_age
    except (TypeError, ValueError):
        return False


def read_json(path):
    """Atomic controller replacement has a brief missing-file window; retry later."""
    try:
        result = json.loads(Path(path).read_text())
        return result if isinstance(result, dict) else None
    except (FileNotFoundError, json.JSONDecodeError, UnicodeError):
        return None


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix="." + path.name + "-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w") as stream:
            json.dump(value, stream, indent=2, allow_nan=False)
            stream.write("\n")
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def publish_start(path, payload):
    """Publish complete JSON without overwriting another unconsumed request."""
    path = Path(path)
    body = (json.dumps(payload, separators=(",", ":"), allow_nan=False) + "\n").encode()
    if len(body) > 4096:
        raise ValueError("Profiler request exceeds controller size limit")
    descriptor, temporary = tempfile.mkstemp(prefix=".scheduler-request-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(body)
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, path)  # Atomic exclusive creation; EEXIST preserves a pending request.
    finally:
        Path(temporary).unlink(missing_ok=True)
    return hashlib.sha256(body).hexdigest()


@dataclass(frozen=True)
class Session:
    pid: int
    game_run_id: str
    app: Path
    executable: Path
    directory: Path
    profile_root: Path


def session_from_json(value, profile_root=None):
    pid = value.get("pid")
    game_id = value.get("gameRunId") or value.get("runId")
    if type(pid) is not int or pid <= 0 or not isinstance(game_id, str) or not game_id:
        raise ValueError("Session requires a positive PID and game run ID")
    app = Path(value["app"])
    directory = Path(value["directory"])
    if not app.is_absolute() or not directory.is_absolute():
        raise ValueError("Session app and directory must be absolute")
    app, directory = app.resolve(), directory.resolve()
    with (app / "Contents/Info.plist").open("rb") as stream:
        executable_name = plistlib.load(stream).get("CFBundleExecutable")
    if (
        not isinstance(executable_name, str)
        or Path(executable_name).name != executable_name
        or not executable_name
    ):
        raise ValueError("App has no safe CFBundleExecutable")
    # Packaging replaces the public entrypoint with a launcher, which execs the
    # separate Unity player. Match ps against that engine, as the matrix does.
    if executable_name not in ("NewEngineH3VR", "NativeH3VR"):
        raise ValueError("App entrypoint is not a recognized NewEngineH3VR launcher")
    executable = player_binary(app).resolve()
    if not executable.is_relative_to(app):
        raise ValueError("Unity player executable escapes the bound application")
    selected_root = Path(
        profile_root or value.get("profileDirectory") or directory / "profiles"
    ).resolve()
    if not selected_root.is_relative_to(directory):
        raise ValueError("Profiler root must be inside the bound session directory")
    return Session(pid, game_id, app, executable, directory, selected_root)


def parse_ps(text, pid, executable):
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    if not lines:
        return None
    if len(lines) != 1:
        raise IdentityError("Ambiguous process inventory")
    fields = lines[0].split(None, 6)
    if len(fields) != 7 or fields[0] != str(pid) or Path(fields[6]).resolve() != executable:
        raise IdentityError("PID executable differs from the bound application")
    start_text = " ".join(fields[1:6])
    start_utc = datetime.strptime(start_text, "%a %b %d %H:%M:%S %Y").astimezone(timezone.utc)
    return dict(
        pid=pid, executable=str(executable), startText=start_text, startUtc=start_utc.isoformat()
    )


def process_identity(session):
    result = subprocess.run(
        ["ps", "-ww", "-p", str(session.pid), "-o", "pid=,lstart=,comm="],
        text=True,
        capture_output=True,
        timeout=3,
        env=dict(os.environ, LC_ALL="C"),
    )
    if result.returncode not in (0, 1):
        raise IdentityError("Process inventory failed: " + result.stderr.strip())
    return parse_ps(result.stdout, session.pid, session.executable)


def validate_status(status, session, process, profiler_id=None):
    if (
        status.get("schema") != SCHEMA
        or type(status.get("pid")) is not int
        or status["pid"] != session.pid
        or status.get("gameRunId") != session.game_run_id
    ):
        raise IdentityError("Profiler status does not match the game session")
    run_id = status.get("runId")
    if (
        not isinstance(run_id, str)
        or not run_id
        or (profiler_id is not None and run_id != profiler_id)
    ):
        raise IdentityError("Profiler run ID changed or is missing")
    if (
        abs(
            (
                parse_utc(status.get("processStartUtc")) - parse_utc(process["startUtc"])
            ).total_seconds()
        )
        > 2
    ):
        raise IdentityError("Profiler process start differs from the live process")
    run_dir = Path(status["runDirectory"]).resolve()
    if run_dir == session.profile_root or not run_dir.is_relative_to(session.profile_root):
        raise IdentityError("Profiler run directory is outside the session profiler root")
    if Path(status["startRequestPath"]).resolve() != run_dir / "start.json":
        raise IdentityError("Profiler start request path is not bound to its run directory")
    if Path(status["stopRequestPath"]).resolve() != run_dir / "stop.json":
        raise IdentityError("Profiler stop request path is not bound to its run directory")
    return run_dir


class SceneStability:
    def __init__(self, scene, seconds):
        self.requested_scene, self.seconds = (None if scene == "auto" else scene), seconds
        self.reset()

    def reset(self):
        self.scene = self.requested_scene
        self.since = self.handle = self.first_frame = self.last_frame = self.last_observed = None

    def observe(self, status, monotonic_now, wall_now, maximum_age=5):
        scene = status.get("activeScene")
        if (
            not fresh(status, wall_now, maximum_age)
            or status.get("capturing") is not False
            or not eligible_scene(scene, automatic=self.requested_scene is None)
            or (self.requested_scene is not None and scene != self.requested_scene)
            or status.get("activeSceneLoaded") is not True
            or type(status.get("activeSceneHandle")) is not int
            or type(status.get("statusUnityFrame")) is not int
        ):
            self.reset()
            return False
        if self.scene != scene:
            self.reset()
            self.scene = scene
        if self.last_observed is not None and monotonic_now - self.last_observed > maximum_age:
            self.reset()
            self.scene = scene
        self.last_observed = monotonic_now
        frame, handle = status["statusUnityFrame"], status["activeSceneHandle"]
        if self.since is None or handle != self.handle or frame < self.last_frame:
            self.since, self.handle, self.first_frame = monotonic_now, handle, frame
        self.last_frame = frame
        return monotonic_now - self.since >= self.seconds and frame > self.first_frame


def acknowledge(status, pending, run_dir):
    """Controller owns capture IDs; bind its response to our unique label/count."""
    count, capture_id = status.get("capturesStarted"), status.get("captureId")
    if count == pending["previousCapturesStarted"]:
        return None
    if type(count) is not int or count != pending["previousCapturesStarted"] + 1:
        raise IdentityError("Unexpected concurrent profiler capture count")
    if not isinstance(capture_id, str) or not capture_id or Path(capture_id).name != capture_id:
        raise IdentityError("Controller has no safe capture ID")
    capture_dir = Path(status["captureDirectory"]).resolve()
    if capture_dir != run_dir / capture_id:
        raise IdentityError("Capture directory does not match assigned capture ID")
    try:
        label = (capture_dir / "label.txt").read_text().rstrip("\n")
    except (FileNotFoundError, UnicodeError):
        return None
    if label != pending["label"]:
        raise IdentityError("Capture label differs from this scheduler request")
    started = read_json(capture_dir / "started.json")
    if started is None:
        return None
    for field in ("pid", "runId", "gameRunId"):
        if started.get(field) != status.get(field):
            raise IdentityError("Capture start identity differs from controller status")
    if (
        started.get("captureId") != capture_id
        or started.get("requestedSeconds") != pending["seconds"]
    ):
        raise IdentityError("Capture start ID/duration differs from request")
    if not eligible_scene(pending.get("scene")) or started.get("startScene") != pending["scene"]:
        raise IdentityError("Capture started in a different scene than the observed request")
    if parse_utc(started["startedUtc"]) < parse_utc(pending["requestedUtc"]) - timedelta(seconds=2):
        raise IdentityError("Assigned capture predates this request")
    return dict(
        captureId=capture_id,
        captureDirectory=str(capture_dir),
        summaryPath=str(capture_dir / "summary.json"),
        captureStartedUtc=started["startedUtc"],
    )


def validate_summary(summary, pending, session, profiler_id, scene):
    failures = []
    for key, expected in [
        ("schema", SCHEMA),
        ("pid", session.pid),
        ("runId", profiler_id),
        ("gameRunId", session.game_run_id),
        ("captureId", pending["captureId"]),
        ("requestedSeconds", pending["seconds"]),
        ("state", "complete"),
        ("capturing", False),
        ("settingsRestored", True),
        ("restoreSkippedOwnershipConflict", False),
        ("sceneChanged", False),
        ("startScene", scene),
        ("endScene", scene),
    ]:
        if summary.get(key) != expected or (
            type(expected) in (bool, int) and type(summary.get(key)) is not type(expected)
        ):
            failures.append("Summary mismatch: " + key)
    for key in ("error", "restoreError"):
        if key not in summary:
            failures.append("Controller omitted " + key)
        elif summary.get(key):
            failures.append("Controller reports " + key + ": " + str(summary[key]))
    rows = summary.get("scenes")
    if (
        not isinstance(rows, list)
        or not rows
        or any(
            not isinstance(row, dict)
            or row.get("name") != scene
            or type(row.get("handle")) is not int
            or row.get("handle") != pending["sceneHandle"]
            for row in rows
        )
        or type(summary.get("sceneSegments")) is not int
        or summary.get("sceneSegments") != len(rows)
    ):
        failures.append("Captured scene segments differ from the stable target scene/handle")
    if type(summary.get("samples")) is not int or summary["samples"] <= 0:
        failures.append("No recorded observer samples")
    capture_dir = Path(pending["captureDirectory"])
    raw = capture_dir / "unity.raw"
    if (
        Path(summary.get("captureDirectory", "")).resolve() != capture_dir
        or Path(summary.get("rawPath", "")).resolve() != raw
    ):
        failures.append("Summary output paths differ from assigned capture directory")
    try:
        size = raw.stat().st_size
        if size <= 0 or type(summary.get("rawBytes")) is not int or summary.get("rawBytes") != size:
            failures.append("Raw profile is empty or size differs from completed summary")
    except FileNotFoundError:
        size = 0
        failures.append("Completed raw profile is missing")
    try:
        if summary.get("startedUtc") != pending.get("captureStartedUtc"):
            failures.append("Summary start timestamp differs from acknowledged capture")
        if not parse_utc(summary["startedUtc"]) <= parse_utc(summary["finishedUtc"]):
            failures.append("Completion precedes capture start")
    except (KeyError, TypeError, ValueError):
        failures.append("Capture completion timestamps are invalid")
    elapsed = summary.get("captureElapsedSeconds")
    if type(elapsed) not in (int, float) or not math.isfinite(elapsed) or elapsed <= 0:
        failures.append("Capture elapsed duration is missing/nonfinite/nonpositive")
    if summary.get("stopReason") not in (
        "duration",
        "sample-capacity",
        "binary-disk-threshold",
        "application-quit",
    ):
        failures.append(
            "Unexpected/interfered capture stop reason: " + str(summary.get("stopReason"))
        )
    return dict(
        valid=not failures,
        failures=failures,
        rawBytes=size,
        completedAsRequested=(
            not failures
            and summary.get("stopReason") == "duration"
            and elapsed >= pending["seconds"] - 0.25
        ),
        stopReason=summary.get("stopReason"),
        captureElapsedSeconds=elapsed,
        settingsRestored=summary.get("settingsRestored"),
        error=summary.get("error"),
    )


def run(args, session, receipt_path, session_sha):
    receipt = dict(
        schemaVersion=1,
        startedUtc=utc_now(),
        sessionFile=str(args.session.resolve()),
        sessionSha256=session_sha,
        pid=session.pid,
        gameRunId=session.game_run_id,
        app=str(session.app),
        executable=str(session.executable),
        scene=args.scene,
        sceneSelectionMode="auto" if args.scene == "auto" else "explicit",
        autoGameplayScenes=sorted(AUTO_GAMEPLAY_SCENES) if args.scene == "auto" else None,
        limits=dict(
            captures=args.captures,
            seconds=args.seconds,
            stableSeconds=args.stable_seconds,
            gapSeconds=args.gap_seconds,
            overallSeconds=args.overall_seconds,
            maximumStatusAgeSeconds=args.status_max_age,
        ),
        requests=[],
        captures=[],
        events=[],
        finished=False,
        gameControlled=False,
    )
    begin = time.monotonic()
    deadline = begin + args.overall_seconds
    original_process = None
    ready_path = run_dir = profiler_id = None
    pending = None
    gap_until = 0.0
    stability = SceneStability(args.scene, args.stable_seconds)
    last_progress = None

    def progress(state, **extra):
        nonlocal last_progress
        if state != last_progress:
            entry = dict(time=utc_now(), state=state, **extra)
            receipt["events"].append(entry)
            print(json.dumps(entry), flush=True)
            atomic_json(receipt_path, receipt)
            last_progress = state

    try:
        progress("waiting-for-process-and-fresh-profiler")
        while time.monotonic() < deadline:
            now = time.monotonic()
            wall_now = datetime.now(timezone.utc)
            current_process = process_identity(session)
            if original_process is None and current_process is not None:
                original_process = current_process
                receipt["processIdentity"] = current_process
            if current_process is not None and current_process != original_process:
                raise IdentityError(
                    "Bound PID was replaced; no replacement process will be followed"
                )
            if ready_path is None and current_process is not None:
                matches = []
                for candidate in session.profile_root.glob(f"ready-{session.pid}-*.json"):
                    status = read_json(candidate)
                    if status is None or not fresh(status, wall_now, args.status_max_age):
                        continue
                    try:
                        directory = validate_status(status, session, original_process)
                    except (IdentityError, KeyError, TypeError, ValueError):
                        continue
                    matches.append((candidate, status, directory))
                if len(matches) > 1:
                    raise IdentityError("Multiple fresh profiler controllers match this session")
                if matches:
                    ready_path, status, run_dir = matches[0]
                    profiler_id = status["runId"]
                    receipt.update(
                        profilerRunId=profiler_id,
                        readyPath=str(ready_path),
                        runDirectory=str(run_dir),
                    )
            status = read_json(ready_path) if ready_path else None
            if status is not None:
                validate_status(status, session, original_process, profiler_id)
            if pending is not None:
                if pending.get("captureId") is None and status is not None:
                    response = acknowledge(status, pending, run_dir)
                    if response:
                        pending.update(response)
                        progress(
                            "capturing-" + str(len(receipt["captures"]) + 1),
                            captureId=pending["captureId"],
                        )
                    elif (
                        status.get("state") in ("request-rejected", "request-error", "start-failed")
                        and not Path(status["startRequestPath"]).exists()
                        and parse_utc(status["statusUpdatedUtc"])
                        >= parse_utc(pending["requestedUtc"])
                    ):
                        raise RuntimeError(
                            "Profiler rejected this request: " + str(status.get("error"))
                        )
                if pending.get("captureId") is not None:
                    summary = read_json(pending["summaryPath"])
                    if summary is not None:
                        outcome = validate_summary(
                            summary, pending, session, profiler_id, pending["scene"]
                        )
                        summary_bytes = Path(pending["summaryPath"]).read_bytes()
                        row = dict(
                            pending,
                            **outcome,
                            summarySha256=hashlib.sha256(summary_bytes).hexdigest(),
                        )
                        receipt["captures"].append(row)
                        progress(
                            "capture-completed-" + str(len(receipt["captures"])),
                            captureId=pending["captureId"],
                            scene=pending["scene"],
                            sceneHandle=pending["sceneHandle"],
                            valid=outcome["valid"],
                        )
                        pending = None
                        if not outcome["valid"]:
                            receipt["stopReason"] = "capture-validation-failed"
                            break
                        if len(receipt["captures"]) >= args.captures:
                            receipt["stopReason"] = "capture-limit-completed"
                            break
                        gap_until = time.monotonic() + args.gap_seconds
                        stability.reset()
                if pending is not None:
                    if current_process is None:
                        receipt["stopReason"] = "process-exited-before-completed-summary"
                        break
                    limit = args.seconds + 40 if pending.get("captureId") else 20
                    if now - pending["requestedMonotonic"] > limit:
                        receipt["stopReason"] = (
                            "capture-completion-timeout"
                            if pending.get("captureId")
                            else "request-acknowledgment-timeout"
                        )
                        break
            if current_process is None:
                receipt["stopReason"] = "process-exited"
                break
            if pending is None and status is not None:
                if status.get("capturing") is True:
                    stability.reset()
                    progress("waiting-for-existing-profiler-capture")
                elif not fresh(status, wall_now, args.status_max_age):
                    stability.reset()
                    progress("waiting-for-fresh-idle-status")
                elif (
                    args.scene != "auto"
                    and receipt["captures"]
                    and (
                        status.get("activeScene") != args.scene
                        or status.get("activeSceneLoaded") is not True
                        or status.get("activeSceneHandle") != receipt["captures"][-1]["sceneHandle"]
                    )
                ):
                    receipt["stopReason"] = "scene-changed-after-capture"
                    break
                else:
                    stable = stability.observe(status, now, wall_now, args.status_max_age)
                    observed_scene = status.get("activeScene")
                    if (
                        args.scene == "auto"
                        and status.get("activeSceneLoaded") is True
                        and isinstance(observed_scene, str)
                        and observed_scene
                        and not eligible_scene(observed_scene, automatic=True)
                    ):
                        action = (
                            "Menus/boot/loading scenes never trigger; enter a gameplay scene."
                            if not eligible_scene(observed_scene)
                            else "Unknown gameplay map: restart scheduler with --scene "
                            + observed_scene
                            + " to select it explicitly."
                        )
                        progress(
                            "auto-rejected-loaded-scene-" + observed_scene,
                            observedScene=observed_scene,
                            action=action,
                        )
                    else:
                        waiting = (
                            ("waiting-for-stable-" + stability.scene)
                            if stability.scene
                            else "waiting-for-gameplay-scene"
                        )
                        progress(
                            waiting if not stable else "waiting-for-capture-gap",
                            observedScene=observed_scene,
                        )
                    if stable and now >= gap_until:
                        if deadline - now < args.seconds + 40:
                            receipt["stopReason"] = (
                                "insufficient-overall-budget-for-another-capture"
                            )
                            break
                        if status.get("developmentBuild") is not True:
                            raise RuntimeError("Bound player is not a Development build")
                        previous = status.get("capturesStarted")
                        maximum = status.get("maximumCaptures")
                        if (
                            type(previous) is not int
                            or type(maximum) is not int
                            or previous >= maximum
                        ):
                            receipt["stopReason"] = "controller-capture-limit"
                            break
                        if process_identity(session) != original_process:
                            raise IdentityError(
                                "Process changed immediately before publishing capture request"
                            )
                        # A scene can change during the process check. Re-read fresh
                        # telemetry and require stability again before publishing.
                        latest = read_json(ready_path)
                        if latest is None:
                            stability.reset()
                            continue
                        validate_status(latest, session, original_process, profiler_id)
                        if not stability.observe(
                            latest,
                            time.monotonic(),
                            datetime.now(timezone.utc),
                            args.status_max_age,
                        ):
                            continue
                        status = latest
                        scene = stability.scene
                        previous = status.get("capturesStarted")
                        maximum = status.get("maximumCaptures")
                        if status.get("developmentBuild") is not True:
                            raise RuntimeError("Bound player is not a Development build")
                        if (
                            type(previous) is not int
                            or type(maximum) is not int
                            or previous >= maximum
                        ):
                            receipt["stopReason"] = "controller-capture-limit"
                            break
                        label = (
                            "User-authorized gameplay capture; scene="
                            + scene
                            + "; scheduler="
                            + uuid.uuid4().hex
                        )
                        request = dict(
                            runId=profiler_id,
                            pid=session.pid,
                            seconds=args.seconds,
                            label=label,
                            scene=scene,
                            sceneHandle=status["activeSceneHandle"],
                        )
                        pending = dict(
                            requestedUtc=utc_now(),
                            requestedMonotonic=now,
                            label=label,
                            seconds=args.seconds,
                            previousCapturesStarted=previous,
                            scene=scene,
                            sceneHandle=status["activeSceneHandle"],
                            requestPath=status["startRequestPath"],
                            captureId=None,
                        )
                        pending["requestSha256"] = publish_start(pending["requestPath"], request)
                        receipt["requests"].append(pending)
                        progress(
                            "capture-requested-" + str(len(receipt["requests"])),
                            profilerRunId=profiler_id,
                            scene=scene,
                            sceneHandle=pending["sceneHandle"],
                        )
            time.sleep(min(1.0, max(0, deadline - time.monotonic())))
        else:
            receipt["stopReason"] = "overall-timeout"
    except KeyboardInterrupt:
        receipt["stopReason"] = (
            "scheduler-interrupted; any accepted bounded capture may finish independently"
        )
    except Exception as error:
        receipt["stopReason"] = "scheduler-error"
        receipt["error"] = str(error)
    receipt.update(
        finished=True,
        finishedUtc=utc_now(),
        elapsedSeconds=time.monotonic() - begin,
        pendingCapture=pending,
        validCaptures=sum(row["valid"] for row in receipt["captures"]),
    )
    atomic_json(receipt_path, receipt)
    print(
        json.dumps(
            dict(
                state="scheduler-finished",
                stopReason=receipt.get("stopReason"),
                validCaptures=receipt["validCaptures"],
                receipt=str(receipt_path),
            )
        ),
        flush=True,
    )
    return (
        0
        if receipt["validCaptures"] > 0
        and not receipt.get("error")
        and all(row["valid"] for row in receipt["captures"])
        else 1
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--session", type=Path, default=ROOT / "build/optih3vr/current-live-session.json"
    )
    parser.add_argument("--profile-root", type=Path)
    parser.add_argument("--receipt", type=Path)
    parser.add_argument(
        "--scene",
        default="auto",
        help="auto: stable original gameplay scene; or an explicit non-menu scene name",
    )
    parser.add_argument("--captures", type=int, default=3)
    parser.add_argument("--seconds", type=int, default=30)
    parser.add_argument("--stable-seconds", type=float, default=15)
    parser.add_argument("--gap-seconds", type=float, default=45)
    parser.add_argument("--overall-seconds", type=float, default=900)
    parser.add_argument("--status-max-age", type=float, default=5)
    args = parser.parse_args()
    if args.scene != "auto" and not eligible_scene(args.scene):
        parser.error("Scene must be auto or a non-menu/non-boot scene name")
    if not 1 <= args.captures <= 3 or not 1 <= args.seconds <= 60:
        parser.error("Captures must be 1..3 and duration 1..60 seconds")
    if (
        not all(
            math.isfinite(v) and v > 0
            for v in (
                args.stable_seconds,
                args.gap_seconds,
                args.overall_seconds,
                args.status_max_age,
            )
        )
        or args.overall_seconds > 900
        or args.status_max_age > 15
    ):
        parser.error("Positive finite timing limits required; overall<=900 and status age<=15")
    raw = args.session.read_bytes()
    session = session_from_json(json.loads(raw), args.profile_root)
    receipt = (
        args.receipt or session.directory / ("profile-scheduler-" + uuid.uuid4().hex + ".json")
    ).resolve()
    if receipt.exists() or not receipt.is_relative_to(session.directory):
        parser.error("Receipt must be a fresh path inside the bound session directory")
    return run(args, session, receipt, hashlib.sha256(raw).hexdigest())


if __name__ == "__main__":
    raise SystemExit(main())
