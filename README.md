# H3VR → Unity 6.7

This tool upgrades a local Steam installation of H3VR to Unity 6.7. The converter checkout contains source code only. It reads your Steam files and writes the generated Unity project and player into a Git-ignored `.local` directory.

On Apple Silicon, the native player uses Mono, Metal and a caller-selected MacVR OpenXR export. The Windows target uses Unity's OpenXR package. See [validation](docs/validation.md) for the tests run so far.

The Steam game directory is read-only throughout conversion.

## What you need

- H3VR installed through Steam
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Unity 6.7 (`6000.7.0b10` by default), with the standalone build module for your target
- Git, and an internet connection the first time you run it
- A local `dxil-spirv` and `spirv-cross` build and the pinned Unity class database to generate shaders. You can also pass a previously verified overlay from your own Steam copy. See [shader recovery](docs/shader-recovery.md).
- On a Mac: the platform integration file, which you build first (see [platform integrations](docs/platform-integrations.md))

## How to use it

Run from the converter checkout once the prerequisites are available:

```sh
dotnet run --project src/H3VR.Converter -c Release -- \
  --target macos \
  --integration "$PWD/.local/integration/integration.json" \
  --dxbc-compiler "/path/to/dxil-spirv" \
  --spirv-cross "/path/to/spirv-cross" \
  --through build
```

For Windows, use `--target windows` and omit `--integration`. The converter looks for Steam and the Unity Hub editor automatically. Use `--game`, `--unity`, or `--unity-version` when your installation differs. To reuse an existing overlay, use `--shaders` in place of the two translator flags.

The default workspace is `.local/workspace`. Unity files go into `project/` and the player into `player/` under that workspace. The shader recovery and overlay directories sit beside the workspace. Use `--workspace` to choose another directory under the checkout's ignored `.local/` directory. The tool cache defaults to `cache/`. You can override it with a directory under `.local/` or `cache/` using `--cache`.

If a run stops partway, run the same command again. It picks up where it left off.

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
