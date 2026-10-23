# Native Steam integration

This rebuilds CSteamworks SDK 1.41 entry points against the installed native Steam client API. No authentication or subscription result is synthesized.

1. `platform/steamworks/build.sh`
2. `dotnet run --project platform/steamworks/managed/Patch.csproj -c Release -- audit-managed/ported/NativeH3VR.FirstPass.dll build/steamworks/managed/NativeH3VR.FirstPass.dll`
3. `python3 platform/steamworks/verify-exports-and-layout.py`
4. `SteamAppId=450540 SteamGameId=450540 dotnet run --project platform/steamworks/probe-managed/Probe.csproj -c Release`

Use the existing `.tools/steamworks/CSteamworks` checkout at 407dcba0151cfa52d1814a752e3038f16dbedc4d, generated against the copied SDK 1.41 headers. `build.sh` reads the installed Steam Helper's native universal libsteam_api.dylib, copies it into the local build, fixes its install name and ad-hoc signs the copy.

The managed migration changes 166 pack 8 declarations to Darwin pack 4, reverses the Windows callback vtable overload ordering, updates the actual packing self-test, and appends the SDK 1.41 PublishedFileId field to SubmitItemUpdateResult. All 165 directly comparable native structure sizes match. All 850 distinct imports resolve. The real original managed callback code received UserStatsReceived(result 1), and the native account reported logged-in and owning 450540. Evidence is in `build/steamworks/managed-probe.json`, `abi-layout.json` and `exports.json`.

The deprecated private Steam_RegisterInterfaceFuncs export can only forward to an actual implementation supplied by its module. If none exists, it fails explicitly; no inspected managed caller uses it. Other removed legacy entry points are translated to current real Steam operations. GameServer translation has not been integration-tested.

See `../launch/README.md` for the native app launcher. Keep actual RestartAppIfNecessary/Init/ownership behavior enabled.
