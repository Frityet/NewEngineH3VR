# Native platform adapters

These authored compatibility sources and `tools/PlatformPatch` were transferred
from MacVR commit `043eb90`, `integrations/h3vr/platform/{steamworks,fmod,audio-spatial,video}`.
They contain no Steamworks SDK, FMOD SDK, game assemblies or media. Their APIs
are matched to the user's locally installed game at conversion time. Steam
initialization, `RestartAppIfNecessary`, and ownership checks remain real Steam
operations; the bridge does not synthesize ownership.

`tools/PlatformPatch/build-native.py` requires explicit local paths for the
CSteamworks wrapper at commit `407dcba0151cfa52d1814a752e3038f16dbedc4d` with
its Steamworks 1.41 headers, the installed Steam client's `libsteam_api.dylib`,
and FMOD Studio/Core 2.03.15 SDK. It builds and signs private arm64 shims and
writes `platform.json` and plugin metadata in a new caller-selected directory.
It neither downloads nor redistributes those SDKs. Feed that directory to the
UnityMetal integration builder's `--platform` option.

The macOS managed conversion stage applies the Darwin Steam ABI adjustment,
redirects FMOD native calls to the bridge, and adapts AVPro's factory to the
Unity VideoPlayer backend. A successful managed or native build does not prove
Steam client authorization, audio parity, decoding, or gameplay. FMOD's older
unsupported callback cases and the video adapter's DRM, embedded subtitle,
custom HTTP header, and AVPro buffered synchronization gaps remain open.
