# How VR works

The game calls SteamVR APIs. The converter redirects those calls to an OpenXR adapter. The managed retargeter follows the same approach for removed Unity APIs, such as collision accessors and mesh collider inflation. It routes existing calls to implementations in `runtime/Compatibility`. Use a targeted change to the local game assembly when an adapter cannot preserve the original behavior, such as handing the render loop to OpenXR.

- Saved fields on game objects don't change.
- SteamVR's render loop and the old XR loader are turned off.
- Unsupported SteamVR calls return a failure, or throw an error naming the call.

The workspace's `openxr-patch-manifest.json` lists forwarded calls, unsupported calls and where they're used.

## macOS

The `NativeH3VRMetal` plug-in uses the Khronos loader and `XR_KHR_metal_enable`.

- **Tracking:** head and eye poses are read for the time the frame will be shown. Index controller grip/aim poses are passed to the game in floor space. The play area comes from OpenXR.
- **Input:** ignored until the session has focus and the controls are at rest. Haptics use OpenXR actions. Finger curl is estimated from the controller.
- **Rendering** uses each eye's own camera projection. Once Unity's Metal work completes, the plug-in copies the eye textures on the OpenXR queue and submits them after the copy finishes. One OpenXR session lasts across scene changes.

Untested: the wear sensor and tracked finger joints. Fake (synthetic) hands only run under explicit test checks with headless OpenXR simulation.

## Windows

Uses Unity's OpenXR support and XR input. Unity passes focus changes and haptics (with frequency) to our adapter.

Mac build and launch steps: [platform integrations](platform-integrations.md).
