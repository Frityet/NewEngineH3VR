from pathlib import Path
import UnityPy, json, lz4.block, struct, hashlib, subprocess
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

root = Path(__file__).resolve().parents[2]
out = Path(__file__).parent / "original"
r = next(
    r
    for r in json.loads((root / "audit-assets/all-shader-source-map.json").read_text())
    if r["source_file"] == "globalgamemanagers.assets" and r["source_path_id"] == 51
)
o = next(
    o
    for o in UnityPy.load(str(root / "audit-assets/all-shader-inputs" / r["copy_file"])).objects
    if o.path_id == 51
)
d = o.read_typetree()
ix = d["platforms"].index(4)
off = d["offsets"][ix]
sz = d["compressedLengths"][ix]
block = lz4.block.decompress(
    bytes(d["compressedBlob"])[off : off + sz], uncompressed_size=d["decompressedLengths"][ix]
)
pg = ShaderProgram(EndianBinaryReader(block, endian="<"), o.version)
rows = []
for pi, p in enumerate(d["m_ParsedForm"]["m_SubShaders"][0]["m_Passes"]):
    names = dict(p["m_NameIndices"])
    for sp in p["progFragment"]["m_SubPrograms"]:
        if sp["m_GpuProgramType"] not in (17, 18):
            continue
        i = sp["m_BlobIndex"]
        v = pg.m_SubPrograms[i]
        if set(v.m_Keywords) not in (
            {"FINALPASS", "COLOR_GRADING_HDR_3D"},
            {"FINALPASS", "COLOR_GRADING_HDR_3D", "BLOOM"},
        ):
            continue
        code = bytes(v.m_ProgramCode)
        start = code.find(b"DXBC")
        raw = code[start : start + struct.unpack_from("<I", code, start + 24)[0]]
        f = out / f"program-{i}.dxbc"
        f.write_bytes(raw)
        rows.append(
            {
                "program": i,
                "pass": pi,
                "keywords": v.m_Keywords,
                "params": [
                    {k: x for k, x in cb.items() if k != "m_StructParams"}
                    for cb in sp["m_ConstantBuffers"]
                ],
                "names": names,
                "sha256": hashlib.sha256(raw).hexdigest(),
            }
        )
(out / "manifest.json").write_text(json.dumps({"identity": r, "programs": rows}, indent=2))
subprocess.run(
    [
        "/opt/homebrew/bin/dotnet",
        str(root / "shaders/particle-bytecode-proof/bin/Release/net10.0/Disassemble.dll"),
        str(out),
    ],
    check=True,
)
print([(x["program"], x["pass"], x["keywords"]) for x in rows])
