# H3VR → Unity 6.7

This tool upgrades a local Steam installation of H3VR to Unity 6.7. The converter checkout contains source code only. It reads your Steam files and writes the generated Unity project and player into a Git-ignored `.local` directory.

On Apple Silicon, the native player uses Mono, Metal and a caller-selected MacVR OpenXR export. The Windows target uses Unity's OpenXR package. See [validation](docs/validation.md) for the tests run so far.

The Steam game directory is read-only throughout conversion.

## Build from your Steam copy

- H3VR installed through Steam
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Unity 6.7 (`6000.7.0b10` by default), with the standalone build module for your target
- Git, and an internet connection the first time you run it
- A native C++ toolchain and Python 3 for building the shader translators. On macOS, the Apple Command Line Tools supply these.
- On macOS, keep your Steamworks 1.41 and FMOD Engine 2.03.15 SDKs in the documented local folders. The converter builds the integration from those SDKs. [macOS setup](docs/platform-integrations.md)

Run this from the checkout and select your Steam installation. The conversion defaults to producing a Unity 6.7 player.

```sh
dotnet run --project src/H3VR.Converter -c Release -- --tui
```

For an unattended build, pass `--game "/path/to/H3VR"`. The converter can detect a single Steam installation and the matching Unity Hub editor. Use `--unity` or `--unity-version` when your installation differs. Select `--target windows` for a Windows build. The converter prepares the Unity class database, shader tools and shader overlay automatically. Use `--shaders` to supply a previously verified overlay from your copy, or `--dxbc-compiler` and `--spirv-cross` to use local translator builds. [Shader recovery](docs/shader-recovery.md)

The default workspace is `.local/workspace`. Unity files go into `project/` and the player into `player/` under that workspace. The shader recovery and overlay directories sit beside the workspace. Use `--workspace` to choose another directory under the checkout's ignored `.local/` directory. The tool cache defaults to `cache/`. You can override it with a directory under `.local/` or `cache/` using `--cache`.

If a run stops partway, run the same command again. It verifies completed stages and picks up where it left off. `init` saves optional defaults to `.local/h3vr-conversion.json`, which later runs load automatically. Flags override those defaults.

## Optional URP graphics upgrade

Add `--graphics-upgrade` to build a separate Unity 6.7 player using URP 17.7.0.

```sh
dotnet run --project src/H3VR.Converter -c Release -- --graphics-upgrade
```

The default output is `.local/workspace-graphics-upgrade/player/`. On the wrist menu, open **Options → Graphics Options** to select Performance, Balanced or Quality. Each rendering control also works independently. [Rendering, presets and verification](docs/graphics-upgrade.md)

On macOS, `UnityMetalCli combine` packages Base, Built-in and URP in one `NativeH3VR.app`. Choose the renderer when you open the app. [One macOS app](docs/graphics-upgrade.md#one-macos-app)

## Other commands

```sh
# Show all options
dotnet run --project src/H3VR.Converter -- --help

# Check that your setup is ready
dotnet run --project src/H3VR.Converter -- doctor --game "/path/to/H3VR" --unity "/path/to/Unity"

# See how far a conversion got
dotnet run --project src/H3VR.Converter -- status

# Pull the shaders out of the game
dotnet run --project src/H3VR.Converter -- recover-shaders --game "/path/to/H3VR" --output "$PWD/.local/raw-shaders"
```

## License

GPL-3.0. See [LICENSE](LICENSE).
