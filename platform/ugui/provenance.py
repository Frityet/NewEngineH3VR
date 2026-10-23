#!/usr/bin/env python3
import hashlib, json, pathlib

root = pathlib.Path(
    "/Applications/Unity/Unity.app/Contents/Resources/PackageManager/BuiltInPackages/com.unity.ugui"
)
files = {
    str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
    for p in sorted((root / "Runtime").rglob("*.cs"))
}
out = pathlib.Path("build/ugui/runtime/UnityEngine.UI.dll")
report = {
    "source": str(root),
    "package": json.loads((root / "package.json").read_text()),
    "sourceFiles": files,
    "sourceCount": len(files),
    "outputSHA256": hashlib.sha256(out.read_bytes()).hexdigest(),
    "sourceModifications": [],
}
pathlib.Path("build/ugui/provenance.json").write_text(json.dumps(report, indent=2) + "\n")
print("Official source files:", len(files), "Output SHA256:", report["outputSHA256"])
