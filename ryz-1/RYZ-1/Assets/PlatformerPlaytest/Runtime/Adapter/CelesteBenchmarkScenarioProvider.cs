using System;
using System.Collections.Generic;
using CelesteBenchmark;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlatformerPlaytest
{
    /// <summary>
    /// Discovers CelesteBenchmark level metadata from the loaded arena. This is game-adapter code, not reusable
    /// core policy: another platformer supplies its own IScenarioProvider and IGameAdapter.
    ///
    /// Procedural generators are prepared before discovery and disabled from regenerating in Start, so planning,
    /// playback, telemetry, and replay all operate on the same seeded geometry.
    /// </summary>
    public sealed class CelesteBenchmarkScenarioProvider : IScenarioProvider
    {
        readonly int stepBudget;
        readonly float fixedDeltaTime;
        readonly float sectionYTolerance;

        public string ScenarioId { get; }

        public CelesteBenchmarkScenarioProvider(
            string scenarioId,
            int stepBudget = 6000,
            float fixedDeltaTime = 0.02f,
            float sectionYTolerance = 1.25f)
        {
            if (string.IsNullOrWhiteSpace(scenarioId))
                throw new ArgumentException("Scenario id must be non-empty.", nameof(scenarioId));
            if (stepBudget <= 0)
                throw new ArgumentOutOfRangeException(nameof(stepBudget));
            if (fixedDeltaTime <= 0f)
                throw new ArgumentOutOfRangeException(nameof(fixedDeltaTime));

            ScenarioId = scenarioId;
            this.stepBudget = stepBudget;
            this.fixedDeltaTime = fixedDeltaTime;
            this.sectionYTolerance = Mathf.Max(0f, sectionYTolerance);
        }

        public ScenarioConfig CreateScenario(Arena arena, int layoutSeed)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));
            if (!arena.Scene.IsValid() || !arena.Scene.isLoaded)
                throw new InvalidOperationException("Scenario discovery requires a loaded arena scene.");

            PrepareProceduralGeometry(arena.Scene, layoutSeed);

            CelesteBenchmarkPlayer player = FindSingle<CelesteBenchmarkPlayer>(arena.Scene, "player");
            BenchmarkGoal goal = FindPrimaryGoal(arena.Scene);
            List<BenchmarkCheckpoint> checkpoints = FindAll<BenchmarkCheckpoint>(arena.Scene);
            Vector2 spawn = player.startPosition == Vector2.zero
                ? (Vector2)player.transform.position
                : player.startPosition;
            Vector2 goalCentre = goal.WorldRect.center;

            bool useExplicitOrder = HasCompleteExplicitOrder(checkpoints);
            checkpoints.Sort((a, b) => CompareCheckpoints(
                a, b, spawn, goalCentre, useExplicitOrder));

            ScenarioConfig scenario = ScriptableObject.CreateInstance<ScenarioConfig>();
            scenario.name = ScenarioId;
            scenario.scenarioId = ScenarioId;
            scenario.layoutSeed = layoutSeed;
            scenario.spawnPosition = spawn;
            scenario.goalRect = goal.WorldRect;
            scenario.sectionBoundariesX = new float[checkpoints.Count];
            scenario.sectionBoundariesY = new float[checkpoints.Count];
            for (int i = 0; i < checkpoints.Count; i++)
            {
                Vector2 position = checkpoints[i].transform.position;
                scenario.sectionBoundariesX[i] = position.x;
                scenario.sectionBoundariesY[i] = position.y + checkpoints[i].respawnOffset.y;
            }
            scenario.sectionBoundaryYTolerance = sectionYTolerance;
            scenario.stepBudget = stepBudget;
            scenario.fixedDeltaTime = fixedDeltaTime;
            return scenario;
        }

        static void PrepareProceduralGeometry(Scene scene, int layoutSeed)
        {
            List<RandomLevelGenerator> generators = FindAll<RandomLevelGenerator>(scene);
            for (int i = 0; i < generators.Count; i++)
            {
                RandomLevelGenerator generator = generators[i];
                generator.GenerateOnStart = false;
                generator.RandomizeSeed = false;
                generator.Seed = layoutSeed;
                generator.GenerateFromSeed(layoutSeed);
            }
        }

        static BenchmarkGoal FindPrimaryGoal(Scene scene)
        {
            List<BenchmarkGoal> goals = FindAll<BenchmarkGoal>(scene);
            if (goals.Count == 0)
                throw new InvalidOperationException(
                    $"Scene '{scene.path}' declares no BenchmarkGoal. Add a goal marker or have the procedural " +
                    "level provider create one before scenario discovery.");

            BenchmarkGoal primary = goals[0];
            for (int i = 1; i < goals.Count; i++)
            {
                if (goals[i].Priority > primary.Priority)
                    primary = goals[i];
            }

            int samePriority = 0;
            for (int i = 0; i < goals.Count; i++)
                if (goals[i].Priority == primary.Priority)
                    samePriority++;
            if (samePriority > 1)
                throw new InvalidOperationException(
                    $"Scene '{scene.path}' has {samePriority} goals at priority {primary.Priority}. " +
                    "Goal selection must be explicit; assign one marker a higher priority.");

            return primary;
        }

        static int CompareCheckpoints(
            BenchmarkCheckpoint a,
            BenchmarkCheckpoint b,
            Vector2 spawn,
            Vector2 goal,
            bool useExplicitOrder)
        {
            if (useExplicitOrder)
                return a.SectionOrder.CompareTo(b.SectionOrder);

            Vector2 route = goal - spawn;
            if (route.sqrMagnitude < 0.0001f)
                return a.transform.position.sqrMagnitude.CompareTo(b.transform.position.sqrMagnitude);
            float aProgress = Vector2.Dot((Vector2)a.transform.position - spawn, route);
            float bProgress = Vector2.Dot((Vector2)b.transform.position - spawn, route);
            return aProgress.CompareTo(bProgress);
        }

        static bool HasCompleteExplicitOrder(List<BenchmarkCheckpoint> checkpoints)
        {
            HashSet<int> orders = new HashSet<int>();
            for (int i = 0; i < checkpoints.Count; i++)
            {
                int order = checkpoints[i].SectionOrder;
                if (order < 0 || !orders.Add(order))
                    return false;
            }
            return checkpoints.Count > 0;
        }

        static T FindSingle<T>(Scene scene, string role) where T : Component
        {
            List<T> values = FindAll<T>(scene);
            if (values.Count != 1)
                throw new InvalidOperationException(
                    $"Scene '{scene.path}' must contain exactly one {role} ({typeof(T).Name}); found {values.Count}.");
            return values[0];
        }

        static List<T> FindAll<T>(Scene scene) where T : Component
        {
            List<T> values = new List<T>();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                T[] found = roots[i].GetComponentsInChildren<T>(true);
                for (int j = 0; j < found.Length; j++)
                {
                    if (found[j].gameObject.activeInHierarchy)
                        values.Add(found[j]);
                }
            }
            return values;
        }
    }
}
