using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    public class EpisodeRunnerPlayModeTests
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

        [UnityTest]
        public IEnumerator ScriptedRunReachesGoal()
        {
            Arena arena = arenaManager.CreateArena("goal-arena", scene => TestLevelBuilder.Build(scene, false));
            yield return null; // physics scene registration needs one frame

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            adapter.Bind(arena, scenario);

            EpisodeRunner runner = new EpisodeRunner(adapter, new ScriptedAgent(), scenario, seed: 1);
            EpisodeResult result = runner.Run();

            Assert.AreEqual(Outcome.Completed, result.Outcome);
            Assert.Greater(result.Steps, 0);
        }

        [UnityTest]
        public IEnumerator SeedRepeatability_TracesMatch()
        {
            Arena arena = arenaManager.CreateArena("determinism-arena", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            scenario.stepBudget = 200;
            adapter.Bind(arena, scenario);

            List<Vector2> traceA = new List<Vector2>(200);
            EpisodeRunner runnerA = new EpisodeRunner(adapter, new ScriptedAgent(), scenario, seed: 42);
            runnerA.Run((tick, action, obs) => traceA.Add(obs.Position));

            List<Vector2> traceB = new List<Vector2>(200);
            EpisodeRunner runnerB = new EpisodeRunner(adapter, new ScriptedAgent(), scenario, seed: 42);
            runnerB.Run((tick, action, obs) => traceB.Add(obs.Position));

            Assert.AreEqual(traceA.Count, traceB.Count);
            for (int i = 0; i < traceA.Count; i++)
                Assert.AreEqual(traceA[i], traceB[i], $"trace diverged at tick {i}");
        }

        [UnityTest]
        public IEnumerator DeathDetection_DiesAndRespawns()
        {
            Arena arena = arenaManager.CreateArena("death-arena", scene => TestLevelBuilder.Build(scene, true));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            scenario.stepBudget = 400;
            adapter.Bind(arena, scenario);

            EpisodeRunner runner = new EpisodeRunner(adapter, new ScriptedAgent(), scenario, seed: 7);
            EpisodeResult result = runner.Run();

            Assert.GreaterOrEqual(result.Deaths, 1);

            bool sawDeathEvent = false;
            for (int i = 0; i < result.Events.Count; i++)
            {
                if (result.Events[i].Kind == GameEventKind.Death)
                    sawDeathEvent = true;
            }
            Assert.IsTrue(sawDeathEvent);
        }

        [UnityTest]
        public IEnumerator CompletionDetection_EndsEpisodeCompleted()
        {
            Arena arena = arenaManager.CreateArena("completion-arena", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            adapter.Bind(arena, scenario);

            EpisodeRunner runner = new EpisodeRunner(adapter, new ScriptedAgent(), scenario, seed: 3);
            EpisodeResult result = runner.Run();

            Assert.AreEqual(Outcome.Completed, result.Outcome);
            Assert.GreaterOrEqual(result.CompletionTick, 0);
        }

        [UnityTest]
        public IEnumerator ResetIsolation_MatchesFreshBindTrace()
        {
            Arena arena = arenaManager.CreateArena("reset-arena", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            scenario.stepBudget = 100;
            adapter.Bind(arena, scenario);

            EpisodeRunner fullRunner = new EpisodeRunner(adapter, new ScriptedAgent(), scenario, seed: 9);
            scenario.stepBudget = 2000;
            fullRunner.Run();

            scenario.stepBudget = 100;
            List<Vector2> traceAfterFull = new List<Vector2>(100);
            EpisodeRunner secondRunner = new EpisodeRunner(adapter, new ScriptedAgent(), scenario, seed: 9);
            secondRunner.Run((tick, action, obs) => traceAfterFull.Add(obs.Position));

            Arena freshArena = arenaManager.CreateArena("reset-arena-fresh", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter freshAdapter = new CelesteBenchmarkAdapter();
            freshAdapter.Bind(freshArena, scenario);
            List<Vector2> freshTrace = new List<Vector2>(100);
            EpisodeRunner freshRunner = new EpisodeRunner(freshAdapter, new ScriptedAgent(), scenario, seed: 9);
            freshRunner.Run((tick, action, obs) => freshTrace.Add(obs.Position));

            Assert.AreEqual(freshTrace.Count, traceAfterFull.Count);
            for (int i = 0; i < freshTrace.Count; i++)
                Assert.AreEqual(freshTrace[i], traceAfterFull[i], $"trace diverged at tick {i}");
        }
    }
}
