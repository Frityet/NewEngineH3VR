#!/usr/bin/env python3
import UnityPy, json, pathlib, hashlib, collections, gc

root = pathlib.Path(
    (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    )
)
out = pathlib.Path("build/video/serialization")
out.mkdir(exist_ok=True, parents=True)
records = []
for entry in json.load(open("audit-assets/inventory.json"))["files"]:
    if not entry["counts"].get("MonoBehaviour") or entry["path"].startswith("StreamingAssets/"):
        continue
    env = UnityPy.load(str(root / entry["path"]))
    for f in env.assets:
        for o in f.objects.values():
            if o.type.name != "MonoBehaviour":
                continue
            d = o.read_typetree(check_read=False)
            s = d.get("m_Script", {})
            fid = s.get("m_FileID", 0)
            pid = s.get("m_PathID", 0)
            source = f.externals[fid - 1].path if fid else f.name
            if pid != 1089 or source.lower() != "globalgamemanagers.assets":
                continue
            raw = o.get_raw_data()
            name = entry["path"].replace("/", "_") + "-" + str(o.path_id) + ".bin"
            (out / name).write_bytes(raw)
            records.append(
                dict(
                    file=entry["path"],
                    path_id=o.path_id,
                    byte_size=o.byte_size,
                    raw_file=name,
                    sha256=hashlib.sha256(raw).hexdigest(),
                    header=d,
                    externals=[x.path for x in f.externals],
                )
            )
        print(entry["path"], len(records), flush=True)
    del env
    gc.collect()
(out / "inventory.json").write_text(json.dumps(records, indent=2))
print("MediaPlayers", len(records), "sizes", collections.Counter(r["byte_size"] for r in records))
