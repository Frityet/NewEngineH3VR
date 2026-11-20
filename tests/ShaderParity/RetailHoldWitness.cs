using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Valve.Newtonsoft.Json;
using Valve.Newtonsoft.Json.Linq;

namespace NativeH3VR.QA
{
    public sealed class RetailHoldWitness : MonoBehaviour
    {
        const string Lobby = "TakeAndHold_Lobby_2";
        const string SceneName = "TakeAndHoldClassic";
        const string Character = "h3vr:Generic_0_BeginnerBlake";
        const int EquipmentSeed = 42;
        const int SimulationSeed = 14796;
        const BindingFlags GameFields = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo HoldStage = typeof(TNH_HoldPoint).GetField("m_state", GameFields);
        static readonly FieldInfo HoldUnits = typeof(TNH_HoldPoint).GetField("m_activeSosigs", GameFields);
        static readonly FieldInfo RoundElapsed = typeof(TNH_Manager).GetField("m_timePlaying", GameFields);

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
            string observerFile = Environment.GetEnvironmentVariable("NATIVEH3VR_RETAIL_HOLD_OBSERVER");
            var reference = string.IsNullOrEmpty(observerFile) ? null : JObject.Parse(File.ReadAllText(observerFile));
            string sceneName = reference == null ? SceneName : (string)reference["begin"]["scene"];
            int sequence = reference == null ? 0 : (int)reference["begin"]["sequenceSeed"];
            float sceneTimeout = reference == null ? 55f : 240f;
            yield return WaitForScene("MainMenu3", sceneTimeout);
            yield return new WaitForSecondsRealtime(5);
            SteamVR_LoadLevel.Begin(Lobby);
            yield return WaitForScene(Lobby, sceneTimeout);
            var menu = FindObjectOfType<TNH_UIManager>();
            if (!menu) throw new InvalidOperationException("Missing retail lobby menu");
            int i = menu.Levels.FindIndex(level => !level.IsModLevel && level.LevelSceneName == sceneName);
            if (i < 0) throw new InvalidOperationException("Missing retail T&H map " + sceneName);
            menu.SelectLevelRelative(i - menu.Levels.IndexOf(menu.CurrentLevel));
            var options = GM.TNHOptions;
            options.LastPlayedCharUniversalID = Character;
            options.GameModeSetting = TNHSetting_GameMode.Classic;
            options.ProgressionTypeSetting = TNHSetting_ProgressionType.FiveHold;
            options.TNHSeed = sequence;
            options.EquipmentSeedMode = TNHSetting_EquipmentSeedMode.Manual;
            options.EquipmentSeed = EquipmentSeed;
            options.HealthModeSetting = TNHSetting_HealthMode.CustomHealth;
            options.HealthMult = TNH_HealthMult.NighInvulnerable;
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> seedScene = delegate(Scene loaded, LoadSceneMode mode)
            {
                if (loaded.name == sceneName) UnityEngine.Random.InitState(SimulationSeed);
            };
            SceneManager.sceneLoaded += seedScene;
            menu.LoadCurrentlySelectedLevel();
            yield return WaitForScene(sceneName, sceneTimeout);
            SceneManager.sceneLoaded -= seedScene;

            var manager = GM.TNH_Manager;
            var hold = manager.CurHoldPoint;
            if (reference != null)
            {
                yield return CaptureBenchmark(output, reference, manager, hold, sequence);
                yield break;
            }
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

        private static IEnumerator CaptureBenchmark(string output, JObject reference, TNH_Manager manager,
            TNH_HoldPoint hold, int sequence)
        {
            int holdIndex = manager.HoldPoints.IndexOf(hold);
            foreach (var edge in new[] { "begin", "end" })
            {
                var state = reference[edge];
                if ((string)state["phase"] != "Hold" || (string)state["scene"] != manager.gameObject.scene.name ||
                    (int)state["sequenceSeed"] != sequence || manager.HoldSequenceSeed != sequence ||
                    (int)state["stockHoldIndex"] != holdIndex || (int)state["holdLayer"] != 0)
                    throw new InvalidOperationException("Saved observer does not match the retail first objective.");
            }
            var observer = reference["holdObserver"];
            var floor = ObserverVector(observer["floor"]);
            var direction = ObserverVector(observer["direction"]);
            if (Mathf.Abs(direction.y) > .001f || Mathf.Abs(direction.sqrMagnitude - 1) > .01f ||
                !hold.SpawnPoints_Turrets.Exists(marker => marker && marker.name == (string)observer["marker"] &&
                    (marker.position-floor).sqrMagnitude <= .0001f))
                throw new InvalidOperationException("Saved observer does not identify a retail turret marker and horizontal heading.");

            // Leave the stock opening spawn in place, as the native benchmark does.
            yield return new WaitForSecondsRealtime(5);
            if (manager.Phase != TNH_Phase.Take)
                throw new InvalidOperationException("Retail opening Take did not stay active.");
            Write(output, "take", manager);
            yield return WaitForCapture(output, "take", 4);
            GM.CurrentMovementManager.TeleportToPoint(floor, false, direction);
            UnityEngine.Random.InitState(SimulationSeed);
            hold.TriggerSystemNode();
            float deadline = Time.realtimeSinceStartup + 180f;
            while (manager.Phase != TNH_Phase.Hold || Stage(hold) != "Hacking" || ObjectiveNpcs(hold) == 0)
            {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Waiting for a live retail encryption wave");
                yield return null;
            }
            GM.CurrentMovementManager.TeleportToPoint(floor, false, direction);
            yield return new WaitForSecondsRealtime(5);
            if (manager.Phase != TNH_Phase.Hold || Stage(hold) != "Hacking" || ObjectiveNpcs(hold) == 0)
                throw new InvalidOperationException("Retail first Hold had no active encryption wave.");
            Write(output, "hold", manager);
            yield return WaitForCapture(output, "hold", 8);
        }

        private static IEnumerator WaitForCapture(string output, string phase, float fallbackSeconds)
        {
            if (Environment.GetEnvironmentVariable("NATIVEH3VR_RETAIL_HOLD_CAPTURE_ACK") != "1")
            {
                yield return new WaitForSecondsRealtime(fallbackSeconds);
                yield break;
            }
            string path = Path.Combine(output, phase + ".captured");
            float deadline = Time.realtimeSinceStartup + 90f;
            while (!File.Exists(path))
            {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Waiting for stereo capture of " + phase);
                yield return null;
            }
        }

        private static Vector3 ObserverVector(JToken value) =>
            new Vector3((float)value["x"], (float)value["y"], (float)value["z"]);

        private static string Stage(TNH_HoldPoint hold) => HoldStage.GetValue(hold).ToString();

        private static int ObjectiveNpcs(TNH_HoldPoint hold) =>
            ((List<Sosig>)HoldUnits.GetValue(hold)).FindAll(unit => unit && unit.BodyState == Sosig.SosigBodyState.InControl).Count;

        private static IEnumerator WaitForScene(string expected, float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
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
            string scene = manager.gameObject.scene.name;
            var rows = new List<object>();
            var cover = new List<object>();
            var closest = new List<ReflectionProbeBlendInfo>();
            foreach (var renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.gameObject.scene.name != scene)
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
                probe => probe.gameObject.scene.name == scene);
            var q = head.rotation;
            string path = Path.Combine(output, phase + ".json");
            File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(new {
                scene = SceneManager.GetActiveScene().name, state = manager.Phase.ToString(),
                seed = manager.HoldSequenceSeed, hold = manager.HoldPoints.IndexOf(manager.CurHoldPoint),
                head = Vector(head.position), direction = Vector(head.forward), renderers = rows, cover,
                headRotation = new { q.x, q.y, q.z, q.w }, equipmentSeed = GM.TNHOptions.EquipmentSeed,
                simulationSeed = SimulationSeed, roundElapsedSeconds = (float)RoundElapsed.GetValue(manager),
                holdStage = Stage(manager.CurHoldPoint), objectiveNpcCount = ObjectiveNpcs(manager.CurHoldPoint),
                probes = Array.ConvertAll(probes, Probe),
                reflections = new { mode = RenderSettings.defaultReflectionMode.ToString(),
                    custom = Texture(RenderSettings.customReflection), fallback = Texture(ReflectionProbe.defaultTexture),
                    RenderSettings.reflectionIntensity, ambientMode = RenderSettings.ambientMode.ToString() }
            }, Formatting.Indented));
            File.Move(path + ".tmp", path);
        }
    }
}
