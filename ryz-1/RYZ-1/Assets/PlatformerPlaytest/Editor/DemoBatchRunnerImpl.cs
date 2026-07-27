using System;
using CelesteBenchmark;
using PlatformerPlaytest.Solver;
using UnityEditor;
using UnityEngine;

namespace PlatformerPlaytest.Editor
{
    /// <summary>
    /// Real implementation of the Run tab's batch button (T8, extended T12 for the real SampleScene level).
    /// Builds an ephemeral arena, plans one expert solution (solved fresh for Demo, cached via CachedSolver for
    /// SampleScene since that solve takes ~116s), then runs N episodes per selected profile through a
    /// ProfiledAgent via <see cref="BatchRunnerCore"/>, recording telemetry under Library/PlatformerPlaytest/runs/.
    ///
    /// EDIT-MODE PHYSICS (Demo level only): manual PhysicsScene2D.Simulate on an in-code arena is attempted
    /// directly. A cheap probe (hold-right for a few ticks, check the player advanced) decides up front whether
    /// edit-mode physics actually steps; if it does not, we return a clear "run via PlayMode tests" message rather
    /// than writing meaningless zero-motion data. SampleScene always requires Play Mode: LoadSceneArena is
    /// Play-Mode-only regardless of the edit-mode physics probe result.
    /// </summary>
    [InitializeOnLoad]
    public static class DemoBatchRunnerImpl
    {
        static DemoBatchRunnerImpl()
        {
            DemoBatchRunner.Execute = RunBatch;
        }

        /// <summary>CLI probe (batchmode -executeMethod): runs a tiny Demo batch and logs the verdict. Edit-mode only.</summary>
        public static void CliProbe()
        {
            string result = DemoBatchRunner.Run(2, new[] { "Beginner", "Expert" }, PlaytestLevel.Demo);
            Debug.Log("[T8-DEMO]\n" + result);
        }

        static string RunBatch(int episodesPerProfile, string[] profileNames, PlaytestLevel level)
        {
            if (episodesPerProfile < 1)
                return "Episodes must be >= 1.";
            if (profileNames == null || profileNames.Length == 0)
                return "Select at least one profile.";

            if (level == PlaytestLevel.SampleScene)
                return "SampleScene batches require an async scene load. Use the Run tab's button (it drives " +
                       "ArenaManager.LoadSceneArena via EditorCoroutinePump), not DemoBatchRunner.Run directly.";

            return RunDemoBatch(episodesPerProfile, profileNames);
        }

        static string RunDemoBatch(int episodesPerProfile, string[] profileNames)
        {
            // Verdict (measured, see T8 report): arenas use SceneManager.CreateScene(LocalPhysicsMode.Physics2D),
            // which is Play-Mode-only ("This can only be used during play mode"). Manual PhysicsScene2D.Simulate is
            // therefore unreachable in edit mode. Rather than fabricate data, direct the user to the PlayMode path.
            if (!Application.isPlaying)
            {
                return "In-editor batch runs require Play Mode: arena scenes use " +
                       "SceneManager.CreateScene(LocalPhysicsMode.Physics2D), which Unity only allows during play. " +
                       "Enter Play Mode and click again, or run batches via the PlayMode tests " +
                       "(Tests/PlayMode/CounterfactualTests.cs / EpisodeRunnerPlayModeTests.cs) and load the " +
                       "resulting run with the Results tab.";
            }

            ArenaManager arenas = new ArenaManager();
            try
            {
                Arena arena = arenas.CreateArena("demo-batch", DemoLevel.Build);
                CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
                ScenarioConfig scenario = DemoLevel.MakeScenario();
                adapter.Bind(arena, scenario);

                if (!PhysicsAdvances(adapter, scenario))
                {
                    return "Edit-mode physics did not advance (PhysicsScene2D.Simulate is a no-op outside Play " +
                           "Mode on this project). Run batches via PlayMode tests " +
                           "(Tests/PlayMode/CounterfactualTests.cs / EpisodeRunnerPlayModeTests.cs) and load the " +
                           "resulting run with the Results tab.";
                }

                SolverConfig solverConfig = SolverConfig.Default;
                solverConfig.Seed = 1;
                solverConfig.MaxMacrosDepth = 40;
                SolveResult solve = new BeamSearchSolver().Solve(adapter, scenario, solverConfig);
                if (!solve.Solved)
                    return $"Solver failed to plan the demo level in edit mode: {solve.Diagnostic} " +
                           $"(nodes={solve.NodesExpanded}, ticks={solve.TicksSimulated}).";

                BatchRunnerCore.BatchResult result = BatchRunnerCore.Run(
                    adapter, scenario, PlaytestLevel.Demo.ScenarioId(), solve.ActionStream, episodesPerProfile, profileNames);
                return result.Summary;
            }
            catch (Exception ex)
            {
                return "Batch run failed: " + ex.Message;
            }
            finally
            {
                arenas.UnloadAll();
            }
        }

        /// <summary>SampleScene batch on an already-loaded arena (Play Mode). Called by PlaytestWindow's Run tab
        /// once its EditorCoroutinePump has finished ArenaManager.LoadSceneArena; the arena's lifetime is the
        /// caller's responsibility. Solves-or-loads via CachedSolver (progress bar on a miss) then delegates to
        /// BatchRunnerCore, same as the Demo path.</summary>
        public static string RunSampleSceneBatch(
            Arena arena,
            int episodesPerProfile,
            string[] profileNames,
            int layoutSeed = 0)
        {
            if (episodesPerProfile < 1)
                return "Episodes must be >= 1.";
            if (profileNames == null || profileNames.Length == 0)
                return "Select at least one profile.";

            try
            {
                CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
                ScenarioConfig scenario = SampleSceneScenario.Create(arena, layoutSeed);
                adapter.Bind(arena, scenario);

                if (!CachedSolver.TrySolve(adapter, scenario, PlaytestLevel.SampleScene.ScenarioId(),
                        SampleSceneScenario.SolverConfig, out var stream, out string solveStatus))
                    return solveStatus;

                BatchRunnerCore.BatchResult result = BatchRunnerCore.Run(
                    adapter, scenario, scenario.scenarioId, stream, episodesPerProfile, profileNames);
                return solveStatus + "\n" + result.Summary;
            }
            catch (Exception ex)
            {
                return "Batch run failed: " + ex.Message;
            }
        }

        // Probe: hold right for a handful of ticks and check the player's x actually moved. Cheap, honest test of
        // whether manual physics stepping works in the current (edit-mode) context.
        static bool PhysicsAdvances(IGameAdapter adapter, ScenarioConfig scenario)
        {
            adapter.ResetEpisode(0);
            Observation obs = new Observation();
            adapter.ReadObservation(obs);
            float startX = obs.Position.x;
            PlayerAction right = PlayerAction.Neutral;
            right.MoveX = 1f;
            for (int i = 0; i < 40; i++)
            {
                adapter.ReadObservation(obs);
                adapter.ApplyAction(in right);
                adapter.TickSimulation(scenario.fixedDeltaTime);
                adapter.AfterStep(i);
            }
            adapter.ReadObservation(obs);
            return obs.Position.x - startX > 0.3f;
        }
    }
}
