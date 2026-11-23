using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Valve.VR;
using Valve.Newtonsoft.Json;

namespace NativeH3VR.QA
{
    public sealed partial class GraphicsProfileWitness : MonoBehaviour
    {
        private NativeStereoRenderer stereo;
        private string output;
        private bool builtIn;
        private bool headset;
        private int profilePairs;

        public static void Initialize()
        {
            var host = new GameObject("Graphics profile witness");
            DontDestroyOnLoad(host);
            host.AddComponent<GraphicsProfileWitness>();
        }

        private IEnumerator Start()
        {
            output = Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_WITNESS");
            if (string.IsNullOrEmpty(output))
                yield break;
            builtIn = Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_BUILTIN") == "1";
            headset = Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_HEADSET") == "1";
            bool takeAndHold = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_TNH"));
            if (!int.TryParse(Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_PAIRS"), out profilePairs) || profilePairs < 100)
                profilePairs = takeAndHold ? 270 : 100;

            var pending = new Stack<IEnumerator>();
            pending.Push(Run());
            while (pending.Count > 0)
            {
                object next;
                try
                {
                    var routine = pending.Peek();
                    if (!routine.MoveNext())
                    {
                        pending.Pop();
                        continue;
                    }
                    next = routine.Current;
                    if (next is IEnumerator child && !(next is CustomYieldInstruction))
                    {
                        pending.Push(child);
                        continue;
                    }
                }
                catch (Exception error)
                {
                    while (pending.Count > 0)
                        (pending.Pop() as IDisposable)?.Dispose();
                    File.WriteAllText(Path.Combine(output, "profile-failed.txt"), error.ToString());
                    Debug.LogError("GRAPHICS_PROFILE_WITNESS_FAILED " + error);
                    Application.Quit(2);
                    yield break;
                }
                yield return next;
            }
        }

        private IEnumerator Run()
        {
            yield return WaitForScene("MainMenu3");
            string map = Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_TNH");
            if (Environment.GetEnvironmentVariable("NATIVEH3VR_AIMED_OPTICS_WITNESS") == "1")
                yield return WitnessAimedOptics();
            else if (Environment.GetEnvironmentVariable("NATIVEH3VR_SHADOW_MOTION_WITNESS") == "1")
                yield return WitnessShadowMotion();
            else if (string.IsNullOrEmpty(map))
            {
                yield return ProfileScene("MainMenu3");
                string extraScenes = Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_SCENE") ?? "";
                foreach (string extraScene in extraScenes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (extraScene == "MainMenu3") continue;
                    Require(Application.CanStreamedLevelBeLoaded(extraScene), "Unavailable stock scene " + extraScene);
                    SteamVR_LoadLevel.Begin(extraScene);
                    yield return WaitForScene(extraScene);
                    yield return ProfileScene(extraScene);
                }
            }
            else
                yield return ProfileTakeAndHold(map);
            File.WriteAllText(Path.Combine(output, "profile-complete.txt"), "Graphics profile witness passed");
            Application.Quit();
        }

        private IEnumerator WaitForScene(string name)
        {
            float deadline = Time.realtimeSinceStartup + 100;
            do
            {
                Require(Time.realtimeSinceStartup < deadline, "Waiting for the submitted pair in " + name);
                yield return null;
                stereo = NativeStereoRenderer.Instance;
            } while (SceneManager.GetActiveScene().name != name || SteamVR_LoadLevel.loading || !GM.CurrentPlayerBody ||
                     !stereo || stereo.CompletedFrames < 12 || stereo.SourceCamera != GM.CurrentPlayerBody.EyeCam);
            yield return new WaitForSecondsRealtime(5f);
        }

        private IEnumerator ProfileScene(string scene)
        {
            float previousTimeScale = Time.timeScale;
            Time.timeScale = 0;
            try
            {
                if (builtIn)
                {
                    Require(!GraphicsSettings.currentRenderPipeline, "Expected a Built-in pipeline for the baseline");
                    yield return Sample(scene, "stock", true);
                    yield return Sample(scene, "stock-repeat", false);
                    yield break;
                }

                if (scene == "WarehouseRange_Rebuilt")
                {
                    VerifyWarehouseReceiver("Warehouse_ShootingRangeGround", "Ground", 0);
                    VerifyWarehouseReceiver("ShootingRangePhys", "concrete_concreteblockmetalcrossbeamsold_2k_d", 1);
                }

                // Load the API by name so the same witness binary can benchmark the stock player.
                var options = new GraphicsPresetControls();
                string[] names = { "balanced", "quality", "ultra" };
                for (int i = 0; i < names.Length; i++)
                {
                    options.Set("Preset", i);
                    yield return Sample(scene, names[i], true, i);
                    if (i == 0)
                        yield return Sample(scene, "balanced-repeat", false);
                }

                options.Set("Preset", 1);
                if (scene == "WarehouseRange_Rebuilt" || scene == "Friendly45_New")
                {
                    int highShadows = options.Index("ShadowQuality");
                    options.Set("ShadowQuality", 0);
                    yield return Sample(scene, "quality-shadows-off", false);
                    options.Set("ShadowQuality", highShadows);
                }

                int exposure = options.Index("Exposure");
                options.Set("Exposure", exposure + 1);
                yield return Sample(scene, "quality-positive-exposure", false);
                options.Set("Exposure", exposure);

                // Hold scale, shadows, lights, AA, and HDR fixed while isolating the image effects.
                foreach (string effect in new[] { "Bloom", "Sharpening", "Tonemapping", "AmbientOcclusion" })
                    options.Set(effect, 0);
                yield return Sample(scene, "quality-effects-off", false);
            }
            finally
            {
                Time.timeScale = previousTimeScale;
            }
        }

        private IEnumerator Sample(string scene, string label, bool profile, int preset = -1,
            Action<string> recordState = null)
        {
            string name = scene + "-" + label;
            yield return new WaitForSecondsRealtime(profile ? 2f : 0.7f);
            var effectsBefore = preset >= 0 ? GraphicsPresetControls.ReadEffects() : default;
            var historyBefore = preset == 2 ? GraphicsPresetControls.ReadTemporalHistories() : null;
            var characterPass = preset == 2 ? Type.GetType(
                "NativeH3VR.GraphicsUpgrade.H3VRCharacterShadowPass, NativeH3VR.GraphicsUpgrade") : null;
            var drawGetter = characterPass?.GetProperty("LastDrawCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetGetMethod(true);
            var readShadowDraws = drawGetter == null ? null : (Func<int>)drawGetter.CreateDelegate(typeof(Func<int>));
            int shadowSamples = 0, activeShadowSamples = 0, peakShadowDraws = 0;

            var latest = profile && FrameTimingManager.IsFeatureEnabled() ? new FrameTiming[1] : null;
            // The manual stereo bridge can run several engine updates per submitted eye pair.
            var gpuFrames = latest == null ? null :
                new List<(int frame, ulong timestamp, double duration)>(Math.Min(profilePairs, 1024) * 8);
            ulong previousTimestamp = 0;
            recordState?.Invoke("begin");
            if (headset) RecordHardwareSample(name, "begin");
            int firstFrame = Time.frameCount + 1;
            ulong start = stereo.CompletedFrames;
            ulong lastSampledPair = start;
            // Keep the full frame sample even when a live scene takes longer to submit it.
            float minimumTimeout = 120f;
            float deadline = Time.realtimeSinceStartup + (profile ? Mathf.Max(minimumTimeout, profilePairs / 10f) : 25f);
            int count = profile ? profilePairs : 10;
            while (stereo.CompletedFrames < start + (ulong)count)
            {
                if (Time.realtimeSinceStartup >= deadline)
                    throw new InvalidOperationException(
                        $"Submitted {stereo.CompletedFrames - start}/{count} stereo pairs before deadline for {name}");
                if (readShadowDraws != null && stereo.CompletedFrames != lastSampledPair)
                {
                    lastSampledPair = stereo.CompletedFrames;
                    int draws = readShadowDraws();
                    shadowSamples++;
                    if (draws > 0) activeShadowSamples++;
                    peakShadowDraws = Math.Max(peakShadowDraws, draws);
                }
                if (latest != null)
                {
                    FrameTimingManager.CaptureFrameTimings();
                    // Unity publishes these asynchronously. Discard the window boundary and repeated reports.
                    if (Time.frameCount >= firstFrame + 16 && FrameTimingManager.GetLatestTimings(1, latest) == 1 &&
                        latest[0].frameStartTimestamp > previousTimestamp && latest[0].gpuFrameTime > 0)
                    {
                        previousTimestamp = latest[0].frameStartTimestamp;
                        gpuFrames.Add((Time.frameCount, previousTimestamp, latest[0].gpuFrameTime));
                    }
                }
                yield return null;
            }
            int lastFrame = Time.frameCount - 1;
            recordState?.Invoke("end");
            if (profile)
            {
                File.WriteAllText(Path.Combine(output, name + "-frames.txt"), firstFrame + " " + lastFrame);
                if (readShadowDraws != null)
                    File.WriteAllText(Path.Combine(output, name + "-character-shadows.json"),
                        JsonConvert.SerializeObject(new { shadowSamples, activeShadowSamples, peakShadowDraws }));
                using var timings = File.CreateText(Path.Combine(output, name + "-unity-gpu.csv"));
                timings.WriteLine("observed_unity_frame,frame_start_timestamp,unity_gpu_frame_ms");
                if (gpuFrames != null)
                    foreach (var (frame, timestamp, duration) in gpuFrames)
                        timings.WriteLine(FormattableString.Invariant($"{frame},{timestamp},{duration:R}"));
            }

            if (headset) RecordHardwareSample(name, "end");
            else VerifyControllers(name);
            yield return new WaitForEndOfFrame();
            Require(!stereo.SuspendSceneRendering, "Scene rendering is suspended for " + name);
            if (preset >= 0)
                GraphicsPresetControls.CheckUrp(preset, stereo, Path.Combine(output, name + "-asset.txt"),
                    Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_EYE_SIZE"), effectsBefore, historyBefore);
            var position = stereo.TrackingOrigin.TransformPoint(stereo.LatestFrame.head.Position);
            var rotation = stereo.TrackingOrigin.rotation * stereo.LatestFrame.head.Rotation;
            File.WriteAllText(Path.Combine(output, name + "-pose.txt"), string.Join(" ", new[]
            {
                Number(position.x), Number(position.y), Number(position.z),
                Number(rotation.x), Number(rotation.y), Number(rotation.z), Number(rotation.w)
            }));

            var previous = RenderTexture.active;
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var eye = stereo.EyeTexture(i);
                    Require(eye && eye.IsCreated(), "Missing eye target for " + name);
                    RenderTexture.active = eye;
                    var copy = new Texture2D(eye.width, eye.height, TextureFormat.RGB24, false);
                    try
                    {
                        copy.ReadPixels(new Rect(0, 0, eye.width, eye.height), 0, 0, false);
                        copy.Apply(false, false);
                        File.WriteAllBytes(Path.Combine(output, name + (i == 0 ? "-left.png" : "-right.png")),
                            copy.EncodeToPNG());
                    }
                    finally
                    {
                        Destroy(copy);
                    }
                }
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static void VerifyWarehouseReceiver(string objectName, string materialName, int detailUV)
        {
            var receiver = GameObject.Find(objectName);
            var renderer = receiver ? receiver.GetComponent<Renderer>() : null;
            Require(renderer, "Missing Warehouse receiver " + objectName);
            var material = Array.Find(renderer.sharedMaterials,
                candidate => candidate && candidate.name == materialName);
            Require(material && material.shader.name == "NativeH3VR/Stock PBR" &&
                    material.GetFloat("_DetailT") == 1 && material.GetFloat("_DetailAlbedoMapUV") == detailUV &&
                    material.GetTexture("_DetailAlbedoMap"),
                "Warehouse receiver lost its native lighting or stock detail layer: " + materialName);
        }

        private void RecordHardwareSample(string sample, string edge)
        {
            int status = NativeTracking.Read(out var tracking);
            var frame = stereo.LatestFrame;
            var hands = FindObjectsByType<FVRViveHand>();
            var devices = new List<object>(3);
            for (uint role = 1; role <= 3; role++)
            {
                var device = tracking.Role(role);
                var source = role == 2 ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
                var hand = role != 1 ? Array.Find(hands, item => item.HandSource == source && item.Pose != null) : null;
                var pose = hand ? hand.Pose[source] : null;
                devices.Add(new {
                    role, device.name, device.flags, device.buttons, device.touches,
                    connected = (device.flags & NativeTracking.Connected) != 0,
                    tracked = (device.flags & (NativeTracking.PoseValid | NativeTracking.PoseTracked)) ==
                              (NativeTracking.PoseValid | NativeTracking.PoseTracked),
                    inputCurrent = (device.flags & NativeTracking.InputCurrent) != 0,
                    stockPoseActive = pose?.active, stockPoseValid = pose?.poseIsValid,
                    stockDeviceIndex = pose?.trackedDeviceIndex,
                });
            }
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            int playerPid = process.Id;
            int? homeActivePid = HomeActivePlayer();
            File.WriteAllText(Path.Combine(output, sample + "-hardware-" + edge + ".json"),
                JsonConvert.SerializeObject(new { status, frame.displayPeriod, frame.focused,
                    completedPairs = stereo.CompletedFrames, realtimeSeconds = Time.realtimeSinceStartupAsDouble,
                    playerPid, homeActivePid, homeOwnsPlayerView = homeActivePid == playerPid,
                    devices }, Formatting.Indented));
        }

        private static int? HomeActivePlayer()
        {
            string control = Environment.GetEnvironmentVariable("VI_HOME_CONTROL_DIR");
            if (string.IsNullOrEmpty(control))
            {
                string state = Environment.GetEnvironmentVariable("VR_STATE_DIR");
                if (string.IsNullOrEmpty(state))
                    return null;
                control = Path.Combine(state, "home");
            }

            // Read the owner's status without writing the shared settings.
            try
            {
                string[] status = File.ReadAllLines(Path.Combine(control, "runtime.txt"));
                if (!Array.Exists(status, line => line == "mode=app"))
                    return null;
                const string prefix = "active_pid=";
                string activePid = Array.Find(status, line => line.StartsWith(prefix, StringComparison.Ordinal));
                return activePid != null && int.TryParse(activePid.Substring(prefix.Length), out int pid) && pid > 0
                    ? pid : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private void VerifyControllers(string sample)
        {
            Require(NativeTracking.Read(out var tracking) == 0, "Missing MacVR tracking for " + sample);
            var hands = FindObjectsByType<FVRViveHand>();
            foreach (var source in new[] { SteamVR_Input_Sources.LeftHand, SteamVR_Input_Sources.RightHand })
            {
                bool isLeft = source == SteamVR_Input_Sources.LeftHand;
                var device = tracking.Role(isLeft ? 2u : 3u);
                const uint flags = NativeTracking.Connected | NativeTracking.PoseValid |
                    NativeTracking.PoseTracked | NativeTracking.InputCurrent;
                Require(device.name == "Valve Index Controller" && (device.flags & flags) == flags,
                    $"Missing simulated Index tracking for {source} in {sample} (flags {device.flags})");
                var hand = Array.Find(hands, item => item.HandSource == source && item.Pose != null);
                Require(hand, $"Missing stock {source} hand in {sample}");
                var pose = hand.Pose[source];
                Require(pose.active && pose.poseIsValid && pose.trackedDeviceIndex == (isLeft ? 1u : 2u) &&
                        hand.CMode.ToString() == "Index" && hand.DMode.ToString() == "Index",
                    $"Inactive stock Index control for {source} in {sample} " +
                    $"(active={pose.active}, valid={pose.poseIsValid}, index={pose.trackedDeviceIndex}, mode={hand.CMode}/{hand.DMode})");
                File.AppendAllText(Path.Combine(output, "controller-poses.csv"),
                    $"{sample},{source},{device.flags},{pose.trackedDeviceIndex},{hand.CMode},{hand.DMode}\n");
            }
        }

        private static string Number(float number) => number.ToString("R", CultureInfo.InvariantCulture);

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
