"""Stage exact-name, main-player unambiguous recovery, preserving existing Unity GUIDs."""

import json, shutil
from pathlib import Path

root = Path(__file__).resolve().parents[1]
rows = json.loads((root / "shaders/replacement-manifest.json").read_text())
report = json.loads((root / "shaders/compile-report.json").read_text())
byfile = {Path(r["path"]).name: r for r in report["shaders"]}
staged = []
for r in rows:
    if not r["selected"]:
        continue
    source = Path(r["selected"]["export_path"])
    target = Path(r["target"])
    before = target.with_suffix(".shader.meta").read_bytes()
    target.write_text(source.read_text().replace("Mode 0", "Mode Off"))
    assert before == target.with_suffix(".shader.meta").read_bytes()
    check = byfile[source.name]
    staged.append(
        {
            "name": r["name"],
            "target": str(target),
            "guid": r["meta_guid"],
            "source_file": r["selected"]["source_file"],
            "source_path_id": r["selected"]["source_path_id"],
            "compile_errors": [m for m in check["messages"] if m["severity"] == "Error"],
            "unsupported_opcodes": r["selected"]["unsupported_opcodes"],
            "variants": "first-selected original stage programs; full originals archived",
        }
    )
(root / "shaders/staged-baseline.json").write_text(json.dumps(staged, indent=2))
print("staged", len(staged), "with compile errors", sum(bool(r["compile_errors"]) for r in staged))
