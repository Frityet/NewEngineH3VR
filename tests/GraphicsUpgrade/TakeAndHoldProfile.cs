using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Valve.VR;
using Valve.Newtonsoft.Json;
using Valve.Newtonsoft.Json.Linq;

namespace NativeH3VR.QA
{
    public sealed partial class GraphicsProfileWitness
    {
        private const int TakeAndHoldPairs = 270;
        private const int EquipmentSeed = 42;
        private const int SimulationSeed = 14796;
        private const string StockCharacter = "h3vr:Generic_0_BeginnerBlake";
        private const BindingFlags GameFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo RoundElapsed = typeof(TNH_Manager).GetField("m_timePlaying", GameFields);
        private static readonly FieldInfo HoldLayer = typeof(TNH_HoldPoint).GetField("m_phaseIndex", GameFields);
        private static readonly FieldInfo HoldStage = typeof(TNH_HoldPoint).GetField("m_state", GameFields);
        private static readonly FieldInfo HoldUnits = typeof(TNH_HoldPoint).GetField("m_activeSosigs", GameFields);

        [Serializable]
        private sealed class LiveState
        {
            public string mapId, mapName, scene, phase, holdStage, character, gameMode, healthMode, healthMultiplier;
            public int stockHoldIndex, holdLayer, sequenceSeed, equipmentSeed, simulationSeed, activeNpcCount, objectiveNpcCount;
            public float roundElapsedSeconds, phaseElapsedSeconds;
            public object headPosition, headRotation;
        }

        [Serializable]
        private sealed class LiveWindow
        {
            public LiveState begin, end;
            public object holdObserver;
        }

        private IEnumerator ProfileTakeAndHold(string mapId)
        {
            int sequence = int.Parse(Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_TNH_SEED") ?? "0",
                System.Globalization.CultureInfo.InvariantCulture);
            string variant = builtIn ? "stock" : Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_PRESET");
            Require(variant == "stock" || variant == "balanced" || variant == "quality", "Unknown T&H profile variant");
            int preset = builtIn ? -1 : variant == "balanced" ? 1 : 2;
            if (builtIn)
                Require(!GraphicsSettings.currentRenderPipeline, "Expected a Built-in pipeline for the T&H baseline");
            else
            {
                var options = Type.GetType("NativeH3VR.GraphicsUpgrade.GraphicsOptions, NativeH3VR.GraphicsUpgrade", true);
                var setting = Type.GetType("NativeH3VR.GraphicsUpgrade.GraphicsSetting, NativeH3VR.GraphicsUpgrade", true);
                options.GetMethod("SetIndex").Invoke(null, new[] { Enum.Parse(setting, "Preset"), (object)preset });
            }

            const string lobby = "TakeAndHold_Lobby_2";
            Require(Application.CanStreamedLevelBeLoaded(lobby), "The stock Take & Hold lobby is missing");
            SteamVR_LoadLevel.Begin(lobby);
            yield return WaitForScene(lobby);
            var menu = FindAnyObjectByType<TNH_UIManager>();
            Require(menu, "Missing stock T&H lobby menu");
            int desired = menu.Levels.FindIndex(level => !level.IsModLevel && level.LevelID == mapId);
            Require(desired >= 0, "Unavailable stock T&H map " + mapId);
            menu.SelectLevelRelative(desired - menu.Levels.IndexOf(menu.CurrentLevel));
            var selected = menu.CurrentLevel;
            string scene = selected.LevelSceneName;
            Require(selected.LevelID == mapId && Application.CanStreamedLevelBeLoaded(scene), "T&H map selection did not resolve");
            Require(sequence >= 0 && sequence < selected.TotalSeeds, "Unsupported stock point sequence for " + mapId);

            var game = GM.TNHOptions;
            game.LastPlayedCharUniversalID = StockCharacter;
            game.GameModeSetting = TNHSetting_GameMode.Classic;
            game.ProgressionTypeSetting = TNHSetting_ProgressionType.FiveHold;
            game.TNHSeed = sequence;
            game.EquipmentSeedMode = TNHSetting_EquipmentSeedMode.Manual;
            game.EquipmentSeed = EquipmentSeed;
            // The stock custom-health option suppresses score submission while allowing live AI.
            game.HealthModeSetting = TNHSetting_HealthMode.CustomHealth;
            game.HealthMult = TNH_HealthMult.NighInvulnerable;

            void SeedScene(Scene loaded, LoadSceneMode mode)
            {
                if (loaded.name == scene)
                    UnityEngine.Random.InitState(SimulationSeed);
            }
            SceneManager.sceneLoaded += SeedScene;
            try
            {
                menu.LoadCurrentlySelectedLevel();
                yield return WaitForScene(scene);
            }
            finally
            {
                SceneManager.sceneLoaded -= SeedScene;
            }
            var manager = GM.TNH_Manager;
            Require(manager && manager.gameObject.scene.name == scene && manager.GameMode == TNHSetting_GameMode.Classic &&
                    manager.Phase == TNH_Phase.Take && manager.HoldSequenceSeed == sequence &&
                    sequence < manager.PossibleSequnces.Count && manager.C && manager.C.UgcId == StockCharacter,
                "Stock T&H Classic did not reach the seeded opening Take on " + scene);
            var hold = manager.CurHoldPoint;
            Require(hold && hold.SpawnPoints_Turrets.Count > 0, "Missing first stock objective in " + scene);
            int markerIndex = 0;
            Vector3 replayDirection = default;
            string observerFile = Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_PROFILE_TNH_OBSERVER");
            bool replayObserver = !string.IsNullOrEmpty(observerFile);
            if (replayObserver)
                (markerIndex, replayDirection) = LoadHoldObserver(observerFile, selected, manager, hold);
            yield return Sample(scene, "take-" + variant, true, preset, TakeAndHoldPairs,
                RecordLiveWindow(selected, manager, TNH_Phase.Take, 0f, "take-" + variant));

            var marker = hold.SpawnPoints_Turrets[markerIndex];
            var towardNode = hold.GetSystemNodeCoreTransform().position - marker.position;
            towardNode.y = 0f;
            var initialDirection = towardNode.sqrMagnitude > 0.01f ? towardNode.normalized : marker.forward;
            GM.CurrentMovementManager.TeleportToPoint(marker.position, false, replayObserver ? replayDirection : initialDirection);
            UnityEngine.Random.InitState(SimulationSeed);
            hold.TriggerSystemNode();

            float deadline = Time.realtimeSinceStartup + 85f;
            while (manager.Phase == TNH_Phase.Take)
            {
                Require(Time.realtimeSinceStartup < deadline, "The stock node did not initiate first Hold");
                yield return null;
            }
            float holdStart = (float)RoundElapsed.GetValue(manager);
            while (Stage(hold) != TNH_HoldPoint.HoldState.Hacking || CountObjectiveNpcs(hold) == 0)
            {
                Require(manager.Phase == TNH_Phase.Hold && Time.realtimeSinceStartup < deadline,
                    "First Hold did not reach a live encryption wave");
                yield return null;
            }
            // Choose the view after the active hold has installed its stock cover.
            var observerDirection = replayObserver ? replayDirection :
                HoldObserverDirection(GM.CurrentPlayerBody.Head.position, towardNode, marker);
            GM.CurrentMovementManager.TeleportToPoint(marker.position, false, observerDirection);
            var floor = marker.position;
            var observer = new
            {
                marker = marker.name, markerIndex,
                floor = new { floor.x, floor.y, floor.z },
                direction = new { observerDirection.x, observerDirection.y, observerDirection.z }
            };
            yield return Sample(scene, "hold-" + variant, true, preset, TakeAndHoldPairs,
                RecordLiveWindow(selected, manager, TNH_Phase.Hold, holdStart, "hold-" + variant, observer));
            if (Environment.GetEnvironmentVariable("NATIVEH3VR_COVER_WITNESS") == "1")
                yield return InspectHoldCover(scene + "-cover-" + variant);
        }

        private static (int markerIndex, Vector3 direction) LoadHoldObserver(string file,
            TNH_UIManager.LevelData map, TNH_Manager manager, TNH_HoldPoint hold)
        {
            var reference = JObject.Parse(File.ReadAllText(file));
            int holdIndex = manager.HoldPoints.IndexOf(hold);
            foreach (string edge in new[] { "begin", "end" })
            {
                var state = reference[edge];
                Require(state != null && (string)state["phase"] == "Hold" &&
                        (string)state["mapId"] == map.LevelID && (string)state["scene"] == map.LevelSceneName &&
                        (int?)state["sequenceSeed"] == manager.HoldSequenceSeed && (int?)state["stockHoldIndex"] == holdIndex &&
                        (int?)state["holdLayer"] == 0,
                    "Saved observer is for a different stock map, route, or first Hold objective");
            }
            var observer = reference["holdObserver"];
            Require(observer != null && !string.IsNullOrEmpty((string)observer["marker"]), "Missing Hold observer marker");
            var floor = ReadObserverVector(observer["floor"]);
            var direction = ReadObserverVector(observer["direction"]);
            Require(Mathf.Abs(direction.y) < 0.001f && Mathf.Abs(direction.sqrMagnitude - 1f) < 0.01f,
                "Hold observer direction must be a horizontal unit vector");
            direction.y = 0f;
            string markerName = (string)observer["marker"];
            int markerIndex = hold.SpawnPoints_Turrets.FindIndex(marker =>
                marker && marker.name == markerName && (marker.position - floor).sqrMagnitude <= 0.0001f);
            Require(markerIndex >= 0, "Saved Hold observer floor is not a stock marker in this objective");
            return (markerIndex, direction);
        }

        private static Vector3 ReadObserverVector(JToken value)
        {
            Require(value is JObject && value["x"] != null && value["y"] != null && value["z"] != null,
                "Missing Hold observer vector");
            var vector = new Vector3((float)value["x"], (float)value["y"], (float)value["z"]);
            Require(float.IsFinite(vector.x) && float.IsFinite(vector.y) && float.IsFinite(vector.z),
                "Nonfinite Hold observer vector");
            return vector;
        }

        private static Vector3 HoldObserverDirection(Vector3 head, Vector3 towardNode, Transform marker)
        {
            const float maxSight = 15f;
            var toward = towardNode.sqrMagnitude > 0.01f ? towardNode.normalized : marker.forward;
            var best = toward;
            float bestScore = float.NegativeInfinity;
            foreach (int yaw in new[] { 0, -15, 15, -30, 30, -45, 45, -60, 60 })
            {
                var direction = Quaternion.Euler(0f, yaw, 0f) * toward;
                float score = -0.025f * Mathf.Abs(yaw);
                foreach (int rayYaw in new[] { 0, -15, 15, -30, 30 })
                {
                    var ray = Quaternion.Euler(0f, rayYaw, 0f) * direction;
                    float clearance = maxSight;
                    foreach (var hit in Physics.RaycastAll(head, ray, maxSight, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore))
                    {
                        var collider = hit.collider;
                        if (collider.attachedRigidbody || collider.GetComponentInParent<Sosig>() ||
                            collider.GetComponentInParent<FVRPlayerBody>() || collider.GetComponentInParent<FVRViveHand>())
                            continue;
                        clearance = Mathf.Min(clearance, hit.distance);
                    }
                    // Quantize close scores, and favor a clear central ray over wide periphery.
                    score += Mathf.Floor(clearance * 4f) * 0.25f * (rayYaw == 0 ? 3f : 1f);
                }
                if (score > bestScore)
                {
                    best = direction;
                    bestScore = score;
                }
            }
            return best;
        }

        private Action<string> RecordLiveWindow(TNH_UIManager.LevelData map, TNH_Manager manager,
            TNH_Phase phase, float phaseStart, string label, object holdObserver = null)
        {
            var window = new LiveWindow { holdObserver = holdObserver };
            return edge =>
            {
                Require(manager.Phase == phase && Mathf.Approximately(Time.timeScale, 1f) &&
                        SceneManager.GetActiveScene().name == map.LevelSceneName,
                    "Unexpected gameplay state during " + label);
                var hold = manager.CurHoldPoint;
                int objectiveNpcs = CountObjectiveNpcs(hold);
                Require(phase != TNH_Phase.Hold || (Stage(hold) == TNH_HoldPoint.HoldState.Hacking && objectiveNpcs > 0),
                    "First Hold encryption or its live NPCs ended during " + label);
                int npcCount = Array.FindAll(FindObjectsByType<Sosig>(),
                    unit => unit.BodyState == Sosig.SosigBodyState.InControl).Length;
                float elapsed = (float)RoundElapsed.GetValue(manager);
                var p = stereo.TrackingOrigin.TransformPoint(stereo.LatestFrame.head.Position);
                var q = stereo.TrackingOrigin.rotation * stereo.LatestFrame.head.Rotation;
                var state = new LiveState
                {
                    mapId = map.LevelID, mapName = map.LevelDisplayName, scene = map.LevelSceneName,
                    phase = phase.ToString(), holdStage = phase == TNH_Phase.Hold ? Stage(hold).ToString() : "",
                    character = manager.C.UgcId, gameMode = manager.GameMode.ToString(),
                    healthMode = manager.HealthMode.ToString(), healthMultiplier = manager.HealthMult.ToString(),
                    stockHoldIndex = manager.HoldPoints.IndexOf(hold), holdLayer = (int)HoldLayer.GetValue(hold),
                    sequenceSeed = manager.HoldSequenceSeed, equipmentSeed = GM.TNHOptions.EquipmentSeed,
                    simulationSeed = SimulationSeed, activeNpcCount = npcCount, objectiveNpcCount = objectiveNpcs,
                    roundElapsedSeconds = elapsed, phaseElapsedSeconds = elapsed - phaseStart,
                    headPosition = new { p.x, p.y, p.z },
                    headRotation = new { q.x, q.y, q.z, q.w }
                };
                Require(state.activeNpcCount > 0, "No active NPCs during " + label);
                if (edge == "begin")
                    window.begin = state;
                else
                {
                    window.end = state;
                    File.WriteAllText(Path.Combine(output, map.LevelSceneName + "-" + label + "-state.json"),
                        JsonConvert.SerializeObject(window, Formatting.Indented));
                }
            };
        }

        private static TNH_HoldPoint.HoldState Stage(TNH_HoldPoint hold) => (TNH_HoldPoint.HoldState)HoldStage.GetValue(hold);

        private static int CountObjectiveNpcs(TNH_HoldPoint hold) =>
            ((List<Sosig>)HoldUnits.GetValue(hold)).FindAll(unit => unit && unit.BodyState == Sosig.SosigBodyState.InControl).Count;
    }
}
