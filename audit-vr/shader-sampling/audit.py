from pathlib import Path
import json, re, hashlib, base64, subprocess, lz4.block, struct, collections
import UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "build/shader-sampling"
OUT.mkdir(exist_ok=True)
identities = json.loads(
    (ROOT / "build/shader-identities/full-shader-identity-map.json").read_text()
)
source = {
    (r["source_file"], r["source_path_id"]): r
    for r in json.loads((ROOT / "audit-assets/all-shader-source-map.json").read_text())
}
cache = {}
rows = []
errors = []
include_cache = {}


def readtree(p, seen=None):
    if p in include_cache:
        return include_cache[p]
    seen = set() if seen is None else seen
    if p in seen or not p.exists():
        return ""
    seen.add(p)
    s = p.read_text(errors="replace")
    parts = [s]
    for inc in re.findall(r'#include\s+"([^"]+)"', s):
        q = ROOT / "unity-full" / inc if inc.startswith("Assets/") else p.parent / inc
        if q.exists():
            parts.append(readtree(q, seen))
    out = "\n".join(parts)
    include_cache[p] = out
    return out


proc = subprocess.Popen(
    ["dotnet", str(ROOT / "audit-vr/shader-sampling/bin/Release/net10.0/Sampling.dll")],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    text=True,
    bufsize=1,
)
byfile = collections.defaultdict(list)
for ident in identities:
    p = ROOT / "unity-full" / ident["exported_path"]
    text = readtree(p)
    r = dict(
        name=ident["name"],
        path=ident["exported_path"],
        guid=ident["guid"],
        source=ident["original_source_file"],
        path_id=ident["path_id"],
        staged_sha256=hashlib.sha256(p.read_bytes()).hexdigest() if p.exists() else None,
        recovered=("float4 tmp" in text),
        explicitUnsupported=sorted(set(re.findall(r"unsupported_\w+", text))),
        programs=[],
    )
    rows.append(r)
    if not r["recovered"]:
        continue
    src = source[(r["source"], r["path_id"])]
    byfile[src["copy_file"]].append((src, r))
for fn, objects in byfile.items():
    env = UnityPy.load(str(ROOT / "audit-assets/all-shader-inputs" / fn))
    lookup = {o.path_id: o for o in env.objects}
    for src, r in objects:
        try:
            obj = lookup[src["copy_path_id"]]
            d = obj.read_typetree()
            if 4 not in d["platforms"]:
                continue
            ix = d["platforms"].index(4)
            off = d["offsets"][ix]
            size = d["compressedLengths"][ix]
            blob = bytes(d["compressedBlob"])
            block = lz4.block.decompress(
                blob[off : off + size], uncompressed_size=d["decompressedLengths"][ix]
            )
            prog = ShaderProgram(EndianBinaryReader(block, endian="<"), obj.version)
            uses = collections.defaultdict(list)
            for si, sub in enumerate(d["m_ParsedForm"]["m_SubShaders"]):
                for pi, pa in enumerate(sub["m_Passes"]):
                    for stage in [
                        "progVertex",
                        "progFragment",
                        "progGeometry",
                        "progHull",
                        "progDomain",
                    ]:
                        for sp in pa[stage]["m_SubPrograms"]:
                            uses[sp["m_BlobIndex"]].append(
                                dict(
                                    subshader=si,
                                    pass_index=pi,
                                    stage=stage,
                                    type=sp["m_GpuProgramType"],
                                    hardwareTier=sp["m_ShaderHardwareTier"],
                                )
                            )
            for i, p in enumerate(prog.m_SubPrograms):
                code = bytes(p.m_ProgramCode)
                start = code.find(b"DXBC")
                if start < 0:
                    continue
                size = struct.unpack_from("<I", code, start + 24)[0]
                raw = code[start : start + size]
                sha = hashlib.sha256(raw).hexdigest()
                if sha not in cache:
                    proc.stdin.write(
                        json.dumps(dict(sha256=sha, code=base64.b64encode(raw).decode())) + "\n"
                    )
                    proc.stdin.flush()
                    result = json.loads(proc.stdout.readline())
                    cache[sha] = result
                    if "error" in result:
                        errors.append(dict(shader=r["name"], program=i, **result))
                result = cache[sha]
                if result.get("operations"):
                    r["programs"].append(
                        dict(
                            index=i,
                            stage=p.m_ProgramType.name,
                            keywords=p.m_Keywords,
                            sha256=sha,
                            uses=uses[i],
                            sampleCount=result["sampleCount"],
                            operations=result["operations"],
                        )
                    )
        except Exception as e:
            errors.append(dict(shader=r["name"], error=repr(e)))
        print(r["name"], len(r["programs"]), len(cache), flush=True)
proc.stdin.close()
proc.wait()
(OUT / "original-instruction-audit.json").write_text(
    json.dumps(dict(rows=rows, uniquePrograms=len(cache), errors=errors), indent=2)
)
summary = dict(
    shaderIdentities=len(rows),
    recoveredIdentities=sum(r["recovered"] for r in rows),
    originalAffectedIdentities=sum(bool(r["programs"]) for r in rows),
    uniquePrograms=len(cache),
    errors=errors,
    families=sorted({r["name"] for r in rows if r["programs"]}),
)
(OUT / "summary.json").write_text(json.dumps(summary, indent=2))
print(json.dumps(summary, indent=2))
