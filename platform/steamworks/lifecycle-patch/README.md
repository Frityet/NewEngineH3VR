# Steam shutdown ordering

The game's SteamManager may be destroyed before FVRSceneSettings. Its original OnDestroy shuts Steam down, then FVRSceneSettings.OnDestroy invokes SteamSyncNow, whose StoreStats call throws before local progress can be saved.

This local assembly migration:

1. Replaces SteamManager's shutdown call with a helper that marks teardown, invokes **real SteamUserStats.StoreStats**, then invokes **real SteamAPI.Shutdown in a finally block**.
2. Guards only the StoreStats portion of FVRSceneSettings.SteamSyncNow once teardown begins. Its existing local PStats.SaveToFile and timer update remain intact.

Normal gameplay stat calls are unchanged. Nothing reports fabricated success. This queues the final stats update before shutdown; the synchronous quit path does not wait for a server acknowledgment. It does not change ownership checks or initialization.

Apply **after other Game assembly changes**, using the latest composition:

```sh
dotnet run --project platform/steamworks/lifecycle-patch/Patch.csproj -c Release -- build/composed/final/NativeH3VR.Game.dll build/steamworks/lifecycle/NativeH3VR.Game.dll
```

The patcher rejects already patched inputs and unexpected call layouts. A sidecar `NativeH3VR.Game.lifecycle.json` records the input/output and changes.

`../lifecycle-fixture` exercises the actual patcher against instrumented, isolated managed methods with the original relevant shapes. It verifies normal stats calls, final StoreStats-before-Shutdown ordering, preserved late local saves, duplicate destruction, and guaranteed shutdown when StoreStats throws. The test makes **no real Steam stats or achievement writes**. Results: `build/steamworks/lifecycle/fixture-result.txt`. The combined migrated game+audio assemblies also pass the managed reference audit. Actual game shutdown remains the final integration check for the parent build.
