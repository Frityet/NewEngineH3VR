"""Hash/GUID-checked staging plan for the corrected-emitter shader candidate.

Default invocation only writes a reviewable plan under shaders/color-path. Pass --apply
only from the parent integration turn after reviewing that plan. No bundle or player files
are touched; only listed shader source and include files in unity-full/Assets are eligible.
"""

from pathlib import Path
import argparse, hashlib, json, os, re, tempfile

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument(
    "--apply",
    action="store_true",
    help="apply the saved plan after rechecking every source and target hash",
)
args = parser.parse_args()
base = root / "shaders/color-path/semantic-v3-packaged"
overlay = root / "shaders/color-path/semantic-v3-overlays"
unity = root / "unity-full"
plan_path = root / "shaders/color-path/semantic-v3-stage-plan.json"
receipt_path = root / "shaders/color-path/semantic-v3-stage-receipt.json"
sha = lambda b: hashlib.sha256(b).hexdigest()


def digest(p):
    return sha(p.read_bytes())


def readj(p):
    return json.loads(p.read_text())


def relsource(value):
    p = Path(value)
    return p if p.is_absolute() else root / p


def build_plan():
    package_manifest_path = base / "manifest.json"
    overlay_manifest_path = overlay / "overlay-manifest.json"
    pm = readj(package_manifest_path)
    om = readj(overlay_manifest_path)
    assert pm.get("staged") is False, "candidate is unexpectedly marked staged"
    candidates = [r for r in pm["rows"] if r["status"] == "candidate_requires_native_validation"]
    assert len(candidates) == 153, len(candidates)
    assert len({r["identity"]["exported_path"] for r in candidates}) == len(candidates)
    validated = readj(root / "shaders/semantic-v2-validated/manifest.json")
    validated_sha = {r["identity"]["exported_path"]: r["sha256"] for r in validated["rows"]}
    selection = readj(root / "shaders/semantic-v2-selection.json")
    selection_sha = {r["exported_path"]: r["current_sha256"] for r in selection["objects"]}
    ieee_known = {
        x["path"]: x["after_sha256"]
        for x in readj(root / "shaders/ieee-mask-repair/candidate-manifest.json")["files"]
    }
    # target -> final layer entry. Later overlay entries intentionally replace candidate bytes
    # at exactly the include paths referenced by the regenerated shaders.
    entries = {}
    shader_meta = {}
    candidate_shader_paths = set()
    for r in candidates:
        ident = r["identity"]
        exported = ident["exported_path"]
        source = relsource(r["source"])
        target_rel = Path("unity-full") / exported
        target = root / target_rel
        assert source.is_file() and digest(source) == r["sha256"], (
            source,
            "candidate hash changed",
        )
        shader_name = re.search(r'\bShader\s+"([^"]+)"', source.read_text())
        assert shader_name and shader_name[1] == ident["name"], (
            source,
            ident["name"],
            shader_name and shader_name[1],
        )
        assert target.is_file(), target
        current_sha = digest(target)
        accepted = {selection_sha[exported]}
        if exported in validated_sha:
            accepted.add(validated_sha[exported])
        if exported in ieee_known:
            accepted.add(ieee_known[exported])
        assert current_sha in accepted, (target, current_sha, accepted)
        meta = Path(str(target) + ".meta")
        meta_bytes = meta.read_bytes()
        gm = re.search(rb"^guid:\s*(\w+)", meta_bytes, re.M)
        assert gm and gm[1].decode() == ident["guid"], (meta, ident["guid"])
        assert ident["guid"] not in shader_meta, (ident["guid"], "duplicate target GUID")
        shader_meta[ident["guid"]] = {
            "path": str(meta.relative_to(root)),
            "sha256": sha(meta_bytes),
        }
        row = {
            "target": str(target_rel),
            "source": str(source.relative_to(root)),
            "kind": "shader",
            "guid": ident["guid"],
            "before_sha256": current_sha,
            "final_sha256": r["sha256"],
            "layers": ["semantic-v3-packaged"],
        }
        entries[str(target_rel)] = row
        candidate_shader_paths.add(exported)
    # Every content-addressed include in the package must be the exact body digest.
    deps = pm["dependencies"]
    assert len(deps) == 17869, len(deps)
    referenced = set()
    for r in candidates:
        source = relsource(r["source"]).read_text()
        referenced.update(
            re.findall(r'#include\s+"(Assets/NativeShaders/SemanticV2Variants/[^"]+)"', source)
        )
    assert referenced == set(deps), (
        len(referenced),
        len(deps),
        len(referenced - set(deps)),
        len(set(deps) - referenced),
    )
    for dep, expected in deps.items():
        src = base / dep
        assert src.is_file() and digest(src) == expected, (src, "package dependency hash mismatch")
        assert Path(dep).stem == expected, (dep, expected)
        target_rel = Path("unity-full") / dep
        target = root / target_rel
        before = digest(target) if target.exists() else None
        entries[str(target_rel)] = {
            "target": str(target_rel),
            "source": str(src.relative_to(root)),
            "kind": "include",
            "guid": None,
            "before_sha256": before,
            "final_sha256": expected,
            "layers": ["semantic-v3-packaged"],
        }
    # Ensure these include paths are private to the replacement shader set. This prevents
    # overwriting a shared include still consumed by an unrelated retained shader.
    external_refs = {}
    for p in (unity / "Assets").rglob("*"):
        if not p.is_file() or p.suffix not in {".shader", ".cginc", ".compute", ".hlsl"}:
            continue
        try:
            text = p.read_text()
        except (UnicodeDecodeError, OSError):
            continue
        for dep in re.findall(
            r'#include\s+"(Assets/NativeShaders/SemanticV2Variants/[^"]+)"', text
        ):
            if dep in deps and str(p.relative_to(unity)) not in candidate_shader_paths:
                external_refs.setdefault(dep, set()).add(str(p.relative_to(unity)))
    assert not external_refs, list(external_refs.items())[:3]
    # Every overlay must be anchored byte-for-byte to its corresponding fresh package source.
    overlay_rows = om["ieee"]["files"] + om["vtp"]["files"]
    assert len(overlay_rows) == 380, len(overlay_rows)
    overlay_paths = set()
    for x in overlay_rows:
        rel = x["path"]
        src = overlay / rel
        assert src.is_file() and digest(src) == x["after_sha256"], (src, "overlay hash mismatch")
        if rel.startswith("Assets/Shader/"):
            target_rel = Path("unity-full") / rel
            package = next((r for r in candidates if r["identity"]["exported_path"] == rel), None)
            assert package and package["sha256"] == x["before_sha256"], (
                rel,
                "overlay does not apply to a candidate shader",
            )
        else:
            assert rel in deps and deps[rel] == x["before_sha256"], (
                rel,
                "overlay does not apply to a generated include",
            )
            target_rel = Path("unity-full") / rel
        key = str(target_rel)
        assert key not in overlay_paths
        overlay_paths.add(key)
        entry = entries[key]
        entry["source"] = str(src.relative_to(root))
        entry["final_sha256"] = x["after_sha256"]
        entry["layers"].append("semantic-v3-overlays")
        entry["overlay_before_sha256"] = x["before_sha256"]
    # Capture targets and metadata now; --apply refuses if any state changed since this plan.
    for row in entries.values():
        target = root / row["target"]
        now = digest(target) if target.exists() else None
        if row["kind"] == "shader":
            assert now == row["before_sha256"], row["target"]
        else:
            row["before_sha256"] = now
        row["operation"] = "write" if now != row["final_sha256"] else "already_matches"
    # The overlay source is the final layer; check original ShaderLab/meta identity.
    for guid, meta in shader_meta.items():
        target_row = next(
            x for x in entries.values() if x["kind"] == "shader" and x["guid"] == guid
        )
        assert re.search(r'\bShader\s+"([^"]+)"', (root / target_row["source"]).read_text())[
            1
        ] == next(r["identity"]["name"] for r in candidates if r["identity"]["guid"] == guid)
    plan = {
        "staged": False,
        "root": "unity-full/Assets",
        "candidate_manifest_sha256": digest(package_manifest_path),
        "overlay_manifest_sha256": digest(overlay_manifest_path),
        "selection_sha256": digest(root / "shaders/semantic-v2-selection.json"),
        "source_count": len(candidates),
        "include_count": len(deps),
        "overlay_count": len(overlay_rows),
        "target_count": len(entries),
        "overlay_replacements": len(overlay_paths),
        "candidate_shader_guids": shader_meta,
        "entries": sorted(entries.values(), key=lambda x: x["target"]),
    }
    plan["write_count"] = sum(x["operation"] == "write" for x in plan["entries"])
    plan["already_matches_count"] = plan["target_count"] - plan["write_count"]
    return plan


def validate_saved_plan(plan):
    assert (
        plan["staged"] is False and plan["source_count"] == 153 and plan["include_count"] == 17869
    )
    assert digest(base / "manifest.json") == plan["candidate_manifest_sha256"]
    assert digest(overlay / "overlay-manifest.json") == plan["overlay_manifest_sha256"]
    assert digest(root / "shaders/semantic-v2-selection.json") == plan["selection_sha256"]
    for row in plan["entries"]:
        target = root / row["target"]
        now = digest(target) if target.exists() else None
        assert now == row["before_sha256"], (
            row["target"],
            "target changed after plan",
            now,
            row["before_sha256"],
        )
        src = root / row["source"]
        assert src.is_file() and digest(src) == row["final_sha256"], (src, "source changed")
        if row["kind"] == "shader":
            meta = Path(str(target) + ".meta")
            m = meta.read_bytes()
            gm = re.search(rb"^guid:\s*(\w+)", m, re.M)
            assert gm and gm[1].decode() == row["guid"], (meta, row["guid"])
            expected = plan["candidate_shader_guids"][row["guid"]]
            assert sha(m) == expected["sha256"], (meta, "meta changed after plan")


if args.apply:
    assert plan_path.is_file(), "Create/review the plan first by running without --apply."
    plan = readj(plan_path)
    validate_saved_plan(plan)
    # The plan is an input file and may have been edited. Recompute the complete
    # allowlist from the immutable candidate manifests and current target state so
    # an altered plan cannot expand the write set or redirect a source/target.
    assert build_plan() == plan, (
        "Saved plan differs from the freshly reconstructed allowlist; regenerate and review it."
    )
    # All validation is complete before the first write.
    applied = []
    for row in plan["entries"]:
        if row["operation"] == "already_matches":
            continue
        target = root / row["target"]
        target.parent.mkdir(parents=True, exist_ok=True)
        data = (root / row["source"]).read_bytes()
        with tempfile.NamedTemporaryFile(
            dir=target.parent, prefix="." + target.name + ".", delete=False
        ) as tmp:
            tmp.write(data)
            tmp_path = Path(tmp.name)
        os.replace(tmp_path, target)
        assert digest(target) == row["final_sha256"], target
        if row["kind"] == "shader":
            meta = Path(str(target) + ".meta")
            assert re.search(
                rb"^guid:\s*" + row["guid"].encode() + rb"\s*$", meta.read_bytes(), re.M
            ), meta
        applied.append(row["target"])
    receipt = {
        "staged": True,
        "plan_sha256": digest(plan_path),
        "written_targets": applied,
        "written_count": len(applied),
        "candidate_shader_guids": len(plan["candidate_shader_guids"]),
        "bundle_or_player_files_touched": False,
    }
    receipt_path.write_text(json.dumps(receipt, indent=2))
    print("Staged", len(applied), "listed shader/include targets; GUIDs preserved.")
else:
    plan = build_plan()
    plan_path.write_text(json.dumps(plan, indent=2))
    print(
        "Plan only; unity-full unchanged. Targets:",
        plan["target_count"],
        "writes:",
        plan["write_count"],
        "already matching:",
        plan["already_matches_count"],
        "shader sources:",
        plan["source_count"],
        "includes:",
        plan["include_count"],
        "overlay replacements:",
        plan["overlay_replacements"],
    )
    print("Review", plan_path.relative_to(root), "then parent may invoke with --apply.")
