using System.Runtime.InteropServices;
using System.Text.Json;
using FMOD;
NativeLibrary.SetDllImportResolver(
    typeof(FMOD.Factory).Assembly,
    (name, assembly, path) => name == "NativeH3VRFMOD"
                                  ? NativeLibrary.Load(Path.GetFullPath(
                                        "build/fmod/parity/native/libNativeH3VRFMOD.dylib"))
                                  : IntPtr.Zero);
void Check(RESULT r, string label) {
    if (r != RESULT.OK)
        throw new Exception(label + ": " + r);
}
Check(FMOD.Studio.System.create(out var studio), "create");
var reports = new List<object>();
try {
    Check(studio.getLowLevelSystem(out var core), "core");
    Check(core.setOutput(OUTPUTTYPE.NOSOUND_NRT), "silent offline output");
    var advanced = new ADVANCEDSETTINGS { randomSeed = 1234 };
    Check(core.setAdvancedSettings(ref advanced), "advanced");
    Check(studio.initialize(128, FMOD.Studio.INITFLAGS.SYNCHRONOUS_UPDATE, INITFLAGS.NORMAL,
                            IntPtr.Zero),
          "initialize");
    var listener =
        new ATTRIBUTES_3D { position = new VECTOR { x = 1, y = 2, z = 3 }, velocity = new VECTOR(),
                            forward = new VECTOR { z = 1 }, up = new VECTOR { y = 1 } };
    Check(studio.setListenerAttributes(0, listener), "set listener");
    Check(studio.getListenerAttributes(0, out var listenerRead), "get listener");
    if (listenerRead.position.x != 1 || listenerRead.position.y != 2 ||
        listenerRead.position.z != 3)
        throw new Exception("Listener ABI roundtrip mismatch");
    var folder = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/StreamingAssets");
    var descriptions = new List<FMOD.Studio.EventDescription>();
    int banks = 0;
    foreach (var path in Directory.GetFiles(folder, "*.bank")
                 .OrderBy(p => !p.Contains("Master Bank"))) {
        Check(studio.loadBankFile(path, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank), "bank");
        Check(bank.getEventList(out var events), "events");
        descriptions.AddRange(events);
        banks++;
    }
    Check(studio.getBus("bus:/Music", out var bus), "musicbus");
    Check(bus.setVolume(0.4f), "bus volume");
    Check(bus.getVolume(out var volume, out var finalVolume), "read bus volume");
    if (Math.Abs(volume - 0.4f) > 1e-6)
        throw new Exception("Bus volume mismatch");
    Check(bus.setVolume(1), "restore bus volume");
    Check(core.getMasterChannelGroup(out var master), "master");
    Check(master.getDSP(0, out var dsp), "master DSP");
    Check(dsp.setMeteringEnabled(false, true), "meter");
    foreach (var desc in descriptions) {
        Check(desc.getPath(out var path), "path");
        Check(desc.is3D(out var spatial), "is3D");
        Check(desc.getLength(out var length), "event length");
        Check(desc.isOneshot(out var oneshot), "oneshot");
        Check(desc.loadSampleData(), "sampledata");
        Check(studio.flushSampleLoading(), "samplesready");
        Check(desc.getParameterCount(out var count), "parametercount");
        Check(desc.createInstance(out var instance), "instance");
        Check(instance.set3DAttributes(listener), "event attributes");
        Check(instance.get3DAttributes(out var attributes), "event attributes read");
        if (attributes.position.x != 1 || attributes.position.y != 2 || attributes.position.z != 3)
            throw new Exception("Event attributes mismatch");
        var parameters = new List<object>();
        for (int i = 0; i < count; i++) {
            Check(desc.getParameterByIndex(i, out var definition), "parameterdefinition");
            string name = definition.name;
            Check(instance.getParameter(name, out var parameter), "legacy parameter");
            Check(parameter.getDescription(out var definitionRead), "parameter description");
            Check(parameter.setValue(definition.defaultvalue), "parameter default");
            Check(parameter.getValue(out var read), "parameter read");
            if (Math.Abs(read - definition.defaultvalue) > 0.001)
                throw new Exception("Legacy parameter value mismatch");
            float value = name.IndexOf("intensity", StringComparison.OrdinalIgnoreCase) >= 0
                              ? definition.maximum
                              : definition.defaultvalue;
            if (name == "Stop")
                value = definition.minimum;
            Check(instance.setParameterValue(name, value), "game name setter");
            Check(instance.getParameterValue(name, out var named, out var evaluated),
                  "game name getter");
            if (Math.Abs(named - value) > 0.001)
                throw new Exception("Named parameter value mismatch");
            parameters.Add(new { name, index = i, minimum = definition.minimum,
                                 maximum = definition.maximum,
                                 defaultValue = definition.defaultvalue, value });
        }
        Check(instance.start(), "start");
        float peak = 0, rms = 0;
        int maxTimeline = 0;
        for (int i = 0; i < 1000; i++) {
            Check(studio.update(), "update");
            Check(dsp.getMeteringInfo(IntPtr.Zero, out var meter), "meter read");
            if (meter.peaklevel != null)
                peak = Math.Max(peak, meter.peaklevel.Max());
            if (meter.rmslevel != null)
                rms = Math.Max(rms, meter.rmslevel.Max());
            Check(instance.getTimelinePosition(out var timeline), "timeline");
            maxTimeline = Math.Max(maxTimeline, timeline);
        }
        Check(instance.getPlaybackState(out var state), "playback state");
        Check(instance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT), "fadeout");
        for (int i = 0; i < 100; i++)
            Check(studio.update(), "fade update");
        Check(instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE), "stop");
        Check(instance.release(), "release event");
        reports.Add(new { path, spatial, length, oneshot, parameters, peak, rms, maxTimeline,
                          state = state.ToString() });
        Console.WriteLine(path + " peak=" + peak + " timeline=" + maxTimeline);
    }
    Check(bus.stopAllEvents(FMOD.Studio.STOP_MODE.IMMEDIATE), "game bus shutdown");
    File.WriteAllText("build/fmod/parity/events.json",
                      JsonSerializer.Serialize(
                          new { architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                                banks, events = reports, listenerAttributesRoundtrip = true },
                          new JsonSerializerOptions { WriteIndented = true }));
} finally {
    Check(studio.release(), "release system");
}
