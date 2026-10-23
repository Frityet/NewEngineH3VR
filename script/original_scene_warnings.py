"""Recognize only original null-script warnings from a hash-verified built scene."""

from collections import Counter
import json
from pathlib import Path
import re
from audit_native_app import sha256

ROOT = Path(__file__).resolve().parents[1]
MISSING = re.compile(
    r"^The referenced script on this Behaviour \(Game Object '(.+)'\) is missing!$"
)


def load_baseline(app, scene):
    baseline_path = ROOT / "build/scene-integrity/missing-script-baseline.json"
    proof_path = ROOT / "build/scene-integrity/full-player-script-proof.json"
    inventory_path = proof_path.with_name(proof_path.stem + "-managed-types.json")
    baseline = json.loads(baseline_path.read_text())
    rows = [row for row in baseline["scenes"] if Path(row["path"]).stem == scene]
    if len(rows) != 1 or not baseline.get("passed") or baseline.get("errors"):
        raise ValueError("Original scene baseline is missing or ambiguous: " + scene)
    original = rows[0]
    if not original["baseline"]:
        return {"scene": scene, "names": {}, "proof": None}
    proof = json.loads(proof_path.read_text())
    if (
        not proof.get("passed")
        or proof["baselineSha256"] != sha256(baseline_path)
        or proof["managedInventorySha256"] != sha256(inventory_path)
    ):
        raise ValueError("Original null-script proof is stale")
    rows = [row for row in proof["scenes"] if row["scene"] == original["path"]]
    if len(rows) != 1 or not rows[0]["passed"] or rows[0]["errors"]:
        raise ValueError("Built scene has no successful original-null mapping")
    built = rows[0]
    data = app / "Contents/Resources/Data"
    if proof["buildSettingsFileSha256"] != sha256(data / "globalgamemanagers") or built[
        "playerSha256"
    ] != sha256(data / built["playerFile"]):
        raise ValueError("Packaged scene differs from its original-null proof")
    for record in json.loads(inventory_path.read_text()):
        if record["sha256"] != sha256(data / "Managed" / record["path"]):
            raise ValueError(
                "Packaged managed assembly differs from the scene proof: " + record["path"]
            )
    slots = {
        (row["gameObjectFileID"], row["componentIndex"], row["componentFileID"]): row[
            "gameObjectName"
        ]
        for row in original["baseline"]
    }
    matched = [
        (row["sourceGameObject"], row["componentIndex"], row["originalComponent"])
        for row in built["preservedNullComponents"]
    ]
    if (
        len(set(matched)) != len(matched)
        or not set(matched) <= set(slots)
        or len(matched) != built["originalNullComponents"] - built["editorOnlyOriginalNulls"]
    ):
        raise ValueError("Built null-script identities do not match the original scene")
    return {
        "scene": scene,
        "names": dict(Counter(slots[key] for key in matched)),
        "proof": str(proof_path),
        "proofSha256": sha256(proof_path),
        "playerFile": built["playerFile"],
        "playerSha256": built["playerSha256"],
    }


def classify(text, issue_pattern, baseline=None):
    """Only consume exact name/count matches during this original scene's load.

    Unity's warning lacks component IDs. The separate scene proof binds the
    original null slots, complete component topology and managed types to the
    exact shipped scene/assemblies. All warnings are retained in the run report.
    """
    baseline = baseline or {"scene": None, "names": {}}
    remaining = Counter(baseline["names"])
    issues, original = [], []
    loading = False
    for line in text.splitlines():
        if line == "NATIVE_GAMEPLAY_STAGE original-scene-transition " + str(baseline["scene"]):
            loading = True
        elif line.startswith("NATIVE_GAMEPLAY_STAGE scene-loaded "):
            loading = False
        if not issue_pattern.search(line):
            continue
        match = MISSING.fullmatch(line)
        if loading and match and remaining[match[1]] > 0:
            remaining[match[1]] -= 1
            original.append(line)
        else:
            issues.append(line)
    return (
        list(dict.fromkeys(issues)),
        original,
        {name: count for name, count in remaining.items() if count},
    )
