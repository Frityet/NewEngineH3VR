using System.Runtime.InteropServices;
using System.Text.Json;
using FMOD;
NativeLibrary.SetDllImportResolver(
    typeof(FMOD.Factory).Assembly,
    (name, assembly,
     path) => name == "fmod" || name == "fmodstudio"
                  ? NativeLibrary.Load(Path.GetFullPath("build/fmod/lib" + name + ".dylib"))
                  : IntPtr.Zero);
void Check(FMOD.RESULT result, string operation) {
    Console.WriteLine(operation + "=" + result);
    if (result != FMOD.RESULT.OK)
        throw new Exception(operation + ": " + result);
}
Check(FMOD.Studio.System.create(out var studio), "create");
try {
    Check(studio.getCoreSystem(out var core), "getCoreSystem");
    Check(core.getVersion(out var version), "getVersion");
    Console.WriteLine($"native_version=0x{version:x}");
    // Offline waveform output measures the actual mixer without changing speakers or user volume.
    Check(core.setOutput(FMOD.OUTPUTTYPE.NOSOUND_NRT), "setOutput");
    Check(studio.initialize(128, FMOD.Studio.INITFLAGS.SYNCHRONOUS_UPDATE, FMOD.INITFLAGS.NORMAL,
                            IntPtr.Zero),
          "initialize");
    var folder = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/StreamingAssets");
    var reports = new List<object>();
    foreach (var filename in Directory.GetFiles(folder, "*.bank")
                 .OrderBy(p => !p.Contains("Master Bank"))) {
        Check(studio.loadBankFile(filename, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank),
              "loadBank " + Path.GetFileName(filename));
        Check(bank.getEventList(out var events), "getEventList");
        var names = new List<string>();
        foreach (var description in events) {
            description.getPath(out var eventPath);
            names.Add(eventPath);
        }
        reports.Add(new { bank = Path.GetFileName(filename), events = names });
        Console.WriteLine($"events={events.Length}");
    }
    File.WriteAllText("build/fmod/banks.json",
                      JsonSerializer.Serialize(
                          new { architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                                nativeVersion = version, banks = reports },
                          new JsonSerializerOptions { WriteIndented = true }));
} finally {
    Check(studio.release(), "release");
}
