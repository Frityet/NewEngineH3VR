#!/usr/bin/env python3
"""Inventory every recovered source shader pass and original DXBC variant. Read-only."""

from pathlib import Path
import json, hashlib, lz4.block, gzip
import UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

here = Path(__file__).resolve().parent
out = here.parent / "shaders"
out.mkdir(exist_ok=True)
rows = []
for f in sorted((here / "all-shader-inputs").glob("*.assets")):
    for obj in UnityPy.load(str(f)).objects:
        d = obj.read_typetree()
        p = d["m_ParsedForm"]
        row = {
            "copy_file": f.name,
            "path_id": obj.path_id,
            "name": p["m_Name"],
            "raw_sha256": hashlib.sha256(obj.get_raw_data()).hexdigest(),
            "platforms": d["platforms"],
            "passes": [],
            "programs": [],
        }
        for si, sub in enumerate(p["m_SubShaders"]):
            for pi, pa in enumerate(sub["m_Passes"]):
                row["passes"].append(
                    {
                        "subshader": si,
                        "pass": pi,
                        "type": pa["m_Type"],
                        "name": pa.get("m_State", {}).get("m_Name"),
                        "state": pa.get("m_State"),
                        "programs": {k: v for k, v in pa.items() if k.startswith("prog")},
                        "name_indices": pa.get("m_NameIndices"),
                    }
                )
        if 4 in d["platforms"]:
            ix = d["platforms"].index(4)
            blob = bytes(d["compressedBlob"])
            off = d["offsets"][ix]
            size = d["compressedLengths"][ix]
            block = lz4.block.decompress(
                blob[off : off + size], uncompressed_size=d["decompressedLengths"][ix]
            )
            prog = ShaderProgram(EndianBinaryReader(block, endian="<"), obj.version)
            row["programs"] = [
                {
                    "index": i,
                    "stage": p.m_ProgramType.name,
                    "keywords": p.m_Keywords,
                    "code_sha256": hashlib.sha256(bytes(p.m_ProgramCode)).hexdigest(),
                }
                for i, p in enumerate(prog.m_SubPrograms)
            ]
        rows.append(row)

with gzip.open(out / "original-variants.json.gz", "wt") as stream:
    json.dump(rows, stream, separators=(",", ":"))
print(len(rows), "shader objects;", sum(len(r["programs"]) for r in rows), "programs")
