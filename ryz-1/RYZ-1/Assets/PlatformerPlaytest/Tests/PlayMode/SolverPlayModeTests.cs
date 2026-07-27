using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using PlatformerPlaytest.Solver;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    public class SolverPlayModeTests
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
        public IEnumerator SolverCompletesTestLevel()
        {
            Arena arena = arenaManager.CreateArena("solver-arena", scene => TestLevelBuilder.Build(scene, false));
            yield return null; // physics scene registration needs one frame

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            adapter.Bind(arena, scenario);

            BeamSearchSolver solver = new BeamSearchSolver();
            SolveResult solve = solver.Solve(adapter, scenario, LevelConfig());

            Assert.IsTrue(solve.Solved, $"solver failed: {solve.Diagnostic} (nodes={solve.NodesExpanded}, ticks={solve.TicksSimulated})");
            Assert.Greater(solve.ActionStream.Count, 0);

            // The planned stream, replayed through the live episode loop, must actually complete.
            EpisodeRunner runner = new EpisodeRunner(adapter, new ReplayAgent(solve.ActionStream), scenario, seed: 1);
            EpisodeResult result = runner.Run();

            Assert.AreEqual(Outcome.Completed, result.Outcome, "replay of solver stream did not complete the level");
        }

        [UnityTest]
        public IEnumerator SolverSolutionReplaysExactly()
        {
            Arena arena = arenaManager.CreateArena("solver-replay-arena", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            adapter.Bind(arena, scenario);

            BeamSearchSolver solver = new BeamSearchSolver();
            SolveResult solve = solver.Solve(adapter, scenario, LevelConfig());
            Assert.IsTrue(solve.Solved, $"solver failed: {solve.Diagnostic}");

            List<Vector2> traceA = new List<Vector2>();
            EpisodeRunner runnerA = new EpisodeRunner(adapter, new ReplayAgent(solve.ActionStream), scenario, seed: 1);
            runnerA.Run((tick, action, obs) => traceA.Add(obs.Position));

            List<Vector2> traceB = new List<Vector2>();
            EpisodeRunner runnerB = new EpisodeRunner(adapter, new ReplayAgent(solve.ActionStream), scenario, seed: 1);
            runnerB.Run((tick, action, obs) => traceB.Add(obs.Position));

            Assert.AreEqual(traceA.Count, traceB.Count);
            for (int i = 0; i < traceA.Count; i++)
                Assert.AreEqual(traceA[i], traceB[i], $"replay trace diverged at tick {i}");
        }
    }
}
