using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest.Live;
using PlatformerPlaytest.Profiles;
using PlatformerPlaytest.Solver;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    public class LivePlaybackTests
    {
        ArenaManager arenaManager;

        [SetUp]
        public void SetUp() => arenaManager = new ArenaManager();

        [TearDown]
        public void TearDown() => arenaManager.UnloadAll();

        static SolverConfig LevelConfig()
        {
            SolverConfig c = SolverConfig.Default;
            c.Seed = 1;
            c.BeamWidth = 24;
            c.MaxMacrosDepth = 40;
            c.TickMenu = new[] { 5, 10, 20, 40 };
            return c;
        }

        (LivePlaybackDriver driver, SolveResult solve, ScenarioConfig scenario) MakeDriver(string arenaName, int seed)
        {
            Arena arena = arenaManager.CreateArena(arenaName, DemoLevel.Build);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = DemoLevel.MakeScenario();
            adapter.Bind(arena, scenario);

            SolveResult solve = new BeamSearchSolver().Solve(adapter, scenario, LevelConfig());
            Assert.IsTrue(solve.Solved, $"solver failed: {solve.Diagnostic}");

            GameObject host = new GameObject("driver");
            SceneManager.MoveGameObjectToScene(host, arena.Scene);
            LivePlaybackDriver driver = host.AddComponent<LivePlaybackDriver>();
            driver.Adapter = adapter;
            driver.Agent = new ReplayAgent(solve.ActionStream);
            driver.Scenario = scenario;
            driver.Seed = seed;
            return (driver, solve, scenario);
        }

        [UnityTest]
        public IEnumerator LiveDriver_TicksAndCompletes()
        {
            (LivePlaybackDriver driver, _, _) = MakeDriver("live-completes", 1);
            driver.SetSpeed(8f);
            driver.Play();

            int frameBudget = 600;
            while (!driver.IsComplete && frameBudget-- > 0)
                yield return new WaitForFixedUpdate();

            Assert.IsTrue(driver.IsComplete, "driver did not finish within the frame budget");
            Assert.Greater(driver.CurrentTick, 0);
        }

        [UnityTest]
        public IEnumerator LiveDriver_PauseStopsProgress()
        {
            (LivePlaybackDriver driver, _, _) = MakeDriver("live-pause", 1);
            driver.Pause();

            for (int i = 0; i < 10; i++)
                yield return new WaitForFixedUpdate();
            Assert.AreEqual(0, driver.CurrentTick, "paused driver should not tick via FixedUpdate");

            driver.StepOnce();
            Assert.AreEqual(1, driver.CurrentTick, "StepOnce should advance exactly one tick");
        }

        [UnityTest]
        public IEnumerator LiveDriver_PlaybackTickLimit_StopsAtRecordedActionCount()
        {
            (LivePlaybackDriver driver, _, _) = MakeDriver("live-recorded-limit", 1);
            driver.PlaybackTickLimit = 3;
            driver.Pause();

            driver.StepOnce();
            driver.StepOnce();
            driver.StepOnce();

            Assert.AreEqual(3, driver.CurrentTick);
            Assert.IsTrue(driver.IsComplete, "recorded replay must stop when its action stream ends");
            yield return null;
        }

        [UnityTest]
        public IEnumerator LiveDriver_RestartResetsState()
        {
            (LivePlaybackDriver driver, _, ScenarioConfig scenario) = MakeDriver("live-restart", 1);
            driver.SetSpeed(4f);
            driver.Play();

            while (driver.CurrentTick < 50)
                yield return new WaitForFixedUpdate();

            driver.Restart();
            Assert.AreEqual(0, driver.CurrentTick);

            // ReadObservation happens at the start of the next tick; step once (from spawn) to confirm the player
            // is actually back at the scenario's spawn position, not just that the tick counter reset.
            driver.Pause();
            driver.StepOnce();
            Assert.AreEqual(1, driver.CurrentTick);
            Assert.AreEqual(scenario.spawnPosition, (Vector2)driver.LastObservation.Position);
        }

        [UnityTest]
        public IEnumerator LiveDriver_MatchesHeadlessTrace()
        {
            // Headless trace via EpisodeRunner, same seed/agent.
            Arena headlessArena = arenaManager.CreateArena("headless-trace", DemoLevel.Build);
            CelesteBenchmarkAdapter headlessAdapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = DemoLevel.MakeScenario();
            headlessAdapter.Bind(headlessArena, scenario);
            SolveResult solve = new BeamSearchSolver().Solve(headlessAdapter, scenario, LevelConfig());
            Assert.IsTrue(solve.Solved, $"solver failed: {solve.Diagnostic}");

            List<Vector2> headlessTrace = new List<Vector2>();
            EpisodeRunner runner = new EpisodeRunner(headlessAdapter, new ReplayAgent(solve.ActionStream), scenario, seed: 1);
            runner.Run((tick, action, obs) => headlessTrace.Add(obs.Position));

            // Live trace via the driver, same action stream/seed, ticked in real FixedUpdate frames at high speed.
            Arena liveArena = arenaManager.CreateArena("live-trace", DemoLevel.Build);
            CelesteBenchmarkAdapter liveAdapter = new CelesteBenchmarkAdapter();
            liveAdapter.Bind(liveArena, scenario);
            GameObject host = new GameObject("driver");
            SceneManager.MoveGameObjectToScene(host, liveArena.Scene);
            LivePlaybackDriver driver = host.AddComponent<LivePlaybackDriver>();
            driver.Adapter = liveAdapter;
            driver.Agent = new ReplayAgent(solve.ActionStream);
            driver.Scenario = scenario;
            driver.Seed = 1;
            driver.SetSpeed(16f);

            List<Vector2> liveTrace = new List<Vector2>();
            driver.Ticked += _ => liveTrace.Add(driver.LastObservation.Position);
            driver.Play();

            int frameBudget = 600;
            while (!driver.IsComplete && frameBudget-- > 0)
                yield return new WaitForFixedUpdate();

            Assert.IsTrue(driver.IsComplete, "live driver did not finish within the frame budget");
            Assert.AreEqual(headlessTrace.Count, liveTrace.Count, "live and headless traces have different lengths");
            for (int i = 0; i < headlessTrace.Count; i++)
                Assert.AreEqual(headlessTrace[i], liveTrace[i], $"trace diverged at tick {i}");
        }

        [UnityTest]
        public IEnumerator DemoLevel_ProducesDeaths()
        {
            Arena arena = arenaManager.CreateArena("demo-deaths", DemoLevel.Build);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = DemoLevel.MakeScenario();
            adapter.Bind(arena, scenario);

            SolveResult solve = new BeamSearchSolver().Solve(adapter, scenario, LevelConfig());
            Assert.IsTrue(solve.Solved, $"solver failed: {solve.Diagnostic}");

            int totalDeaths = 0;
            int completions = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                ProfiledAgent beginner = new ProfiledAgent(solve.ActionStream, ProfileParams.Beginner, profileSalt: 0);
                EpisodeRunner runner = new EpisodeRunner(adapter, beginner, scenario, seed);
                EpisodeResult result = runner.Run();
                totalDeaths += result.Deaths;
                if (result.Outcome == Outcome.Completed)
                    completions++;
            }

            Assert.Greater(totalDeaths, 0, "beginner profile should die at least once on the demo level's spike pit");

            int expertCompletions = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                ProfiledAgent expert = new ProfiledAgent(solve.ActionStream, ProfileParams.Expert, profileSalt: 0);
                EpisodeRunner runner = new EpisodeRunner(adapter, expert, scenario, seed);
                EpisodeResult result = runner.Run();
                if (result.Outcome == Outcome.Completed)
                    expertCompletions++;
            }
            Assert.GreaterOrEqual(expertCompletions, 4, "expert profile should still complete most episodes");

            yield break;
        }
    }
}
