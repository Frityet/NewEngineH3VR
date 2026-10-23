# Full native player startup f13e1c6d

Read-only diagnosis of the first full packaged ARM64 player menu run, 2026-10-23. No app/Unity mutation, additional player launch, or hardware access occurred. Original runner receipt and complete log are preserved alongside two short `/usr/bin/sample` captures. The saved log SHA matches the runner receipt.

## Timing and warmup

The supervisor stopped on a real player-log error after **190.954 seconds**, not on its 600-second startup limit. Total run duration was 193.254 seconds, including 2.294 seconds of bounded cleanup. PID86038 exited after TERM, no SIGKILL was needed, and cleanup verified no remaining matched player.

The player launched at 01:32:17.792 BST. At sample timestamp 01:34:36.795 (~139 seconds after launch), its main thread was inside `PlayerLoadFirstScene → GraphicsSettings::WarmupPreloadedShaders → ShaderVariantCollection::WarmupShaders → SubProgram::Compile → CreateGpuProgram`, waiting on the graphics worker. That worker was in `GpuProgramMetal::CreateFromShaderLabSource → newLibraryWithSource → MTLCompilerScheduler`. The associated Metal compiler PID86064 was actively running `MTLGPUCompilerBuildFromSourceToBuffer → clang_metalfe`, not sitting idle. These are native pre-scene callbacks, explaining why the BeforeSceneLoad diagnostics/run-ID had not yet appeared at sampling time.

`packaged-preload.json` binds actual packaged metadata: original H3_ShaderAsset3 (94 shaders / 841 variants), followed by generated OriginalVariants (109 shaders / 28,298 variants). Sampling does not identify which collection was active. The original `GM.Awake` has another `ShaderVariants.WarmUp()` after initialization. It was not yet reached at sampling time but was reached before the final log error; the main menu subsequently logged Index detection and scene delta. Because player log lines have no individual timestamps, exact native-preload duration cannot be separated from later initialization. **190.954 seconds is time to failure, not an exact shader compilation duration.**

No eager-warmup change is necessary to unblock this bounded test. Repair the native package first and retain the accepted shader checkpoint. A future startup-performance experiment could separate generated variant retention from eager preloading, while preserving the original 841 preload and GM warmup, but that requires a new build/retention proof and is not justified as an emergency timeout workaround here.

## Actual managed/native import closure

`managed-imports.json` comes from Mono.Cecil reading actual packaged NativeH3VR assemblies, including their SHA-256 values, P/Invoke module names, entry points and calling conventions. It contains all 1,576 declarations in those assemblies; this does not claim every legacy declaration is reachable. `closure/Closure.csproj` is the read-only extractor.

`export-closure.json` compares unique expected exports against native `nm -gU` inventories. For FMOD only, it follows the explicit `LC_REEXPORT_DYLIB` commands into the packaged SDK libraries; direct-symbol-only checks would incorrectly reject valid forwarded functions.

At diagnosis time:

- XR: 19 unique used import names. The current native build and packaged ARM64 copy supply all 19. The stale root plugin lacks **nh3vr_xr_focused, nh3vr_xr_fade_color, nh3vr_xr_fade_grid, nh3vr_xr_fade_state**. The runtime exception confirms the stale copy was selected. Root SHA: `193c96a0441684847e79d6a5e0e4bf6c11714a97f75f3de37c1747abc10960e0`; current/ARM64 SHA: `408aad71ac9cb9d2b7065a58ad93502fb892b8eb7f427d3f2ca604fa39385e23`.
- Tracking: both declared imports exist in both package locations and the current native build.
- All 21 XR/Tracking imports have public C-header declarations. Managed widths match the shown int32/uint32/uint64/float/pointer shapes; this is not a fresh runtime ABI-size exercise.
- CSteamworks: all 850 unique import names exist in both packaged copies.
- FMOD bridge: all 545 unique import names resolve through direct exports plus the two explicit packaged SDK reexports, in both locations.

The platform agent owns package normalization, signing and the reusable gate. The export report is historical evidence for the failing package; its file hashes need not match a subsequently repaired package.

## Two unavailable features observed during startup

`unavailable-method-il.json` records the actual packaged method bodies for the two guarded OpenVR calls and SteamVR_Ears.OnEnable; recovered original source establishes their callers.

`CVRScreenshots.HookScreenshot` is called by SteamVR_Render.OnEnable or its successful-initialization callback, requesting SteamVR stereo-panorama screenshot registration. The native guard returns `EVRScreenshotError.RequestFailed` (1) with the explicit warning; the original callers ignore this return and continue rendering. The associated screenshot event/filename/submission path needs the unavailable OpenVR screenshot service. Thus SteamVR-requested panorama screenshots are unavailable. This does not disable this project's independent desktop/stereo diagnostic captures or block scene loading, firearm input, grabbing or firing.

`CVRSettings.GetBool("steamvr", "usingSpeakers")` has one original caller, SteamVR_Ears.OnEnable. Native mode returns false and writes `EVRSettingsError.UnsetSettingHasNoDefault` (5). The original caller leaves `usingSpeakers=false`, skips the subsequent speakersForwardYawOffsetDegrees query, and does not register the pose callback that fixes the audio listener to the room's speaker orientation. The listener remains parented to the head camera with zero local transform, i.e. the existing headphone/head-following path. This is suitable for headset/headphone listening; SteamVR's external-speaker orientation/yaw setting is not supported. Actual physical output routing/audio remains untested. The main-menu/range/firearm route does not depend on that setting.
