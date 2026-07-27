#if UNITY_EDITOR
using System.IO;
using Playtester.Agent;
using Playtester.Gym;
using Playtester.Telemetry;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace Playtester.EditorTool
{
    /// <summary>Creates the reproducible minimum scene wiring required by the RL contracts.</summary>
    public static class PlaytesterSceneWiring
    {
        private const string Configs = "Assets/Configs";
        private const string Prefabs = "Assets/Prefabs";

        [MenuItem("Playtester/Create Or Repair Playable Scenes")]
        public static void CreateOrRepair()
        {
            SyncConfigFromYaml.Sync();
            Directory.CreateDirectory(Prefabs);
            PieceLibraryConfig pieces = AssetDatabase.LoadAssetAtPath<PieceLibraryConfig>(Configs + "/PieceLibraryConfig.asset");
            pieces.SetPrefabReferences(
                CreatePiecePrefab<GapJumpPiece>("GapJumpPiece"),
                CreatePiecePrefab<MoveToGoalPiece>("MoveToGoalPiece"),
                CreatePiecePrefab<ElevationPiece>("ElevationPiece"));
            EditorUtility.SetDirty(pieces);
            AssetDatabase.SaveAssets();
            CreateGymScene();
            CreateLevelScene("LevelA", false);
            CreateLevelScene("LevelB", true);
        }

        private static GameObject CreatePiecePrefab<T>(string name) where T : MonoBehaviour, IPieceType
        {
            string path = Prefabs + "/" + name + ".prefab";
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            GameObject root = new(name);
            T piece = root.AddComponent<T>();
            GameObject goal = new("Goal");
            goal.transform.SetParent(root.transform, false);
            goal.AddComponent<GoalMarker>();
            goal.AddComponent<CircleCollider2D>().isTrigger = true;
            goal.AddComponent<AgentTriggerRelay>();
            Set(piece, "localGoal", goal.transform);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static void CreateGymScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateWorld("Gym", out Tilemap tilemap);
            PlaytestAgent agent = CreatePlayer("Player", tilemap, true, "gym");
            GameObject root = new("CompositionRoot");
            PieceComposer composer = root.AddComponent<PieceComposer>();
            Set(composer, "pieceLibrary", AssetDatabase.LoadAssetAtPath<PieceLibraryConfig>(Configs + "/PieceLibraryConfig.asset"));
            Set(composer, "player", agent.transform);
            Set(composer, "playerBody", agent.GetComponent<Rigidbody2D>());
            Set(composer, "compositionRoot", root.transform);
            Set(composer, "agent", agent);
            Set(agent, "pieceComposer", composer);
            Save(scene, "GymScene");
        }

        private static void CreateLevelScene(string levelName, bool plantedHazard)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateWorld(levelName, out Tilemap tilemap);
            StartMarker start = new GameObject("StartMarker").AddComponent<StartMarker>();
            start.transform.position = new Vector3(-10f, 1f, 0f);
            string levelId = levelName == "LevelA" ? "level_a" : "level_b";
            PlaytestAgent agent = CreatePlayer("Player", tilemap, false, levelId);
            agent.transform.position = start.transform.position;
            Set(agent, "levelStart", start);
            Transform previous = null;
            for (int index = 0; index < 3; index++)
            {
                GoalMarker goal = new GameObject("GoalMarker_" + (index + 1)).AddComponent<GoalMarker>();
                goal.transform.position = new Vector3(-4f + index * 6f, 1f, 0f);
                goal.gameObject.AddComponent<CircleCollider2D>().isTrigger = true;
                AgentTriggerRelay relay = goal.gameObject.AddComponent<AgentTriggerRelay>();
                if (previous != null)
                    previous.GetComponent<AgentTriggerRelay>().Configure(
                        agent,
                        AgentTriggerRelay.TriggerKind.PieceGoal,
                        goal.transform,
                        $"piece_{index}",
                        "move_to_goal",
                        new PieceParams { Distance = 6f },
                        true);
                previous = goal.transform;
                if (index == 0) Set(agent, "currentGoal", goal);
            }
            previous.GetComponent<AgentTriggerRelay>().Configure(
                agent,
                AgentTriggerRelay.TriggerKind.PieceGoal,
                null!,
                "piece_3",
                plantedHazard ? "gap_jump" : "move_to_goal",
                plantedHazard
                    ? new PieceParams { Width = 5f }
                    : new PieceParams { Distance = 6f },
                true);
            if (plantedHazard)
            {
                GameObject hazard = new("PlantedHazard");
                hazard.transform.position = new Vector3(3f, 0.5f, 0f);
                BoxCollider2D collider = hazard.AddComponent<BoxCollider2D>();
                collider.size = new Vector2(1f, 2f);
                collider.isTrigger = true;
                hazard.AddComponent<AgentTriggerRelay>().Configure(
                    agent,
                    AgentTriggerRelay.TriggerKind.Hazard,
                    null!,
                    "piece_3",
                    "gap_jump",
                    new PieceParams { Width = 5f },
                    true);
            }
            Save(scene, levelName);
        }

        private static void CreateWorld(string name, out Tilemap tilemap)
        {
            new GameObject(name + "Camera", typeof(Camera)).transform.position = new Vector3(0f, 0f, -10f);
            GameObject ground = new("Ground");
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.AddComponent<BoxCollider2D>().size = new Vector2(40f, 1f);
            GameObject grid = new("Grid", typeof(Grid));
            tilemap = new GameObject("Tilemap", typeof(Tilemap), typeof(TilemapRenderer)).GetComponent<Tilemap>();
            tilemap.transform.SetParent(grid.transform);
        }

        private static PlaytestAgent CreatePlayer(string name, Tilemap tilemap, bool stageOne, string levelId)
        {
            GameObject player = new(name);
            player.AddComponent<PlaytesterRuntimeSmokeExit>();
            Rigidbody2D body = player.AddComponent<Rigidbody2D>();
            body.gravityScale = 2f;
            player.AddComponent<BoxCollider2D>().size = new Vector2(0.75f, 1f);
            player.AddComponent<PlayerInputAdapter>();
            PlayerController controller = player.AddComponent<PlayerController>();
            BehaviorParameters behavior = player.AddComponent<BehaviorParameters>();
            behavior.BehaviorName = "PlaytestAgent";
            behavior.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(4);
            behavior.BrainParameters.VectorObservationSize = 203;
            GridObservationEncoder encoder = player.AddComponent<GridObservationEncoder>();
            TelemetryRecorder telemetry = player.AddComponent<TelemetryRecorder>();
            PlaytestAgent agent = player.AddComponent<PlaytestAgent>();
            player.AddComponent<DecisionRequester>();
            Set(controller, "playerConfig", AssetDatabase.LoadAssetAtPath<PlayerConfig>(Configs + "/PlayerConfig.asset"));
            Set(controller, "playerControls", AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>("Assets/PlayerControls.inputactions"));
            Set(encoder, "observationConfig", AssetDatabase.LoadAssetAtPath<ObservationConfigAsset>(Configs + "/ObservationConfig.asset"));
            Set(agent, "playerInput", player.GetComponent<PlayerInputAdapter>());
            Set(agent, "playerController", controller);
            Set(agent, "playerBody", body);
            Set(agent, "tilemap", tilemap);
            Set(agent, "observationEncoder", encoder);
            Set(agent, "rewardConfig", AssetDatabase.LoadAssetAtPath<RewardConfigAsset>(Configs + "/RewardConfig.asset"));
            Set(agent, "telemetryRecorder", telemetry);
            Set(agent, "stageOne", stageOne);
            Set(agent, "levelId", levelId);
            Set(telemetry, "telemetryDirectory", Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Telemetry")));
            return agent;
        }

        private static void Save(Scene scene, string name)
        {
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/" + name + ".unity");
        }

        private static void Set(Object target, string property, Object value)
        {
            SerializedObject serialized = new(target);
            serialized.FindProperty(property).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(Object target, string property, bool value)
        {
            SerializedObject serialized = new(target);
            serialized.FindProperty(property).boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(Object target, string property, string value)
        {
            SerializedObject serialized = new(target);
            serialized.FindProperty(property).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
