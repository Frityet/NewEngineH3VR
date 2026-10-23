from pathlib import Path
import json, hashlib, base64, subprocess, lz4.block, struct, collections
import UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "build/shader-declarations"
OUT.mkdir(exist_ok=True)
identities = json.loads(
    (ROOT / "build/shader-identities/full-shader-identity-map.json").read_text()
)
source = {
    (r["source_file"], r["source_path_id"]): r
    for r in json.loads((ROOT / "audit-assets/all-shader-source-map.json").read_text())
}
cache = {}
errors = []
findings = []
stats = collections.Counter()
cross = []
examples = []
stage_candidates = []
proc = subprocess.Popen(
    ["dotnet", str(ROOT / "audit-vr/shader-declarations/bin/Release/net10.0/Declarations.dll")],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    text=True,
    bufsize=1,
)
byfile = collections.defaultdict(list)
for ident in identities:
    src = source[(ident["original_source_file"], ident["path_id"])]
    byfile[src["copy_file"]].append((src, ident))
for fn, objects in byfile.items():
    env = UnityPy.load(str(ROOT / "audit-assets/all-shader-inputs" / fn))
    lookup = {o.path_id: o for o in env.objects}
    for src, ident in objects:
        stats["shaderIdentities"] += 1
        declarations = collections.defaultdict(dict)
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
            for si, sub in enumerate(d["m_ParsedForm"]["m_SubShaders"]):
                for pi, pa in enumerate(sub["m_Passes"]):
                    names = {v: k for k, v in pa["m_NameIndices"]}
                    for stage in [
                        "progVertex",
                        "progFragment",
                        "progGeometry",
                        "progHull",
                        "progDomain",
                    ]:
                        for sp in pa[stage]["m_SubPrograms"]:
                            i = sp["m_BlobIndex"]
                            p = prog.m_SubPrograms[i]
                            code = bytes(p.m_ProgramCode)
                            start = code.find(b"DXBC")
                            if start < 0:
                                continue
                            stats["programReferences"] += 1
                            size = struct.unpack_from("<I", code, start + 24)[0]
                            raw = code[start : start + size]
                            sha = hashlib.sha256(raw).hexdigest()
                            if sha not in cache:
                                proc.stdin.write(
                                    json.dumps(dict(code=base64.b64encode(raw).decode())) + "\n"
                                )
                                proc.stdin.flush()
                                cache[sha] = json.loads(proc.stdout.readline())
                            result = cache[sha]
                            if "error" in result:
                                errors.append(
                                    dict(shader=ident["name"], program=i, error=result["error"])
                                )
                                continue
                            params = {
                                t["m_Index"]: {**t, "name": names[t["m_NameIndex"]]}
                                for t in sp["m_TextureParams"]
                            }
                            context = dict(
                                shader=ident["name"],
                                source=ident["original_source_file"],
                                pathID=ident["path_id"],
                                exportedPath=ident["exported_path"],
                                copyFile=fn,
                                copyPathID=src["copy_path_id"],
                                program=i,
                                subshader=si,
                                passIndex=pi,
                                stage=stage,
                                keywords=p.m_Keywords,
                                sha256=sha,
                            )
                            grouped = collections.defaultdict(list)
                            for param in params.values():
                                mode = result["modes"].get(str(param["m_SamplerIndex"]))
                                signature = (stage, param["m_Dim"], mode)
                                declarations[(si, pi, param["name"])].setdefault(
                                    signature, dict(**context, parameter=param, mode=mode)
                                )
                            for op in result["operations"]:
                                stats["samplingInstructions"] += 1
                                grouped[op["resource"]].append(op)
                                if op["compare"]:
                                    stats["comparisonInstructions"] += 1
                                    if len(examples) < 8:
                                        examples.append(
                                            dict(
                                                **context,
                                                operation=op,
                                                parameter=params.get(op["resource"]),
                                                modes=result["modes"],
                                            )
                                        )
                                param = params.get(op["resource"])
                                if not param:
                                    errors.append(
                                        dict(
                                            **context,
                                            reason="Missing texture parameter",
                                            operation=op,
                                        )
                                    )
                                    continue
                                ownmode = result["modes"].get(str(param["m_SamplerIndex"]))
                                usedmode = result["modes"].get(str(op["sampler"]))
                                if op["sampler"] != param["m_SamplerIndex"]:
                                    stats["crossSamplerInstructions"] += 1
                                    if op["compare"]:
                                        stats["crossComparisonInstructions"] += 1
                                        cross.append(
                                            dict(
                                                **context,
                                                parameter=param,
                                                ownerMode=ownmode,
                                                usedMode=usedmode,
                                                operation=op,
                                            )
                                        )
                                if op["compare"] and ownmode != "mode_comparison":
                                    findings.append(
                                        dict(
                                            **context,
                                            reason="Comparison resource declared float4 by owner sampler rule",
                                            parameter=param,
                                            ownerMode=ownmode,
                                            usedMode=usedmode,
                                            operation=op,
                                        )
                                    )
                            for resource, ops in grouped.items():
                                if any(o["compare"] for o in ops) and any(
                                    not o["compare"] for o in ops
                                ):
                                    findings.append(
                                        dict(
                                            **context,
                                            reason="Same resource used for comparison and ordinary sampling",
                                            parameter=params.get(resource),
                                            operations=ops,
                                        )
                                    )
            for key, values in declarations.items():
                vertex = [v for (stage, dim, mode), v in values.items() if stage == "progVertex"]
                pixel = [v for (stage, dim, mode), v in values.items() if stage == "progFragment"]
                for v in vertex:
                    for f in pixel:
                        if (v["mode"] == "mode_comparison") != (
                            f["mode"] == "mode_comparison"
                        ) or v["parameter"]["m_Dim"] != f["parameter"]["m_Dim"]:
                            stage_candidates.append(
                                dict(
                                    reason="Potential vertex/pixel declaration conflict across original variants (pairing not established)",
                                    vertex=v,
                                    fragment=f,
                                )
                            )
        except Exception as e:
            errors.append(dict(shader=ident["name"], error=repr(e)))
    print(
        fn,
        dict(stats),
        "unique",
        len(cache),
        "findings",
        len(findings),
        "errors",
        len(errors),
        flush=True,
    )
proc.stdin.close()
proc.wait()
provenance = {
    str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
    for p in [
        ROOT / "shaders/texture-declaration-template.cs.txt",
        ROOT / "build/shader-identities/full-shader-identity-map.json",
        ROOT / "audit-assets/all-shader-source-map.json",
        *[(ROOT / "audit-assets/all-shader-inputs" / fn) for fn in byfile],
    ]
}
report = dict(
    provenance=provenance,
    comparisonExamples=examples,
    crossStageCandidates=stage_candidates,
    stats=dict(stats),
    uniqueDXBCPrograms=len(cache),
    findings=findings,
    crossComparison=cross,
    errors=errors,
)
(OUT / "original-sampler-resource-audit.json").write_text(json.dumps(report, indent=2))
print(
    json.dumps(
        dict(
            stats=dict(stats),
            uniqueDXBCPrograms=len(cache),
            findings=len(findings),
            crossComparison=len(cross),
            errors=len(errors),
        ),
        indent=2,
    )
)
