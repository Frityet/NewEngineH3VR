#!/usr/bin/env python3
"""Pure fixture/file tests. No live process lookup or profiler request is used."""

from copy import deepcopy
from datetime import datetime, timezone, timedelta
import json
from pathlib import Path
import plistlib
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch
import profile_live_session as live


class LiveSchedulerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        app = self.root / "New Engine.app"
        (app / "Contents/MacOS").mkdir(parents=True)
        with (app / "Contents/Info.plist").open("wb") as stream:
            plistlib.dump(dict(CFBundleExecutable="NewEngineH3VR"), stream)
        (app / "Contents/MacOS/NewEngineH3VRPlayer").write_bytes(b"fixture engine, never executed")
        directory = self.root / "session"
        directory.mkdir()
        self.session = live.session_from_json(
            dict(app=str(app), directory=str(directory), pid=1234, runId="game-123")
        )
        self.run_dir = self.session.profile_root / "live-fixture"
        self.run_dir.mkdir(parents=True)
        self.now = datetime(2026, 10, 23, 18, 0, 0, tzinfo=timezone.utc)
        self.process = dict(startUtc="2026-10-23T17:00:00+00:00")
        self.status = dict(
            schema=live.SCHEMA,
            pid=1234,
            gameRunId="game-123",
            runId="profiler-123",
            processStartUtc="2026-10-23T17:00:00.1234567Z",
            runDirectory=str(self.run_dir),
            startRequestPath=str(self.run_dir / "start.json"),
            stopRequestPath=str(self.run_dir / "stop.json"),
            statusUpdatedUtc=self.now.isoformat(),
            statusUnityFrame=10,
            activeScene="MF2_MainScene",
            activeSceneHandle=-20,
            activeSceneLoaded=True,
            capturing=False,
            state="ready",
            capturesStarted=0,
            maximumCaptures=8,
        )

    def test_session_resolves_executable_from_bundle_and_rejects_root_escape(self):
        self.assertEqual(self.session.executable.name, "NewEngineH3VRPlayer")
        with self.assertRaises(ValueError):
            live.session_from_json(
                dict(
                    app=str(self.session.app),
                    directory=str(self.session.directory),
                    pid=1234,
                    runId="game-123",
                ),
                self.root,
            )

    def test_launcher_process_is_not_the_bound_unity_engine(self):
        row = f"1234 Fri Oct 23 18:00:00 2026 {self.session.app}/Contents/MacOS/NewEngineH3VR\n"
        with self.assertRaises(live.IdentityError):
            live.parse_ps(row, 1234, self.session.executable)
        value = dict(
            app=str(self.session.app),
            directory=str(self.session.directory),
            pid=1234,
            runId="game-123",
        )
        (self.session.app / "Contents/MacOS/NativeH3VRPlayer").write_bytes(b"ambiguous fixture")
        with self.assertRaises(ValueError):
            live.session_from_json(value)
        (self.session.app / "Contents/MacOS/NativeH3VRPlayer").unlink()
        self.session.executable.unlink()
        with self.assertRaises(ValueError):
            live.session_from_json(value)

    def test_ps_matches_exact_executable_and_stable_start(self):
        row = f"1234 Fri Oct 23 18:00:00 2026 {self.session.executable}\n"
        result = live.parse_ps(row, 1234, self.session.executable)
        self.assertEqual(result["startText"], "Fri Oct 23 18:00:00 2026")
        self.assertIsNone(live.parse_ps("", 1234, self.session.executable))
        with self.assertRaises(live.IdentityError):
            live.parse_ps(
                row.replace("/NewEngineH3VR", "/AnotherPlayer"), 1234, self.session.executable
            )
        with self.assertRaises(live.IdentityError):
            live.parse_ps(row.replace("1234 ", "1235 ", 1), 1234, self.session.executable)

    def test_freshness_uses_explicit_timestamp_not_file_mtime(self):
        self.assertTrue(live.fresh(self.status, self.now))
        self.assertFalse(
            live.fresh(
                dict(self.status, statusUpdatedUtc=(self.now - timedelta(seconds=6)).isoformat()),
                self.now,
            )
        )
        self.assertFalse(
            live.fresh(
                dict(self.status, statusUpdatedUtc=(self.now + timedelta(seconds=3)).isoformat()),
                self.now,
            )
        )
        self.assertFalse(live.fresh(dict(self.status, statusUpdatedUtc=None), self.now))

    def test_identity_binds_pid_both_run_ids_process_start_and_paths(self):
        self.assertEqual(
            live.validate_status(self.status, self.session, self.process), self.run_dir
        )
        for fields in [
            dict(pid=3),
            dict(gameRunId="other"),
            dict(runId="other"),
            dict(processStartUtc="2026-10-23T16:00:00Z"),
            dict(startRequestPath=str(self.root / "start.json")),
        ]:
            with self.subTest(fields=fields), self.assertRaises(live.IdentityError):
                live.validate_status(
                    dict(self.status, **fields), self.session, self.process, "profiler-123"
                )

    def test_stability_requires_fifteen_seconds_fresh_progress_same_handle(self):
        tracker = live.SceneStability("MF2_MainScene", 15)
        for second in range(16):
            status = dict(
                self.status,
                statusUnityFrame=10 + second,
                statusUpdatedUtc=(self.now + timedelta(seconds=second)).isoformat(),
            )
            ready = tracker.observe(status, second, self.now + timedelta(seconds=second))
            self.assertEqual(ready, second == 15)
        self.assertFalse(
            tracker.observe(
                dict(status, activeSceneHandle=-21), 16, self.now + timedelta(seconds=16)
            )
        )
        self.assertFalse(
            tracker.observe(
                dict(status, activeScene="MainMenu3"), 17, self.now + timedelta(seconds=17)
            )
        )

    def test_duplicate_or_blind_status_interval_cannot_establish_stability(self):
        tracker = live.SceneStability("MF2_MainScene", 15)
        self.assertFalse(tracker.observe(self.status, 0, self.now))
        advanced = dict(
            self.status,
            statusUnityFrame=99,
            statusUpdatedUtc=(self.now + timedelta(seconds=20)).isoformat(),
        )
        self.assertFalse(tracker.observe(advanced, 20, self.now + timedelta(seconds=20)))
        tracker = live.SceneStability("MF2_MainScene", 1)
        self.assertFalse(tracker.observe(self.status, 0, self.now))
        self.assertFalse(tracker.observe(self.status, 1, self.now + timedelta(seconds=1)))

    def test_auto_accepts_range_arena_training_and_playable_starting_areas(self):
        for scene in (
            "IndoorRange_Updated",
            "MF2_MainScene",
            "Cappocolosseum",
            "ObstacleCourseScene1",
            "TakeAndHold_Lobby_2",
            "MeatGrinder_StartingScene",
            "RotWienersStagingScene",
        ):
            with self.subTest(scene=scene):
                self.assertTrue(live.eligible_scene(scene, automatic=True))
        self.assertEqual(len(live.AUTO_GAMEPLAY_SCENES), 45)

    def test_auto_never_selects_menu_boot_or_unknown_loaded_scenes(self):
        tracker = live.SceneStability("auto", 1)
        for scene in ("MainMenu3", "Boot", "LoadingScene", "Startup", "CustomSandbox"):
            with self.subTest(scene=scene):
                status = dict(self.status, activeScene=scene)
                self.assertFalse(tracker.observe(status, 0, self.now))
                self.assertFalse(
                    tracker.observe(
                        dict(status, statusUnityFrame=99), 2, self.now + timedelta(seconds=2)
                    )
                )
                self.assertIsNone(tracker.scene)
        self.assertTrue(live.eligible_scene("CustomSandbox"))
        self.assertFalse(live.eligible_scene("MainMenu3"))

    def test_auto_scene_name_change_resets_stability_even_if_handle_reused(self):
        tracker = live.SceneStability("auto", 2)
        self.assertFalse(tracker.observe(self.status, 0, self.now))
        status = dict(
            self.status,
            statusUnityFrame=20,
            statusUpdatedUtc=(self.now + timedelta(seconds=1)).isoformat(),
        )
        self.assertFalse(tracker.observe(status, 1, self.now + timedelta(seconds=1)))
        status = dict(
            status,
            activeScene="IndoorRange_Updated",
            statusUnityFrame=30,
            statusUpdatedUtc=(self.now + timedelta(seconds=2)).isoformat(),
        )
        self.assertFalse(tracker.observe(status, 2, self.now + timedelta(seconds=2)))
        self.assertEqual(tracker.scene, "IndoorRange_Updated")
        status = dict(
            status,
            statusUnityFrame=40,
            statusUpdatedUtc=(self.now + timedelta(seconds=4)).isoformat(),
        )
        self.assertTrue(tracker.observe(status, 4, self.now + timedelta(seconds=4)))
        self.assertFalse(
            tracker.observe(
                dict(status, activeSceneHandle=-21), 4.1, self.now + timedelta(seconds=4.1)
            )
        )

    def test_auto_loading_or_stale_transition_requires_new_stability(self):
        tracker = live.SceneStability("auto", 1)
        self.assertFalse(tracker.observe(self.status, 0, self.now))
        ready = dict(
            self.status,
            statusUnityFrame=20,
            statusUpdatedUtc=(self.now + timedelta(seconds=1)).isoformat(),
        )
        self.assertTrue(tracker.observe(ready, 1, self.now + timedelta(seconds=1)))
        for status in (
            dict(ready, activeSceneLoaded=False),
            dict(ready, activeScene="MainMenu3"),
            dict(ready, statusUpdatedUtc=(self.now - timedelta(seconds=9)).isoformat()),
        ):
            self.assertFalse(tracker.observe(status, 2, self.now + timedelta(seconds=2)))
            self.assertIsNone(tracker.scene)
            self.assertFalse(tracker.observe(ready, 2.1, self.now + timedelta(seconds=2.1)))

    def test_reader_retries_missing_and_partly_replaced_json(self):
        path = self.root / "status.json"
        self.assertIsNone(live.read_json(path))
        path.write_text('{"state":')
        self.assertIsNone(live.read_json(path))
        live.atomic_json(path, self.status)
        self.assertEqual(live.read_json(path)["runId"], "profiler-123")

    def test_atomic_request_never_overwrites_existing_request(self):
        path = self.run_dir / "fixture-start.json"
        payload = dict(runId="fixture", pid=1, seconds=30, label="not a live request")
        live.publish_start(path, payload)
        before = path.read_bytes()
        self.assertEqual(json.loads(before), payload)
        with self.assertRaises(FileExistsError):
            live.publish_start(path, dict(payload, seconds=1))
        self.assertEqual(path.read_bytes(), before)
        self.assertFalse(list(self.run_dir.glob(".scheduler-request-*")))

    def make_capture(self):
        capture = self.run_dir / "capture-01-fixture"
        capture.mkdir()
        label = "fixture-label-unique"
        (capture / "label.txt").write_text(label + "\n")
        start = dict(
            self.status,
            captureId=capture.name,
            captureDirectory=str(capture),
            capturesStarted=1,
            requestedSeconds=30,
            startedUtc=self.now.isoformat(),
            startScene="MF2_MainScene",
        )
        live.atomic_json(capture / "started.json", start)
        pending = dict(
            label=label,
            seconds=30,
            previousCapturesStarted=0,
            requestedUtc=self.now.isoformat(),
            scene="MF2_MainScene",
            sceneHandle=-20,
        )
        ack = live.acknowledge(start, pending, self.run_dir)
        pending.update(ack)
        raw = capture / "unity.raw"
        raw.write_bytes(b"fixture bytes, not a real profiler capture")
        summary = dict(
            schema=live.SCHEMA,
            pid=1234,
            gameRunId="game-123",
            runId="profiler-123",
            captureId=capture.name,
            captureDirectory=str(capture),
            rawPath=str(raw),
            rawBytes=raw.stat().st_size,
            requestedSeconds=30,
            state="complete",
            capturing=False,
            settingsRestored=True,
            restoreSkippedOwnershipConflict=False,
            sceneChanged=False,
            startScene="MF2_MainScene",
            endScene="MF2_MainScene",
            scenes=[dict(name="MF2_MainScene", handle=-20)],
            sceneSegments=1,
            samples=100,
            startedUtc=self.now.isoformat(),
            finishedUtc=(self.now + timedelta(seconds=30)).isoformat(),
            error="",
            restoreError="",
            captureElapsedSeconds=30.0,
            stopReason="duration",
        )
        return start, pending, summary

    def test_acknowledgment_requires_unique_label_and_new_capture_count(self):
        start, pending, _ = self.make_capture()
        self.assertEqual(pending["captureId"], start["captureId"])
        self.assertIsNone(live.acknowledge(self.status, pending, self.run_dir))
        with self.assertRaises(live.IdentityError):
            live.acknowledge(dict(start, capturesStarted=2), pending, self.run_dir)
        (Path(pending["captureDirectory"]) / "label.txt").write_text("somebody else\n")
        with self.assertRaises(live.IdentityError):
            live.acknowledge(start, pending, self.run_dir)

    def test_acknowledgment_rejects_scene_change_between_request_and_start(self):
        start, pending, _ = self.make_capture()
        live.atomic_json(
            Path(pending["captureDirectory"]) / "started.json",
            dict(start, startScene="IndoorRange_Updated"),
        )
        with self.assertRaises(live.IdentityError):
            live.acknowledge(start, pending, self.run_dir)

    def test_auto_run_binds_request_and_completed_summary_to_actual_range(self):
        # Complete fake capture files in a temporary fixture directory. No ps,
        # live controller, game state, profiler, or actual start request is used.
        status = dict(
            self.status,
            activeScene="IndoorRange_Updated",
            developmentBuild=True,
            statusUpdatedUtc=datetime.now(timezone.utc).isoformat(),
        )
        ready = self.session.profile_root / "ready-1234-fixture.json"
        live.atomic_json(ready, status)
        requests = []
        clock = [0.0]

        def monotonic():
            clock[0] += 0.25
            return clock[0]

        def tick(_):
            status["statusUnityFrame"] += 1
            status["statusUpdatedUtc"] = datetime.now(timezone.utc).isoformat()
            live.atomic_json(ready, status)

        def publish(path, payload):
            requests.append(payload)
            self.assertEqual(payload["scene"], "IndoorRange_Updated")
            self.assertEqual(payload["sceneHandle"], -20)
            capture = self.run_dir / "capture-auto-fixture"
            capture.mkdir()
            started = dict(
                status,
                captureId=capture.name,
                captureDirectory=str(capture),
                requestedSeconds=30,
                capturesStarted=1,
                startedUtc=live.utc_now(),
                startScene=payload["scene"],
            )
            (capture / "label.txt").write_text(payload["label"] + "\n")
            live.atomic_json(capture / "started.json", started)
            raw = capture / "unity.raw"
            raw.write_bytes(b"fixture profile")
            summary = dict(
                started,
                rawPath=str(raw),
                rawBytes=raw.stat().st_size,
                state="complete",
                capturing=False,
                finishedUtc=(
                    live.parse_utc(started["startedUtc"]) + timedelta(seconds=30)
                ).isoformat(),
                endScene=payload["scene"],
                scenes=[dict(name=payload["scene"], handle=-20)],
                sceneSegments=1,
                samples=100,
                settingsRestored=True,
                restoreSkippedOwnershipConflict=False,
                sceneChanged=False,
                error="",
                restoreError="",
                captureElapsedSeconds=30,
                stopReason="duration",
            )
            live.atomic_json(capture / "summary.json", summary)
            status.update(started)
            live.atomic_json(ready, status)
            return "fixture-request-hash"

        args = SimpleNamespace(
            session=self.root / "session.json",
            scene="auto",
            captures=1,
            seconds=30,
            stable_seconds=1,
            gap_seconds=1,
            overall_seconds=200,
            status_max_age=5,
        )
        receipt = self.session.directory / "auto-receipt.json"
        with (
            patch.object(live, "process_identity", return_value=self.process),
            patch.object(live, "publish_start", side_effect=publish),
            patch.object(live.time, "monotonic", side_effect=monotonic),
            patch.object(live.time, "sleep", side_effect=tick),
        ):
            self.assertEqual(live.run(args, self.session, receipt, "fixture-session-sha"), 0)
        result = json.loads(receipt.read_text())
        self.assertEqual(len(requests), 1)
        self.assertEqual(result["captures"][0]["scene"], "IndoorRange_Updated")
        self.assertEqual(result["captures"][0]["sceneHandle"], -20)
        self.assertEqual(result["validCaptures"], 1)

    def test_auto_unknown_loaded_scene_logs_explicit_selection_hint_without_request(self):
        status = dict(
            self.status,
            activeScene="CustomSandbox",
            developmentBuild=True,
            statusUpdatedUtc=datetime.now(timezone.utc).isoformat(),
        )
        ready = self.session.profile_root / "ready-1234-fixture.json"
        live.atomic_json(ready, status)
        clock = [0.0]

        def monotonic():
            clock[0] += 0.25
            return clock[0]

        args = SimpleNamespace(
            session=self.root / "session.json",
            scene="auto",
            captures=1,
            seconds=30,
            stable_seconds=1,
            gap_seconds=1,
            overall_seconds=2,
            status_max_age=5,
        )
        receipt = self.session.directory / "unknown-scene-receipt.json"
        with (
            patch.object(live, "process_identity", return_value=self.process),
            patch.object(live, "publish_start") as publish,
            patch.object(live.time, "monotonic", side_effect=monotonic),
            patch.object(live.time, "sleep"),
        ):
            self.assertEqual(live.run(args, self.session, receipt, "fixture-session-sha"), 1)
        publish.assert_not_called()
        result = json.loads(receipt.read_text())
        rejection = next(
            row
            for row in result["events"]
            if row["state"] == "auto-rejected-loaded-scene-CustomSandbox"
        )
        self.assertIn("--scene CustomSandbox", rejection["action"])
        self.assertEqual(result["requests"], [])

    def test_completed_capture_validates_restoration_ids_scene_raw_and_timestamps(self):
        _, pending, summary = self.make_capture()
        outcome = live.validate_summary(
            summary, pending, self.session, "profiler-123", "MF2_MainScene"
        )
        self.assertTrue(outcome["valid"])
        self.assertTrue(outcome["completedAsRequested"])
        bad = [
            dict(pid=2),
            dict(runId="old"),
            dict(gameRunId="other"),
            dict(captureId="other"),
            dict(settingsRestored=False),
            dict(restoreSkippedOwnershipConflict=True),
            dict(error="error"),
            dict(restoreError="restore failed"),
            dict(sceneChanged=True),
            dict(startScene="MainMenu3"),
            dict(rawBytes=0),
            dict(samples=0),
            dict(scenes=[dict(name="MF2_MainScene", handle=99)]),
            dict(captureElapsedSeconds=float("nan")),
            dict(startedUtc="2020-01-01T00:00:00Z"),
            dict(stopReason="profiler-state-changed-externally"),
        ]
        for fields in bad:
            with self.subTest(fields=fields):
                self.assertFalse(
                    live.validate_summary(
                        dict(summary, **fields),
                        pending,
                        self.session,
                        "profiler-123",
                        "MF2_MainScene",
                    )["valid"]
                )

    def test_bounded_partial_capture_is_preserved_but_not_thirty_second_success(self):
        _, pending, summary = self.make_capture()
        outcome = live.validate_summary(
            dict(summary, stopReason="binary-disk-threshold", captureElapsedSeconds=12),
            pending,
            self.session,
            "profiler-123",
            "MF2_MainScene",
        )
        self.assertTrue(outcome["valid"])
        self.assertFalse(outcome["completedAsRequested"])


if __name__ == "__main__":
    unittest.main()
