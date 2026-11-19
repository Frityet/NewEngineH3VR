# Native platform adapters

These let the game's Steam, FMOD and video code run natively on macOS. They and `tools/PlatformPatch` came from MacVR commit `043eb90` (`integrations/h3vr/platform/{steamworks,fmod,audio-spatial,video}`).

No SDKs, game code or media are included. The adapters are matched to your installed game during conversion. Steam stays real: startup, `RestartAppIfNecessary` and ownership checks all go to the actual Steam client.

## Building the shims

`dotnet run --project tools/PlatformPatch/NativeBuilder -c Release --` takes local paths to:

- CSteamworks at `407dcba0151cfa52d1814a752e3038f16dbedc4d`, with its Steamworks 1.41 headers
- `libsteam_api.dylib` from your Steam client
- FMOD Studio/Core 2.03.15 SDK

It builds and signs arm64 shims into a new folder, with `platform.json` and plug-in metadata. It never downloads or redistributes the SDKs. Pass the folder to the integration builder's `--platform` ([full commands](../../docs/platform-integrations.md)).

During conversion on macOS, Steam calls are adjusted for the Darwin ABI, FMOD calls go to the bridge, and AVPro video is swapped for Unity's VideoPlayer.

## Known gaps

A working build doesn't prove Steam authorization, audio, video or gameplay work. Still open: some older FMOD callbacks, and video DRM, embedded subtitles, custom HTTP headers and AVPro buffered sync.
