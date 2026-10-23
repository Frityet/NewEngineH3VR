#!/usr/bin/env python3
"""Disable costly lossy recompression for experimental recovery builds.
Keeps dimensions, mipmaps, color space and sampler settings intact.
Original exports retain compression settings for later optimization.
"""

from pathlib import Path
import argparse
import json
import re

root = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument("project", nargs="?", type=Path, default=root / "unity")
args = p.parse_args()
project = args.project.resolve()
if project in [root / "recovered/ExportedProject", root / "recovered-full/ExportedProject"]:
    raise SystemExit("Refusing to modify pristine recovered settings")
changed = []
for meta in (project / "Assets").rglob("*.meta"):
    text = meta.read_text(errors="surrogateescape")
    if "\nTextureImporter:" not in text:
        continue
    new = re.sub(r"(?m)^(\s+textureCompression:) \d+$", r"\1 0", text)
    new = re.sub(r"(?m)^(\s+forceMaximumCompressionQuality_BC6H_BC7:) 1$", r"\1 0", new)
    if new != text:
        meta.write_text(new, errors="surrogateescape")
        changed.append(str(meta.relative_to(project)))
report = {
    "project": str(project),
    "mode": "uncompressed-debug",
    "changed": len(changed),
    "files": changed,
}
(root / "build/texture-import-mode.json").write_text(json.dumps(report, indent=2) + "\n")
print(f"Set {len(changed)} textures to uncompressed; resolution and sampler settings preserved.")
