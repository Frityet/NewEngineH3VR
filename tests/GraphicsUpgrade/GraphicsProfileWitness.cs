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

namespace NativeH3VR.QA
{
    public sealed partial class GraphicsProfileWitness : MonoBehaviour
    {
        private NativeStereoRenderer stereo;
        private string output;
        private bool builtIn;

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
            if (string.IsNullOrEmpty(map))
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
                var options = Type.GetType("NativeH3VR.GraphicsUpgrade.GraphicsOptions, NativeH3VR.GraphicsUpgrade", true);
                var key = Type.GetType("NativeH3VR.GraphicsUpgrade.GraphicsSetting, NativeH3VR.GraphicsUpgrade", true);
                var set = options.GetMethod("SetIndex");
                var preset = Enum.Parse(key, "Preset");
                string[] names = { "performance", "balanced", "quality" };
                for (int i = 0; i < names.Length; i++)
                {
                    set.Invoke(null, new[] { preset, (object)i });
                    yield return Sample(scene, names[i], true, i);
                    if (i == 1)
                        yield return Sample(scene, "balanced-repeat", false);
                }

                if (scene == "WarehouseRange_Rebuilt" || scene == "Friendly45_New")
                {
                    var shadowQuality = Enum.Parse(key, "ShadowQuality");
                    var highShadows = options.GetMethod("GetIndex").Invoke(null, new[] { shadowQuality });
                    set.Invoke(null, new[] { shadowQuality, (object)0 });
                    yield return Sample(scene, "quality-shadows-off", false);
                    set.Invoke(null, new[] { shadowQuality, highShadows });
                }

                set.Invoke(null, new[] { Enum.Parse(key, "Exposure"), (object)4 });
                yield return Sample(scene, "quality-zero-exposure", false);

                // Hold scale, shadows, lights, AA, and HDR fixed while isolating the image effects.
                foreach (string effect in new[] { "Bloom", "Sharpening", "Tonemapping", "AmbientOcclusion" })
                    set.Invoke(null, new[] { Enum.Parse(key, effect), (object)0 });
                yield return Sample(scene, "quality-effects-off", false);
            }
            finally
            {
                Time.timeScale = previousTimeScale;
            }
        }

        private IEnumerator Sample(string scene, string label, bool profile, int preset = -1,
            int profilePairs = 100, Action<string> recordState = null)
        {
            string name = scene + "-" + label;
            yield return new WaitForSecondsRealtime(profile ? 2f : 0.7f);
            if (preset >= 0)
                SavePipeline(name, preset);

            recordState?.Invoke("begin");
            int firstFrame = Time.frameCount + 1;
            ulong start = stereo.CompletedFrames;
            float deadline = Time.realtimeSinceStartup + 25f;
            int count = profile ? profilePairs : 10;
            while (stereo.CompletedFrames < start + (ulong)count)
            {
                Require(Time.realtimeSinceStartup < deadline, "No submitted stereo interval for " + name);
                yield return null;
            }
            int lastFrame = Time.frameCount - 1;
            recordState?.Invoke("end");
            if (profile)
                File.WriteAllText(Path.Combine(output, name + "-frames.txt"), firstFrame + " " + lastFrame);

            VerifyControllers(name);
            yield return new WaitForEndOfFrame();
            Require(!stereo.SuspendSceneRendering, "Scene rendering is suspended for " + name);
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

        private void SavePipeline(string name, int preset)
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            Require(asset && asset.GetType().FullName == "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset",
                "Active pipeline is not URP");
            object Property(string key) => asset.GetType().GetProperty(key).GetValue(asset, null);
            var scale = (float)Property("renderScale");
            var samples = (int)Property("msaaSampleCount");
            var distance = (float)Property("shadowDistance");
            var shadows = (int)Property("mainLightShadowmapResolution");
            var lights = (int)Property("maxAdditionalLightsCount");
            var hdr = (bool)Property("supportsHDR");
            Require(Mathf.Abs(scale - (preset == 0 ? 0.85f : 1f)) < 0.001f && samples == 1 << preset &&
                    Mathf.Abs(distance - (15 << preset)) < 0.001f && shadows == 512 << preset &&
                    lights == 2 << preset && hdr == (preset != 0),
                name + " did not apply the expected URP settings");
            File.WriteAllLines(Path.Combine(output, name + "-asset.txt"), new[]
            {
                asset.name, "renderScale=" + Number(scale), "msaaSampleCount=" + samples,
                "shadowDistance=" + Number(distance), "mainLightShadowmapResolution=" + shadows,
                "maxAdditionalLightsCount=" + lights, "supportsHDR=" + hdr
            });
        }

        private static string Number(float number) => number.ToString("R", CultureInfo.InvariantCulture);

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
