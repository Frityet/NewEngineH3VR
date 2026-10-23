#!/usr/bin/env python3
"""Snapshot a currently passing runtime into this workspace, never mutate source."""

import pathlib, hashlib, json, shutil, subprocess, datetime

root = pathlib.Path(__file__).resolve().parent
source = root.parents[1] / "ValveIndexMacOS"
out = root / "build/runtime"
snapshot = root.parent / ".tools/native-vr-runtime-snapshot"
(out / "build/bin").mkdir(parents=True, exist_ok=True)
(out / "build/openxr").mkdir(parents=True, exist_ok=True)
if snapshot.exists():
    shutil.rmtree(snapshot)
snapshot.mkdir(parents=True, exist_ok=True)
records = {}


def hash_tree(paths):
    result = {}
    for base in paths:
        members = [base] if base.is_file() else sorted(base.rglob("*"))
        for path in members:
            if path.is_file():
                result[str(path.relative_to(source))] = hashlib.sha256(
                    path.read_bytes()
                ).hexdigest()
    return result


source_roots = [
    source / "native",
    source / "buildscripts",
    source / "xmake.lua",
    source / "THIRD_PARTY_NOTICES.md",
]
source_manifest = hash_tree(source_roots)
for name in ["libopenxr_loader.dylib", "libVirtualRealityOpenXR.dylib", "libIndexCore.dylib"]:
    path = source / "build/bin" / name
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    (out / "build/bin" / name).write_bytes(data)
    if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise RuntimeError("Dependency changed during snapshot: " + str(path))
    records[str(path)] = {"sha256": digest, "mtime_ns": path.stat().st_mtime_ns}
for directory in ["native", "buildscripts"]:
    shutil.copytree(source / directory, snapshot / directory, dirs_exist_ok=True)
for name in ["xmake.lua", "THIRD_PARTY_NOTICES.md"]:
    shutil.copy2(source / name, snapshot / name)
if hash_tree(source_roots) != source_manifest:
    raise RuntimeError("Source runtime tree changed while snapshotting")
copied_manifest = {
    str(path.relative_to(snapshot)): hashlib.sha256(path.read_bytes()).hexdigest()
    for path in snapshot.rglob("*")
    if path.is_file()
}
expected_manifest = source_manifest
if copied_manifest != expected_manifest:
    raise RuntimeError("Copied native/buildscripts source snapshot does not match source hashes")
for path in snapshot.rglob("*"):
    if path.is_file():
        records[str(path.relative_to(snapshot))] = {
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()
        }
# Pin the only non-system external Mach-O dependency as well.
lua = pathlib.Path("/opt/homebrew/opt/luajit/lib/libluajit-5.1.2.dylib").resolve()
lua_destination = out / "build/bin/libluajit-5.1.2.dylib"
if lua_destination.exists():
    lua_destination.unlink()
shutil.copy2(lua, lua_destination)
records[str(lua)] = {"sha256": hashlib.sha256(lua.read_bytes()).hexdigest()}
runtime = out / "build/bin/libVirtualRealityOpenXR.dylib"
subprocess.run(
    [
        "install_name_tool",
        "-change",
        "/opt/homebrew/opt/luajit/lib/libluajit-5.1.2.dylib",
        "@loader_path/libluajit-5.1.2.dylib",
        str(runtime),
    ],
    check=True,
)
for path in (out / "build/bin").glob("*.dylib"):
    subprocess.run(["codesign", "--force", "--sign", "-", str(path)], check=True)
manifest = {
    "file_format_version": "1.0.0",
    "runtime": {
        "library_path": "../bin/libVirtualRealityOpenXR.dylib",
        "name": "NativeH3VR Pinned Virtual Reality",
        "functions": {"xrNegotiateLoaderRuntimeInterface": "xrNegotiateLoaderRuntimeInterface"},
    },
}
(out / "build/openxr/virtual-reality.json").write_text(json.dumps(manifest, indent=2) + "\n")
record = {
    "captured_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "source": str(source),
    "note": "Read-only source snapshot accompanies pinned binaries; provenance does not claim source tree was clean or rebuild-equivalent. Owned binary LuaJIT dependency relocated and ad-hoc re-signed.",
    "sources": records,
    "pinned_binaries": {
        p.name: hashlib.sha256(p.read_bytes()).hexdigest()
        for p in (out / "build/bin").glob("*.dylib")
    },
}
(out / "provenance.json").write_text(json.dumps(record, indent=2) + "\n")
subprocess.run(
    [
        str(root / "build/xr-test"),
        str(out / "build/bin/libopenxr_loader.dylib"),
        str(out / "build/openxr/virtual-reality.json"),
        str(out / "pinned-stereo.png"),
    ],
    check=True,
    timeout=30,
)
print("Pinned runtime and verified actual compositor pixels:", out)
