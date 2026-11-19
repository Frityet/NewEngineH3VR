#!/usr/bin/env python3
"""Run an isolated MainMenu3 A/B in a signed, locally imported stock player."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("app", "runtime", "launcher", "out"):
        parser.add_argument("--" + key, required=True, type=Path)
    parser.add_argument("--render-percent", type=int, default=75)
    parser.add_argument("--survey-scene", action="append", default=[], help="Additional official scene name to render")
    parser.add_argument("--gear", action="store_true", help="Compare both stock wire controllers at fixed poses")
    args = parser.parse_args()
    here = Path(__file__).resolve().parent
    app, runtime, launcher, out = (p.resolve() for p in (args.app, args.runtime, args.launcher, args.out))
    if out.exists():
        parser.error("--out must be a fresh directory for the captures and temporary settings")
    data = app / "Contents/Resources/Data"
    receipt = data.parent / "nativeh3vr-build.json"
    settings = json.loads(receipt.read_text())
    assembly = "NativeH3VR.StockMenuAudit.dll"
    package = out / "mods/nativeh3vr.stock-menu-audit"
    subprocess.run(["dotnet", "build", str(here / "StockMenuRendering.csproj"), "-c", "Release",
                    "-p:NativeH3VRManaged=" + str(data / "Managed"), "--nologo", "-v:q"], check=True)
    package.mkdir(parents=True)
    dll = Path(shutil.copy2(here / "bin/Release/net472" / assembly, package))
    manifest = {"schemaVersion": 2, "id": package.name, "unityVersion": settings["unityVersion"],
                "buildTarget": "StandaloneOSX", "hostBuildSha256": digest(receipt),
                "assemblies": [{"file": assembly, "sha256": digest(dll)}],
                "entryPoints": [{"assembly": assembly, "type": "NativeH3VR.QA.StockMenuAudit", "method": "Initialize"}]}
    (package / "native-mod.json").write_text(json.dumps(manifest, indent=2))
    prefs = out / "state/macvr/preferences.json"
    prefs.parent.mkdir(parents=True)
    prefs.write_text(json.dumps({"render_resolution_percent": args.render_percent}))
    env = dict(os.environ, NATIVEH3VR_STOCK_MENU_AUDIT=str(out), NATIVEH3VR_STOCK_AUDIT_SCENES=",".join(args.survey_scene),
               NATIVEH3VR_STOCK_GEAR_AUDIT="1" if args.gear else "0")
    subprocess.run(["dotnet", str(launcher), "run", "--app", str(app), "--runtime", str(runtime),
                    "--mods-dir", str(package.parent), "--state-dir", str(out / "state"),
                    "--log-file", str(out / "player.log"), "--capture-file", str(out / "compositor.png"),
                    "--synthetic", "--timeout", str(150 + 90 * len(args.survey_scene))],
                   env=env, check=True)
    if not (out / "complete").is_file():
        raise RuntimeError("Stock eye audit did not complete. Inspect " + str(out / "player.log"))
    print(out / "menu.json")

if __name__ == "__main__":
    main()
