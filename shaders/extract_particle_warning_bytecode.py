"""Read original particle DXBC for the reported shadow/spot attenuation warning."""

from pathlib import Path
import UnityPy, json, lz4.block, struct, hashlib
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

root = Path(__file__).resolve().parents[1]
source = (
    root
    / "shaders/particle-variant-inputs/019-0-StreamingAssets_assets_resources_objectids_weaponry_support.assets"
)
out = root / "shaders/particle-bytecode-proof"
rows = []
for obj in UnityPy.load(str(source)).objects:
    d = obj.read_typetree()
    if d["m_ParsedForm"]["m_Name"] != "Particles/Vertex Lit Smooth Blended H3VR":
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
        if (
            p.m_ProgramType.name != "kShaderGpuProgramDX11VertexSM40"
            or "_RECEIVE_SHADOWS" not in p.m_Keywords
            or "UNITY_SINGLE_PASS_STEREO" in p.m_Keywords
        ):
            continue
        code = bytes(p.m_ProgramCode)
        start = code.find(b"DXBC")
        assert start >= 0
        size = struct.unpack_from("<I", code, start + 24)[0]
        raw = code[start : start + size]
        name = f"program-{i}.dxbc"
        (out / name).write_bytes(raw)
        rows.append(
            dict(file=name, index=i, keywords=p.m_Keywords, sha256=hashlib.sha256(raw).hexdigest())
        )
    print(sorted({p.m_ProgramType.name for p in program.m_SubPrograms}))
(out / "manifest.json").write_text(json.dumps(rows, indent=2))
print("Extracted", len(rows), "original vertex programs")
