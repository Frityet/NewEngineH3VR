using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NativeH3VR;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// Explicit synthetic measurement only. CPU optimizations belong to OptiH3VR;
// this class observes frame cadence without changing game or renderer settings.
internal static class NativePerformanceProbe {
    [Serializable]
    sealed class Configuration {
        public string runId, description;
        public int pid;
        public bool available, enabled;
    }
    [Serializable]
    sealed class Settings {
        public string unityVersion, graphicsDevice, quality, colorSpace;
        public int width, height, targetFrameRate, vSyncCount, antiAliasing, pixelLightCount;
        public uint eyeWidth, eyeHeight;
        public float fixedDeltaTime, shadowDistance, lodBias;
        public bool texturePointerCache;
    }
    [Serializable]
    sealed class Window {
        public string schema = "nativeh3vr-perf-window-1", runId, scene, settingsFingerprint;
        public string phase = "steady",
                      source = "Stopwatch intervals between Unity coroutine frames";
        public string workload = "fixed synthetic head, original scene entry, no scripted combat";
        public string semantics =
            "Post-camera-handoff warmup excluded. Intervals include rendering/runtime waits and " +
            "all main-loop work; not CPU time or physical scanout. No method timing hooks. " +
            "Original scene simulation and random state are not frozen.";
        public int pid, frames, startFrame, endFrame, gc0, gc1, gc2, texturePointerQueries;
        public double warmupSeconds, durationSeconds, requestedDurationSeconds;
        public ulong stereoFrames;
        public double[] frameMs;
        public Settings settings;
    }
    static bool configurationLogged;

    internal static void RunOptionalModChecks() {
        string path = Environment.GetEnvironmentVariable("OPTIH3VR_NEW_ENGINE_TEST_ASSEMBLY");
        if (string.IsNullOrEmpty(path))
            return;
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") != "synthetic")
            throw new InvalidOperationException("Mod fixtures require isolated synthetic mode");
        string output = Environment.GetEnvironmentVariable("OPTIH3VR_NEW_ENGINE_TEST_OUTPUT");
        if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(path) || !Path.IsPathRooted(output))
            throw new ArgumentException("Mod fixtures require absolute assembly and output paths");
        Directory.CreateDirectory(output);
        var assembly = Assembly.LoadFrom(path);
        string[] checks =
            Environment.GetEnvironmentVariable("OPTIH3VR_NEW_ENGINE_COVER_TEST") == "1"
                ? new[] { "CoreRuntimeChecks", "FixedMaxRuntimeChecks", "CoverRuntimeChecks" }
                : new[] { "CoreRuntimeChecks", "FixedMaxRuntimeChecks" };
        foreach (string name in checks) {
            var type = assembly.GetType("OptiH3VR.Tests." + name, true);
            var report = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                             .Invoke(null, new object[] { Path.Combine(output, name + ".json") });
            if (!(bool)report.GetType().GetField("passed").GetValue(report))
                throw new InvalidOperationException("OptiH3VR fixture did not pass: " + name +
                                                    " report=" + JsonUtility.ToJson(report));
            Debug.Log("NATIVE_OPTIH3VR_TEST_PASS " + name +
                      " path=" + Path.Combine(output, name + ".json"));
        }
    }

    internal static void LogConfiguration() {
        if (configurationLogged)
            return;
        configurationLogged = true;
        var config =
            new Configuration { runId = Environment.GetEnvironmentVariable("NATIVEH3VR_RUN_ID"),
                                pid = Process.GetCurrentProcess().Id,
                                description = "OptiH3VR runtime not installed" };
        // Optional assembly lookup keeps the port's harness usable on its
        // pristine baseline. No CPU patch logic is duplicated here.
        Type runtime = Type.GetType("OptiH3VR.NewEngine, OptiH3VR.NewEngine", false);
        if (runtime != null) {
            config.available = true;
            var property =
                runtime.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Static);
            var field = runtime.GetField("Enabled", BindingFlags.Public | BindingFlags.Static);
            if (property != null)
                config.enabled = (bool)property.GetValue(null, null);
            else if (field != null)
                config.enabled = (bool)field.GetValue(null);
            else
                throw new InvalidOperationException("OptiH3VR runtime has no enabled state");
            config.description =
                (string)runtime.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, null);
        }
        Debug.Log("NATIVEH3VR_OPTI_CONFIG " + JsonUtility.ToJson(config));
    }

    internal static IEnumerator Measure(NativeStereoRenderer renderer, string scene) {
        string requested = Environment.GetEnvironmentVariable("NATIVEH3VR_PERF_CAPTURE_SECONDS");
        if (string.IsNullOrEmpty(requested) ||
            Environment.GetEnvironmentVariable("NATIVEH3VR_PERF_ENABLED") == "0")
            yield break;
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") != "synthetic")
            throw new InvalidOperationException(
                "Frame fixture requires isolated synthetic tracking");
        double capture = ReadSeconds(requested, 1, 120);
        double warmup = ReadSeconds(
            Environment.GetEnvironmentVariable("NATIVEH3VR_PERF_WARMUP_SECONDS") ?? "2", 0, 120);
        double frequency = Stopwatch.Frequency;
        long warmStart = Stopwatch.GetTimestamp();
        while ((Stopwatch.GetTimestamp() - warmStart) / frequency < warmup) {
            CheckScene(renderer, scene);
            yield return null;
        }
        // Allocate outside the measured interval and copy/serialize only after it.
        double[] samples = new double[120000];
        var settings = ReadSettings(renderer);
        string fingerprint = Fingerprint(settings);
        var result = new Window { runId = Environment.GetEnvironmentVariable("NATIVEH3VR_RUN_ID"),
                                  pid = Process.GetCurrentProcess().Id,
                                  scene = scene,
                                  warmupSeconds = warmup,
                                  requestedDurationSeconds = capture,
                                  settings = settings,
                                  settingsFingerprint = fingerprint,
                                  startFrame = Time.frameCount };
        ulong stereoStart = renderer.completedFrames;
        int pointerStart = NativeEyeTextureDiagnostics.PointerQueries;
        int gc0 = GC.CollectionCount(0), gc1 = Collections(1), gc2 = Collections(2), count = 0;
        long start = Stopwatch.GetTimestamp(), previous = start, now = start;
        while ((now - start) / frequency < capture) {
            yield return null;
            now = Stopwatch.GetTimestamp();
            CheckScene(renderer, scene);
            if (count == samples.Length)
                throw new InvalidOperationException("Frame sample capacity exceeded");
            samples[count++] = (now - previous) * 1000.0 / frequency;
            previous = now;
        }
        result.durationSeconds = (now - start) / frequency;
        result.endFrame = Time.frameCount;
        result.frames = count;
        result.stereoFrames = renderer.completedFrames - stereoStart;
        result.texturePointerQueries = NativeEyeTextureDiagnostics.PointerQueries - pointerStart;
        result.gc0 = GC.CollectionCount(0) - gc0;
        result.gc1 = gc1 < 0 ? -1 : Collections(1) - gc1;
        result.gc2 = gc2 < 0 ? -1 : Collections(2) - gc2;
        result.frameMs = new double[count];
        Array.Copy(samples, result.frameMs, count);
        if (count < 2 || result.stereoFrames < 2)
            throw new InvalidOperationException("Insufficient rendered frames for cadence window");
        if (Fingerprint(ReadSettings(renderer)) != fingerprint)
            throw new InvalidOperationException("Graphics settings changed during frame window");
        Debug.Log("NATIVEH3VR_PERF_WINDOW " + JsonUtility.ToJson(result));
    }
    static Settings ReadSettings(NativeStereoRenderer renderer) {
        return new Settings { unityVersion = Application.unityVersion,
                              graphicsDevice = SystemInfo.graphicsDeviceName,
                              quality = QualitySettings.names[QualitySettings.GetQualityLevel()],
                              colorSpace = QualitySettings.activeColorSpace.ToString(),
                              width = Screen.width,
                              height = Screen.height,
                              targetFrameRate = Application.targetFrameRate,
                              vSyncCount = QualitySettings.vSyncCount,
                              antiAliasing = QualitySettings.antiAliasing,
                              pixelLightCount = QualitySettings.pixelLightCount,
                              eyeWidth = renderer.latestFrame.width,
                              eyeHeight = renderer.latestFrame.height,
                              fixedDeltaTime = Time.fixedDeltaTime,
                              shadowDistance = QualitySettings.shadowDistance,
                              lodBias = QualitySettings.lodBias,
                              texturePointerCache = NativeEyeTextureDiagnostics.CacheEnabled };
    }
    static string Fingerprint(Settings settings) {
        using (var hash = SHA256.Create()) return BitConverter
            .ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(settings))))
            .Replace("-", "")
            .ToLowerInvariant();
    }
    static double ReadSeconds(string text, double minimum, double maximum) {
        double value;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
            throw new ArgumentException("Invalid performance duration: " + text);
        return value;
    }
    static int Collections(int generation) {
        return generation > GC.MaxGeneration ? -1 : GC.CollectionCount(generation);
    }
    static void CheckScene(NativeStereoRenderer renderer, string scene) {
        if (renderer == null || renderer.sourceCamera == null || renderer.suspendSceneRendering ||
            renderer.sourceCamera.gameObject.scene.name != scene ||
            SceneManager.GetActiveScene().name != scene || SteamVR_LoadLevel.loading)
            throw new InvalidOperationException("Scene/camera changed during frame measurement");
    }
}
