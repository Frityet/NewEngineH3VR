"""Exercise the publication checker against real Git trees and indexes."""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "tools" / "audit_sources.py"


class PublicationAuditTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repo = Path(temporary.name) / "source repo"
        self.repo.mkdir()
        self.git("init", "--quiet")
        self.git("config", "user.name", "Publication Audit Test")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "commit.gpgSign", "false")
        self.git("config", "core.hooksPath", str(self.repo / "no-hooks"))

    def git(self, *args, payload=None):
        return subprocess.run(
            ["git", *args], cwd=self.repo, input=payload, capture_output=True, check=True,
        ).stdout

    def write(self, path, data=b"sample text\n"):
        file = self.repo / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_bytes(data)

    def check(self, *args, cwd=None):
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args],
            cwd=cwd or self.repo,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_index_accepts_source_files_and_unicode_content(self):
        paths = (
            "README.md",
            "LICENSE",
            ".gitignore",
            ".github/workflows/validation.yml",
            "docs/validation.md",
            "fixture/Assets/Editor.meta",
            "fixture/Assets/Editor/Build.cs",
            "fixture/Assets/Validation/Shaders/Unlit.shader",
            "fixture/Assets/Validation/Shaders/Unlit.shader.meta",
            "fixture/Packages/manifest.json",
            "fixture/Packages/packages-lock.json",
            "fixture/ProjectSettings/ProjectVersion.txt",
            "tools/validate.py",
            "tests/test_validate.py",
        )
        for path in paths:
            self.write(path, "Testing original sources ✓\r\n".encode("utf-8"))
        self.git("add", ".")

        result = self.check("--staged", cwd=self.repo / "fixture")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Review text provenance", result.stdout)

    def test_default_checks_head_and_staged_uses_blob_not_worktree(self):
        self.write("tools/validate.py", b"original source\n")
        self.git("add", ".")
        self.git("commit", "-qm", "Original sources")
        self.write("tools/validate.py", b"binary\x00payload")
        self.git("add", ".")
        self.write("tools/validate.py", b"working copy is text again\n")

        committed = self.check()
        self.assertEqual(committed.returncode, 0, committed.stderr)
        staged = self.check("--staged")
        self.assertEqual(staged.returncode, 1)
        self.assertIn("tools/validate.py' is not UTF-8 text", staged.stderr)

    def test_denies_asset_build_and_unusual_paths(self):
        paths = (
            "fixture/Assets/Editor/image.png",
            "fixture/Assets/Scenes/Level.unity",
            "fixture/Library/Cache.txt",
            "fixture/ProjectSettings/ProjectSettings.asset",
            "tools/line\nbreak.py",
            "tools/field\tseparator.py",
            "dist/player.zip",
        )
        for path in paths:
            self.write(path)
        self.git("add", ".")

        result = self.check("--staged")
        self.assertEqual(result.returncode, 1)
        for path in paths:
            with self.subTest(path=path):
                self.assertIn(f"{path.encode()!r} is outside the source allowlist", result.stderr)

    def test_denies_oversized_invalid_utf8_and_binary_data(self):
        self.write("docs/huge.md", b"x" * (1024 * 1024 + 1))
        self.write("docs/not_utf8.md", b"invalid \xff")
        self.write("docs/control_bytes.md", b"image\x1a\n")
        self.git("add", ".")

        result = self.check("--staged")
        self.assertEqual(result.returncode, 1)
        self.assertIn("docs/huge.md' exceeds", result.stderr)
        self.assertIn("docs/not_utf8.md' is not UTF-8 text", result.stderr)
        self.assertIn("docs/control_bytes.md' is not UTF-8 text", result.stderr)

    def test_denies_symlinks_gitlinks_and_unmerged_entries(self):
        self.write("README.md")
        self.git("add", ".")
        self.git("commit", "-qm", "Original sources")
        commit = self.git("rev-parse", "HEAD").strip().decode("ascii")
        blob = self.git("hash-object", "-w", "--stdin", payload=b"README.md").strip().decode("ascii")
        self.git("update-index", "--add", "--cacheinfo", f"120000,{blob},tools/link.py")
        self.git("update-index", "--add", "--cacheinfo", f"160000,{commit},tools/submodule.py")
        self.git("update-index", "--index-info", payload=f"100644 {blob} 2\ttools/conflict.py\n".encode())

        result = self.check("--staged")
        self.assertEqual(result.returncode, 1)
        self.assertIn("tools/link.py' is not a regular file", result.stderr)
        self.assertIn("tools/submodule.py' is not a regular file", result.stderr)
        self.assertIn("tools/conflict.py' has an unresolved index entry", result.stderr)


if __name__ == "__main__":
    unittest.main()
