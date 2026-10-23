# Managed H3VR migration

This directory contains a reproducible, local-only binary migration of the installed H3VR managed assemblies to Unity 2020.3.49f1. The original Steam files and the other H3VR projects are read-only inputs. This is a verified API-linking checkpoint, not a complete runnable port or gameplay validation.

## Reproduce

From `/Users/frityet/Projects/NativeH3VR`:

```sh
dotnet run --project audit-managed/AuditManaged.csproj -c Release
dotnet build audit-managed/shim/NativeH3VR.Compat.csproj -c Release
dotnet run --project audit-managed/retarget/Retarget.csproj -c Release
dotnet run --project audit-managed/verify/Verify.csproj -c Release
```

All source and output paths are local. The Cecil package is reused from the existing NuGet cache. The shim references the installed Unity macOS player modules. These managed DLLs contain IL, not x86 machine code; the native arm64 player executes them through its arm64 Mono runtime.

## Actual source baseline

The installed `h3vr_Data/globalgamemanagers` bytes at offset 0x14 identify Unity **5.6.7f1**. Its 19 managed DLLs target `v2.0.50727` and are `ILOnly`, with no `Required32Bit` flag. `Assembly-CSharp.dll` contains 3,722 types and 21,683 methods, including 1,382 types in the exact `FistVR` namespace (additional FistVR subnamespaces are separate). `Assembly-CSharp-firstpass.dll` contains 1,531 types and 7,856 methods, including Steamworks, OpenVR, FMOD, and FinalIK.

Input hashes, full references and counts are in `results/assemblies.json`. P/Invoke declarations, their actual managed callers, and Unity API call sites are recorded separately in `results/`.

## Produced files and import

`ported/` contains twelve DLLs. The three special Unity-generated assembly names have been renamed so that the recovered Unity project can reference them as plugins:

| Original | Output |
| --- | --- |
| Assembly-CSharp | NativeH3VR.Game |
| Assembly-CSharp-firstpass | NativeH3VR.FirstPass |
| Assembly-UnityScript | NativeH3VR.UnityScript |

All interassembly references and assembly-name literals referencing those names are migrated. Other third-party assembly names are retained. The old **UnityEngine.UI** and **UnityEngine.Networking** implementations are preserved and retargeted. Do not load the modern UGUI package's UnityEngine.UI alongside this one.

Import these DLLs into a separate working recovery project. Remove the equivalent decompiled `.cs` inputs from that working project to avoid duplicate classes. Rebind every recovered MonoScript reference to its DLL MonoScript identity. Asset agent supplied `../audit-assets/monoscript-map.json` with original assembly/type and recovered GUID mappings. Preserve execution order from the original `.cs.meta` files: for example `FistVR.GM` has `executionOrder: -16000`.

Do not copy the game's old `UnityEngine.dll`, `UnityEngine.VR.dll`, `mscorlib.dll`, or System/Mono framework assemblies into a Unity 2020 player. Those belong to the new engine/runtime.

## Migration behavior

The retargeter resolves all old engine TypeRefs to their actual 2020 modules. It moves `UnityEngine.VR` types to the matching `UnityEngine.XR` types, and moves `Experimental.Rendering.RenderPipelineAsset` to `Rendering.RenderPipelineAsset`.

The concrete API migrations are:

- Texture PNG/EXR encoding and image decoding use `ImageConversion`.
- The old UnityWebRequest error property maps to network-or-HTTP error, preserving its old combined meaning.
- VR render scale maps to `XRSettings.eyeTextureResolutionScale`.
- VR model maps to the actual XR head device name, or an empty string when absent; no headset is fabricated.
- WWW audio clip property maps to `GetAudioClip()`.
- Camera HDR maps to `allowHDR`.
- Immediate indirect procedural drawing maps to `DrawProceduralIndirectNow`.
- The UI vertex UV fields are converted from old Vector2 to current Vector4 storage. Reads retain XY and writes use the engine's implicit XY00 conversion.
- The old UI stream helpers convert UV lists around the actual engine implementation, including output-list updates.
- Old ScriptingUtils delegate creation uses `Delegate.CreateDelegate`.

Short IL branches are widened before output so inserted conversion calls cannot overflow short-branch distances. Detailed mutations are in `ported/retarget-manifest.json`.

`ported/verification.json` records a successful re-read and resolution of **1,753 type references, 98,441 engine/compatibility call sites (4,613 distinct methods), and 14,476 engine field accesses**, with **zero unresolved references**. This proves metadata/API compatibility only; it does not prove Unity serialization, native plugin ABI, physics parity, rendering parity, or gameplay.

## Native dependencies that remain

| Managed import | Declarations | Current role / next action |
| --- | ---: | --- |
| CSteamworks | 850 | Steam startup/account/API integration. `SteamManager.Awake` runs pack/DLL checks, RestartAppIfNecessary(450540), and Init, then Update runs callbacks. Rebuild wrapper and audit Darwin callback/vtable/packing ABI; do not bypass Steam ownership. |
| openvr_api | 10 | Actual 25 managed call sites initialize OpenVR interfaces and delegates. Needs a native arm64 OpenVR implementation backed by the real macOS tracking/render runtime. SteamVR managed code must use this backend. |
| fmodstudio | 548 | FMOD/Studio integration; actual FMOD bank use and bank-version compatibility must be checked. Requires native compatible engine or a faithful replacement. |
| AVProVideo / AVProVideoWinRT | 79 / 40 | Windows video implementations compiled directly into game DLL. `MediaPlayer.CreateMediaPlayer()` always calls the Windows factory, so merely changing platform is insufficient. Replace the backend with native playback while preserving texture, audio, seeking and events. |
| ForceTubeVR_API_x32 / _x64 | 7 / 7 | Peripheral haptics; Windows API selection needs a native implementation for feature completeness. |
| LIV_MR | 5 | Mixed-reality integration; native implementation needed for feature completeness. |
| sdkencryptedappticket | 8 | Steam encrypted ticket helpers; needs compatible native dependency if used. |
| kernel32.dll | 1 | AVPro helper `GetShortPathNameW`; remove Windows short-path requirement in the native video backend. |

The installed Plugins directory also contains EasyMovieTexture, Audio360, FFmpeg components, gvraudio, resonanceaudio and libvhacd, but they have **no direct P/Invoke declarations in the inspected managed assemblies**. That is not evidence they are unused: engine audio spatializers, native-to-native dependencies, serialized components and runtime-loaded assets need separate inspection.

The native dependency inventory includes optional paths as well as boot-critical ones. Disabling optional integrations or reporting successful placeholder return values would not satisfy the requested 100% native functionality. No native P/Invoke has been silently stubbed by this migration.
