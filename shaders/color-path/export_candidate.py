"""Reproducibly export original selected H3VR shader variants with the corrected emitter."""

from pathlib import Path
import argparse, hashlib, json, os, subprocess, time, urllib.parse, urllib.request

root = Path(__file__).resolve().parents[2]
ap = argparse.ArgumentParser()
ap.add_argument("--port", type=int, default=58981)
ap.add_argument("--name", default="semantic-v3-swizzle")
a = ap.parse_args()
selection = json.loads((root / "shaders/semantic-v2-selection.json").read_text())
dest = root / "shaders/color-path" / a.name
manifest = root / "shaders/color-path" / (a.name + "-identities.tsv")
assert not manifest.exists(), manifest
assert not dest.exists() or not any(dest.iterdir()), dest
dest.mkdir(parents=True, exist_ok=True)
dll = (
    root
    / ".tools/assetripper-source-1.0.15/Source/0Bins/AssetRipper.GUI.Free/Debug/AssetRipper.GUI.Free.dll"
)
assert dll.exists(), dll
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
assembly = dll.parent
hashes = {p.name: sha(p) for p in assembly.glob("AssetRipper*.dll")}
env = os.environ.copy()
env.update(
    DOTNET_ROLL_FORWARD="Major",
    H3VR_VARIANT_SHADERS="|".join(selection["full_variant_names"]),
    H3VR_SHADER_MANIFEST=str(manifest),
)
base = f"http://127.0.0.1:{a.port}/"
with (root / "shaders/color-path" / (a.name + "-server.log")).open("w") as log:
    child = subprocess.Popen(
        ["/opt/homebrew/bin/dotnet", str(dll), "--port", str(a.port), "--launch-browser", "false"],
        env=env,
        stdout=log,
        stderr=subprocess.STDOUT,
    )
    try:
        for _ in range(300):
            assert child.poll() is None, "Exporter exited; inspect server log"
            try:
                with urllib.request.urlopen(base, timeout=1):
                    break
            except OSError:
                time.sleep(0.2)
        else:
            raise RuntimeError("Exporter did not become ready")
        settings = json.loads((root / "audit-assets/assetripper-settings-1.0.15.json").read_text())
        inputs = [
            ("Path", str(p)) for p in sorted((root / "shaders/semantic-v2-inputs").glob("*.assets"))
        ]
        assert len(inputs) >= 21, len(inputs)
        for endpoint, fields in [
            ("Settings/Update", settings),
            ("LoadFile", inputs),
            ("Export", {"Path": str(dest)}),
        ]:
            with urllib.request.urlopen(
                urllib.request.Request(
                    base + endpoint, data=urllib.parse.urlencode(fields).encode()
                ),
                timeout=3600,
            ) as response:
                print(endpoint, response.status, flush=True)
    finally:
        child.terminate()
        child.wait(timeout=30)
assert all(sha(assembly / name) == value for name, value in hashes.items()), (
    "Exporter assembly changed"
)
receipt = {
    "selection_sha256": sha(root / "shaders/semantic-v2-selection.json"),
    "assembly_sha256": hashes,
    "identity_manifest_sha256": sha(manifest),
    "input_count": len(inputs),
    "selected_object_count": len(selection["objects"]),
    "full_variant_names": selection["full_variant_names"],
    "export_path": str(dest / "ExportedProject"),
}
(root / "shaders/color-path" / (a.name + "-receipt.json")).write_text(json.dumps(receipt, indent=2))
print(json.dumps({k: v for k, v in receipt.items() if k != "full_variant_names"}, indent=2))
