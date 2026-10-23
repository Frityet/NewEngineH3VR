#!/usr/bin/env python3
"""Install retargeted managed assemblies and rebind recovered MonoScripts in working Unity copy."""

import argparse, hashlib, json, pathlib, re, shutil, struct


def md4(data):
    def rol(x, n):
        return ((x << n) | (x >> (32 - n))) & 0xFFFFFFFF

    length = len(data) * 8
    data += b"\x80"
    data += b"\0" * ((56 - len(data) % 64) % 64) + struct.pack("<Q", length)
    state = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476]
    for off in range(0, len(data), 64):
        x = struct.unpack("<16I", data[off : off + 64])
        a, b, c, d = state
        for j in range(48):
            if j < 16:
                k = j
                s = (3, 7, 11, 19)[j % 4]
                f = (b & c) | (~b & d)
                v = 0
            elif j < 32:
                k = ((j - 16) % 4) * 4 + (j - 16) // 4
                s = (3, 5, 9, 13)[j % 4]
                f = (b & c) | (b & d) | (c & d)
                v = 0x5A827999
            else:
                k = (0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15)[j - 32]
                s = (3, 9, 11, 15)[j % 4]
                f = b ^ c ^ d
                v = 0x6ED9EBA1
            a = rol((a + f + x[k] + v) & 0xFFFFFFFF, s)
            a, b, c, d = d, a, b, c
        state = [(i + j) & 0xFFFFFFFF for i, j in zip(state, [a, b, c, d])]
    return struct.pack("<4I", *state)


assert md4(b"").hex() == "31d6cfe0d16ae931b73c59d7e0c089c0"
assert md4(b"abc").hex() == "a448017aaf21d8525fc10ae87aa6729d"


def fileid(namespace, name):
    return struct.unpack("<i", md4(("s\0\0\0" + namespace + name).encode())[:4])[0]


def guid(meta):
    m = re.search(r"^guid: ([0-9a-f]{32})$", meta, re.M)
    return m.group(1) if m else None


p = argparse.ArgumentParser()
p.add_argument("--project", default="unity")
p.add_argument("--recovered", default="recovered/ExportedProject")
p.add_argument("--map", default="audit-assets/monoscript-map.json")
p.add_argument("--ported", default="audit-managed/ported")
p.add_argument("--report-dir", default="audit-managed/unity-preparation")
args = p.parse_args()
project = pathlib.Path(args.project).resolve()
recovered = pathlib.Path(args.recovered).resolve()
ported = pathlib.Path(args.ported).resolve()
out = pathlib.Path(args.report_dir).resolve()
out.mkdir(parents=True, exist_ok=True)
if project == recovered:
    raise SystemExit("Refusing to mutate original recovered export")
renames = {
    "Assembly-CSharp.dll": "NativeH3VR.Game.dll",
    "Assembly-CSharp-firstpass.dll": "NativeH3VR.FirstPass.dll",
    "Assembly-UnityScript.dll": "NativeH3VR.UnityScript.dll",
}
dlls = {}
orders = {}
bindings = {}
mapping = []
conflicts = []
for dll in sorted(ported.glob("*.dll")):
    dest = project / "Assets/Plugins" / dll.name
    meta = dest.with_suffix(".dll.meta")
    meta_text = meta.read_text() if meta.exists() else ""
    dlls[dll.name] = {
        "guid": guid(meta_text)
        or hashlib.sha256(("NativeH3VR.Managed:" + dll.name).encode()).hexdigest()[:32],
        "dest": dest,
        "original_meta": meta_text,
    }
    orders[dll.name] = {}
for row in json.loads(pathlib.Path(args.map).read_text()):
    original = row["assembly"]
    assembly = renames.get(original, original)
    if assembly not in dlls:
        continue
    target = {
        "assembly": assembly,
        "namespace": row["namespace"],
        "class_name": row["class_name"],
        "guid": dlls[assembly]["guid"],
        "fileID": fileid(row["namespace"], row["class_name"]),
    }
    if original == "UnityEngine.UI.dll":
        # Unity 5's built-in UI DLL reference GUID in the recovered serialized assets.
        bindings[(target["fileID"], "f5f67c52d1564df4a8936ccd202a3bd8")] = target
    for script in row["recovered_scripts"]:
        old = script["guid"]
        if not old:
            continue
        key = (11500000, old)
        if key in bindings and bindings[key] != target:
            conflicts.append({"source": old, "a": bindings[key], "b": target})
            continue
        bindings[key] = target
        mapping.append(dict(target, source_guid=old, source_path=script["path"]))
        source_meta = recovered / (script["path"] + ".meta")
        if source_meta.exists():
            m = re.search(r"^  executionOrder: (-?\d+)", source_meta.read_text(), re.M)
            if m and int(m.group(1)):
                fullname = (row["namespace"] + "." if row["namespace"] else "") + row["class_name"]
                orders[assembly][fullname] = int(m.group(1))
# AssetRipper points built-in UGUI components at the canonical UGUI package C# GUIDs.
ugui = pathlib.Path(
    "/Applications/Unity/Unity.app/Contents/Resources/PackageManager/BuiltInPackages/com.unity.ugui"
)
for source in ugui.rglob("*.cs"):
    meta = source.with_suffix(".cs.meta")
    if not meta.exists():
        continue
    text = source.read_text(errors="replace")
    nm = re.search(r"\bnamespace\s+([\w.]+)", text)
    namespace = nm.group(1) if nm else ""
    name = source.stem
    if namespace not in ["UnityEngine.UI", "UnityEngine.EventSystems"]:
        continue
    assembly = "UnityEngine.UI.dll"
    target = {
        "assembly": assembly,
        "namespace": namespace,
        "class_name": name,
        "guid": dlls[assembly]["guid"],
        "fileID": fileid(namespace, name),
    }
    old = guid(meta.read_text())
    bindings[(11500000, old)] = target
    mapping.append(dict(target, source_guid=old, source_path=str(source)))
if conflicts:
    raise SystemExit("Conflicting source GUID identities: " + str(conflicts[:3]))
# Copy managed binaries and retain existing GUIDs. All are enabled for Editor and player.
for name, entry in dlls.items():
    dest = entry["dest"]
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(ported / name, dest)
    execution = (
        "  executionOrder: {}\n"
        if not orders[name]
        else "  executionOrder:\n"
        + "".join("    " + k + ": " + str(v) + "\n" for k, v in sorted(orders[name].items()))
    )
    meta = (
        "fileFormatVersion: 2\nguid: "
        + entry["guid"]
        + "\nPluginImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  iconMap: {}\n"
        + execution
        + "  defineConstraints: []\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n  platformData:\n  - first:\n      Any:\n    second:\n      enabled: 1\n      settings: {}\n  userData: NativeH3VR local managed migration\n  assetBundleName:\n  assetBundleVariant:\n"
    )
    dest.with_suffix(".dll.meta").write_text(meta)
pattern = re.compile(
    r"(m_Script:\s*\{fileID:\s*)(-?\d+)(,\s*guid:\s*)([0-9a-f]{32})(,\s*type:\s*\d+\})"
)
counts = {}
unmapped = {}
for path in (project / "Assets").rglob("*"):
    if path.suffix not in {".unity", ".prefab", ".asset", ".controller", ".overrideController"}:
        continue
    data = path.read_text(errors="surrogateescape")
    if "m_Script:" not in data:
        continue
    count = [0]

    def replace(m):
        target = bindings.get((int(m[2]), m[4]))
        if not target:
            return m.group(0)
        count[0] += 1
        return m[1] + str(target["fileID"]) + m[3] + target["guid"] + m[5]

    new = pattern.sub(replace, data)
    if count[0]:
        path.write_text(new, errors="surrogateescape")
        counts[str(path.relative_to(project))] = count[0]
# Preserve decompiled source outside Assets; recovered original remains untouched.
backup = out / "decompiled-source"
backup.mkdir(exist_ok=True)
for rel in [
    "Assets/Scripts/Assembly-CSharp",
    "Assets/Plugins/Assembly-CSharp-firstpass",
    "Assets/Scripts/Assembly-UnityScript",
]:
    source = project / rel
    if source.exists():
        dest = backup / source.name
        if dest.exists():
            raise SystemExit("Backup already exists: " + str(dest))
        shutil.move(str(source), str(dest))
        meta = pathlib.Path(str(source) + ".meta")
        if meta.exists():
            shutil.move(str(meta), str(backup / (source.name + ".meta")))
# Imported API shims from the recovery tool are unrelated framework copies; keep them outside Assets.
for name in ["System.Runtime.CompilerServices.Unsafe.dll", "System.Runtime.InteropServices.dll"]:
    source = project / "Assets/Plugins" / name
    if source.exists():
        shutil.move(str(source), str(backup / name))
        meta = source.with_suffix(".dll.meta")
        if meta.exists():
            shutil.move(str(meta), str(backup / (name + ".meta")))
# Prevent the package manager from importing a second UnityEngine.UI assembly.
manifest = project / "Packages/manifest.json"
if manifest.exists():
    content = json.loads(manifest.read_text())
    content.get("dependencies", {}).pop("com.unity.ugui", None)
    manifest.write_text(json.dumps(content, indent=2) + "\n")
report = {
    "project": str(project),
    "dlls": {k: {"guid": v["guid"], "path": str(v["dest"])} for k, v in dlls.items()},
    "bindings": mapping,
    "executionOrders": orders,
    "modifiedFiles": counts,
    "referencesRewritten": sum(counts.values()),
}
previous = out / "rewiring.json"
if previous.exists():
    old = json.loads(previous.read_text())
    report["previousReferencesRewritten"] = old.get("referencesRewritten", 0) + old.get(
        "previousReferencesRewritten", 0
    )
(out / "rewiring.json").write_text(json.dumps(report, indent=2))
print(
    json.dumps(
        {
            "dlls": len(dlls),
            "bindings": len(bindings),
            "files": len(counts),
            "referencesRewritten": sum(counts.values()),
            "executionOrders": sum(len(v) for v in orders.values()),
            "report": str(out / "rewiring.json"),
        },
        indent=2,
    )
)
