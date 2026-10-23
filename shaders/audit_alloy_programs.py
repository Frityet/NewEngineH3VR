from pathlib import Path
import json, hashlib, lz4.block, collections
import UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

root = Path(__file__).resolve().parents[1]
source = json.loads((root / "audit-assets/all-shader-source-map.json").read_text())
rows = []
for r in source:
    if r["name"] != "Alloy/Core":
        continue
    env = UnityPy.load(str(root / "audit-assets/all-shader-inputs" / r["copy_file"]))
    obj = next(o for o in env.objects if o.path_id == r["copy_path_id"])
    d = obj.read_typetree()
    ix = d["platforms"].index(4)
    off = d["offsets"][ix]
    size = d["compressedLengths"][ix]
    blob = bytes(d["compressedBlob"])
    block = lz4.block.decompress(
        blob[off : off + size], uncompressed_size=d["decompressedLengths"][ix]
    )
    prog = ShaderProgram(EndianBinaryReader(block, endian="<"), obj.version)
    programs = [
        {
            "stage": p.m_ProgramType.name,
            "keywords": sorted(p.m_Keywords),
            "sha256": hashlib.sha256(bytes(p.m_ProgramCode)).hexdigest(),
        }
        for p in prog.m_SubPrograms
    ]
    rows.append(
        {
            "source_file": r["source_file"],
            "path_id": r["source_path_id"],
            "dxbc_block_sha256": hashlib.sha256(block).hexdigest(),
            "program_count": len(programs),
            "stages": dict(collections.Counter(p["stage"] for p in programs)),
            "unique_keyword_sets": len({tuple(p["keywords"]) for p in programs}),
            "programs": programs,
        }
    )
    print(r["source_file"], len(programs), rows[-1]["unique_keyword_sets"], flush=True)
(root / "shaders/alloy-original-program-audit.json").write_text(json.dumps(rows, indent=2))
