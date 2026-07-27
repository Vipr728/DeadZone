using System;
using System.Collections.Generic;
using System.IO;
using CelesteBenchmark;
using PlatformerPlaytest;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryzi.Integrations.ExistingSimulator
{
    /// <summary>
    /// File bridge for the GB10-native SimCore path. Unity exports the static
    /// deterministic subset; the native runner returns macro IDs; Unity expands
    /// and authoritatively replays them through the real isolated physics arena.
    /// </summary>
    public static class NativeSimCoreBridge
    {
        const string SnapshotSchema = "ryz-unity-snapshot/1.0";

        /// <summary>
        /// Checks the deliberately small native parity surface before a scene is
        /// exported. The first GUI release supports static, axis-aligned box
        /// platforms plus rectangular hazards. Unsupported dynamic mechanics are
        /// rejected instead of being silently flattened into static rectangles.
        /// </summary>
        public static BridgeCompatibilityReport InspectCompatibility(
            Arena arena,
            ScenarioConfig scenario = null)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            var errors = new List<string>();
            var warnings = new List<string>();
            Scene scene = arena.Scene;
            int groundLayer = LayerMask.NameToLayer("Ground");
            int oneWayLayer = LayerMask.NameToLayer("OneWay");
            int hazardLayer = LayerMask.NameToLayer("Hazard");
            int movingLayer = LayerMask.NameToLayer("MovingPlatform");
            int crumbleLayer = LayerMask.NameToLayer("Crumble");
            int platformCount = 0;
            int hazardCount = 0;
            int playerCount = 0;
            int goalCount = 0;

            if (groundLayer < 0 || oneWayLayer < 0 || hazardLayer < 0)
                errors.Add("The Ground, OneWay, and Hazard layers must exist.");

            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                CelesteBenchmarkPlayer[] players =
                    roots[rootIndex].GetComponentsInChildren<CelesteBenchmarkPlayer>(true);
                playerCount += players.Length;
                BenchmarkGoal[] goals = roots[rootIndex].GetComponentsInChildren<BenchmarkGoal>(true);
                goalCount += goals.Length;

                MonoBehaviour[] behaviours =
                    roots[rootIndex].GetComponentsInChildren<MonoBehaviour>(true);
                for (int behaviourIndex = 0; behaviourIndex < behaviours.Length; behaviourIndex++)
                {
                    MonoBehaviour behaviour = behaviours[behaviourIndex];
                    if (!behaviour)
                        continue;
                    if (behaviour is BenchmarkMovingPlatform)
                        errors.Add($"{HierarchyPath(behaviour.transform)} uses a moving platform.");
                    else if (behaviour is BenchmarkCrumblingPlatform)
                        errors.Add($"{HierarchyPath(behaviour.transform)} uses a crumbling platform.");
                    else if (behaviour is BenchmarkSpring)
                        errors.Add($"{HierarchyPath(behaviour.transform)} uses a spring.");
                    else if (behaviour is BenchmarkDashRefill)
                        warnings.Add(
                            $"{HierarchyPath(behaviour.transform)} is a dash refill. Its location is not modeled " +
                            "by SimCore; final Unity replay remains authoritative.");
                }

                Collider2D[] colliders = roots[rootIndex].GetComponentsInChildren<Collider2D>(true);
                for (int colliderIndex = 0; colliderIndex < colliders.Length; colliderIndex++)
                {
                    Collider2D collider = colliders[colliderIndex];
                    if (!collider || !collider.enabled ||
                        collider.GetComponent<CelesteBenchmarkPlayer>() != null ||
                        collider.GetComponent<BenchmarkGoal>() != null)
                        continue;

                    int layer = collider.gameObject.layer;
                    string path = HierarchyPath(collider.transform);
                    if (layer == movingLayer || layer == crumbleLayer)
                    {
                        errors.Add($"{path} is on an unsupported dynamic platform layer.");
                        continue;
                    }

                    bool isPlatform = layer == groundLayer || layer == oneWayLayer;
                    bool isHazard = layer == hazardLayer;
                    if (!isPlatform && !isHazard)
                        continue;

                    if (!(collider is BoxCollider2D))
                    {
                        errors.Add($"{path} must use BoxCollider2D for native parity.");
                        continue;
                    }
                    if (Mathf.Abs(Mathf.DeltaAngle(collider.transform.eulerAngles.z, 0f)) > 0.01f)
                    {
                        errors.Add($"{path} is rotated; only axis-aligned rectangles are supported.");
                        continue;
                    }
                    if (collider.attachedRigidbody != null &&
                        collider.attachedRigidbody.bodyType != RigidbodyType2D.Static)
                    {
                        errors.Add($"{path} has a non-static Rigidbody2D.");
                        continue;
                    }
                    if (isPlatform && collider.isTrigger)
                    {
                        errors.Add($"{path} is a trigger on a platform layer.");
                        continue;
                    }

                    if (isPlatform)
                        platformCount++;
                    else
                        hazardCount++;
                }
            }

            if (playerCount != 1)
                errors.Add($"Expected exactly one CelesteBenchmarkPlayer, found {playerCount}.");
            bool hasScenarioGoal = scenario != null &&
                                   scenario.goalRect.width > 0f &&
                                   scenario.goalRect.height > 0f;
            if (goalCount == 0 && !hasScenarioGoal)
                errors.Add("No BenchmarkGoal was found.");
            if (platformCount == 0)
                errors.Add("No supported static Ground or OneWay BoxCollider2D platforms were found.");

            return new BridgeCompatibilityReport
            {
                supported = errors.Count == 0,
                platformCount = platformCount,
                hazardCount = hazardCount,
                playerCount = playerCount,
                goalCount = goalCount,
                errors = errors.ToArray(),
                warnings = warnings.ToArray()
            };
        }

        public static UnityTaskSnapshot ExportSnapshot(
            Arena arena,
            ScenarioConfig scenario,
            string path,
            string taskId = "unity-demo")
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));
            if (scenario == null)
                throw new ArgumentNullException(nameof(scenario));

            BridgeCompatibilityReport compatibility = InspectCompatibility(arena, scenario);
            if (!compatibility.supported)
                throw new InvalidOperationException(
                    "Unity scene is outside the supported native subset:\n- " +
                    string.Join("\n- ", compatibility.errors));

            CelesteBenchmarkPlayer player = FindPlayer(arena);
            if (player == null)
                throw new InvalidOperationException("Unity bridge could not find the CelesteBenchmark player.");

            List<BridgePlatform> platforms = new List<BridgePlatform>();
            List<BridgeRect> hazards = new List<BridgeRect>();
            int hazardLayer = LayerMask.NameToLayer("Hazard");
            HashSet<int> platformLayers = new HashSet<int>
            {
                LayerMask.NameToLayer("Ground"),
                LayerMask.NameToLayer("OneWay")
            };

            GameObject[] roots = arena.Scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                Collider2D[] colliders = roots[rootIndex].GetComponentsInChildren<Collider2D>(true);
                for (int colliderIndex = 0; colliderIndex < colliders.Length; colliderIndex++)
                {
                    Collider2D collider = colliders[colliderIndex];
                    if (!collider || !collider.enabled || collider.GetComponent<CelesteBenchmarkPlayer>() != null)
                        continue;

                    BridgeRect rect = FromBounds(collider.bounds);
                    int layer = collider.gameObject.layer;
                    if (layer == hazardLayer)
                    {
                        hazards.Add(rect);
                    }
                    else if (!collider.isTrigger && platformLayers.Contains(layer))
                    {
                        platforms.Add(new BridgePlatform
                        {
                            rect = rect,
                            kind = layer == LayerMask.NameToLayer("OneWay") ? "one-way" : "solid"
                        });
                    }
                }
            }

            BridgeVec2[] checkpoints = new BridgeVec2[scenario.sectionBoundariesX?.Length ?? 0];
            for (int i = 0; i < checkpoints.Length; i++)
            {
                float y = scenario.sectionBoundariesY != null && i < scenario.sectionBoundariesY.Length
                    ? scenario.sectionBoundariesY[i]
                    : scenario.spawnPosition.y;
                checkpoints[i] = new BridgeVec2(scenario.sectionBoundariesX[i], y);
            }

            UnityTaskSnapshot snapshot = new UnityTaskSnapshot
            {
                schemaVersion = SnapshotSchema,
                taskId = taskId,
                randomizationSeed = scenario.layoutSeed,
                maxTicks = scenario.stepBudget,
                fixedDeltaTime = scenario.fixedDeltaTime,
                spawn = new BridgeVec2(scenario.spawnPosition.x, scenario.spawnPosition.y),
                goal = new BridgeRect(
                    scenario.goalRect.x,
                    scenario.goalRect.y,
                    scenario.goalRect.width,
                    scenario.goalRect.height),
                platforms = platforms.ToArray(),
                hazards = hazards.ToArray(),
                checkpoints = checkpoints,
                movement = BridgeMovement.From(player)
            };
            if (snapshot.platforms.Length == 0)
                throw new InvalidOperationException("Unity bridge found no supported static platforms.");

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
            return snapshot;
        }

        public static List<PlayerAction> LoadNativeReplay(
            string taskBundlePath,
            string replayPath,
            out NativeReplay replay)
        {
            if (!File.Exists(taskBundlePath))
                throw new FileNotFoundException("Native task bundle is missing.", taskBundlePath);
            if (!File.Exists(replayPath))
                throw new FileNotFoundException("Native replay is missing.", replayPath);

            NativeTaskBundle bundle =
                JsonUtility.FromJson<NativeTaskBundle>(File.ReadAllText(taskBundlePath));
            replay = JsonUtility.FromJson<NativeReplay>(File.ReadAllText(replayPath));
            if (bundle?.task?.actionSchema?.macros == null)
                throw new InvalidDataException("Native task bundle has no macro vocabulary.");
            if (replay == null || !replay.verified || !replay.completed)
                throw new InvalidDataException("SimCore did not return a completed, verified replay.");

            Dictionary<int, NativeMacro> macros = new Dictionary<int, NativeMacro>();
            for (int i = 0; i < bundle.task.actionSchema.macros.Length; i++)
                macros[bundle.task.actionSchema.macros[i].id] = bundle.task.actionSchema.macros[i];

            List<PlayerAction> actions = new List<PlayerAction>();
            for (int macroIndex = 0; macroIndex < replay.macroIds.Length; macroIndex++)
            {
                if (!macros.TryGetValue(replay.macroIds[macroIndex], out NativeMacro macro))
                    throw new InvalidDataException("Native replay references an unknown macro.");
                for (int tick = 0; tick < macro.ticks; tick++)
                {
                    actions.Add(new PlayerAction
                    {
                        MoveX = macro.moveX,
                        MoveY = macro.moveY,
                        JumpPressed = macro.button0Pressed && tick == 0,
                        JumpHeld = macro.button0Held,
                        DashPressed = macro.button1Pressed && tick == 0,
                        ClimbHeld = macro.button2Held
                    });
                }
            }
            return actions;
        }

        public static BridgeReplayVerification VerifyInUnity(
            Arena arena,
            ScenarioConfig scenario,
            IReadOnlyList<PlayerAction> actions,
            int seed = 0)
        {
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);
            adapter.ResetEpisode(seed);
            Observation observation = new Observation();
            float furthest = 0f;
            int executed = 0;
            for (int tick = 0; tick < actions.Count; tick++)
            {
                PlayerAction action = actions[tick];
                adapter.ApplyAction(in action);
                adapter.TickSimulation(scenario.fixedDeltaTime);
                adapter.AfterStep(tick);
                adapter.ReadObservation(observation);
                executed = tick + 1;
                furthest = Mathf.Max(furthest, observation.Progress);
                if (adapter.IsComplete || adapter.IsDead)
                    break;
            }
            return new BridgeReplayVerification
            {
                completed = adapter.IsComplete,
                died = adapter.IsDead,
                actionCount = actions.Count,
                executedTicks = executed,
                furthestProgress = furthest,
                finalPosition = observation.Position,
                diagnostic = adapter.IsComplete
                    ? "Native macro replay reached the Unity goal."
                    : adapter.IsDead
                        ? "Native macro replay died in Unity."
                        : "Native macro replay ended before the Unity goal."
            };
        }

        static CelesteBenchmarkPlayer FindPlayer(Arena arena)
        {
            GameObject[] roots = arena.Scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                CelesteBenchmarkPlayer player =
                    roots[i].GetComponentInChildren<CelesteBenchmarkPlayer>(true);
                if (player != null)
                    return player;
            }
            return null;
        }

        static string HierarchyPath(Transform transform)
        {
            if (!transform)
                return "<missing object>";
            string path = transform.name;
            Transform parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        static BridgeRect FromBounds(Bounds bounds) =>
            new BridgeRect(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y);
    }

    [Serializable]
    public sealed class BridgeCompatibilityReport
    {
        public bool supported;
        public int platformCount;
        public int hazardCount;
        public int playerCount;
        public int goalCount;
        public string[] errors = Array.Empty<string>();
        public string[] warnings = Array.Empty<string>();

        public string Summary()
        {
            string headline = supported
                ? $"Supported static scene: {platformCount} platforms, {hazardCount} hazards."
                : $"Unsupported scene: {errors.Length} blocking issue(s).";
            if (errors.Length > 0)
                headline += "\n- " + string.Join("\n- ", errors);
            if (warnings.Length > 0)
                headline += "\nWarnings:\n- " + string.Join("\n- ", warnings);
            return headline;
        }
    }

    [Serializable]
    public sealed class UnityTaskSnapshot
    {
        public string schemaVersion;
        public string taskId;
        public int randomizationSeed;
        public int maxTicks;
        public float fixedDeltaTime;
        public BridgeVec2 spawn;
        public BridgeRect goal;
        public BridgePlatform[] platforms = Array.Empty<BridgePlatform>();
        public BridgeRect[] hazards = Array.Empty<BridgeRect>();
        public BridgeVec2[] checkpoints = Array.Empty<BridgeVec2>();
        public BridgeMovement movement = new BridgeMovement();
    }

    [Serializable]
    public struct BridgeVec2
    {
        public float x;
        public float y;
        public BridgeVec2(float xValue, float yValue) { x = xValue; y = yValue; }
    }

    [Serializable]
    public struct BridgeRect
    {
        public float x;
        public float y;
        public float width;
        public float height;
        public BridgeRect(float xValue, float yValue, float widthValue, float heightValue)
        {
            x = xValue; y = yValue; width = widthValue; height = heightValue;
        }
    }

    [Serializable]
    public sealed class BridgePlatform
    {
        public BridgeRect rect;
        public string kind = "solid";
    }

    [Serializable]
    public sealed class BridgeMovement
    {
        public float movementSpeed;
        public float groundAcceleration;
        public float groundDeceleration;
        public float airAcceleration;
        public float airDeceleration;
        public float normalGravity;
        public float fallGravity;
        public float jumpCutGravity;
        public float maxFallSpeed;
        public float jumpVelocity;
        public float coyoteTime;
        public float jumpBufferTime;
        public int maxDashes;
        public float dashSpeed;
        public float dashLength;
        public float dashEndSpeed;
        public float dashBufferTime;
        public float dashRefillCooldown;

        public static BridgeMovement From(CelesteBenchmarkPlayer player) => new BridgeMovement
        {
            movementSpeed = player.movementSpeed,
            groundAcceleration = player.groundAcceleration,
            groundDeceleration = player.groundDeceleration,
            airAcceleration = player.airAcceleration,
            airDeceleration = player.airDeceleration,
            normalGravity = player.normalGravity,
            fallGravity = player.fallGravity,
            jumpCutGravity = player.jumpCutGravity,
            maxFallSpeed = player.maxFallSpeed,
            jumpVelocity = player.jumpVelocity,
            coyoteTime = player.coyoteTime,
            jumpBufferTime = player.jumpBufferTime,
            maxDashes = player.maxDashes,
            dashSpeed = player.dashSpeed,
            dashLength = player.dashLength,
            dashEndSpeed = player.dashEndSpeed,
            dashBufferTime = player.dashBufferTime,
            dashRefillCooldown = player.dashRefillCooldown
        };
    }

    [Serializable] public sealed class NativeTaskBundle { public NativeTask task; }
    [Serializable] public sealed class NativeTask { public NativeActionSchema actionSchema; }
    [Serializable] public sealed class NativeActionSchema { public NativeMacro[] macros; }
    [Serializable]
    public sealed class NativeMacro
    {
        public int id;
        public int ticks;
        public float moveX;
        public float moveY;
        public bool button0Pressed;
        public bool button0Held;
        public bool button1Pressed;
        public bool button2Held;
    }
    [Serializable]
    public sealed class NativeReplay
    {
        public int[] macroIds = Array.Empty<int>();
        public bool completed;
        public bool verified;
        public string diagnostic;
    }
    [Serializable]
    public sealed class BridgeReplayVerification
    {
        public bool completed;
        public bool died;
        public int actionCount;
        public int executedTicks;
        public float furthestProgress;
        public Vector2 finalPosition;
        public string diagnostic;
    }
}
