# Unity eye submission orientation and color, 22 October 2026

The native plugin now renders a full-screen Metal triangle into each OpenXR sRGB swapchain. For Unity render events it reverses Y, matching the upright Unity camera image. Standalone callers retain their existing top-left texture convention. Texture reads honor the source Metal pixel format: sRGB is decoded and re-encoded once, while linear UNORM is encoded into sRGB. GPU completion still precedes releasing either swapchain and xrEndFrame; completion dispatch stays off the Metal callback thread.

The real arm64 Unity 2020 player on Apple M5 Max rendered asymmetric quadrants and eight gray patches in the original camera's OnPostRender callback. It submitted real RenderTextures through the native Unity render callback and the pinned OpenXR compositor. No physical HMD or shared Home output was used.

Evidence in `vr-probe/output/orientation-proof/`:

- `srgb.png` and `srgb.png.source-{0,1}.png`: all 32,768 pixels agree exactly (maximum channel error 0). Native source format 71, RGBA8Unorm_sRGB.
- `linear.png` and source files: all 32,768 pixels agree exactly after the standard linear-to-sRGB transfer function (maximum channel error 0). Native source format 70, RGBA8Unorm.
- Eight intended linear gray levels become sRGB bytes 71,120,152,177,198,216,233,248 for the sRGB source; UNORM quantization changes the fifth to197.
- `raw-blit-control-srgb.png`: the previous raw-copy implementation reproduced upside-down output. The same pixel verifier rejected it, maximum channel error255.
- Individual verification JSONs and player logs identify formats, actual Apple GPU and completed frames.

Reproduction: `python3 native/build.py --test`; stage its dylib in vr-probe/Assets/Plugins; `python3 native/test_unity.py`; then `NATIVEH3VR_PROBE_LINEAR=1 python3 native/test_unity.py --skip-build`. `--refresh-scripts` rebuilds the same probe classes using native Mono into an existing arm64 player while preserving its built scene and serialized class identity. This was used because new Rosetta helper processes (UnityPackageManager and Unity csc) were stuck before execution. It is a real Unity player test, not a mocked host. The separate native test also checks callback command-buffer ownership.

Do not brighten already-sRGB eye textures to match a desktop screenshot: their pixels already survive submission exactly. An optional `NATIVEH3VR_PROBE_MIRROR=1` diagnostic uses the game's OnGUI DrawTexture pattern and captures the window; this showed a separate desktop color mismatch requiring investigation. No claim of calibrated physical HMD color is made.

The legacy game `GM.Awake` model call is now routed from retargeted `Compatibility.GetVRModel` through the adapter. It queries xrGetSystemProperties for the real runtime name. The synthetic name remains explicit, `Virtual Reality — Simulated Headset (Index diagnostic profile)`, so original Index control-mode selection runs without fabricating connected controllers. Actual original SteamVR player proof logged `NATIVE_HEADSET_IDENTITY_PASS`, 21 actions, 5 stereo frames and 12 each original camera pre-cull/post-render/image-effect callbacks in `vr-probe/output/game-player.log`.
