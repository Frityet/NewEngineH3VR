# How VR works

The game was built for SteamVR. The converter patches it to use our own SteamVR replacement, which runs on OpenXR.

- Saved fields on game objects don't change.
- SteamVR's render loop and the old XR loader are turned off.
- Unsupported SteamVR calls return a failure, or throw an error naming the call.

The workspace's `openxr-patch-manifest.json` lists forwarded calls, unsupported calls and where they're used.

## macOS

The `NativeH3VRMetal` plug-in uses the Khronos loader and `XR_KHR_metal_enable`.

- **Tracking:** head and eye poses are read for the time the frame will be shown. Index controller grip/aim poses are passed to the game in floor space. The play area comes from OpenXR.
- **Input:** ignored until the session has focus and the controls are at rest. Haptics use OpenXR actions. Finger curl is estimated from the controller.
- **Rendering:** the camera renders once per eye with that eye's projection. Eye images are copied on Unity's Metal command buffer and handed to OpenXR after the GPU finishes. One session lasts across scene changes.

Untested: the wear sensor and tracked finger joints. Fake (synthetic) hands only run under explicit test checks with headless OpenXR simulation.

## Windows

Uses Unity's OpenXR support and XR input. Unity passes focus changes and haptics (with frequency) to our adapter.

Mac build and launch steps: [platform integrations](platform-integrations.md).
