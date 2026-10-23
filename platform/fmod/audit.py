#!/usr/bin/env python3
"""Record actual legacy/native FMOD signatures and generate native struct-size probes."""

import pathlib, re, json, subprocess

root = pathlib.Path(__file__).resolve().parents[2]
out = root / "build/fmod"
sdk = root / ".tools/fmod/sdk-2.03.15/api"
oldroot = root / "recovered/ExportedProject/Assets/Plugins/Assembly-CSharp-firstpass/FMOD"
old = "\n".join(p.read_text() for p in oldroot.rglob("*.cs"))
new = "\n".join(p.read_text() for p in (root / ".tools/fmod/source").glob("*.cs"))
rx = r"(?:public|private|internal)\s+static\s+extern\s+(\w+)\s+(FMOD5?_\w+)\s*\(([^;]*?)\);"
a = {n: {"result": r, "args": args} for r, n, args in re.findall(rx, old)}
b = {n: {"result": r, "args": args} for r, n, args in re.findall(rx, new)}


def types(args):
    return [re.sub(r"\[.*?\]", "", p).strip().rsplit(" ", 1)[0] for p in args.split(",")]


missing = {n: s for n, s in a.items() if n not in b}
changed = {
    n: {"old": s, "new": b[n]}
    for n, s in a.items()
    if n in b and (s["result"] != b[n]["result"] or types(s["args"]) != types(b[n]["args"]))
}
(out / "api-delta.json").write_text(
    json.dumps({"old": len(a), "new": len(b), "removed": missing, "changed": changed}, indent=2)
    + "\n"
)
headers = "\n".join(p.read_text(errors="replace") for p in sdk.glob("*/inc/*.h"))
pairs = []
for p in oldroot.rglob("*.cs"):
    m = re.search(r"public struct (\w+)\b", p.read_text())
    if not m:
        continue
    ns = "FMOD.Studio" if "/Studio/" in str(p) else "FMOD"
    native = ("FMOD_STUDIO_" if ns.endswith("Studio") else "FMOD_") + m[1]
    if re.search(r"typedef struct\s+" + native + r"\s*\{", headers):
        pairs.append((ns + "." + m[1], native))
(out / "abi-sizes.cpp").write_text(
    '#include "fmod.h"\n#include "fmod_studio.h"\n#include <cstdio>\nint main(){\n'
    + "\n".join('printf("%s=%%zu\\n",sizeof(%s));' % p for p in pairs)
    + "\n}\n"
)
subprocess.run(
    [
        "clang++",
        "-arch",
        "arm64",
        "-I" + str(sdk / "core/inc"),
        "-I" + str(sdk / "studio/inc"),
        str(out / "abi-sizes.cpp"),
        "-o",
        str(out / "abi-sizes"),
    ],
    check=True,
)
(out / "abi-sizes.txt").write_text(subprocess.check_output([str(out / "abi-sizes")], text=True))
print(f"Compared {len(a)} imports; generated {len(pairs)} native struct sizes.")
