"""Reject stale asset baselines and preserve originals on failed atomic updates."""

from copy import deepcopy
from pathlib import Path
import hashlib
import json
import tempfile
import unittest
from unittest.mock import patch

import install_shader_state_overlay as installer
from audit_native_app import shader_overlay_bindings


class OverlayInstallationGuards(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.bundle = self.root / installer.BUNDLE
        self.bundle.write_bytes(b"original verified bundle")
        self.record = {
            "path": installer.BUNDLE,
            "bytes": self.bundle.stat().st_size,
            "sha256": installer.sha256(self.bundle),
        }
        self.proof = {
            "schemaVersion": 2,
            "verified": True,
            "unityVersion": "2021.3.45f2",
            "outputs": [self.record],
        }

    def tearDown(self):
        self.temporary.cleanup()

    def test_exact_verified_baseline_accepted(self):
        self.assertEqual(installer.validate_base_record(self.proof, self.bundle), self.record)

    def test_unverified_or_different_engine_refused(self):
        for changes in (
            {"verified": False},
            {"schemaVersion": 1},
            {"unityVersion": "5.6.7f1"},
            {"unityVersion": "2022.3.1f1"},
        ):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                installer.validate_base_record(dict(self.proof, **changes), self.bundle)

    def test_ambiguous_or_missing_bundle_record_refused(self):
        for records in ([], [self.record, self.record], [dict(self.record, path="../escape")]):
            with self.subTest(records=records), self.assertRaises(ValueError):
                installer.validate_base_record(dict(self.proof, outputs=records), self.bundle)

    def test_same_length_modified_payload_refused(self):
        self.bundle.write_bytes(b"changed! verified bundle")
        self.assertEqual(self.bundle.stat().st_size, self.record["bytes"])
        with self.assertRaises(ValueError):
            installer.validate_base_record(self.proof, self.bundle)

    def test_wrong_size_or_digest_refused(self):
        for field, value in (("bytes", 0), ("sha256", "0" * 64)):
            proof = deepcopy(self.proof)
            proof["outputs"][0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                installer.validate_base_record(proof, self.bundle)

    def test_atomic_replacement_failure_keeps_original(self):
        candidate = self.root / "candidate"
        candidate.write_bytes(b"new bundle")
        with patch.object(installer.os, "replace", side_effect=OSError("replacement failed")):
            with self.assertRaises(OSError):
                installer.atomic_copy(candidate, self.bundle)
        self.assertEqual(self.bundle.read_bytes(), b"original verified bundle")
        self.assertFalse(self.bundle.with_name(self.bundle.name + ".new-engine-update").exists())

    def test_existing_update_is_preserved(self):
        pending = self.bundle.with_name(self.bundle.name + ".new-engine-update")
        pending.write_bytes(b"earlier partial update")
        with self.assertRaises(ValueError):
            installer.atomic_copy(self.bundle, self.bundle)
        self.assertEqual(pending.read_bytes(), b"earlier partial update")
        self.assertEqual(self.bundle.read_bytes(), b"original verified bundle")


class OverlayAuditBinding(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.app = Path(self.temporary.name)
        self.assets = self.app / "Contents/Resources/Data/StreamingAssets"
        self.assets.mkdir(parents=True)
        self.target = self.assets / installer.BUNDLE
        self.target.write_bytes(b"corrected bundle")
        before = {"path": installer.BUNDLE, "bytes": 8, "sha256": "b" * 64}
        base = self.assets / installer.BASE_PROOF
        base.write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "verified": True,
                    "unityVersion": "2021.3.45f2",
                    "outputs": [before],
                }
            )
        )
        self.data = {
            "schemaVersion": 1,
            "kind": "newengine-unity2021-scope-depth-clip",
            "bundle": installer.BUNDLE,
            "before": before,
            "after": {
                "path": installer.BUNDLE,
                "bytes": self.target.stat().st_size,
                "sha256": installer.sha256(self.target),
            },
            "baseContentProof": {"path": base.name, "sha256": installer.sha256(base)},
            "proofEncoding": "json-indent-2-utf8-lf",
            "proof": {
                "schemaVersion": 1,
                "kind": "newengine-pip-zclip-state-only",
                "passed": True,
                "input": {"sha256": before["sha256"]},
                "candidate": {"sha256": installer.sha256(self.target)},
                "verification": {
                    "fieldDifferences": [
                        {
                            "path": "m_ParsedForm.m_SubShaders[0].m_Passes["
                            + str(i)
                            + "].m_State.zClip.val",
                            "before": 0.0,
                            "after": 1.0,
                        }
                        for i in range(2)
                    ],
                    "shader": {
                        "name": "Unlit/PIPScope",
                        "file": "CAB-d98205cd3e60321f23684452d96b0f22",
                        "pathId": 5248756200144351081,
                        "compiledProgramBytesIdentical": True,
                    },
                    "blockVerification": {
                        "completeDecompressedStreamDiffersOnlyAtShaderStateBytes": True,
                        "directoryAndResourceBytesIdentical": True,
                    },
                    "objectCount": 2,
                    "unchangedObjectCount": 1,
                },
            },
        }
        self.write()

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, refresh_digest=True):
        if refresh_digest:
            self.data["proofSha256"] = hashlib.sha256(
                (json.dumps(self.data["proof"], indent=2) + "\n").encode()
            ).hexdigest()
        (self.assets / installer.MARKER).write_text(json.dumps(self.data))

    def test_proof_and_delivered_bytes_both_bound(self):
        self.assertEqual(len(shader_overlay_bindings(self.app)), 2)

    def test_stale_proof_digest_refused(self):
        self.data["proof"]["passed"] = False
        self.write(False)
        with self.assertRaises(ValueError):
            shader_overlay_bindings(self.app)

    def test_changed_bundle_refused(self):
        self.target.write_bytes(b"uncorrect bundle")
        with self.assertRaises(ValueError):
            shader_overlay_bindings(self.app)

    def test_failed_or_misbound_proof_refused(self):
        original = deepcopy(self.data)
        for key, value in [
            ("passed", False),
            ("kind", "different-repair"),
            ("input", {"sha256": "0" * 64}),
            ("candidate", {"sha256": "0" * 64}),
        ]:
            self.data = deepcopy(original)
            self.data["proof"][key] = value
            self.write()
            with self.subTest(key=key), self.assertRaises(ValueError):
                shader_overlay_bindings(self.app)

    def test_non_admitted_changes_refused(self):
        original = deepcopy(self.data)
        for key, value in [
            ("fieldDifferences", []),
            ("shader", {}),
            ("blockVerification", {}),
            ("unchangedObjectCount", 0),
        ]:
            self.data = deepcopy(original)
            self.data["proof"]["verification"][key] = value
            self.write()
            with self.subTest(key=key), self.assertRaises(ValueError):
                shader_overlay_bindings(self.app)

    def test_different_baseline_record_refused(self):
        self.data["before"] = dict(self.data["before"], sha256="0" * 64)
        self.write()
        with self.assertRaises(ValueError):
            shader_overlay_bindings(self.app)


if __name__ == "__main__":
    unittest.main()
