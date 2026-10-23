# Shader source recovery

The normal AssetRipper 2 export does not reconstruct H3VR's compiled Unity 5.6
shader programs into usable source. This bootstrap uses AssetsTools.NET 3.0.5
to write read-only, shader-only copies of serialized Shader objects from a
locally installed game, then asks a separate AssetRipper 1.0.15 build to
decompile them. It does not modify Steam files.

## Prerequisites

- .NET 10 SDK, Git, network access, and the platform's native build tools.
- No prebuilt binary, Unity player, game asset, or recovered game shader is checked in.

## Build the shader exporter

The provisioning command clones AssetRipper at commit
`d142a4b46586acdf931aaefc34d3e9eed4b7158e`, applies the generic shader exporter
repairs in the bundled source patch, and publishes a self-contained executable for the
current platform. It deletes its temporary source checkout after the build and
leaves the upstream GPL-3.0-only license and corresponding patch beside the
executable. Choose a new output directory with enough space for the build:

```sh
dotnet run --project tools/ShaderExporter -- provision "$HOME/.local/share/H3VR/assetripper-shader"
```

The command prints the executable path. It refuses to overwrite an existing
output directory. The exporter preserves all recovered shader keyword variants
and includes generic DirectX-to-HLSL fixes; it has no H3VR-specific runtime
hooks. Follow the license and source notice in the generated directory when
redistributing the patched executable.

## Run the raw export

From the repository root:

```sh
dotnet run --project tools/ShaderRecovery -- \
  "$HOME/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data" \
  "$HOME/H3VR-shader-recovery" \
  "$HOME/.local/share/H3VR/assetripper-shader/AssetRipper.GUI.Free"
```

The command creates `shader-inputs/`, `assetripper-export/`,
`shader-input-manifest.json`, and `recovery-receipt.json` under the output
directory. It refuses to overwrite a
non-empty output, and refuses to put it inside the installed game's data folder.
The local AssetRipper service is started on loopback port 58948 and stopped when
the export finishes. Use an empty, private output directory with enough free
space for the serialized input and source project.

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
