using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using FMODUnity;
public sealed class AudioStartupProbe : MonoBehaviour {
    [Serializable]
    public class Report {
        public string unity, output, error;
        public int banks, events, outputValue, rate, realChannels, virtualChannels;
        public bool initialized, settingsLoaded;
        public List<string> bankPaths = new List<string>();
    }
    void Start() {
        var r = new Report { unity = Application.unityVersion };
        try {
            var settings = Resources.Load<Settings>("FMODStudioSettings");
            if (settings == null)
                throw new Exception("Recovered FMOD settings missing");
            r.settingsLoaded = true;
            r.virtualChannels = settings.GetVirtualChannels(FMODPlatform.Mac);
            // Exercise the original game's RuntimeManager.Initialize including real AUTODETECT,
            // recovered settings, original GetBankPath and automatic event/sample loading.
            var studio = RuntimeManager.StudioSystem;
            var core = RuntimeManager.LowlevelSystem;
            r.initialized = studio.isValid();
            Check(core.getOutput(out var output), "output");
            r.output = output.ToString();
            r.outputValue = (int)output;
            if (output != FMOD.OUTPUTTYPE.COREAUDIO)
                throw new Exception("Expected CoreAudio, actual " + output);
            Check(core.getSoftwareFormat(out r.rate, out var speaker, out var raw), "format");
            Check(core.getSoftwareChannels(out r.realChannels), "channels");
            Check(studio.getBankList(out var banks), "banks");
            r.banks = banks.Length;
            foreach (var bank in banks) {
                Check(bank.getPath(out var path), "bank path");
                r.bankPaths.Add(path);
                Check(bank.getEventCount(out var count), "event count");
                r.events += count;
            }
            if (r.banks != 7 || r.events != 24)
                throw new Exception("Original automatic bank loading incomplete");
            foreach (var name in settings.Banks)
                if (!File.Exists(Path.Combine(Application.streamingAssetsPath, name + ".bank")))
                    throw new Exception("Missing bank " + name);
        } catch (Exception e) {
            r.error = e.ToString();
        }
        var text = JsonUtility.ToJson(r, true);
        File.WriteAllText(Environment.GetEnvironmentVariable("NATIVEH3VR_AUDIO_RESULT"), text);
        Debug.Log("AUDIO_STARTUP_PROBE " + text);
        Application.Quit(string.IsNullOrEmpty(r.error) ? 0 : 1);
    }
    static void Check(FMOD.RESULT status, string label) {
        if (status != FMOD.RESULT.OK)
            throw new Exception(label + ": " + status);
    }
}
