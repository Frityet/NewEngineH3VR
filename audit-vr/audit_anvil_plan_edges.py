#!/usr/bin/env python3
"""Read-only evidence of explicit cross-bundle PPtrs in the native build plan."""

import collections, json, re, time
from pathlib import Path
import UnityPy
from serialized_payload_audit import guid_text

ROOT = Path(__file__).resolve().parents[1]
plan = json.loads((ROOT / "build/native-bundle-plan.json").read_text())
project = Path(plan["project"])
owners = {}
paths = {}
guids = {}
for b in plan["bundles"]:
    for a in b["assets"]:
        p = project / a["asset"]
        meta = Path(str(p) + ".meta").read_text()
        g = re.search(r"^guid: (\w+)", meta, re.M).group(1)
        owners[g] = b["name"]
        paths[g] = a["asset"]
        guids[a["asset"]] = g
edges = collections.Counter()
samples = {}
errors = []
start = time.time()
n = 0
for b in plan["bundles"]:
    for a in b["assets"]:
        p = project / a["asset"]
        data = p.read_bytes()
        if data.startswith(b"%YAML"):
            found = set(re.findall(rb"\{fileID: (?!0[,}])-?\d+, guid: ([0-9a-f]+)", data))
            targets = {g.decode() for g in found if owners.get(g.decode()) not in (None, b["name"])}
        else:
            env = UnityPy.load(data)
            targets = set()
            for f in env.files.values():
                if not hasattr(f, "externals"):
                    continue
                candidates = {
                    i + 1: guid_text(e.guid)
                    for i, e in enumerate(f.externals)
                    if owners.get(guid_text(e.guid)) not in (None, b["name"])
                }
                if not candidates:
                    continue

                def walk(v):
                    if isinstance(v, dict):
                        if v.get("m_FileID") in candidates and v.get("m_PathID"):
                            targets.add(candidates[v["m_FileID"]])
                        for x in v.values():
                            walk(x)
                    elif isinstance(v, list):
                        for x in v:
                            walk(x)

                for o in f.objects.values():
                    try:
                        walk(o.read_typetree())
                    except Exception as e:
                        errors.append({"asset": a["asset"], "path_id": o.path_id, "error": str(e)})
        for g in targets:
            edge = (b["name"], owners[g])
            edges[edge] += 1
            samples.setdefault(edge, [])
            if len(samples[edge]) < 4:
                samples[edge].append(
                    {
                        "source_asset": a["asset"],
                        "source_address": a["address"],
                        "target_asset": paths[g],
                        "target_guid": g,
                    }
                )
        n += 1
    result = {
        "assets_checked": n,
        "seconds": time.time() - start,
        "edges": [
            {"bundle": x, "dependency": y, "direct_ref_assets": count, "examples": samples[x, y]}
            for (x, y), count in sorted(edges.items())
        ],
        "errors": errors,
    }
    (ROOT / "build/anvil-dependencies/native-plan-direct-edges.json").write_text(
        json.dumps(result, indent=2) + "\n"
    )
    print(b["name"], n, "assets", len(edges), "edges", flush=True)
