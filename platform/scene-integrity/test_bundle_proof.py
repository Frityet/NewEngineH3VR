"""Acceptance-gate regression tests; only temporary files are created/moved."""

import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "script"))
from verify_native_bundles import (
    CONTENT_MARKER,
    LEGACY_MARKER,
    COMPILER_RECEIPT,
    COMPILER_LOG,
    POLICY_PATH,
    digest,
    verify,
)
from stage_native_bundles import stage


class ProofGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.project = self.root / "project"
        self.source = self.root / "build/StreamingAssets"
        self.target = self.project / "Assets/StreamingAssets"
        self.source.mkdir(parents=True)
        self.project.mkdir()
        self.plan_path = self.root / "plan.json"
        self.plan = {
            "project": str(self.project),
            "issues": [],
            "bundles": [
                {
                    "name": "test",
                    "dependencies": [],
                    "assets": [{"address": "asset", "asset": "Assets/Test.asset"}],
                }
            ],
        }
        self.plan_path.write_text(json.dumps(self.plan))
        (self.project / "Assets").mkdir()
        (self.project / "Assets/Game.dll").write_bytes(b"managed")
        (self.project / "Assets/Test.asset").write_bytes(b"original source")
        outputs = []
        for name in ("test", "test.manifest", "StreamingAssets", "StreamingAssets.manifest"):
            path = self.source / name
            path.write_bytes(name.encode())
            outputs.append(self.record(path, name))
        self.summary = {
            "platform": "StandaloneOSX",
            "graphics": "Metal",
            "bundles": 1,
            "addresses": 1,
            "missing": 0,
        }
        self.proof = dict(
            self.summary,
            schemaVersion=2,
            verified=True,
            mode="existing-output",
            unityVersion="2021.3.45f2",
            planSha256=digest(self.plan_path),
            outputs=outputs,
            bundleInventory=[{"name": "test", "addresses": ["asset"], "dependencies": []}],
            managedInputs=[self.record(self.project / "Assets/Game.dll", "Assets/Game.dll")],
            sourceDependencies=[{"path": "Assets/Test.asset", "hash": "0" * 32}],
            sourceFiles=[self.record(self.project / "Assets/Test.asset", "Assets/Test.asset")],
        )
        (self.source / COMPILER_LOG).write_text(
            "NATIVEH3VR_COMPILER_SCOPE fixture\nClean compiler run\nNATIVEH3VR_COMPILER_BUILD_END fixture\n"
        )
        self.proof["compilerLog"] = self.record(self.source / COMPILER_LOG, COMPILER_LOG)
        self.receipt = {
            key: copy.deepcopy(self.proof[key])
            for key in (
                "planSha256",
                "unityVersion",
                "managedInputs",
                "sourceFiles",
                "outputs",
                "compilerLog",
            )
        }
        self.receipt.update(
            schemaVersion=1,
            clean=True,
            runId="fixture",
            compilerPolicySha256=digest(POLICY_PATH),
            buildLogPath=str(self.source / COMPILER_LOG),
        )
        self.write_receipt()
        self.write_proof()

    def write_receipt(self):
        (self.source / COMPILER_RECEIPT).write_text(json.dumps(self.receipt))
        self.proof["compilerReceipt"] = self.record(
            self.source / COMPILER_RECEIPT, COMPILER_RECEIPT
        )

    def record(self, path, name):
        return {"path": name, "bytes": path.stat().st_size, "sha256": digest(path)}

    def write_proof(self):
        (self.source / LEGACY_MARKER).write_text(json.dumps(self.summary))
        (self.source / CONTENT_MARKER).write_text(json.dumps(self.proof))

    def check(self):
        return verify(self.source, self.plan_path, self.project)

    def test_valid_stage_and_reverify(self):
        self.check()
        stage(self.source, self.target, self.plan_path, self.project, self.root / "staged.json")
        verify(self.target, self.plan_path, self.project)
        self.assertFalse((self.source / "test").exists())

    def test_old_marker_without_content_proof_never_moves(self):
        (self.source / CONTENT_MARKER).unlink()
        with self.assertRaises(FileNotFoundError):
            stage(self.source, self.target, self.plan_path, self.project, self.root / "staged.json")
        self.assertTrue((self.source / "test").exists())
        self.assertFalse(self.target.exists())

    def test_output_tampering(self):
        (self.source / "test").write_bytes(b"fail")  # Same byte count as original.
        with self.assertRaisesRegex(ValueError, "hash changed"):
            self.check()

    def test_managed_tampering(self):
        (self.project / "Assets/Game.dll").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "hash changed"):
            self.check()

    def test_added_managed_input(self):
        (self.project / "Assets/New.dll").write_bytes(b"new assembly")
        with self.assertRaisesRegex(ValueError, "New managed input"):
            self.check()

    def test_source_tampering(self):
        (self.project / "Assets/Test.asset").write_bytes(b"altered source!")
        with self.assertRaises(ValueError):
            self.check()

    def test_deleted_source(self):
        (self.project / "Assets/Test.asset").unlink()
        with self.assertRaisesRegex(ValueError, "file set changed"):
            self.check()

    def test_added_meta(self):
        (self.project / "Assets/Test.asset.meta").write_text("new import settings")
        with self.assertRaisesRegex(ValueError, "file set changed"):
            self.check()

    def test_plan_tampering(self):
        self.plan["bundles"][0]["assets"][0]["address"] = "different"
        self.plan_path.write_text(json.dumps(self.plan))
        with self.assertRaisesRegex(ValueError, "plan changed"):
            self.check()

    def test_incomplete_output_set(self):
        self.proof["outputs"].pop()
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "output set"):
            self.check()

    def test_missing_observed_bundle_inventory(self):
        del self.proof["bundleInventory"]
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "bundle inventory"):
            self.check()

    def test_extra_address_rejected_even_when_planned_addresses_survive(self):
        self.proof["bundleInventory"][0]["addresses"].append("unexpected")
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "address set"):
            self.check()

    def test_relocated_address_rejected_at_same_count(self):
        self.proof["bundleInventory"][0]["addresses"] = ["different"]
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "address set"):
            self.check()

    def test_changed_dependency_graph_rejected(self):
        self.proof["bundleInventory"][0]["dependencies"] = ["unexpected"]
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "dependency graph"):
            self.check()

    def test_old_success_without_compiler_receipt_fails(self):
        del self.proof["compilerReceipt"]
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "compiler evidence"):
            self.check()

    def test_shader_errors_fail_even_with_rehashed_evidence(self):
        (self.source / COMPILER_LOG).write_text(
            "NATIVEH3VR_COMPILER_BUILD_END fixture\nShader error in 'Alloy/Car Paint': invalid subscript 'pos'\n"
        )
        self.proof["compilerLog"] = self.record(self.source / COMPILER_LOG, COMPILER_LOG)
        self.receipt["compilerLog"] = copy.deepcopy(self.proof["compilerLog"])
        self.write_receipt()
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "Compiler log contains errors"):
            self.check()

    def test_receipt_cannot_bless_other_outputs(self):
        self.receipt["outputs"][0]["sha256"] = "0" * 64
        self.write_receipt()
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "bind accepted outputs"):
            self.check()

    def test_changed_compiler_policy_fails(self):
        self.receipt["compilerPolicySha256"] = "0" * 64
        self.write_receipt()
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "policy changed"):
            self.check()

    def test_delayed_raw_log_error_cannot_be_hidden_by_clean_snapshot(self):
        raw = self.root / "original-build.log"
        raw.write_text(
            (self.source / COMPILER_LOG).read_text()
            + "program_source:18:2: error: no matching function\n"
        )
        self.receipt["buildLogPath"] = str(raw)
        self.write_receipt()
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "delayed compiler"):
            self.check()

    def test_unrelated_clean_raw_log_cannot_replace_original(self):
        raw = self.root / "unrelated.log"
        raw.write_text("Unrelated clean build\n")
        self.receipt["buildLogPath"] = str(raw)
        self.write_receipt()
        self.write_proof()
        with self.assertRaisesRegex(ValueError, "replaced/truncated"):
            self.check()

    def test_partial_stage_invalidates_old_target_marker(self):
        self.target.mkdir(parents=True)
        (self.target / LEGACY_MARKER).write_text("stale success")
        with patch("stage_native_bundles.shutil.move", side_effect=OSError("disk failure")):
            with self.assertRaises(OSError):
                stage(
                    self.source,
                    self.target,
                    self.plan_path,
                    self.project,
                    self.root / "staged.json",
                )
        self.assertFalse((self.target / LEGACY_MARKER).exists())
        self.assertFalse((self.target / CONTENT_MARKER).exists())


if __name__ == "__main__":
    unittest.main()
