#!/usr/bin/env python3
"""Recover main data and streaming bundles together using local AssetRipper 2.0.
Original files are read-only; output must be a fresh directory.
"""

from pathlib import Path
import json
import shutil
import urllib.request
import urllib.parse

root = Path(__file__).resolve().parents[1]
source = Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR"
dest = root / "recovered-full"
if dest.exists() and any(dest.iterdir()):
    raise SystemExit("Refusing to overwrite recovered-full; preserve or choose a fresh output")
if shutil.disk_usage(root).free < 100 * 1024**3:
    raise SystemExit("Full recovery requires at least 100 GiB free before starting")
settings = json.loads((root / "audit-assets/assetripper-settings.json").read_text())
settings.pop("IgnoreStreamingAssets", None)
dest.mkdir(exist_ok=True)
for endpoint, fields in [
    ("Settings/Update", settings),
    ("LoadFolder", {"path": str(source)}),
    ("Export/UnityProject", {"path": str(dest)}),
]:
    print(endpoint, flush=True)
    req = urllib.request.Request(
        "http://127.0.0.1:58947/" + endpoint, data=urllib.parse.urlencode(fields).encode()
    )
    with urllib.request.urlopen(req) as response:
        print(response.status, response.url, flush=True)
print("Recovery complete. Shader replacement and managed migration are still required.", flush=True)
