#!/usr/bin/env python3
"""Generate native sizeof evidence for all patched Steam structs and audit exports."""

import json, re, pathlib, subprocess

root = pathlib.Path(__file__).resolve().parents[2]
source = root / ".tools/steamworks/CSteamworks"
out = root / "build/steamworks"
imports = json.loads((root / "audit-managed/results/pinvokes.json").read_text())
required = {x["entryPoint"] for x in imports if x["library"] == "CSteamworks"}
exports = {
    line.split()[-1][1:]
    for line in subprocess.check_output(
        ["nm", "-gU", str(out / "libCSteamworks.dylib")], text=True
    ).splitlines()
    if line.strip()
}
report = {
    "requiredUniqueImports": len(required),
    "exports": len(exports),
    "missing": sorted(required - exports),
}
(out / "exports.json").write_text(json.dumps(report, indent=2) + "\n")
assert not report["missing"], report
changes = json.loads((out / "managed/NativeH3VR.FirstPass.dll.darwin-abi.json").read_text())
headers = "\n".join(p.read_text(errors="replace") for p in (source / "steam").glob("*.h"))
names = [
    x["type"].split(".")[-1]
    for x in changes
    if x["operation"] == "Darwin pack 8 -> 4" and "/" not in x["type"]
]
names = [
    n
    for n in names
    if re.search(
        r"\b(?:struct|class)\s+" + re.escape(n) + r"\b|DEFINE_CALLBACK\(\s*" + re.escape(n) + r"\b",
        headers,
    )
]
lines = (
    ['#include "CSteamworks.h"', "#include <cstdio>", "int main(){"]
    + ['printf("%s=%%zu\\n",sizeof(%s));' % (n, n) for n in names]
    + ["}"]
)
(out / "abi-sizes.cpp").write_text("\n".join(lines) + "\n")
subprocess.run(
    [
        "clang++",
        "-arch",
        "arm64",
        "-std=c++11",
        "-DPOSIX",
        "-DOSX",
        "-I" + str(source / "steam"),
        "-I" + str(source / "src"),
        str(out / "abi-sizes.cpp"),
        "-o",
        str(out / "abi-sizes"),
    ],
    check=True,
)
(out / "abi-sizes.txt").write_text(subprocess.check_output([str(out / "abi-sizes")], text=True))
print(f"All {len(required)} imports exported; generated native sizes for {len(names)} structs.")
