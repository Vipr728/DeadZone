using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest.Analysis;
using PlatformerPlaytest.Profiles;
using PlatformerPlaytest.Solver;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    /// <summary>
    /// T11: the REAL SampleScene, not the in-code demo level. Proves the tool can load the authored scene as an
    /// isolated arena, that the level stays tick-deterministic with crumble/refill/moving platforms live, and how
    /// far the segmented solver gets across the full ~115-unit traverse.
    /// Play Mode only — additive LoadSceneParameters loading is not available in Edit Mode.
    /// </summary>
    public class SampleSceneTests
    {
        ArenaManager arenaManager;

        [SetUp]
        public void SetUp() => arenaManager = new ArenaManager();

        [TearDown]
        public void TearDown() => arenaManager.UnloadAll();

        IEnumerator LoadArena(Action<Arena> onLoaded)
        {
            yield return arenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, onLoaded);
        }

        static List<Vector2> RunTrace(CelesteBenchmarkAdapter adapter, ScenarioConfig scenario, IAgent agent,
            int seed, int ticks)
        {
            List<Vector2> trace = new List<Vector2>(ticks);
            Observation obs = new Observation();
            adapter.ResetEpisode(seed);
            agent.OnEpisodeStart(seed);
            for (int t = 0; t < ticks; t++)
            {
                adapter.ReadObservation(obs);
                PlayerAction action = agent.Act(obs, t);
                adapter.ApplyAction(in action);
                adapter.TickSimulation(scenario.fixedDeltaTime);
                adapter.AfterStep(t);
                trace.Add(obs.Position);
            }
            return trace;
        }

        // ---- Phase 1: the scene loads as an isolated arena ----------------------------------------------------

        [UnityTest]
        public IEnumerator SampleScene_LoadsAsIsolatedArena()
        {
            Arena arena = null;
            yield return LoadArena(a => arena = a);

            Assert.IsNotNull(arena, "LoadSceneArena produced no arena");
            Assert.IsTrue(arena.Scene.isLoaded, "loaded scene is not marked loaded");

            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);

            Observation obs = new Observation();
            adapter.ResetEpisode(0);
            adapter.ReadObservation(obs);
            Vector2 start = obs.Position;
            Assert.AreEqual(scenario.spawnPosition.x, start.x, 0.01f,
                "player did not bind at the discovered spawn");

            List<Vector2> trace = RunTrace(adapter, scenario, new ScriptedAgent(), 0, 100);
            Assert.Greater(trace[trace.Count - 1].x, start.x + 1f,
                "player did not move right over 100 ticks in the loaded SampleScene");
        }

        /// <summary>Two simultaneous copies of the SAME scene must be independent worlds.</summary>
        [UnityTest]
        public IEnumerator SampleScene_DuplicateAdditiveLoadsAreIndependent()
        {
            // URP's Light2DManager LogErrors on a second global Light2D. The light is registered in OnEnable
            // during scene integration, i.e. before any of our code can disable it, so this error is inherent to
            // loading two copies of a lit scene. It is cosmetic (arenas never render) — ArenaManager disables the
            // lights right after load.
            LogAssert.ignoreFailingMessages = true;

            Arena a = null, b = null;
            yield return LoadArena(x => a = x);
            yield return LoadArena(x => b = x);
            LogAssert.ignoreFailingMessages = false;

            Assert.AreNotEqual(a.Scene.handle, b.Scene.handle, "duplicate additive load returned the same scene");

            ScenarioConfig scenarioA = SampleSceneScenario.Create(a, 0);
            ScenarioConfig scenarioB = SampleSceneScenario.Create(b, 0);
            CelesteBenchmarkAdapter adapterA = new CelesteBenchmarkAdapter();
            CelesteBenchmarkAdapter adapterB = new CelesteBenchmarkAdapter();
            adapterA.Bind(a, scenarioA);
            adapterB.Bind(b, scenarioB);

            // Advance only arena A; arena B must not move.
            Observation obsB = new Observation();
            adapterB.ResetEpisode(0);
            adapterB.ReadObservation(obsB);
            Vector2 bStart = obsB.Position;

            RunTrace(adapterA, scenarioA, new ScriptedAgent(), 0, 120);

            adapterB.ReadObservation(obsB);
            Assert.AreEqual(bStart.x, obsB.Position.x, 1e-4f, "arena B moved while only arena A was ticked");
        }

        // ---- Phase 2: determinism on the real level, with crumble/refill/platforms live ------------------------

        [UnityTest]
        public IEnumerator SampleScene_DeterministicAcrossResets()
        {
            Arena arena = null;
            yield return LoadArena(a => arena = a);

            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);

            const int Ticks = 700;
            List<Vector2> first = RunTrace(adapter, scenario, new ScriptedAgent(), 7, Ticks);
            List<Vector2> second = RunTrace(adapter, scenario, new ScriptedAgent(), 7, Ticks);

            for (int t = 0; t < Ticks; t++)
            {
                Assert.AreEqual(first[t].x, second[t].x, 0f, $"x diverged at tick {t}");
                Assert.AreEqual(first[t].y, second[t].y, 0f, $"y diverged at tick {t}");
            }
        }


        // ---- Phase 5: replay fidelity on the real level -------------------------------------------------------

        [UnityTest]
        public IEnumerator SampleScene_ReplayZeroDesync()
        {
            Arena arena = null;
            yield return LoadArena(a => arena = a);

            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            scenario.stepBudget = 900;
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);

            TelemetryRecorder recorder = new TelemetryRecorder(
                "sample-" + Guid.NewGuid().ToString("N").Substring(0, 8), "1", recordFullTrajectory: true);
            List<PlayerAction> actions = new List<PlayerAction>();

            new EpisodeRunner(adapter, new ScriptedAgent(), scenario, 3).Run((tick, action, obs) =>
            {
                actions.Add(action);
                string ev = adapter.IsDead ? "Death" : (adapter.IsComplete ? "GoalReached" : null);
                recorder.OnStep(tick, in action, obs, ev);
            });

            ReplayVerifier verifier = new ReplayVerifier(recorder.Keyframes);
            new EpisodeRunner(adapter, new ReplayAgent(actions), scenario, 3)
                .Run((tick, action, obs) => verifier.OnStep(tick, obs));

            Assert.AreEqual(-1, verifier.Report.FirstDesyncTick,
                $"replay desync on SampleScene: {verifier.Report.FieldDeltas}");
        }

        // ---- Phase 4/5: segmented solver over the full traverse -----------------------------------------------

        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator SampleScene_SegmentedSolverCompletes()
        {
            Arena arena = null;
            yield return LoadArena(a => arena = a);

            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);

            SolverConfig config = SolverConfig.Default;
            config.BeamWidth = 20;
            config.MaxMacrosDepth = 50;
            config.TickMenu = new[] { 4, 8, 16, 32 };
            config.MaxTicksSimulated = 4_000_000;   // per segment
            config.Seed = 0;

            SegmentedSolveResult result = new SegmentedSolver().Solve(adapter, scenario, config);

            Debug.Log($"[T11] segmented solve: solved={result.Solved} " +
                      $"segments={result.SegmentsSolved}/{result.SegmentCount} failedSegment={result.FailedSegment} " +
                      $"furthestX={result.FurthestX:F2} streamTicks={result.ActionStream.Count} " +
                      $"ticksSimulated={result.TicksSimulated} elapsedMs={result.ElapsedMs} " +
                      $"diag={result.Diagnostic}");

            Assert.IsTrue(result.Solved,
                $"segmented solver did not clear SampleScene. {result.Diagnostic}");

            // Difficulty analysis on the real level, reusing the solved stream as the profiles' base plan so the
            // 2-minute solve is paid once. Beginner/Expert x 3 seeds.
            List<DeathRecord> deaths = new List<DeathRecord>();
            RunProfile(adapter, scenario, result.ActionStream, ProfileParams.Beginner, "beg", deaths);
            RunProfile(adapter, scenario, result.ActionStream, ProfileParams.Expert, "exp", deaths);

            int sectionCount = scenario.sectionBoundariesX.Length + 1;
            List<SectionStats> stats = SectionStatsCalculator.Compute(deaths, sectionCount);
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[T11] deaths by section:");
            int occupied = 0, empty = 0;
            for (int s = 0; s < stats.Count; s++)
            {
                sb.Append($" s{s}={stats[s].DeathCount}({stats[s].FailureShare:P0})");
                if (stats[s].DeathCount > 0) occupied++; else empty++;
            }
            Debug.Log(sb.ToString());

            Assert.Greater(deaths.Count, 0, "profile batch on the real level produced no deaths at all");
            Assert.Greater(empty, 0,
                "deaths were spread over every section — no clustering, so the difficulty signal is meaningless");
        }

        static void RunProfile(CelesteBenchmarkAdapter adapter, ScenarioConfig scenario,
            List<PlayerAction> basePlan, ProfileParams profile, string tag, List<DeathRecord> into)
        {
            for (int seed = 1; seed <= 3; seed++)
            {
                DeathLogger logger = new DeathLogger(adapter, $"{tag}-{seed}");
                new EpisodeRunner(adapter, new ProfiledAgent(basePlan, profile, seed), scenario, seed)
                    .Run(logger.OnStep);
                into.AddRange(logger.Records);
            }
        }
    }
}
