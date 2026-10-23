#!/usr/bin/env python3
"""Export shader-only copies through the already running 1.0.15 service.
Use a new empty output directory because old AssetRipper recreates it.
"""

from pathlib import Path
import json, urllib.request, urllib.parse

HERE = Path(__file__).resolve().parent
source = HERE / "all-shader-inputs"
dest = HERE / "all-shader-recovery-1.0.15"
if not source.exists():
    raise SystemExit("Run extract_all_shader_assets.py first")
if dest.exists() and any(dest.iterdir()):
    raise SystemExit("Refusing to overwrite a nonempty output folder")
dest.mkdir(exist_ok=True)
settings = json.loads((HERE / "assetripper-settings-1.0.15.json").read_text())
for endpoint, fields in [
    ("Settings/Update", settings),
    ("LoadFile", [("Path", str(p)) for p in sorted(source.glob("*.assets"))]),
    ("Export", {"Path": str(dest)}),
]:
    request = urllib.request.Request(
        "http://127.0.0.1:58948/" + endpoint, data=urllib.parse.urlencode(fields).encode()
    )
    with urllib.request.urlopen(request) as response:
        print(endpoint, response.status, response.url, flush=True)
