# Building and running on macOS

On Apple Silicon, the converter targets Unity **6.7**, Mono and Metal. The ARM64 Metal plug-in opens one OpenXR session through the Khronos loader, reads tracking and input and sends both eye images.

The Windows build uses Unity's OpenXR 1.19 package. [Unity OpenXR](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.19/manual/index.html) · [OpenXR Metal spec](https://registry.khronos.org/OpenXR/specs/1.1/html/xrspec.html#XR_KHR_metal_enable)

## Build the integration automatically

`integration.json` lists the native Steam, audio and OpenXR components that the converter installs into the Unity project. Each entry records a destination and a SHA-256 hash. The converter can build it with Unity 6.7 and the Apple Command Line Tools.

Get the vendor SDKs through their download pages and place them in these checkout-local directories. You can also point to SDKs installed elsewhere.

| Input | Default path | Optional flag |
| --- | --- | --- |
| Steamworks SDK 1.41 headers | `.local/sdk/steamworks-1.41/public/steam` | `--steamworks-headers` |
| FMOD Engine SDK 2.03.15 root containing `api/` | `.local/sdk/fmod-2.03.15` | `--fmod-sdk` |

The vendor download pages require sign-in. [Steamworks downloads](https://partner.steamgames.com/downloads/list) · [FMOD downloads](https://www.fmod.com/download)

The converter finds the Steam API library in the installed macOS Steam client and prepares the pinned public CSteamworks source and OpenXR headers. It builds the native adapters and generates the manifest when you run conversion. Use `--csteamworks`, `--steam-api`, or `--openxr-headers` to select local overrides. Use `--integration` to supply an integration you already built. Keep all generated components in the checkout's ignored local directories.

## Build the integration separately

Run these from this repo. Use new, empty output folders.

```sh
dotnet run --project tools/PlatformPatch/NativeBuilder -c Release -- \
  --csteamworks "/path/to/CSteamworks" \
  --steamworks-headers "/path/to/steamworks-1.41/public/steam" \
  --steam-api "/path/to/Steam Helper.app/Contents/MacOS/libsteam_api.dylib" \
  --fmod-sdk "/path/to/fmod-sdk-2.03.15" \
  --output "$PWD/.local/platform"

dotnet run --project tools/UnityMetalCli -c Release -- build \
  --unity-editor "/path/to/Unity.app/Contents/MacOS/Unity" \
  --openxr-headers "/path/to/MacVR/extern/openxr/include" \
  --platform "$PWD/.local/platform" \
  --output "$PWD/.local/integration"
```

To reuse that integration, pass it to the converter:

```sh
--integration "$PWD/.local/integration/integration.json"
```

Things to know:
- Use the same exact Unity version for integration and conversion. Both commands accept `--unity-version` and default to `6000.7.0b10`.
- Use a fresh project, and keep it closed in Unity while the converter runs.
- The finished app is signed ad-hoc on your machine.
- Its receipt records the Steam build, extraction, Unity version, architecture, executable, DLL hashes and the mod manifest version it supports.

## Run the player

First build a MacVR export:

```sh
xmake openxr-export --output=/absolute/new/path
```

Then launch the game:

```sh
dotnet run --project tools/UnityMetalCli -c Release -- run \
  --app "$PWD/.local/workspace/player/NativeH3VR.app" \
  --runtime "/path/to/macvr-openxr" \
  --state-dir "$PWD/.local/player-run/state" \
  --synthetic --log-file "$PWD/.local/player-run/player.log"
```

- `--synthetic` uses a fake headset. Use `--headset` for a real one.
- Put `--state-dir` and the log, trace, profile and capture outputs under the checkout's `.local/` directory. Unity keeps settings and saves in `--state-dir`.
- The launcher checks the runtime and the app signature first. It points only this process at MacVR, through `XR_RUNTIME_JSON`.
- Headset launches use MacVR’s saved headset calibration, room setup and render resolution. Set `VR_STATE_DIR` to select a custom MacVR state directory. Synthetic runs use their own runtime state under `--state-dir`.
- Steam gets the real H3VR app ID and does its own startup and ownership checks.

### With mods

Add `--mods-dir /path/to/mods`. Leave it out for an unmodded game.

Each mod goes in its own folder with a schema 2 `native-mod.json`. Before loading a mod, the game checks two things:
- its `hostBuildSha256` matches the app's signed `Contents/Resources/nativeh3vr-build.json`
- its assembly hashes are correct

### Profiling

Add `--trace-file` and `--unity-profile-file` (new paths) to the run command. Keep `NATIVEH3VR_EYE_WIDTH`, `NATIVEH3VR_EYE_HEIGHT` and `VR_OPENXR_TARGET_HZ` the same between runs you want to compare. Then:

```sh
dotnet run --project tools/UnityMetalCli -c Release -- profile \
  --unity "/path/to/unity.csv" --openxr "/path/to/macvr.csv" \
  --warmup-seconds 2 --window-seconds 10
```

This lines up Unity's frames with MacVR's by predicted display time.

- To look at specific frames instead, use `--unity-frames START END` (both ends included).
- Counters Unity doesn't support show up as missing.
- MacVR's timings are for when the GPU finished, not when the display showed the frame.

### Calling it from other tools

Tools like the Backrooms launcher can build the CLI once:

```sh
dotnet build tools/UnityMetalCli -c Release
```

Then run `dotnet /absolute/path/to/UnityMetalCli.dll run` with the same options.

## Compute buffer sizes on Metal

Metal shaders can't ask a buffer how big it is the way Direct3D 11 and Vulkan can. On macOS the converter records the property names the game creates with `Shader.PropertyToID`. When the game binds a `ComputeBuffer` to a material or property block, it also sets `_NativeH3VRBufferCount` in the same place. The translated Metal shaders read that as the element count. [Unity's material property binding](https://github.com/Unity-Technologies/UnityCsReference/blob/6000.7/Runtime/Export/Shaders/MaterialPropertyBlock.cs)
