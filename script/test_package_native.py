#!/usr/bin/env python3
"""Player-preservation checks on temporary byte fixtures; never touch a real app."""

from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import package_native as package


class PackagePlayerChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="newengine-package-check-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        p = patch.object(package.subprocess, "check_output", return_value="arm64\n")
        p.start()
        self.addCleanup(p.stop)

        def commands(path):
            data = path.read_bytes()
            return (
                ["@executable_path/../Frameworks/UnityPlayer.dylib"]
                if data.startswith(b"engine")
                else []
            ), []

        p = patch.object(package, "macho_commands", side_effect=commands)
        p.start()
        self.addCleanup(p.stop)

    def write(self, name, value):
        path = self.directory / name
        path.write_bytes(value)
        return path

    def test_fresh_named_unity_engine_is_preserved_before_launcher_install(self):
        entry = self.write(package.LAUNCHER, b"engine current")
        player = package.prepare_player(self.directory, package.LAUNCHER)
        self.assertEqual(player.read_bytes(), b"engine current")
        self.assertFalse(entry.exists())

    def test_already_packaged_engine_is_preserved(self):
        entry = self.write(package.LAUNCHER, b"launcher")
        self.write(package.PLAYER, b"engine packaged")
        player = package.prepare_player(self.directory, package.LAUNCHER)
        self.assertEqual(player.read_bytes(), b"engine packaged")
        self.assertEqual(entry.read_bytes(), b"launcher")

    def test_legacy_packaged_names_migrate_without_overwriting_engine(self):
        self.write(package.LEGACY_LAUNCHER, b"legacy launcher")
        self.write(package.LEGACY_PLAYER, b"engine legacy")
        player = package.prepare_player(self.directory, package.LEGACY_LAUNCHER)
        self.assertEqual(player.read_bytes(), b"engine legacy")
        self.assertFalse((self.directory / package.LEGACY_PLAYER).exists())
        self.assertFalse((self.directory / package.LEGACY_LAUNCHER).exists())

    def test_fresh_build_engine_supersedes_previous_generated_player(self):
        self.write(package.LAUNCHER, b"engine rebuilt")
        self.write(package.PLAYER, b"engine previous")
        player = package.prepare_player(self.directory, package.LAUNCHER)
        self.assertEqual(player.read_bytes(), b"engine rebuilt")

    def test_conflicting_packaged_players_fail_without_modification(self):
        self.write(package.LAUNCHER, b"launcher")
        self.write(package.PLAYER, b"engine one")
        self.write(package.LEGACY_PLAYER, b"engine two")
        before = {p.name: p.read_bytes() for p in self.directory.iterdir()}
        with self.assertRaisesRegex(ValueError, "Conflicting"):
            package.prepare_player(self.directory, package.LAUNCHER)
        self.assertEqual({p.name: p.read_bytes() for p in self.directory.iterdir()}, before)

    def test_missing_engine_rejected_instead_of_relabeling_launcher(self):
        entry = self.write(package.LAUNCHER, b"launcher")
        with self.assertRaisesRegex(ValueError, "no valid separate"):
            package.prepare_player(self.directory, package.LAUNCHER)
        self.assertEqual(entry.read_bytes(), b"launcher")

    def test_non_arm64_fresh_engine_rejected_before_mutation(self):
        entry = self.write(package.LAUNCHER, b"engine intel")
        with patch.object(package.subprocess, "check_output", return_value="x86_64\n"):
            with self.assertRaisesRegex(ValueError, "ARM64-only"):
                package.prepare_player(self.directory, package.LAUNCHER)
        self.assertEqual(entry.read_bytes(), b"engine intel")
        self.assertFalse((self.directory / package.PLAYER).exists())

    def test_symlink_player_rejected_without_following_it(self):
        self.write(package.LAUNCHER, b"launcher")
        external = self.write("outside", b"engine external")
        (self.directory / package.PLAYER).symlink_to(external)
        with self.assertRaisesRegex(ValueError, "symlink"):
            package.prepare_player(self.directory, package.LAUNCHER)
        self.assertEqual(external.read_bytes(), b"engine external")


if __name__ == "__main__":
    unittest.main()
