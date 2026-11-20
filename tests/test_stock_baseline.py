import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "tools" / "stock_baseline.py"


class StockBaselineTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        self.manifest = root / "appmanifest_450540.acf"
        self.log = root / "Player.log"
        self.manifest.write_text(
            '"AppState"\n{\n'
            '    "appid" "450540"\n'
            '    "buildid" "12345678"\n'
            '    "LastOwner" "PRIVATE_STEAM_ID"\n'
            '}\n',
            encoding="utf-8-sig",
        )

    def run_tool(self, *args):
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args], capture_output=True, text=True, check=False
        )

    def test_windows_player_log_with_direct3d_and_steam_build(self):
        source_log = (
            "Initialize engine version: 5.6.3p1 (fixture)\n"
            "GfxDevice: creating device client; threaded=1\n"
            "Direct3D:\n"
            "    Version: Direct3D 11.0 [level 11.0]\n"
            "Scene stack includes C:\\Users\\PRIVATE_USER\\scene.unity\n"
            "A translation layer mentioned Vulkan in a message\n"
        )
        self.log.write_text(source_log, encoding="utf-8-sig")
        manifest_before = self.manifest.read_bytes()
        log_before = self.log.read_bytes()
        result = self.run_tool("--appmanifest", self.manifest, "--player-log", self.log)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout), {
            "steam_installation": {"app_id": "450540", "build_id": "12345678"},
            "unity_player_log": {
                "engine_versions_reported": ["5.6.3p1"],
                "graphics_apis_reported": ["Direct3D 11"],
                "graphics_device_initialization_reported": True,
            },
            "elapsed_time_ms": None,
            "rendered_frames_verified": False,
        })
        self.assertNotIn("PRIVATE_", result.stdout)
        self.assertEqual(self.manifest.read_bytes(), manifest_before)
        self.assertEqual(self.log.read_bytes(), log_before)

    def test_unity_6_and_metal_markers_do_not_prove_frame_rendering(self):
        self.log.write_bytes(
            b"Initialize engine version: 6000.7.0b10 (fixture)\r\n"
            b"Initializing Metal device caps: Private GPU label\r\n"
            b"\xFF\r\n"
        )
        result = self.run_tool("--player-log", self.log)
        self.assertEqual(result.returncode, 0, result.stderr)
        summary = json.loads(result.stdout)
        self.assertIsNone(summary["steam_installation"])
        self.assertEqual(summary["unity_player_log"]["engine_versions_reported"], ["6000.7.0b10"])
        self.assertEqual(summary["unity_player_log"]["graphics_apis_reported"], ["Metal"])
        self.assertFalse(summary["rendered_frames_verified"])
        self.assertIsNone(summary["elapsed_time_ms"])
        self.assertNotIn("Private GPU label", result.stdout)

    def test_manifest_alone_does_not_imply_player_started(self):
        result = self.run_tool("--appmanifest", self.manifest)
        self.assertEqual(result.returncode, 0, result.stderr)
        summary = json.loads(result.stdout)
        self.assertIsNone(summary["unity_player_log"])
        self.assertFalse(summary["rendered_frames_verified"])

    def test_non_unity_log_does_not_imply_player_started(self):
        self.log.write_text("Wine renderer report: game_d3d11.log: empty\n", encoding="utf-8")
        result = self.run_tool("--player-log", self.log)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["unity_player_log"], {
            "engine_versions_reported": [],
            "graphics_apis_reported": [],
            "graphics_device_initialization_reported": False,
        })

    def test_missing_input_errors_do_not_leak_the_path(self):
        result = self.run_tool("--player-log", self.log)
        self.assertEqual(result.returncode, 2)
        self.assertIn("could not read Unity Player log", result.stderr)
        self.assertNotIn(str(self.log), result.stderr)

    def test_readable_manifest_for_different_app_is_rejected(self):
        self.manifest.write_text('"appid" "1000"\n"buildid" "42"\n', encoding="utf-8")
        result = self.run_tool("--appmanifest", self.manifest)
        self.assertEqual(result.returncode, 2)
        self.assertIn("different app", result.stderr)

    def test_manifest_with_ambiguous_build_ids_is_rejected(self):
        with self.manifest.open(mode="a", encoding="utf-8") as stream:
            stream.write('"buildid" "98765432"\n')
        result = self.run_tool("--appmanifest", self.manifest)
        self.assertEqual(result.returncode, 2)
        self.assertIn("expected one numeric buildid", result.stderr)

    def test_at_least_one_input_is_required(self):
        result = self.run_tool()
        self.assertEqual(result.returncode, 2)
        self.assertIn("supply --appmanifest, --player-log, or both", result.stderr)


if __name__ == "__main__":
    unittest.main()
