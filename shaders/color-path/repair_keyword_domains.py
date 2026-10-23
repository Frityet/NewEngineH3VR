#!/usr/bin/env python3
"""Fail-closed repair of recovered Alloy Forward lighting keyword domains.

Only the multi_compile rows listed below may change. Guards, includes, pass
state, and the complete ordered guard-key set are checked before a write.
The transformation removes impossible all-off lighting states by expressing
the original one-hot light domain in one required row and the original
optional shadow domain in a separate row. It does not change shader code or
variant recovery membership.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RECEIPT = Path(__file__).resolve().parent / "keyword-domain-repair-receipt.json"
INVENTORY = ROOT / "build/mvp-keyword-domain-readonly-audit.json"
STAGE_PLAN = Path(__file__).resolve().parent / "semantic-v3-stage-plan.json"
STAGE_RECEIPT = Path(__file__).resolve().parent / "semantic-v3-stage-receipt.json"
LIGHTS = ("DIRECTIONAL", "DIRECTIONAL_COOKIE", "POINT", "POINT_COOKIE", "SPOT")
SHADOWS = ("SHADOWS_CUBE", "SHADOWS_DEPTH", "SHADOWS_SCREEN")

LIGHT_FIRST = (
    "#pragma multi_compile __ DIRECTIONAL DIRECTIONAL_COOKIE POINT POINT_COOKIE SHADOWS_DEPTH",
    "#pragma multi_compile DIRECTIONAL DIRECTIONAL_COOKIE POINT POINT_COOKIE SPOT",
)
LIGHT_SECOND = (
    "#pragma multi_compile __ SHADOWS_CUBE SHADOWS_SCREEN SPOT",
    "#pragma multi_compile __ SHADOWS_CUBE SHADOWS_DEPTH SHADOWS_SCREEN",
)
BASE_LIGHT = ("#pragma multi_compile __ DIRECTIONAL", "#pragma multi_compile DIRECTIONAL")

GUARD_RE = re.compile(r"(?m)^\s*#(?:if|elif)\b.*$")
INCLUDE_RE = re.compile(r'(?m)^\s*#include\s+"[^"]+"\s*$')
PASS_RE = re.compile(r'(?m)^\s*Name\s+"([^"]+)"\s*$')
GUARD_LIGHT = re.compile(
    r"(?<!!)defined\((DIRECTIONAL|DIRECTIONAL_COOKIE|POINT|POINT_COOKIE|SPOT)\)"
)
GUARD_SHADOW = re.compile(r"(?<!!)defined\((SHADOWS_CUBE|SHADOWS_DEPTH|SHADOWS_SCREEN)\)")


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def active_passes(text: str) -> list[tuple[str, str, int, int]]:
    matches = list(PASS_RE.finditer(text))
    return [
        (
            m.group(1),
            text[m.start() : matches[i + 1].start() if i + 1 < len(matches) else len(text)],
            m.start(),
            matches[i + 1].start() if i + 1 < len(matches) else len(text),
        )
        for i, m in enumerate(matches)
    ]


def check_onehot(pass_body: str, pass_kind: str) -> int:
    guards = GUARD_RE.findall(pass_body)
    # Includes appear only under the recovered positive branches; parse those
    # branches and prove the metadata is one-hot for ForwardBase/Add.
    positive = [g for g in guards if g.lstrip().startswith(("#if", "#elif"))]
    if pass_kind == "FORWARDBASE":
        branches = [g for g in positive if "defined(" in g]
        assert branches, "no ForwardBase guards found"
        assert all(re.search(r"(?<!!)defined\(DIRECTIONAL\)", g) for g in branches), (
            "ForwardBase guard without positive DIRECTIONAL"
        )
        return len(branches)
    if pass_kind == "FORWARDADD":
        branches = [g for g in positive if "defined(" in g]
        assert branches, f"no ForwardAdd guards found"
        for guard in branches:
            present = set(GUARD_LIGHT.findall(guard))
            assert len(present) == 1, (pass_kind, guard, present)
            shadows = set(GUARD_SHADOW.findall(guard))
            assert len(shadows) <= 1, (pass_kind, guard, shadows)
        return len(branches)
    return 0


def prepare(
    path: Path, rel: str, pass_specs: list[dict], allow_already: bool = False
) -> tuple[bytes, bytes, dict]:
    before = path.read_bytes()
    original = before.decode("utf-8")
    changed = original
    replaced = []
    for item in pass_specs:
        index, kind = item["pass"], item["kind"].upper()
        pass_ranges = active_passes(changed)
        assert index < len(pass_ranges), f"{rel}: pass index {index} out of range"
        pass_name, body, start, end = pass_ranges[index]
        assert kind in ("FORWARDBASE", "FORWARDADD"), (rel, index, kind, pass_name)
        expected_lightmode = "FORWARDBASE" if kind == "FORWARDBASE" else "FORWARDADD"
        assert expected_lightmode in body.upper(), (rel, index, kind, pass_name)
        pairs = [BASE_LIGHT] if kind == "FORWARDBASE" else [LIGHT_FIRST, LIGHT_SECOND]
        for old, new in pairs:
            count = body.count(old)
            if count == 0 and allow_already and body.count(new) == 1:
                replaced.append(
                    {
                        "pass_index": index,
                        "pass_name": pass_name,
                        "before": old,
                        "after": new,
                        "already_applied": True,
                    }
                )
                continue
            assert count == 1, (
                f"{rel} pass#{index} {pass_name}: expected one exact row {old!r}, found {count}"
            )
            new_body = body.replace(old, new, 1)
            changed = changed[:start] + new_body + changed[end:]
            end = start + len(new_body)
            replaced.append(
                {"pass_index": index, "pass_name": pass_name, "before": old, "after": new}
            )
            body = new_body
    after = changed.encode("utf-8")

    # Exact byte-for-byte invariance after restoring only declared rows.
    normalized = changed
    for row in replaced:
        if not row.get("already_applied"):
            normalized = normalized.replace(row["after"], row["before"], 1)
    assert normalized == original, f"unexpected non-pragma change in {rel}"
    # Key-guard set/order, include list/order, and pass names/order are intact.
    assert GUARD_RE.findall(original) == GUARD_RE.findall(changed), f"guard change in {rel}"
    assert INCLUDE_RE.findall(original) == INCLUDE_RE.findall(changed), f"include change in {rel}"
    assert PASS_RE.findall(original) == PASS_RE.findall(changed), (
        f"pass order/state change in {rel}"
    )
    guard_rows = GUARD_RE.findall(original)
    assert guard_rows == GUARD_RE.findall(changed), f"allowed guard-key set changed in {rel}"

    proof = []
    before_ranges, after_ranges = active_passes(original), active_passes(changed)
    for item in pass_specs:
        index, kind = item["pass"], item["kind"].upper()
        before_body, after_body = before_ranges[index][1], after_ranges[index][1]
        proof.append(
            {
                "pass_index": index,
                "kind": kind,
                "guards_before": check_onehot(before_body, kind),
                "guards_after": check_onehot(after_body, kind),
                "guard_lines_sha256": sha("\n".join(GUARD_RE.findall(before_body)).encode()),
                "include_lines_sha256": sha("\n".join(INCLUDE_RE.findall(before_body)).encode()),
            }
        )
        assert proof[-1]["guards_before"] == proof[-1]["guards_after"]
    meta_path = Path(str(path) + ".meta")
    meta_hash = sha(meta_path.read_bytes()) if meta_path.exists() else None
    return (
        before,
        after,
        {
            "path": rel,
            "before_sha256": sha(before),
            "after_sha256": sha(after),
            "meta_sha256": meta_hash,
            "replacement_count": len(replaced),
            "replacements": replaced,
            "invariance": proof,
        },
    )


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="validate transformations without writing")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    ap.add_argument("--receipt", type=Path, default=RECEIPT)
    ap.add_argument(
        "--native-proof", type=Path, help="JSON receipt from a native material-backed Unity build"
    )
    ap.add_argument(
        "--refresh-receipt",
        action="store_true",
        help="refresh hashes/proof for already applied sources using the prior delta receipt",
    )
    args = ap.parse_args()
    inventory = json.loads(INVENTORY.read_text())
    by_path = {}
    for row in inventory:
        if not row["hasRequiredLightingRow"]:
            by_path.setdefault(row["shader"], []).append({"pass": row["pass"], "kind": row["kind"]})
    rows, writes = [], []
    for rel, specs in sorted(by_path.items()):
        path = args.root / rel
        before, after, row = prepare(
            path, rel, specs, allow_already=args.check or args.refresh_receipt
        )
        plan = json.loads(STAGE_PLAN.read_text())
        entry = next((e for e in plan["entries"] if e["target"] == rel), None)
        assert entry is not None, f"missing original staged-source identity: {rel}"
        stage_receipt = json.loads(STAGE_RECEIPT.read_text())
        assert sha(STAGE_PLAN.read_bytes()) == stage_receipt["plan_sha256"], (
            "staging receipt does not bind stage plan"
        )
        assert (
            row["before_sha256"] == entry["final_sha256"] or args.check or args.refresh_receipt
        ), f"stage preimage mismatch: {rel}"
        meta_path = args.root / (rel + ".meta")
        meta_text = meta_path.read_text() if meta_path.exists() else ""
        match = re.search(r"(?m)^guid:\s*([0-9a-f]+)\s*$", meta_text)
        assert match and match.group(1) == entry["guid"], f"shader GUID changed: {rel}"
        row["stage_plan_final_sha256"] = entry["final_sha256"]
        row["stage_plan_guid"] = entry["guid"]
        row["stage_plan_path"] = str(STAGE_PLAN.relative_to(ROOT))
        rows.append(row)
        if before != after:
            writes.append((path, before, after))
    if args.check:
        assert all(a == b for _, a, b in writes), "sources require transformation"
        prior = json.loads(args.receipt.read_text())
        expected = {r["path"]: r for r in prior["files"]}
        for row in rows:
            assert row["path"] in expected, f"missing receipt entry for {row['path']}"
            assert row["before_sha256"] == expected[row["path"]]["after_sha256"], (
                f"source hash differs from receipt: {row['path']}"
            )
            assert row["meta_sha256"] == expected[row["path"]]["meta_sha256"], (
                f"metadata hash differs from receipt: {row['path']}"
            )
            assert (
                row["stage_plan_final_sha256"] == expected[row["path"]]["stage_plan_final_sha256"]
            ), f"stage identity differs from receipt: {row['path']}"
    if args.refresh_receipt:
        prior = json.loads(args.receipt.read_text())
        expected = {r["path"]: r for r in prior["files"]}
        for row in rows:
            old = expected.get(row["path"])
            assert old is not None, f"no prior source hash for {row['path']}"
            assert row["before_sha256"] == old["after_sha256"], (
                f"current source differs from prior receipt: {row['path']}"
            )
            row["before_sha256"] = old["before_sha256"]
            row["after_sha256"] = old["after_sha256"]
    else:
        for path, before, after in writes:
            assert path.read_bytes() == before, f"source changed during validation: {path}"
            path.write_bytes(after)
    report = {
        "passed": True,
        "tool": "shaders/color-path/repair_keyword_domains.py",
        "scope": "Only inventory-listed recovered ForwardBase/ForwardAdd passes lacking a required light row; keyword pragma domain only.",
        "invariants": [
            "all #if/#elif guard lines/order preserved",
            "all include lines/order preserved",
            "pass names/order preserved",
            "source equals preimage after reversing declared pragma edits",
            "recovered ForwardBase guards require DIRECTIONAL",
            "recovered ForwardAdd guards have exactly one light keyword",
        ],
        "native_proof": {
            "status": "pending all-target material-backed candidate build",
            "earlier_three_shader_proof": {
                "unity": "2021.3.45f2 ARM64",
                "baseline_log": "build/shader-keyword-domain-baseline.log",
                "candidate_log": "build/shader-keyword-domain-regrouped.log",
                "result": "Prototyping and both Camera Relative variants passed; retained original counts unchanged; missing=0",
            },
        },
        "files": rows,
    }
    if args.native_proof:
        report["native_proof"] = json.loads(args.native_proof.read_text())
    if not args.check:
        args.receipt.parent.mkdir(parents=True, exist_ok=True)
        args.receipt.write_text(json.dumps(report, indent=2) + "\n")
    print(
        json.dumps(
            {
                "passed": True,
                "files": len(rows),
                "changed": len(writes),
                "receipt": str(args.receipt),
            }
        )
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise
