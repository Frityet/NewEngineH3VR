#!/usr/bin/env python3
"""Extract small, unmodified DXBC test cases from local H3VR; never edit the game."""

import pathlib
from pathlib import Path
import UnityPy, json, lz4.block, struct
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

root = Path(
    (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    )
)
out = Path(__file__).resolve().parent / "shader-proof"
out.mkdir(exist_ok=True)
env = UnityPy.load(str(root / "globalgamemanagers.assets"))
names = {
    "Custom/ReticleShader",
    "Unlit/ScopeShader",
    "H3VR/HolograpicSight",
    "Custom/OpticLensShader",
}
manifest = []
for obj in env.objects:
    if obj.type.name != "Shader":
        continue
    data = obj.read_typetree()
    name = data["m_ParsedForm"]["m_Name"]
    if name not in names:
        continue
    stem = name.replace("/", "__")
    (out / (stem + ".raw.bin")).write_bytes(obj.get_raw_data())
    (out / (stem + ".metadata.json")).write_text(
        json.dumps({k: v for k, v in data.items() if k != "compressedBlob"}, indent=2)
    )
    blob = bytes(data["compressedBlob"])
    block = lz4.block.decompress(
        blob[data["offsets"][0] : data["offsets"][0] + data["compressedLengths"][0]],
        uncompressed_size=data["decompressedLengths"][0],
    )
    program = ShaderProgram(EndianBinaryReader(block, endian="<"), obj.version)
    for index, sub in enumerate(program.m_SubPrograms):
        code = bytes(sub.m_ProgramCode)
        start = code.find(b"DXBC")
        row = dict(
            shader=name,
            path_id=obj.path_id,
            index=index,
            type=sub.m_ProgramType.name,
            bytes=len(code),
            dxbc_offset=start,
            keywords=sub.m_Keywords,
        )
        manifest.append(row)
        if start >= 0:
            size = struct.unpack_from("<I", code, start + 24)[0]
            assert size <= len(code) - start
            row["dxbc_size"] = size
            (out / (stem + f".{index}.dxbc")).write_bytes(code[start : start + size])
    print(name, len(program.m_SubPrograms), "subprograms")
(out / "manifest.json").write_text(json.dumps(manifest, indent=2))
print("Extracted", len(manifest), "subprograms")
