// Unity 2021 Editor-only offline binary Profiler reader. No game code changes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NewEngineH3VR.EditorTools {
public static class NewEngineProfileAnalysis {
    [Serializable]
    public sealed class Options {
        public int firstFrame = -1, lastFrame = -1, maxFrames = 600, maxThreads = 256,
                   topMarkers = 60, maxSamplesPerThreadFrame = 250000;
        public int frameHistoryCapacity = 20000;
        public long maxSamples = 5000000, maxInputBytes = 2147483648L;
        public double maxAnalysisSeconds = 45;
    }
    [Serializable]
    public sealed class Marker {
        public string threadKey, threadName, name, category, flags;
        public long calls;
        public long rootCalls;
        public int framesSeen, lastFrame = -1;
        public double inclusiveMs, exclusiveMs, maxInclusiveMs;
        public bool waitNameHint;
    }
    [Serializable]
    public sealed class Thread {
        public string key, name, group, id;
        public int frameViews, minThreadIndex = int.MaxValue, maxThreadIndex;
        public long samples;
        public List<Marker> topInclusive = new List<Marker>(), topExclusive = new List<Marker>();
        public List<Marker> topNamedInclusive = new List<Marker>(),
                            topNamedNonWaitExclusive = new List<Marker>(),
                            topNamedWaitExclusive = new List<Marker>();
    }
    [Serializable]
    public sealed class Frame {
        public int index, threadViews, invalidThreadViews;
        public bool complete;
        public long samples;
        public double cpuFrameMs;
        public string frameTimeThread;
    }
    [Serializable]
    public sealed class AllocationParent {
        public string threadKey, threadName, parentName, recordedAncestorPath;
        public long calls, bytesKnownCalls, allocatedBytes;
        public int framesSeen, lastFrame = -1;
        public double allocationMarkerMs;
    }
    [Serializable]
    public sealed class Report {
        public string schema = "newengineh3vr-profiler-analysis-2", input, inputSha256,
                      editorVersion, generatedUtc;
        public long inputBytes, samplesAnalyzed;
        public bool loaded, valid, complete, fullCaptureAnalyzed, inputUnchanged, capped;
        public bool fullLoadedHistoryAnalyzed, loadHistoryStartsAtZero,
            loadedHistoryAtRequestedCapacity, historyCapacityRestored;
        public int requestedHistoryCapacity, previousEditorHistorySetting;
        public string historyCoverage =
            "ProfilerDriver exposes loaded history, not an independently verified raw-file frame " +
            "count. fullCaptureAnalyzed remains false; fullLoadedHistoryAnalyzed refers only to " +
            "the loaded API range. A nonzero loadedFirstFrame indicates a missing prefix. Only " +
            "this isolated Editor's native history capacity is changed; shared EditorPrefs are " +
            "not written.";
        public long allocationSamples, allocationSamplesWithBytes, allocatedBytesFromSamples;
        public string allocationAttribution =
            "Immediate parent and ancestor path are recorded sample scopes, not inferred managed " +
            "call stacks. Byte totals use only nonnegative metadata explicitly declared " +
            "Int64/Bytes on GC.Alloc; unknown metadata remains unavailable. Top groups are " +
            "truncated by topMarkers; totals include all groups in analyzed thread views.";
        public List<AllocationParent> topAllocationParents = new List<AllocationParent>();
        public int loadedFirstFrame, loadedLastFrame, eligibleFrames, selectedFrames,
            analyzedFrames, completeFrames, partialFrames, framesWithoutData, threadViews,
            invalidThreadViews;
        public double analysisSeconds, meanFrameMs, medianFrameMs, p95FrameMs, maxFrameMs;
        public string frameSampling,
            timeUnit = "milliseconds",
            managedMethodAttribution = "Only recorded markers; no uninstrumented managed methods " +
                                       "or call stacks are inferred. Deep-profiling state cannot " +
                                       "be established from current Editor settings.";
        public Options options;
        public List<Frame> frames = new List<Frame>();
        public List<Thread> threads = new List<Thread>();
        public List<Marker> topInclusive = new List<Marker>(), topExclusive = new List<Marker>();
        public List<Marker> topNamedInclusive = new List<Marker>(),
                            topNamedNonWaitExclusive = new List<Marker>(),
                            topNamedWaitExclusive = new List<Marker>(),
                            rootOrUnnamedMarkers = new List<Marker>();
        public List<string> issues = new List<string>(), limits = new List<string>(),
                            recordedManagedNameExamples = new List<string>();
    }
    sealed class Sample {
        internal string name, category, flags;
        internal int children;
        internal int parent = -1;
        internal long allocationBytes = -1;
        internal double duration, start, self;
    }
    sealed class Parent {
        internal int index, remaining;
        internal double lastChildEnd = double.NegativeInfinity;
    }

    // Called from executeCSharp after this source is copied into an Editor
    // folder, or via -executeMethod ...AnalyzeFromEnvironment on an idle
    // analysis Editor. Never invoke against an actively recording Profiler.
    public static string Analyze(string profile, string output) {
        return Analyze(profile, output, new Options());
    }
    public static string Analyze(string profile, string output, Options options) {
        if (EditorApplication.isPlaying || ProfilerDriver.enabled)
            throw new InvalidOperationException(
                "Stop Editor play/Profiler recording before replacing its loaded Profiler data");
        if (options == null || options.maxFrames < 1 || options.maxThreads < 1 ||
            options.maxThreads > 4096 || options.topMarkers < 1 || options.topMarkers > 500 ||
            options.maxSamples < 1 || options.maxSamplesPerThreadFrame < 1 ||
            options.maxInputBytes < 1 || options.firstFrame < -1 || options.lastFrame < -1 ||
            (options.firstFrame >= 0 && options.lastFrame >= 0 &&
             options.firstFrame > options.lastFrame) ||
            options.frameHistoryCapacity < 1 || options.frameHistoryCapacity > 200000 ||
            double.IsNaN(options.maxAnalysisSeconds) ||
            double.IsInfinity(options.maxAnalysisSeconds) || options.maxAnalysisSeconds <= 0)
            throw new ArgumentException("Invalid analysis limits");
        profile = Path.GetFullPath(profile);
        output = Path.GetFullPath(output);
        if (profile == output || File.Exists(output))
            throw new ArgumentException(
                "Output must differ from raw input and must be a fresh path");
        var file = new FileInfo(profile);
        if (!file.Exists || file.Length == 0 || file.Length > options.maxInputBytes)
            throw new ArgumentException("Input is absent, empty or exceeds maxInputBytes");
        long originalLength = file.Length;
        DateTime originalWrite = file.LastWriteTimeUtc;
        var watch = Stopwatch.StartNew();
        var report = new Report {
            input = profile,
            inputBytes = originalLength,
            inputSha256 = Hash(profile),
            options = options,
            editorVersion = Application.unityVersion,
            generatedUtc = DateTime.UtcNow.ToString("o"),
            frameSampling =
                "Uniformly spaced frame indices when eligible frame count exceeds maxFrames"
        };
        report.limits.AddRange(new[] {
            "Inclusive marker totals overlap nested/recursive scopes. Do not sum them as total " +
                "frame time.",
            "Exclusive means marker duration minus immediate recorded child scopes, not measured " +
                "CPU execution. It includes waits, preemption and uninstrumented work.",
            "Threads overlap in wall time. Summing thread marker times is not elapsed capture " +
                "time or CPU utilization.",
            "Partial-frame aggregates retain only successfully read thread views and are " +
                "diagnostic; inspect completeFrames/partialFrames/capped before comparing " +
                "rankings.",
            "Wait hints use marker names only; they are interpretation aids, not scheduler " +
                "measurements.",
            "Root/unnamed thread spans include uninstrumented elapsed time; preserve them as " +
                "unknown/root time, not attributed work hotspots. Named rankings omit " +
                "root/unnamed " + "spans and split name-hinted waits.",
            "No automatic warmup/loading exclusion. Select firstFrame/lastFrame from the actual " +
                "busy gameplay interval.",
            "No headset scanout/FPS, physical tracking or visual acceptance follows from a CPU " +
                "profile.",
            "Raw-file analysis alone does not authenticate the captured player process, app " +
                "hashes, settings or Sosig count; retain the capture manifest/log.",
            "Frame statistics describe analyzed frames only; uniformly sampled frames can miss " +
                "short spikes, and maxFrameMs is not necessarily the full-capture maximum.",
            "This replaces the analysis Editor's loaded Profiler history; it never edits the raw " +
                "capture."
        });
        var markers = new Dictionary<string, Marker>();
        var threads = new Dictionary<string, Thread>();
        var allocations = new Dictionary<string, AllocationParent>();
        MethodInfo historySetter = null;
        report.requestedHistoryCapacity = options.frameHistoryCapacity;
        bool capped = false;
        try {
            // Unity defaults to retaining only the last 300 loaded frames.
            // This native-only setter avoids writing global EditorPrefs.
            historySetter = typeof(ProfilerDriver)
                                .GetMethod("SetMaxFrameHistoryLength",
                                           BindingFlags.Static | BindingFlags.NonPublic);
            var settingsType =
                typeof(ProfilerDriver)
                    .Assembly.GetType("UnityEditor.Profiling.ProfilerUserSettings", true);
            report.previousEditorHistorySetting =
                (int)settingsType
                    .GetProperty("frameCount", BindingFlags.Public | BindingFlags.Static)
                    .GetValue(null, null);
            if (historySetter == null)
                throw new MissingMethodException(
                    "Unity 2021 ProfilerDriver.SetMaxFrameHistoryLength");
            historySetter.Invoke(null, new object[] { options.frameHistoryCapacity });
            report.loaded = ProfilerDriver.LoadProfile(profile, false);
            if (!report.loaded)
                throw new InvalidDataException("Unity rejected the binary profile");
            report.loadedFirstFrame = ProfilerDriver.firstFrameIndex;
            report.loadedLastFrame = ProfilerDriver.lastFrameIndex;
            report.loadHistoryStartsAtZero = report.loadedFirstFrame == 0;
            report.loadedHistoryAtRequestedCapacity =
                (long)report.loadedLastFrame - report.loadedFirstFrame + 1 >=
                options.frameHistoryCapacity;
            if (!report.loadHistoryStartsAtZero)
                report.limits.Add("Loaded history begins at nonzero frame " +
                                  report.loadedFirstFrame +
                                  "; a raw-capture prefix is not exposed by this load.");
            if (report.loadedHistoryAtRequestedCapacity)
                report.limits.Add("Loaded history reached requested capacity; raw-capture prefix " +
                                  "may have been discarded.");
            if (report.loadedFirstFrame < 0 || report.loadedLastFrame < report.loadedFirstFrame)
                throw new InvalidDataException("Profile has no frame range");
            var eligible = new List<int>();
            int current = report.loadedFirstFrame, enumerated = 0;
            while (current >= 0 && current <= report.loadedLastFrame) {
                if ((options.firstFrame < 0 || current >= options.firstFrame) &&
                    (options.lastFrame < 0 || current <= options.lastFrame))
                    eligible.Add(current);
                if (current == report.loadedLastFrame)
                    break;
                int next = ProfilerDriver.GetNextFrameIndex(current);
                if (next <= current)
                    throw new InvalidDataException(
                        "Invalid/non-increasing recorded frame index sequence");
                current = next;
                if (++enumerated > 200000)
                    throw new InvalidDataException("Frame index enumeration exceeds safety bound");
            }
            report.eligibleFrames = eligible.Count;
            if (eligible.Count == 0)
                throw new InvalidDataException("Requested range contains no recorded frames");
            var selected = SelectFrames(eligible, options.maxFrames);
            report.selectedFrames = selected.Count;
            foreach (int frameIndex in selected) {
                if (watch.Elapsed.TotalSeconds >= options.maxAnalysisSeconds) {
                    capped = true;
                    report.issues.Add("Analysis wall-time cap reached");
                    break;
                }
                var frame = new Frame { index = frameIndex };
                bool any = false, ended = false;
                int invalidBefore = report.invalidThreadViews;
                for (int threadIndex = 0; threadIndex < options.maxThreads; threadIndex++) {
                    if (watch.Elapsed.TotalSeconds >= options.maxAnalysisSeconds) {
                        capped = true;
                        report.issues.Add("Analysis wall-time cap reached within frame");
                        break;
                    }
                    using (var view = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex)) {
                        if (!view.valid) {
                            ended = true;
                            break;
                        }
                        int count = view.sampleCount;
                        if (count < 0)
                            throw new InvalidDataException("Negative sample count");
                        if (count > options.maxSamplesPerThreadFrame) {
                            capped = true;
                            report.issues.Add("Per-thread-frame sample cap reached");
                            break;
                        }
                        if (report.samplesAnalyzed + count > options.maxSamples) {
                            capped = true;
                            report.issues.Add("Sample-count cap reached");
                            break;
                        }
                        try {
                            var samples = Read(view, count, watch, options.maxAnalysisSeconds);
                            ComputeExclusive(samples);
                            string key = ThreadKey(view, threadIndex);
                            Thread thread;
                            if (!threads.TryGetValue(key, out thread)) {
                                thread = new Thread {
                                    key = key, name = view.threadName, group = view.threadGroupName,
                                    id = view.threadId.ToString(CultureInfo.InvariantCulture)
                                };
                                threads.Add(key, thread);
                            }
                            thread.frameViews++;
                            thread.samples += count;
                            thread.minThreadIndex = Math.Min(thread.minThreadIndex, threadIndex);
                            thread.maxThreadIndex = Math.Max(thread.maxThreadIndex, threadIndex);
                            foreach (var sample in samples) {
                                string markerKey = key + "\u001e" + sample.category + "\u001e" +
                                                   sample.flags + "\u001e" + sample.name;
                                Marker marker;
                                if (!markers.TryGetValue(markerKey, out marker)) {
                                    marker = new Marker { threadKey = key,
                                                          threadName = thread.name,
                                                          name = sample.name,
                                                          category = sample.category,
                                                          flags = sample.flags,
                                                          waitNameHint = WaitHint(sample.name) };
                                    markers.Add(markerKey, marker);
                                }
                                marker.calls++;
                                marker.inclusiveMs += sample.duration;
                                marker.exclusiveMs += sample.self;
                                if (sample.parent < 0)
                                    marker.rootCalls++;
                                marker.maxInclusiveMs =
                                    Math.Max(marker.maxInclusiveMs, sample.duration);
                                if (marker.lastFrame != frameIndex) {
                                    marker.framesSeen++;
                                    marker.lastFrame = frameIndex;
                                }
                            }
                            AggregateAllocations(samples, frameIndex, key, thread.name,
                                                 allocations);
                            if (!any || view.threadName == "Main Thread") {
                                frame.cpuFrameMs = view.frameTimeMs;
                                frame.frameTimeThread = view.threadName;
                            }
                            any = true;
                            frame.threadViews++;
                            frame.samples += count;
                            report.threadViews++;
                            report.samplesAnalyzed += count;
                        } catch (TimeoutException error) {
                            capped = true;
                            report.issues.Add(error.Message);
                            break;
                        } catch (Exception error) {
                            report.invalidThreadViews++;
                            if (report.issues.Count < 30)
                                report.issues.Add("Frame " + frameIndex + " thread " + threadIndex +
                                                  ": " + error.Message);
                        }
                    }
                }
                if (!ended && !capped) {
                    capped = true;
                    report.issues.Add("Thread-index cap reached on frame " + frameIndex);
                }
                frame.invalidThreadViews = report.invalidThreadViews - invalidBefore;
                frame.complete = ended && !capped && frame.invalidThreadViews == 0 && any;
                if (any) {
                    report.frames.Add(frame);
                    report.analyzedFrames++;
                    if (frame.complete)
                        report.completeFrames++;
                    else
                        report.partialFrames++;
                } else {
                    report.framesWithoutData++;
                    if (report.issues.Count < 30)
                        report.issues.Add("Frame " + frameIndex +
                                          " has no successfully read thread views");
                }
                if (capped)
                    break;
            }
        } catch (Exception error) {
            report.issues.Add(error.ToString());
        } finally {
            if (historySetter != null && report.previousEditorHistorySetting > 0) {
                try {
                    historySetter.Invoke(null,
                                         new object[] { report.previousEditorHistorySetting });
                    report.historyCapacityRestored = true;
                } catch (Exception error) {
                    report.issues.Add(
                        "Failed to restore this analysis Editor's history capacity: " + error);
                }
            }
        }
        foreach (var thread in threads.Values) {
            var values = markers.Values.Where(m => m.threadKey == thread.key);
            thread.topInclusive =
                values.OrderByDescending(m => m.inclusiveMs).Take(options.topMarkers).ToList();
            thread.topExclusive =
                values.OrderByDescending(m => m.exclusiveMs).Take(options.topMarkers).ToList();
            thread.topNamedInclusive = values.Where(NamedNonRoot)
                                           .OrderByDescending(m => m.inclusiveMs)
                                           .Take(options.topMarkers)
                                           .ToList();
            thread.topNamedNonWaitExclusive = values.Where(m => NamedNonRoot(m) && !m.waitNameHint)
                                                  .OrderByDescending(m => m.exclusiveMs)
                                                  .Take(options.topMarkers)
                                                  .ToList();
            thread.topNamedWaitExclusive = values.Where(m => NamedNonRoot(m) && m.waitNameHint)
                                               .OrderByDescending(m => m.exclusiveMs)
                                               .Take(options.topMarkers)
                                               .ToList();
        }
        report.threads = threads.Values
                             .OrderBy(t => t.name == "Main Thread"     ? 0
                                           : t.name == "Render Thread" ? 1
                                                                       : 2)
                             .ThenBy(t => t.group)
                             .ThenBy(t => t.name)
                             .ToList();
        report.topInclusive =
            markers.Values.OrderByDescending(m => m.inclusiveMs).Take(options.topMarkers).ToList();
        report.topExclusive =
            markers.Values.OrderByDescending(m => m.exclusiveMs).Take(options.topMarkers).ToList();
        report.rootOrUnnamedMarkers = markers.Values.Where(m => !NamedNonRoot(m))
                                          .OrderByDescending(m => m.exclusiveMs)
                                          .ToList();
        report.topNamedInclusive = markers.Values.Where(NamedNonRoot)
                                       .OrderByDescending(m => m.inclusiveMs)
                                       .Take(options.topMarkers)
                                       .ToList();
        report.topNamedNonWaitExclusive =
            markers.Values.Where(m => NamedNonRoot(m) && !m.waitNameHint)
                .OrderByDescending(m => m.exclusiveMs)
                .Take(options.topMarkers)
                .ToList();
        report.topNamedWaitExclusive = markers.Values.Where(m => NamedNonRoot(m) && m.waitNameHint)
                                           .OrderByDescending(m => m.exclusiveMs)
                                           .Take(options.topMarkers)
                                           .ToList();
        report.allocationSamples = allocations.Values.Sum(a => a.calls);
        report.allocationSamplesWithBytes = allocations.Values.Sum(a => a.bytesKnownCalls);
        report.allocatedBytesFromSamples = allocations.Values.Sum(a => a.allocatedBytes);
        report.topAllocationParents = allocations.Values.OrderByDescending(a => a.allocatedBytes)
                                          .ThenByDescending(a => a.calls)
                                          .Take(options.topMarkers)
                                          .ToList();
        report.recordedManagedNameExamples =
            markers.Values
                .Where(m => m.name.Contains("FistVR.") || m.name.Contains("NativeH3VR.") ||
                            m.name.Contains("OptiH3VR."))
                .Select(m => m.name)
                .Distinct()
                .Take(30)
                .ToList();
        var durations = report.frames.Select(f => f.cpuFrameMs)
                            .Where(v => Finite(v) && v > 0)
                            .OrderBy(v => v)
                            .ToArray();
        if (report.analyzedFrames == 0 || report.samplesAnalyzed == 0)
            report.issues.Add("No analyzable CPU frame/sample data");
        if (durations.Length != report.analyzedFrames)
            report.issues.Add(
                "Some analyzed frames have absent/nonpositive/nonfinite CPU frame duration");
        if (durations.Length > 0) {
            report.meanFrameMs = durations.Average();
            report.medianFrameMs = Median(durations);
            report.p95FrameMs = durations[(int)Math.Ceiling(durations.Length * .95) - 1];
            report.maxFrameMs = durations[durations.Length - 1];
        }
        file.Refresh();
        report.inputUnchanged =
            file.Length == originalLength && file.LastWriteTimeUtc == originalWrite;
        if (!report.inputUnchanged)
            report.issues.Add(
                "Raw profile changed during analysis; results are not stable evidence");
        report.analysisSeconds = watch.Elapsed.TotalSeconds;
        report.valid = report.loaded && report.analyzedFrames > 0 && durations.Length > 0 &&
                       report.invalidThreadViews == 0 && report.inputUnchanged &&
                       report.issues.Count == 0;
        report.capped = capped;
        report.complete = report.valid && !capped && report.completeFrames == report.selectedFrames;
        report.fullLoadedHistoryAnalyzed =
            report.complete && report.selectedFrames == report.eligibleFrames &&
            (options.firstFrame < 0 || options.firstFrame <= report.loadedFirstFrame) &&
            (options.lastFrame < 0 || options.lastFrame >= report.loadedLastFrame);
        report.fullCaptureAnalyzed = false;
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, JsonUtility.ToJson(report, true) + "\n");
        Debug.Log("NEWENGINEH3VR_PROFILE_ANALYSIS valid=" + report.valid +
                  " complete=" + report.complete + " frames=" + report.analyzedFrames +
                  " threads=" + report.threads.Count + " samples=" + report.samplesAnalyzed +
                  " output=" + output);
        return output;
    }
    static Sample[] Read(RawFrameDataView view, int count, Stopwatch watch, double maximumSeconds) {
        var values = new Sample[count];
        var categories = new Dictionary<ushort, string>();
        for (int i = 0; i < count; i++) {
            if ((i & 4095) == 0 && watch.Elapsed.TotalSeconds >= maximumSeconds)
                throw new TimeoutException(
                    "Analysis wall-time cap reached while reading a sample slice");
            // Local Unity2021 XML explicitly documents these APIs in ms.
            double duration = view.GetSampleTimeMs(i), start = view.GetSampleStartTimeMs(i);
            if (!Finite(duration) || duration < 0 || !Finite(start))
                throw new InvalidDataException("Invalid sample interval");
            ushort categoryId = view.GetSampleCategoryIndex(i);
            string category;
            if (!categories.TryGetValue(categoryId, out category)) {
                try {
                    category = categoryId + ":" + view.GetCategoryInfo(categoryId).name;
                } catch {
                    category = categoryId + ":<unavailable-name>";
                }
                categories.Add(categoryId, category);
            }
            string name = view.GetSampleName(i) ?? "<unnamed>";
            long bytes = -1;
            if (name == "GC.Alloc" && view.GetSampleMetadataCount(i) > 0) {
                var metadata = view.GetMarkerMetadataInfo(view.GetSampleMarkerId(i));
                for (int m = 0; m < metadata.Length && m < view.GetSampleMetadataCount(i); m++)
                    if (metadata[m].unit.ToString() == "Bytes" &&
                        metadata[m].type.ToString() == "Int64") {
                        long value = view.GetSampleMetadataAsLong(i, m);
                        if (value >= 0)
                            bytes = value;
                        break;
                    }
            }
            values[i] = new Sample { name = name,
                                     duration = duration,
                                     start = start,
                                     allocationBytes = bytes,
                                     children = view.GetSampleChildrenCount(i),
                                     category = category,
                                     flags = view.GetSampleFlags(i).ToString() };
        }
        return values;
    }
    static void ComputeExclusive(Sample[] samples) {
        var stack = new Stack<Parent>();
        for (int i = 0; i < samples.Length; i++) {
            var sample = samples[i];
            if (sample.children < 0)
                throw new InvalidDataException("Negative direct-child count");
            sample.self = sample.duration;
            while (stack.Count > 0 && stack.Peek().remaining == 0)
                stack.Pop();
            if (stack.Count > 0) {
                var parent = stack.Peek();
                parent.remaining--;
                var enclosing = samples[parent.index];
                sample.parent = parent.index;
                if (sample.start < enclosing.start - .05 ||
                    sample.start + sample.duration > enclosing.start + enclosing.duration + .05)
                    throw new InvalidDataException(
                        "Raw preorder child lies outside parent interval");
                if (sample.start < parent.lastChildEnd - .05)
                    throw new InvalidDataException(
                        "Raw preorder direct siblings overlap or are out of order");
                parent.lastChildEnd = sample.start + sample.duration;
                enclosing.self -= sample.duration;
            }
            if (sample.children > 0)
                stack.Push(new Parent { index = i, remaining = sample.children });
        }
        if (stack.Any(p => p.remaining != 0))
            throw new InvalidDataException("Raw preorder hierarchy has missing child samples");
        foreach (var sample in samples) {
            if (sample.self < -.05)
                throw new InvalidDataException("Direct-child durations exceed parent duration");
            sample.self = Math.Max(0, sample.self);
        }
    }
    static void AggregateAllocations(Sample[] samples, int frame, string threadKey,
                                     string threadName,
                                     Dictionary<string, AllocationParent> groups) {
        for (int i = 0; i < samples.Length; i++) {
            var sample = samples[i];
            if (sample.name != "GC.Alloc")
                continue;
            var path = new List<string>();
            int ancestor = sample.parent, depth = 0;
            while (ancestor >= 0 && depth++ < 32) {
                path.Add(samples[ancestor].name);
                ancestor = samples[ancestor].parent;
            }
            if (ancestor >= 0)
                path.Add("<earlier recorded ancestors truncated>");
            path.Reverse();
            string ancestry = string.Join(" > ", path.ToArray()),
                   parent =
                       sample.parent >= 0 ? samples[sample.parent].name : "<no recorded parent>";
            string key = threadKey + "\u001e" + ancestry;
            AllocationParent group;
            if (!groups.TryGetValue(key, out group)) {
                group =
                    new AllocationParent { threadKey = threadKey, threadName = threadName,
                                           parentName = parent, recordedAncestorPath = ancestry };
                groups.Add(key, group);
            }
            group.calls++;
            group.allocationMarkerMs += sample.duration;
            if (sample.allocationBytes >= 0) {
                group.bytesKnownCalls++;
                group.allocatedBytes += sample.allocationBytes;
            }
            if (group.lastFrame != frame) {
                group.framesSeen++;
                group.lastFrame = frame;
            }
        }
    }
    static List<int> SelectFrames(List<int> frames, int maximum) {
        int count = Math.Min(maximum, frames.Count);
        var selected = new List<int>(count);
        for (int i = 0; i < count; i++)
            selected.Add(frames[count == 1 ? frames.Count / 2
                                           : (int)((long)i * (frames.Count - 1) / (count - 1))]);
        return selected;
    }
    static string ThreadKey(RawFrameDataView view, int index) {
        return view.threadGroupName + "/" + view.threadName + "/" +
               (view.threadId == FrameDataView.invalidThreadId
                    ? "index:" + index
                    : view.threadId.ToString(CultureInfo.InvariantCulture));
    }
    static bool WaitHint(string name) {
        return new[] { "wait", "idle", "sleep", "semaphore", "present" }.Any(
            word => name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
    }
    static bool NamedNonRoot(Marker marker) {
        return marker.rootCalls == 0 && marker.name != "<unnamed>" &&
               marker.name != marker.threadName;
    }
    static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    static double Median(double[] sorted) {
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }
    static string Hash(string path) {
        using (var stream = File.OpenRead(path)) using (
            var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream))
            .Replace("-", "")
            .ToLowerInvariant();
    }
    public static void AnalyzeFromEnvironment() {
        string input = Environment.GetEnvironmentVariable("NEWENGINEH3VR_PROFILE_INPUT"),
               output = Environment.GetEnvironmentVariable("NEWENGINEH3VR_PROFILE_ANALYSIS");
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output))
            throw new ArgumentException("Set NEWENGINEH3VR_PROFILE_INPUT and " +
                                        "NEWENGINEH3VR_PROFILE_ANALYSIS to absolute paths");
        if (!Path.IsPathRooted(input) || !Path.IsPathRooted(output))
            throw new ArgumentException("Profile input/output must be absolute paths");
        Analyze(input, output);
    }
    public static string RunAggregationSelfTest() {
        // Nested/repeated names are kept separate as calls, and direct-child
        // subtraction must not subtract grandchildren twice.
        var values = new[] { new Sample { name = "root", start = 0, duration = 20, children = 2 },
                             new Sample { name = "repeat", start = 1, duration = 10, children = 1 },
                             new Sample { name = "repeat", start = 3, duration = 4, children = 0 },
                             new Sample { name = "wait", start = 12, duration = 5, children = 0 } };
        ComputeExclusive(values);
        if (values[0].self != 5 || values[1].self != 6 || values[2].self != 4 ||
            values[3].self != 5 || values.Sum(v => v.self) != 20)
            throw new Exception("Nested exclusive-time conservation failed");
        bool rejected = false;
        try {
            ComputeExclusive(new[] { new Sample { start = 0, duration = 1, children = 1 } });
        } catch (InvalidDataException) {
            rejected = true;
        }
        if (!rejected)
            throw new Exception("Truncated hierarchy was accepted");
        rejected = false;
        try {
            ComputeExclusive(new[] { new Sample { start = 0, duration = 2, children = 1 },
                                     new Sample { start = 1, duration = 3 } });
        } catch (InvalidDataException) {
            rejected = true;
        }
        if (!rejected)
            throw new Exception("Outside-parent child was accepted");
        rejected = false;
        try {
            ComputeExclusive(new[] { new Sample { start = 0, duration = 10, children = 2 },
                                     new Sample { start = 1, duration = 4 },
                                     new Sample { start = 4, duration = 3 } });
        } catch (InvalidDataException) {
            rejected = true;
        }
        if (!rejected)
            throw new Exception("Overlapping direct siblings were accepted");
        var chosen = SelectFrames(Enumerable.Range(10, 11).ToList(), 4);
        if (!chosen.SequenceEqual(new[] { 10, 13, 16, 20 }))
            throw new Exception("Bounded frame selection lost range coverage");
        var allocationSamples =
            new[] { new Sample { name = "recorded parent", start = 0, duration = 3, children = 1 },
                    new Sample { name = "GC.Alloc", start = 1, duration = 1,
                                 allocationBytes = 64 } };
        ComputeExclusive(allocationSamples);
        var allocationGroups = new Dictionary<string, AllocationParent>();
        AggregateAllocations(allocationSamples, 10, "main", "Main Thread", allocationGroups);
        var allocation = allocationGroups.Values.Single();
        if (allocation.parentName != "recorded parent" || allocation.allocatedBytes != 64 ||
            allocation.bytesKnownCalls != 1 || allocation.calls != 1)
            throw new Exception("Recorded allocation-parent/byte attribution failed");
        return "Nested exclusive conservation, hierarchy rejection, bounded sampling and " +
               "recorded allocation-parent/byte attribution passed";
    }
}
}
