# H3VR → Unity 2021

C# tooling for converting your own installed copy of H3VR to **Unity 2021.3.45f2**, the version used by the original experiment. VR goes through **OpenXR and Unity XR**, with no dependency on a particular headset or ValveIndexMacOS.

This repository contains conversion tools, compatibility code and tests. It contains no game assemblies, extracted assets, shaders, Unity editor/player binaries, or prebuilt game. All extraction and rebuilding happen in a private workspace on your computer.

**Status:** experimental. Managed migration, project preparation, original bundle address mapping and Unity OpenXR editor configuration have been exercised locally. A fresh game-to-playable-player conversion is **not yet unattended**: faithful shader restoration still needs a locally validated shader overlay. The new OpenXR player has not been playtested. See [validation](docs/validation.md) before assuming gameplay parity.

## Requirements

- Your installed copy of H3VR; the converter reads it without changing it.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Unity **2021.3.45f2**, activated on your machine, with the player build module for your target.
- [AssetRipper 2.0](https://github.com/AssetRipper/AssetRipper) for extraction, or an existing complete AssetRipper recovery.
- A local, identity-matched shader source overlay, including its original `.meta` files and include files. [Shader recovery tooling and limitations](docs/shader-recovery.md).

Windows x64 / D3D11 is the default player target. Unity's pinned OpenXR provider does not provide a macOS player backend. Other platforms require an explicit compatible Unity XR provider and native game dependencies; an OpenXR runtime alone does not supply those. See [platform integrations](docs/platform-integrations.md).

## Run

Run commands from this checkout. Put the configuration and workspace outside the checkout:

```sh
dotnet build H3VR.Converter.sln -c Release
dotnet run --project src/H3VR.Converter -- init ../h3vr-conversion.json
```

Edit the generated JSON to set `GameDirectory`, `UnityEditor`, `Workspace` and `ShaderSources`. Choose a new, empty workspace. Set `RecoveredProject` to an existing `ExportedProject`, or start AssetRipper's local web service and set `AssetRipperUrl` to its loopback URL. Relative file paths resolve against the configuration file's directory.

```sh
dotnet run --project src/H3VR.Converter -- doctor ../h3vr-conversion.json
dotnet run --project src/H3VR.Converter -- convert ../h3vr-conversion.json --through project
dotnet run --project src/H3VR.Converter -- convert ../h3vr-conversion.json --through validate
dotnet run --project src/H3VR.Converter -- convert ../h3vr-conversion.json --through build
```

`convert` defaults to `validate`. The stages are `extract`, `managed`, `project`, `validate`, and `build`. They retarget the installed managed assemblies, replace legacy VR calls, preserve serialized script identities and execution orders, configure OpenXR, and rebuild bundles with the original asset addresses. Failed checks stop conversion. The editor verifies both scenes and prefabs without starting gameplay.

The output is `Workspace/project` and, after a successful build, `Workspace/player`. Logs, migration reports, unsupported VR API reports and SHA-256 receipts stay in that workspace. No upload or game launch is performed. `status <config>` lists completed stages. Repeating `convert` checks input bytes and recorded outputs before resuming; changed source inputs require a new workspace. Use the converter to perform the initial Unity import: it disables Unity's assembly updater because the assemblies are already explicitly retargeted and verified.

## Storage

On macOS the converter uses **APFS copy-on-write clones** for project staging when the source and destination support them. Edits never write through to the original. Other filesystems use normal copies. Keep the workspace on the same APFS volume as the recovery to share unchanged data. Finder/`du` can count a shared extent in several directories; available volume space is the useful measure.

Failed private staging candidates are cleaned automatically. The original recovery, generated Unity project and built player remain yours to keep or delete. Unity's `Library` directories and .NET `bin`/`obj` directories are regenerable caches; close their editor/process before removing them. No hardlinks are used for editable project files.

## Development

- `src/H3VR.Converter`: CLI, safe staging, project/bundle migration and publication checks.
- `tools`: managed retargeting/verification, OpenXR IL patching, shader export tooling.
- `runtime`: managed Unity API compatibility and the Unity XR adapter.
- `editor`: project validation, OpenXR settings and bundle/player building.
- `tests`: small synthetic fixtures; no copy of the game is required.

```sh
dotnet run --project tests/H3VR.Converter.Tests -c Release
dotnet run --project tools/OpenXRPatch.Tests -c Release
dotnet format H3VR.Converter.sln --verify-no-changes
```

Native macOS runtime/driver work is maintained in [ValveIndexMacOS](https://github.com/Frityet/ValveIndexMacOS). This converter calls Unity XR APIs, not that repository's private bridge API. [OpenXR behavior and limits](docs/openxr.md).

Before publishing, stage only authored sources and run:

```sh
dotnet run --project src/H3VR.Converter -- check-publication
```

The checker reads the **staged Git blobs**, rejects assets/binaries/generated paths, and does not treat `.gitignore` alone as an audit. Source provenance still needs review. See [publication policy](docs/publication.md). The tooling is GPL-3.0 under [LICENSE](LICENSE); that license does not cover the user's game or Unity installation.
