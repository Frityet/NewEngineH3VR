"""Extract original encoded FSB5 slices; never invoke an audio encoder."""

import pathlib
from pathlib import Path
import json, collections, hashlib, gc, struct, sys
import UnityPy
from UnityPy.helpers.ResourceReader import get_resource_data
from native_audio import native_file

ORIGINAL = Path(
    (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    )
)
OUT = Path("build/audio-clips")
STAGE = OUT / "staged"
STAGE.mkdir(exist_ok=True)
rows = json.load(open(OUT / "identity-map.json"))
groups = collections.defaultdict(list)
for row in rows:
    groups[row["source"]].append(row)
result = []
formats = collections.Counter()
loads = collections.Counter()
total = 0
for source, records in groups.items():
    env = UnityPy.load(str(ORIGINAL / source))
    objects = {
        (f.name.lower(), o.path_id): o
        for f in env.assets
        for o in f.objects.values()
        if o.type.name == "AudioClip"
    }
    resource_path = (
        "Assets/NativeAudioResources/" + source.replace("/", "_").replace(".assets", "") + ".resS"
    )
    resource = STAGE / resource_path
    resource.parent.mkdir(parents=True, exist_ok=True)
    with resource.open("wb") as output:
        for row in records:
            obj = objects[(row["collection"].lower(), row["path_id"])]
            tree = obj.read_typetree()
            res = tree["m_Resource"]
            raw = get_resource_data(
                res["m_Source"], obj.assets_file, res["m_Offset"], res["m_Size"]
            )
            assert len(raw) == res["m_Size"] and raw[:4] == b"FSB5", (source, row["path_id"])
            fsb_format = struct.unpack_from("<I", raw, 24)[0]
            expected = ".ogg" if fsb_format == 15 else ".wav"
            assert Path(row["exported_path"]).suffix == expected, (row, fsb_format)
            offset = output.tell()
            output.write(raw)
            path = str(Path(row["exported_path"]).with_suffix(".asset"))
            target = STAGE / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(native_file(tree, resource_path, offset, len(raw)))
            Path(str(target) + ".meta").write_text(
                "fileFormatVersion: 2\nguid: "
                + row["guid"]
                + "\nNativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 8300000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
            )
            result.append(
                dict(
                    source=source,
                    collection=row["collection"],
                    path_id=row["path_id"],
                    guid=row["guid"],
                    original_export=row["exported_path"],
                    native_asset=path,
                    resource=resource_path,
                    offset=offset,
                    size=len(raw),
                    sha256=hashlib.sha256(raw).hexdigest(),
                    metadata=tree,
                    fsb_format=fsb_format,
                )
            )
            formats[str(fsb_format)] += 1
            loads[str(tree["m_LoadType"])] += 1
            total += len(raw)
    del env, objects
    gc.collect()
    print(source, len(records), "total", len(result), "bytes", total, flush=True)
(OUT / "preservation-manifest.json").write_text(
    json.dumps(
        dict(
            clips=result,
            count=len(result),
            payload_bytes=total,
            fsb_formats=dict(formats),
            load_types=dict(loads),
        ),
        indent=2,
    )
    + "\n"
)
assert len(result) == 13762
print("COMPLETE", len(result), total, dict(formats), dict(loads), flush=True)
