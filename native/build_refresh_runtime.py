#!/usr/bin/env python3
"""Build the game's refresh-aware runtime from its immutable source snapshot.

The sibling runtime supplies headers only. Installation is separate from this
candidate build; no Steam files, headset modes or running Home state are changed.
"""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "native/build/runtime"
SNAPSHOT = ROOT / ".tools/native-vr-runtime-snapshot"
OUT = ROOT / "build/refresh-rate"
SOURCE = OUT / "source"
RUNTIME = OUT / "runtime"
DEPENDENCIES = ROOT.parent / "ValveIndexMacOS"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(args, **kwargs):
    subprocess.run([str(x) for x in args], check=True, timeout=120, **kwargs)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    provenance = json.loads((BASE / "provenance.json").read_text())
    inputs = {}
    for path in (SNAPSHOT / "native").rglob("*"):
        if not path.is_file():
            continue
        name = path.relative_to(SNAPSHOT).as_posix()
        actual = digest(path)
        if actual != provenance["sources"][name]["sha256"]:
            raise ValueError("Pinned runtime source changed: " + name)
        inputs[str(path)] = actual
    shutil.copytree(SNAPSHOT / "native", SOURCE / "native", dirs_exist_ok=True)
    patch = ROOT / "native/runtime/headset-refresh.patch"
    run(["patch", "--batch", "--fuzz=0", "-p1", "-i", patch], cwd=SOURCE)
    bins = RUNTIME / "build/bin"
    bins.mkdir(parents=True, exist_ok=True)
    for name in ["libIndexCore.dylib", "libopenxr_loader.dylib", "libluajit-5.1.2.dylib"]:
        source = BASE / "build/bin" / name
        inputs[str(source)] = digest(source)
        (bins / name).unlink(missing_ok=True)
        shutil.copy2(source, bins / name)
    manifest = RUNTIME / "build/openxr/virtual-reality.json"
    manifest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(BASE / "build/openxr/virtual-reality.json", manifest)
    headers = [
        DEPENDENCIES / "extern/openxr/include",
        DEPENDENCIES / "extern/vulkan-headers/include",
    ]
    for folder in headers:
        for path in folder.rglob("*.h"):
            inputs[str(path)] = digest(path)
    flags = [
        "xcrun",
        "clang",
        "-arch",
        "arm64",
        "-mmacosx-version-min=13.0",
        "-O2",
        "-fobjc-arc",
        "-Wall",
        "-Wextra",
        "-Werror",
        "-fvisibility=hidden",
        "-DIVHMDDisplaySession=IVXRHMDDisplaySession",
        "-DIVTrackingIPCServer=IVXRTrackingIPCServer",
        '-DINDEXMAC_RESOURCES="' + str(DEPENDENCIES / "build/resources") + '"',
        "-I" + str(SOURCE / "native"),
        "-I" + str(ROOT / "native/runtime"),
        *["-I" + str(path) for path in headers],
    ]
    common = [
        SOURCE / "native" / name
        for name in [
            "IVSupport.m",
            "IVRoomCalibration.m",
            "IVHMDDisplay.m",
            "IVTrackingIPC.m",
            "openxr/IVXRFrameTrace.m",
        ]
    ]
    helper = ROOT / "native/runtime/HeadsetFrameRate.m"
    xr_sources = [
        SOURCE / "native/openxr" / ("IVXR" + name + ".m")
        for name in ["Runtime", "Actions", "Graphics", "Dispatch", "Home", "Notifications"]
    ]
    frameworks = [
        "Cocoa",
        "IOKit",
        "CoreAudio",
        "Metal",
        "MetalKit",
        "QuartzCore",
        "AVFoundation",
        "CoreMedia",
        "IOSurface",
        "ImageIO",
    ]
    links = [bins / "libIndexCore.dylib", "-lbsm", "-lz", "-lc++", "-Wl,-rpath,@loader_path"]
    for framework in frameworks:
        links += ["-framework", framework]
    library = bins / "libVirtualRealityOpenXR.dylib"
    run(
        flags
        + [
            "-dynamiclib",
            *xr_sources,
            *common,
            helper,
            *links,
            "-Wl,-install_name,@rpath/libVirtualRealityOpenXR.dylib",
            "-o",
            library,
        ]
    )
    run(
        [
            "install_name_tool",
            "-change",
            "@rpath/libIndexCore.dylib",
            "@loader_path/libIndexCore.dylib",
            library,
        ]
    )
    run(["codesign", "--force", "--sign", "-", library])
    selection_test = OUT / "frame-rate-test"
    run(
        flags
        + [
            ROOT / "native/tests/frame_rate_test.m",
            *common,
            helper,
            *links,
            "-Wl,-rpath," + str(bins),
            "-o",
            selection_test,
        ]
    )
    run([selection_test])
    # Native bridge/compositor regression at every supported explicit rate,
    # including the new 80/144 Hz values, with isolated synthetic tracking.
    xr_test = OUT / "xr-test"
    unity_headers = Path("/Applications/Unity/Unity.app/Contents/PluginAPI")
    run(
        [
            "xcrun",
            "clang++",
            "-arch",
            "arm64",
            "-mmacosx-version-min=13.0",
            "-O2",
            "-std=c++17",
            "-fobjc-arc",
            "-I" + str(ROOT / "native/include"),
            "-I" + str(unity_headers),
            ROOT / "native/tests/xr_test.mm",
            ROOT / "native/build/libNativeH3VRXR.dylib",
            "-Wl,-rpath," + str(ROOT / "native/build"),
            "-framework",
            "Foundation",
            "-framework",
            "Metal",
            "-framework",
            "ImageIO",
            "-framework",
            "CoreGraphics",
            "-o",
            xr_test,
        ]
    )
    env = dict(os.environ)
    env.pop("VR_OPENXR_TARGET_HZ", None)
    env.pop("VR_OPENXR_FRAME_TRACE", None)
    for rate in [None, "80", "90", "120", "144"]:
        if rate is None:
            env.pop("VR_OPENXR_TARGET_HZ", None)
        else:
            env["VR_OPENXR_TARGET_HZ"] = rate
        run(
            [
                xr_test,
                bins / "libopenxr_loader.dylib",
                manifest,
                OUT / ("stereo-" + str(rate) + ".png"),
            ],
            env=env,
        )
    for path in [
        patch,
        helper,
        ROOT / "native/runtime/HeadsetFrameRate.h",
        ROOT / "native/tests/frame_rate_test.m",
        ROOT / "native/tests/xr_test.mm",
        Path(__file__),
    ]:
        inputs[str(path)] = digest(path)
    receipt = {
        "passed": True,
        "scope": "Game-owned runtime rebuild from pinned sources plus headset refresh patch",
        "baseProvenanceSha256": digest(BASE / "provenance.json"),
        "inputs": inputs,
        "binaries": {p.name: digest(p) for p in bins.glob("*.dylib")},
        "syntheticRegressionRates": ["default-90", 80, 90, 120, 144],
        "physicalModeWrites": False,
        "physicalPlaytest": False,
    }
    (OUT / "build.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print("PASS refresh-aware runtime candidate: " + str(library))


if __name__ == "__main__":
    main()
