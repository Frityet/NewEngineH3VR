#!/usr/bin/env python3
"""Synthetic saved-file fixtures only; no app, Unity or live capture requests."""

from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from PIL import Image, ImageDraw
import verify_scope_capture as verify


def save_json(path, value):
    path.write_text(json.dumps(value))


def quadrants(path, size):
    image = Image.new("RGB", size, "white")
    draw = ImageDraw.Draw(image)
    w, h = size
    for xy, color in [
        ((0, 0, w // 2 - 1, h // 2 - 1), "red"),
        ((w // 2, 0, w - 1, h // 2 - 1), "lime"),
        ((0, h // 2, w // 2 - 1, h - 1), "blue"),
    ]:
        draw.rectangle(xy, fill=color)
    image.save(path)
    n = w * h
    return dict(
        path=path.name,
        available=True,
        unavailable="",
        width=w,
        height=h,
        pixels=n,
        nonBlackPixels=n,
        redPixels=n // 4,
        greenPixels=n // 4,
        bluePixels=n // 4,
        minimum=1.0,
        maximum=1.0,
        mean=1.0,
        contrast=0.0,
    )


class ScopeCaptureChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.optics = self.root / "optics"
        self.optics.mkdir()
        self.started = time.time() - 1
        self.finished = self.started + 1.5
        iso = lambda t: datetime.fromtimestamp(t, timezone.utc).isoformat()
        self.report = dict(
            schema="newengineh3vr-scope-probe-1",
            startedUtc=iso(self.started),
            finishedUtc=iso(self.finished),
            scene="IndoorRange_Updated",
            mode="owned-original-prefab",
            item="EC79A2",
            itemGuid=verify.GUIDS["EC79A2"],
            scopeName="EC79_PIPScope",
            error="",
            selectedEye="left native eye (fixture)",
            synthetic=True,
            finalEyeCopied=True,
            sourceMainBindingObserved=True,
            sourceCallbacksObserved=True,
            scopeCameraRendered=True,
            cleanupComplete=True,
            pid=1234,
            sourceRenders=14,
            scopeRenders=7,
            ownMeshExperimentRequested=False,
            ownMeshExperimentApplied=False,
            ownMeshExperimentRestored=True,
            ownMeshRendererCount=0,
            ownMeshScopePasses=0,
            ownMeshRestoreError="",
            requestedReliefOffsetMM=1,
            observations=[
                dict(
                    phase="source.onPreRender",
                    targetCreated=True,
                    targetWidth=64,
                    targetHeight=64,
                    instanceMatches=True,
                    sourceId=5,
                    mainId=5,
                    shaderSupported=True,
                    mainTextureMatchesScopeTarget=True,
                )
            ],
        )
        for key, filename in verify.IMAGE_NAMES.items():
            self.report[key] = quadrants(
                self.optics / filename, (32, 32) if key == "lensRegion" else (64, 64)
            )
        self.run = dict(
            verificationPassed=True,
            sceneObservationPassed=True,
            scenePassMarker=True,
            failures=[],
            issues=[],
            timedOut=False,
            launchServicesExitCode=0,
            cleanup=dict(
                verifiedExited=True, errors=[], remainingPids=[], termPids=[], killPids=[]
            ),
            testMode="synthetic isolated OpenXR",
            architecture="arm64",
            capturePolicy="required",
            runId="fixture-run",
            requestedScene="IndoorRange_Updated",
            duration=10,
            captures=[],
        )
        for filename, size in [
            ("native-gameplay.png", (64, 64)),
            ("native-game-stereo.png", (128, 64)),
        ]:
            path = self.root / filename
            quadrants(path, size)
            row = dict(
                path=str(path),
                width=size[0],
                height=size[1],
                bytes=path.stat().st_size,
                sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
            )
            if "stereo" in filename:
                metadata_path = path.with_suffix(".png.json")
                save_json(
                    metadata_path,
                    dict(
                        process_id=1234,
                        native_hmd_submitted_frames=0,
                        width=128,
                        height=64,
                        composed_frames=20,
                    ),
                )
                row["compositor"] = dict(
                    metadataPath=str(metadata_path),
                    metadataSha256=hashlib.sha256(metadata_path.read_bytes()).hexdigest(),
                    processId=1234,
                    nativeHmdSubmittedFrames=0,
                    composedFrames=20,
                    minimumSceneFrame=2,
                )
            self.run["captures"].append(row)
        self.log = self.root / "native-gameplay.log"
        self.report_path = self.optics / "scope-probe.json"
        self.run_path = self.root / "native-gameplay-run.json"
        self.commit()

    def commit(self):
        log = (
            "NATIVEH3VR_RUN_ID fixture-run pid=1234\n"
            "NEWENGINEH3VR_SCOPE_PROBE_START mode=" + self.report["mode"] + " scope=EC79_PIPScope\n"
            "NEWENGINEH3VR_SCOPE_PROBE_STOP status=complete report=" + str(self.optics) + "\n"
            "NATIVE_SCENE_PROBE_PASS scene=IndoorRange_Updated observationSeconds=5 stereoFrames=20\n"
        )
        self.log.write_text(log)
        self.run["log"] = str(self.log)
        self.run["logSha256"] = hashlib.sha256(log.encode()).hexdigest()
        save_json(self.report_path, self.report)
        save_json(self.run_path, self.run)
        os.utime(self.report_path, (self.finished, self.finished))
        os.utime(self.log, (self.finished + 1, self.finished + 1))

    def check(self, **kwargs):
        return verify.verify_scope_capture(self.root, **kwargs)

    def assert_failed(self, text=None, **kwargs):
        result = self.check(**kwargs)
        self.assertFalse(result["passed"], result)
        if text:
            self.assertIn(text, " ".join(result["failures"]))

    def test_saved_rgb_sanity_pass_binds_all_receipts_and_default_is_read_only(self):
        before = {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        result = self.check(expected_item="EC79A2", eye="left")
        self.assertTrue(result["passed"], result["failures"])
        self.assertEqual(result["evidence"]["scopeImages"]["scopeImage"]["redPixels"], 1024)
        self.assertEqual(
            result["evidence"]["run"]["sha256"],
            hashlib.sha256(self.run_path.read_bytes()).hexdigest(),
        )
        self.assertEqual(before, {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()})

    def test_run_failure_cleanup_and_hidden_mesh_experiment_are_rejected(self):
        original = deepcopy(self.run)
        for change in [
            dict(verificationPassed=False),
            dict(failures=["original failure"]),
            dict(timedOut=True),
            dict(launchServicesExitCode=1),
            dict(cleanup=dict(verifiedExited=False)),
        ]:
            self.run = dict(original, **change)
            self.commit()
            self.assert_failed()
        self.run = original
        for key in ["ownMeshExperimentRequested", "ownMeshExperimentApplied"]:
            self.report[key] = True
            self.commit()
            self.assert_failed("Own-mesh")
            self.report[key] = False

    def test_wrong_item_eye_and_process_identity_are_rejected(self):
        self.assert_failed("scope item", expected_item="ClassicScope")
        self.assert_failed("selected eye", eye="right")
        self.report["pid"] = 99
        self.commit()
        self.assert_failed("exact run log identity")

    def test_stale_image_missing_image_and_path_escape_are_rejected(self):
        path = self.optics / "scope-rt.png"
        original = path.read_bytes()
        os.utime(path, (self.started - 20, self.started - 20))
        self.assert_failed("Stale scope PNG")
        path.unlink()
        self.assert_failed("Missing evidence")
        path.write_bytes(original)
        self.report["scopeImage"]["path"] = "../native-gameplay.png"
        self.commit()
        self.assert_failed("renamed")

    def test_wrong_image_dimensions_counts_and_non_png_are_rejected(self):
        self.report["scopeImage"]["width"] = 60
        self.commit()
        self.assert_failed("scopeImage.width")
        self.report["scopeImage"]["width"] = 64
        self.report["scopeImage"]["redPixels"] += 1
        self.commit()
        self.assert_failed("scopeImage.redPixels")
        (self.optics / "scope-rt.png").write_text("not a PNG")
        self.assert_failed("not a PNG")

    def test_black_image_is_rejected_even_when_report_counts_agree(self):
        path = self.optics / "scope-rt.png"
        Image.new("RGB", (64, 64), "black").save(path)
        self.report["scopeImage"].update(
            nonBlackPixels=0,
            redPixels=0,
            greenPixels=0,
            bluePixels=0,
            minimum=0,
            maximum=0,
            mean=0,
            contrast=0,
        )
        self.commit()
        self.assert_failed("Insufficient RGB")

    def test_exact_relief_zero_never_waives_black_image_failure(self):
        self.report["requestedReliefOffsetMM"] = 0
        path = self.optics / "final-lens-region.png"
        Image.new("RGB", (32, 32), "black").save(path)
        self.report["lensRegion"].update(
            nonBlackPixels=0,
            redPixels=0,
            greenPixels=0,
            bluePixels=0,
            minimum=0,
            maximum=0,
            mean=0,
            contrast=0,
        )
        self.commit()
        self.assert_failed("Insufficient RGB")

    def test_native_capture_hash_and_optic_target_dimensions_are_bound(self):
        self.run["captures"][0]["sha256"] = "0" * 64
        self.commit()
        self.assert_failed("capture receipt")
        self.run["captures"][0]["sha256"] = hashlib.sha256(
            (self.root / "native-gameplay.png").read_bytes()
        ).hexdigest()
        self.report["observations"][0]["targetWidth"] = 32
        self.commit()
        self.assert_failed("observed original scope target")

    def test_stale_scope_report_and_changed_log_are_rejected(self):
        self.report["startedUtc"] = "2020-01-01T00:00:00Z"
        self.report["finishedUtc"] = "2020-01-01T00:00:01Z"
        self.commit()
        self.assert_failed("stale/outside")
        self.log.write_text(self.log.read_text() + "modified\n")
        self.assert_failed("Run log hash")

    def mounted(self):
        self.report["mode"] = "api-mounted-synthetic-original-prefabs"
        self.report["mountedFixture"] = dict(
            requested=True,
            apiAttached=True,
            verified=True,
            detachedForCleanup=True,
            deregisteredForCleanup=True,
            ownedGunDestroyed=True,
            error="",
            cleanupError="",
            mountItem="C7A2",
            opticItem="EC79A2",
            mountItemGuid=verify.GUIDS["C7A2"],
            opticItemGuid=verify.GUIDS["EC79A2"],
            observations=[
                dict(
                    phase="after-attach",
                    mountName="_RailMount_Top",
                    mountType="Picatinny",
                    curMountMatches=True,
                    registeredOnMount=True,
                    registeredOnGun=True,
                    rootObjectMatchesGun=True,
                    controllerMatchesOptic=True,
                    gunHeld=False,
                    attachmentHeld=False,
                    gunHandAssigned=False,
                    attachmentHandAssigned=False,
                )
            ],
        )
        self.commit()

    def test_mounted_requires_original_api_attachment_registrations_and_cleanup(self):
        self.assert_failed("Mounted C7A2 fixture report", require_mounted=True)
        self.mounted()
        self.assertTrue(self.check(require_mounted=True)["passed"])
        mounted = deepcopy(self.report["mountedFixture"])
        for change in [
            dict(verified=False),
            dict(apiAttached=False),
            dict(detachedForCleanup=False),
            dict(deregisteredForCleanup=False),
            dict(ownedGunDestroyed=False),
            dict(mountItem="Other"),
            dict(cleanupError="failure"),
        ]:
            self.report["mountedFixture"] = dict(mounted, **change)
            self.commit()
            self.assert_failed(require_mounted=True)

    def test_experiment_missing_flags_and_symlink_escape_are_rejected(self):
        value = self.report.pop("ownMeshExperimentApplied")
        self.commit()
        self.assert_failed("unknown")
        self.report["ownMeshExperimentApplied"] = value
        self.commit()
        with tempfile.TemporaryDirectory() as outside:
            other = Path(outside) / "image.png"
            other.write_bytes((self.optics / "scope-rt.png").read_bytes())
            (self.optics / "scope-rt.png").unlink()
            (self.optics / "scope-rt.png").symlink_to(other)
            self.assert_failed("escapes case")

    def test_output_is_explicit_contained_and_never_overwrites_history(self):
        result = self.check()
        path = self.root / "verification.json"
        verify.write_output(self.root, path, result)
        before = path.read_bytes()
        with self.assertRaises(ValueError):
            verify.write_output(self.root, path, dict(passed=False))
        self.assertEqual(path.read_bytes(), before)
        with self.assertRaises(ValueError):
            verify.write_output(self.root, self.root.parent / "escaped.json", result)


if __name__ == "__main__":
    unittest.main()
