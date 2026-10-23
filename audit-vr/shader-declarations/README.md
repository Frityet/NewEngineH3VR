# Original texture declaration audit

Read-only audit of the 277 exact original shader identities backing the recovered full project. Inputs are extracted original shader-only serialized files, not an Editor import. Reproduce with:

```
dotnet build audit-vr/shader-declarations/Declarations.csproj -c Release
audit-assets/.venv/bin/python audit-vr/shader-declarations/audit.py
```

The auditor decodes each DX11 program to DXBC assembly, joins texture register `tN` to the original pass `m_TextureParams` entry, and checks each sample/gather instruction's resource and sampler register against `m_SamplerIndex` and decoded `dcl_sampler` mode. It detects comparison instructions using a resource whose own sampler would cause the proposed declaration template to select float4, and resources used for both ordinary and comparison sampling in a program. It also looks for conflicting dimension/comparison declarations by texture name between vertex and pixel stages in the same pass, including across original variants. This last check is conservative: it does not presume variants are paired.

`build/shader-declarations/original-sampler-resource-audit.json` records original input hashes, the reviewed template hash, comparison instruction examples, counts, findings, and errors.

Results:

- 277 original shader identities; 57,957 distinct DXBC bytecode programs; 335,666 program references (references include repeated shader identities, tiers and variants).
- 1,588,366 sample/gather instructions across those references.
- 44,516 comparison instructions: all use the resource's own declared comparison sampler.
- 111,974 ordinary cross-sampler instructions, confirming resource identity cannot generally be replaced with sampler ownership.
- Zero cross-sampler comparison cases, zero mixed ordinary/comparison resource cases, zero vertex/pixel declaration dimension/mode conflicts, zero decode or missing-parameter errors.

No counterexample to the reviewed comparison-based scalar declaration rule exists in this bounded original DX11 corpus. This is not a general HLSL theorem or proof of arbitrary future shaders. It does not validate emitted arithmetic, Metal rendering, sampler filtering, or runtime branch selection. No production shader, emitter, recovered source, proof candidate or full Unity project was modified.
