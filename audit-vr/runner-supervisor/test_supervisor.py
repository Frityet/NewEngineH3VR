#!/usr/bin/env python3
"""No Unity/hardware: deterministic deadline tests plus real subprocess/log I/O."""

import importlib.util, json, sys, time, subprocess, tempfile, unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "script"))
import run_native as runner

RUN = "current-fixture"
DIAGNOSTICS = (
    "NATIVEH3VR_RUNTIME Unity=2021.3.45f2 Graphics=Metal GPU=fixture\nNATIVEH3VR_RUN_ID "
    + RUN
    + " pid=42\n"
)
STEREO = "NATIVE_STEAMVR_INITIALIZED backend=Metal/OpenXR synthetic=True actionCount=21\nNativeH3VR eye submission source Metal formats=71,71\n"
PASS = "NATIVEH3VR_OBSERVATION_END scene=MainMenu3 roots=42 frames=12\n"


class Clock:
    def __init__(self):
        self.now = 0.0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class Process:
    def __init__(self, clock, exit_at=None):
        self.clock = clock
        self.exit_at = exit_at

    def poll(self):
        return 0 if self.exit_at is not None and self.clock.now >= self.exit_at else None


class SupervisorTests(unittest.TestCase):
    def run_case(self, events, exit_at=None, startup=300, runtime=30):
        clock = Clock()
        proc = Process(clock, exit_at)
        read = lambda: "".join(s for at, s in events if clock.now >= at)
        return runner.supervise(
            proc,
            read,
            RUN,
            startup,
            runtime,
            monotonic=clock.monotonic,
            sleep=clock.sleep,
            poll_interval=1,
        )

    def test_slow_startup_before_diagnostics(self):
        r = self.run_case([(240, DIAGNOSTICS + STEREO + PASS)], 250)
        self.assertEqual(r["stopReason"], "process-exited")
        self.assertEqual(r["startupSeconds"], 240)
        self.assertEqual(r["runtimeSeconds"], 10)

    def test_diagnostics_do_not_end_startup_before_first_stereo(self):
        r = self.run_case([(1, DIAGNOSTICS), (240, STEREO + PASS)], 250)
        self.assertEqual(r["startupSeconds"], 240)
        self.assertEqual(r["stopReason"], "process-exited")

    def test_missing_current_run_correlation_stays_bounded(self):
        r = self.run_case([(1, (DIAGNOSTICS + STEREO).replace(RUN, "previous-run"))])
        self.assertFalse(r["startupReady"])
        self.assertEqual(r["stopReason"], "startup-timeout")
        self.assertEqual(r["supervisedSeconds"], 300)

    def test_missing_stereo_is_startup_failure(self):
        r = self.run_case([(1, DIAGNOSTICS)])
        self.assertEqual(r["stopReason"], "startup-timeout")

    def test_gameplay_hang_and_repeated_markers_do_not_extend_deadline(self):
        r = self.run_case([(5, DIAGNOSTICS + STEREO), (200, DIAGNOSTICS + STEREO)], runtime=210)
        self.assertEqual(r["stopReason"], "runtime-timeout")
        self.assertEqual(r["supervisedSeconds"], 215)

    def test_normal_run(self):
        r = self.run_case([(1, DIAGNOSTICS + STEREO), (8, PASS)], 10)
        self.assertEqual(r["stopReason"], "process-exited")
        self.assertTrue(r["startupReady"])
        self.assertFalse(
            runner.inspect_log(DIAGNOSTICS + STEREO + PASS, RUN, False, False)["failures"]
        )

    def test_early_exit_does_not_wait_out_startup(self):
        r = self.run_case([], 7)
        self.assertEqual(r["stopReason"], "process-exited")
        self.assertEqual(r["supervisedSeconds"], 7)
        self.assertTrue(runner.inspect_log("", RUN, False, False)["failures"])

    def test_real_error_during_warmup_fails_immediately(self):
        r = self.run_case([(2, "DllNotFoundException: missing required plugin\n")])
        self.assertEqual(r["stopReason"], "log-error")
        self.assertEqual(r["supervisedSeconds"], 2)

    def test_error_after_success_marker_is_still_rejected(self):
        text = DIAGNOSTICS + STEREO + PASS + "Exception: real teardown failure\n"
        r = self.run_case([(1, text)], 2)
        self.assertEqual(r["stopReason"], "log-error")
        self.assertTrue(runner.inspect_log(text, RUN, False, False)["failures"])

    def test_late_startup_is_not_rescued_by_ready_markers(self):
        r = self.run_case([(300, DIAGNOSTICS + STEREO)])
        self.assertEqual(r["stopReason"], "startup-timeout")
        self.assertLessEqual(r["supervisedSeconds"], r["hardTimeoutSeconds"])

    def test_strict_error_gate_remains_fail_closed(self):
        for issue in [
            "Exception: failed",
            "DllNotFoundException: native",
            "EntryPointNotFoundException: symbol",
            "Could not load dependency",
            "Couldn't load library",
            "Shader error in Test",
            "Debug.LogError: broken",
            "Failed to load asset",
            "Assertion failed",
            "Shader Test is not supported",
            "missing script",
            "NATIVE_GAMEPLAY_PROBE_FAIL timeout",
        ]:
            with self.subTest(issue=issue):
                self.assertTrue(
                    runner.inspect_log(DIAGNOSTICS + STEREO + PASS + issue, RUN, False, False)[
                        "failures"
                    ]
                )

    def test_scene_route_cannot_accept_a_menu_or_other_scene(self):
        scene = "IndoorRange_Updated"
        good = (
            DIAGNOSTICS
            + STEREO
            + "NATIVE_SCENE_PROBE_PASS scene="
            + scene
            + " observationSeconds=5 stereoFrames=45\n"
        )
        self.assertFalse(
            runner.inspect_log(good, RUN, False, False, scene_smoke=True, scene=scene)["failures"]
        )
        for text in (
            DIAGNOSTICS + STEREO + PASS,
            good.replace(scene, "MainMenu3"),
            good.replace("stereoFrames=45", "stereoFrames=0"),
            good + "Exception: scene failure\n",
        ):
            self.assertTrue(
                runner.inspect_log(text, RUN, False, False, scene_smoke=True, scene=scene)[
                    "failures"
                ]
            )

    def test_gameplay_result_must_match_requested_item_and_scene(self):
        good = (
            DIAGNOSTICS
            + STEREO
            + "NATIVE_GAMEPLAY_STAGE passed scene=IndoorRange_Updated item=M1911 originalShotEvents=0 synthetic=true\nNATIVE_GAMEPLAY_PROBE_PASS shots=0 ballisticProjectiles=0\n"
        )
        self.assertFalse(
            runner.inspect_log(good, RUN, True, False, scene="IndoorRange_Updated", item="M1911")[
                "failures"
            ]
        )
        for scene, item in [("MainMenu3", "M1911"), ("IndoorRange_Updated", "AKM")]:
            self.assertTrue(
                runner.inspect_log(good, RUN, True, False, scene=scene, item=item)["failures"]
            )

    def test_demo_requires_interactive_range_startup_and_observation(self):
        ready = "NATIVE_HEADSET_DEMO_READY scene=IndoorRange_Updated stereoFrames=30 tracking=synthetic controls=player-driven\n"
        observed = PASS.replace("MainMenu3", "IndoorRange_Updated")
        good = DIAGNOSTICS + STEREO + ready + observed
        self.assertFalse(runner.inspect_log(good, RUN, False, False, demo=True)["failures"])
        for text in (
            DIAGNOSTICS + STEREO + observed,
            DIAGNOSTICS + STEREO + ready + PASS,
            good.replace("tracking=synthetic", "tracking=live"),
            good.replace("stereoFrames=30", "stereoFrames=0"),
            good + "NATIVE_GAMEPLAY_STAGE original-grip-input\n",
            good
            + "NATIVE_DIAGNOSTIC_INPUT_BEGIN simulated controller poses/buttons/proximity only\n",
            good + "NATIVE_HEADSET_DEMO_FAILED timed out loading IndoorRange_Updated\n",
        ):
            self.assertTrue(runner.inspect_log(text, RUN, False, False, demo=True)["failures"])

    def test_stereo_capture_rejects_a_fresh_transition_frame(self):
        text = (
            DIAGNOSTICS
            + STEREO
            + "NATIVE_GAMEPLAY_STAGE native-camera-handoff frames=43->192 camera=eye pre=2 post=2\n"
        )
        metadata = {
            "process_id": 42,
            "composed_frames": 300,
            "width": 2880,
            "height": 1600,
            "native_hmd_submitted_frames": 0,
        }
        capture = {"width": 2880, "height": 1600}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "stereo.png.json"
            path.write_text(json.dumps(metadata))
            result = runner.stereo_capture_evidence(path, text, capture, route=True)
            self.assertEqual(result["composedFrames"], 300)
            self.assertEqual(result["minimumSceneFrame"], 192)
            for change in (
                {"composed_frames": 180},
                {"process_id": 43},
                {"width": 1440},
                {"native_hmd_submitted_frames": 1},
            ):
                path.write_text(json.dumps(dict(metadata, **change)))
                with self.subTest(change=change), self.assertRaises(ValueError):
                    runner.stereo_capture_evidence(path, text, capture, route=True)

    def test_demo_stereo_capture_must_follow_ready(self):
        text = (
            DIAGNOSTICS
            + STEREO
            + "NATIVE_HEADSET_DEMO_READY scene=IndoorRange_Updated stereoFrames=194 tracking=synthetic controls=player-driven\n"
        )
        metadata = {
            "process_id": 42,
            "composed_frames": 3600,
            "width": 2880,
            "height": 1600,
            "native_hmd_submitted_frames": 0,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "stereo.png.json"
            path.write_text(json.dumps(metadata))
            self.assertEqual(
                runner.stereo_capture_evidence(path, text, metadata, demo=True)[
                    "minimumSceneFrame"
                ],
                194,
            )
            metadata["composed_frames"] = 180
            path.write_text(json.dumps(metadata))
            with self.assertRaises(ValueError):
                runner.stereo_capture_evidence(path, text, metadata, demo=True)

    def test_original_null_warnings_are_limited_to_bound_scene_load_and_count(self):
        baseline = {"scene": "ArizonaTargets", "names": {"Fencepole 2": 1}}
        warning = (
            "The referenced script on this Behaviour (Game Object 'Fencepole 2') is missing!\n"
        )
        transition = "NATIVE_GAMEPLAY_STAGE original-scene-transition ArizonaTargets\n"
        loaded = "NATIVE_GAMEPLAY_STAGE scene-loaded ArizonaTargets roots=1\n"
        issues, observed, remaining = runner.classify(
            transition + warning + loaded, runner.ISSUE, baseline
        )
        self.assertFalse(issues)
        self.assertEqual(observed, [warning.strip()])
        self.assertFalse(remaining)
        for text in (
            warning,
            transition + warning + warning,
            transition + loaded + warning,
            transition.replace("ArizonaTargets", "OtherScene") + warning,
            transition + warning.replace("Fencepole 2", "New missing script"),
        ):
            self.assertTrue(runner.classify(text, runner.ISSUE, baseline)[0])

    def test_original_warning_allowance_does_not_consume_exception(self):
        baseline = {"scene": "ArizonaTargets", "names": {"Fencepole 2": 1}}
        text = "NATIVE_GAMEPLAY_STAGE original-scene-transition ArizonaTargets\nException: Fencepole 2 missing script\n"
        self.assertTrue(runner.classify(text, runner.ISSUE, baseline)[0])

    def test_original_warning_count_is_required_at_success(self):
        baseline = {"scene": "ArizonaTargets", "names": {"Fencepole 2": 1}}
        text = (
            DIAGNOSTICS
            + STEREO
            + "NATIVE_SCENE_PROBE_PASS scene=ArizonaTargets observationSeconds=5 stereoFrames=7\n"
        )
        self.assertTrue(
            runner.inspect_log(
                text,
                RUN,
                False,
                False,
                scene_smoke=True,
                scene="ArizonaTargets",
                original_nulls=baseline,
            )["failures"]
        )

    def test_real_subprocess_slow_log_startup(self):
        with tempfile.TemporaryDirectory() as d:
            log = Path(d) / "player.log"
            text = DIAGNOSTICS + STEREO + PASS
            code = (
                "import time,pathlib;time.sleep(.25);pathlib.Path("
                + repr(str(log))
                + ").write_text("
                + repr(text)
                + ");time.sleep(.08)"
            )
            proc = subprocess.Popen([sys.executable, "-c", code])
            started = time.monotonic()
            try:
                r = runner.supervise(
                    proc,
                    lambda: log.read_text() if log.exists() else "",
                    RUN,
                    2,
                    0.2,
                    started=started,
                    poll_interval=0.01,
                )
                self.assertEqual(r["stopReason"], "process-exited")
                self.assertGreaterEqual(r["startupSeconds"], 0.25)
                self.assertLess(r["runtimeSeconds"], 0.2)
            finally:
                if proc.poll() is None:
                    proc.kill()
                proc.wait(timeout=2)


if __name__ == "__main__":
    unittest.main()
