using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NativeH3VR;
using Valve.VR;
using Old = OriginalNativeH3VR.NativeSteamVRInput;
using New = NativeH3VR.NativeSteamVRInput;

static class Program {
    static int assertions;
    static readonly List<string> cases = new List<string>();
    static readonly List<object> allocations = new List<object>();
    static string directory;
    static ulong set, otherSet, left, right, trigger, analog, pose, skeleton;
    static long sink;
    static void Need(bool value, string label) {
        assertions++;
        if (!value)
            throw new Exception(label);
    }
    static uint Size<T>() { return (uint)Marshal.SizeOf(typeof(T)); }
    static void Equal<T>(T before, T after, string label) {
        Need(EqualityComparer<T>.Default.Equals(before, after),
             label + ": " + before + " != " + after);
    }
    static void Case(string name) { cases.Add(name); }
    static NativeTracking.Device Device(uint role, uint flags, uint buttons, float axis = 0) {
        var d = new NativeTracking.Device { role = role, flags = flags, buttons = buttons,
                                            axes = new float[16] };
        d.axes[1] = axis;
        d.axes[2] = .3f;
        d.axes[3] = -.7f;
        return d;
    }
    static NativeTracking.Snapshot Sample(params NativeTracking.Device[] devices) {
        return new NativeTracking.Snapshot { deviceCount = (uint)devices.Length,
                                             devices = devices };
    }
    static void Fixture(NativeTracking.Snapshot before, NativeTracking.Snapshot after) {
        Old.SetFixture(before, after);
        New.SetFixture(before, after);
    }
    static void Manifest(string name, string json) {
        string path = Path.Combine(directory, name + ".json");
        File.WriteAllText(path, json);
        Equal(Old.SetActionManifestPath(path), New.SetActionManifestPath(path), "manifest " + name);
    }
    static ulong Action(string path) {
        ulong a = 999, b = 999;
        Equal(Old.GetActionHandle(path, ref a), New.GetActionHandle(path, ref b), "action " + path);
        Equal(a, b, "handle " + path);
        return a;
    }
    static ulong Set(string path) {
        ulong a = 999, b = 999;
        Equal(Old.GetActionSetHandle(path, ref a), New.GetActionSetHandle(path, ref b),
              "set " + path);
        Equal(a, b, "set handle " + path);
        return a;
    }
    static ulong Source(string path) {
        ulong a = 999, b = 999;
        Equal(Old.GetInputSourceHandle(path, ref a), New.GetInputSourceHandle(path, ref b),
              "source " + path);
        Equal(a, b, "source handle " + path);
        return a;
    }
    static void Activate(params VRActiveActionSet_t[] sets) {
        Equal(Old.UpdateActionState(sets, Size<VRActiveActionSet_t>()),
              New.UpdateActionState(sets, Size<VRActiveActionSet_t>()), "activation");
    }
    static InputDigitalActionData_t Digital(ulong action, ulong source, uint? size = null) {
        var a = new InputDigitalActionData_t { bActive = true, bState = true, activeOrigin = 999 };
        var b = a;
        Equal(Old.GetDigitalActionData(action, ref a, size ?? Size<InputDigitalActionData_t>(),
                                       source),
              New.GetDigitalActionData(action, ref b, size ?? Size<InputDigitalActionData_t>(),
                                       source),
              "digital error");
        Equal(a, b, "digital fields");
        return b;
    }
    static void Analog(ulong action, ulong source, uint? size = null) {
        var a = new InputAnalogActionData_t { bActive = true, activeOrigin = 999, x = 999 };
        var b = a;
        Equal(
            Old.GetAnalogActionData(action, ref a, size ?? Size<InputAnalogActionData_t>(), source),
            New.GetAnalogActionData(action, ref b, size ?? Size<InputAnalogActionData_t>(), source),
            "analog error");
        Equal(a, b, "analog fields");
    }
    static void
    Pose(ulong action, ulong source,
         ETrackingUniverseOrigin origin = ETrackingUniverseOrigin.TrackingUniverseStanding,
         uint? size = null) {
        var a = new InputPoseActionData_t { bActive = true, activeOrigin = 999 };
        var b = a;
        Equal(Old.GetPoseActionDataForNextFrame(action, origin, ref a,
                                                size ?? Size<InputPoseActionData_t>(), source),
              New.GetPoseActionDataForNextFrame(action, origin, ref b,
                                                size ?? Size<InputPoseActionData_t>(), source),
              "pose error");
        Equal(a, b, "pose fields");
    }
    static void QueryAll() {
        foreach (ulong source in new[] { 0UL, left, right, 987654321UL }) {
            Digital(trigger, source);
            Analog(analog, source);
            Pose(pose, source);
        }
    }
    static void Behavior() {
        Old.Synthetic = New.Synthetic = true;
        Fixture(default(NativeTracking.Snapshot), default(NativeTracking.Snapshot));
        Manifest("main",
                 "{\"actions\":[{\"name\":\"/actions/default/in/" +
                     "Trigger_Button\",\"type\":\"boolean\"},{\"name\":\"/actions/default/in/" +
                     "Primary2Axis_Axes\",\"type\":\"vector2\"},{\"name\":\"/actions/default/" +
                     "in/Pose\",\"type\":\"pose\"},{\"name\":\"/actions/default/in/" +
                     "LeftSkeleton\",\"type\":\"skeleton\",\"skeleton\":\"/skeleton/hand/" +
                     "left\"}],\"action_sets\":[{\"name\":\"/actions/default\"},{\"name\":\"/" +
                     "actions/other\"}]}");
        set = Set("/actions/default");
        otherSet = Set("/actions/other");
        trigger = Action("/actions/default/in/Trigger_Button");
        analog = Action("/actions/default/in/Primary2Axis_Axes");
        pose = Action("/actions/default/in/Pose");
        skeleton = Action("/actions/default/in/LeftSkeleton");
        left = Source("/user/hand/left");
        right = Source("/user/hand/right");
        Equal(trigger, Action("/ACTIONS/DEFAULT/IN/TRIGGER_BUTTON"),
              "case insensitive same handle");
        Action(null);
        Action("missing");
        Set("/actions/default/in/Trigger_Button");
        Source(null);
        Source("");
        Source("/unrestricted");
        Source("not a source");
        QueryAll();
        Case("manifest, duplicate/case-insensitive handles, invalid names, inactive sets");
        const uint flags = NativeTracking.Connected | NativeTracking.InputCurrent;
        var neutral = Sample(Device(2, flags, 0), Device(3, flags, 0));
        var held = Sample(Device(2, flags, 1, .8f), Device(3, flags, 0));
        Fixture(neutral, held);
        Activate(new VRActiveActionSet_t { ulActionSet = set });
        QueryAll();
        Need(Digital(trigger, left).bChanged && Digital(trigger, left).bState, "press edge");
        Fixture(held, held);
        Need(!Digital(trigger, left).bChanged, "steady hold");
        Fixture(held, neutral);
        Need(Digital(trigger, left).bChanged && !Digital(trigger, left).bState, "release edge");
        Case("press, hold, release, analog deltas, absent origins and Any preference");
        Fixture(neutral, held);
        foreach (var sets in new[] {
                     new[] { new VRActiveActionSet_t { ulActionSet = set,
                                                       ulRestrictedToDevice = left } },
                     new[] { new VRActiveActionSet_t { ulActionSet = set,
                                                       ulRestrictedToDevice = right } },
                     new[] {
                         new VRActiveActionSet_t { ulActionSet = set, ulRestrictedToDevice = left },
                         new VRActiveActionSet_t { ulActionSet = set, ulRestrictedToDevice = right }
                     },
                     new[] { new VRActiveActionSet_t { ulActionSet = set,
                                                       ulRestrictedToDevice = right },
                             new VRActiveActionSet_t { ulActionSet = set } },
                     new[] { new VRActiveActionSet_t { ulActionSet = otherSet } },
                     new VRActiveActionSet_t[0]
                 }) {
            Activate(sets);
            QueryAll();
        }
        Case("dynamic activation, Any, restricted devices, duplicates and empty activation");
        Activate(new VRActiveActionSet_t { ulActionSet = set });
        Equal(Old.UpdateActionState(null, Size<VRActiveActionSet_t>()),
              New.UpdateActionState(null, Size<VRActiveActionSet_t>()), "null activation");
        QueryAll();
        Equal(Old.UpdateActionState(new VRActiveActionSet_t[0], 0),
              New.UpdateActionState(new VRActiveActionSet_t[0], 0), "wrong activation size");
        QueryAll();
        Activate(new VRActiveActionSet_t { ulActionSet = set },
                 new VRActiveActionSet_t { ulActionSet = 999999 });
        QueryAll();
        Activate(new VRActiveActionSet_t { ulActionSet = 999999 },
                 new VRActiveActionSet_t { ulActionSet = set });
        QueryAll();
        Digital(0, 0);
        Analog(0, 0);
        Digital(analog, left);
        Analog(trigger, left);
        Pose(trigger, left);
        Digital(trigger, left, 0);
        Analog(analog, left, 0);
        Pose(pose, left, ETrackingUniverseOrigin.TrackingUniverseSeated);
        Pose(pose, left, size: 0);
        foreach (ulong source in new[] { left, right, 0UL, 999999UL }) {
            var a = new InputOriginInfo_t();
            var b = a;
            Equal(Old.GetOriginTrackedDeviceInfo(source, ref a, Size<InputOriginInfo_t>()),
                  New.GetOriginTrackedDeviceInfo(source, ref b, Size<InputOriginInfo_t>()),
                  "origin error");
            Equal(a, b, "origin fields");
        }
        var sa = new InputSkeletalActionData_t();
        var sb = sa;
        Equal(Old.GetSkeletalActionData(skeleton, ref sa, 0),
              New.GetSkeletalActionData(skeleton, ref sb, 0), "skeletal size error");
        Equal(sa, sb, "skeletal reset");
        Activate();
        foreach (ulong action in new[] { skeleton, trigger, 999999UL }) {
            sa = default(InputSkeletalActionData_t);
            sb = sa;
            Equal(Old.GetSkeletalActionData(action, ref sa, Size<InputSkeletalActionData_t>()),
                  New.GetSkeletalActionData(action, ref sb, Size<InputSkeletalActionData_t>()),
                  "inactive skeletal lookup");
            Equal(sa, sb, "inactive skeletal fields");
        }
        Case("invalid ABI sizes/types/handles and partial activation-error state");
        Activate(new VRActiveActionSet_t { ulActionSet = set });
        var stale = Sample(Device(2, NativeTracking.Connected, 1, .8f));
        Fixture(held, stale);
        QueryAll();
        Need(!Digital(trigger, left).bActive, "stale withheld");
        Old.EndFixture();
        New.EndFixture();
        Old.SetFocusFixture(neutral, true);
        New.SetFocusFixture(neutral, true);
        Old.SetFocusFixture(held, true);
        New.SetFocusFixture(held, true);
        Need(Digital(trigger, left).bState, "focus initial press");
        var retainedOld = Old.Snapshot;
        var retainedNew = New.Snapshot;
        Old.SetFocusFixture(held, false);
        New.SetFocusFixture(held, false);
        Need(!Digital(trigger, left).bState, "focus loss release");
        Need(retainedOld.devices[0].buttons == 1 && retainedNew.devices[0].buttons == 1 &&
                 held.devices[0].buttons == 1,
             "retained/raw snapshot ownership");
        Old.SetFocusFixture(held, true);
        New.SetFocusFixture(held, true);
        Need(!Digital(trigger, left).bState, "held on regain gated");
        Old.SetFocusFixture(neutral, true);
        New.SetFocusFixture(neutral, true);
        Need(!Digital(trigger, left).bState, "neutral rearm");
        Old.SetFocusFixture(held, true);
        New.SetFocusFixture(held, true);
        Need(Digital(trigger, left).bState && Digital(trigger, left).bChanged,
             "new postfocus edge");
        Case("freshness, focus loss/neutral rearm, retained snapshot ownership");
        Old.EndFixture();
        New.EndFixture();
        Fixture(neutral, held);
        Manifest("duplicate", "{\"actions\":[{\"name\":\"/ACTIONS/DEFAULT/IN/" +
                                  "TRIGGER_BUTTON\",\"type\":\"vector1\"}],\"action_sets\":[]}");
        Equal(trigger, Action("/actions/default/in/Trigger_Button"), "old handle retained");
        Activate(new VRActiveActionSet_t { ulActionSet = set });
        QueryAll();
        Manifest(
            "partial",
            "{\"actions\":[{\"name\":\"/actions/later/in/Trigger_Button\",\"type\":\"boolean\"}]}");
        ulong laterAction = Action("/actions/later/in/Trigger_Button");
        Digital(laterAction, left);
        Manifest("later", "{\"actions\":[],\"action_sets\":[{\"name\":\"/actions/later\"}]}");
        ulong laterSet = Set("/actions/later");
        Activate(new VRActiveActionSet_t { ulActionSet = laterSet });
        Need(Digital(laterAction, left).bState, "late set resolves previous partial registration");
        Manifest("malformed",
                 "{\"actions\":[{\"name\":\"/short\",\"type\":\"boolean\"}],\"action_sets\":[]}");
        ulong malformed = Action("/short");
        Type ea = null, eb = null;
        try {
            var d = new InputDigitalActionData_t();
            Old.GetDigitalActionData(malformed, ref d, Size<InputDigitalActionData_t>(), left);
        } catch (Exception e) {
            ea = e.GetType();
        }
        try {
            var d = new InputDigitalActionData_t();
            New.GetDigitalActionData(malformed, ref d, Size<InputDigitalActionData_t>(), left);
        } catch (Exception e) {
            eb = e.GetType();
        }
        Need(ea == typeof(ArgumentOutOfRangeException) && ea == eb,
             "malformed query exception preserved");
        Equal(Old.SetActionManifestPath(Path.Combine(directory, "absent")),
              New.SetActionManifestPath(Path.Combine(directory, "absent")), "missing manifest");
        QueryAll();
        Case("manifest reload, immutable old types/handles, partial failure, later sets, " +
             "malformed query exceptions");
        var random = new Random(412893);
        for (int i = 0; i < 300; i++) {
            var old = Sample(Device(2, (uint)random.Next(32) & ~NativeTracking.PoseValid,
                                    (uint)random.Next(256)),
                             Device(3, (uint)random.Next(32) & ~NativeTracking.PoseValid,
                                    (uint)random.Next(256)));
            var now = Sample(Device(2, (uint)random.Next(32) & ~NativeTracking.PoseValid,
                                    (uint)random.Next(256)),
                             Device(3, (uint)random.Next(32) & ~NativeTracking.PoseValid,
                                    (uint)random.Next(256)));
            Fixture(old, now);
            Activate(new VRActiveActionSet_t { ulActionSet = set,
                                               ulRestrictedToDevice = i % 3 == 0   ? left
                                                                      : i % 3 == 1 ? right
                                                                                   : 0 });
            // Random flags deliberately omit PoseValid: pose transform is a Unity native API,
            // outside this pure host test.
            QueryAll();
        }
        Case("300 deterministic device/button/restriction transitions");
    }
    static void LaunchMode() {
        string original = Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR");
        try {
            Environment.SetEnvironmentVariable("NATIVEH3VR_OPENXR", "live");
            Need(NativeSteamVRBootstrap.Enabled &&
                     OriginalNativeH3VR.NativeSteamVRBootstrap.Enabled,
                 "live launch enabled");
            Environment.SetEnvironmentVariable("NATIVEH3VR_OPENXR", "disabled");
#if NEWENGINE_UNITY_2021
            Need(NativeSteamVRBootstrap.Enabled, "session snapshot remains enabled");
            var cache = typeof(NativeSteamVRBootstrap)
                            .GetNestedType("LaunchConfiguration", BindingFlags.NonPublic);
            var refresh = cache.GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Static);
            Equal("disabled", (string)refresh.Invoke(null, null), "session refresh disabled");
            Need(!NativeSteamVRBootstrap.Enabled, "new disabled session");
            foreach (string mode in new[] { "synthetic", "live", null, "LIVE", "" }) {
                Environment.SetEnvironmentVariable("NATIVEH3VR_OPENXR", mode);
                refresh.Invoke(null, null);
                Equal(OriginalNativeH3VR.NativeSteamVRBootstrap.Enabled,
                      NativeSteamVRBootstrap.Enabled, "refreshed mode " + mode);
            }
#else
            Need(!NativeSteamVRBootstrap.Enabled,
                 "legacy branch retains dynamic environment behavior");
#endif
            Case("launch mode snapshot and explicit session refresh / legacy dynamic mode");
        } finally {
            Environment.SetEnvironmentVariable("NATIVEH3VR_OPENXR", original);
        }
    }
    static long Allocated(Action action, int count) {
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
            action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    static void AllocationChecks() {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var retained = new byte[65536];
        retained[0] = 1;
        retained[65535] = 2;
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(retained);
        Need(delta >= 65536, "allocation counter retained positive calibration");
        allocations.Add(new { test = "counter calibration",
                              runtime = RuntimeInformation.FrameworkDescription,
                              retainedPayloadBytes = 65536, observedBytes = delta });
        const uint flags = NativeTracking.Connected | NativeTracking.InputCurrent;
        Fixture(default(NativeTracking.Snapshot), Sample(Device(2, flags, 1)));
        Activate(new VRActiveActionSet_t { ulActionSet = set });
        uint size = Size<InputDigitalActionData_t>();
        Action old = () => {
            var data = new InputDigitalActionData_t();
            sink += (int)Old.GetDigitalActionData(trigger, ref data, size, left);
        };
        Action changed = () => {
            var data = new InputDigitalActionData_t();
            sink += (int)New.GetDigitalActionData(trigger, ref data, size, left);
        };
        for (int i = 0; i < 10000; i++) {
            old();
            changed();
        }
        for (int batch = 0; batch < 4; batch++) {
            long a, b;
            if (batch % 2 == 0) {
                a = Allocated(old, 50000);
                b = Allocated(changed, 50000);
            } else {
                b = Allocated(changed, 50000);
                a = Allocated(old, 50000);
            }
#if NEWENGINE_UNITY_2021
            Need(a > 0 && b == 0, "warm candidate query removes lookup allocations");
#else
            Equal(a, b, "legacy branch allocation parity");
#endif
            allocations.Add(new { test = "warm digital query", batch, queries = 50000,
                                  originalBytes = a, candidateBytes = b });
        }
        string mode = Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR");
        try {
            Environment.SetEnvironmentVariable("NATIVEH3VR_OPENXR", "live");
#if NEWENGINE_UNITY_2021
            typeof(NativeSteamVRBootstrap)
                .GetNestedType("LaunchConfiguration", BindingFlags.NonPublic)
                .GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
#endif
            Action oldGuard = () => {
                if (OriginalNativeH3VR.NativeSteamVRBootstrap.Enabled)
                    sink++;
            };
            Action newGuard = () => {
                if (NativeSteamVRBootstrap.Enabled)
                    sink++;
            };
            for (int i = 0; i < 10000; i++) {
                oldGuard();
                newGuard();
            }
            long a = Allocated(oldGuard, 50000), b = Allocated(newGuard, 50000);
#if NEWENGINE_UNITY_2021
            Need(a > 0 && b == 0, "warm launch guard removes environment strings");
#else
            Equal(a, b, "legacy guard allocation parity");
#endif
            allocations.Add(new { test = "warm Enabled guard", queries = 50000, originalBytes = a,
                                  candidateBytes = b });
            Action oldGuarded = () => {
                if (OriginalNativeH3VR.NativeSteamVRBootstrap.Enabled &&
                    OriginalNativeH3VR.NativeSteamVRBootstrap.Enabled)
                    old();
            };
            Action newGuarded = () => {
                if (NativeSteamVRBootstrap.Enabled && NativeSteamVRBootstrap.Enabled)
                    changed();
            };
            for (int i = 0; i < 10000; i++) {
                oldGuarded();
                newGuarded();
            }
            a = Allocated(oldGuarded, 50000);
            b = Allocated(newGuarded, 50000);
#if NEWENGINE_UNITY_2021
            Need(a > 0 && b == 0, "two guards and warm query remove lookup allocations");
#else
            Equal(a, b, "legacy guarded query allocation parity");
#endif
            allocations.Add(new { test = "two Enabled guards plus warm digital query",
                                  queries = 50000, originalBytes = a, candidateBytes = b });
        } finally {
            Environment.SetEnvironmentVariable("NATIVEH3VR_OPENXR", mode);
        }
        GC.KeepAlive(sink);
    }
    static int Main(string[] args) {
        directory =
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "fixture-manifests");
        Directory.CreateDirectory(directory);
        string error = null;
        try {
            Behavior();
            LaunchMode();
            AllocationChecks();
        } catch (Exception e) {
            error = e.ToString();
        }
        File.WriteAllText(
            args[0],
            JsonSerializer.Serialize(
                new { passed = error == null, assertions, cases, allocations, error,
                      limits = "Pure .NET host uses actual game structs, Valve JSON and Unity " +
                               "managed modules; no Unity/player/native API execution, " +
                               "polling-rate or frame-time improvement claim. Valid-pose native " +
                               "transform and haptic hardware remain outside scope." },
                new JsonSerializerOptions { WriteIndented = true }));
        if (error != null) {
            Console.Error.WriteLine(error);
            return 1;
        }
        Console.WriteLine("Input lookup checks pass: " + assertions);
        return 0;
    }
}
