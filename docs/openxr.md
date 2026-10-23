# Unity XR / OpenXR compatibility adapter

This managed adapter preserves the SteamVR surface referenced by H3VR while
delegating session startup, stereo camera rendering, and display pacing to the
active Unity XR provider. Its tracking and haptic calls use Unity's
`UnityEngine.XR.InputDevices` API. There are no native OpenVR imports in this
adapter.

Build it after managed retargeting, using the exact Unity 2021 managed
assemblies and the converted H3VR assemblies:

```sh
dotnet build runtime/OpenXR/NativeH3VR.OpenXR.csproj \
  -p:UnityManagedPath=/path/to/Unity.app/Contents/Managed/UnityEngine \
  -p:GameManagedPath=/path/to/managed-retarget-output \
  -p:NewtonsoftAssembly=/path/to/managed-retarget-output/Valve.Newtonsoft.Json.dll
```

Then patch the managed game assemblies into a separate output directory:

```sh
dotnet run --project tools/OpenXRPatch/OpenXRPatch.csproj -- \
  /path/to/managed-retarget-output \
  /path/to/NativeH3VR.OpenXR.dll \
  /path/to/patched-managed-output \
  --unity-engine /path/to/Unity.app/Contents/Managed/UnityEngine
```

The `--unity-engine` argument is required when the managed input tree does not
include Unity's engine assemblies.

The patcher copies the managed input tree, forwards the implemented
`CVRInput`, `CVRSystem`, `CVRCompositor`, and `CVRChaperone` methods, disables
SteamVR's manual per-eye render coroutine, and removes `openvr_api` and
`ValveIndexMacOS` P/Invoke entry points. Unknown interfaces either return a
non-success error where the API has an error result or throw
`NotSupportedException`; the patcher writes `openxr-patch-manifest.json` and
fails if any targeted native import remains.

Forced SteamVR initialization keeps the configured Unity XR loader. The patcher
removes the legacy `LoadDeviceByName("OpenVR")` request and rejects an unexpected
initialization method shape or any remaining Valve.VR loader-switch call.

Coverage includes action-manifest handles, action-set updates, digital and
analog controls, controller/head poses and origin metadata, controller
vibration impulses, device class/role and basic device properties, camera
projection/eye transforms, render-target size, tracking-space recentering,
play-area bounds, and render/input availability. Unity XR owns stereo frame
submission and display pacing. Unsupported runtime/firmware, hidden-area,
chaperone editing, compositor timing/texture submission, and binding UI
operations return an explicit error or throw; inspect the generated manifest
for method-level coverage and call-chain audit.

Input focus defaults to false. The staged
`Assets/H3VRConverter/OpenXR/OpenXRSessionFeature.cs` feature reports standard
OpenXR session focus transitions to the fail-closed input gate. The SteamVR skeleton API reports a
31-bone topology to let Hand initialization proceed, but the adapter only
provides controller-root pose and estimated curl/splay summary data. It does
not fabricate joint transforms or claim finger tracking; unsupported bone
data queries return an input error.

The managed adapter does not require the OpenXR package at compile time. The
Unity project should pin `com.unity.xr.openxr` 1.13.0 and the matching Input
System package as part of project staging. Unity's official OpenXR provider
does not establish macOS player support; macOS therefore requires a separate
standards-based provider that supports that target. Configure the selected
provider in XR Plug-in Management. This adapter does not provide a native
OpenXR loader or runtime.

The staged OpenXR session feature routes haptic requests through OpenXR's
frequency-aware `OpenXRInput.SendHapticImpulse` API. Other Unity XR providers
use `InputDevice.SendHapticImpulse`, which has no frequency parameter; those
providers accept only the default frequency (`0`) and return an error for
frequency-specific requests. Pose validity requires available position and
rotation plus either Unity's tracked flag or both tracking-state bits, so
cached values alone do not keep a pose marked valid after tracking is lost.
