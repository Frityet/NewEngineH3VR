# Managed OpenXR compatibility

The converter retargets the game assemblies and then applies its managed
SteamVR adapter. It preserves serialized fields and disables the stock
SteamVR render loop and legacy XR loader request. Unknown legacy methods return
an unsuccessful API result where possible or raise a descriptive exception.
The workspace's `openxr-patch-manifest.json` records forwarded methods,
unsupported calls and the audited call sites.

On macOS, `NativeH3VRMetal` uses the Khronos loader and `XR_KHR_metal_enable`.
It locates the head and both eyes at the predicted display time, reads standard
Index grip/aim poses and actions, and hands the managed game adapter floor-space
poses. Stage bounds come from OpenXR. Input remains gated until the session is
focused and the controls return to neutral. Haptics use the runtime's action
interface. Controller-based estimates supply finger curls where the stock hand
code expects a summary. Physical wear sensing and tracked finger joints remain
unverified.

The player renders the original camera twice using the two asymmetric OpenXR
projections. The plug-in encodes the eye copies on Unity's Metal command
buffer, then releases the images and submits the projection layer after the
GPU completes. The same session persists across game scene changes. The
host's synthetic hand override requires explicit acceptance diagnostics and
headless OpenXR simulation.

The Windows adapter uses Unity's OpenXR provider and built-in XR input APIs.
Unity's session feature carries focus changes and frequency-aware haptics to
the adapter on that target.

See [platform integrations](platform-integrations.md) for the macOS build and
launch commands.
