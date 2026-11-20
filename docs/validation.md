# Unity 6.7 validation

The converter targets Unity 6.7. The current editor tested on Apple Silicon is `6000.7.0b10`. The Unity test scenes generate their own content and require no H3VR files.

## Engine and OpenXR checks

- The macOS standalone engine fixture ran on ARM64 Metal and passed 22 rendering, transform, and physics checks.
- The native Unity Metal smoke built an ARM64 player and loaded a relocated MacVR 0.4.0 export. It completed 240 synthetic stereo frames and verified focus, the Index controller profile, haptics, the play area, and the two eye images.
- The Unity editor generated standalone players and asset bundles for macOS and Windows using the converter's editor entry point and synthetic assets.

These tests cover the engine, build tools and synthetic OpenXR frame delivery. Verify game startup, gameplay, and physical VR on the rebuilt game itself.

## Original Steam player under Wine

Steam app 450540, build 25002541, ran in an isolated Wine prefix using Unity 5.6.7f1, D3D11 and OpenVR through OpenComposite. The MacVR synthetic run reached the original control-mode menu and recorded 3,558 completed stereo frames. Steam initialization was unavailable in this isolated runtime run. A separate Steam-managed startup reached Steam initialization.

A direct comparison needs the rebuilt Unity 6.7 player and the same scene, tracking pose, graphics options, and input sequence. The renderer audit contains controls and measurements collected before the 6.7 upgrade. [Stock rendering audit](stock-menu-rendering.md)

## Running the native OpenXR test

Build a MacVR export and choose a fresh ignored output directory. Run this from the converter checkout.

```sh
dotnet run --project tools/UnityMetalCli -c Release -- smoke \
  --unity-editor "/path/to/Unity.app" \
  --openxr-headers "/path/to/MacVR/extern/openxr/include" \
  --runtime "/path/to/macvr-openxr" \
  --frames 240 --work "$PWD/.local/metal-smoke"
```

The test saves the editor and player logs, stereo image, and Unity and MacVR traces. For timings, use the paired traces. [Profiling](platform-integrations.md#profiling)
