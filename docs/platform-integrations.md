# Native macOS integration

Apple Silicon conversion targets Unity **2021.3.45f2**, the Mono scripting
backend and Metal. The host creates one OpenXR session in its ARM64 Metal
plug-in. It reads tracking and actions and submits the two eye images through
the Khronos loader. Unity's OpenXR 1.13 package supports the converter's Windows
target. The macOS target uses the Metal plug-in. [Unity's runtime matrix](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.13/manual/index.html#runtimes) · [OpenXR Metal specification](https://registry.khronos.org/OpenXR/specs/1.1/html/xrspec.html#XR_KHR_metal_enable)

## Prepare the local integration

Use the OpenXR headers from the same MacVR source revision as the runtime
package. Install Python 3.11 or later, Xcode command line tools and the Unity
2021.3.45f2 Apple Silicon editor with Mac Standalone Support. Supply the
[CSteamworks and FMOD inputs](../runtime/Platform/README.md) locally.

Run these commands from the NativeH3VR checkout with new private paths.

```sh
python3 tools/PlatformPatch/build-native.py \
  --csteamworks "/path/to/CSteamworks" \
  --steam-api "/path/to/Steam Helper.app/Contents/MacOS/libsteam_api.dylib" \
  --fmod-sdk "/path/to/fmod-sdk-2.03.15" \
  --output "/path/to/private/platform"

python3 runtime/UnityMetal/build.py \
  --unity-editor "/path/to/Unity.app/Contents/MacOS/Unity" \
  --openxr-headers "/path/to/MacVR/extern/openxr/include" \
  --platform "/path/to/private/platform" \
  --output "/path/to/private/integration"
```

Pass `--integration /path/to/private/integration/integration.json` to the
converter. The manifest binds each authored source and local plug-in to the
exact target and a SHA-256 digest. The Unity editor selects the Metal backend
and uses its runtime initialization to attach the game camera after rig
expansion. The converter includes a generic mod host that discovers private
mod manifests under `Contents/Resources/NativeMods`.

The macOS editor stage requires the pinned editor and its native player module.
Use a fresh private project with that project closed before running batch
conversion. The built player is ad-hoc signed locally. Its receipt records
Steam build and extraction identities, Unity version, architecture, executable
and managed DLL hashes.

## Launch an imported player

Build a MacVR export with `xmake openxr-export --output=/absolute/new/path`.
The launcher verifies the runtime files and the signed app, selects the runtime
with a process-local `XR_RUNTIME_JSON`, and directs Unity preferences and saves
into the supplied state directory. It passes the real H3VR app ID to Steam.
The Steam libraries still perform their own initialization and ownership
operations.

```sh
python3 runtime/UnityMetal/run.py \
  --app "/path/to/private-workspace/player/NativeH3VR.app" \
  --runtime "/path/to/macvr-openxr" \
  --state-dir "/path/to/private-state" \
  --synthetic --log-file "/path/to/new-player.log"
```

Use `--headset` for a physical session. For synthetic timing, supply
`--trace-file` and `--unity-profile-file` with fresh paths, and keep
`NATIVEH3VR_EYE_WIDTH`, `NATIVEH3VR_EYE_HEIGHT` and `VR_OPENXR_TARGET_HZ` fixed
across runs. The profiler joins Unity rows to MacVR by predicted display time.

```sh
python3 runtime/UnityMetal/profile.py \
  --unity "/path/to/unity.csv" --openxr "/path/to/macvr.csv" \
  --warmup-seconds 2 --window-seconds 10
```

Use `--unity-frames START END` for an inclusive range of known scene frames.
Unsupported Unity recorder counters appear as missing data. MacVR completion
records report GPU completion. Physical scanout requires separate evidence.
