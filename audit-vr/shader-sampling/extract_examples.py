from pathlib import Path
import json, struct, lz4.block, hashlib
import UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

ROOT = Path(__file__).resolve().parents[2]
out = ROOT / "build/shader-sampling/examples"
out.mkdir(exist_ok=True)
rows = json.loads((ROOT / "build/shader-sampling/original-instruction-audit.json").read_text())[
    "rows"
]
sources = {
    (s["source_file"], s["source_path_id"]): s
    for s in json.loads((ROOT / "audit-assets/all-shader-source-map.json").read_text())
}
specs = [
    ("Hidden/PostProcessing/Lut2DBaker", 2, 122),
    ("Hidden/PostProcessing/TemporalAntialiasing", 2, 70),
    ("Hidden/PostProcessing/SubpixelMorphologicalAntialiasing", 6, 302),
    ("Unlit/PIPScope", 1552, 702),
]
proof = []
for name, index, line in specs:
    r = next(r for r in rows if r["name"] == name)
    s = sources[(r["source"], r["path_id"])]
    obj = next(
        o
        for o in UnityPy.load(str(ROOT / "audit-assets/all-shader-inputs" / s["copy_file"])).objects
        if o.path_id == s["copy_path_id"]
    )
    d = obj.read_typetree()
    ix = d["platforms"].index(4)
    off = d["offsets"][ix]
    size = d["compressedLengths"][ix]
    block = lz4.block.decompress(
        bytes(d["compressedBlob"])[off : off + size], uncompressed_size=d["decompressedLengths"][ix]
    )
    p = ShaderProgram(EndianBinaryReader(block, endian="<"), obj.version).m_SubPrograms[index]
    code = bytes(p.m_ProgramCode)
    off = code.find(b"DXBC")
    raw = code[off : off + struct.unpack_from("<I", code, off + 24)[0]]
    file = out / (name.replace("/", "_") + f"-program-{index}.dxbc")
    file.write_bytes(raw)
    path = (
        ROOT / "unity-full" / r["path"]
        if name != "Unlit/PIPScope"
        else ROOT / "unity-full/Assets/NativeShaders/PIPScopeVariants/variant-1520.cginc"
    )
    lines = path.read_text().splitlines()
    proof.append(
        dict(
            name=name,
            source=r["source"],
            path_id=r["path_id"],
            guid=r["guid"],
            original_program=next(p for p in r["programs"] if p["index"] == index),
            dxbc=str(file.relative_to(ROOT)),
            staged_path=str(path.relative_to(ROOT)),
            staged_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
            staged_line=line,
            staged_excerpt=lines[line - 1 : line + 3],
        )
    )
(out / "proof.json").write_text(json.dumps(proof, indent=2))
