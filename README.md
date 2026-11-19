# H3VR → Unity 2021

Tooling for importing your installed Steam copy of H3VR into a private Unity **2021.3.45f2** project. The Apple Silicon player uses Metal and the generic MacVR OpenXR runtime.

This repository contains conversion tools, compatibility code and tests. It contains no game assemblies, extracted assets, shaders, Unity editor/player binaries, or prebuilt game. All extraction and rebuilding happen in a private workspace on your computer.

**Status.** A cold Steam import produced a signed ARM64 Metal player. It initialized Steamworks and rendered the stock menu in both eyes through MacVR's synthetic OpenXR runtime. The importer requires a validated, identity-matched shader overlay. [Validation and remaining gates](docs/validation.md)

## Requirements

- Your installed copy of H3VR; the converter reads it without changing it.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Unity **2021.3.45f2**, activated on your machine, with the player build module for your target.
- Git and internet access on first extraction. The converter manages the pinned [AssetRipper 2.0.0](https://github.com/AssetRipper/AssetRipper/releases/tag/2.0.0) dependency and caches its worker.
- A local, identity-matched shader source overlay, including its original `.meta` files and include files. [Shader recovery tooling and limitations](docs/shader-recovery.md).

Apple Silicon selects the macOS target by default. The supported Steam input is app 450540 build 25002541. Build the local Metal, Steamworks and FMOD integration before generating the macOS project. The macOS build uses the pinned Unity class database in the converter cache to restore Steam's realtime lighting probes before signing. The Windows x64 target uses Unity OpenXR. [Platform prerequisites and commands](docs/platform-integrations.md) · [Class database and digest](docs/shader-recovery.md#validation-boundary)

## Run

Clone this repository normally; recursive submodule setup is optional. From the checkout, run the guided terminal interface:

```sh
dotnet run --project src/H3VR.Converter -- --tui
```

The wizard detects standard Steam libraries and the matching Unity Hub installation, lets you review or edit paths, then runs the conversion. If more than one installation is found, it asks you to choose. Use a new, empty workspace outside the game and checkout. The default is `~/H3VR-Unity2021`.

For scripts, use flags directly. No JSON file or separately started AssetRipper service is required:

```sh
dotnet run --project src/H3VR.Converter -- \
  --game "/path/to/H3VR" \
  --unity "/path/to/Unity/2021.3.45f2" \
  --workspace "/path/to/private-workspace" \
  --integration "/path/to/private/integration/integration.json" \
  --shaders "/path/to/validated-shader-overlay" \
  --through build
```

`convert` is the default command and stops after `validate` unless you select another stage. The stages are `extract`, `managed`, `project`, `validate`, and `build`. They retarget the installed managed assemblies, replace legacy VR calls, preserve serialized script identities and execution orders, configure OpenXR, and rebuild bundles with the original asset addresses. Failed checks stop conversion. The editor verifies both scenes and prefabs without starting gameplay.

For macOS project and player stages, use the converter's own extraction in a fresh workspace. `--recovered` is a comparison input allowed only through `managed`; it cannot supply a macOS player build.

An existing converter extraction can also serve several target workspaces.
`--reuse-extraction` verifies the current Steam inputs, exporter and extracted
file inventory before reading that recovery tree. Give each target its own
writable workspace. The extraction lease allows concurrent readers and keeps
the original converter from changing the source during a build.

```sh
dotnet run --project src/H3VR.Converter -c Release -- convert \
  --target windows --through build --game "$STEAM" --unity "$UNITY" \
  --reuse-extraction "$EXTRACTED_WORKSPACE" --shaders "$SHADER_OVERLAY" \
  --workspace "$WINDOWS_WORKSPACE"

dotnet run --project src/H3VR.Converter -c Release -- convert \
  --target linux --build-probe --through build --game "$STEAM" --unity "$UNITY" \
  --reuse-extraction "$EXTRACTED_WORKSPACE" --shaders "$SHADER_OVERLAY" \
  --workspace "$LINUX_WORKSPACE"
```

Use the Windows and Linux Mono support modules for the same editor version.
The Linux probe compiles the scenes and bundles into a Vulkan player for
build verification. Linux gameplay requires its own compatible XR integration
and native Steamworks, FMOD and video support. [Unity OpenXR runtime matrix](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.13/manual/index.html#runtimes)

Useful commands and options:

```sh
dotnet run --project src/H3VR.Converter -- --help
dotnet run --project src/H3VR.Converter -- doctor --game "/path/to/H3VR" --unity "/path/to/Unity"
dotnet run --project src/H3VR.Converter -- status --workspace "/path/to/private-workspace"
dotnet run --project src/H3VR.Converter -- --game "/path/to/H3VR" --through extract
dotnet run --project src/H3VR.Converter -- recover-shaders --game "/path/to/H3VR" --output "/path/to/raw-shaders"
```

`--target macos` makes the Apple Silicon selection explicit. See `--help` for comparison and other conversion targets. NativeH3VR stores verified tool builds in the checkout's ignored `cache/` directory. Use `--cache` to specify an external path and `--offline` to require a verified cached tool build. Unity Package Manager and .NET restore use their own settings. `--no-color` and `NO_COLOR` disable ANSI colors. `--tui` requires an interactive terminal.

Saved JSON setups remain optional: `init /path/to/settings.json` creates one, and `--config /path/to/settings.json` loads it. Flags override saved values. Flag paths resolve against your current directory; JSON paths resolve against the file's directory. The former `AssetRipperUrl` setting is obsolete and ignored.

The output is `workspace/project` and, after a successful macOS build, `workspace/player/NativeH3VR.app`. Logs, migration reports, unsupported VR API reports and SHA-256 receipts stay in that workspace. No upload or game launch is performed. `status` lists recorded stages; repeating the conversion checks input bytes and recorded outputs before resuming. You can extract without Unity, then add `--unity` and `--shaders` later in the same workspace. Settings only affect receipts for stages that use them; changing inputs to an already completed stage requires a new workspace. Use the converter to perform the initial Unity import: it disables Unity's assembly updater because the assemblies are already explicitly retargeted and verified.

## AssetRipper integration

`extern/AssetRipper` is a submodule pinned to **2.0.0**, commit `1ac666f47d8e9dedf96afb0b914c70d7656151ea`. On first extraction, the converter initializes that submodule and builds `tools/AssetRipper.Worker` against its C# export API. Later runs verify and reuse a content-addressed worker cache. Local changes or an unexpected revision are never reset automatically. The CLI and synthetic tests build without initializing the submodule.

The worker runs as an owned child process, writes `.conversion/assetripper.log`, and stops when conversion is cancelled. Extraction goes into a temporary candidate, published only after a project is produced. Failed candidates are removed. Shader source recovery uses the same CLI and builds the pinned AssetRipper fork. [Shader recovery and validation](docs/shader-recovery.md)

## Storage

On macOS the converter uses **APFS copy-on-write clones** for project staging when the source and destination support them. Edits never write through to the original. Other filesystems use normal copies. Keep the workspace on the same APFS volume as the recovery to share unchanged data. Finder/`du` can count a shared extent in several directories; available volume space is the useful measure.

Failed private staging candidates are cleaned automatically. Rebuilt bundles
move from temporary output into the finished player's `StreamingAssets`
directory. The Unity project keeps its source assets and the player holds
the rebuilt bundle files for its target. Retain the extraction workspace to
reuse it across builds. Unity's `Library` directories and .NET `bin`/`obj`
directories are regenerable and can be removed once their editor or process is
closed.

## Development

- `src/H3VR.Converter`: CLI, safe staging, project/bundle migration and publication checks.
- `tools`: managed retargeting/verification, OpenXR IL patching, shader export tooling.
- `runtime` contains managed Unity API compatibility, the Metal OpenXR plug-in, platform adapters and the optional native mod bootstrap.
- `editor`: project validation, OpenXR settings and bundle/player building.
- `tests`: small synthetic fixtures; no copy of the game is required.

```sh
dotnet run --project tests/H3VR.Converter.Tests -c Release
dotnet run --project tools/OpenXRPatch.Tests -c Release
dotnet format H3VR.Converter.sln --verify-no-changes
```

The optional direct-export integration check generates a tiny synthetic Unity asset, provisions the worker, and tests both online and cached/offline export. It needs network access on first use but no game or editor:

```sh
dotnet run --project tools/AssetRipper.Worker.Tests -c Release
```

The macOS bridge uses the packaged [MacVR runtime](https://github.com/Frityet/MacVR) through standard OpenXR. [OpenXR behavior and limits](docs/openxr.md)

Before publishing, stage only authored sources and run:

```sh
dotnet run --project src/H3VR.Converter -- check-publication
```

The checker reads the **staged Git blobs**, rejects assets/binaries/generated paths, and does not treat `.gitignore` alone as an audit. Source provenance still needs review. See [publication policy](docs/publication.md). The tooling is GPL-3.0 under [LICENSE](LICENSE); that license does not cover the user's game or Unity installation.
