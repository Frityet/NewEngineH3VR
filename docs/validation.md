# Unity 6.7 validation

The converter targets Unity 6.7. The Apple Silicon build and runtime checks used `6000.7.0b10`.

## Engine and OpenXR checks

- The macOS standalone engine fixture ran on ARM64 Metal and passed 22 rendering, transform, and physics checks.
- The native Unity Metal smoke built an ARM64 player and loaded a relocated MacVR 0.4.0 export. It completed over 240 synthetic stereo frames and verified focus, the Index controller profile, haptics, the play area, and the two eye images.
- The Unity editor generated standalone players and asset bundles for macOS and Windows using the converter's editor entry point and synthetic assets.

These fixtures generate their own content and require no H3VR files.

## Steam build and Wine comparison

The converter built a signed macOS ARM64 player from Steam app 450540, build 25002541. Unity validated 46 scenes and 8,293 prefabs with zero new missing-script references and zero shader compilation errors. The player also passed stock address checks across 18 asset bundles. The three densest legacy convex hulls use a vertex-limited approximation in the rebuilt player.

The original player ran in an isolated Wine prefix using Unity 5.6.7f1, D3D11 and OpenVR through OpenComposite. Native and Wine runs replayed the same synthetic MacVR head and controller samples at 3072×1600 packed stereo. Both passed seven checks covering the opening prompt, head turn, controller selection, scene loading, and stereo output after entering The Sampler Platter. The final comparison recorded 722 completed stereo frames on Wine and 1,241 on native. A separate Steam-managed startup confirmed Steam initialization for the original player.

After aligning the captures, the 95th-percentile landmark error was 0.33 pixels per eye in the menu and 0.65–0.68 pixels after the scene load. Average low-pass RGB difference was under 2 of 255 in the menu and about 7.8 of 255 after the load. The native tutorial card appeared darker. These measurements cover the tested startup and scene transition. Broader gameplay, audio parity, and physical headset use still need verification. [Stock rendering audit](stock-menu-rendering.md)

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
