# H3VR → Unity 2021

This tool takes your own installed copy of H3VR and turns it into a Unity 2021.3.45f2 project. You can then open that project in Unity or build a playable copy of the game from it. VR runs through OpenXR, so it isn't tied to any one headset.

It doesn't change your game folder. Everything it makes goes into a separate folder (the "workspace") that you choose.

This is still experimental. The converted game hasn't been playtested in VR yet.

## What you need

- H3VR installed
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Unity 2021.3.45f2, with the build module for the platform you want
- Git, and an internet connection the first time you run it
- A folder of shader source files for H3VR (see [shader recovery](docs/shader-recovery.md))

## How to use it

The easiest way is the step-by-step setup in your terminal:

```sh
dotnet run --project src/H3VR.Converter -- --tui
```

It finds your game and Unity on its own, lets you check the paths, and then does the conversion. By default the output goes to `~/H3VR-Unity2021`.

If you'd rather type the paths yourself:

```sh
dotnet run --project src/H3VR.Converter -- \
  --game "/path/to/H3VR" \
  --unity "/path/to/Unity/2021.3.45f2" \
  --workspace "/path/to/output-folder" \
  --shaders "/path/to/shaders" \
  --through build
```

When it's done, the Unity project is in `output-folder/project` and the built game is in `output-folder/player`.

If a run stops partway, run the same command again. It picks up where it left off.

## Other commands

```sh
# Show all options
dotnet run --project src/H3VR.Converter -- --help

# Check that your setup is ready
dotnet run --project src/H3VR.Converter -- doctor --game "/path/to/H3VR" --unity "/path/to/Unity"

# See how far a conversion got
dotnet run --project src/H3VR.Converter -- status --workspace "/path/to/output-folder"

# Pull the shaders out of the game
dotnet run --project src/H3VR.Converter -- recover-shaders --game "/path/to/H3VR" --output "/path/to/shaders"
```

## License

GPL-3.0. See [LICENSE](LICENSE).
