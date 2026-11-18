# Shader source recovery

The normal AssetRipper 2 export does not reconstruct H3VR's compiled Unity 5.6
shader programs into usable source. This bootstrap uses AssetsTools.NET 3.0.5
to write read-only, shader-only copies of serialized Shader objects from a
locally installed game, then asks a separate AssetRipper 1.0.15 build to
decompile them. It does not modify Steam files.

## Prerequisites

- .NET 10 SDK, Git, network access, and the platform's native build tools.
- No prebuilt binary, Unity player, game asset, or recovered game shader is checked in.

## Run from the converter

No exporter executable path or configuration file is required:

```sh
dotnet run --project src/H3VR.Converter -- recover-shaders \
  --game "/path/to/H3VR" --output "/path/to/private-raw-shaders"
```

The converter provisions the exporter on first use under `~/.cache/h3vr-converter`, then reuses a verified cache keyed by source commit, patch, provisioning recipe and host runtime. `--cache` changes the location; `--offline` requires an existing verified cache and skips provisioning. The output defaults to the conversion workspace path plus `-shaders`. This raw export does **not** automatically supply the validated `--shaders` overlay required by project preparation.

The command creates `shader-inputs/`, `assetripper-export/`,
`shader-input-manifest.json`, `shader-export-identities.json`,
`assetripper.log`, and `recovery-receipt.json` under the output
directory. It refuses to overwrite a non-empty output or overlap the game, checkout,
workspace or tool cache. The legacy exporter starts on an ephemeral loopback port
and is stopped when the export finishes or is cancelled. Its standard output and
error are drained to the log. Choose a private output with enough free space.
Large shaders may take many minutes; each local service request has a two-hour
limit and can be cancelled. An exporter exception invalidates the entire export,
even if its service returned success and some files were written. Failed output
is retained for diagnosis and must not be treated as an overlay.

Input manifest schema 3 records each source file's SHA-256, the serialized bundle
entry index (null for a standalone assets file), the serialized member name, each
Shader's signed path ID, and the hash of the shader-only copy. Together these identify the original
serialized objects in the same local Steam input; a bundle entry index is scoped
to that exact source hash. The two export identity manifests associate the
original objects with exported paths and the recovered project GUIDs. An exact
identity match establishes provenance. Shader semantics, Metal compilation and
visual behavior are checked separately.
The schema 2 recovery receipt hashes that input manifest, the exporter executable,
every exported shader and, when available, the verified tool manifest (which
includes the pinned source commit, patch and cached file hashes). It also records
whether the converter started the exporter locally; an externally supplied
service is not authenticated by the local executable hash.

## Exporter provenance

The provisioning command clones AssetRipper at commit
`d142a4b46586acdf931aaefc34d3e9eed4b7158e`, applies the generic shader exporter
repairs in the bundled source patch, and publishes a self-contained executable for the
current platform. It deletes its temporary source checkout after the build and
leaves the upstream GPL-3.0-only license and corresponding patch beside the
executable. For development, the standalone provisioning tool is also available:

```sh
dotnet run --project tools/ShaderExporter -- provision "$HOME/.local/share/H3VR/assetripper-shader"
```

The command prints the executable path. It refuses to overwrite an existing
output directory. The exporter preserves all recovered shader keyword variants
and includes generic DirectX-to-HLSL fixes; it has no H3VR-specific runtime
hooks. Follow the license and source notice in the generated directory when
redistributing the patched executable.

## Validation boundary

The cold extractor records each exported shader's serialized member, bundle,
path ID, Unity GUID and project path. The raw exporter links its result to the
hashed shader-only input and original serialized object. Join the two receipts
after both commands complete.

```sh
python3 tools/ShaderExporter/link-exports.py \
  --workspace "/path/to/private-workspace" \
  --recovery "/path/to/private-raw-shaders" \
  --steam-install "/path/to/H3VR" \
  --output "/path/to/private-shader-correspondence.json"
```

Unmatched project paths fail the command and appear in the correspondence
report. The join verifies hashes and matches source objects by their
serialized identities. Matching shader names provide no identity evidence.
After a complete match, `--overlay-candidate` can stage private diagnostic
sources with the original project GUIDs and a correspondence receipt. Use the
candidate to measure import, shader and material failures. The rendering
gate still requires compiling and exercising the original reachable variants
on Metal.

Use `--repair-exclusive-keywords` alongside `--overlay-candidate` to restore
mutually exclusive light and shadow keyword groups from the original variant
metadata. This option uses Python 3.13 with `UnityPy==1.25.4` and `lz4==4.4.5`.
It records each changed pass and its before and after file hashes in
`shader-overlay-candidate.json`.

The receipt intentionally labels the result `raw-export-unvalidated`. Passing
this raw export to the converter as `ShaderSources` is not supported. In
particular, non-dummy output does not establish correct channel masks, texture
swizzles, offsets, reflection metadata, shader keyword variants, Unity import,
Metal pipeline creation, or visual equivalence. AssetRipper's decompiler has
had known semantic loss in these areas.

The prior local recovery reached a stronger state only after custom fixes and
targeted Unity/Metal tests. That workflow used generated identity maps and
manually reviewed source restorations (including public historical sources for
some shader families); its full validated overlay is not reconstructable from
the game directory by this bootstrap. A fresh installation still needs a
separate review and validation pass before an overlay can be trusted. Do not
replace every shader with one fallback shader to make the project compile.

## Compute shader recovery

The raw exporter covers Unity `Shader` objects. Steam build `25002541` also
contains eleven serialized `ComputeShader` objects with 22 Direct3D kernels.
Use Python 3.13 with `PyYAML==6.0.3`, a local build of `dxil-spirv` at
`a781dce9b5f2dd076f419d7f98dcc2dcf5f011d5`, and SPIRV-Cross from Vulkan SDK
`1.4.357.0`. [dxil-spirv build instructions](https://github.com/HansKristian-Work/dxil-spirv/tree/a781dce9b5f2dd076f419d7f98dcc2dcf5f011d5) · [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross)

```sh
python3 tools/ShaderExporter/recover-compute.py \
  --workspace "/path/to/private-workspace" \
  --overlay "/path/to/private-shader-overlay" \
  --dxbc-compiler "/path/to/dxil-spirv" \
  --spirv-cross "/path/to/spirv-cross"
```

The command adds `Assets/ComputeShader` to the existing local overlay.
It verifies the serialized exports against the extraction receipt, translates
the original DXBC through SPIR-V and restores the resource and constant
names from the same Unity metadata. It records the tool, source and output
hashes, each kernel and its original thread-group size. The importer verifies
that receipt, keeps the original GUIDs and changes the serialized references
to use Unity's compute source importer. Missing bindings and unsupported
layouts stop the translation. Unity's Metal validation checks for Windows-only
programs, compiler errors and missing entry points.
