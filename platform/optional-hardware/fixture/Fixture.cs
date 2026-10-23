using System;
using System.Runtime.InteropServices;

public class ForceTubeVRInterface {
    [DllImport("ForceTubeVR_API_x32", EntryPoint = "InitAsync")]
    private static extern void InitAsync_x32();

    [DllImport("ForceTubeVR_API_x32", EntryPoint = "SetActive")]
    private static extern void SetActiveResearch_x32(bool active);

    [DllImport("ForceTubeVR_API_x32", EntryPoint = "Kick")]
    private static extern void Kick_x32(byte power);

    [DllImport("ForceTubeVR_API_x32", EntryPoint = "Rumble")]
    private static extern void Rumble_x32(byte power, float timeInSeconds);

    [DllImport("ForceTubeVR_API_x32", EntryPoint = "Shot")]
    private static extern void Shot_x32(byte kickPower, byte rumblePower, float rumbleDuration);

    [DllImport("ForceTubeVR_API_x32", EntryPoint = "TempoToKickPower")]
    private static extern byte TempoToKickPower_x32(float tempo);

    [DllImport("ForceTubeVR_API_x32", EntryPoint = "GetBatteryLevel")]
    private static extern byte GetBatteryLevel_x32();

    [DllImport("ForceTubeVR_API_x64", EntryPoint = "InitAsync")]
    private static extern void InitAsync_x64();

    [DllImport("ForceTubeVR_API_x64", EntryPoint = "SetActive")]
    private static extern void SetActiveResearch_x64(bool active);

    [DllImport("ForceTubeVR_API_x64", EntryPoint = "Kick")]
    private static extern void Kick_x64(byte power);

    [DllImport("ForceTubeVR_API_x64", EntryPoint = "Rumble")]
    private static extern void Rumble_x64(byte power, float timeInSeconds);

    [DllImport("ForceTubeVR_API_x64", EntryPoint = "Shot")]
    private static extern void Shot_x64(byte kickPower, byte rumblePower, float rumbleDuration);

    [DllImport("ForceTubeVR_API_x64", EntryPoint = "TempoToKickPower")]
    private static extern byte TempoToKickPower_x64(float tempo);

    [DllImport("ForceTubeVR_API_x64", EntryPoint = "GetBatteryLevel")]
    private static extern byte GetBatteryLevel_x64();

    private static void InitAsync() {
        if (NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()) {
            return;
        }
        if (IntPtr.Size == 8) {
            InitAsync_x64();
        } else {
            InitAsync_x32();
        }
    }

    public static void Kick(byte power) {
        if (NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()) {
            return;
        }
        if (IntPtr.Size == 8) {
            Kick_x64(power);
        } else {
            Kick_x32(power);
        }
    }

    public static void Rumble(byte power, float duration) {
        if (NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()) {
            return;
        }
        if (IntPtr.Size == 8) {
            Rumble_x64(power, duration);
        } else {
            Rumble_x32(power, duration);
        }
    }

    public static void Shoot(byte kickPower, byte rumblePower, float rumbleDuration) {
        if (NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()) {
            return;
        }
        if (IntPtr.Size == 8) {
            Shot_x64(kickPower, rumblePower, rumbleDuration);
        } else {
            Shot_x32(kickPower, rumblePower, rumbleDuration);
        }
    }

    public static void SetActiveResearch(bool active) {
        if (NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()) {
            return;
        }
        if (IntPtr.Size == 8) {
            SetActiveResearch_x64(active);
        } else {
            SetActiveResearch_x32(active);
        }
    }

    public static byte TempoToKickPower(float tempo) {
        if (NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()) {
            throw new NotSupportedException("ForceTube unavailable");
        }
        if (IntPtr.Size == 8) {
            return TempoToKickPower_x64(tempo);
        }
        return TempoToKickPower_x32(tempo);
    }

    public static byte GetBatteryLevel() {
        if (NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()) {
            throw new NotSupportedException("ForceTube unavailable");
        }
        if (IntPtr.Size == 8) {
            return GetBatteryLevel_x64();
        }
        return GetBatteryLevel_x32();
    }

    public static void OpenBluetoothSettings(bool isInVR) {}

    private static void OnLoadRuntimeMethod() { InitAsync(); }
}

namespace NativeH3VR {
public static class NativeSteamVRBootstrap {
    public static bool ForceTubeUnavailable() {
        return Environment.GetEnvironmentVariable("FORCETUBE_FIXTURE_UNAVAILABLE") == "1";
    }
}
}
public static class ForceTubeFixture {
    [DllImport("ForceTubeVR_API_x64", EntryPoint = "GetCount")]
    static extern int NativeCount();
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int Count() { return NativeCount(); }
    public static void Main() {
        typeof(ForceTubeVRInterface)
            .GetMethod("InitAsync", System.Reflection.BindingFlags.Static |
                                        System.Reflection.BindingFlags.NonPublic)
            .Invoke(null, null);
        ForceTubeVRInterface.Kick(11);
        ForceTubeVRInterface.Rumble(12, 0.5f);
        ForceTubeVRInterface.Shoot(13, 14, 0.6f);
        ForceTubeVRInterface.SetActiveResearch(true);
        if (NativeH3VR.NativeSteamVRBootstrap.ForceTubeUnavailable()) {
            int rejected = 0;
            try {
                ForceTubeVRInterface.GetBatteryLevel();
            } catch (NotSupportedException) {
                rejected++;
            }
            try {
                ForceTubeVRInterface.TempoToKickPower(2);
            } catch (NotSupportedException) {
                rejected++;
            }
            if (rejected != 2)
                throw new Exception("Unavailable queries fabricated success");
            Console.WriteLine(
                "PASS unavailable: optional commands disabled, both data queries reject");
        } else {
            if (ForceTubeVRInterface.GetBatteryLevel() != 42 ||
                ForceTubeVRInterface.TempoToKickPower(2) != 17 || Count() != 7)
                throw new Exception("Supported native behavior changed");
            Console.WriteLine(
                "PASS supported: all seven original native calls executed with original results");
        }
    }
}
