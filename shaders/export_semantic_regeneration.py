"""Run consolidated exact-identity regeneration after the corrected exporter handoff.
Never overwrites a prior export. Public/historical source acceptance is handled by the
selection manifest; this command does not stage any Unity project files.
"""

from pathlib import Path
import argparse, json, os, subprocess, time, urllib.parse, urllib.request, hashlib

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--port", type=int, default=58968)
args = parser.parse_args()
selection = json.loads((root / "shaders/semantic-v2-selection.json").read_text())
dest = root / "shaders/semantic-v2-recovery"
manifest = root / "shaders/semantic-v2-export-identities.tsv"
assert not manifest.exists()
assert not dest.exists() or not any(dest.iterdir())
dest.mkdir(exist_ok=True)
dll = (
    root
    / ".tools/assetripper-source-1.0.15/Source/0Bins/AssetRipper.GUI.Free/Release/AssetRipper.GUI.Free.dll"
)
hashfile = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
assembly_dir = dll.parent
hashes = {p.name: hashfile(p) for p in assembly_dir.glob("AssetRipper*.dll")}
env = os.environ.copy()
env.update(
    DOTNET_ROLL_FORWARD="Major",
    H3VR_VARIANT_SHADERS="|".join(selection["full_variant_names"]),
    H3VR_SHADER_MANIFEST=str(manifest),
)
base = f"http://127.0.0.1:{args.port}/"
with (root / "shaders/semantic-v2-server.log").open("w") as log:
    child = subprocess.Popen(
        [
            "/opt/homebrew/bin/dotnet",
            str(dll),
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
        for _ in range(100):
            assert child.poll() is None
            try:
                with urllib.request.urlopen(base, timeout=1):
                    break
            except OSError:
                time.sleep(0.1)
        else:
            raise RuntimeError("Exporter not ready")
        settings = json.loads((root / "audit-assets/assetripper-settings-1.0.15.json").read_text())
        inputs = [
            ("Path", str(p)) for p in sorted((root / "shaders/semantic-v2-inputs").glob("*.assets"))
        ]
        for endpoint, fields in [
            ("Settings/Update", settings),
            ("LoadFile", inputs),
            ("Export", {"Path": str(dest)}),
        ]:
            with urllib.request.urlopen(
                urllib.request.Request(
                    base + endpoint, data=urllib.parse.urlencode(fields).encode()
                ),
                timeout=1800,
            ) as response:
                print(endpoint, response.status, flush=True)
    finally:
        child.terminate()
        child.wait(timeout=30)
assert all(hashfile(assembly_dir / name) == sha for name, sha in hashes.items()), (
    "Exporter assemblies changed during recovery"
)
(root / "shaders/semantic-v2-export-receipt.json").write_text(
    json.dumps(
        {
            "selection_sha256": hashfile(root / "shaders/semantic-v2-selection.json"),
            "export_assembly_sha256": hashes,
            "identity_manifest_sha256": hashfile(manifest),
            "original_identities": len(selection["objects"]),
            "full_variant_names": selection["full_variant_names"],
        },
        indent=2,
    )
)
