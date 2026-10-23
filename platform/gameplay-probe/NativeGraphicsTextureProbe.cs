using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using NativeH3VR;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Dedicated synthetic resource-lifetime test. Include/invoke explicitly after
// camera handoff, outside any measured window; it adds render-thread sync calls.
internal static class NativeGraphicsTextureProbe {
    [Serializable]
    sealed class Step {
        public int eye, width, height, textureInstanceId;
        public int pointerQueriesBefore, pointerQueriesAfter, synchronizationPointerCalls;
        public ulong stereoBefore, stereoAfter;
        public bool released, recreated, sameTextureWrapper, cameraRetained, passed;
    }
    [Serializable]
    sealed class Evidence {
        public string schema = "newengineh3vr-eye-texture-recreate-1", runId, scene, failure;
        public string semantics = "Synthetic Release/recreation of renderer-owned eyes; external " +
                                  "pointer sync calls excluded from renderer PointerQueries. Not " +
                                  "a frame benchmark or physical-headset proof.";
        public int pid, rendererInstanceId, cameraInstanceId, originInstanceId;
        public bool cacheEnabled, passed;
        public int stablePointerQueriesBefore, stablePointerQueriesAfter;
        public ulong stableStereoBefore, stableStereoAfter;
        public Step[] steps = new Step[2];
    }
    static readonly FieldInfo texturesField =
        typeof(NativeStereoRenderer)
            .GetField("textures", BindingFlags.Instance | BindingFlags.NonPublic);
    const double TimeoutSeconds = 12;

    internal static IEnumerator Run(NativeStereoRenderer renderer) {
        if (Environment.GetEnvironmentVariable("NEWENGINEH3VR_GRAPHICS_RECREATE_TEST") != "1")
            throw new InvalidOperationException(
                "Eye resource test requires explicit NEWENGINEH3VR_GRAPHICS_RECREATE_TEST=1");
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") != "synthetic" ||
            renderer == null || !renderer.syntheticHeadless)
            throw new InvalidOperationException(
                "Eye resource test requires an isolated synthetic renderer");
        string output = Environment.GetEnvironmentVariable("NEWENGINEH3VR_GRAPHICS_TEST_OUTPUT");
        if (!string.IsNullOrEmpty(output) && !Path.IsPathRooted(output))
            throw new ArgumentException("Graphics test evidence path must be absolute");
        string runId = Environment.GetEnvironmentVariable("NATIVEH3VR_RUN_ID");
        if (string.IsNullOrEmpty(runId))
            throw new InvalidOperationException("Eye resource test requires a correlated run ID");
        var report = new Evidence { runId = runId, pid = Process.GetCurrentProcess().Id,
                                    rendererInstanceId = renderer.GetInstanceID(),
                                    cacheEnabled = NativeEyeTextureDiagnostics.CacheEnabled };
        Write(output, report); // Replace stale success before any mutation.
        var routine = RunChecks(renderer, report);
        while (true) {
            bool next = false;
            Exception failure = null;
            try {
                next = routine.MoveNext();
            } catch (Exception error) {
                failure = error;
            }
            if (failure != null) {
                report.failure = failure.ToString();
                Write(output, report);
                Debug.LogError("NATIVE_GRAPHICS_TEXTURE_TEST_FAIL " + JsonUtility.ToJson(report));
                throw new InvalidOperationException("Synthetic eye texture recreation test failed",
                                                    failure);
            }
            if (!next)
                break;
            yield return routine.Current;
        }
        report.passed = true;
        Write(output, report);
        Debug.Log("NATIVE_GRAPHICS_TEXTURE_TEST_PASS " + JsonUtility.ToJson(report));
    }

    static IEnumerator RunChecks(NativeStereoRenderer renderer, Evidence report) {
        if (texturesField == null)
            throw new MissingFieldException("NativeStereoRenderer.textures");
        var camera = renderer.sourceCamera;
        var origin = renderer.trackingOrigin;
        Check(renderer, camera, origin);
        var original = (RenderTexture[])Textures(renderer).Clone();
        foreach (var texture in original)
            if (!texture.IsCreated())
                throw new InvalidOperationException(
                    "Eye texture test began before real texture creation");
        if (NativeEyeTextureDiagnostics.PointerQueries < 2)
            throw new InvalidOperationException("Eye pointers were not initialized");
        report.cameraInstanceId = camera.GetInstanceID();
        report.originInstanceId = origin.GetInstanceID();
        report.scene = camera.gameObject.scene.name;

        // Check steady retention before releasing either texture. Cache-off runs
        // must still query pointers while cache-on runs must retain them.
        report.stablePointerQueriesBefore = NativeEyeTextureDiagnostics.PointerQueries;
        ulong completed;
        XRBridge.Check(XRBridge.nh3vr_xr_status(out completed));
        report.stableStereoBefore = completed;
        long deadline = Deadline();
        while (completed < report.stableStereoBefore + 3) {
            Check(renderer, camera, origin);
            CheckDeadline(deadline, "steady stereo observation");
            yield return null;
            XRBridge.Check(XRBridge.nh3vr_xr_status(out completed));
        }
        report.stableStereoAfter = completed;
        report.stablePointerQueriesAfter = NativeEyeTextureDiagnostics.PointerQueries;
        int stableDelta = report.stablePointerQueriesAfter - report.stablePointerQueriesBefore;
        if (report.cacheEnabled ? stableDelta != 0 : stableDelta < 4)
            throw new InvalidOperationException(
                "Steady eye pointer-query behavior differs from the selected cache mode: " +
                stableDelta);

        for (int eye = 0; eye < 2; eye++) {
            var step = new Step { eye = eye };
            report.steps[eye] = step;
            var texture = Textures(renderer)[eye];
            if (!ReferenceEquals(texture, original[eye]))
                throw new InvalidOperationException("Eye wrapper changed before its release test");
            step.width = texture.width;
            step.height = texture.height;
            step.textureInstanceId = texture.GetInstanceID();
            deadline = Deadline();
            while (true) {
                Check(renderer, camera, origin);
                CheckDeadline(deadline, "render-thread/GPU drain");
                var eyes = Textures(renderer);
                // GetNativeTexturePtr synchronizes Unity's rendering thread.
                // Without this, bridge status=0 could precede execution of the
                // previous EOF's queued plugin event (active, not pending yet).
                // No yield occurs between this flush, status=0 and Release;
                // the renderer starts frames only from its later EOF coroutine.
                for (int index = 0; index < 2; index++) {
                    if (!eyes[index].IsCreated())
                        throw new InvalidOperationException(
                            "An eye resource vanished before the safe release boundary");
                    if (eyes[index].GetNativeTexturePtr() == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "Eye synchronization returned a null native resource");
                    step.synchronizationPointerCalls++;
                }
                int status = XRBridge.nh3vr_xr_status(out completed);
                XRBridge.Check(status);
                if (status == 0)
                    break;
                if (status != 1)
                    throw new InvalidOperationException(
                        "Unexpected bridge status before eye release: " + status);
                yield return null;
            }
            step.stereoBefore = completed;
            step.pointerQueriesBefore = NativeEyeTextureDiagnostics.PointerQueries;
            texture.Release();
            step.released = !texture.IsCreated();
            if (!step.released)
                throw new InvalidOperationException(
                    "RenderTexture.Release did not release the eye resource");
            Debug.Log("NATIVE_GRAPHICS_TEXTURE_RELEASE eye=" + eye + " runId=" + report.runId +
                      " pid=" + report.pid + " stereoFrames=" + completed);
            // Do not call Create/GetNativeTexturePtr here: the actual renderer
            // must detect loss, recreate and refresh. Mirror/OnGUI stays active,
            // so implicit recreation that bypasses invalidation is not hidden.
            deadline = Deadline();
            do {
                Check(renderer, camera, origin);
                CheckDeadline(deadline, "eye resource recovery");
                yield return null;
                XRBridge.Check(XRBridge.nh3vr_xr_status(out completed));
            } while (completed < step.stereoBefore + 3);
            var current = Textures(renderer)[eye];
            step.recreated = current.IsCreated();
            step.sameTextureWrapper = ReferenceEquals(current, texture);
            step.cameraRetained =
                renderer.sourceCamera == camera && renderer.trackingOrigin == origin;
            step.stereoAfter = completed;
            step.pointerQueriesAfter = NativeEyeTextureDiagnostics.PointerQueries;
            int queryDelta = step.pointerQueriesAfter - step.pointerQueriesBefore;
            if (!step.recreated || !step.sameTextureWrapper || current.width != step.width ||
                current.height != step.height || !step.cameraRetained ||
                (report.cacheEnabled ? queryDelta < 1 : queryDelta < 6))
                throw new InvalidOperationException(
                    "Eye recovery lacks real recreation, pointer refresh, stable " +
                    "dimensions/camera or expected query count: " + JsonUtility.ToJson(step));
            step.passed = true;
            Write(Environment.GetEnvironmentVariable("NEWENGINEH3VR_GRAPHICS_TEST_OUTPUT"), report);
        }
    }
    static RenderTexture[] Textures(NativeStereoRenderer renderer) {
        var eyes = texturesField.GetValue(renderer) as RenderTexture[];
        if (eyes == null || eyes.Length != 2 || eyes[0] == null || eyes[1] == null)
            throw new InvalidOperationException("Renderer does not own exactly two eye targets");
        return eyes;
    }
    static void Check(NativeStereoRenderer renderer, Camera camera, Transform origin) {
        if (renderer == null || camera == null || origin == null ||
            renderer.sourceCamera != camera || renderer.trackingOrigin != origin ||
            renderer.suspendSceneRendering || !renderer.isActiveAndEnabled)
            throw new InvalidOperationException(
                "Eye test lost its active renderer, original camera or tracking origin");
    }
    static long Deadline() {
        return Stopwatch.GetTimestamp() + (long)(TimeoutSeconds * Stopwatch.Frequency);
    }
    static void CheckDeadline(long deadline, string phase) {
        if (Stopwatch.GetTimestamp() >= deadline)
            throw new TimeoutException("Eye texture test timed out during " + phase);
    }
    static void Write(string output, Evidence report) {
        if (string.IsNullOrEmpty(output))
            return;
        string parent = Path.GetDirectoryName(output);
        if (!Directory.Exists(parent))
            Directory.CreateDirectory(parent);
        File.WriteAllText(output, JsonUtility.ToJson(report, true) + "\n");
    }
}
