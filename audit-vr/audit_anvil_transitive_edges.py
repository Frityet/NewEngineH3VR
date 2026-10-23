#!/usr/bin/env python3
"""Conservative serialized-reference dependency graph; generated manifests remain authoritative."""

import collections, json, re, time, gc
from pathlib import Path
import UnityPy
from serialized_payload_audit import guid_text

ROOT = Path(__file__).resolve().parents[1]
plan = json.loads((ROOT / "build/native-bundle-plan.json").read_text())
project = Path(plan["project"])
out = ROOT / "build/anvil-dependencies"
start = time.time()
byguid = {}
bypath = {}
for p in (project / "Assets").rglob("*.meta"):
    m = re.search(r"^guid: ([a-f0-9]+)", p.read_text(errors="replace"), re.M)
    if m:
        asset = str(p.relative_to(project))[:-5]
        byguid[m[1]] = asset
        bypath[asset] = m[1]
owners = {bypath[a["asset"]]: b["name"] for b in plan["bundles"] for a in b["assets"]}
leaf = {
    ".texture2d",
    ".cubemap",
    ".audioclip",
    ".assetbundle",
    ".shader",
    ".cs",
    ".dll",
    ".dylib",
    ".png",
    ".jpg",
    ".jpeg",
    ".tga",
    ".wav",
    ".ogg",
    ".mp3",
    ".mp4",
    ".ttf",
    ".otf",
    ".fbx",
    ".compute",
    ".cginc",
    ".hlsl",
    ".txt",
    ".json",
    ".bytes",
    ".bank",
    ".mpg",
    ".webm",
    ".resS",
}
cache = {}
errors = []


def links(g):
    if g in cache:
        return cache[g]
    rel = byguid.get(g)
    result = set()
    cache[g] = result
    if rel is None or Path(rel).suffix.lower() in leaf:
        return result
    p = project / rel
    if not p.is_file():
        return result
    data = p.read_bytes()
    if data.startswith(b"%YAML"):
        result.update(
            x.decode() for x in re.findall(rb"\{fileID: (?!0[,}])-?\d+, guid: ([0-9a-f]+)", data)
        )
        return result
    if len(data) < 20:
        return result
    try:
        env = UnityPy.load(data)
        for f in env.files.values():
            if not hasattr(f, "externals"):
                continue
            external = {i + 1: guid_text(e.guid) for i, e in enumerate(f.externals)}
            if not external:
                continue

            def walk(v):
                if isinstance(v, dict):
                    if v.get("m_FileID") in external and v.get("m_PathID"):
                        result.add(external[v["m_FileID"]])
                    for x in v.values():
                        walk(x)
                elif isinstance(v, list):
                    for x in v:
                        walk(x)

            for o in f.objects.values():
                try:
                    walk(o.read_typetree())
                except Exception as e:
                    errors.append({"asset": rel, "object": o.path_id, "error": str(e)})
    except Exception as e:
        errors.append({"asset": rel, "error": str(e)})
    return result


edges = []
for b in plan["bundles"]:
    queue = collections.deque((bypath[a["asset"]], []) for a in b["assets"])
    seen = set()
    edgepaths = {}
    while queue:
        g, chain = queue.popleft()
        if g in seen:
            continue
        seen.add(g)
        owner = owners.get(g)
        chain = chain + [byguid.get(g, g)]
        if owner and owner != b["name"]:
            edgepaths.setdefault(owner, chain)
            continue
        for child in links(g):
            queue.append((child, chain))
    for dep, chain in sorted(edgepaths.items()):
        edges.append({"bundle": b["name"], "dependency": dep, "example_chain": chain})
    result = {
        "serialized_graph_only": True,
        "not_a_generated_manifest": True,
        "indexed_guids": len(byguid),
        "cached_assets": len(cache),
        "seconds": time.time() - start,
        "edges": edges,
        "errors": errors,
    }
    (out / "native-plan-transitive-edges.json").write_text(json.dumps(result, indent=2) + "\n")
    gc.collect()
    print(b["name"], "visited", len(seen), "cache", len(cache), "edges", len(edges), flush=True)
