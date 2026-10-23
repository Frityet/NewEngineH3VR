from pathlib import Path
import json, hashlib

root = Path(__file__).resolve().parents[1]
source = root / "shaders/packed-io-recovery/ExportedProject/Assets/Shader"
probe = root / "shader-probe/Assets/Resources/Shaders"
rows = []
restored = {
    Path(x["probe"]).name
    for x in json.load(open(root / "shaders/postprocessing-2017-match.json"))
    if "original_passes" in x
}
for p in source.glob("*.shader"):
    if p.name in restored:
        continue
    s = p.read_text()
    if "packedOutput" not in s:
        continue
    dest = probe / p.name
    before = dest.read_bytes()
    dest.write_text(s)
    rows.append(
        {
            "file": p.name,
            "original_sha256": hashlib.sha256(before).hexdigest(),
            "sha256": hashlib.sha256(dest.read_bytes()).hexdigest(),
        }
    )
(root / "shaders/packed-io-probe-changes.json").write_text(json.dumps(rows, indent=2))
print(len(rows))
