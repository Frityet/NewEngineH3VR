using UnityEditor;
using UnityEngine;
using System;
using System.IO;
public static class AudioClipProof {
    public static void Build() {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        var r = BuildPipeline.BuildPlayer(new[] { "Assets/Probe.unity" },
                                          "../build/audio-clips/AudioClipProbe.app",
                                          BuildTarget.StandaloneOSX, BuildOptions.Development);
        EditorApplication.Exit(
            r.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
    public static void Inspect() {
        var c = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Resources/Original.asset");
        Debug.Log("AUDIO_CLIP " + c + " channels=" + c.channels + " frequency=" + c.frequency +
                  " samples=" + c.samples + " length=" + c.length + " state=" + c.loadState);
        bool loaded = c.LoadAudioData();
        var d = new float[c.samples * c.channels];
        bool read = c.GetData(d, 0);
        double peak = 0, sum = 0;
        foreach (var v in d) {
            peak = Math.Max(peak, Math.Abs(v));
            sum += v * v;
        }
        File.WriteAllText("../build/audio-clips/editor-result.json",
                          "{\"loaded\":" + loaded.ToString().ToLower() +
                              ",\"read\":" + read.ToString().ToLower() + ",\"peak\":" + peak +
                              ",\"rms\":" + Math.Sqrt(sum / d.Length) +
                              ",\"samples\":" + c.samples + ",\"channels\":" + c.channels +
                              ",\"frequency\":" + c.frequency + "}");
        EditorApplication.Exit(peak > 0 ? 0 : 1);
    }
    public static void MakeTemplate() {
        EditorSettings.serializationMode = SerializationMode.ForceBinary;
        var clip = AudioClip.Create("NativeAudioTemplate", 128, 1, 44100, false);
        var samples = new float[128];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(i * 0.2) * 0.2f;
        clip.SetData(samples, 0);
        AssetDatabase.CreateAsset(clip, "Assets/AudioClip.asset");
        AssetDatabase.SaveAssets();
        File.Copy("Assets/AudioClip.asset", "../build/audio-clips/AudioClip.asset", true);
        Debug.Log("AUDIO_TEMPLATE_SAVED " + new FileInfo("Assets/AudioClip.asset").Length);
        EditorApplication.Exit(0);
    }
}
