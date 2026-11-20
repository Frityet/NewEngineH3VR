# Getting the shaders back

H3VR's shaders ship compiled for Unity 5.6, and the normal AssetRipper 2 export can't turn them back into usable source. So shader recovery is its own step:

1. AssetsTools.NET 3.0.5 copies just the shader objects out of your installed game (read-only).
2. A separate AssetRipper 1.0.15 build decompiles those copies.

Your Steam files are never changed.

The output is a **raw export**, not something the converter can use directly. You have to link it and check it first (see [Linking and checking](#validation-boundary)).

## What you need

.NET 10 SDK, Git, internet access and your platform's native build tools. Nothing prebuilt, and no game files or shaders, is stored in this repo.

For an end-to-end conversion, pass `--dxbc-compiler` and `--spirv-cross` to the converter. It runs shader recovery and overlay preparation after extracting the game. Put the pinned Unity class database in the checkout's ignored `cache/` or the directory passed to `--cache`. On macOS, you can fetch and verify it with:

```sh
mkdir -p cache
curl -fL https://raw.githubusercontent.com/nesrak1/UABEA/057e2f6ae67ebc94a38faca9f946f8577162fa99/ReleaseFiles/classdata.tpk \
  -o cache/legacy-unity-classdata.tpk
printf '129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6  cache/legacy-unity-classdata.tpk\n' | shasum -a 256 -c -
```

## Recovering the shaders

```sh
dotnet run --project src/H3VR.Converter -- recover-shaders \
  --game "/path/to/H3VR" --output "/path/to/private-raw-shaders"
```

- The first run builds the exporter and caches it in the repo's ignored `cache/` folder. Later runs check its hashes before reusing it.
- `--cache` puts the cache somewhere else. `--offline` only uses a cached build that's already been checked.
- If you leave out `--output`, it uses your workspace path with `-shaders` on the end.
- The output folder must be empty and separate from the game, workspace and tool cache. Use an ignored folder under the checkout's `.local` directory or a separate private directory.
- Big shaders can take many minutes. Each request has a two-hour limit, and you can cancel.

You get these files in the output folder:

| File | What it is |
|---|---|
| `shader-inputs/` | the shader-only copies from the game |
| `assetripper-export/` | the decompiled shaders |
| `shader-input-manifest.json` | where each shader came from |
| `shader-export-identities.json` | which original object each exported file came from |
| `assetripper.log` | the exporter's output |
| `recovery-receipt.json` | hashes of everything above |

**If the exporter throws any error, the whole export is bad**, even if files were written. Failed output is kept for debugging. Don't use it as an overlay.

### How it tracks where shaders came from

The input manifest (schema 3) records, for each shader:
- the source file's SHA-256
- its entry index in the bundle (null for a plain assets file)
- its member name and path ID
- the hash of the shader-only copy

That pins down the exact original object. The identity files connect each original to its exported path and project GUID. This proves where a shader came from, not that it's correct.

The recovery receipt (schema 2) hashes the input manifest, exporter, every exported shader and the tool manifest, and notes whether the converter started the exporter.

## The exporter

The exporter writes HLSL `log2` and `exp2` for Direct3D's base-two logarithm and exponential instructions (used by the stock HDR color-grading shader and other lighting/image effects).

We use a fork of AssetRipper, pinned to [`8f3a527f19f1ac3423ced1abb40dfb29f733c27c`](https://github.com/Frityet/AssetRipper/tree/8f3a527f19f1ac3423ced1abb40dfb29f733c27c), on top of upstream `d142a4b46586acdf931aaefc34d3e9eed4b7158e`. It's built as a standalone executable, and its GPL-3.0-only license and source link are kept next to it. Follow that license if you share the executable.

To build it into a folder by hand:

```sh
dotnet run --project tools/ShaderExporter -- provision cache/assetripper-shader-manual
```

It prints the executable's path, and won't overwrite an existing folder.

What the fork adds:
- keeps every shader keyword variant
- translates Direct3D texture and structured buffer code
- on Metal, structured buffer reads use a separate element count (see [compute buffer sizes](platform-integrations.md#compute-buffer-sizes-on-metal)). Direct3D 11 and Vulkan get the size from the buffer itself.

<a id="validation-boundary"></a>
## Linking and checking

You can rerun recovery and linking as often as you like in the same workspace, as long as the game and extractor stay the same.

To prepare the graphics and compute overlay in one command, give it the completed cold extraction, raw recovery and translation tools from this guide.

```sh
dotnet run --project tools/ShaderOverlay -c Release -- prepare \
  --workspace "/path/to/private-workspace" \
  --recovery "/path/to/private-raw-shaders" \
  --steam-install "/path/to/H3VR" \
  --class-database "/path/to/legacy-unity-classdata.tpk" \
  --dxbc-compiler "/path/to/dxil-spirv" \
  --spirv-cross "/path/to/spirv-cross" \
  --output "/path/to/private-shader-overlay"
```

The command creates the output only after both stages succeed. Pass that directory to the converter's `--shaders` option. To diagnose either stage separately, use the commands below.

### 1. Link the export to the extraction

Extraction and shader recovery each record where every shader came from. This step joins the two:

```sh
dotnet run --project tools/ShaderOverlay -c Release -- link \
  --workspace "/path/to/private-workspace" \
  --recovery "/path/to/private-raw-shaders" \
  --steam-install "/path/to/H3VR" \
  --output "/path/to/private-shader-correspondence.json"
```

Shaders are matched by their saved identity and hashes, never by name. Any project path that doesn't match fails the command and is listed in the report.

### 2. Build an overlay candidate

Once everything matches, add `--overlay-candidate` to stage the shaders with their original GUIDs. Use it to find import, shader and material errors. Proving they render correctly still means compiling and running the original variants on Metal.

Also add `--repair-exclusive-keywords`. This rebuilds keyword groups from the original Direct3D vertex and fragment program pairs:
- It checks that their keywords match every generated guard.
- It recovers required choices, optional choices and constants.
- If the groups can't describe every variant, it keeps the verified lighting and geometry modes and leaves explicit guards on the rest. The compiler may still reject some of those.

This needs Unity's class database. Save it at `cache/legacy-unity-classdata.tpk`, or pass `--class-database`. The tested file is [this one](https://github.com/nesrak1/UABEA/blob/057e2f6ae67ebc94a38faca9f946f8577162fa99/ReleaseFiles/classdata.tpk), SHA-256 `129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6`.

The output is `shader-overlay-candidate.json`. It records:
- the database hash
- which passes changed, and the recovered groups
- whether the groups cover every variant exactly
- shader hashes before and after

Large passes are split into guarded include files (hashed in the receipt) that compile to the same original program. The converter checks this receipt against the extraction and makes sure every shader and include is there. **The receipt is required for macOS**, and for any overlay that comes with a variant catalog.

### Variant catalog

The link step also writes `shader-variant-domains.json`. It lists the real keyword combinations for every pass group whose original Direct3D programs match all the generated guards.

When Unity builds for Metal, Direct3D 11 or Vulkan, the build strips any combination that isn't in that list for its pass group. The guards in the source still catch anything the catalog can't vouch for. [Unity shader preprocessing](https://docs.unity3d.com/6000.7/Documentation/ScriptReference/Build.IPreprocessShaders.OnProcessShader.html)

If a material turns on several keywords from the same group, Unity picks the closest variant it has. Making sure that matches what Unity 5.6 did means comparing renders. [Unity shader keywords](https://docs.unity3d.com/6000.7/Documentation/Manual/shader-keywords.html)

### Why the raw export isn't enough

The recovery receipt marks the output `raw-export-unvalidated`, and the converter won't accept it as `ShaderSources`. AssetRipper's decompiler is known to get things wrong, so a file that isn't empty might still have bad:

- channel masks, texture swizzles or offsets
- reflection metadata or keyword variants
- Unity import, Metal pipeline creation, or how it looks

The pinned exporter and overlay tools apply the recorded source and keyword repairs. The Unity build still needs to compile the translated programs and render the target scenes. Validate again whenever the Steam build, exporter or Unity version changes.

**Don't replace every shader with one fallback shader just to make the project compile.**

## Compute shader recovery

The steps above only cover regular shaders. Steam build `25002541` also has 11 compute shaders, with 22 Direct3D kernels between them.

You need:
- [dxil-spirv](https://github.com/HansKristian-Work/dxil-spirv/tree/a781dce9b5f2dd076f419d7f98dcc2dcf5f011d5) at `a781dce9b5f2dd076f419d7f98dcc2dcf5f011d5`, built locally
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) from Vulkan SDK `1.4.357.0`

To build the pinned DXIL translator, check out that commit, run `git submodule update --init` and build the CLI with CMake.

```sh
cmake -S "/path/to/dxil-spirv-source" -B "/path/to/translator-build" \
  -DCMAKE_BUILD_TYPE=Release -DSPIRV_SKIP_TESTS=ON
cmake --build "/path/to/translator-build" --config Release --target dxil-spirv
```

Keep the translator in its build directory with its shared library.

```sh
dotnet run --project tools/ShaderOverlay -c Release -- compute \
  --workspace "/path/to/private-workspace" \
  --overlay "/path/to/private-shader-overlay" \
  --dxbc-compiler "/path/to/dxil-spirv" \
  --spirv-cross "/path/to/spirv-cross"
```

This adds `Assets/ComputeShader` to your overlay. It:

1. checks the exported compute shaders against the extraction receipt
2. translates the original DXBC to Metal by way of SPIR-V
3. puts back the original resource and constant names from Unity's metadata
4. records tool, source and output hashes, plus each kernel and its thread-group size

On import, the converter checks that receipt, keeps the GUIDs and uses Unity's compute shader importer. Translation stops on missing bindings or unsupported layouts. Unity's Metal check catches Windows-only programs, compile errors and missing entry points.
