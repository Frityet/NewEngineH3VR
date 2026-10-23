# Native FMOD music integration

The official FMOD Studio/Core SDK 2.03.15 macOS package was downloaded from the user's signed-in [FMOD Engine download page](https://www.fmod.com/download#fmodengine). The pinned DMG SHA256 is 85c3a2ba5d4cfd724a84fdebe98f71a44e0325c80132e0d1e42ab4afa4dcf24b. Its sources and original libraries are local ignored inputs under `.tools/fmod/sdk-2.03.15/`. The SDK and banks are not included in source control.

The bridge preserves the original FMOD 1.10.02 managed classes and controllers. Official native FMOD interprets the original banks, sequences the events and parameters, and mixes the audio. It does not replace adaptive events with extracted music files.

Build and verify:

```sh
platform/fmod/prepare-sdk.sh
platform/fmod/build.sh
dotnet run --project platform/fmod/patch/Patch.csproj -c Release -- build/steamworks/managed/NativeH3VR.FirstPass.dll build/fmod/managed/NativeH3VR.FirstPass.dll
python3 platform/fmod/audit.py
dotnet run --project platform/fmod/probe-legacy/Probe.csproj -c Release
```

Stage the resulting FirstPass DLL together with `libNativeH3VRFMOD.dylib`, `libfmodstudio.dylib`, and `libfmod.dylib`. The managed patch redirects 548 imports (545 distinct names) to the bridge. Every import resolves.

The native ABI audit compared 37 old/new structs. Six layouts changed; the bridge translates Core/Studio advanced settings, Studio CPU usage, parameter descriptions, create-sound information and Studio sound information. It also translates removed ParameterInstance handles to real event/parameter identities, listener APIs, native creation/version calls, and CPU reporting. Unchanged exports forward to the real native runtime.

Verified using the original managed FMOD API in arm64 .NET: all 7 installed banks load, all 24 events enumerate, TAH 2 Take plays, all 3 of its parameters get/set through the old ParameterInstance API, and the actual native mix produced peak 0.3749/RMS 0.253 with a 5152ms timeline. This used FMOD's non-real-time no-speaker output so measurement did not change physical sound settings. Evidence: `build/fmod/legacy-probe.json`, `banks.json`, `abi-layout.json`, and `exports.json`.

This is not a claim of full FMOD 1.10 behavioral parity. Unsupported obsolete Core sound-RAM queries and old custom asynchronous filesystem callbacks return real `ERR_UNSUPPORTED`; no game/FMODUnity call to those was found in the inspected recovered controllers. Nonzero removed create-sound channel masks also require further implementation. Legacy alternative pan position behavior and custom third-party DSP callback contracts require deeper testing. FMOD major versions can change mixing behavior, so every adaptive music mode still needs a gameplay/audition pass.

A public alternative was independently found in the maintained [Godot integration release 6.1.0-4.5.0](https://github.com/utopia-rise/fmod-gdextension/releases/tag/6.1.0-4.5.0), addons.zip SHA256 8796f6cd1cb38bf7ff4da2648f8af2695244ffdb80a7ac4ab8a171ca44895713. The final build uses the official downloaded SDK instead. Vendor confirmation of forward-compatible 1.10 bank loading: [FMOD developer response](https://qa.fmod.com/t/fmod-api-for-aarch64-on-linux/15918/5).

## Extended original-controller calling-surface check

`platform/fmod/parity/run.sh` checks the current composed legacy managed API
against the current native bridge. It loads all seven banks and all 24 events,
roundtrips all 30 event parameters through both the old ParameterInstance and
name-based methods, checks listener/event 3D attribute structs, music bus volume,
fadeout, immediate stop and bus teardown. Actual native DSP meters register
nonzero output for 23 events. The remaining East Winerton event reports zero
length, one-shot=true and no parameters, and stops after 21ms; its original bank
is unchanged. This observation is recorded rather than replacing it with
invented music or counting it as successful audible output.

All 24 bank events report 2D authoring. The GVR room component's original script
GUID has no references in the full pristine scene/prefab/asset export. Listener
and event 3D attribute roundtrips are real API checks, not an HRTF/panning claim.

The static composed-game audit records 53 direct FMOD/FMODY call sites, 117
reachable wrapper methods and 38 native imports, with no resolution failures.
It includes the unused GVR helper code; static reachability is not proof that a
path executes. Evidence: `build/fmod/parity/call-surface.json` and `events.json`.
The existing original RuntimeManager/CoreAudio startup proof remains separate.
