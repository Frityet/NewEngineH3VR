from pathlib import Path
import json
import tempfile
import unittest
from legacy_runtime_staging import retire_legacy_runtime


class LegacyRuntimeStagingChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="newengine-stage-check-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.streaming = self.root / "project/Assets/StreamingAssets"
        self.legacy = self.streaming / "NativeRuntime"
        (self.legacy / "build/bin").mkdir(parents=True)
        (self.legacy / "build/bin/runtime.dylib").write_bytes(b"preserved runtime")
        self.meta = self.streaming / "NativeRuntime.meta"
        self.meta.write_text("preserved folder GUID")
        self.receipt = self.root / "build/retired-runtime"

    def test_preserves_legacy_contents_and_folder_metadata_outside_assets(self):
        report = retire_legacy_runtime(self.streaming, self.receipt)
        self.assertTrue(json.loads(report.read_text())["retired"])
        self.assertEqual(
            (self.receipt / "NativeRuntime/build/bin/runtime.dylib").read_bytes(),
            b"preserved runtime",
        )
        self.assertEqual((self.receipt / "NativeRuntime.meta").read_text(), "preserved folder GUID")
        self.assertFalse(self.legacy.exists())
        self.assertFalse(self.meta.exists())

    def test_collision_refused_without_mutating_legacy(self):
        (self.streaming / "NewEngine").mkdir()
        with self.assertRaisesRegex(ValueError, "collision"):
            retire_legacy_runtime(self.streaming, self.receipt)
        self.assertTrue((self.legacy / "build/bin/runtime.dylib").is_file())
        self.assertFalse(self.receipt.exists())

    def test_symlink_refused_without_following_or_moving(self):
        (self.legacy / "escape").symlink_to(self.root)
        with self.assertRaisesRegex(ValueError, "symlink"):
            retire_legacy_runtime(self.streaming, self.receipt)
        self.assertTrue(self.legacy.is_dir())
        self.assertFalse(self.receipt.exists())

    def test_backup_collision_refused_without_moving(self):
        self.receipt.mkdir(parents=True)
        with self.assertRaisesRegex(ValueError, "already exists"):
            retire_legacy_runtime(self.streaming, self.receipt)
        self.assertTrue(self.legacy.is_dir())

    def test_backup_inside_assets_refused(self):
        with self.assertRaisesRegex(ValueError, "outside"):
            retire_legacy_runtime(self.streaming, self.streaming.parent / "backup")
        self.assertTrue(self.legacy.is_dir())


if __name__ == "__main__":
    unittest.main()
