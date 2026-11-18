#!/usr/bin/env python3
"""Build and run a source-only macOS Metal/OpenXR fixture with two captured eyes."""
import argparse
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile

FIXTURE = Path(__file__).resolve().parent
sys.path.insert(0, str(FIXTURE.parents[1] / "runtime/UnityMetal"))
from build import LIBRARY, PLUGIN_META, SOURCE, UNITY_VERSION, build_plugin
from run import run


def check_capture(capture, width, height):
    with tempfile.TemporaryDirectory(prefix="nh3vr-bitmap-") as temporary:
        bitmap = Path(temporary) / "stereo.bmp"
        subprocess.run(["sips", "-s", "format", "bmp", capture, "--out", bitmap],
                       check=True, stdout=subprocess.DEVNULL)
        data = bitmap.read_bytes()
    image_width, stored_height, planes, bits, compression = struct.unpack_from("<iiHHI", data, 18)
    if (image_width, abs(stored_height), planes, bits, compression) != (width * 2, height, 1, 32, 3):
        raise ValueError("Unexpected macOS stereo bitmap format")
    if struct.unpack_from("<III", data, 54) != (0xff0000, 0x00ff00, 0x0000ff):
        raise ValueError("Unexpected macOS stereo bitmap channel masks")
    offset = struct.unpack_from("<I", data, 10)[0]
    markers = [[[], []], [[], []]]
    for row in range(height):
        y = row if stored_height < 0 else height - row - 1
        start = offset + row * image_width * 4
        pixels = struct.iter_unpack("<BBBB", data[start:start + image_width * 4])
        for x, (blue, green, red, _) in enumerate(pixels):
            eye, local_x = divmod(x, width)
            if red >= 160 and green < 90 and blue < 90:
                markers[eye][0].append((local_x, y))
            if green >= 160 and red < 90 and blue < 90:
                markers[eye][1].append((local_x, y))
    if any(len(color) < 32 for eye in markers for color in eye):
        raise ValueError("Both compositor eyes must contain the red and green markers")
    centers = [[tuple(sum(values) / len(values) for values in zip(*color)) for color in eye] for eye in markers]
    if any(red[1] - green[1] < 20 for red, green in centers):
        raise ValueError("Compositor eye textures are vertically inverted")
    if centers[0][0][0] - centers[1][0][0] < 2:
        raise ValueError("Compositor eye textures lack binocular parallax")
    return centers


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-editor", type=Path, required=True)
    parser.add_argument("--openxr-headers", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--frames", type=int, default=240)
    args = parser.parse_args()
    if args.frames < 60:
        parser.error("Use at least 60 stereo frames")
    work = args.work.resolve()
    work.mkdir(parents=True)
    project = work / "project"
    assets = project / "Assets"
    editor = assets / "Editor"
    editor.mkdir(parents=True)
    plugins = assets / "Plugins/macOS"
    plugins.mkdir(parents=True)
    settings = project / "ProjectSettings"
    settings.mkdir()
    (settings / "ProjectVersion.txt").write_text(f"m_EditorVersion: {UNITY_VERSION}\n")
    packages = project / "Packages"
    packages.mkdir()
    (packages / "manifest.json").write_text(
        '{"dependencies":{"com.unity.modules.xr":"1.0.0","com.unity.modules.vr":"1.0.0"}}\n')
    for file in ("MetalOpenXR.cs", "NativeStereoRenderer.cs"):
        shutil.copyfile(SOURCE / file, assets / file)
    shutil.copyfile(FIXTURE / "MetalSmoke.cs", assets / "MetalSmoke.cs")
    shutil.copyfile(FIXTURE / "BuildMetalSmoke.cs", editor / "BuildMetalSmoke.cs")
    build_plugin(args.unity_editor.resolve(), args.openxr_headers.resolve(), plugins / LIBRARY)
    (plugins / (LIBRARY + ".meta")).write_text(PLUGIN_META)
    app = work / "MetalSmoke.app"
    subprocess.run([args.unity_editor.resolve(), "-batchmode", "-quit", "-projectPath", project,
                    "-buildTarget", "OSXUniversal", "-executeMethod", "BuildMetalSmoke.Build",
                    "-smokeOutput", app, "-logFile", work / "editor.log"], check=True, timeout=180)
    log = work / "player.log"
    capture = work / "stereo.png"
    os.environ.update(VR_OPENXR_TARGET_HZ="90", NATIVEH3VR_EYE_WIDTH="800", NATIVEH3VR_EYE_HEIGHT="800",
                      NATIVEH3VR_SMOKE_FRAMES=str(args.frames))
    result = run(app, args.runtime, work / "state", True, log,
                 ["-screen-fullscreen", "0", "-screen-width", "640", "-screen-height", "400"],
                 trace=work / "macvr.csv", profile=work / "unity.csv", capture=capture, timeout=100)
    if result:
        raise SystemExit(f"Smoke player exited {result}, see {log}")
    lines = log.read_text(errors="replace").splitlines()
    receipt = next((line for line in lines
                    if line.startswith("UNITY_METAL_SMOKE passed=True")), None)
    state_line = next((line for line in lines if line.startswith("UNITY_METAL_SAVE_ROOT ")), "")
    if not receipt or not state_line or not Path(state_line.split(" ", 1)[1]).is_relative_to(work / "state/preferences"):
        raise SystemExit("Missing player validation or isolated Unity save path")
    centers = check_capture(capture, 800, 800)
    print(receipt)
    print("COMPOSITOR_STEREO_MARKERS", centers)
    print(capture)


if __name__ == "__main__":
    main()
