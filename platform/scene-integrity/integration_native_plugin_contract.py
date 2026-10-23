#!/usr/bin/env python3
"""Real stale-plugin regression; no runtime, Unity or headset is launched."""

from pathlib import Path
import json
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "script"))
from native_plugin_contract import check_bridge, check_plugin_copies, digest, refresh_bridges

STALE = ROOT / "build/player-attempts/full-first-missing-focus/native-plugins/libNativeH3VRXR.dylib"
CURRENT = ROOT / "native/build/libNativeH3VRXR.dylib"


class NativePluginContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="native-plugin-contract-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.app = self.root / "Fixture.app"
        self.plugins = self.app / "Contents/PlugIns"
        (self.plugins / "ARM64").mkdir(parents=True)

    def copy(self, source, relative):
        destination = self.plugins / relative
        shutil.copy2(source, destination)
        return destination

    def test_actual_stale_library_rejects_all_four_missing_exports(self):
        with self.assertRaises(ValueError) as error:
            check_bridge(STALE)
        for symbol in ("focused", "fade_color", "fade_grid", "fade_state"):
            self.assertIn("nh3vr_xr_" + symbol, str(error.exception))

    def test_current_duplicate_copies_pass(self):
        self.copy(CURRENT, CURRENT.name)
        self.copy(CURRENT, "ARM64/" + CURRENT.name)
        self.assertEqual(len(check_plugin_copies(self.app)), 2)

    def test_stale_shadow_rejected_even_with_correct_arm64_copy(self):
        self.copy(STALE, STALE.name)
        self.copy(CURRENT, "ARM64/" + CURRENT.name)
        with self.assertRaisesRegex(ValueError, "missing public native bridge exports"):
            check_plugin_copies(self.app)

    def test_same_exports_different_binaries_rejected(self):
        self.copy(CURRENT, CURRENT.name)
        other = self.copy(CURRENT, "ARM64/" + CURRENT.name)
        subprocess.run(
            ["/usr/bin/install_name_tool", "-id", "@rpath/different.dylib", str(other)], check=True
        )
        check_bridge(other)  # ABI still exists, but lookup order must not select different code.
        with self.assertRaisesRegex(ValueError, "Conflicting packaged plugin copies"):
            check_plugin_copies(self.app)

    def test_missing_library_with_managed_consumer_rejected(self):
        managed = self.app / "Contents/Resources/Data/Managed"
        managed.mkdir(parents=True)
        (managed / "NativeH3VR.XR.Managed.dll").write_bytes(b"consumer fixture")
        with self.assertRaisesRegex(ValueError, "Managed native bridge library missing"):
            check_plugin_copies(self.app)

    def test_refresh_both_copies_and_idempotency(self):
        for name in ("libNativeH3VRXR.dylib", "libNativeH3VRTracking.dylib"):
            source = ROOT / "native/build" / name
            self.copy(STALE if name == CURRENT.name else source, name)
            self.copy(source, "ARM64/" + name)
        managed = self.app / "Contents/Resources/Data/Managed"
        managed.mkdir(parents=True)
        for name in ("NativeH3VR.XR.Managed.dll", "NativeH3VR.SteamVR.Adapter.dll"):
            shutil.copy2(ROOT / "build/composed/final" / name, managed / name)
        result = refresh_bridges(self.app)
        self.assertEqual(len(result["copies"]), 4)
        self.assertEqual(sum(r["beforeSha256"] != r["afterSha256"] for r in result["copies"]), 1)
        self.assertEqual(len(check_plugin_copies(self.app)), 4)
        self.assertTrue(
            all(r["beforeSha256"] == r["afterSha256"] for r in refresh_bridges(self.app)["copies"])
        )
        # A mismatched managed consumer must stop refresh before any native write.
        self.copy(STALE, STALE.name)
        (managed / "NativeH3VR.XR.Managed.dll").write_bytes(b"wrong managed bridge")
        before = digest(self.plugins / STALE.name)
        with self.assertRaisesRegex(ValueError, "Packaged managed bridge differs"):
            refresh_bridges(self.app)
        self.assertEqual(digest(self.plugins / STALE.name), before)


if __name__ == "__main__":
    unittest.main()
