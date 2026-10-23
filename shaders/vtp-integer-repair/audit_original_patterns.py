from pathlib import Path
import UnityPy, json, lz4.block, struct, hashlib, subprocess, re, collections
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

root = Path(__file__).resolve().parents[2]
r = next(
    r
    for r in json.loads((root / "audit-assets/all-shader-source-map.json").read_text())
    if r["source_file"] == "globalgamemanagers.assets" and r["source_path_id"] == 45
)
o = next(
    o
    for o in UnityPy.load(str(root / "audit-assets/all-shader-inputs" / r["copy_file"])).objects
    if o.path_id == 45
)
d = o.read_typetree()
ix = d["platforms"].index(4)
off = d["offsets"][ix]
sz = d["compressedLengths"][ix]
block = lz4.block.decompress(
    bytes(d["compressedBlob"])[off : off + sz], uncompressed_size=d["decompressedLengths"][ix]
)
pg = ShaderProgram(EndianBinaryReader(block, endian="<"), o.version)
uses = {}
for si, sub in enumerate(d["m_ParsedForm"]["m_SubShaders"]):
    for pi, p in enumerate(sub["m_Passes"]):
        names = dict(p["m_NameIndices"])
        param = names.get("_ParallaxInterpolation")
        for sp in p["progFragment"]["m_SubPrograms"]:
            if sp["m_GpuProgramType"] not in (17, 18):
                continue
            params = [
                v
                for cb in sp["m_ConstantBuffers"]
                for v in cb["m_VectorParams"]
                if v["m_NameIndex"] == param
            ]
            uses.setdefault(sp["m_BlobIndex"], []).append(
                {
                    "subshader": si,
                    "pass": pi,
                    "name": p["m_State"]["m_Name"],
                    "interpolation": params,
                }
            )
out = root / "shaders/vtp-integer-repair/original"
out.mkdir(parents=True, exist_ok=True)
rows = []
for i in sorted(uses):
    p = pg.m_SubPrograms[i]
    code = bytes(p.m_ProgramCode)
    start = code.find(b"DXBC")
    raw = code[start : start + struct.unpack_from("<I", code, start + 24)[0]]
    f = out / f"program-{i}.dxbc"
    if f.exists():
        assert f.read_bytes() == raw
    else:
        f.write_bytes(raw)
    rows.append(
        {
            "program": i,
            "keywords": p.m_Keywords,
            "uses": uses[i],
            "dxbc": str(f.relative_to(root)),
            "sha256": hashlib.sha256(raw).hexdigest(),
        }
    )
if any(not (root / x["dxbc"]).with_suffix(".asm").exists() for x in rows):
    subprocess.run(
        [
            "/opt/homebrew/bin/dotnet",
            str(root / "shaders/particle-bytecode-proof/bin/Release/net10.0/Disassemble.dll"),
            str(out),
        ],
        check=True,
        capture_output=True,
    )
for row in rows:
    p = (root / row["dxbc"]).with_suffix(".asm")
    s = p.read_text()
    row["asm_sha256"] = hashlib.sha256(p.read_bytes()).hexdigest()
    row["predicate_integer_one"] = len(
        re.findall(r"^\s*and r\d+\.[xyzw], r\d+\.[xyzw], l\(1\)$", s, re.M)
    )
    xors = re.findall(r"^\s*xor r\d+\.[xyzw], (cb\d+\[\d+\]\.[xyzw]), l\(2\)$", s, re.M)
    row["xor_two"] = len(xors)
    row["sign_mask"] = len(
        re.findall(r"^\s*and r\d+\.[xyzw], r\d+\.[xyzw], l\(0x80000000\)$", s, re.M)
    )
    row["integer_loops"] = len(re.findall(r"^\s*loop ", s, re.M))
    if xors:
        params = [v for u in row["uses"] for v in u["interpolation"]]
        assert params and all(v["m_Type"] == 1 and v["m_Dim"] == 1 for v in params)
        expected = {
            "cb0[" + str(v["m_Index"] // 16) + "]." + "xyzw"[(v["m_Index"] % 16) // 4]
            for v in params
        }
        assert len(expected) == 1 and set(xors) == expected, (row["program"], xors, expected)
        row["validated_int_uniform_operand"] = next(iter(expected))
    assert (row["predicate_integer_one"], row["xor_two"], row["sign_mask"]) in (
        (0, 0, 0),
        (3, 3, 3),
    ), row["program"]
report = {
    "identity": r,
    "original_raw_sha256": hashlib.sha256(o.get_raw_data()).hexdigest(),
    "dxbc_block_sha256": hashlib.sha256(block).hexdigest(),
    "pixel_programs": len(rows),
    "affected_programs": sum(bool(x["xor_two"]) for x in rows),
    "programs": rows,
}
(out / "manifest.json").write_text(json.dumps(report, indent=2))
print(
    "Pixel programs",
    len(rows),
    "affected",
    report["affected_programs"],
    "patterns",
    dict(
        collections.Counter(
            str((x["predicate_integer_one"], x["xor_two"], x["sign_mask"], x["integer_loops"]))
            for x in rows
        )
    ),
)
