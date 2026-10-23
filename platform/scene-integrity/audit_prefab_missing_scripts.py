#!/usr/bin/env python3
"""Record every pristine prefab's exact missing-script slots; inputs read-only."""

import gc
import hashlib
import json
from pathlib import Path
import re
from audit_missing_scripts import ROOT, yaml_nulls, binary_nulls, records, identity

pristine = ROOT / "recovered-full/ExportedProject"
working = ROOT / "unity-full"
rows, errors = [], []
warnings = {}
for name in re.findall(
    r"Script attached to '(.*?)' in scene '' is missing",
    (ROOT / "build/native-bundles-build.log").read_text(),
):
    warnings[name] = warnings.get(name, 0) + 1
for path in sorted((pristine / "Assets").rglob("*.prefab")):
    relative = str(path.relative_to(pristine))
    objects, nulls = yaml_nulls(path)
    baseline, unattached = records(objects, nulls)
    row = {
        "path": relative,
        "guid": re.search(r"^guid: (\w+)$", path.with_suffix(".prefab.meta").read_text(), re.M)[1],
        "expectedMissingComponentCount": len(baseline),
        "baseline": baseline,
        "unattachedNullScriptIDs": unattached,
    }
    if baseline or unattached:
        target = working / relative
        with target.open("rb") as stream:
            is_yaml = stream.read(5) == b"%YAML"
        if is_yaml:
            wobjects, wnulls = yaml_nulls(target)
        else:
            wobjects, wnulls, _ = binary_nulls(target)
        current, wu = records(wobjects, wnulls)
        row["workingIdentitiesMatch"] = {identity(r) for r in baseline} == {
            identity(r) for r in current
        } and unattached == wu
        row["pristineSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        if not row["workingIdentitiesMatch"]:
            errors.append(relative)
    # Name-only warnings cannot identify a prefab uniquely; retain all candidates.
    row["warningCandidates"] = [
        {
            "gameObjectFileID": gid,
            "name": obj["name"],
            "warningCountByName": warnings[obj["name"]],
            "pristineNullSlots": [
                r["componentIndex"] for r in baseline if r["gameObjectFileID"] == gid
            ],
        }
        for gid, obj in objects.items()
        if obj["name"] in warnings
    ]
    rows.append(row)
    if len(rows) % 1000 == 0:
        print("Prefabs:", len(rows), flush=True)
        gc.collect()
report = {
    "readOnly": True,
    "baselineSource": "pristine AssetRipper export; prefab local IDs are export IDs",
    "prefabCount": len(rows),
    "expectedMissingComponentCount": sum(r["expectedMissingComponentCount"] for r in rows),
    "prefabsWithMissingComponents": sum(r["expectedMissingComponentCount"] > 0 for r in rows),
    "errors": errors,
    "passed": not errors,
    "prefabs": rows,
}
out = ROOT / "build/scene-integrity/prefab-missing-script-baseline.json"
out.write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps({k: v for k, v in report.items() if k != "prefabs"}, indent=2))
