using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Valve.Newtonsoft.Json;

namespace NativeH3VR.QA
{
    public sealed class RetailHoldWitness : MonoBehaviour
    {
        const string Lobby = "TakeAndHold_Lobby_2";
        const string SceneName = "TakeAndHoldClassic";
        const string Character = "h3vr:Generic_0_BeginnerBlake";
        const int EquipmentSeed = 42;
        const int SimulationSeed = 14796;

        public static void Initialize()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NATIVEH3VR_RETAIL_HOLD")) ||
                FindObjectOfType<RetailHoldWitness>()) return;
            var host = new GameObject("Retail Hold shader witness");
            DontDestroyOnLoad(host);
            host.AddComponent<RetailHoldWitness>();
        }

        private IEnumerator Start()
        {
            string output = Environment.GetEnvironmentVariable("NATIVEH3VR_RETAIL_HOLD");
            Directory.CreateDirectory(output);
            var steps = new Stack<IEnumerator>();
            steps.Push(Capture(output));
            while (steps.Count != 0)
            {
                var step = steps.Peek();
                object value;
                try
                {
                    if (!step.MoveNext()) { steps.Pop(); continue; }
                    value = step.Current;
                    var nested = value as IEnumerator;
                    if (nested != null && !(value is CustomYieldInstruction)) { steps.Push(nested); continue; }
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(output, "failed.txt"), error.ToString());
                    Debug.LogException(error);
                    Application.Quit();
                    yield break;
                }
                yield return value;
            }
            File.WriteAllText(Path.Combine(output, "complete"), Application.unityVersion);
            Application.Quit();
        }

        private IEnumerator Capture(string output)
        {
            yield return WaitForScene("MainMenu3");
            yield return new WaitForSecondsRealtime(5);
            SteamVR_LoadLevel.Begin(Lobby);
            yield return WaitForScene(Lobby);
            var menu = FindObjectOfType<TNH_UIManager>();
            if (!menu) throw new InvalidOperationException("Missing retail lobby menu");
            int i = menu.Levels.FindIndex(level => !level.IsModLevel && level.LevelSceneName == SceneName);
            if (i < 0) throw new InvalidOperationException("Missing retail Classic map");
            menu.SelectLevelRelative(i - menu.Levels.IndexOf(menu.CurrentLevel));
            var options = GM.TNHOptions;
            options.LastPlayedCharUniversalID = Character;
            options.GameModeSetting = TNHSetting_GameMode.Classic;
            options.ProgressionTypeSetting = TNHSetting_ProgressionType.FiveHold;
            options.TNHSeed = 0;
            options.EquipmentSeedMode = TNHSetting_EquipmentSeedMode.Manual;
            options.EquipmentSeed = EquipmentSeed;
            options.HealthModeSetting = TNHSetting_HealthMode.CustomHealth;
            options.HealthMult = TNH_HealthMult.NighInvulnerable;
            SceneManager.sceneLoaded += SeedScene;
            menu.LoadCurrentlySelectedLevel();
            yield return WaitForScene(SceneName);
            SceneManager.sceneLoaded -= SeedScene;

            var manager = GM.TNH_Manager;
            var hold = manager.CurHoldPoint;
            var marker = hold.SpawnPoints_Turrets[0];
            var direction = hold.GetSystemNodeCoreTransform().position - marker.position;
            direction.y = 0f;
            direction = direction.sqrMagnitude > 0.01f ? direction.normalized : marker.forward;
            GM.CurrentMovementManager.TeleportToPoint(marker.position, false, direction);
            yield return new WaitForSecondsRealtime(2);
            Write(output, "take", manager);
            yield return new WaitForSecondsRealtime(4);
            UnityEngine.Random.InitState(SimulationSeed);
            hold.TriggerSystemNode();
            float deadline = Time.realtimeSinceStartup + 35f;
            while (manager.Phase != TNH_Phase.Hold)
            {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Waiting for active retail Hold");
                yield return null;
            }
            yield return new WaitForSecondsRealtime(2);

            var floor = marker.position;
            Renderer nearest = null;
            float closest = float.PositiveInfinity;
            foreach (var renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.gameObject.scene.name != SceneName ||
                    !Array.Exists(renderer.sharedMaterials, material => material && material.name == "mtl_destructibleBarriers"))
                    continue;
                var delta = renderer.bounds.center - marker.position;
                if (delta.sqrMagnitude >= closest) continue;
                closest = delta.sqrMagnitude;
                nearest = renderer;
            }
            if (nearest)
            {
                var delta = nearest.bounds.center - marker.position;
                delta.y = 0f;
                if (delta.sqrMagnitude > .1f) direction = delta.normalized;
                floor += direction * Mathf.Max(0f, delta.magnitude - 3f);
                Debug.Log("RETAIL_HOLD_COVER " + nearest.name + " distance=" + Mathf.Sqrt(closest));
            }
            else throw new InvalidOperationException("Missing live Classic metal cover");
            GM.CurrentMovementManager.TeleportToPoint(floor, false, direction);
            yield return new WaitForSecondsRealtime(2);
            Write(output, "hold", manager);
            yield return new WaitForSecondsRealtime(8);
        }

        private static void SeedScene(Scene loaded, LoadSceneMode mode)
        {
            if (loaded.name == SceneName) UnityEngine.Random.InitState(SimulationSeed);
        }

        private static IEnumerator WaitForScene(string expected)
        {
            float deadline = Time.realtimeSinceStartup + 55f;
            while (SceneManager.GetActiveScene().name != expected)
            {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Waiting for " + expected);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(3);
        }

        private static object Vector(Vector3 v) { return new { v.x, v.y, v.z }; }

        private static object Probe(ReflectionProbe probe)
        {
            var bounds = probe.bounds;
            return new {
                name = probe.name, active = probe.gameObject.activeInHierarchy, enabled = probe.enabled,
                position = Vector(probe.transform.position), size = Vector(probe.size),
                center = Vector(probe.center), boundsCenter = Vector(bounds.center), boundsSize = Vector(bounds.size),
                probe.intensity, probe.cullingMask, probe.importance, probe.blendDistance, probe.boxProjection,
                mode = probe.mode.ToString(), texture = Texture(probe.texture), bakedTexture = Texture(probe.bakedTexture),
                customTexture = Texture(probe.customBakedTexture)
            };
        }

        private static string Texture(Texture texture)
        {
            return texture ? texture.name : null;
        }

        private static void Write(string output, string phase, TNH_Manager manager)
        {
            var rows = new List<object>();
            var cover = new List<object>();
            var closest = new List<ReflectionProbeBlendInfo>();
            foreach (var renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.gameObject.scene.name != SceneName)
                    continue;
                if ((renderer.bounds.center - GM.CurrentPlayerBody.Head.position).sqrMagnitude > 36 * 36) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (!mat) continue;
                    rows.Add(new { renderer = renderer.name, parent = renderer.transform.parent ? renderer.transform.parent.name : null,
                        center = Vector(renderer.bounds.center), size = Vector(renderer.bounds.size), material = mat.name,
                        shader = mat.shader.name, keys = mat.shaderKeywords, queue = mat.renderQueue, texture = mat.mainTexture ? mat.mainTexture.name : null });
                    if (mat.name != "mtl_destructibleBarriers") continue;
                    closest.Clear();
                    renderer.GetClosestReflectionProbes(closest);
                    var candidates = closest.ConvertAll(item => new {
                        name = item.probe ? item.probe.name : null,
                        texture = item.probe ? Texture(item.probe.texture) : null,
                        item.weight
                    });
                    cover.Add(new { name = renderer.name, position = Vector(renderer.bounds.center),
                        usage = renderer.reflectionProbeUsage.ToString(),
                        anchor = renderer.probeAnchor ? Vector(renderer.probeAnchor.position) : null,
                        renderer.lightmapIndex, candidates });
                }
            }
            var head = GM.CurrentPlayerBody.Head;
            var probes = Array.FindAll(Resources.FindObjectsOfTypeAll<ReflectionProbe>(),
                probe => probe.gameObject.scene.name == SceneName);
            File.WriteAllText(Path.Combine(output, phase + ".json"), JsonConvert.SerializeObject(new {
                scene = SceneManager.GetActiveScene().name, state = manager.Phase.ToString(),
                seed = manager.HoldSequenceSeed, hold = manager.HoldPoints.IndexOf(manager.CurHoldPoint),
                head = Vector(head.position), direction = Vector(head.forward), renderers = rows, cover,
                probes = Array.ConvertAll(probes, Probe),
                reflections = new { mode = RenderSettings.defaultReflectionMode.ToString(),
                    custom = Texture(RenderSettings.customReflection), fallback = Texture(ReflectionProbe.defaultTexture),
                    RenderSettings.reflectionIntensity, ambientMode = RenderSettings.ambientMode.ToString() }
            }, Formatting.Indented));
        }
    }
}
