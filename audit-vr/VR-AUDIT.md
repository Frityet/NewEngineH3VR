# Native VR audit — 22 October 2026

Scope: actual installed H3VR binaries and current sibling-project sources/builds.
No sibling files, tracking configuration, hardware state, or Steam files changed.

## Reusable native components

`ValveIndexMacOS/build/bin/libIndexCore.dylib`,
`libVirtualRealityOpenXR.dylib`, and `libopenxr_loader.dylib` are actual Mach-O
arm64 binaries. The built Virtual Reality app is also arm64. The OpenXR runtime
has Metal/Vulkan swapchains, layer validation and composition, room/floor
calibration, tracking/input, haptics, and a broker that shares tracking without
opening USB twice. `native/openxr/IVXRGraphics.m` implements
`XR_KHR_metal_enable`, accepting an `MTLCommandQueue` and returning Metal textures.
The native diagnostic in `native/` loads the existing loader/runtime and confirms
the extension is actually advertised; it does not create a session.

`native/IVHMDDisplay.m` implements physical HMD output through private
`NSHMDMetalSession` AppKit APIs. The current source explicitly rejects physical
refresh changes after an AppKit reconfiguration failure. This private dependency
and the absence of prediction/asynchronous reprojection remain limitations.
Current source documentation contains user confirmations of physical Index
imagery and H3VR gameplay through Wine/OpenComposite on 21–22 October. Those
are historical evidence, not a new hardware test here; the earlier injected
memory saying no physical output was available is obsolete.

## What the installed game expects

The Steam install is
`~/Library/Application Support/Steam/steamapps/common/H3VR`.
Every file in `h3vr_Data/Plugins` is PE x86-64 Windows machine code:
OpenVR, CSteamworks, FMOD, AVProVideo/WinRT, EasyMovieTexture, Audio360,
GVR/Resonance audio, LIV, ForceTube, VHACD, and FFmpeg DLLs.

UTF-16 metadata-string inspection of `Assembly-CSharp-firstpass.dll` finds
function-table requests for:

| Main path | Version |
|---|---|
| System | `IVRSystem_019` |
| Compositor | `IVRCompositor_022` |
| Input | `IVRInput_006` |
| Overlay | `IVROverlay_019` |
| Render models | `IVRRenderModels_006` |
| Settings | `IVRSettings_002` |
| Chaperone / setup | `IVRChaperone_003` / `IVRChaperoneSetup_006` |
| Applications | `IVRApplications_006` |

Additional function-table strings include ExtendedDisplay_001, IOBuffer_002,
Notifications_002, Screenshots_001, SpatialAnchors_001, TrackedCamera_005.
String presence establishes the binding surface, not that all calls execute.
The main game assembly itself also references Compositor_022, Input_006,
Overlay_019, and RenderModels_006. The bundled OpenVR loader's internal strings
are older versions and must not be substituted for the managed binding versions.

`actions.json` contains 21 action definitions: poses, skeletons, trigger,
primary/secondary 2D controls, buttons/touches, grip and thumb squeeze, haptics,
and an optional mixed-reality camera pose. The native OpenXR runtime does not
advertise hand tracking. Existing OpenComposite provides estimated skeleton
paths; a native replacement must deliberately implement or adapt those paths
without claiming measured finger-joint tracking.

## Missing native layer

The existing built OpenComposite `openvr_api.dll` is PE x86-64. Its sources have
DirectX/OpenGL/Vulkan compositors but no Metal compositor. Its CMake defaults
non-Windows platforms to Linux/Xlib and its source includes Linux-specific files.
It is not a drop-in arm64 macOS library.

Two possible native implementation routes are a genuine macOS/Metal port of
OpenComposite, or adapting the managed SteamVR integration to a narrower native
OpenXR bridge. Both need the correct function-table/calling/layout contracts,
calibrated stereo poses/FOV, rendering handoff on Unity's rendering thread,
actions and controller poses, haptics, and honest optional-feature handling.
Neither solves Unity engine/data-version migration or the other native plugins.

The implemented `native/` tracking library is a tested initial part of the
second route. It exposes real broker tracking, validated saved floor, controller
grip/aim offsets and raw inputs in Unity coordinates. It has no Wine dependency,
does not claim to replace OpenVR, and cannot submit eye images yet.
