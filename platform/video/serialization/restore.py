#!/usr/bin/env python3
"""Validate original payloads, map exact export identities, stage recovered YAML."""

import json, pathlib, re, struct, hashlib, copy, sys
import UnityPy

out = pathlib.Path("build/video/serialization")
project = pathlib.Path("unity-full")
original = pathlib.Path(
    (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    )
)
records = json.loads((out / "decoded.json").read_text())
schema = json.loads((out / "schema.json").read_text())
scenes = json.load(open("audit-assets/build-settings.json"))["scenes"]


# Independent write-back must reproduce every original byte, including zero padding.
def encode(s, v):
    k = s["kind"]
    if k == "struct":
        return b"".join(encode(f["schema"], v[f["name"]]) for f in s["fields"])
    if k == "PPtr":
        return struct.pack("<iq", v["m_FileID"], v["m_PathID"])
    if k == "String":
        b = v.encode()
        b = struct.pack("<i", len(b)) + b
    elif k == "array":
        b = struct.pack("<i", len(v)) + b"".join(encode(s["element"], x) for x in v)
    else:
        b = struct.pack(
            "<"
            + {
                "Int32": "i",
                "UInt32": "I",
                "Int64": "q",
                "UInt64": "Q",
                "Single": "f",
                "Double": "d",
                "Boolean": "?",
                "Byte": "B",
                "SByte": "b",
                "Int16": "h",
                "UInt16": "H",
            }[k],
            v,
        )
    return b + b"\0" * ((-len(b)) % 4)


for rec in records:
    raw = (out / rec["raw_file"]).read_bytes()
    assert raw[:32] + encode(schema, rec["fields"]) == raw, rec["raw_file"]


# YAML parser limited to document headers and engine-owned hierarchy fields, never names alone.
def docs(text):
    return list(re.finditer(r"^--- !u!(\d+) &(-?\d+)\n([\s\S]*?)(?=^--- !u!|\Z)", text, re.M))


def ptr(body, key):
    return int(re.search(r"^  " + key + r": \{fileID: (-?\d+)\}", body, re.M).group(1))


def exported_hierarchy(ds, gid):
    gos = {int(d[2]): d[3] for d in ds if d[1] == "1"}
    trs = {int(d[2]): d[3] for d in ds if d[1] in ("4", "224")}
    bygo = {ptr(b, "m_GameObject"): tid for tid, b in trs.items()}

    def path(g):
        name = re.search(r"^  m_Name: (.*)$", gos[g], re.M)[1]
        t = trs[bygo[g]]
        father = ptr(t, "m_Father")
        return (path(ptr(trs[father], "m_GameObject")) + "/" if father else "") + name

    return path(gid)


res = UnityPy.load(str(original / "resources.assets")).assets[0]
cache = {}


def od(pid):
    if pid not in cache:
        cache[pid] = res.objects[pid].read_typetree(check_read=False)
    return cache[pid]


def original_hierarchy(gid):
    g = od(gid)
    cs = [x["component"]["m_PathID"] for x in g["m_Component"]]
    t = next(od(c) for c in cs if res.objects[c].type.name in ("Transform", "RectTransform"))
    father = t["m_Father"]["m_PathID"]
    return (original_hierarchy(od(father)["m_GameObject"]["m_PathID"]) + "/" if father else "") + g[
        "m_Name"
    ]


reference = project / "Assets/MonoBehaviour/Splash_MainMenu.asset"
guid = re.search(r"^guid: (\w+)", reference.with_suffix(".asset.meta").read_text(), re.M)[1]
assert "_path: TutorialVideo/Splash/MainmenuLoop.mp4" in reference.read_text()
assert (original / "StreamingAssets/TutorialVideo/Splash/MainmenuLoop.mp4").is_file()


# The only non-null content pointer is checked against both source object and export.
def convert(v, rec):
    if isinstance(v, dict):
        if set(v) == {"m_FileID", "m_PathID"}:
            if not v["m_PathID"]:
                return {"fileID": 0}
            target = rec["externals"][v["m_FileID"] - 1] if v["m_FileID"] else rec["file"]
            assert target.lower() == "resources.assets" and v["m_PathID"] == 32405
            assert od(32405)["m_Name"] == "Splash_MainMenu"
            return {"fileID": 11400000, "guid": guid, "type": 2}
        return {k: convert(x, rec) for k, x in v.items()}
    if isinstance(v, list):
        return [convert(x, rec) for x in v]
    if isinstance(v, str):
        return v.replace(", Assembly-CSharp,", ", NativeH3VR.Game,")
    return v


def yaml(v, indent=2):
    lines = []
    for k, x in v.items():
        prefix = " " * indent + k + ":"
        if isinstance(x, dict) and "fileID" in x:
            lines.append(prefix + " {" + ", ".join(f"{a}: {b}" for a, b in x.items()) + "}")
        elif isinstance(x, dict):
            lines.append(prefix)
            lines.extend(yaml(x, indent + 2))
        elif isinstance(x, list):
            assert not x, "Nonempty array needs explicit YAML encoder"
            lines.append(prefix + " []")
        else:
            lines.append(
                prefix
                + " "
                + (str(int(x)) if isinstance(x, bool) else json.dumps(x, ensure_ascii=False))
            )
    return lines


texts = {}
mapped = []
seen = set()
for rec in records:
    if rec["file"].startswith("level"):
        path = project / scenes[int(rec["file"][5:])]
        hierarchy = None
    else:
        hierarchy = original_hierarchy(rec["header"]["m_GameObject"]["m_PathID"])
        path = project / "Assets/GameObject" / ((hierarchy.split("/")[0]) + ".prefab")
    if path not in texts:
        texts[path] = path.read_text()
    ds = docs(texts[path])
    candidates = []
    for d in ds:
        if (
            d[1] != "114"
            or "m_Script: {fileID: 1990293376, guid: 924068faa081ab453cd95dc24ee0841b, type: 3}"
            not in d[3]
        ):
            continue
        if hierarchy:
            if exported_hierarchy(ds, ptr(d[3], "m_GameObject")) == hierarchy:
                candidates.append(d)
        elif int(d[2]) == rec["path_id"]:
            assert ptr(d[3], "m_GameObject") == rec["header"]["m_GameObject"]["m_PathID"]
            candidates.append(d)
    assert len(candidates) == 1, (rec["file"], rec["path_id"], len(candidates))
    d = candidates[0]
    key = (str(path), int(d[2]))
    assert key not in seen
    seen.add(key)
    # Existing failed components contain only the engine header, never overwrite authored fields.
    assert d[3].endswith("  m_EditorClassIdentifier:\n"), (path, d[2])
    addition = "\n".join(yaml(convert(rec["fields"], rec))) + "\n"
    texts[path] = texts[path][: d.end()] + addition + texts[path][d.end() :]
    mapped.append(
        dict(
            source_file=rec["file"],
            source_path_id=rec["path_id"],
            source_sha256=rec["sha256"],
            target=str(path),
            target_file_id=int(d[2]),
            hierarchy=hierarchy,
            recovered_fields=len(rec["fields"]),
        )
    )
files = []
for path, text in texts.items():
    target = out / "staged" / path.relative_to(project)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text)
    files.append(
        dict(
            target=str(path),
            staged=str(target),
            before_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
            after_sha256=hashlib.sha256(target.read_bytes()).hexdigest(),
        )
    )
report = dict(
    components=len(mapped),
    files=len(files),
    byte_roundtrips=len(records),
    media_reference=dict(
        source="resources.assets:32405",
        export=str(reference),
        guid=guid,
        path="TutorialVideo/Splash/MainmenuLoop.mp4",
    ),
    mapping=mapped,
    changed_files=files,
)
(out / "restoration-plan.json").write_text(json.dumps(report, indent=2) + "\n")
print("Validated, byte-roundtripped and staged:", len(mapped), "components in", len(files), "files")
