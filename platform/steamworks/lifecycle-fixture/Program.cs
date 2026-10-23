using System.Reflection;
public static class Trace {
    public static List<string> Calls = new();
    public static bool FailStore;
}
namespace Steamworks {
public static class SteamUserStats {
    public static bool StoreStats() {
        Trace.Calls.Add("StoreStats");
        if (Trace.FailStore)
            throw new InvalidOperationException("fixture failure");
        return true;
    }
}
public static class SteamAPI {
    public static void Shutdown() { Trace.Calls.Add("Shutdown"); }
}
}
public sealed class SteamManager {
    static SteamManager instance;
    bool initialized = true;
    public SteamManager() { instance = this; }
    void OnDestroy() {
        if (instance != this)
            return;
        instance = null;
        if (initialized)
            Steamworks.SteamAPI.Shutdown();
    }
    public void Destroy() => OnDestroy();
}
namespace FistVR {
public sealed class FVRSceneSettings {
    public void SteamSyncNow() {
        Steamworks.SteamUserStats.StoreStats();
        Trace.Calls.Add("SaveToFile");
    }
}
}
public static class Program {
    static void Need(bool b, string msg) {
        if (!b)
            throw new Exception(msg + ": " + string.Join(",", Trace.Calls));
    }
    public static void Main() {
        var scene = new FistVR.FVRSceneSettings();
        var manager = new SteamManager();
        scene.SteamSyncNow();
        manager.Destroy();
        scene.SteamSyncNow();
        manager.Destroy();
        Need(Trace.Calls.SequenceEqual(
                 new[] { "StoreStats", "SaveToFile", "StoreStats", "Shutdown", "SaveToFile" }),
             "normal updates, final real flush, local teardown save, duplicate destroy");
        typeof(SteamManager)
            .GetField("s_NativeSteamTeardown", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, false);
        Trace.Calls.Clear();
        Trace.FailStore = true;
        manager = new SteamManager();
        bool threw = false;
        try {
            manager.Destroy();
        } catch (InvalidOperationException) {
            threw = true;
        }
        Need(threw && Trace.Calls.SequenceEqual(new[] { "StoreStats", "Shutdown" }),
             "shutdown finally on StoreStats failure");
        scene.SteamSyncNow();
        Need(Trace.Calls.Last() == "SaveToFile", "local save after shutdown failure");
        Console.WriteLine("PASS: normal stats unchanged, final StoreStats before Shutdown, no " +
                          "post-shutdown stats calls, local saves retained, duplicate teardown " +
                          "safe, Shutdown guaranteed on failure.");
    }
}
