#!/usr/bin/env python3
"""Map original AssetBundle addresses to a recovered project, fail on ambiguity."""

from pathlib import Path
from collections import defaultdict
import argparse
import json
import re
import UnityPy

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--project", type=Path, default=root / "unity-full")
args = parser.parse_args()
project = args.project.resolve()
source = (
    Path.home()
    / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/StreamingAssets"
)
paths = {
    str(path.relative_to(project)).casefold(): str(path.relative_to(project))
    for path in (project / "Assets").rglob("*")
    if path.is_file() and path.suffix != ".meta"
}
bundled = defaultdict(list)
for meta in (project / "Assets").rglob("*.meta"):
    match = re.search(r"^  assetBundleName: (.+)$", meta.read_text(errors="replace"), re.M)
    if match:
        bundled[match[1]].append(str(meta.with_suffix("").relative_to(project)))
entries = []
issues = []
for original in sorted(source.iterdir()):
    if not original.is_file() or original.suffix:
        continue
    with original.open("rb") as stream:
        if not stream.read(16).startswith(b"UnityFS"):
            continue
    env = UnityPy.load(str(original))
    bundle = next((obj.read() for obj in env.objects if obj.type.name == "AssetBundle"), None)
    if bundle is None:
        continue
    if not bundle.m_Container or {address.casefold() for address, _ in bundle.m_Container} == {
        "assetbundlemanifest"
    }:
        continue  # Unity's generated bundle-manifest container is rebuilt automatically.
    assets = []
    for address, info in bundle.m_Container:
        exact = paths.get(address.casefold())
        resolution = "original-path"
        if exact is None:
            candidates = [
                path
                for path in bundled[bundle.m_Name]
                if Path(path).name.casefold() == Path(address).name.casefold()
            ]
            if len(candidates) == 1:
                exact = candidates[0]
                resolution = "unique-bundle-basename"
            else:
                issues.append(
                    {"bundle": original.name, "address": address, "candidates": candidates}
                )
                continue
        assets.append({"address": address, "asset": exact, "resolution": resolution})
    entries.append(
        {
            "name": original.name,
            "internalName": bundle.m_Name,
            "dependencies": list(bundle.m_Dependencies),
            "assets": assets,
        }
    )
    print(original.name, "addresses:", len(assets), flush=True)
report = {"project": str(project), "bundles": entries, "issues": issues}
out = root / "build/native-bundle-plan.json"
out.write_text(json.dumps(report, indent=2) + "\n")
print("Bundles:", len(entries), "unresolved addresses:", len(issues))
if issues:
    raise SystemExit(1)
