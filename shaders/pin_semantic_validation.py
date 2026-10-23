"""Pin exact native-tested regenerated bodies after both strict native proof gates."""

from pathlib import Path
import json, re, hashlib, shutil
from verify_shader_proof_logs import require_clean

root = Path(__file__).resolve().parents[1]
package = root / "shaders/semantic-v2-shadow-packaged"
out = root / "shaders/semantic-v2-validated"
assert not (out / "manifest.json").exists(), "Never replace a pinned validation snapshot"
load = lambda p: json.loads((root / p).read_text())


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def upgraded(s):
    for a, b in [
        ("glstate_matrix_projection", "UNITY_MATRIX_P"),
        ("glstate_matrix_modelview0", "UNITY_MATRIX_MV"),
        ("glstate_matrix_mvp", "UNITY_MATRIX_MVP"),
    ]:
        s = s.replace(a, b)
    return s


def compare(raw, tested):
    a = raw.read_text()
    b = tested.read_text()
    if a == b:
        return False
    for old, new in [
        ("glstate_matrix_projection", "UNITY_MATRIX_P"),
        ("glstate_matrix_modelview0", "UNITY_MATRIX_MV"),
        ("glstate_matrix_mvp", "UNITY_MATRIX_MVP"),
    ]:
        note = f"// Upgrade NOTE: replaced '{old}' with '{new}'\n"
        a = a.replace(note, "")
        b = b.replace(note, "")
    assert b.lstrip("\n") == upgraded(a).lstrip("\n"), str(tested)
    return True


proofs = {}
for batch in ["semantic-optics", "semantic-materials"]:
    proof = load("shaders/" + batch + "-execution-proof.json")
    assert (
        proof["passed"]
        and proof["retentionMissing"] == 0
        and proof["nativeArchitecture"] == "arm64"
    )
    require_clean(
        root / ("shaders/" + batch + "-build.log"),
        root / ("shaders/" + batch + "-native-player.log"),
    )
    for row in load(proof["sourceInventory"]["path"]):
        assert sha(root / row["path"]) == row["sha256"], row["path"]
    assert sha(root / "shaders/NativeOriginalVariantFilter.cs") == sha(
        root / (batch + "-probe/Assets/Editor/NativeOriginalVariantFilter.cs")
    )
    proofs[batch] = {
        "path": "shaders/" + batch + "-execution-proof.json",
        "sha256": sha(root / ("shaders/" + batch + "-execution-proof.json")),
    }
manifest = load("shaders/semantic-v2-shadow-packaged/manifest.json")
rows = []
deps = {}
out.mkdir(exist_ok=True)
for row in manifest["rows"]:
    if not row["status"].startswith("candidate"):
        assert (
            sha(root / "unity-full" / row["identity"]["exported_path"])
            == row["identity"]["current_sha256"]
        )
        continue
    source = root / row["source"]
    matches = [
        root / (batch + "-probe/Assets/Resources/OriginalSources") / source.name for batch in proofs
    ]
    matches = [p for p in matches if p.exists()]
    assert len(matches) == 1
    tested = matches[0]
    up = compare(source, tested)
    dest = out / source.name
    shutil.copyfile(tested, dest)
    rows.append(
        {
            "identity": row["identity"],
            "source": str(dest.relative_to(root)),
            "sha256": sha(dest),
            "unity_matrix_upgrade": up,
            "pass_sets": row["pass_sets"],
            "pass_name_changes": row["pass_name_changes"],
            "gather_repairs": row["gather_repairs"],
        }
    )
    project = tested.parents[3]
    for rel in re.findall(
        r'#include "(Assets/NativeShaders/SemanticV2Variants/[^\"]+)"', tested.read_text()
    ):
        p = project / rel
        d = out / rel
        compare(package / rel, p)
        if rel in deps:
            assert deps[rel] == sha(p)
        else:
            d.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(p, d)
            deps[rel] = sha(d)
assert len(rows) == 153 and len(deps) == 17869
result = {
    "passed": True,
    "rows": rows,
    "dependencies": deps,
    "proofs": proofs,
    "finalpass_oracle": "shaders/finalpass-regenerated-oracle-verification.json",
    "declaration_only_proof": {
        "path": "shaders/semantic-v2-declaration-only-proof.json",
        "sha256": sha(root / "shaders/semantic-v2-declaration-only-proof.json"),
    },
    "export_receipt": {
        "path": "shaders/semantic-v2-export-receipt.json",
        "sha256": sha(root / "shaders/semantic-v2-export-receipt.json"),
    },
    "original_residual_coverage": {
        "path": "shaders/semantic-v2-residual-coverage.json",
        "sha256": sha(root / "shaders/semantic-v2-residual-coverage.json"),
    },
    "preserved_public_manual_targets": 124,
    "limitations": [
        "Native mono branch execution and retention are proven for the manifest. Sampling and predicate regressions cover specific original instructions; this is not complete Windows pixel equivalence.",
        "META and inactive Unity single-pass stereo remain excluded from the per-eye native runtime draw set.",
    ],
}
(out / "manifest.json").write_text(json.dumps(result, indent=2))
print("Pinned", len(rows), "sources", len(deps), "dependencies")
