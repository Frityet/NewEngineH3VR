using Steamworks;
using System.Runtime.InteropServices;
using System.Text.Json;
var nativePath = Path.GetFullPath("build/steamworks/libCSteamworks.dylib");
NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly,
                                   (name, assembly, path) => name == "CSteamworks"
                                                                 ? NativeLibrary.Load(nativePath)
                                                                 : IntPtr.Zero);
var sizes = File.ReadAllLines("build/steamworks/abi-sizes.txt").Select(line => line.Split('='));
var sizeResults = sizes
                      .Select(parts => {
                          var type = typeof(SteamAPI).Assembly.GetType("Steamworks." + parts[0])!;
                          return new { type = type.FullName, nativeSize = int.Parse(parts[1]),
                                       managedSize = Marshal.SizeOf(type) };
                      })
                      .ToArray();
File.WriteAllText("build/steamworks/abi-layout.json",
                  JsonSerializer.Serialize(sizeResults,
                                           new JsonSerializerOptions { WriteIndented = true }));
var mismatch = sizeResults.Where(x => x.nativeSize != x.managedSize).ToArray();
Console.WriteLine($"ABI sizes verified={sizeResults.Length} mismatches={mismatch.Length}");
if (mismatch.Length != 0) {
    Console.WriteLine(JsonSerializer.Serialize(mismatch));
    return 4;
}
var packed = Packsize.Test();
Console.WriteLine(
    $"managed_pack_check={packed} user_stats_size={Marshal.SizeOf<UserStatsReceived_t>()} subscribed_files_size={Marshal.SizeOf<RemoteStorageEnumerateUserSubscribedFilesResult_t>()}");
if (!packed)
    return 1;
var initialized = SteamAPI.Init();
Console.WriteLine($"managed_initialized={initialized}");
if (!initialized)
    return 2;
try {
    var logged = SteamUser.BLoggedOn();
    var owns = SteamApps.BIsSubscribedApp(new AppId_t(450540));
    var app = SteamUtils.GetAppID().m_AppId;
    int received = 0;
    int callbackResult = 0;
    using (var callback = Callback<UserStatsReceived_t>.Create(data => {
        ++received;
        callbackResult = (int)data.m_eResult;
        Console.WriteLine($"managed_stats_callback={(int)data.m_eResult} app={data.m_nGameID}");
    })) {
        var requested = SteamUserStats.RequestCurrentStats();
        for (int i = 0; i < 100 && received == 0; i++) {
            SteamAPI.RunCallbacks();
            Thread.Sleep(100);
        }
        var report = new { architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                           packCheck = packed,
                           initialized,
                           loggedOn = logged,
                           owns450540 = owns,
                           appId = app,
                           requested,
                           callbacksReceived = received,
                           callbackResult };
        File.WriteAllText(
            "build/steamworks/managed-probe.json",
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return logged && owns && app == 450540 && received > 0 && callbackResult == 1 ? 0 : 3;
    }
} finally {
    SteamAPI.Shutdown();
}
