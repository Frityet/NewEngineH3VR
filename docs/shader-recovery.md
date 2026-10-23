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

The converter provisions the exporter on first use under `~/.cache/h3vr-converter`, then reuses a verified cache keyed by source commit, patch and host runtime. `--cache` changes the location; `--offline` requires an existing verified cache and skips provisioning. The output defaults to the conversion workspace path plus `-shaders`. This raw export does **not** automatically supply the validated `--shaders` overlay required by project preparation.

The command creates `shader-inputs/`, `assetripper-export/`,
`shader-input-manifest.json`, `assetripper.log`, and `recovery-receipt.json` under the output
directory. It refuses to overwrite a non-empty output or overlap the game, checkout,
workspace or tool cache. The legacy exporter starts on an ephemeral loopback port
and is stopped when the export finishes or is cancelled. Its standard output and
error are drained to the log. Choose a private output with enough free space.

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
