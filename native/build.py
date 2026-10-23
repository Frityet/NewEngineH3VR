#!/usr/bin/env python3
"""Build only this directory. Reads source/header dependencies from the runtime."""

import argparse
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument("--runtime", type=pathlib.Path, default=ROOT.parents[1] / "ValveIndexMacOS")
parser.add_argument("--test", action="store_true")
args = parser.parse_args()
runtime = args.runtime.resolve()
test_runtime = (
    ROOT / "build/runtime"
    if (ROOT / "build/runtime/build/openxr/virtual-reality.json").exists()
    else runtime
)
build = ROOT / "build"
build.mkdir(exist_ok=True)
includes = [
    "-I" + str(ROOT / "include"),
    "-I" + str(ROOT / "src"),
    "-I" + str(runtime / "native"),
    "-I" + str(runtime / "extern/openxr/include"),
]
flags = [
    "xcrun",
    "clang",
    "-arch",
    "arm64",
    "-mmacosx-version-min=13.0",
    "-O2",
    "-Wall",
    "-Wextra",
    "-Werror",
]


def run(command):
    print(" ".join(str(x) for x in command), flush=True)
    subprocess.run([str(x) for x in command], check=True, timeout=120)


xr_library = build / "libNativeH3VRXR.dylib"
unity_headers = pathlib.Path("/Applications/Unity/Unity.app/Contents/PluginAPI")
run(
    flags
    + includes
    + [
        "-I" + str(unity_headers),
        "-std=c++17",
        "-fobjc-arc",
        "-fvisibility=hidden",
        "-dynamiclib",
        ROOT / "src/xr_bridge.mm",
        "-framework",
        "Foundation",
        "-framework",
        "Metal",
        "-lc++",
        "-install_name",
        "@rpath/libNativeH3VRXR.dylib",
        "-o",
        xr_library,
    ]
)
library = build / "libNativeH3VRTracking.dylib"
run(
    flags
    + includes
    + [
        "-fobjc-arc",
        "-fvisibility=hidden",
        "-dynamiclib",
        ROOT / "src/tracking.m",
        runtime / "native/IVRoomCalibration.m",
        xr_library,
        "-Wl,-rpath,@loader_path",
        "-framework",
        "Foundation",
        "-install_name",
        "@rpath/libNativeH3VRTracking.dylib",
        "-o",
        library,
    ]
)
for name, source, extra in [
    ("tracking-probe", ROOT / "tools/tracking_probe.c", []),
    ("tracking-test", ROOT / "tests/tracking_test.m", ["-fobjc-arc", "-framework", "Foundation"]),
]:
    run(flags + includes + [source, library, "-Wl,-rpath,@loader_path", *extra, "-o", build / name])
run(flags + includes + [ROOT / "tools/openxr_capabilities.c", "-o", build / "openxr-capabilities"])
run(
    flags
    + includes
    + [
        "-I" + str(unity_headers),
        "-std=c++17",
        "-fobjc-arc",
        ROOT / "tests/xr_test.mm",
        xr_library,
        "-Wl,-rpath,@loader_path",
        "-framework",
        "Foundation",
        "-framework",
        "Metal",
        "-framework",
        "ImageIO",
        "-framework",
        "CoreGraphics",
        "-lc++",
        "-o",
        build / "xr-test",
    ]
)
run(["codesign", "--force", "--sign", "-", library])
run(["codesign", "--force", "--sign", "-", xr_library])
sources = [
    ROOT / "include/nativeh3vr_xr.h",
    ROOT / "src/xr_bridge.mm",
    ROOT / "managed/NativeH3VRXR.cs",
    unity_headers / "IUnityGraphicsMetal.h",
    unity_headers / "IUnityInterface.h",
    runtime / "extern/openxr/include/openxr/openxr.h",
    runtime / "extern/openxr/include/openxr/openxr_platform.h",
    ROOT / "include/nativeh3vr_tracking.h",
    ROOT / "src/broker_protocol.h",
    ROOT / "src/tracking.m",
    runtime / "native/index_core.h",
    runtime / "native/IVTrackingIPC.m",
    runtime / "native/IVRoomCalibration.m",
    runtime / "native/IVRoomCalibration.h",
    runtime / "native/openxr/IVXRControllerPoses.h",
]
provenance = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources}
(build / "source-sha256.json").write_text(json.dumps(provenance, indent=2) + "\n")
run(
    [
        "/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/bin/mcs",
        "-target:library",
        "-out:" + str(build / "NativeH3VR.XR.Managed.dll"),
        "-r:/Applications/Unity/Unity.app/Contents/Managed/UnityEngine/UnityEngine.CoreModule.dll",
        "-r:/Applications/Unity/Unity.app/Contents/Managed/UnityEngine/UnityEngine.IMGUIModule.dll",
        ROOT / "managed/NativeH3VRXR.cs",
    ]
)
run(
    [
        "file",
        xr_library,
        build / "xr-test",
        library,
        build / "tracking-probe",
        build / "tracking-test",
        build / "openxr-capabilities",
    ]
)
if args.test:
    run(
        flags
        + includes
        + [
            "-I" + str(unity_headers),
            "-std=c++17",
            "-fobjc-arc",
            ROOT / "tests/eye_view_query_test.mm",
            "-framework",
            "Foundation",
            "-framework",
            "Metal",
            "-lc++",
            "-o",
            build / "eye-view-query-test",
        ]
    )
    run([build / "eye-view-query-test"])
    run([build / "tracking-test", build])
    run(
        [
            build / "openxr-capabilities",
            test_runtime / "build/bin/libopenxr_loader.dylib",
            test_runtime / "build/openxr/virtual-reality.json",
        ]
    )

if args.test:
    run(
        [
            build / "xr-test",
            test_runtime / "build/bin/libopenxr_loader.dylib",
            test_runtime / "build/openxr/virtual-reality.json",
            build / "xr-stereo.png",
        ]
    )
