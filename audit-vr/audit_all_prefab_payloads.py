#!/usr/bin/env python3
"""Stream the decoded MonoBehaviour payload audit without modifying Unity assets."""

import collections, gc, json, math, time
from pathlib import Path
from serialized_payload_audit import audit, OLD, ROOT

out = ROOT / "build/serialized-payload-audit"
out.mkdir(parents=True, exist_ok=True)
paths = sorted((OLD / "Assets").rglob("*.prefab"))
summary = {
    "total_prefabs": len(paths),
    "completed_prefabs": 0,
    "components": 0,
    "source_fields": 0,
    "script_binding_mismatches": 0,
    "file_errors": 0,
    "component_errors": 0,
    "differences": 0,
    "neutral_curve_additions": 0,
    "unclassified_differences": 0,
    "files_with_unclassified_differences": 0,
}
start = time.time()
kinds = collections.Counter()


def neutral(d):
    field = d["field"]
    kind = field.rsplit(".", 1)[-1]
    v = d.get("after")
    return (
        d["kind"] == "added"
        and ".m_Curve." in field
        and (
            (kind in ("weightedMode", "tangentMode") and v == 0)
            or (
                kind in ("inWeight", "outWeight")
                and isinstance(v, (float, int))
                and math.isclose(v, 1 / 3, rel_tol=1e-6)
            )
        )
    )


with (
    (out / "all-prefabs.jsonl").open("w") as stream,
    (out / "all-prefabs-issues.jsonl").open("w") as issues,
):
    for i, p in enumerate(paths, 1):
        rel = str(p.relative_to(OLD))
        try:
            result = audit(rel)
        except Exception as error:
            result = {"path": rel, "error": repr(error)}
            summary["file_errors"] += 1
            issues.write(json.dumps(result) + "\n")
            issues.flush()
        else:
            flagged = False
            for c in result["components"]:
                summary["components"] += 1
                summary["source_fields"] += c.get("source_fields", 0)
                if "error" in c:
                    summary["component_errors"] += 1
                    flagged = True
                if not c.get("script_matches_binding", False):
                    summary["script_binding_mismatches"] += 1
                    flagged = True
                for d in c.get("differences", []):
                    summary["differences"] += 1
                    kinds[d["kind"]] += 1
                    if neutral(d):
                        d["classification"] = "neutral-unity-curve-schema-addition"
                        summary["neutral_curve_additions"] += 1
                    else:
                        d["classification"] = "requires-review"
                        summary["unclassified_differences"] += 1
                        flagged = True
            if flagged:
                summary["files_with_unclassified_differences"] += 1
                issues.write(json.dumps(result) + "\n")
                issues.flush()
        stream.write(json.dumps(result) + "\n")
        summary["completed_prefabs"] = i
        if i % 100 == 0 or i == len(paths):
            stream.flush()
            gc.collect()
            summary["seconds"] = time.time() - start
            summary["difference_kinds"] = dict(kinds)
            (out / "all-prefabs-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
            print(json.dumps(summary), flush=True)
