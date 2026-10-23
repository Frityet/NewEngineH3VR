#if NEWENGINE_UNITY_2021
// Engine diagnostics only. CPU optimization algorithms remain in OptiH3VR.
// Caller explicitly invokes Install(); merely compiling this file installs nothing.
// API references: Unity 2021.3 Profiler.enableBinaryLog / logFile / ProfilerRecorder.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NativeH3VR;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

public sealed class NewEngineLiveProfile : MonoBehaviour {
    const int MaximumFrames = 8192, MaximumRequestBytes = 4096, MaximumCaptures = 8;
    const long BinaryStopThreshold = 512L * 1024 * 1024;
    const int BinaryBufferBytes = 64 * 1024 * 1024;
    const int ExcludedWarmupFrames = 2;
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    static NewEngineLiveProfile instance;
    static readonly string[] MetricNames = { "Main Thread",
                                             "Render Thread",
                                             "PlayerLoop",
                                             "Update.ScriptRunBehaviourUpdate",
                                             "FixedUpdate.PhysicsFixedUpdate",
                                             "Physics.Simulate",
                                             "GC.Alloc",
                                             "GC Allocated In Frame",
                                             "GC Used Memory",
                                             "Total Used Memory",
                                             "System Used Memory",
                                             "Draw Calls Count",
                                             "Batches Count",
                                             "Triangles Count",
                                             "SetPass Calls Count",
                                             "Gfx.WaitForPresentOnGfxThread",
                                             "Gfx.WaitForGfxCommandsFromMainThread" };
#pragma warning disable 0649 // JsonUtility populates request fields.
    [Serializable]
    sealed class Request {
        public string runId, captureId, label;
        public int pid, seconds;
    }
#pragma warning restore 0649
    [Serializable]
    sealed class Metadata {
        public string schema = "newengineh3vr-live-profile-1", runId, gameRunId, processStartUtc,
                      startedUtc, finishedUtc;
        public string runDirectory, captureId, captureDirectory, rawPath, startRequestPath,
            stopRequestPath;
        public string unityVersion, platform, graphicsDevice, startScene, endScene, state,
            stopReason, error;
        public string observedProfilerLogFile, previousProfilerLogFile, restoreError, metricsError;
        public string metricFrameAlignment =
            "Latest fresh completed-frame samples read in Update; metric source frame is " +
            "estimated as observer frame minus one. Binary log provides authoritative marker " +
            "placement.";
        public string semantics =
            "Profiling overhead is present throughout the binary log and measured window. Unity " +
            "marker timings are inclusive elapsed durations and can include waits; they are not " +
            "OS CPU consumption and must not be summed as exclusive time. Update intervals are " +
            "observed main-loop wall cadence. Stereo completion deltas are Unity observations of " +
            "the stereoRenderer's native completion counter, not individual completion " +
            "timestamps, physical scanout or headset acceptance. First two observer frames are " +
            "excluded from scalar summaries; binary setup/warmup remains visible. " +
            "Missing/unregistered/unobserved metrics are unavailable, never substituted with " +
            "zero. Scene changes are observed and reported, never driven. Binary disk stop " +
            "threshold is polled once per second; in-flight buffers can overshoot it. Forced " +
            "termination cannot guarantee flushing or restoration.";
        public int pid, requestedSeconds, samples, excludedWarmupFrames,
            maximumSamples = MaximumFrames;
        public int capturesStarted, maximumCaptures = MaximumCaptures, sceneSegments,
                                    staleRecorderSamples;
        public int width, height, vSyncCount, antiAliasing, targetFrameRate;
        public float fixedDeltaTime;
        public long stopwatchFrequency, binaryStopThresholdBytes = BinaryStopThreshold,
                                        profilerBufferBytes = BinaryBufferBytes;
        public long rawBytes = -1;
        public bool profilerOverhead = true, developmentBuild, capturing, previousProfilerEnabled,
                    previousBinaryLog;
        public bool settingsRestored, restoreSkippedOwnershipConflict, sceneChanged,
            sampleCapacityReached;
        public double captureElapsedSeconds, acceptedSampleWindowSeconds;
        public ulong observedStereoCompletions;
        public int stereoObservedIntervals, stereoBatchedIntervals, stereoCounterResets,
            missingStereoSamples;
    }
    // Separate new types preserve the existing component and capture Metadata field layouts.
    [Serializable]
    sealed class SceneStatus {
        public string activeScene, statusUpdatedUtc;
        public int activeSceneHandle, statusUnityFrame;
        public bool activeSceneLoaded;
    }
    static class StatusCache {
        public static string lastState, lastError;
        public static long revision;
    }
    sealed class Metric {
        public string name, category, unit, dataType, error;
        public bool found, created, running, timing, requiresPositive;
        public int lastReadCount;
        public ProfilerRecorder recorder;
        public long[] values, calls;
        public bool[] valid;
        public int observed, missing, nonPositiveTiming, stale;
    }
    struct Frame {
        public int unityFrame, sourceUnityFrame, sceneIndex, rendererId;
        public long ticks;
        public double intervalMs;
        public bool stereoAvailable, stereoDeltaAvailable;
        public ulong stereoCount, stereoDelta;
    }
    [Serializable]
    sealed class SceneRow {
        public string name;
        public int handle, firstObserverFrame;
        public double atSeconds;
    }
    string rootDirectory, runDirectory, readyPath, startPath, stopPath, runId, gameRunId,
        processStart;
    int pid, defaultSeconds = 30, captureCount, warmup, frameCount;
    bool capturing, profilerTouched;
    bool previousEnabled, previousBinary;
    string previousLog, ownedLog;
    int previousMemory;
    long captureTicks, lastTicks, nextPoll;
    ulong lastStereo;
    int lastRendererId, lastSceneHandle = int.MinValue;
    NativeStereoRenderer stereoRenderer;
    Metadata metadata;
    Frame[] frames;
    readonly List<Metric> metrics = new List<Metric>();
    readonly List<SceneRow> scenes = new List<SceneRow>();

    public static void Install() {
        if (instance != null)
            return;
        string directory = Environment.GetEnvironmentVariable("NEWENGINEH3VR_PROFILE_DIR");
        if (String.IsNullOrEmpty(directory))
            return;
        if (!Path.IsPathRooted(directory)) {
            Debug.LogError(
                "NEWENGINEH3VR_PROFILE_FAIL: NEWENGINEH3VR_PROFILE_DIR must be absolute");
            return;
        }
        GameObject host = null;
        try {
            host = new GameObject("NewEngine opt-in live profiling controller");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<NewEngineLiveProfile>();
            instance.Configure(Path.GetFullPath(directory));
        } catch (Exception error) {
            Debug.LogError("NEWENGINEH3VR_PROFILE_FAIL install failed: " + error);
            if (host != null)
                Destroy(host);
            instance = null;
        }
    }
    void Configure(string directory) {
        StatusCache.lastState = "ready";
        StatusCache.lastError = null;
        StatusCache.revision = 0;
        rootDirectory = directory;
        using (Process process = Process.GetCurrentProcess()) {
            pid = process.Id;
            try {
                processStart = process.StartTime.ToUniversalTime().ToString("o");
            } catch {
                processStart = "unavailable";
            }
        }
        runId = Guid.NewGuid().ToString("N");
        gameRunId = Environment.GetEnvironmentVariable("NEWENGINEH3VR_RUN_ID") ??
                    Environment.GetEnvironmentVariable("NATIVEH3VR_RUN_ID");
        int seconds;
        string requested = Environment.GetEnvironmentVariable("NEWENGINEH3VR_PROFILE_SECONDS");
        if (requested != null &&
            (!Int32.TryParse(requested, out seconds) || seconds < 1 || seconds > 60))
            throw new ArgumentException("NEWENGINEH3VR_PROFILE_SECONDS must be 1..60");
        if (requested != null)
            defaultSeconds = Int32.Parse(requested, Invariant);
        runDirectory =
            Path.Combine(rootDirectory, "live-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") +
                                            "-" + pid + "-" + runId.Substring(0, 8));
        Directory.CreateDirectory(runDirectory);
        startPath = Path.Combine(runDirectory, "start.json");
        stopPath = Path.Combine(runDirectory, "stop.json");
        readyPath =
            Path.Combine(rootDirectory, "ready-" + pid + "-" + runId.Substring(0, 8) + ".json");
        WriteState("ready", null);
        nextPoll = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        Debug.Log("NEWENGINEH3VR_PROFILE_READY pid=" + pid + " runId=" + runId +
                  " directory=" + runDirectory);
    }
    Metadata NewMetadata() {
        return new Metadata { runId = runId,
                              gameRunId = gameRunId,
                              pid = pid,
                              processStartUtc = processStart,
                              runDirectory = runDirectory,
                              startRequestPath = startPath,
                              stopRequestPath = stopPath,
                              unityVersion = Application.unityVersion,
                              platform = Application.platform.ToString(),
                              developmentBuild = Debug.isDebugBuild,
                              startedUtc = DateTime.UtcNow.ToString("o"),
                              stopwatchFrequency = Stopwatch.Frequency,
                              capturesStarted = captureCount,
                              observedProfilerLogFile = Profiler.logFile };
    }
    void WriteState(string state, string error) {
        var status = NewMetadata();
        status.state = state;
        status.error = error;
        status.capturing = capturing;
        Scene scene = SceneManager.GetActiveScene();
        var sceneStatus =
            new SceneStatus { activeScene = scene.name, activeSceneHandle = scene.handle,
                              activeSceneLoaded = scene.isLoaded,
                              statusUpdatedUtc = DateTime.UtcNow.ToString("o"),
                              statusUnityFrame = Time.frameCount };
        status.requestedSeconds =
            capturing && metadata != null ? metadata.requestedSeconds : defaultSeconds;
        if (metadata != null) {
            status.captureId = metadata.captureId;
            status.captureDirectory = metadata.captureDirectory;
            status.rawPath = metadata.rawPath;
        }
        string json = JsonUtility.ToJson(status, true).TrimEnd();
        // Both are JSON objects; append only the new scene properties to ready/status output.
        // Capture summaries continue to serialize the original Metadata layout alone.
        json = json.Substring(0, json.Length - 1) + ",\n" +
               JsonUtility.ToJson(sceneStatus, true).Substring(1);
        AtomicJson(readyPath, json);
        AtomicJson(Path.Combine(runDirectory, "status.json"), json);
        StatusCache.lastState = state;
        StatusCache.lastError = error;
        StatusCache.revision++;
    }
    static void AtomicJson(string path, string json) {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json + "\n");
        if (File.Exists(path))
            File.Delete(path);
        File.Move(temporary, path);
    }
    void Update() {
        if (runDirectory == null)
            return;
        long now = Stopwatch.GetTimestamp();
        try {
            if (capturing) {
                if ((now - captureTicks) / (double)Stopwatch.Frequency >= metadata.requestedSeconds)
                    Finish("duration", null);
                else if (frameCount >= MaximumFrames) {
                    metadata.sampleCapacityReached = true;
                    Finish("sample-capacity", null);
                } else
                    Sample(now);
            }
            if (now >= nextPoll) {
                nextPoll = now + Stopwatch.Frequency;
                long revisionBeforePoll = StatusCache.revision;
                if (capturing && stereoRenderer == null)
                    stereoRenderer = FindObjectOfType<NativeStereoRenderer>();
                Poll(stopPath, true);
                Poll(startPath, false);
                if (capturing) {
                    if (Profiler.logFile != ownedLog || !Profiler.enabled ||
                        !Profiler.enableBinaryLog)
                        Finish("profiler-state-changed-externally", null);
                    else if (File.Exists(metadata.rawPath) &&
                             new FileInfo(metadata.rawPath).Length >= BinaryStopThreshold)
                        Finish("binary-disk-threshold", null);
                }
                // Idle telemetry follows scene entry without adding periodic writes to a capture.
                // Preserve the last request/result state and avoid duplicating an event write in
                // this poll.
                if (!capturing && StatusCache.revision == revisionBeforePoll)
                    WriteState(StatusCache.lastState, StatusCache.lastError);
            }
        } catch (Exception error) {
            if (capturing || profilerTouched)
                Finish("error", error.ToString());
            else {
                try {
                    WriteState("request-error", error.ToString());
                } catch {
                }
                Debug.LogError("NEWENGINEH3VR_PROFILE_FAIL: " + error);
            }
        }
    }
    void Poll(string path, bool stop) {
        if (!File.Exists(path))
            return;
        if (new FileInfo(path).Length > MaximumRequestBytes) {
            File.Delete(path);
            WriteState("request-rejected", "Request exceeds 4096 bytes");
            return;
        }
        string text = File.ReadAllText(path);
        File.Delete(path);
        Request request = JsonUtility.FromJson<Request>(text);
        if (request == null || request.runId != runId || request.pid != pid) {
            WriteState("request-rejected", "Exact runId and pid binding required");
            return;
        }
        if (stop) {
            if (capturing && request.captureId == metadata.captureId)
                Finish("requested-stop", null);
            else
                WriteState("request-rejected", "Stop requires active captureId");
            return;
        }
        if (capturing) {
            WriteState("request-rejected", "Capture already active");
            return;
        }
        int seconds = request.seconds == 0 ? defaultSeconds : request.seconds;
        if (seconds < 1 || seconds > 60 || captureCount >= MaximumCaptures) {
            WriteState("request-rejected",
                       "Duration must be 1..60 seconds; maximum eight captures per process");
            return;
        }
        if (!Debug.isDebugBuild) {
            WriteState("request-rejected",
                       "Binary profiling requires this port's Development build");
            return;
        }
        if (Profiler.enabled || Profiler.enableBinaryLog) {
            WriteState("request-rejected",
                       "Profiler is already active; existing profiling session preserved");
            return;
        }
        Begin(seconds, request.label);
    }
    void Begin(int seconds, string label) {
        previousEnabled = Profiler.enabled;
        previousBinary = Profiler.enableBinaryLog;
        previousLog = Profiler.logFile;
        previousMemory = Profiler.maxUsedMemory;
        captureCount++;
        metadata = NewMetadata();
        metadata.capturesStarted = captureCount;
        metadata.requestedSeconds = seconds;
        metadata.state = "capturing";
        metadata.capturing = true;
        metadata.captureId = "capture-" + captureCount.ToString("D2") + "-" +
                             Guid.NewGuid().ToString("N").Substring(0, 8);
        metadata.captureDirectory = Path.Combine(runDirectory, metadata.captureId);
        metadata.rawPath = Path.Combine(metadata.captureDirectory, "unity.raw");
        metadata.previousProfilerEnabled = previousEnabled;
        metadata.previousBinaryLog = previousBinary;
        metadata.previousProfilerLogFile = previousLog;
        metadata.startScene = SceneManager.GetActiveScene().name;
        metadata.width = Screen.width;
        metadata.height = Screen.height;
        metadata.vSyncCount = QualitySettings.vSyncCount;
        metadata.antiAliasing = QualitySettings.antiAliasing;
        metadata.targetFrameRate = Application.targetFrameRate;
        metadata.fixedDeltaTime = Time.fixedDeltaTime;
        metadata.graphicsDevice = SystemInfo.graphicsDeviceName;
        Directory.CreateDirectory(metadata.captureDirectory);
        File.WriteAllText(Path.Combine(metadata.captureDirectory, "label.txt"),
                          (label ?? "User-driven gameplay") + "\n");
        frames = new Frame[MaximumFrames];
        metrics.Clear();
        scenes.Clear();
        frameCount = 0;
        warmup = 0;
        lastSceneHandle = int.MinValue;
        lastRendererId = 0;
        lastStereo = 0;
        try {
            PrepareMetrics();
            if (stereoRenderer == null)
                stereoRenderer = FindObjectOfType<NativeStereoRenderer>();
            profilerTouched = true;
            Profiler.maxUsedMemory = BinaryBufferBytes;
            Profiler.logFile = metadata.rawPath;
            ownedLog = Profiler.logFile;
            Profiler.enableBinaryLog = true;
            Profiler.enabled = true;
            if (!Profiler.enabled || !Profiler.enableBinaryLog)
                throw new InvalidOperationException("Unity did not enable binary profiling");
            foreach (Metric metric in metrics)
                if (metric.found) {
                    metric.recorder.Start();
                    metric.running = metric.recorder.IsRunning;
                }
            capturing = true;
            captureTicks = lastTicks = Stopwatch.GetTimestamp();
            AtomicJson(Path.Combine(metadata.captureDirectory, "started.json"),
                       JsonUtility.ToJson(metadata, true));
            WriteState("capturing:" + metadata.captureId, null);
            Debug.Log("NEWENGINEH3VR_PROFILE_START pid=" + pid + " runId=" + runId +
                      " captureId=" + metadata.captureId + " seconds=" + seconds);
        } catch (Exception error) {
            RestoreProfiler(true);
            DisposeMetrics();
            capturing = false;
            metadata.error = error.ToString();
            metadata.state = "start-failed";
            WriteCapture();
            WriteState("start-failed", error.ToString());
        }
    }
    void PrepareMetrics() {
        var available = new List<ProfilerRecorderHandle>();
        try {
            ProfilerRecorderHandle.GetAvailable(available);
        } catch (Exception error) {
            metadata.metricsError = error.ToString();
        }
        foreach (string name in MetricNames) {
            var metric =
                new Metric { name = name, values = new long[MaximumFrames],
                             calls = new long[MaximumFrames], valid = new bool[MaximumFrames] };
            metrics.Add(metric);
            foreach (ProfilerRecorderHandle handle in available) {
                try {
                    var description = ProfilerRecorderHandle.GetDescription(handle);
                    if (description.Name != name)
                        continue;
                    metric.category = description.Category.Name;
                    metric.unit = description.UnitType.ToString();
                    metric.dataType = description.DataType.ToString();
                    metric.timing = description.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds;
                    metric.requiresPositive = metric.timing || name == "GC Used Memory" ||
                                              name == "Total Used Memory" ||
                                              name == "System Used Memory";
                    // Every selected metric is integer based; reject unexpected representations.
                    if (metric.dataType != "Int64" && metric.dataType != "UInt64") {
                        metric.error = "Unsupported recorder data type: " + metric.dataType;
                        break;
                    }
                    metric.recorder =
                        new ProfilerRecorder(handle, MaximumFrames + ExcludedWarmupFrames + 4,
                                             ProfilerRecorderOptions.Default);
                    metric.created = true;
                    metric.found = metric.recorder.Valid;
                    if (!metric.found)
                        metric.error = "Registered metric has invalid recorder";
                    break;
                } catch (Exception error) {
                    metric.error = error.Message;
                }
            }
            if (!metric.found && metric.error == null)
                metric.error = "Metric not registered in this Unity player/workload";
        }
    }
    void Sample(long now) {
        if (warmup++ < ExcludedWarmupFrames) {
            foreach (Metric metric in metrics)
                if (metric.found)
                    metric.lastReadCount = metric.recorder.Count;
            lastTicks = now;
            return;
        }
        Scene scene = SceneManager.GetActiveScene();
        if (scene.handle != lastSceneHandle) {
            scenes.Add(
                new SceneRow { name = scene.name, handle = scene.handle,
                               firstObserverFrame = Time.frameCount,
                               atSeconds = (now - captureTicks) / (double)Stopwatch.Frequency });
            lastSceneHandle = scene.handle;
        }
        var frame =
            new Frame { unityFrame = Time.frameCount, sourceUnityFrame = Time.frameCount - 1,
                        sceneIndex = scenes.Count - 1, ticks = now,
                        intervalMs = (now - lastTicks) * 1000.0 / Stopwatch.Frequency };
        lastTicks = now;
        if (stereoRenderer != null) {
            frame.stereoAvailable = true;
            frame.rendererId = stereoRenderer.GetInstanceID();
            frame.stereoCount = stereoRenderer.completedFrames;
            if (lastRendererId == frame.rendererId && frame.stereoCount >= lastStereo) {
                frame.stereoDeltaAvailable = true;
                frame.stereoDelta = frame.stereoCount - lastStereo;
                metadata.observedStereoCompletions += frame.stereoDelta;
                if (frame.stereoDelta > 0) {
                    metadata.stereoObservedIntervals++;
                    if (frame.stereoDelta > 1)
                        metadata.stereoBatchedIntervals++;
                }
            } else if (lastRendererId != 0)
                metadata.stereoCounterResets++;
            lastRendererId = frame.rendererId;
            lastStereo = frame.stereoCount;
        } else {
            metadata.missingStereoSamples++;
            lastRendererId = 0;
        }
        frames[frameCount] = frame;
        foreach (Metric metric in metrics) {
            if (!metric.found || !metric.running) {
                metric.missing++;
                continue;
            }
            try {
                int count = metric.recorder.Count;
                if (count <= metric.lastReadCount) {
                    metric.missing++;
                    continue;
                }
                int fresh = count - metric.lastReadCount;
                var sample = metric.recorder.GetSample(count - 1);
                metric.lastReadCount = count;
                if (fresh > 1) {
                    metric.stale += fresh - 1;
                    metadata.staleRecorderSamples += fresh - 1;
                }
                if (metric.requiresPositive && sample.Value <= 0) {
                    metric.nonPositiveTiming++;
                    metric.missing++;
                    continue;
                }
                metric.values[frameCount] = sample.Value;
                metric.calls[frameCount] = sample.Count;
                metric.valid[frameCount] = true;
                metric.observed++;
            } catch (Exception error) {
                metric.error = error.Message;
                metric.missing++;
                metric.running = false;
            }
        }
        frameCount++;
    }
    void Finish(string reason, string error) {
        if (!capturing && !profilerTouched)
            return;
        capturing = false;
        if (metadata != null) {
            metadata.capturing = false;
            metadata.stopReason = reason;
            metadata.error = error;
            metadata.finishedUtc = DateTime.UtcNow.ToString("o");
            metadata.samples = frameCount;
            metadata.excludedWarmupFrames = Math.Min(warmup, ExcludedWarmupFrames);
            metadata.captureElapsedSeconds =
                (Stopwatch.GetTimestamp() - captureTicks) / (double)Stopwatch.Frequency;
            metadata.acceptedSampleWindowSeconds =
                frameCount < 2 ? 0
                               : (frames[frameCount - 1].ticks - frames[0].ticks) /
                                     (double)Stopwatch.Frequency;
            metadata.endScene = SceneManager.GetActiveScene().name;
            metadata.sceneSegments = scenes.Count;
            metadata.sceneChanged = scenes.Count > 1;
        }
        RestoreProfiler(false);
        DisposeMetrics();
        try {
            metadata.state = error == null ? "complete" : "failed";
            WriteCapture();
            WriteState("ready-after:" + metadata.captureId, error);
        } catch (Exception writeError) {
            Debug.LogError("NEWENGINEH3VR_PROFILE_FAIL report failed: " + writeError);
        }
        frames = null;
        metrics.Clear();
        scenes.Clear();
        Debug.Log("NEWENGINEH3VR_PROFILE_STOP pid=" + pid + " runId=" + runId +
                  " reason=" + reason);
    }
    void RestoreProfiler(bool force) {
        if (!profilerTouched)
            return;
        try {
            if (!force && Profiler.logFile != ownedLog) {
                metadata.restoreSkippedOwnershipConflict = true;
                metadata.restoreError = "External profiler changed logFile; new owner left intact";
                return;
            }
            // Changing logFile closes/flushes the binary stream per Unity's API.
            RestoreSetting(delegate { Profiler.enabled = false; });
            RestoreSetting(delegate { Profiler.enableBinaryLog = false; });
            RestoreSetting(delegate { Profiler.logFile = ""; });
            RestoreSetting(delegate { Profiler.maxUsedMemory = previousMemory; });
            RestoreSetting(delegate { Profiler.logFile = previousLog; });
            RestoreSetting(delegate { Profiler.enableBinaryLog = previousBinary; });
            RestoreSetting(delegate { Profiler.enabled = previousEnabled; });
            metadata.settingsRestored =
                Profiler.enabled == previousEnabled && Profiler.enableBinaryLog == previousBinary &&
                Profiler.logFile == previousLog && Profiler.maxUsedMemory == previousMemory;
        } catch (Exception error) {
            metadata.restoreError = error.ToString();
        } finally {
            profilerTouched = false;
        }
    }
    void RestoreSetting(Action action) {
        try {
            action();
        } catch (Exception error) {
            metadata.restoreError = (metadata.restoreError ?? "") + "\n" + error;
        }
    }
    void DisposeMetrics() {
        foreach (Metric metric in metrics)
            if (metric.created) {
                try {
                    metric.recorder.Stop();
                } catch (Exception error) {
                    metric.error = (metric.error ?? "") + "; stop: " + error.Message;
                } finally {
                    try {
                        metric.recorder.Dispose();
                    } catch (Exception error) {
                        metric.error = (metric.error ?? "") + "; dispose: " + error.Message;
                    }
                    metric.created = false;
                    metric.running = false;
                }
            }
    }
    void OnApplicationQuit() { Finish("application-quit", null); }
    void OnDisable() { Finish("component-disabled", null); }
    void OnDestroy() {
        Finish("component-destroyed", null);
        if (instance == this)
            instance = null;
    }

    static void Quote(TextWriter writer, string text) {
        if (text == null) {
            writer.Write("null");
            return;
        }
        writer.Write('"');
        foreach (char c in text) {
            if (c == '"' || c == '\\') {
                writer.Write('\\');
                writer.Write(c);
            } else if (c < 32)
                writer.Write("\\u" + ((int)c).ToString("x4"));
            else
                writer.Write(c);
        }
        writer.Write('"');
    }
    static void Number(TextWriter writer, double value) {
        writer.Write(value.ToString("R", Invariant));
    }
    static void Stats(TextWriter writer, double[] values, int count) {
        if (count == 0) {
            writer.Write("null");
            return;
        }
        Array.Sort(values, 0, count);
        double sum = 0;
        for (int i = 0; i < count; i++)
            sum += values[i];
        writer.Write("{\"samples\":" + count + ",\"mean\":");
        Number(writer, sum / count);
        writer.Write(",\"min\":");
        Number(writer, values[0]);
        writer.Write(",\"max\":");
        Number(writer, values[count - 1]);
        writer.Write(",\"median\":");
        Number(writer, (values[(count - 1) / 2] + values[count / 2]) * 0.5);
        writer.Write(",\"p95\":");
        Number(writer, values[Math.Min(count - 1, (int)Math.Ceiling(count * .95) - 1)]);
        writer.Write(",\"p99\":");
        Number(writer, values[Math.Min(count - 1, (int)Math.Ceiling(count * .99) - 1)]);
        writer.Write('}');
    }
    void WriteCapture() {
        if (metadata == null || metadata.captureDirectory == null)
            return;
        try {
            metadata.rawBytes =
                File.Exists(metadata.rawPath) ? new FileInfo(metadata.rawPath).Length : -1;
        } catch {
            metadata.rawBytes = -1;
        }
        string header = JsonUtility.ToJson(metadata, true).TrimEnd();
        using (var writer =
                   new StreamWriter(Path.Combine(metadata.captureDirectory, "summary.json"), false,
                                    Utf8NoBom, 16384)) {
            writer.Write(header.Substring(0, header.Length - 1));
            writer.Write(",\n\"scenes\":[");
            for (int i = 0; i < scenes.Count; i++) {
                if (i != 0)
                    writer.Write(',');
                writer.Write(JsonUtility.ToJson(scenes[i]));
            }
            writer.Write("],\n\"observerCadenceMs\":");
            var cadence = new double[frameCount];
            for (int i = 0; i < frameCount; i++)
                cadence[i] = frames[i].intervalMs;
            Stats(writer, cadence, frameCount);
            writer.Write(",\n\"observedStereoCompletionsPerSecond\":");
            if (metadata.stereoObservedIntervals == 0 ||
                metadata.acceptedSampleWindowSeconds <= 0 || metadata.missingStereoSamples != 0 ||
                metadata.stereoCounterResets != 0)
                writer.Write("null");
            else
                Number(writer,
                       metadata.observedStereoCompletions / metadata.acceptedSampleWindowSeconds);
            writer.Write(",\n\"metrics\":[");
            for (int m = 0; m < metrics.Count; m++) {
                Metric metric = metrics[m];
                if (m != 0)
                    writer.Write(',');
                writer.Write("{\"name\":");
                Quote(writer, metric.name);
                writer.Write(",\"category\":");
                Quote(writer, metric.category);
                writer.Write(",\"unit\":");
                Quote(writer, metric.unit);
                writer.Write(",\"dataType\":");
                Quote(writer, metric.dataType);
                writer.Write(",\"registered\":" + (metric.found ? "true" : "false") +
                             ",\"available\":" + (metric.observed > 0 ? "true" : "false") +
                             ",\"observedFrames\":" + metric.observed +
                             ",\"missingFrames\":" + metric.missing);
                writer.Write(",\"nonPositiveTimingFrames\":" + metric.nonPositiveTiming +
                             ",\"discardedExtraSamples\":" + metric.stale + ",\"error\":");
                Quote(writer, metric.error);
                writer.Write(",\"rawValueStatistics\":");
                var values = new double[metric.observed];
                int at = 0;
                for (int i = 0; i < frameCount; i++)
                    if (metric.valid[i])
                        values[at++] = metric.values[i];
                Stats(writer, values, at);
                writer.Write('}');
            }
            writer.Write("]\n}\n");
        }
        using (var writer = new StreamWriter(Path.Combine(metadata.captureDirectory, "frames.csv"),
                                             false, Utf8NoBom, 16384)) {
            writer.Write("observerUnityFrame,metricSourceUnityFrame,sceneIndex,stopwatchTicks," +
                         "observerIntervalMs,rendererId,observedStereoCount,observedStereoDelta");
            foreach (Metric metric in metrics) {
                writer.Write(',');
                Quote(writer, metric.name + " [" + metric.unit + "]");
                writer.Write(',');
                Quote(writer, metric.name + " sampleCount");
            }
            writer.WriteLine();
            for (int i = 0; i < frameCount; i++) {
                Frame frame = frames[i];
                writer.Write(frame.unityFrame + "," + frame.sourceUnityFrame + "," +
                             frame.sceneIndex + "," + frame.ticks + ",");
                Number(writer, frame.intervalMs);
                writer.Write("," + frame.rendererId + ",");
                if (frame.stereoAvailable)
                    writer.Write(frame.stereoCount);
                writer.Write(',');
                if (frame.stereoDeltaAvailable)
                    writer.Write(frame.stereoDelta);
                foreach (Metric metric in metrics) {
                    writer.Write(',');
                    if (metric.valid[i])
                        writer.Write(metric.values[i]);
                    writer.Write(',');
                    if (metric.valid[i])
                        writer.Write(metric.calls[i]);
                }
                writer.WriteLine();
            }
        }
    }
}
#endif
