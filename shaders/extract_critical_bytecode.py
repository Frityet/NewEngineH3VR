"""Retain original DXBC evidence for exact texture instruction recovery."""

from pathlib import Path
import UnityPy, json, lz4.block, struct, hashlib
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

root = Path(__file__).resolve().parents[1]
out = root / "shaders/critical-bytecode-proof"
out.mkdir(exist_ok=True)
rows = []
for source in (root / "shaders/uncovered-variant-inputs").glob("*"):
    for obj in UnityPy.load(str(source)).objects:
        d = obj.read_typetree()
        name = d["m_ParsedForm"]["m_Name"]
        if name != "Hidden/PostProcessing/FinalPass":
            continue
        ix = d["platforms"].index(4)
        blob = bytes(d["compressedBlob"])
        off = d["offsets"][ix]
        size = d["compressedLengths"][ix]
        block = lz4.block.decompress(
            blob[off : off + size], uncompressed_size=d["decompressedLengths"][ix]
        )
        program = ShaderProgram(EndianBinaryReader(block, endian="<"), obj.version)
        for i, p in enumerate(program.m_SubPrograms):
            if "Pixel" not in p.m_ProgramType.name:
                continue
            code = bytes(p.m_ProgramCode)
            start = code.find(b"DXBC")
            assert start >= 0
            size = struct.unpack_from("<I", code, start + 24)[0]
            raw = code[start : start + size]
            fn = f"program-{i}.dxbc"
            (out / fn).write_bytes(raw)
            rows.append(
                dict(
                    file=fn,
                    index=i,
                    keywords=p.m_Keywords,
                    type=p.m_ProgramType.name,
                    sha256=hashlib.sha256(raw).hexdigest(),
                )
            )
(out / "manifest.json").write_text(json.dumps(rows, indent=2))
print("Extracted", len(rows))
