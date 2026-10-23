#!/usr/bin/env python3
"""Pure evidence/parser tests; these never launch Unity or a native player."""

from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
import optih3vr_matrix as matrix


class MatrixTests(unittest.TestCase):
    def setUp(self):
        self.case = dict(id="scene-IndoorRange_Updated", scene="IndoorRange_Updated", group="scene")
        self.result = dict(
            runId="fixture-run",
            verificationPassed=True,
            cleanup=dict(verifiedExited=True),
            timedOut=False,
            requestedScene="IndoorRange_Updated",
            failures=[],
        )
        self.config = dict(runId="fixture-run", pid=42, enabled=True, available=True)
        self.window = dict(
            runId="fixture-run",
            pid=42,
            scene="IndoorRange_Updated",
            phase="steady",
            warmupSeconds=2,
            durationSeconds=2,
            frames=100,
            stereoFrames=80,
            frameMs=[20] * 100,
            settingsFingerprint="settings-a",
            workload="stationary-original-scene",
            source="Unity frame delta",
        )

    def log(self, window=None, config=None, before_handoff=False):
        sample = "NATIVEH3VR_PERF_WINDOW " + json.dumps(self.window if window is None else window)
        handoff = (
            "NATIVE_GAMEPLAY_STAGE native-camera-handoff frames=10->20 camera=Camera pre=2 post=2"
        )
        return (
            "\n".join(
                [
                    "NATIVEH3VR_RUN_ID fixture-run pid=42",
                    "NATIVEH3VR_OPTI_CONFIG "
                    + json.dumps(self.config if config is None else config),
                ]
                + ([sample, handoff] if before_handoff else [handoff, sample])
            )
            + "\n"
        )

    def parse(self, **kw):
        return matrix.telemetry(self.log(**kw), self.result, self.case, True)

    def test_actual_steady_window_is_valid(self):
        evidence = self.parse()
        self.assertTrue(evidence["variantVerified"])
        self.assertFalse(evidence["errors"])
        self.assertEqual(evidence["windows"][0]["fps"], 50)
        self.assertEqual(evidence["windows"][0]["p95Ms"], 20)
        self.assertEqual(evidence["windows"][0]["nativeStereoFps"], 40)

    def test_windows_reject_other_process_scene_loading_and_bad_samples(self):
        for field, value in [
            ("runId", "old-run"),
            ("pid", 7),
            ("pid", True),
            ("scene", "MainMenu3"),
            ("phase", "loading"),
            ("warmupSeconds", 0),
            ("durationSeconds", 1),
            ("frames", 99),
            ("stereoFrames", 0),
            ("stereoFrames", 1),
            ("frameMs", [20] * 29),
            ("frameMs", [float("nan")] * 100),
            ("frameMs", [0] * 100),
            ("settingsFingerprint", ""),
            ("workload", ""),
        ]:
            with self.subTest(field=field, value=str(value)[:50]):
                row = deepcopy(self.window)
                row[field] = value
                result = self.parse(window=row)
                self.assertFalse(result["windows"])
                self.assertTrue(result["errors"])

    def test_frames_before_handoff_rejected(self):
        self.assertFalse(self.parse(before_handoff=True)["windows"])

    def test_interval_sum_must_match_duration(self):
        window = dict(self.window, durationSeconds=10)
        self.assertFalse(self.parse(window=window)["windows"])

    def test_toggle_is_process_bound_and_boolean(self):
        for change in [
            dict(available=False),
            dict(enabled=False),
            dict(enabled=1),
            dict(pid=7),
            dict(runId="old"),
        ]:
            with self.subTest(change=change):
                self.assertFalse(self.parse(config=dict(self.config, **change))["variantVerified"])

    def test_duplicate_config_or_identity_rejected(self):
        text = self.log() + "NATIVEH3VR_OPTI_CONFIG " + json.dumps(self.config) + "\n"
        self.assertFalse(matrix.telemetry(text, self.result, self.case, True)["variantVerified"])
        text = self.log() + "NATIVEH3VR_RUN_ID stale pid=8\n"
        self.assertFalse(matrix.telemetry(text, self.result, self.case, True)["variantVerified"])

    def test_missing_metrics_never_infer_fps_from_process_duration(self):
        text = "\n".join(self.log().splitlines()[:-1])
        result = matrix.telemetry(text, dict(self.result, duration=2), self.case, True)
        self.assertFalse(result["windows"])
        self.assertTrue(result["errors"])

    def test_raw_log_hash_and_scene_are_required(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "raw.log"
            path.write_text(self.log())
            result = dict(self.result, logSha256=matrix.digest(path))
            self.assertTrue(matrix.assess(result, path, 0, self.case, True)["acceptancePassed"])
            path.write_text(self.log() + "post-run exception\n")
            self.assertFalse(matrix.assess(result, path, 0, self.case, True)["acceptancePassed"])
            result.update(logSha256=matrix.digest(path), requestedScene="Other")
            self.assertFalse(matrix.assess(result, path, 0, self.case, True)["acceptancePassed"])

    def test_benchmark_rejects_profiler_and_fixture_workloads(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "raw.log"
            for marker in (
                "NEWENGINEH3VR_PROFILE_READY",
                "NATIVE_OPTIH3VR_TEST_PASS",
                "NATIVE_GRAPHICS_TEXTURE_TEST_PASS",
                "NEWENGINEH3VR_SCOPE_PROBE_START",
            ):
                path.write_text(self.log() + marker + "\n")
                result = dict(
                    self.result,
                    logSha256=matrix.digest(path),
                    capturePolicy="disabled-for-performance",
                    captures=[],
                )
                evidence = matrix.assess(result, path, 0, self.case, True, timing_requested=True)
                self.assertTrue(evidence["acceptancePassed"])
                self.assertIn(
                    "Performance run contains a separate profiler or diagnostic workload",
                    evidence["telemetry"]["errors"],
                )

    def records(self):
        rows = []
        for repeat in range(3):
            for variant in ("baseline", "optimized"):
                window = dict(
                    self.window, meanMs=20 if variant == "baseline" else 18, nativeStereoFps=40
                )
                rows.append(
                    dict(
                        caseId=self.case["id"],
                        repeat=repeat + 1,
                        variant=variant,
                        timingRequested=True,
                        capturePolicy="disabled-for-performance",
                        acceptancePassed=True,
                        telemetry=dict(variantVerified=True, errors=[], windows=[window]),
                    )
                )
        return rows

    def test_comparison_needs_three_matching_clean_repeats(self):
        rows = self.records()
        report = matrix.compare(rows)[0]
        self.assertTrue(report["comparable"])
        self.assertAlmostEqual(report["frameTimeChangePercent"], -10)
        self.assertEqual(report["nativeStereoFpsChangePercent"], 0)
        self.assertFalse(matrix.compare(rows[:-1])[0]["comparable"])
        for key in ("settingsFingerprint", "workload", "source", "scene"):
            changed = deepcopy(rows)
            changed[0]["telemetry"]["windows"][0][key] = "different"
            self.assertFalse(matrix.compare(changed)[0]["comparable"])
        changed = deepcopy(rows)
        changed[0]["acceptancePassed"] = False
        self.assertFalse(matrix.compare(changed)[0]["comparable"])
        changed = deepcopy(rows)
        changed[0]["telemetry"]["errors"] = ["extra malformed telemetry"]
        self.assertFalse(matrix.compare(changed)[0]["comparable"])

    def test_timing_uses_no_capture_mode_only_for_scene_observations(self):
        timing = matrix.run_options(self.case, True, 5, 15)
        self.assertEqual(timing["arguments"], ["--no-captures"])
        self.assertEqual(timing["capturePolicy"], "disabled-for-performance")
        self.assertEqual(timing["environment"]["NATIVEH3VR_PERF_ENABLED"], "1")
        self.assertEqual(timing["environment"]["NATIVEH3VR_PERF_CAPTURE_SECONDS"], "15")
        for group in ("scene", "weapon", "systems", "vault"):
            with self.subTest(group=group):
                options = matrix.run_options(dict(self.case, group=group), group != "scene", 5, 15)
                self.assertFalse(options["timingRequested"])
                self.assertEqual(options["arguments"], [])
                self.assertEqual(options["capturePolicy"], "required")
                self.assertEqual(options["environment"]["NATIVEH3VR_PERF_ENABLED"], "0")
                self.assertIsNone(options["environment"]["NATIVEH3VR_PERF_CAPTURE_SECONDS"])

    def test_performance_rejects_missing_capture_policy_and_unexpected_readbacks(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "raw.log"
            path.write_text(self.log())
            result = dict(
                self.result,
                logSha256=matrix.digest(path),
                captures=[],
                capturePolicy="disabled-for-performance",
            )
            evidence = matrix.assess(result, path, 0, self.case, True, timing_requested=True)
            self.assertTrue(evidence["acceptancePassed"])
            self.assertFalse(evidence["telemetry"]["errors"])
            for change in [dict(capturePolicy="required"), dict(captures=[dict(path="image.png")])]:
                evidence = matrix.assess(
                    dict(result, **change), path, 0, self.case, True, timing_requested=True
                )
                self.assertTrue(evidence["telemetry"]["errors"])
            (path.parent / "surprise.png").write_bytes(b"not even a complete PNG")
            evidence = matrix.assess(result, path, 0, self.case, True, timing_requested=True)
            self.assertTrue(evidence["telemetry"]["errors"])

    def test_capture_contaminated_and_acceptance_only_runs_are_not_comparable(self):
        rows = self.records()
        rows[0]["capturePolicy"] = "required"
        self.assertFalse(matrix.compare(rows)[0]["comparable"])
        for row in rows:
            row["timingRequested"] = False
        self.assertEqual(matrix.compare(rows), [])

    def test_full_plan_has_46_scenes_five_classes_systems_and_vault(self):
        # Test scheduling with synthetic identities, independent of recovered game data.
        with tempfile.TemporaryDirectory() as tmp:
            baseline = Path(tmp) / "baseline.json"
            baseline.write_text(
                json.dumps(
                    {
                        "passed": True,
                        "scenes": [{"path": f"Assets/FixtureScene{i}.unity"} for i in range(46)],
                    }
                )
            )
            scenes = matrix.load_scenes(baseline)
        cases = matrix.build_cases(scenes, ["scene", "weapon", "systems", "vault"])
        self.assertEqual(len(cases), 53)
        self.assertEqual(len({row["id"] for row in cases}), 53)
        jobs = matrix.schedule(cases, 2)
        self.assertEqual(len(jobs), 212)
        self.assertEqual([j["variant"] for j in jobs[:2]], ["baseline", "optimized"])
        self.assertEqual([j["variant"] for j in jobs[106:108]], ["optimized", "baseline"])
        self.assertEqual(len(matrix.schedule(cases, 1, ("optimized",))), 53)

    def test_invalid_catalog_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "baseline.json"
            path.write_text(json.dumps(dict(passed=True, scenes=[dict(path="X.unity")] * 46)))
            with self.assertRaises(ValueError):
                matrix.load_scenes(path)

    def test_lock_prevents_concurrent_matrix(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "test.lock"
            with matrix.exclusive_lock(path):
                with self.assertRaises(RuntimeError):
                    with matrix.exclusive_lock(path):
                        pass


if __name__ == "__main__":
    unittest.main()
