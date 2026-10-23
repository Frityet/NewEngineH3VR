"""Run a bounded shader-only recovery server for one frozen extraction batch."""

from pathlib import Path
import argparse, json, os, subprocess, time, urllib.parse, urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("batch", choices=["residual", "remaining", "runtime", "uncovered"])
parser.add_argument("--port", type=int, default=58962)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
selection = json.loads((root / f"shaders/{args.batch}-recovery-selection.json").read_text())
destination = root / f"shaders/{args.batch}-variant-recovery"
assert not destination.exists() or not any(destination.iterdir()), (
    "Refusing to overwrite recovered evidence"
)
destination.mkdir(exist_ok=True)
env = os.environ.copy()
env["DOTNET_ROLL_FORWARD"] = "Major"
env["H3VR_VARIANT_SHADERS"] = "|".join(selection["names"])
env["H3VR_SHADER_MANIFEST"] = str(root / f"shaders/{args.batch}-export-identities.tsv")
assert not Path(env["H3VR_SHADER_MANIFEST"]).exists(), (
    "Refusing to append to an earlier identity manifest"
)
base = f"http://127.0.0.1:{args.port}/"
with (root / f"shaders/{args.batch}-variant-server.log").open("w") as log:
    child = subprocess.Popen(
        [
            "/opt/homebrew/bin/dotnet",
            str(
                root
                / ".tools/assetripper-source-1.0.15/Source/0Bins/AssetRipper.GUI.Free/Release/AssetRipper.GUI.Free.dll"
            ),
            "--port",
            str(args.port),
            "--launch-browser",
            "false",
        ],
        env=env,
        stdout=log,
        stderr=subprocess.STDOUT,
    )
    try:
        for attempt in range(100):
            assert child.poll() is None, "Recovery server exited before readiness"
            try:
                with urllib.request.urlopen(base, timeout=1):
                    break
            except OSError:
                time.sleep(0.1)
        else:
            raise RuntimeError("Recovery server did not become ready")
        settings = json.loads((root / "audit-assets/assetripper-settings-1.0.15.json").read_text())
        inputs = [
            ("Path", str(p))
            for p in sorted((root / f"shaders/{args.batch}-variant-inputs").glob("*.assets"))
        ]
        for endpoint, fields in [
            ("Settings/Update", settings),
            ("LoadFile", inputs),
            ("Export", {"Path": str(destination)}),
        ]:
            request = urllib.request.Request(
                base + endpoint, data=urllib.parse.urlencode(fields).encode()
            )
            with urllib.request.urlopen(request, timeout=1200) as response:
                print(endpoint, response.status, flush=True)
    finally:
        child.terminate()
        child.wait(timeout=30)
