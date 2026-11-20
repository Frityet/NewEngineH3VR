from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
import json
from pathlib import Path
import plistlib
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from tools import engine_validation as engine


class EngineValidationTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.output = Path(directory.name)

    def write_frame(self, name, pixels, size=(2, 1)):
        path = self.output / name
        width, height = size
        path.write_bytes(f"P6\n{width} {height}\n255\n".encode("ascii") + bytes(pixels))
        return path

    def write_report(self, **changes):
        report = {
            "version": engine.TARGET_VERSION,
            "graphicsDevice": "Metal",
            "scriptingBackend": "Mono",
            "checks": [{"name": name, "passed": True} for name in sorted(engine.EXPECTED_CHECKS)],
            "captures": list(engine.CAPTURES),
            "passed": True,
        }
        report.update(changes)
        (self.output / "report.json").write_text(json.dumps(report), encoding="utf-8")
        for name in engine.CAPTURES:
            (self.output / name).write_bytes(b"\x89PNG\r\n\x1a\n")
            self.write_frame(Path(name).with_suffix(".ppm"), [0] * 6)

    def test_reads_editor_version_from_app_and_executable(self):
        app = self.output / "Unity With Spaces.app"
        executable = app / "Contents/MacOS/Unity"
        executable.parent.mkdir(parents=True)
        executable.touch(mode=0o755)
        tool = subprocess.CompletedProcess([], 0, stdout=f"{engine.TARGET_VERSION}\n")
        for path in (app, executable):
            with self.subTest(path=path), patch.object(engine.subprocess, "run", return_value=tool) as run:
                self.assertEqual(engine.find_editor(path),
                                 engine.Editor(executable.resolve(), engine.TARGET_VERSION))
                self.assertEqual(run.call_args.args[0], [executable.resolve(), "-version"])

    def test_rejects_older_unity(self):
        executable = self.output / "Unity"
        executable.touch(mode=0o755)
        tool = subprocess.CompletedProcess([], 0, stdout="2021.3.45f2\n")
        with patch.object(engine.subprocess, "run", return_value=tool):
            with self.assertRaisesRegex(engine.ValidationError, "Expected a Unity 6.7 editor"):
                engine.find_editor(executable)

    def test_rejects_shallow_invalid_editor_path(self):
        with self.assertRaises(engine.ValidationError):
            engine.find_editor(self.output / "missing" / "Unity")

    def test_checks_macos_bundle_architecture(self):
        player = self.output / "EngineValidation.app"
        executable = player / "Contents/MacOS/EngineValidation"
        executable.parent.mkdir(parents=True)
        with (player / "Contents/Info.plist").open("wb") as info:
            plistlib.dump({"CFBundleExecutable": "EngineValidation"}, info)
        executable.write_bytes(struct.pack("<II", 0xFEEDFACF, 0x0100000C))
        self.assertEqual(engine.check_player(player, "macos"), executable)
        executable.write_bytes(struct.pack("<II", 0xFEEDFACF, 0x01000007))
        with self.assertRaisesRegex(engine.ValidationError, "wrong architecture"):
            engine.check_player(player, "macos")

    def test_checks_windows_player_architecture_and_layout(self):
        player = self.output / "EngineValidation.exe"
        header = b"MZ" + bytes(58) + struct.pack("<I", 64) + b"PE\0\0"
        self.check_x86_64_package(player, "windows", "UnityPlayer.dll", header, 0x8664, 0xAA64)

    def test_checks_linux_player_architecture_and_layout(self):
        player = self.output / "EngineValidation.x86_64"
        header = b"\x7fELF\x02\x01\x01" + bytes(11)
        self.check_x86_64_package(player, "linux", "UnityPlayer.so", header, 62, 183)

    def check_x86_64_package(self, player, target, library_name, header, correct, wrong):
        library = self.output / library_name
        library.touch()
        (self.output / "EngineValidation_Data").mkdir()
        player.write_bytes(header + struct.pack("<H", correct))
        self.assertEqual(engine.check_player(player, target), player)
        player.write_bytes(header + struct.pack("<H", wrong))
        with self.assertRaisesRegex(engine.ValidationError, "wrong architecture"):
            engine.check_player(player, target)
        player.write_bytes(header + struct.pack("<H", correct))
        library.unlink()
        with self.assertRaisesRegex(engine.ValidationError, "Incomplete"):
            engine.check_player(player, target)

    def test_identical_frames_pass(self):
        baseline = self.write_frame("baseline.ppm", [10, 20, 30, 11, 22, 33])
        target = self.write_frame("target.ppm", [10, 20, 30, 11, 22, 33])
        metrics = engine.compare_frames(baseline, target)
        self.assertEqual(metrics["mean_channel_error"], 0.0)
        self.assertEqual(metrics["changed_pixel_fraction"], 0.0)
        self.assertTrue(metrics["passed"])

    def test_small_rounding_difference_passes(self):
        baseline = self.write_frame("baseline.ppm", [0] * 6)
        target = self.write_frame("target.ppm", [1] * 6)
        self.assertTrue(engine.compare_frames(baseline, target)["passed"])

    def test_localized_change_exceeds_pixel_budget(self):
        baseline = self.write_frame("baseline.ppm", [0] * 300, (10, 10))
        target = self.write_frame("target.ppm", [9] + [0] * 299, (10, 10))
        metrics = engine.compare_frames(baseline, target)
        self.assertAlmostEqual(metrics["changed_pixel_fraction"], 0.01)
        self.assertLess(metrics["mean_channel_error"], 1)
        self.assertFalse(metrics["passed"])

    def test_global_color_shift_exceeds_mean_error_budget(self):
        baseline = self.write_frame("baseline.ppm", [0] * 6)
        target = self.write_frame("target.ppm", [2] * 6)
        metrics = engine.compare_frames(baseline, target)
        self.assertEqual(metrics["changed_pixel_fraction"], 0.0)
        self.assertFalse(metrics["passed"])

    def test_rejects_frames_with_different_dimensions(self):
        first = self.write_frame("first.ppm", [0] * 6, (2, 1))
        second = self.write_frame("second.ppm", [0] * 6, (1, 2))
        with self.assertRaisesRegex(engine.ValidationError, "dimensions differ"):
            engine.compare_frames(first, second)

    def test_rejects_truncated_and_overlong_frames(self):
        for pixel_count in (5, 7):
            path = self.write_frame("frame.ppm", [0] * pixel_count)
            with self.subTest(pixel_count=pixel_count), self.assertRaises(engine.ValidationError):
                engine.read_frame(path)

    def test_rejects_unexpected_ppm_header(self):
        path = self.output / "image.ppm"
        for header in (b"P3\n2 1\n255\n", b"P6\n0 5\n255\n", b"P6\n2 1\n254\n"):
            path.write_bytes(header + bytes(6))
            with self.subTest(header=header), self.assertRaises(engine.ValidationError):
                engine.read_frame(path)

    def test_validates_version_metal_and_success(self):
        self.write_report()
        self.assertEqual(engine.read_report(self.output, engine.TARGET_VERSION, "Metal", "Mono"),
                         engine.EXPECTED_CHECKS)
        for changes in ({"version": "2021.3.45f2"}, {"graphicsDevice": "Null"},
                        {"passed": False}, {"checks": []},
                        {"checks": [{"name": "depth", "passed": "true"}]},
                        {"checks": [{"name": "depth", "passed": True}] * 2},
                        {"captures": ["../out.png"]}):
            self.write_report(**changes)
            with self.subTest(changes=changes), self.assertRaises(engine.ValidationError):
                engine.read_report(self.output, engine.TARGET_VERSION, "Metal", "Mono")

    def test_requires_complete_fixture_checks(self):
        checks = [{"name": name, "passed": True} for name in sorted(engine.EXPECTED_CHECKS)]
        for changes, message in ((checks[1:], "Missing .*alpha-blend"),
                                 (checks + [{"name": "unrecognized", "passed": True}], "Unexpected .*unrecognized")):
            self.write_report(checks=changes)
            with self.subTest(message=message), self.assertRaisesRegex(engine.ValidationError, message):
                engine.read_report(self.output, engine.TARGET_VERSION, "Metal", "Mono")

    def test_requires_observed_scripting_backend(self):
        self.write_report(scriptingBackend="CoreCLR")
        self.assertEqual(engine.read_report(self.output, engine.TARGET_VERSION, "Metal", "CoreCLR"),
                         engine.EXPECTED_CHECKS)
        for backend in ("CoreCLR", "Unknown", None):
            self.write_report(scriptingBackend=backend)
            with self.subTest(backend=backend), self.assertRaisesRegex(engine.ValidationError, "Expected Mono"):
                engine.read_report(self.output, engine.TARGET_VERSION, "Metal", "Mono")

    def test_requires_expected_graphics_backend_for_each_platform(self):
        for graphics in ("Direct3D11", "Vulkan"):
            self.write_report(graphicsDevice=graphics)
            with self.subTest(graphics=graphics):
                self.assertEqual(engine.read_report(self.output, engine.TARGET_VERSION, graphics, "Mono"),
                                 engine.EXPECTED_CHECKS)
                with self.assertRaisesRegex(engine.ValidationError, "Expected Metal"):
                    engine.read_report(self.output, engine.TARGET_VERSION, "Metal", "Mono")

    def test_missing_and_malformed_captures_never_pass(self):
        self.write_report()
        (self.output / "raster.png").unlink()
        with self.assertRaises(FileNotFoundError):
            engine.read_report(self.output, engine.TARGET_VERSION, "Metal", "Mono")
        self.write_report()
        (self.output / "raster.png").write_bytes(b"corrupt")
        with self.assertRaisesRegex(engine.ValidationError, "Invalid PNG capture"):
            engine.read_report(self.output, engine.TARGET_VERSION, "Metal", "Mono")

    def test_subprocess_error_is_visible_and_keeps_log(self):
        log = self.output / "process.log"
        with self.assertRaisesRegex(engine.ValidationError, "exited with status 4"):
            engine.run_process([sys.executable, "-c", "print('error'); raise SystemExit(4)"],
                               log, None, 10)
        self.assertIn("error", log.read_text(encoding="utf-8"))
        with self.assertRaisesRegex(engine.ValidationError, "exceeded"):
            engine.run_process([sys.executable, "-c", "import time; time.sleep(10)"],
                               log, None, 0.1)

    def test_matrix_records_failed_target_and_continues(self):
        build_dir = self.output / "builds"
        editor = engine.Editor(self.output / "Unity", engine.TARGET_VERSION)
        passed = {"status": "built", "passed": True}
        with (patch.object(engine, "find_editor", return_value=editor),
              patch.object(engine, "run_fixture", side_effect=[passed, OSError("missing module"), passed]) as run,
              redirect_stdout(StringIO()), redirect_stderr(StringIO())):
            code = engine.main(["--platform", "all", "--build-only", "--coreclr", "--output", str(build_dir)])
        self.assertEqual(code, 1)
        self.assertEqual([call.args[1] for call in run.call_args_list], ["macos", "windows", "linux"])
        self.assertTrue(all(call.kwargs["coreclr"] for call in run.call_args_list))
        report = json.loads((build_dir / "summary.json").read_text(encoding="utf-8"))
        self.assertFalse(report["passed"])
        self.assertEqual(report["platforms"]["windows"]["error"], "missing module")
        self.assertEqual(report["platforms"]["linux"], passed)


if __name__ == "__main__":
    unittest.main()
