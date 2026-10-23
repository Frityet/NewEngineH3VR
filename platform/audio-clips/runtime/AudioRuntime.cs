using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class AudioRuntime : MonoBehaviour {
    [RuntimeInitializeOnLoadMethod]
    static void Boot() { new GameObject("AudioRuntime").AddComponent<AudioRuntime>(); }
    [Serializable]
    public class Row {
        public string name, error, loadType, state;
        public int samples, channels, frequency, timeSamples;
        public bool loaded, read, isPlaying;
        public double peak, rms;
    }
    [Serializable]
    public class Report {
        public string unity;
        public List<Row> clips = new List<Row>();
        public int errors;
    }
    IEnumerator Start() {
        var report = new Report { unity = Application.unityVersion };
        gameObject.AddComponent<AudioListener>();
        var source = gameObject.AddComponent<AudioSource>();
        source.volume = 0;
        source.loop = true;
        for (int i = 0; i < 100; i++) {
            var c = Resources.Load<AudioClip>("case" + i);
            if (!c)
                break;
            var row =
                new Row { name = c.name, loadType = c.loadType.ToString(), samples = c.samples,
                          channels = c.channels, frequency = c.frequency };
            report.clips.Add(row);
            row.loaded = c.LoadAudioData();
            var deadline = Time.realtimeSinceStartup + 5;
            while (c.loadState == AudioDataLoadState.Loading &&
                   Time.realtimeSinceStartup < deadline)
                yield return null;
            row.state = c.loadState.ToString();
            if (c.loadType == AudioClipLoadType.DecompressOnLoad) {
                var d = new float[c.samples * c.channels];
                row.read = c.GetData(d, 0);
                double sum = 0;
                foreach (var v in d) {
                    row.peak = Math.Max(row.peak, Math.Abs(v));
                    sum += v * v;
                }
                row.rms = Math.Sqrt(sum / d.Length);
            }
            source.clip = c;
            source.Play();
            yield return new WaitForSeconds(0.15f);
            row.isPlaying = source.isPlaying;
            row.timeSamples = source.timeSamples;
            if (!row.isPlaying || row.timeSamples == 0 ||
                c.loadState != AudioDataLoadState.Loaded) {
                row.error = "Playback did not advance";
                report.errors++;
            }
            source.Stop();
            c.UnloadAudioData();
        }
        if (report.clips.Count == 0)
            report.errors++;
        File.WriteAllText(Environment.GetEnvironmentVariable("NATIVEH3VR_AUDIO_RESULT"),
                          JsonUtility.ToJson(report, true));
        Application.Quit(report.errors == 0 ? 0 : 1);
    }
}
