#!/usr/bin/env python3
"""A stale scene or DLL must never authorize ignoring missing-script warnings."""

import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "script"))
import original_scene_warnings as warnings


class OriginalWarningProofTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.app = self.root / "Test.app"
        self.data = self.app / "Contents/Resources/Data"
        (self.data / "Managed").mkdir(parents=True)
        self.directory = self.root / "build/scene-integrity"
        self.directory.mkdir(parents=True)
        for name in ("level0", "globalgamemanagers", "Managed/Test.dll"):
            (self.data / name).write_bytes(name.encode())
        self.baseline = self.directory / "missing-script-baseline.json"
        self.write(
            self.baseline,
            {
                "passed": True,
                "errors": [],
                "scenes": [
                    {
                        "path": "Assets/Scenes/Range.unity",
                        "baseline": [
                            {
                                "gameObjectFileID": 4,
                                "componentIndex": 2,
                                "componentFileID": 9,
                                "gameObjectName": "Fence",
                            }
                        ],
                    }
                ],
            },
        )
        self.inventory = self.directory / "full-player-script-proof-managed-types.json"
        self.write(
            self.inventory,
            [{"path": "Test.dll", "sha256": warnings.sha256(self.data / "Managed/Test.dll")}],
        )
        self.proof = self.directory / "full-player-script-proof.json"
        self.write(
            self.proof,
            {
                "passed": True,
                "baselineSha256": warnings.sha256(self.baseline),
                "managedInventorySha256": warnings.sha256(self.inventory),
                "buildSettingsFileSha256": warnings.sha256(self.data / "globalgamemanagers"),
                "scenes": [
                    {
                        "passed": True,
                        "errors": [],
                        "scene": "Assets/Scenes/Range.unity",
                        "playerFile": "level0",
                        "playerSha256": warnings.sha256(self.data / "level0"),
                        "originalNullComponents": 1,
                        "editorOnlyOriginalNulls": 0,
                        "preservedNullComponents": [
                            {"sourceGameObject": 4, "componentIndex": 2, "originalComponent": 9}
                        ],
                    }
                ],
            },
        )
        self.patch = patch.object(warnings, "ROOT", self.root)
        self.patch.start()

    def tearDown(self):
        self.patch.stop()
        self.temp.cleanup()

    @staticmethod
    def write(path, value):
        path.write_text(json.dumps(value))

    def test_current_proof_allows_only_original_name_and_count(self):
        self.assertEqual(warnings.load_baseline(self.app, "Range")["names"], {"Fence": 1})

    def test_changed_packaged_inputs_reject_the_allowance(self):
        for name in ("level0", "globalgamemanagers", "Managed/Test.dll"):
            with self.subTest(name=name):
                path = self.data / name
                before = path.read_bytes()
                path.write_bytes(before + b"changed")
                with self.assertRaises(ValueError):
                    warnings.load_baseline(self.app, "Range")
                path.write_bytes(before)

    def test_changed_original_baseline_is_rejected(self):
        self.baseline.write_text(self.baseline.read_text() + " ")
        with self.assertRaises(ValueError):
            warnings.load_baseline(self.app, "Range")

    def test_unproven_null_component_is_rejected(self):
        proof = json.loads(self.proof.read_text())
        proof["scenes"][0]["preservedNullComponents"][0]["componentIndex"] = 3
        self.write(self.proof, proof)
        with self.assertRaises(ValueError):
            warnings.load_baseline(self.app, "Range")


if __name__ == "__main__":
    unittest.main()
