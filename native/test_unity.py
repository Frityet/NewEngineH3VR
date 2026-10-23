#!/usr/bin/env python3
"""Build/run isolated arm64 Unity proof, always synthetic/private home output."""

import pathlib, subprocess, os, argparse, json, hashlib, shutil

root = pathlib.Path(__file__).resolve().parent
project = root.parent / "vr-probe"
p = argparse.ArgumentParser()
p.add_argument("--game", action="store_true")
p.add_argument("--skip-build", action="store_true")
p.add_argument(
    "--refresh-scripts",
    action="store_true",
    help="Compile the same probe classes with native Mono into an existing isolated player; preserves its built scene.",
)
args = p.parse_args()
kind = "game" if args.game else "stereo"
out = project / "output"
out.mkdir(exist_ok=True)
if not args.skip_build and not args.refresh_scripts:
    subprocess.run(
        [
            "/Applications/Unity/Unity.app/Contents/MacOS/Unity",
            "-batchmode",
            "-nographics",
            "-noUpm",
            "-disable-assembly-updater",
            "-projectPath",
            str(project),
            "-executeMethod",
            "ProbeBuild.BuildGame" if args.game else "ProbeBuild.Build",
            "-logFile",
            str(out / (kind + "-build.log")),
        ],
        check=True,
        timeout=180,
    )
app = out / ("NativeGameXRProbe.app" if args.game else "NativeXRProbe.app")
exe = app / "Contents/MacOS/Native H3VR Metal Probe"
if args.refresh_scripts:
    managed = app / "Contents/Resources/Data/Managed"
    refs = [
        *pathlib.Path("/Applications/Unity/Unity.app/Contents/Managed/UnityEngine").glob("*.dll"),
        *(project / "Assets/Plugins").glob("*.dll"),
    ]
    subprocess.run(
        [
            "/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/bin/mcs",
            "-target:library",
            "-out:" + str(managed / "Assembly-CSharp.dll"),
            *["-r:" + str(r) for r in refs],
            *[str(f) for f in (project / "Assets").glob("*.cs")],
        ],
        check=True,
    )
    for f in (project / "Assets/Plugins").glob("*"):
        if f.suffix == ".dll":
            shutil.copy2(f, managed / f.name)
        if f.suffix == ".dylib":
            shutil.copy2(f, app / "Contents/Plugins" / f.name)
env = dict(os.environ)
env["NATIVEH3VR_RUNTIME"] = str(root / "build/runtime")
env["NATIVEH3VR_ACTIONS"] = str(
    pathlib.Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/actions.json"
)
env["VR_OPENXR_CAPTURE"] = str(out / (kind + "-stereo.png"))
if args.game:
    env["NATIVEH3VR_OPENXR"] = "synthetic"
else:
    env.pop("NATIVEH3VR_OPENXR", None)
for old in out.glob(kind + "-stereo.png*"):
    if old.is_file():
        old.unlink()
log = out / (kind + "-player.log")
subprocess.run([str(exe), "-force-metal", "-logFile", str(log)], env=env, check=True, timeout=60)
content = log.read_text()
marker = "NATIVE_STEAMVR_PROBE_PASS" if args.game else "NATIVE_XR_PROBE_PASS"
assert marker in content, content[-5000:]
assert "DllNotFoundException" not in content and "EntryPointNotFoundException" not in content, (
    content[-5000:]
)
if not args.game:
    assert "NATIVE_INPUT_PROBE_PASS" in content
    subprocess.run(
        [
            str(root.parent / "audit-assets/.venv/bin/python"),
            str(root / "verify_eye_capture.py"),
            env["VR_OPENXR_CAPTURE"],
            *(["--linear-source"] if env.get("NATIVEH3VR_PROBE_LINEAR") == "1" else []),
        ],
        check=True,
    )
if args.game:
    assert (
        "NATIVE_SCENE_HANDOFF_PASS" in content
        and "NATIVE_COMPOSITOR_FADE_PASS" in content
        and "NATIVE_FOCUS_INPUT_PASS" in content
    )
    if env.get("NATIVEH3VR_DIAGNOSTIC_INPUT") == "1":
        assert "NATIVE_DIAGNOSTIC_ACTIONS_PASS" in content and "NATIVE_INDEX_GRAB_PASS" in content
    subprocess.run(
        [
            str(root.parent / "audit-assets/.venv/bin/python"),
            str(root / "verify_compositor_capture.py"),
            env["VR_OPENXR_CAPTURE"],
        ],
        check=True,
    )
architecture = subprocess.check_output(["file", str(exe)], text=True).strip()
assert "arm64" in architecture
record = {
    "probe_script_refresh": args.refresh_scripts,
    "mode": "synthetic-headless-private-home",
    "architecture": architecture,
    "log": str(log),
    "capture": env["VR_OPENXR_CAPTURE"],
    "markers": [
        line
        for line in content.splitlines()
        if "_PASS" in line or "NATIVE_STEAMVR_INITIALIZED" in line
    ],
    "pinned_runtime_provenance": str(root / "build/runtime/provenance.json"),
    "plugin_hashes": {
        str(f.relative_to(app)): hashlib.sha256(f.read_bytes()).hexdigest()
        for f in (app / "Contents/Plugins").glob("*.dylib")
    },
}
(out / (kind + "-verification.json")).write_text(json.dumps(record, indent=2) + "\n")
print(json.dumps(record, indent=2))
