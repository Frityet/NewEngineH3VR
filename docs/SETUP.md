# Local setup

Run commands from the repository root. This is a development port, not yet a
single-command fresh-install conversion. Historical reports describe outputs
from the original workstation; those outputs are deliberately not in Git.

## Prerequisites

- Apple Silicon macOS, Xcode Command Line Tools (`xcode-select --install`).
- Python 3.11+, `uv`, LLVM `clang-format`, .NET SDK 10. Python extraction tools
  also use `UnityPy` and Pillow; `uv run` installs the pinned dependencies in a local environment.
- Licensed H3VR installed through Steam. Default input is
  `~/Library/Application Support/Steam/steamapps/common/H3VR`. Individual
  extractors offer command-line overrides where documented.
- Unity 2021.3.45f2 ARM64 with macOS standalone support. Set `UNITY_EDITOR` to
  `Unity.app/Contents/MacOS/Unity` and `UNITY_CONTENTS` to `Unity.app/Contents`.
  Some older standalone diagnostic scripts still target the initial Unity 2020
  installation at `/Applications/Unity/Unity.app`; inspect their arguments before use.
- AssetRipper 2.0 for full extraction, plus the historical AssetRipper 1.0.15
  exporter with this repository's patches for shader recovery. Place locally
  acquired tools under `.tools/`; no downloaded tools are shipped here.
- CSteamworks headers/source under `.tools/steamworks/CSteamworks`, a compatible
  ARM64 Steam API library, and FMOD Studio API 2.03.15 acquired under its terms.
  `platform/fmod/prepare-sdk.sh` checks the pinned installer hash.
- A built native headset runtime in sibling `../ValveIndexMacOS` (or the native
  build's `--runtime` argument), plus sibling `../OptiH3VR` for default CPU
  optimization integration. These are separate dependencies, not included here.

## Recovery and preparation

1. Start the local AssetRipper 2.0 web service on port 58947, then run
   `python3 script/recover_full_game.py`. This reads the Steam installation and
   writes `recovered-full/`; it requires at least 100 GiB free and refuses to
   overwrite an existing recovery. Initial main-data recovery is separately
   expected at `recovered/ExportedProject` by older migration tools.
2. Follow `audit-managed/README.md` to audit/retarget the installed assemblies and
   prepare the working Unity projects. Pass local paths to the tools when your
   installation differs from their defaults. Mono.Cecil is restored from NuGet,
   rather than referenced from a particular developer's cache.
3. Build the platform adapters using their component READMEs: Steamworks, FMOD,
   video, UI, audio and the native runtime. SDK licenses and downloads are your
   responsibility; no credentials or license acceptance are automated.
4. Run the shader recovery/validation workflow documented in `shaders/README.md`.
   Its historical exporter identities, validated manifests and recovered sources
   must be regenerated locally. These are mandatory inputs, not shipped fixtures.
5. With recovery, shaders, runtime and platform outputs prepared:

   ```sh
   python3 script/compose_managed.py
   python3 script/stage_native.py --all --project unity-full
   ./script/build_native_bundles.sh
   ./script/build_and_run.sh --build-only full
   ```

Set `NATIVEH3VR_USE_PACKAGE_MANAGER=1` when Unity needs to resolve packages; the
existing build scripts otherwise use `-noUpm` for a previously prepared project.
The full bundle/source validation gates must pass. Do not copy old verification
receipts to bypass them.

## Current reproducibility limits

The tooling originated as an incremental recovery project. The shader pipeline
still depends on intermediate identity maps and validation manifests; a complete
ordered bootstrap from only the Steam installation has not been validated.
The repository is therefore source-only development tooling, not a verified
turnkey installer. Component READMEs include historical measurements and may
refer to local reports that are generated rather than tracked.

This cleanup validates source checks and regression tests. It does not claim a
fresh full-game rebuild, a new physical playtest or cross-platform support.

## Running after preparation

`./script/run_headset_demo.sh --check` checks hardware readiness without starting
an XR session. `./script/run_headset_demo.sh` launches the range with live tracking.
For isolated synthetic validation, use `python3 script/run_native.py --demo` or
`python3 script/run_native.py --gameplay --fire` as described by the component docs.
Do not confuse synthetic checks with physical headset acceptance.

The optional app-copy integration check is
`uv run platform/scene-integrity/verify_native_app_integration.py`. It requires a
local built app and is deliberately separate from the source-only regression suite.

`uv run platform/scene-integrity/integration_native_plugin_contract.py` also
requires locally built current/stale bridge libraries and composed managed
outputs. It checks real binary ABI regressions and is not a source-only test.
