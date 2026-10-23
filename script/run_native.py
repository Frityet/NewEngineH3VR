#!/usr/bin/env python3
"""Bounded native integration run; a boot or route pass never implies parity."""

from pathlib import Path
import argparse
import ctypes
import hashlib
import json
import os
import re
import signal
import struct
import subprocess
import time
import uuid
from audit_native_app import check_saved_audit, player_binary
from original_scene_warnings import load_baseline, classify

ROOT = Path(__file__).resolve().parents[1]
ISSUE = re.compile(
    r"Exception:|DllNotFound|EntryPointNotFound|Could not|Couldn.t|\berror\b|"
    r"Debug[.:](?:LogError|LogException)|Failed to|Assertion failed|"
    r"Shader .*?(?:not supported|unsupported)|missing script|"
    r"referenced script .*?missing|Enlighten.*?(?:missing|failed)|"
    r"(?:missing|failed).*?Enlighten|NATIVE_GAMEPLAY_PROBE_FAIL|NATIVE_HEADSET_DEMO_FAILED",
    re.I,
)


def inspect_log(
    text,
    run_id,
    gameplay,
    fire,
    *,
    scene_smoke=False,
    scene=None,
    item=None,
    original_nulls=None,
    vault=False,
    demo=False,
    player_systems=False,
):
    """Return strict run evidence independently of LaunchServices' exit status."""
    lines = text.splitlines()
    issues, original_warnings, unobserved = classify(text, ISSUE, original_nulls)
    failures = []
    if not text:
        failures.append("Player log is empty or absent")
    if not re.search(r"^NATIVEH3VR_RUN_ID " + re.escape(run_id) + r" pid=\d+$", text, re.M):
        failures.append("Player log is not correlated with this run")
    if not re.search(r"^NATIVEH3VR_RUNTIME Unity=\S+ Graphics=Metal ", text, re.M):
        failures.append("Metal runtime initialization was not observed")
    if "NATIVE_STEAMVR_INITIALIZED backend=Metal/OpenXR synthetic=True" not in text:
        failures.append("Native synthetic stereo initialization was not observed")
    if "NativeH3VR eye submission source Metal formats=" not in text:
        failures.append("Native Metal eye submission was not observed")
    route_marker = bool(re.search(r"^NATIVE_GAMEPLAY_PROBE_PASS ", text, re.M))
    player_systems_route_marker = bool(
        re.search(
            r"^NATIVE_GAMEPLAY_PROBE_PASS scene="
            + re.escape(scene or "IndoorRange_Updated")
            + r" item="
            + re.escape(item or "M1911")
            + r" shots=0 playerSystems=true$",
            text,
            re.M,
        )
    )
    player_systems_marker = bool(
        re.search(
            r"^NATIVE_PLAYER_SYSTEMS_PASS wristGesture=true wristClick=true quickbeltStore=true "
            r"quickbeltRetrieve=true quickbeltRestow=true$",
            text,
            re.M,
        )
    )
    scene_marker = re.search(
        r"^NATIVE_SCENE_PROBE_PASS scene=(\S+) observationSeconds=5 stereoFrames=(\d+)$", text, re.M
    )
    demo_marker = re.search(
        r"^NATIVE_HEADSET_DEMO_READY scene=IndoorRange_Updated stereoFrames=([1-9]\d*) "
        r"tracking=synthetic controls=player-driven$",
        text,
        re.M,
    )
    if demo and not demo_marker:
        failures.append("Interactive demo startup did not reach the original indoor range")
    if demo and (
        "NATIVE_GAMEPLAY_STAGE " in text or "NATIVE_DIAGNOSTIC_INPUT_BEGIN" in text or route_marker
    ):
        failures.append("Interactive demo unexpectedly enabled the gameplay input fixture")
    vault_markers = re.findall(
        r"^NATIVE_GAMEPLAY_VAULT_FIXTURE_PASS path=(.+) sha256=([a-f0-9]{64}) "
        r"type=(\S+) objectId=(\S+) chamberRounds=([1-9]\d*) flags=(\d+) callbackCount=1 clearScene=false$",
        text,
        re.M,
    )
    if vault and (len(vault_markers) != 1 or vault_markers[0][3] != (item or "M1911")):
        failures.append(
            "Requested original Vault save/reload/respawn did not complete for this firearm"
        )
    if scene_smoke:
        if not scene_marker or scene_marker[1] != scene or int(scene_marker[2]) < 3:
            failures.append("Original scene observation did not complete for the requested scene")
    elif gameplay:
        if not route_marker or "NATIVE_GAMEPLAY_PROBE_FAIL" in text:
            failures.append("Original gameplay acceptance route did not complete")
        if player_systems:
            if not player_systems_marker:
                failures.append(
                    "Original wrist and quickbelt player-systems route did not complete"
                )
            if not player_systems_route_marker:
                failures.append(
                    "Gameplay probe did not confirm the expected no-fire player-systems route"
                )
            expected_stages = (
                "player-systems-ready",
                "wrist-face-bound",
                "wrist-gesture-open",
                "wrist-button-hover",
                "wrist-button-click",
                "wrist-gesture-close",
                "quickbelt-slot-selected",
                "quickbelt-original-store",
                "quickbelt-original-retrieve",
                "quickbelt-roundtrip-pass",
            )
            stage_names = {
                match.group(1) for match in re.finditer(r"^NATIVE_GAMEPLAY_STAGE (\S+)", text, re.M)
            }
            missing_stages = [name for name in expected_stages if name not in stage_names]
            if missing_stages:
                failures.append(
                    "Player-systems evidence is missing stages: " + ", ".join(missing_stages)
                )
            if not re.search(
                r"^NATIVE_GAMEPLAY_STAGE passed scene="
                + re.escape(scene or "IndoorRange_Updated")
                + r" item="
                + re.escape(item or "M1911")
                + r" wrist=true quickbelt=true$",
                text,
                re.M,
            ):
                failures.append(
                    "Player-systems result does not confirm wrist and quickbelt completion in the requested scene"
                )
        if fire and not re.search(r"^NATIVE_GAMEPLAY_PROBE_PASS .*\bshots=1(?:\s|$)", text, re.M):
            failures.append("The requested original in-game shot was not accepted")
        if fire and (
            not re.search(
                r"^NATIVE_GAMEPLAY_PROBE_PASS .*\bballisticProjectiles=[1-9]\d*(?:\s|$)", text, re.M
            )
            or not re.search(r"^NATIVE_GAMEPLAY_STAGE original-ballistic-projectile ", text, re.M)
        ):
            failures.append("No original moving ballistic projectile was observed")
        if scene is not None and not re.search(
            r"^NATIVE_GAMEPLAY_STAGE passed scene="
            + re.escape(scene)
            + r" item="
            + re.escape(item or "M1911")
            + r" ",
            text,
            re.M,
        ):
            failures.append("Gameplay result does not match the requested scene and item")
    else:
        observation = re.search(
            r"^NATIVEH3VR_OBSERVATION_END scene=(\S+) roots=(\d+) frames=(\d+)$", text, re.M
        )
        expected_scene = "IndoorRange_Updated" if demo else "MainMenu3"
        if not observation or observation[1] != expected_scene or int(observation[3]) < 2:
            failures.append(
                "Original "
                + ("demo range" if demo else "main-menu")
                + " observation did not finish"
            )
    if issues:
        failures.append("Player log contains unresolved errors or load failures")
    if (route_marker or scene_marker) and unobserved:
        failures.append("Original null-script warning counts differ from the bound scene baseline")
    return {
        "issues": issues,
        "failures": failures,
        "routePassMarker": route_marker,
        "playerSystemsRouteMarker": player_systems_route_marker,
        "playerSystemsPassMarker": player_systems_marker,
        "scenePassMarker": bool(scene_marker),
        "demoReadyMarker": bool(demo_marker),
        "vaultEvidence": [
            {
                "path": row[0],
                "sha256": row[1],
                "type": row[2],
                "item": row[3],
                "chamberRounds": int(row[4]),
                "flags": int(row[5]),
            }
            for row in vault_markers
        ],
        "originalNullScriptWarnings": original_warnings,
        "stages": [line for line in lines if line.startswith("NATIVE_GAMEPLAY_STAGE ")],
        "unavailableFeatures": list(
            dict.fromkeys(
                line for line in lines if line.startswith("Native H3VR feature unavailable:")
            )
        ),
    }


def png_evidence(path):
    # Every capture is moved aside before launch, so existence proves freshness.
    data = path.read_bytes()
    if (
        len(data) < 45
        or data[:8] != b"\x89PNG\r\n\x1a\n"
        or data[12:16] != b"IHDR"
        or data[-8:-4] != b"IEND"
    ):
        raise ValueError("Capture is not a complete PNG")
    width, height = struct.unpack(">II", data[16:24])
    if min(width, height) < 2:
        raise ValueError("Capture has no useful image dimensions")
    return {
        "path": str(path),
        "width": width,
        "height": height,
        "bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "visualAcceptance": "pending review",
    }


def stereo_capture_evidence(path, text, capture, *, route=False, demo=False):
    """Bind the compositor image to this process and a completed scene handoff.

    A fresh PNG can still precede the range transition in a short gameplay run.
    The runtime sidecar supplies the actual submitted frame, independently of
    the desktop ScreenCapture marker. This does not prove a captured gunshot.
    """
    raw = path.read_bytes()
    metadata = json.loads(raw)
    pid = re.search(r"^NATIVEH3VR_RUN_ID \S+ pid=(\d+)$", text, re.M)
    if not pid or metadata.get("process_id") != int(pid[1]):
        raise ValueError("Stereo capture does not belong to this player process")
    frame = metadata.get("composed_frames")
    if type(frame) is not int or frame <= 0:
        raise ValueError("Stereo capture has no submitted frame count")
    if metadata.get("width") != capture["width"] or metadata.get("height") != capture["height"]:
        raise ValueError("Stereo capture dimensions differ from its metadata")
    if metadata.get("native_hmd_submitted_frames") != 0:
        raise ValueError("Synthetic test capture reports physical headset submission")
    minimum = 1
    if route:
        handoff = re.search(
            r"^NATIVE_GAMEPLAY_STAGE native-camera-handoff frames=\d+->(\d+) ", text, re.M
        )
        if not handoff:
            raise ValueError("Stereo capture has no completed range camera handoff")
        minimum = int(handoff[1])
    elif demo:
        ready = re.search(
            r"^NATIVE_HEADSET_DEMO_READY scene=IndoorRange_Updated stereoFrames=(\d+) ", text, re.M
        )
        if not ready:
            raise ValueError("Stereo capture has no completed demo startup")
        minimum = int(ready[1])
    if frame < minimum:
        raise ValueError(
            f"Stereo capture frame {frame} precedes the completed scene handoff at {minimum}"
        )
    return {
        "metadataPath": str(path),
        "metadataSha256": hashlib.sha256(raw).hexdigest(),
        "processId": metadata["process_id"],
        "composedFrames": frame,
        "minimumSceneFrame": minimum,
        "nativeHmdSubmittedFrames": 0,
        "capturedAt": metadata.get("time"),
        "gameplayMomentValidated": False,
    }


def startup_evidence(text, run_id):
    """Diagnostics run before scene loading; also require actual stereo submission."""
    return bool(
        re.search(r"^NATIVEH3VR_RUN_ID " + re.escape(run_id) + r" pid=\d+$", text, re.M)
        and re.search(r"^NATIVEH3VR_RUNTIME Unity=\S+ Graphics=Metal ", text, re.M)
        and "NATIVE_STEAMVR_INITIALIZED backend=Metal/OpenXR synthetic=True" in text
        and "NativeH3VR eye submission source Metal formats=" in text
    )


def supervise(
    process,
    read_log,
    run_id,
    startup_timeout,
    runtime_timeout,
    *,
    started=None,
    monotonic=time.monotonic,
    sleep=time.sleep,
    poll_interval=0.2,
    original_nulls=None,
):
    """Observe a process under separate startup/runtime and absolute deadlines.

    Runtime time starts once, on correlated initialization plus first stereo
    submission. Repeated markers cannot reset it. Cleanup belongs to the caller.
    """
    start = monotonic() if started is None else started
    ready_at = None
    hard_deadline = start + startup_timeout + runtime_timeout
    while True:
        text = read_log()
        now = monotonic()
        issues, _, _ = classify(text, ISSUE, original_nulls)
        if issues:
            reason = "log-error"
            break
        if ready_at is None and now < start + startup_timeout and startup_evidence(text, run_id):
            ready_at = now
        deadline = min(
            hard_deadline,
            start + startup_timeout if ready_at is None else ready_at + runtime_timeout,
        )
        if now >= deadline:
            reason = "startup-timeout" if ready_at is None else "runtime-timeout"
            break
        if process.poll() is not None:
            reason = "process-exited"
            break
        sleep(min(poll_interval, deadline - now))
    return {
        "stopReason": reason,
        "startupTimeoutSeconds": startup_timeout,
        "runtimeTimeoutSeconds": runtime_timeout,
        "hardTimeoutSeconds": startup_timeout + runtime_timeout,
        "startupReady": ready_at is not None,
        "startupSeconds": (ready_at if ready_at is not None else now) - start,
        "runtimeSeconds": None if ready_at is None else now - ready_at,
        "supervisedSeconds": now - start,
        "readiness": "correlated run ID, Metal runtime, synthetic stereo initialization, first eye submission",
    }


def player_instances(exe, timeout=2):
    """Return exact executable identities; never match arbitrary command substrings."""
    output = subprocess.check_output(
        ["ps", "-ww", "-axo", "pid=,lstart=,stat=,comm="], text=True, timeout=timeout
    )
    found = {}
    for line in output.splitlines():
        fields = line.split(None, 7)
        if len(fields) == 8 and "Z" not in fields[6] and Path(fields[7]).resolve() == exe.resolve():
            found[int(fields[0])] = " ".join(fields[1:6])
    return found


def process_arguments(pid):
    """Read macOS argv only. Do not parse, retain or log process environment values."""
    libc = ctypes.CDLL(None, use_errno=True)
    mib = (ctypes.c_int * 3)(1, 49, pid)  # CTL_KERN, KERN_PROCARGS2
    size = ctypes.c_size_t()
    if libc.sysctl(mib, 3, None, ctypes.byref(size), None, 0) != 0:
        raise OSError(ctypes.get_errno(), "Cannot read process argument size")
    buffer = ctypes.create_string_buffer(size.value)
    if libc.sysctl(mib, 3, buffer, ctypes.byref(size), None, 0) != 0:
        raise OSError(ctypes.get_errno(), "Cannot read process arguments")
    data = buffer.raw[: size.value]
    argc = struct.unpack_from("i", data)[0]
    cursor = data.index(b"\0", 4) + 1  # Skip executable path and padding.
    while cursor < len(data) and data[cursor] == 0:
        cursor += 1
    arguments = []
    for _ in range(argc):
        end = data.index(b"\0", cursor)
        arguments.append(os.fsdecode(data[cursor:end]))
        cursor = end + 1
    return arguments


def stop_player(process, exe, log, existing, *, grace=2.0, kill_wait=2.0):
    """Stop only this launch's exact executable/log pair, including pre-diagnostics.

    Pre-existing instances are excluded. Recheck PID/start identity before every
    signal; SIGKILL is limited to tracked instances still alive after TERM grace.
    An already-exited `open -W` does not skip native-player cleanup.
    """
    report = {"matchedPids": [], "termPids": [], "killPids": [], "remainingPids": [], "errors": []}
    tracked = {}
    term_times = {}
    deadline = time.monotonic() + 2 * grace + kill_wait + 4
    report["maximumCleanupSeconds"] = 2 * grace + kill_wait + 8

    def matching():
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("Process cleanup deadline exceeded")
        current = player_instances(exe, timeout=min(2, remaining))
        matches = {}
        for pid, identity in current.items():
            if existing.get(pid) == identity:
                continue
            try:
                argv = process_arguments(pid)
            except OSError:
                if (
                    player_instances(
                        exe, timeout=min(2, max(0.01, deadline - time.monotonic()))
                    ).get(pid)
                    != identity
                ):
                    continue  # Process exited between inventory and argument read.
                raise
            if any(argv[i] == "-logFile" and argv[i + 1] == str(log) for i in range(len(argv) - 1)):
                matches[pid] = identity
        return matches

    def send(pid, identity, sig, key):
        if matching().get(pid) != identity:
            return
        try:
            os.kill(pid, sig)
            report[key].append(pid)
        except ProcessLookupError:
            pass

    try:
        end = time.monotonic() + grace
        while True:
            current = matching()
            for pid, identity in current.items():
                if tracked.get(pid) != identity:
                    tracked[pid] = identity
                    report["matchedPids"].append(pid)
                    send(pid, identity, signal.SIGTERM, "termPids")
                    term_times[pid] = time.monotonic()
            # Stop the launch request too; continue observing for delayed startup.
            if process.poll() is None:
                process.terminate()
            if time.monotonic() >= end:
                break
            time.sleep(min(0.05, max(0, end - time.monotonic())))
        # A process discovered late receives the same full TERM grace.
        if term_times:
            time.sleep(max(0, min(max(term_times.values()) + grace, deadline) - time.monotonic()))
        for pid, identity in tracked.items():
            send(pid, identity, signal.SIGKILL, "killPids")
        end = time.monotonic() + kill_wait
        while True:
            remaining = matching()
            # A late instance was not given TERM grace: report it, never blanket-kill it.
            report["remainingPids"] = sorted(remaining)
            if not remaining or time.monotonic() >= end:
                break
            time.sleep(0.05)
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        report["errors"].append("Player cleanup: " + str(error))
    finally:
        try:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            try:
                process.kill()
                process.wait(timeout=2)
            except (OSError, subprocess.SubprocessError) as error:
                report["errors"].append("LaunchServices cleanup: " + str(error))
        except OSError as error:
            report["errors"].append("LaunchServices cleanup: " + str(error))
    report["verifiedExited"] = not report["remainingPids"] and not report["errors"]
    return report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=int, default=30)
    parser.add_argument(
        "--startup-timeout",
        type=int,
        default=600,
        help="Maximum seconds before correlated runtime and first stereo submission (default: 600)",
    )
    parser.add_argument(
        "--gameplay", action="store_true", help="Run original scene/load/grip/trigger acceptance"
    )
    parser.add_argument(
        "--player-systems",
        action="store_true",
        help="Run original wrist-menu and quickbelt interaction acceptance (implies --gameplay)",
    )
    parser.add_argument("--fire", action="store_true", help="Include one real in-game shot")
    parser.add_argument(
        "--vault",
        action="store_true",
        help="Include an isolated original Handgun Vault save/reload/respawn",
    )
    parser.add_argument(
        "--scene-smoke",
        action="store_true",
        help="Load and observe an original scene for five seconds",
    )
    parser.add_argument(
        "--no-captures",
        action="store_true",
        help="Scene cadence benchmark only: omit screenshot/readback overhead; no visual proof",
    )
    parser.add_argument(
        "--demo",
        action="store_true",
        help="Check the interactive demo startup with synthetic head tracking and no input fixture",
    )
    parser.add_argument("--scene", help="Original build scene name (default: IndoorRange_Updated)")
    parser.add_argument("--item", help="Original firearm object ID for gameplay (default: M1911)")
    parser.add_argument(
        "--output-dir", type=Path, help="Separate evidence directory under this workspace build/"
    )
    parser.add_argument("--app", type=Path, default=ROOT / "dist/NewEngineH3VR.app")
    parser.add_argument(
        "--audit-report",
        type=Path,
        default=ROOT / "build/native-app-audit.json",
        help="Saved audit for this exact generated app; permits isolated candidate testing",
    )
    args = parser.parse_args()
    if args.player_systems and (
        args.fire or args.vault or args.scene_smoke or args.demo or args.scene or args.item
    ):
        parser.error(
            "--player-systems cannot be combined with --fire, --vault, --scene-smoke, --demo, --scene or --item"
        )
    gameplay = args.gameplay or args.player_systems
    if args.fire and not gameplay:
        parser.error("--fire requires --gameplay")
    if args.vault and not gameplay:
        parser.error("--vault requires --gameplay")
    if args.scene_smoke and gameplay:
        parser.error("--scene-smoke and --gameplay select different acceptance routes")
    if args.demo and (args.scene_smoke or gameplay):
        parser.error("--demo cannot run with --scene-smoke or --gameplay")
    if args.no_captures and (
        not args.scene_smoke or os.environ.get("NATIVEH3VR_PERF_ENABLED") != "1"
    ):
        parser.error("--no-captures requires --scene-smoke and NATIVEH3VR_PERF_ENABLED=1")
    if args.scene and not (gameplay or args.scene_smoke):
        parser.error("--scene requires --gameplay or --scene-smoke")
    if args.item and not gameplay:
        parser.error("--item requires --gameplay")
    for value in (args.scene, args.item):
        if value is not None and not re.fullmatch(r"[A-Za-z0-9_().&-]+", value):
            parser.error("Scene and item IDs must be nonempty names without whitespace or paths")
    if args.seconds <= 0:
        parser.error("--seconds must be positive")
    if not 1 <= args.startup_timeout <= 3600:
        parser.error("--startup-timeout must be between 1 and 3600 seconds")
    app = args.app.resolve()
    if not app.is_relative_to(ROOT / "dist"):
        raise SystemExit("Only generated NativeH3VR applications may be tested")
    audit_report = args.audit_report.resolve()
    if not audit_report.is_relative_to(ROOT / "build"):
        parser.error("--audit-report must be inside this workspace build/")
    route = gameplay or args.scene_smoke
    scene = args.scene or "IndoorRange_Updated"
    item = args.item or "M1911"
    output_dir = (args.output_dir or ROOT / "build").resolve()
    if not output_dir.is_relative_to(ROOT / "build"):
        parser.error("--output-dir must be inside this workspace build/")
    output_dir.mkdir(parents=True, exist_ok=True)
    route_name = (
        "native-player-systems" if args.player_systems else "native-gameplay" if route else "native"
    )
    output = output_dir / (route_name + "-run.json")
    if output.exists():
        output.replace(output.with_suffix(".previous.json"))
    try:
        binary_inventory = check_saved_audit(app, audit_report)
        original_nulls = load_baseline(app, scene) if route else None
    except (OSError, ValueError, KeyError) as error:
        failure = {
            "verificationPassed": False,
            "gameplayParityValidated": False,
            "phase": "packaged application preflight",
            "app": str(app),
            "failures": [str(error)],
        }
        output.write_text(json.dumps(failure, indent=2) + "\n")
        raise SystemExit("Packaged application preflight failed: " + str(error))
    exe = player_binary(app)
    architecture = subprocess.check_output(["lipo", "-archs", str(exe)], text=True).strip()
    if architecture != "arm64":
        raise SystemExit("Integration tests require an ARM64-only player")
    log = output_dir / (route_name + ".log")
    screen = output_dir / (route_name + ".png")
    stereo = output_dir / "native-game-stereo.png"
    stereo_metadata = stereo.with_suffix(stereo.suffix + ".json")
    for previous in (log, screen, stereo, stereo_metadata):
        if previous.exists():
            previous.replace(previous.with_suffix(".previous" + previous.suffix))
    run_id = str(uuid.uuid4())
    env = dict(os.environ)
    for override in list(env):
        if override in (
            "NATIVEH3VR_RUNTIME",
            "NATIVEH3VR_ACTIONS",
            "NATIVEH3VR_DIAGNOSTIC_INPUT",
            "NATIVEH3VR_HEADSET_DEMO",
        ) or override.startswith("NATIVEH3VR_GAMEPLAY_"):
            env.pop(override)
    env.update(
        NATIVEH3VR_RUN_ID=run_id,
        NATIVEH3VR_TEST_DURATION=str(args.seconds),
        NATIVEH3VR_OPENXR="synthetic",
        NATIVEH3VR_SCREENSHOT=str(screen),
        VR_OPENXR_CAPTURE=str(stereo),
        VR_OPENXR_CAPTURE_EVERY="30" if route else "180",
    )
    if args.demo:
        env["NATIVEH3VR_HEADSET_DEMO"] = "1"
    if route:
        env.update(
            NATIVEH3VR_TEST_DURATION="0",
            NATIVEH3VR_DIAGNOSTIC_INPUT="1",
            NATIVEH3VR_GAMEPLAY_PROBE="1",
            NATIVEH3VR_GAMEPLAY_FIRE="1" if args.fire else "0",
            NATIVEH3VR_GAMEPLAY_SCREENSHOT=str(screen),
            NATIVEH3VR_GAMEPLAY_SCENE=scene,
            NATIVEH3VR_GAMEPLAY_ITEM=item,
            NATIVEH3VR_GAMEPLAY_SCENE_ONLY="1" if args.scene_smoke else "0",
        )
        if args.player_systems:
            env["NATIVEH3VR_GAMEPLAY_PLAYER_SYSTEMS"] = "1"
        if args.vault:
            env["NATIVEH3VR_GAMEPLAY_VAULT_DIRECTORY"] = str(
                ROOT / "platform/gameplay-probe/build/vault-fixture"
            )
    if args.no_captures:
        for key in (
            "VR_OPENXR_CAPTURE",
            "VR_OPENXR_CAPTURE_EVERY",
            "NATIVEH3VR_SCREENSHOT",
            "NATIVEH3VR_GAMEPLAY_SCREENSHOT",
        ):
            env.pop(key, None)
    existing_players = player_instances(exe)
    start = time.monotonic()
    process = subprocess.Popen(
        ["open", "-n", "-W", str(app), "--args", "-force-metal", "-logFile", str(log)], env=env
    )

    def read_log():
        try:
            return log.read_text(errors="replace")
        except FileNotFoundError:
            return ""

    cleanup_start = None
    try:
        supervision = supervise(
            process,
            read_log,
            run_id,
            args.startup_timeout,
            210 if route else args.seconds + 90,
            started=start,
            original_nulls=original_nulls,
        )
    finally:
        cleanup_start = time.monotonic()
        cleanup = stop_player(process, exe, log, existing_players)
    cleanup_seconds = 0 if cleanup_start is None else time.monotonic() - cleanup_start
    timed_out = supervision["stopReason"].endswith("-timeout")
    text = read_log()
    result = inspect_log(
        text,
        run_id,
        gameplay,
        args.fire,
        scene_smoke=args.scene_smoke,
        scene=scene if route else None,
        item=item if gameplay else None,
        original_nulls=original_nulls,
        vault=args.vault,
        demo=args.demo,
        player_systems=args.player_systems,
    )
    result.update(
        architecture=architecture,
        duration=time.monotonic() - start,
        timedOut=timed_out,
        runId=run_id,
        testMode="synthetic isolated OpenXR",
        launchServicesExitCode=process.returncode,
        log=str(log),
        logSha256=hashlib.sha256(text.encode()).hexdigest(),
        playerSha256=hashlib.sha256(exe.read_bytes()).hexdigest(),
        binaryInventory=binary_inventory,
        gameplayParityValidated=False,
        captures=[],
        capturePolicy="disabled-for-performance" if args.no_captures else "required",
        supervision=supervision,
        cleanupSeconds=cleanup_seconds,
        cleanup=cleanup,
    )
    if timed_out:
        result["failures"].append("Player exceeded the bounded " + supervision["stopReason"])
    if supervision["stopReason"] == "log-error":
        result["failures"].append("Supervisor stopped on a player log error")
    if not cleanup["verifiedExited"]:
        result["failures"].append("Generated player cleanup could not verify process exit")
    result["failures"].extend(cleanup["errors"])
    if supervision["stopReason"] == "process-exited" and cleanup["termPids"]:
        result["failures"].append(
            "LaunchServices exited while the generated player was still running"
        )
    if process.returncode:
        result["failures"].append("LaunchServices returned a nonzero status")
    for capture in () if args.no_captures else (screen, stereo):
        try:
            evidence = png_evidence(capture)
            result["captures"].append(evidence)
            if capture == stereo:
                evidence["compositor"] = stereo_capture_evidence(
                    stereo_metadata, text, evidence, route=route, demo=args.demo
                )
        except (OSError, ValueError) as error:
            result["failures"].append(f"Capture {capture.name}: {error}")
    if args.vault:
        for record in result["vaultEvidence"]:
            try:
                path = Path(record["path"]).resolve()
                if path.parent != (ROOT / "platform/gameplay-probe/build/vault-fixture").resolve():
                    raise ValueError(
                        "Vault evidence path is outside the isolated fixture directory"
                    )
                if hashlib.sha256(path.read_bytes()).hexdigest() != record["sha256"]:
                    raise ValueError("Vault file does not match the in-game save hash")
            except (OSError, ValueError) as error:
                result["failures"].append(str(error))
    result["verificationPassed"] = not result["failures"]
    if args.demo:
        result["demoStartupPassed"] = result["verificationPassed"] and result["demoReadyMarker"]
    if gameplay:
        result["acceptanceRoutePassed"] = result["verificationPassed"] and result["routePassMarker"]
        result["shotRequested"] = args.fire
        result["requestedItem"] = item
        result["vaultRequested"] = args.vault
        result["playerSystemsRequested"] = args.player_systems
        result["playerSystemsPassed"] = (
            result["verificationPassed"]
            and result["playerSystemsPassMarker"]
            and result["playerSystemsRouteMarker"]
            if args.player_systems
            else None
        )
    if args.scene_smoke:
        result["sceneObservationPassed"] = (
            result["verificationPassed"] and result["scenePassMarker"]
        )
    if route:
        result["requestedScene"] = scene
        result["originalNullScriptBaseline"] = original_nulls
    output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))
    return 0 if result["verificationPassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
