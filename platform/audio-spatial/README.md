# Native audio spatialization and FMOD startup audit

The original game's preloaded `AudioPluginMsHRTF`, `AudioPluginOculusSpatializer` and `libAudioPluginOculusSpatializer` are unused in the audited original assets and code. No external spatializer replacement is required for this content:

- Original AudioManager selects an empty spatializer-plugin name.
- All 1,725 AudioSources have `Spatialize=false` and `SpatializePostEffects=false`. This does **not** mean 2D audio: 939 have full built-in Unity 3D spatial blend, 151 partial blend and 635 zero blend; their settings remain unchanged.
- All 13,762 AudioClips have no ambisonic flag. All 36 AudioMixerControllers have an empty native plugin-effect name buffer; built-in effects remain present.
- Cecil inspected 33,760 methods in nine game/plugin assemblies and found zero spatializer API calls, native spatializer imports or spatializer-name string references.
- Source assets inside StreamingAssets bundles are included. `audit_assets.py` visits the 98 inventoried files containing audio objects; no object-read failures occurred.

Evidence: `build/audio-spatial/{assets.json,managed.json,mixer-summary.json}`. These checks do not establish subjective acoustic parity or headset listening approval.

## FMOD fixes and real startup proof

`build-bridge.sh` compiles the existing FMOD bridge plus `fmod-output.cpp` into `build/audio-spatial/libNativeH3VRFMOD.dylib`. FMOD 1.10 and 2.03 assign different OUTPUTTYPE numbers after5; the added functions translate real get/set calls. CoreAudio is modern10 and legacy12. Removed output types return unsupported; unrepresentable modern query values return the legacy UNKNOWN enum. AUTODETECT continues to call the real engine.

`patch/Patch.csproj` changes only `FMODUnity.RuntimeUtils.GetCurrentPlatform` from the original Windows constant to Mac9. Apply to the **latest composed FirstPass** so every other patch is preserved:

```sh
platform/audio-spatial/build-bridge.sh
dotnet run --project platform/audio-spatial/patch/Patch.csproj -c Release -- build/composed/final/NativeH3VR.FirstPass.dll build/audio-spatial/managed/NativeH3VR.FirstPass.dll
```

The isolated native ARM64 Unity2020 player exercises the **original RuntimeManager.Initialize**, recovered `FMODStudioSettings`, native AUTODETECT output, original GetBankPath and automatic event/sample loading. It initialized CoreAudio at48kHz with32 software channels, loaded all7banks/24events, and did not fall back to NOSOUND. It starts no music event. Results: `build/audio-spatial/startup.json`, `player.log`. The probe has no StudioListener because it tests startup only, so the integration's expected missing-listener warning is not evidence of a game-scene listener problem.

`bank-paths.json` verifies all7banks in the original installation, working full project and current native game app are SHA256-identical and correctly located under StreamingAssets. Absence of FMOD errors in the main-menu log alone does not prove the game invoked RuntimeManager; the isolated startup probe is the affirmative startup evidence. Earlier platform probes separately measured real bank/event mixing.

`virtualChannels` records the recovered platform setting (128), while rate and real-channel count are queried from the initialized CoreAudio system. No application/audio settings were changed in the main or full Unity projects during this work.

The canonical `platform/fmod/build.sh` now includes the output translation, and `platform/fmod/patch/Program.cs` also selects Mac when rebuilding FirstPass from the Steam-patched baseline. The supplemental patch remains useful for updating an already composed FirstPass without discarding other changes.
