# Auditing the stock renderer

The stock menu test loads only its diagnostic DLL into a signed local player. It starts MainMenu3 with a fresh save directory and synthetic Index tracking, waits for the stock player's eye camera, and reads the two render textures that NativeStereoRenderer submitted to OpenXR. It also captures the runtime compositor. The original Steam install and the normal player's preferences have separate paths.

Run from a NativeH3VR checkout with an imported player, a built UnityMetalCli, and a packaged MacVR runtime.

```sh
python3 tests/StockMenuRendering/run.py \
  --app "/path/to/NativeH3VR.app" \
  --launcher "/path/to/UnityMetalCli.dll" \
  --runtime "/path/to/MacVR-runtime" \
  --out "/path/to/fresh/output" \
  --survey-scene WarehouseRange_Rebuilt --survey-scene ArizonaTargets

uv run tests/StockMenuRendering/measure.py "/path/to/fresh/output" --verify-stock

python3 tests/StockMenuRendering/verify_profiles.py \
  "/path/to/recovery/ExportedProject" "/path/to/retargeted/project" > stock-profiles.json

python3 tests/StockMenuRendering/verify_scenes.py stock-profiles.json /path/to/completed/scene-runs/*
```

The report records the actual HDR camera, eye transforms, frusta, render formats, active and inactive materials, lights, cubemaps and post-processing resources. Each eye is captured twice with identical settings to establish the noise floor. Further captures switch off the grade, switch off bloom, toggle AO, disable the whole layer and restore the original settings. The neutral test card samples five known material input values and the black surround beside an HDR patch. The test also records the blended grading settings, the five sampled curve values and the full 33³ floating-point LUT. Use `--gear` to capture the stock Index controller wire meshes at fixed poses. Each `--survey-scene` enters another official scene through the stock scene loader and records its two eyes.

A matched run of the official Windows D3D11 player used High quality, bloom and grading on, AO off and no eye MSAA. Both tests used a stationary, forward-facing head with 64 mm IPD and symmetric 0.85-radian half angles. The macOS readback was one pixel taller and the stock rigs placed the synthetic head 7 mm apart vertically. Compare flat patches and registered regions when using these output images.

| Neutral card, grading on and bloom off | Windows D3D11 | Original native build | Corrected shader |
| --- | ---: | ---: | ---: |
| Black surround | 0.6 | 31.7 | 0.6 |
| Input 0.18 | 68.0 | 81.1 | 68.0 |
| Input 0.5 | 164.4 | 149.2 | 164.3 |
| Input 1 | 235.9 | 205.6 | 235.9 |

Values are 8-bit display codes. The signed release matched Windows within 0.10 code across all six sampled patches and both eyes. In the original native build, disabling all grading already recovered the Windows ungraded swatches. The stock LUT was correct. The DXBC exporter emitted a natural logarithm in the shader that indexes the LUT, which changes the input coordinate across the whole range. The corrected exporter emits base-two HLSL operations for the corresponding Direct3D instructions. The reference check allows two display codes in a smaller 7 × 7 sample to accommodate dithering. [Direct3D log](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/log--sm4---asm-) · [Direct3D exp](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/exp--sm4---asm-)

With the original scene cubemap restored, the mean ungraded luminance of three menu wall regions reached 38.4–45.2, compared with 40.6–49.1 on Windows and 0.9–1.0 in the earlier native build. The source cubemap is 1024 square, linear BC6H, with eleven mip levels. The converter verifies every restored per-face payload against the original game. The two menu eye textures and the corresponding synthetic MacVR compositor pixels agreed exactly after splitting the eyes. A physical headset display measurement would require a separate optical capture.

The recovered stock materials also required migration of their saved HDR colors. The original and native IndexWire meshes have identical vertex positions, normals, tangents and UVs. On a neutral backdrop, equivalent material values produced a smaller blue rim on Metal, while a separate common HDR patch produced the expected bloom spread in both games. Converting the original HDR color components to linear during stock material import restored 97–100% of the original blue outline area across two orientations and both eyes. The measured halo retained 66–93% of the original intensity in this fixed-pose test. The normal scene controller measured 120.3–120.6 compared with 115.9–116.1 on Windows.

On the signed player, mean menu luminance on the left was 76.02 on Metal and 75.88 on D3D11. The right eye measured 75.91 and 75.84. The three sampled wall panels differed by at most 0.60 across both eyes. Bright interior pixels in the title and welcome lettering had median luminance within one code of Windows. Bloom and grading were enabled in both games. On Metal, turning on the stock menu's optional ambient occlusion changed 31–32% of each eye by more than five RGB codes. The repeat capture with unchanged settings recorded zero pixels above that threshold.

All 46 official scenes loaded through the stock scene loader and submitted color images for both eyes. The survey found 51 distinct supported material shaders and zero unsupported shaders assigned to an active scene renderer. Each Friendly45 variant retains three inactive fir meshes with an empty stock material. Their original Windows material has a null shader pointer and their common root is disabled. Runtime surveys include these placeholders and empty particle or equipment slots so the captured state can be inspected.

The 46 original scenes contain 155 post-processing volumes. Their 25 shared profiles comprise 13 grading presets, eight bloom presets and four ambient occlusion presets. The migrated Unity project preserves the original weights, curves, colors, profile GUIDs and every serialized setting in these volumes and profiles. The source check verifies the new managed class binding for every component and compares its remaining fields verbatim.

All 34 post-processing shader and compute references resolved. The signed release also resolved all five terrain shader lookups through Unity's public `Shader.GetDependency` API, including grass, terrain detail and billboard trees. All 18 rebuilt asset bundles opened in Unity and exposed their original 4,387 addresses. These checks cover the original preset data and the three effect families exercised in the player. Individual presets, external mod shaders and physical headset optics require separate visual comparisons.

All 214 active packed shaders affected by the base-two conversion matched their original source identities, including 78 in the main player and 136 in its stock bundles. The audit loaded all 14 AVPro and Amplify resource addresses in the player, including both bloom shaders. The Steam re-export yielded 277 identified graphics shader sources and the same keyword variant catalog. The 11 compute shaders retained their 22 original kernels.

The signed game also rendered stock reticle glow, scene lighting controls, emissive indicators and particle tint using the original material setter inputs. All 112 named-setter comparisons in the two eyes agreed with the Windows reference within one 8-bit code per channel. The 14 per-renderer property-block comparisons matched exactly. The fresh import marks 18 identified stock materials for the legacy behavior at 20 stock call sites. The controls included an untagged HDR material and a tagged ordinary color property.

The linear HDR color migration applies to the verified Steam material import on every desktop target. A separate Windows D3D11 test drew the stock Alloy/Unlit shader into a floating-point target. Steam rendered the authored tint (0.5, 1.5, 0.125) as (0.21404, 2.44006, 0.01435). Unity 6.7 on Windows initially passed the authored values directly. After migration, both the saved material and the runtime setter matched the Steam readback in all four channels. The same test kept white at (1, 1, 1, 1).
