#!/usr/bin/env python3
"""Read-only H3VR serialized-asset/shader inventory. Write metadata only."""

import pathlib
import collections, gc, json, re, sys
from pathlib import Path
import UnityPy
from UnityPy.enums import ShaderCompilerPlatform

ROOT = Path(
    sys.argv[1]
    if len(sys.argv) > 1
    else (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    )
)
OUT = Path(__file__).resolve().parent
paths = [
    p
    for p in ROOT.iterdir()
    if p.is_file()
    and (
        p.name == "globalgamemanagers"
        or p.name.endswith(".assets")
        or re.fullmatch(r"level\d+", p.name)
    )
]
paths += [p for p in (ROOT / "Resources").iterdir() if p.is_file()]
paths += [p for p in (ROOT / "StreamingAssets").iterdir() if p.is_file() and not p.suffix]
records = []
shaders = []
total = collections.Counter()
errors = []
for p in sorted(paths):
    try:
        env = UnityPy.load(str(p))
        for f in env.assets:
            counts = collections.Counter(o.type.name for o in f.objects.values())
            total.update(counts)
            records.append(
                dict(
                    path=str(p.relative_to(ROOT)),
                    file=f.name,
                    bytes=p.stat().st_size,
                    unity_version=f.unity_version,
                    serialized_version=f.header.version,
                    platform=int(f.target_platform),
                    type_tree_enabled=f._enable_type_tree,
                    type_count=len(f.types),
                    object_count=len(f.objects),
                    counts=dict(counts),
                )
            )
            for o in f.objects.values():
                if o.type.name == "Shader":
                    try:
                        d = o.read_typetree()
                        platforms = d.get("platforms", [])
                        shaders.append(
                            dict(
                                file=str(p.relative_to(ROOT)),
                                path_id=o.path_id,
                                bytes=o.byte_size,
                                name=d.get("m_ParsedForm", {}).get("m_Name", d.get("m_Name")),
                                platforms=[
                                    dict(id=x, name=ShaderCompilerPlatform(x).name)
                                    for x in platforms
                                ],
                                compressed_bytes=len(d.get("compressedBlob", [])),
                                source_bytes=len(d.get("m_Script", [])),
                            )
                        )
                    except Exception as ex:
                        errors.append(dict(path=str(p), path_id=o.path_id, error=repr(ex)))
                elif o.type.name == "BuildSettings":
                    d = o.read_typetree()
                    (OUT / "build-settings.json").write_text(
                        json.dumps(
                            {
                                k: d[k]
                                for k in [
                                    "scenes",
                                    "preloadedPlugins",
                                    "enabledVRDevices",
                                    "m_Version",
                                    "m_GraphicsAPIs",
                                ]
                                if k in d
                            },
                            indent=2,
                        )
                    )
        print(p.name, len(env.objects), "objects", flush=True)
        del env
        gc.collect()
    except Exception as ex:
        errors.append(dict(path=str(p), error=repr(ex)))
    (OUT / "inventory.json").write_text(
        json.dumps(
            dict(
                source=str(ROOT),
                unitypy_version=UnityPy.__version__,
                files=records,
                totals=dict(total),
                errors=errors,
            ),
            indent=2,
        )
    )
    (OUT / "shaders.json").write_text(json.dumps(shaders, indent=2))
print(
    json.dumps(
        dict(files=len(records), totals=total, shaders=len(shaders), errors=errors), indent=2
    )
)
