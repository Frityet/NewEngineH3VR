# NewEngineH3VR

Experimental native ARM64 macOS / Unity / Metal port tooling for **Hot Dogs,
Horseshoes & Hand Grenades**. You must own and install H3VR through Steam.
Steam authentication and ownership checks remain active.

**This repository contains port tooling, not the game or a ready-to-run player.**
Game assemblies, recovered shaders, textures, audio, scenes, asset bundles,
proprietary SDKs and generated apps are not distributed. They are generated or
acquired locally. Do not upload your recovery directories or packaged app.

## Requirements and setup

See [the setup guide](docs/SETUP.md) for prerequisites, the local build sequence,
and the remaining fresh-install limitations. The current supported development
target is Apple Silicon macOS; this is not a Windows/Linux build recipe.

```sh
git clone https://github.com/Frityet/NewEngineH3VR.git
cd NewEngineH3VR
uv run script/check_source.py
```

The source checks require Python 3.11+, `uv` and `clang-format` 23. They do not build
or start the game. Native/managed builds also require Unity, .NET and the local
SDK/runtime dependencies described in the setup guide.

Once the local recovery and integrations are prepared:

```sh
./script/build_and_run.sh --build-only full
./script/run_headset_demo.sh --check
./script/run_headset_demo.sh
```

The app is generated at `dist/NewEngineH3VR.app`. A clean checkout cannot execute
these build commands until the required recovery inputs exist.

## Layout

| Directory | Purpose |
| --- | --- |
| `script/` | Composition, staging, build, packaging and verification entry points |
| `native/` | Tracking, Metal/OpenXR bridge and managed adapters |
| `platform/` | Steamworks, FMOD, video, audio and gameplay integration tools |
| `audit-managed/`, `audit-assets/`, `audit-vr/` | Local extraction, migration and validation tools |
| `shaders/` | Shader recovery tools and exporter patches; no recovered shader programs |
| `docs/` | Setup and publication policy |
| `config/public-files.json` | Explicitly reviewed publication inventory |

## Development

```sh
uv run script/check_source.py --format
uv run script/check_source.py
git add <reviewed-files>
python3 script/check_publication.py
```

The ignore file defaults to excluding new files. Add new authored files to the
publication manifest and its matching ignore exceptions only after checking
provenance. The publication check reads **staged blobs**, rejects unexpected
files, symlinks, binary data and asset extensions, and must pass before pushing.
See [the publication policy](docs/PUBLICATION.md).

Standalone Unity probe projects and historical development reports are excluded.
The compiler, bundle, asset and native-plugin checks remain because they reject
broken conversion outputs. `platform/gameplay-probe` is currently built by the
staging pipeline and supplies demo startup and optional in-player acceptance
checks; it is not one of the removed standalone Unity projects.

The repository retains its existing GPL-3.0 license. Third-party tools and SDKs
retain their own licenses; the project license does not license H3VR content.
