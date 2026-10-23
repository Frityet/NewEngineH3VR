"""Exercise the publication gate against isolated Git indexes, never game data."""

import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "script").mkdir()
        (self.root / "config").mkdir()
        shutil.copy2(
            Path(__file__).with_name("check_publication.py"),
            self.root / "script/check_publication.py",
        )
        self.git("init", "-q")
        self.manifest = ["README.md", "config/public-files.json", "script/check_publication.py"]
        (self.root / "README.md").write_text("Authored source-only project\n")
        self.stage()

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True)

    def stage(self):
        (self.root / "config/public-files.json").write_text(json.dumps(self.manifest))
        self.git("add", ".")

    def check(self, success):
        result = subprocess.run(
            [sys.executable, str(self.root / "script/check_publication.py")],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 0 if success else 1, result.stdout + result.stderr)

    def test_reviewed_text_passes(self):
        self.check(True)

    def test_unreviewed_source_fails(self):
        (self.root / "extra.py").write_text("print(1)\n")
        self.stage()
        self.check(False)

    def test_asset_fails_even_when_manifest_lists_it(self):
        (self.root / "example.asset").write_text("synthetic asset fixture\n")
        self.manifest.append("example.asset")
        self.stage()
        self.check(False)

    def test_binary_disguised_as_source_fails(self):
        (self.root / "README.md").write_bytes(b"MZ\0synthetic")
        self.stage()
        self.check(False)

    def test_symlink_fails(self):
        (self.root / "README.md").unlink()
        (self.root / "README.md").symlink_to("script/check_publication.py")
        self.stage()
        self.check(False)

    def test_missing_reviewed_file_fails(self):
        self.git("rm", "-f", "README.md")
        self.check(False)

    def test_uses_staged_blob_not_working_copy(self):
        (self.root / "README.md").write_bytes(b"MZ\0synthetic")
        self.stage()
        (self.root / "README.md").write_text("clean unstaged replacement\n")
        self.check(False)


if __name__ == "__main__":
    unittest.main()
