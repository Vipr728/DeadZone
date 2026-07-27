using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using PlatformerPlaytest.Profiles;
using PlatformerPlaytest.Solver;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    /// <summary>
    /// SYNTHETIC PROFILE PLAYMODE TESTS. Written to run once the test-clone environment is free (mirrors
    /// SolverPlayModeTests wiring) — not yet executed in this session. See PlayerProfile's disclaimer: these
    /// profiles are parameterized degradations of a solver plan, not validated human models.
    /// </summary>
    public class ProfilePlayModeTests
    {
        ArenaManager arenaManager;

        [SetUp]
        public void SetUp()
        {
            arenaManager = new ArenaManager();
        }

        [TearDown]
        public void TearDown()
        {
            arenaManager.UnloadAll();
        }

        static SolverConfig LevelConfig()
        {
            SolverConfig c = SolverConfig.Default;
            c.Seed = 1;
            c.BeamWidth = 24;
            c.MaxMacrosDepth = 40;
            c.TickMenu = new[] { 5, 10, 20, 40 };
            return c;
        }

        [UnityTest]
        public IEnumerator BeginnerWorseThanExpert_OnLedgeLevel()
        {
            Arena arena = arenaManager.CreateArena("profile-arena", scene => TestLevelBuilder.Build(scene, false));
            yield return null; // physics scene registration needs one frame

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            adapter.Bind(arena, scenario);

            // Solve once with an expert-grade config to get the base plan every profile executes.
            BeamSearchSolver solver = new BeamSearchSolver();
            SolveResult solve = solver.Solve(adapter, scenario, LevelConfig());
            Assert.IsTrue(solve.Solved, $"solver failed: {solve.Diagnostic} (nodes={solve.NodesExpanded}, ticks={solve.TicksSimulated})");

            int seedCount = 10;
            int expertCompletions = RunProfile(adapter, scenario, solve.ActionStream, ProfileParams.Expert, seedCount);
            int beginnerCompletions = RunProfile(adapter, scenario, solve.ActionStream, ProfileParams.Beginner, seedCount);

            if (beginnerCompletions >= seedCount)
            {
                // Widen before weakening the assertion, per task instructions.
                seedCount = 20;
                expertCompletions = RunProfile(adapter, scenario, solve.ActionStream, ProfileParams.Expert, seedCount);
                beginnerCompletions = RunProfile(adapter, scenario, solve.ActionStream, ProfileParams.Beginner, seedCount);
            }

            Assert.GreaterOrEqual(expertCompletions, beginnerCompletions,
                $"expert ({expertCompletions}/{seedCount}) should complete at least as often as beginner ({beginnerCompletions}/{seedCount})");
            Assert.Less(beginnerCompletions, seedCount,
                $"expected some beginner degradation on the ledge level; beginner completed all {seedCount}/{seedCount} runs — " +
                "widen seed count further before weakening this assertion, do not delete it.");
        }

        static int RunProfile(CelesteBenchmarkAdapter adapter, ScenarioConfig scenario, List<PlayerAction> basePlan, ProfileParams profile, int seedCount)
        {
            int completions = 0;
            for (int seed = 0; seed < seedCount; seed++)
            {
                ProfiledAgent agent = new ProfiledAgent(basePlan, profile);
                EpisodeRunner runner = new EpisodeRunner(adapter, agent, scenario, seed: seed);
                EpisodeResult result = runner.Run();
                if (result.Outcome == Outcome.Completed)
                    completions++;
            }
            return completions;
        }
    }
}
