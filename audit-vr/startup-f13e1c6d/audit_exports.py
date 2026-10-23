from pathlib import Path
import subprocess, json, hashlib, re

P = Path(__file__).resolve().parent
R = P.parent.parent
app = R / "dist/NativeH3VR Experimental.app"
imports = json.loads((P / "managed-imports.json").read_text())


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


reports = []
for lib in ["NativeH3VRXR", "NativeH3VRTracking", "NativeH3VRFMOD", "CSteamworks"]:
    wanted = sorted({x["entryPoint"] for x in imports if x["library"] == lib})
    for location, folder in [
        ("package-root", app / "Contents/Plugins"),
        ("package-arm64", app / "Contents/Plugins/ARM64"),
        ("current-native-build", R / "native/build"),
    ]:
        p = folder / f"lib{lib}.dylib"
        if not p.exists():
            continue
        out = subprocess.check_output(["nm", "-gU", str(p)], text=True)
        symbols = {line.split()[-1].removeprefix("_") for line in out.splitlines() if line.split()}
        missing_direct = sorted(set(wanted) - symbols)
        forwarded = []
        if lib == "NativeH3VRFMOD":
            commands = subprocess.check_output(["otool", "-l", str(p)], text=True)
            for name in re.findall(
                r"cmd LC_REEXPORT_DYLIB\s+cmdsize \d+\s+name (@loader_path/[^ ]+)", commands
            ):
                dep = p.parent / name.removeprefix("@loader_path/")
                raw = subprocess.check_output(["nm", "-gU", str(dep)], text=True)
                symbols |= {x.split()[-1].removeprefix("_") for x in raw.splitlines() if x.split()}
                forwarded.append(dict(path=str(dep.relative_to(R)), sha256=sha(dep)))
        missing = sorted(set(wanted) - symbols)
        reports.append(
            dict(
                library=lib,
                location=location,
                path=str(p.relative_to(R)),
                sha256=sha(p),
                imports=len(wanted),
                exportsIncludingReexports=len(symbols),
                missingDirect=missing_direct,
                reexports=forwarded,
                missing=missing,
            )
        )
headers = {}
for lib, name in [
    ("NativeH3VRXR", "nativeh3vr_xr.h"),
    ("NativeH3VRTracking", "nativeh3vr_tracking.h"),
]:
    p = R / "native/include" / name
    decls = set(re.findall(r"\b(nh3vr_\w+)\s*\(", p.read_text()))
    wanted = {x["entryPoint"] for x in imports if x["library"] == lib}
    headers[lib] = dict(
        path=str(p.relative_to(R)), sha256=sha(p), missingDeclarations=sorted(wanted - decls)
    )
out = dict(
    scope="Actual packaged NativeH3VR managed metadata; native VR bridges plus FMOD/CSteamworks import names. Inactive legacy imports are inventoried, not claimed reachable.",
    reports=reports,
    headers=headers,
    vrImportDeclarations=[
        x for x in imports if x["library"] in ("NativeH3VRXR", "NativeH3VRTracking")
    ],
)
(P / "export-closure.json").write_text(json.dumps(out, indent=2) + "\n")
for x in reports:
    print(x["library"], x["location"], "imports", x["imports"], "missing", x["missing"])
