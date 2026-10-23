#!/usr/bin/env python3
"""Snapshot the immutable VTP baseline and plan its additive repair. Never stage."""

from pathlib import Path
import hashlib, json, re

ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def read(p):
    return json.loads(p.read_text())


def write(p, d):
    p.write_text(json.dumps(d, indent=2) + "\n")


def require(v, message):
    if not v:
        raise ValueError(message)


m = read(BASE / "candidate-manifest.json")
changed = [r for r in m["files"] if r["before_sha256"] != r["after_sha256"]]
require(len(m["files"]) == 379 and len(changed) == 374, "Unexpected delta scope")
require(
    all(
        r["path"].startswith("Assets/NativeShaders/SemanticV2Variants/")
        and r["path"].endswith(".cginc")
        for r in changed
    ),
    "Only recovered includes may change",
)
proofs = {}
for rel, n in [
    ("vtp-integer-probe/output/verification.json", 420),
    ("vtp-family-probe/output/verification.json", 1364),
]:
    p = ROOT / rel
    d = read(p)
    require(
        d["passed"] and d["architecture"] == "arm64" and d["graphics"] == "Metal",
        "Proof failed " + rel,
    )
    require(len(d["hashes"]) == n, "Proof scope changed " + rel)
    for name, h in d["hashes"].items():
        require(sha(ROOT / name) == h, "Proof input changed " + name)
    proofs[rel] = {"sha256": sha(p), "verified_hashes": n}
for row in m["files"]:
    require(
        sha(BASE / "candidate" / row["path"]) == row["after_sha256"],
        "Candidate changed " + row["path"],
    )
projects = []
for project in ("unity-full", "unity"):
    shader_metas = []
    include_metas = []
    absent_include_metas = []
    for row in m["files"]:
        p = ROOT / project / row["path"]
        require(sha(p) == row["before_sha256"], "Baseline changed " + str(p))
        meta = Path(str(p) + ".meta")
        if meta.is_file():
            entry = {
                "path": row["path"] + ".meta",
                "guid": re.search(r"^guid: (\w+)$", meta.read_text(), re.M)[1],
                "sha256": sha(meta),
            }
            (shader_metas if p.suffix == ".shader" else include_metas).append(entry)
        else:
            require(p.suffix == ".cginc", "Missing shader meta " + str(meta))
            absent_include_metas.append(row["path"] + ".meta")
        if project == "unity-full" and row in changed:
            dest = BASE / "before" / row["path"]
            dest.parent.mkdir(parents=True, exist_ok=True)
            if dest.exists():
                require(sha(dest) == row["before_sha256"], "Snapshot collision " + str(dest))
            else:
                dest.write_bytes(p.read_bytes())
    projects.append(
        {
            "project": project,
            "shader_metas": shader_metas,
            "include_metas": include_metas,
            "absent_include_metas": absent_include_metas,
        }
    )
archived = []
for dirname in ("semantic-collection-order", "semantic-manifest-lifetime"):
    rel = f"build/bundle-attempts/{dirname}/native-bundle-compiler-receipt.json"
    p = ROOT / rel
    d = read(p)
    captured = {r["path"] for key in ("sourceFiles", "managedInputs") for r in d[key]}
    changed_paths = {r["path"] for r in changed}
    overlap = sorted(changed_paths & captured)
    archived.append(
        {
            "path": rel,
            "sha256": sha(p),
            "clean": d["clean"],
            "run_id": d["runId"],
            "source_files": len(d["sourceFiles"]),
            "managed_inputs": len(d["managedInputs"]),
            "changed_overlap": overlap,
            "top_level_shader_captured": m["source"] in captured,
            "candidate_dependency_overlap": sorted({r["path"] for r in m["files"]} & captured),
        }
    )
plan = {
    "staged": False,
    "files": changed,
    "unchanged_files": [r for r in m["files"] if r not in changed],
    "projects": projects,
    "candidate_manifest_sha256": sha(BASE / "candidate-manifest.json"),
    "original_manifest_sha256": sha(BASE / "original/manifest.json"),
    "original_to_candidate_map_sha256": sha(BASE / "original-to-candidate-map.json"),
    "baseline_manifest": "shaders/semantic-v2-validated/manifest.json",
    "baseline_manifest_sha256": sha(ROOT / "shaders/semantic-v2-validated/manifest.json"),
    "baseline_staging_verification_sha256": sha(
        ROOT / "shaders/semantic-v2-staging-verification.json"
    ),
    "proofs": proofs,
    "archived_bundle_receipts": archived,
    "policy": "Additive 374-include delta only. Preserve baseline manifests/receipts, original shader bytes, every meta/GUID, and dependency paths. Verify all before/candidate/proof hashes and clean native logs. Current accepted bundle receipt and dependency closure must be rechecked at checkpoint; archived disjointness is not current acceptance. No staging during the parent active bundle build.",
}
write(BASE / "staging-plan.json", plan)
print(
    json.dumps(
        {
            "plan_sha256": sha(BASE / "staging-plan.json"),
            "changed": len(changed),
            "proofs": proofs,
            "archived": [
                {k: v for k, v in r.items() if k != "candidate_dependency_overlap"}
                for r in archived
            ],
        },
        indent=2,
    )
)
