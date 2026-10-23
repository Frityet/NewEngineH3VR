using System.Runtime.InteropServices;
using System.Text.Json;
using FMOD;
NativeLibrary.SetDllImportResolver(
    typeof(FMOD.Factory).Assembly,
    (name, assembly,
     path) => name == "NativeH3VRFMOD"
                  ? NativeLibrary.Load(Path.GetFullPath("build/fmod/libNativeH3VRFMOD.dylib"))
                  : IntPtr.Zero);
var layout = File.ReadAllLines("build/fmod/abi-sizes.txt")
                 .Select(line => line.Split('='))
                 .Select(parts => new { type = parts[0], nativeSize = int.Parse(parts[1]),
                                        legacySize = Marshal.SizeOf(
                                            typeof(FMOD.Factory).Assembly.GetType(parts[0])) })
                 .ToArray();
File.WriteAllText("build/fmod/abi-layout.json",
                  JsonSerializer.Serialize(layout,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(layout.Where(x => x.nativeSize != x.legacySize)));
var library = NativeLibrary.Load(Path.GetFullPath("build/fmod/libNativeH3VRFMOD.dylib"));
var imports = JsonDocument.Parse(File.ReadAllText("audit-managed/results/pinvokes.json"))
                  .RootElement.EnumerateArray()
                  .Where(e => e.GetProperty("library").GetString() == "fmodstudio")
                  .Select(e => e.GetProperty("entryPoint").GetString())
                  .Distinct()
                  .ToArray();
var missing = imports.Where(name => !NativeLibrary.TryGetExport(library, name, out _)).ToArray();
File.WriteAllText("build/fmod/exports.json",
                  JsonSerializer.Serialize(new { imports = imports.Length, missing },
                                           new JsonSerializerOptions { WriteIndented = true }));
if (missing.Length > 0)
    throw new Exception("Missing FMOD exports: " + string.Join(", ", missing));
void Check(FMOD.RESULT result, string operation) {
    Console.WriteLine(operation + "=" + result);
    if (result != FMOD.RESULT.OK)
        throw new Exception(operation + ": " + result);
}
Check(FMOD.Studio.System.create(out var studio), "legacy.create");
try {
    Check(studio.getLowLevelSystem(out var core), "getLowLevelSystem");
    Check(core.getVersion(out var version), "getVersion");
    Console.WriteLine($"native_version=0x{version:x}");
    Check(core.setOutput(FMOD.OUTPUTTYPE.NOSOUND_NRT), "setOutput");
    var advanced = new FMOD.ADVANCEDSETTINGS { randomSeed = 1234 };
    Check(core.setAdvancedSettings(ref advanced), "setAdvancedSettings");
    Check(studio.initialize(128, FMOD.Studio.INITFLAGS.SYNCHRONOUS_UPDATE, FMOD.INITFLAGS.NORMAL,
                            IntPtr.Zero),
          "initialize");
    var folder = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/StreamingAssets");
    int eventCount = 0;
    foreach (var filename in Directory.GetFiles(folder, "*.bank")
                 .OrderBy(p => !p.Contains("Master Bank"))) {
        Check(studio.loadBankFile(filename, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank),
              "loadBank " + Path.GetFileName(filename));
        Check(bank.getEventList(out var events), "getEventList");
        eventCount += events.Length;
    }
    Check(studio.getEvent("event:/MX/TAH2/mx_TAH2_Take", out var desc), "getEvent");
    Check(desc.loadSampleData(), "loadSampleData");
    Check(studio.flushSampleLoading(), "flushSampleLoading");
    Check(desc.getParameterCount(out int parameterCount), "getParameterCount");
    Check(desc.createInstance(out var instance), "createInstance");
    for (int i = 0; i < parameterCount; i++) {
        Check(desc.getParameterByIndex(i, out var pd), "getParameterByIndex");
        string name = pd.name;
        Console.WriteLine(
            $"parameter={name}, min={pd.minimum}, max={pd.maximum}, default={pd.defaultvalue}");
        Check(instance.getParameter(name, out var parameter), "getParameter");
        Check(parameter.getDescription(out var pd2), "parameter.getDescription");
        Check(parameter.setValue(pd.defaultvalue), "parameter.setValue");
        Check(parameter.getValue(out var value), "parameter.getValue");
    }
    Check(core.getMasterChannelGroup(out var master), "getMasterChannelGroup");
    Check(master.getDSP(0, out var dsp), "getDSP");
    Check(dsp.setMeteringEnabled(false, true), "setMeteringEnabled");
    Check(instance.start(), "start");
    float peak = 0, rms = 0;
    for (int i = 0; i < 500; i++) {
        CheckSilent(studio.update());
        dsp.getMeteringInfo(IntPtr.Zero, out var meter);
        if (meter.peaklevel != null)
            peak = Math.Max(peak, meter.peaklevel.Max());
        if (meter.rmslevel != null)
            rms = Math.Max(rms, meter.rmslevel.Max());
    }
    Check(instance.getPlaybackState(out var state), "getPlaybackState");
    Check(instance.getTimelinePosition(out var timeline), "getTimelinePosition");
    Check(studio.getCPUUsage(out var cpu), "getCPUUsage");
    Check(instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE), "stop");
    Check(instance.release(), "event.release");
    var report = new { architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                       nativeVersion = version,
                       banksLoaded = 7,
                       eventCount,
                       parameterCount,
                       playbackState = state.ToString(),
                       timelineMs = timeline,
                       peak,
                       rms };
    File.WriteAllText(
        "build/fmod/legacy-probe.json",
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(report));
    if (peak <= 0 || rms <= 0 || timeline <= 0)
        throw new Exception("Native mixer produced no measured music output.");
} finally {
    Check(studio.release(), "release");
}
void CheckSilent(FMOD.RESULT result) {
    if (result != FMOD.RESULT.OK)
        throw new Exception(result.ToString());
}
