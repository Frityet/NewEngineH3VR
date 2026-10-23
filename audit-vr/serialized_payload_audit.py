#!/usr/bin/env python3
"""Read-only comparison of pristine YAML and Unity binary MonoBehaviour payloads."""

import argparse, json, math, re, struct
from pathlib import Path
import UnityPy, yaml

ROOT = Path(__file__).resolve().parents[1]
OLD = ROOT / "recovered-full/ExportedProject"
NEW = ROOT / "unity-full"
BASE = {
    "m_ObjectHideFlags",
    "m_PrefabParentObject",
    "m_PrefabInternal",
    "m_CorrespondingSourceObject",
    "m_PrefabInstance",
    "m_PrefabAsset",
    "m_Script",
    "m_EditorClassIdentifier",
    "serializedVersion",
}
BINDINGS = {
    r["source_guid"]: r
    for r in json.loads((ROOT / "build/unity-full-preparation/rewiring.json").read_text())[
        "bindings"
    ]
}
HEADER = re.compile(r"^--- !u!(\d+) &(-?\d+).*\n", re.M)


def guid_text(b):
    h = b.hex()
    return "".join(h[i + 1] + h[i] for i in range(0, len(h), 2))


def norm(x, externals=None):
    if isinstance(x, dict):
        if "fileID" in x:
            return (
                {"$ref": [x.get("guid", ""), int(x["fileID"])]}
                if int(x["fileID"])
                else {"$ref": ["", 0]}
            )
        if "m_FileID" in x and "m_PathID" in x:
            return (
                {
                    "$ref": [
                        guid_text(externals[x["m_FileID"] - 1].guid)
                        if x["m_FileID"] and x["m_PathID"]
                        else "",
                        x["m_PathID"],
                    ]
                }
                if x["m_PathID"]
                else {"$ref": ["", 0]}
            )
        return {k: norm(v, externals) for k, v in x.items() if k != "serializedVersion"}
    if isinstance(x, list):
        return [norm(v, externals) for v in x]
    if x is None:
        return ""
    return x


def diff(a, b, path=""):
    # Unity YAML uses unquoted strings/numbers and packed little-endian arrays;
    # use the actual binary field's kind, not YAML 1.1's octal/bool inference.
    if isinstance(a, str) and isinstance(b, (int, float)):
        try:
            a = float(a) if isinstance(b, float) else int(a, 10)
        except ValueError:
            pass
    if isinstance(a, str) and isinstance(b, list):
        if a == "":
            a = []
        elif re.fullmatch(r"[0-9a-fA-F]+", a) and b and all(isinstance(v, (int, float)) for v in b):
            raw = bytes.fromhex(a)
            if len(raw) == len(b) * 4:
                kind = (
                    "f"
                    if any(isinstance(v, float) for v in b)
                    else ("i" if any(v < 0 for v in b) else "I")
                )
                a = list(struct.unpack("<" + kind * len(b), raw))
            elif len(raw) == len(b):
                a = list(raw)
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(a.keys() - b.keys()):
            yield dict(field=path + k, kind="missing", before=a[k])
        for k in sorted(b.keys() - a.keys()):
            yield dict(field=path + k, kind="added", after=b[k])
        for k in sorted(a.keys() & b.keys()):
            yield from diff(a[k], b[k], path + k + ".")
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            yield dict(field=path.rstrip("."), kind="array-length", before=len(a), after=len(b))
        for i, (x, y) in enumerate(zip(a, b)):
            yield from diff(x, y, path + str(i) + ".")
    elif isinstance(a, (float, int)) and isinstance(b, (float, int)):
        if not (
            a == b
            or (isinstance(a, float) and isinstance(b, float) and math.isnan(a) and math.isnan(b))
            or math.isclose(a, b, rel_tol=2e-6, abs_tol=1e-7)
        ):
            yield dict(field=path.rstrip("."), kind="value", before=a, after=b)
    elif a != b:
        yield dict(field=path.rstrip("."), kind="value", before=a, after=b)


UI_BINDINGS = {}
for line in (ROOT / "build/ugui/old-script-ids.txt").read_text().splitlines():
    name, guid, pid = line.split()
    UI_BINDINGS[int(pid)] = (name, guid)


def yaml_components(text, only=None):
    headers = list(HEADER.finditer(text))
    result = {}
    for n, h in enumerate(headers):
        if h[1] != "114":
            continue
        chunk = text[h.end() : headers[n + 1].start() if n + 1 < len(headers) else len(text)]
        if only and not any(g in chunk for g in only):
            continue
        result[int(h[2])] = yaml.load(chunk, Loader=yaml.CBaseLoader)["MonoBehaviour"]
    return result


def audit(rel, only=None):
    sources = yaml_components((OLD / rel).read_text(), only)
    target_path = NEW / rel
    with target_path.open("rb") as f:
        target_yaml = f.read(5) == b"%YAML"
    env = None
    if target_yaml:
        targets = yaml_components(target_path.read_text())
    else:
        env = UnityPy.load(str(target_path))
        targets = {o.path_id: o for o in env.objects if o.type.value == 114}
    rows = []
    for pid, src in sources.items():
        o = targets.get(pid)
        if o is None:
            rows.append(dict(path_id=pid, error="object missing"))
            continue
        try:
            dst = o if target_yaml else o.read_typetree()
        except Exception as e:
            rows.append(dict(path_id=pid, error=str(e)))
            continue
        externals = None if target_yaml else o.assets_file.externals
        a = norm({k: v for k, v in src.items() if k not in BASE})
        b = norm({k: v for k, v in dst.items() if k not in BASE}, externals)
        source_script = norm(src["m_Script"])["$ref"]
        binding = BINDINGS.get(src["m_Script"].get("guid"))
        actual_script = norm(dst["m_Script"], externals)["$ref"]
        class_name = None
        binding_basis = "unchanged-source-pointer"
        expected_script = source_script
        if binding:
            expected_script = [binding["guid"], binding["fileID"]]
            class_name = binding["namespace"] + "." + binding["class_name"]
            binding_basis = "managed-rewiring-report"
        elif (
            source_script[0] == "f5f67c52d1564df4a8936ccd202a3bd8"
            and source_script[1] in UI_BINDINGS
        ):
            class_name, guid = UI_BINDINGS[source_script[1]]
            expected_script = [guid, source_script[1]]
            binding_basis = "verified-ugui-dll-migration"
        rows.append(
            dict(
                path_id=pid,
                source_script=src["m_Script"],
                class_name=class_name,
                target_script=actual_script,
                script_matches_binding=actual_script == expected_script,
                script_binding_basis=binding_basis,
                source_fields=len(a),
                target_fields=len(b),
                differences=list(diff(a, b)),
            )
        )
    return dict(
        path=rel,
        target_format="yaml" if target_yaml else "binary",
        source_components=len(sources),
        target_components=len(targets),
        components=rows,
    )


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("paths", nargs="+")
    p.add_argument("--output", required=True)
    p.add_argument("--scripts", nargs="*")
    a = p.parse_args()
    results = [audit(x, a.scripts) for x in a.paths]
    out = Path(a.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(results, indent=2) + "\n")
    for r in results:
        print(
            r["path"],
            len(r["components"]),
            "components;",
            sum(len(c.get("differences", [])) for c in r["components"]),
            "differences",
        )
