"""Pin the independently checked FinalPass source and exact native-tested includes."""

from pathlib import Path
import json, re, hashlib, shutil
from verify_shader_proof_logs import require_clean

root = Path(__file__).resolve().parents[1]
require_clean(
    root / "shaders/unity2021-finalpass-complete-repair-draw.log",
    root / "shaders/unity2021-finalpass-mask-reference-draw.log",
    root / "shaders/unity2021-critical-corrected-build.log",
    root / "shaders/critical-corrected-native-player.log",
)
pixel = json.loads((root / "shaders/finalpass-reference-pixel-comparison.json").read_text())
assert (
    len(pixel["rows"]) == 144
    and not pixel["errors"]
    and max(r["maxAbs"] for r in pixel["rows"]) < 1 / 255
)
native = json.loads((root / "shaders/critical-corrected-native-gpu-report.json").read_text())
assert native["passed"] and native["draws"] == 2108
source = (
    root / "critical-probe/Assets/Resources/OriginalSources/Hidden_PostProcessing_FinalPass.shader"
)
dest = root / "shaders/finalpass-reference-validated"
assert not (dest / "manifest.json").exists(), "Refusing to replace pinned independent oracle"
dest.mkdir(exist_ok=True)
files = [(source, Path(source.name))]
for rel in sorted(set(re.findall(r'#include "(Assets/NativeShaders/[^\"]+)"', source.read_text()))):
    files.append((root / "critical-probe" / rel, Path(rel)))
rows = []
for p, rel in files:
    out = dest / rel
    out.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(p, out)
    rows.append({"path": str(rel), "sha256": hashlib.sha256(out.read_bytes()).hexdigest()})
(dest / "manifest.json").write_text(
    json.dumps(
        {
            "files": rows,
            "native_proof": "shaders/critical-corrected-native-gpu-report.json",
            "pixel_comparison": "shaders/finalpass-reference-pixel-comparison.json",
            "reference_commit": "23dfcb6f407f74704d8e23a52b21740ecb4baa3d",
            "pixel_max_abs": max(r["maxAbs"] for r in pixel["rows"]),
            "pixel_max_rmse": max(r["rmse"] for r in pixel["rows"]),
            "limitations": "Pixel comparison covers144 deterministic cases against historical public source, not all possible inputs or Windows-driver binary equivalence. Inactive single-pass stereo source only has Editor execution proof.",
        },
        indent=2,
    )
)
print("Pinned", len(rows), "FinalPass files")
