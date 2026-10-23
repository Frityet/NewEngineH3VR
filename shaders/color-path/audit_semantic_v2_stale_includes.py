"""Read-only reachability audit for stale SemanticV2Variants include bodies.

Roots are every .shader and .compute asset. Quoted or angle-bracket includes are
followed transitively, resolving Assets/... from unity-full and other paths relative
to their including file. This script only writes its JSON report under shaders/.
"""

from pathlib import Path
import collections
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[2]
UNITY = ROOT / "unity-full"
ASSETS = UNITY / "Assets"
NAMESPACE = ASSETS / "NativeShaders/SemanticV2Variants"
COLOR = ROOT / "shaders/color-path"
RECEIPT = COLOR / "semantic-v3-stage-receipt.json"
PLAN = COLOR / "semantic-v3-stage-plan.json"
PACKAGE = COLOR / "semantic-v3-packaged/manifest.json"
REPORT = COLOR / "semantic-v3-stale-includes-report.json"

sha = lambda data: hashlib.sha256(data).hexdigest()
include_re = re.compile(r'^\s*#\s*include(?:_with_pragmas)?\s*[<"]([^">]+)[">]', re.M)
shader_suffixes = {".shader", ".compute", ".cginc", ".hlsl"}

receipt = json.loads(RECEIPT.read_text())
plan_bytes = PLAN.read_bytes()
plan = json.loads(plan_bytes)
package = json.loads(PACKAGE.read_text())
assert receipt["staged"] is True and receipt["bundle_or_player_files_touched"] is False
assert receipt["plan_sha256"] == sha(plan_bytes), "stage receipt and saved plan disagree"

roots = sorted(
    p for p in ASSETS.rglob("*") if p.is_file() and p.suffix.lower() in {".shader", ".compute"}
)
reached = set()
stack = list(roots)
unresolved = collections.Counter()
directive_count = 0

while stack:
    path = stack.pop().resolve()
    if path in reached or not path.is_file():
        continue
    reached.add(path)
    try:
        text = path.read_text(errors="replace")
    except OSError:
        continue
    for reference in include_re.findall(text):
        directive_count += 1
        candidate = (
            UNITY / reference if reference.startswith("Assets/") else path.parent / reference
        ).resolve()
        if candidate.is_file():
            if candidate.suffix.lower() in shader_suffixes:
                stack.append(candidate)
            else:
                reached.add(candidate)
        else:
            unresolved[reference] += 1

all_namespace = sorted(NAMESPACE.glob("*.cginc"))
stale = [p for p in all_namespace if p.resolve() not in reached]
candidate_dependencies = set(package["dependencies"])
reachable_dependencies = {
    p.relative_to(UNITY).as_posix() for p in all_namespace if p.resolve() in reached
}
assert reachable_dependencies == candidate_dependencies, (
    len(reachable_dependencies),
    len(candidate_dependencies),
    len(reachable_dependencies - candidate_dependencies),
    len(candidate_dependencies - reachable_dependencies),
)
assert not any(
    name.startswith("Assets/NativeShaders/SemanticV2Variants/") for name in unresolved
), "unresolved target-namespace include"

rows = []
for path in stale:
    meta = Path(str(path) + ".meta")
    assert meta.is_file(), f"stale include is missing its Unity metadata: {meta}"
    rows.append(
        {
            "path": path.relative_to(UNITY).as_posix(),
            "sha256": sha(path.read_bytes()),
            "size_bytes": path.stat().st_size,
            "meta_path": meta.relative_to(UNITY).as_posix(),
            "meta_sha256": sha(meta.read_bytes()),
            "meta_size_bytes": meta.stat().st_size,
        }
    )

report = {
    "read_only_audit": True,
    "staged_receipt_sha256": sha(RECEIPT.read_bytes()),
    "stage_plan_sha256": receipt["plan_sha256"],
    "roots": {
        "shader": sum(p.suffix.lower() == ".shader" for p in roots),
        "compute": sum(p.suffix.lower() == ".compute" for p in roots),
    },
    "reachable_file_count": len(reached),
    "include_directive_count": directive_count,
    "unresolved_external_directives": sum(unresolved.values()),
    "common_unresolved_external_names": unresolved.most_common(20),
    "namespace_cginc_count": len(all_namespace),
    "reachable_candidate_cginc_count": len(reachable_dependencies),
    "stale_unreferenced_cginc_count": len(rows),
    "stale_cginc_bytes": sum(r["size_bytes"] for r in rows),
    "stale_meta_bytes": sum(r["meta_size_bytes"] for r in rows),
    "stale_combined_bytes": sum(r["size_bytes"] + r["meta_size_bytes"] for r in rows),
    "method_limits": [
        "Reachability is conservative over all literal include directives, including conditional branches.",
        "Missing engine-provided includes such as UnityCG.cginc are counted as unresolved external directives; none are in the SemanticV2Variants namespace.",
        "This is an inventory only. It does not delete assets or establish that cleanup is safe before accepted bundle validation.",
    ],
    "stale_files": rows,
}
REPORT.write_text(json.dumps(report, indent=2) + "\n")
print(
    f"Read-only: {len(rows)} unreferenced cginc + metas; "
    f"{report['stale_combined_bytes']} bytes; report {REPORT.relative_to(ROOT)}"
)
