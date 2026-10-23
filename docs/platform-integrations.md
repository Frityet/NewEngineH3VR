# OpenXR platform integrations

The stock conversion target is `StandaloneWindows64`, using Unity 2021.3.45f2 and OpenXR package 1.13.0. Unity's [OpenXR supported runtime list](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.13/manual/index.html) does not include a macOS player provider. A working native OpenXR runtime and a Unity XR provider are separate components.

On other targets, supply `IntegrationManifest` in the conversion configuration. The manifest is local, outside the public repository. It must match the exact Unity version and build target, declare `XrBackend: "OpenXR"`, and list every supplied file with a SHA-256 digest. `Provider` names the fully qualified managed Unity XR loader type. The loader must register with XR Plug-in Management's metadata store. The converter selects that loader instead of the stock OpenXR loader.

```json
{
  "SchemaVersion": 1,
  "UnityVersion": "2021.3.45f2",
  "BuildTarget": "StandaloneOSX",
  "XrBackend": "OpenXR",
  "Provider": "YourProvider.OpenXRLoader",
  "Files": [
    {
      "Source": "provider/YourProvider.dll",
      "Destination": "Assets/OpenXRProvider/YourProvider.dll",
      "Sha256": "REPLACE_WITH_THE_ACTUAL_64_HEX_DIGEST"
    }
  ]
}
```

Paths are relative to the manifest directory; destination files are limited to `Assets/Plugins` and `Assets/OpenXRProvider`. Parent traversal, symlinks, hash mismatches and overwriting a different existing file are rejected. Include required Unity `.meta`/assembly definition files in the manifest. These files remain private build inputs.

A provider must implement stereo display, pose/input subsystems and OpenXR session lifecycle. The managed game adapter expects floor/stage-relative headset and controller poses, standard Unity `CommonUsages`, haptics, and reliable device availability. Providers using the stock OpenXR feature system receive the included session-focus feature; a custom provider must forward actual session focus changes to `NativeH3VR.OpenXRSessionState.SetInputFocus`. It must clear focus on loss or teardown. Focus must never be inferred merely from a connected headset.

Platform support also requires replacements for the game's native Steam, FMOD, video and optional peripheral dependencies. The manifest mechanism stages supplied integrations; it does not implement or certify them. Windows plugins are copied from the user's installation only for Windows builds, with platform-restricted Unity import settings. OpenVR's native DLL is excluded.

The macOS compatibility work moved to ValveIndexMacOS under `integrations/h3vr`, with the reusable native bridge/tests under `integrations/unity/openxr`. That legacy bridge owns an OpenXR session and is not a Unity XR display/input provider; it is not selected by this converter.
