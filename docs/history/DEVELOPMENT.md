# NewEngineH3VR

An experimental ARM64 macOS Unity/Metal port of the installed H3VR game, without Wine. Physical playtests have passed the indoor range, wrist menu, quickbelt and a Take and Hold session; exhaustive compatibility and full gameplay parity remain unestablished.

The Steam installation is read-only input. Recovered game content, binaries, SDKs and build products stay local and are excluded from version control. Redundant Windows bundle copies in generated recovery output are removed only after matching the original Steam files by SHA-256. Native Steam authentication and ownership checks remain active.

## Current work

- Recover the original Unity 5.6.7 assets and managed gameplay assemblies.
- Retarget the original game code to native Unity ARM64 players (2020.3 baseline; 2021.3 full rebuild).
- Replace Windows Steamworks, FMOD, AVPro video and OpenVR paths with native integrations.
- Recover compiled shader logic and compile it for Metal; restore matching historical Unity post-processing sources.
- Rebuild original asset bundles with their original 4,387 content addresses for macOS.

See [STATUS.md](STATUS.md) for evidence and unresolved work. Passing component probes does not establish full game compatibility or physical headset operation.

The port now loads the shared CPU optimizations from `../OptiH3VR` through
`OptiH3VR.NewEngine`. See [OPTIH3VR.md](OPTIH3VR.md) for integration,
the complete scene sweep, runtime checks and profiling limits.

The user accepted indoor-range visuals, controller alignment, M1911 firing,
headset audio and haptics, then accepted the wrist menu, quickbelt and a Take and
Hold session. See [HEADSET_PLAYTEST.md](HEADSET_PLAYTEST.md) and
[PLAYER_SYSTEMS.md](PLAYER_SYSTEMS.md) for those checkpoints.
The current OptiH3VR integration and renamed build have not yet received a new
physical playtest. See [MVP.md](MVP.md) for setup. After building, use
`./script/run_headset_demo.sh` to open the range with live tracking, or add
`--check` to read hardware readiness without starting an XR session.
Synthetic checks also exercise menu, demo startup and M1911 interactions;
the desktop mirror still appears brighter than the stereo output.

The game now targets the headset's reported refresh rate at startup (currently
144 Hz on the connected Index). See [FRAME_PACING.md](FRAME_PACING.md)
for verification, fallback behavior and the distinction between the frame target
and delivered FPS.

## Local build

The generated working projects are `unity/` (initial main-data integration) and `unity-full/` (main data plus all streaming content). Build scripts prefer the official ARM64 Unity 2021.3.45f2 editor extracted locally under `.tools/unity2021-arm64-expanded/`. Set `UNITY_EDITOR` to override it. The initial validated player used the installed Unity 2020.3.49f1 toolchain; both produce ARM64 players with native plugins and runtime. Prepared projects use their resolved package cache with `-noUpm` because this host currently stalls new Intel helper processes. Set `NATIVEH3VR_USE_PACKAGE_MANAGER=1` when package resolution is needed and its server is functional.

After recovery, managed preparation and platform builds:

```sh
python3 script/compose_managed.py
python3 script/stage_native.py --all --project unity-full
./script/build_native_bundles.sh
./script/build_and_run.sh --build-only full
python3 script/run_native.py --seconds 30
python3 script/run_native.py --demo --seconds 60
python3 script/run_native.py --gameplay --fire
```

The bundle gate checks loaded assets and their references, then binds its proof to
the source files, managed assemblies and output hashes. The full player build also
checks the copied bundles. If inputs change, rebuild or reverify the bundles before
building the player. The gameplay command requires the original range transition,
firearm loading, grip/proximity hold, one moving ballistic projectile and release.
It rejects unresolved runtime errors and records fresh screenshots for visual review.
The diagnostic route passes in the combined demo build. `--demo` tests the actual
range startup with isolated synthetic head tracking and no controller input fixture.
Stereo capture metadata must match the current player process and follow camera
handoff; a periodic image from the scene transition is rejected.

Automated tests use isolated synthetic head poses with the real native Metal/OpenXR compositor. Opening the packaged app normally selects the live headset runtime. Earlier physical acceptance applies to the tested checkpoints above; current optimization changes still need their own physical retest. Unsupported optional APIs report their absence; they do not synthesize successful hardware or ownership responses.

The Codex Run action invokes `script/build_and_run.sh`, builds the full recovered
game and launches it against the live native headset runtime. The optional
second argument `boot` builds only the menu project for development;
`--verify` always uses isolated synthetic tracking. Local application packaging
installs a small native launcher that provides the Steam development AppID and
then executes the native Unity player. Legacy options and progress use the
separate `NativeH3VRLocal` company path. The newer Vault retains the original
UGC/JSON serialization route under `My Games/H3VR` in .NET's Personal folder.

The packaged app is `dist/NewEngineH3VR.app`; its launcher is `NewEngineH3VR`
and preserved Unity executable is `NewEngineH3VRPlayer`. Runtime data is staged
under `StreamingAssets/NewEngine/build`. The managed bridge accepts the earlier
`NativeRuntime` directory only as a read-only fallback for existing local builds.
Staging archives an existing generated legacy runtime directory and its folder
metadata under `build/runtime-staging/` before copying the new directory; it
rejects symlinks and competing runtime roots.
Legacy `NativeH3VR` managed assembly names, namespaces and asset GUIDs remain for
serialized-content compatibility.

Full builds install the sibling `../OptiH3VR` mod's default CPU optimizations
through its portable `OptiH3VR.NewEngine.dll` helper. CPU algorithms stay in the
mod; the port supplies the offline integration and graphics/runtime adapters.
To update only this integration in a stopped, audited app, run
`python3 script/install_optih3vr.py --source ../OptiH3VR`. The installer prepares
from a saved pristine game assembly, verifies source hashes and assembly layout,
and records an installation receipt. Launch with
`OPTIH3VR_ENABLED=0 dist/NewEngineH3VR.app/Contents/MacOS/NewEngineH3VR` for a
baseline comparison; `1` or an unset value selects the default optimizations.

The [busy-scene profile](BUSY_PROFILE_2026-10-23.md) and
[input/recovery validation](INPUT_RECOVERY_2026-10-23.md) record the
subsequent live measurements and engine-specific changes. The input caches are
gated by `NEWENGINE_UNITY_2021`. The validated input caches and transient
eye-query recovery are installed in `dist/NewEngineH3VR.app`; the report includes
actual-player checks, allocation comparisons, their limits and rollback evidence.

## Sources and reproducibility

- `audit-managed/`: assembly inspection, Unity API compatibility shim and scene-script rebinding.
- `audit-assets/`: original asset, bundle, shader and script metadata.
- `platform/`: native Steamworks wrapper, FMOD ABI bridge, video backend and launcher.
- `native/`: native tracking, Metal/OpenXR integration and guarded original SteamVR adapters.
- `shaders/`: source recovery fixes, historical post-processing sources, variant recovery and GPU evidence.
- `script/`: composition, recovery, original compressed-texture restoration, bundle mapping and builds.

No game content or proprietary SDK is published by this project. Use the user's installed game and locally acquired SDKs to reproduce the development build.
