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

The converter provisions the exporter on first use in the checkout's ignored `cache/` directory. It verifies the fork commit, upstream base, host runtime and payload hashes on reuse. Use `--cache` to specify an external path and `--offline` to require a verified cached tool build. The output defaults to the conversion workspace path plus `-shaders`. Project preparation requires a validated `--shaders` overlay.

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
every exported shader and, when available, the verified tool manifest with the
pinned fork and cached file hashes. It also records
whether the converter started the exporter locally. A locally verified tool
manifest identifies the fork and its upstream base.

## Exporter provenance

The exporter writes HLSL `log2` and `exp2` for Direct3D's logarithm and exponential instructions. The stock HDR grading shader uses the base-two logarithm when it looks up a color in its 3D table. The instruction base also matters for unpaired uses in lighting and other image effects. [Direct3D log instruction](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/log--sm4---asm-) · [Direct3D exp instruction](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/exp--sm4---asm-)

The provisioning command builds Frityet's AssetRipper fork at
`8f3a527f19f1ac3423ced1abb40dfb29f733c27c` and verifies its upstream base
`d142a4b46586acdf931aaefc34d3e9eed4b7158e`. It removes its temporary checkout
after publishing a self-contained executable and keeps the GPL-3.0-only
license and the source URL alongside it. The standalone provisioning command
can also write to a new local directory. [Pinned source](https://github.com/Frityet/AssetRipper/tree/8f3a527f19f1ac3423ced1abb40dfb29f733c27c)

```sh
dotnet run --project tools/ShaderExporter -- provision cache/assetripper-shader-manual
```

The command prints the executable path. It refuses to overwrite an existing
output directory. The exporter preserves all recovered shader keyword variants
and includes Direct3D texture and structured-buffer translations. Translated
Metal structured-buffer reads use a companion element count and a valid
bound buffer. The managed macOS retargeter supplies the companion on the
same material or property block as the stock binding. Direct3D 11 and
Vulkan query the buffer dimensions in the shader.
Follow the license and source notice in the generated directory when
redistributing the fork executable.

## Validation boundary

The extraction receipt binds the Steam inputs and the extraction worker.
Shader recovery and linking can iterate against that receipt in the same
private workspace while the game and extractor stay fixed.

The cold extractor records each exported shader's serialized member, bundle,
path ID, Unity GUID and project path. The raw exporter links its result to the
hashed shader-only input and original serialized object. Join the two receipts
after both commands complete.

```sh
dotnet run --project tools/ShaderOverlay -c Release -- link \
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

Use `--repair-exclusive-keywords` alongside `--overlay-candidate` to recover
keyword groups from the original paired Direct3D vertex and fragment programs.
The linker checks that their keyword sets match every generated guard. It
recovers required choices, optional choices and constants. When those groups
cannot express the entire variant table, the linker preserves verified
required lighting and geometry modes and keeps the explicit guards for unsupported
combinations. The compiler can still reject some combinations in those passes.
Save the generic class database at `cache/legacy-unity-classdata.tpk` or
supply `--class-database` with its local path. The tested database has SHA-256
`129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6`. [Pinned class database](https://github.com/nesrak1/UABEA/blob/057e2f6ae67ebc94a38faca9f946f8577162fa99/ReleaseFiles/classdata.tpk)

`shader-overlay-candidate.json` records the database hash, changed passes,
recovered groups, whether the groups describe the exact variant domain, and
before and after shader hashes. The linker partitions large passes into
guarded includes. The compiler selects the same exact guard and original
program body, and the receipt records each include's hash. The project stage
verifies a supplied receipt against the extraction and checks that every
original graphics shader and generated include is present. This receipt is
required for the macOS target and for any overlay that supplies a catalog.

The same link command writes `shader-variant-domains.json` for pass groups
whose original Direct3D programs match every generated guard. The receipt
seals the catalog. When Unity builds Metal, Direct3D 11 or Vulkan bundles or
players, the preprocessor compares requested keywords and platform defines
with those original rows. It removes combinations absent from every verified
domain for the matched pass group. Source guards still surface combinations
the catalog cannot prove. [Unity shader preprocessing](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Build.IPreprocessShaders.OnProcessShader.html)

When a material enables several keywords from one group, Unity can choose a
closest variant. Matching Unity 5.6's visual choice requires a rendering
comparison. [Unity shader keywords](https://docs.unity3d.com/cn/2021.3/Manual/shader-keywords-scripts.html)

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
Use a local build of `dxil-spirv` at
`a781dce9b5f2dd076f419d7f98dcc2dcf5f011d5`, and SPIRV-Cross from Vulkan SDK
`1.4.357.0`. [dxil-spirv build instructions](https://github.com/HansKristian-Work/dxil-spirv/tree/a781dce9b5f2dd076f419d7f98dcc2dcf5f011d5) · [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross)

```sh
dotnet run --project tools/ShaderOverlay -c Release -- compute \
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
