from pathlib import Path
import urllib.request, urllib.parse, json

here = Path(__file__).resolve().parents[1]
source = here / "audit-assets/all-shader-inputs"
dest = here / "shaders/variant-recovery"
assert not dest.exists(), str(dest)
dest.mkdir()
settings = json.loads((here / "audit-assets/assetripper-settings-1.0.15.json").read_text())
for ep, fields in [
    ("Settings/Update", settings),
    ("LoadFile", [("Path", str(p)) for p in sorted(source.glob("*.assets"))]),
    ("Export", {"Path": str(dest)}),
]:
    with urllib.request.urlopen(
        urllib.request.Request(
            "http://127.0.0.1:58950/" + ep, data=urllib.parse.urlencode(fields).encode()
        )
    ) as r:
        print(ep, r.status, flush=True)
