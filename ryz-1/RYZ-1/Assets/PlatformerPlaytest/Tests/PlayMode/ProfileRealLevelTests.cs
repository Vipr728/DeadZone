using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using PlatformerPlaytest.Profiles;
using PlatformerPlaytest.Solver;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace PlatformerPlaytest.Tests.PlayMode
{
    /// <summary>
    /// T13 acceptance: synthetic profiles must work on the REAL 115-unit SampleScene, not just the 10-unit demo
    /// level. Open-loop replay of a jittered plan desyncs from the moving platforms / crumble chain within a few
    /// ticks; the closed loop (deviation detection + re-anchor + reactive recovery in ProfiledAgent) is what these
    /// tests prove.
    ///
    /// All tests share one solved plan loaded from SolutionCache with SampleSceneScenario.SolverConfig — on a
    /// cache miss the first test pays the ~116 s segmented solve once and saves it.
    /// </summary>
    public class ProfileRealLevelTests
    {
        ArenaManager arenaManager;

        [SetUp]
        public void SetUp() => arenaManager = new ArenaManager();

        [TearDown]
        public void TearDown() => arenaManager.UnloadAll();

        struct Fixture
        {
            public CelesteBenchmarkAdapter Adapter;
            public ScenarioConfig Scenario;
            public List<PlayerAction> BasePlan;
            public List<Vector2> Reference;
        }

        IEnumerator LoadFixture(System.Action<Fixture> onReady)
        {
            Arena arena = null;
            yield return arenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, a => arena = a);
            Assert.IsNotNull(arena, "SampleScene did not load as an arena");

            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);

            string key = SolutionCache.MakeKey(
                scenario.CacheIdentity(PlaytestLevel.SampleScene.ScenarioId()),
                SampleSceneScenario.SolverConfig);
            List<PlayerAction> basePlan;
            if (!SolutionCache.TryLoad(key, out basePlan))
            {
                SegmentedSolveResult solve = new SegmentedSolver().Solve(adapter, scenario, SampleSceneScenario.SolverConfig);
                Assert.IsTrue(solve.Solved, $"segmented solver did not clear SampleScene: {solve.Diagnostic}");
                SolutionCache.Save(key, solve.ActionStream);
                basePlan = solve.ActionStream;
            }
            Assert.Greater(basePlan.Count, 100, "solved SampleScene plan is suspiciously short");

            List<Vector2> reference = ProfiledAgent.RecordTrajectory(adapter, scenario, basePlan);
            ulong refHash = 1469598103934665603UL;
            for (int i = 0; i < reference.Count; i++)
            {
                refHash = (refHash ^ (ulong)System.BitConverter.SingleToInt32Bits(reference[i].x)) * 1099511628211UL;
                refHash = (refHash ^ (ulong)System.BitConverter.SingleToInt32Bits(reference[i].y)) * 1099511628211UL;
            }
            Debug.Log($"[T13] reference trajectory: {reference.Count} points, hash={refHash:x16}, last={reference[reference.Count - 1]}");

            onReady(new Fixture
            {
                Adapter = adapter,
                Scenario = scenario,
                BasePlan = basePlan,
                Reference = reference
            });
        }

        static ProfiledAgent MakeAgent(in Fixture f, ProfileParams profile, int salt)
        {
            ProfiledAgent agent = new ProfiledAgent(f.BasePlan, profile, salt);
            agent.SetReferenceTrajectory(f.Reference);
            return agent;
        }

        /// <summary>Completions out of seedCount, logging where the failures stalled.</summary>
        static int RunSeeds(in Fixture f, ProfileParams profile, string label, int seedCount, int salt)
        {
            int completions = 0;
            for (int seed = 1; seed <= seedCount; seed++)
            {
                ProfiledAgent agent = MakeAgent(f, profile, salt);
                float furthestX = float.NegativeInfinity;
                EpisodeResult result = new EpisodeRunner(f.Adapter, agent, f.Scenario, seed)
                    .Run((tick, action, obs) => { if (obs.Position.x > furthestX) furthestX = obs.Position.x; });

                if (result.Outcome == Outcome.Completed) completions++;
                else
                    Debug.Log($"[T13] {label} seed {seed}: {result.Outcome} at furthest x={furthestX:F2} " +
                              $"(steps={result.Steps} deaths={result.Deaths} observedDeaths={agent.ObservedDeaths})");
            }
            Debug.Log($"[T13] {label}: {completions}/{seedCount} completed");
            return completions;
        }

        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator Expert_CompletesSampleScene()
        {
            Fixture f = default;
            yield return LoadFixture(x => f = x);

            Stopwatch sw = Stopwatch.StartNew();
            const int Seeds = 5;
            int completions = RunSeeds(f, ProfileParams.Expert, "Expert", Seeds, salt: 2);
            sw.Stop();
            Debug.Log($"[T13] Expert_CompletesSampleScene wall time {sw.ElapsedMilliseconds} ms");

            Assert.Greater(completions * 2, Seeds,
                $"Expert completed only {completions}/{Seeds} SampleScene runs. Expert is near-perfect execution — " +
                "if it cannot mostly finish the real level the closed loop is still broken. Do not weaken this.");
        }

        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator ProfileOrdering_OnSampleScene()
        {
            Fixture f = default;
            yield return LoadFixture(x => f = x);

            Stopwatch sw = Stopwatch.StartNew();
            const int Seeds = 6;
            int beginner = RunSeeds(f, ProfileParams.Beginner, "Beginner", Seeds, salt: 0);
            int intermediate = RunSeeds(f, ProfileParams.Intermediate, "Intermediate", Seeds, salt: 1);
            int expert = RunSeeds(f, ProfileParams.Expert, "Expert", Seeds, salt: 2);
            sw.Stop();
            Debug.Log($"[T13] completion rates over {Seeds} seeds: " +
                      $"Beginner={beginner}/{Seeds} Intermediate={intermediate}/{Seeds} Expert={expert}/{Seeds} " +
                      $"(wall {sw.ElapsedMilliseconds} ms)");

            Assert.LessOrEqual(beginner, intermediate,
                $"completion rate not monotone: Beginner {beginner} > Intermediate {intermediate}");
            Assert.LessOrEqual(intermediate, expert,
                $"completion rate not monotone: Intermediate {intermediate} > Expert {expert}");
            Assert.Less(beginner, Seeds,
                $"Beginner completed all {Seeds}/{Seeds} runs — the degradation model does not bite on the real " +
                "level. Raise the seed count before touching this assertion; never delete it.");
        }

        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator ClosedLoop_IsDeterministic()
        {
            Fixture f = default;
            yield return LoadFixture(x => f = x);

            List<Vector2> first = Trace(f, ProfileParams.Intermediate, seed: 4);
            List<Vector2> second = Trace(f, ProfileParams.Intermediate, seed: 4);

            Assert.AreEqual(first.Count, second.Count, "same seed produced different episode lengths");
            for (int t = 0; t < first.Count; t++)
            {
                Assert.AreEqual(first[t].x, second[t].x, 0f, $"x diverged at tick {t}");
                Assert.AreEqual(first[t].y, second[t].y, 0f, $"y diverged at tick {t}");
            }
        }

        static List<Vector2> Trace(in Fixture f, ProfileParams profile, int seed)
        {
            List<Vector2> trace = new List<Vector2>(1024);
            new EpisodeRunner(f.Adapter, MakeAgent(f, profile, 1), f.Scenario, seed)
                .Run((tick, action, obs) => trace.Add(obs.Position));
            return trace;
        }

        /// <summary>
        /// Forces a deviation the plan cannot absorb: for the first 20 ticks the agent's output is overridden
        /// with "run left", dragging it backwards off the reference route (which only ever goes right from spawn).
        /// Proves recovery genuinely re-acquires the route rather than merely not crashing.
        /// </summary>
        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator Recovery_ActuallyRecovers()
        {
            Fixture f = default;
            yield return LoadFixture(x => f = x);

            ProfileParams profile = ProfileParams.Expert;
            ProfiledAgent agent = MakeAgent(f, profile, 3);
            const int BlindTicks = 20;

            Vector2 lastPosition = Vector2.zero;
            float worstDeviationAfterBlind = 0f;
            float bestDeviationAfterRecovery = float.MaxValue;
            EpisodeResult result = new EpisodeRunner(f.Adapter, new PerturbedAgent(agent, BlindTicks), f.Scenario, 5)
                .Run((tick, action, obs) =>
                {
                    lastPosition = obs.Position;
                    if (tick < BlindTicks) return;
                    float d = NearestReferenceDistance(f.Reference, obs.Position);
                    if (tick < BlindTicks + 5 && d > worstDeviationAfterBlind) worstDeviationAfterBlind = d;
                    if (tick >= BlindTicks + 5 && d < bestDeviationAfterRecovery) bestDeviationAfterRecovery = d;
                });

            Debug.Log($"[T13] Recovery_ActuallyRecovers: outcome={result.Outcome} steps={result.Steps} " +
                      $"deviationAtInjection={worstDeviationAfterBlind:F2} bestDeviationAfter={bestDeviationAfterRecovery:F2} lastPos={lastPosition}");

            Assert.Greater(worstDeviationAfterBlind, profile.deviationToleranceUnits,
                "the injected perturbation did not actually knock the agent off route — test proves nothing");
            Assert.Less(bestDeviationAfterRecovery, profile.deviationToleranceUnits,
                $"agent never got back within {profile.deviationToleranceUnits} units of the reference trajectory " +
                $"(best was {bestDeviationAfterRecovery:F2})");
            Assert.AreEqual(Outcome.Completed, result.Outcome,
                "agent recovered onto the route but did not finish the level after the perturbation");
        }

        static float NearestReferenceDistance(List<Vector2> reference, Vector2 position)
        {
            float best = float.MaxValue;
            for (int i = 0; i < reference.Count; i++)
            {
                float d = (reference[i] - position).sqrMagnitude;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }

        /// <summary>Wraps an agent and forces "run left" for the first N ticks — a deviation injector.</summary>
        sealed class PerturbedAgent : IAgent
        {
            readonly IAgent inner;
            readonly int blindTicks;

            public PerturbedAgent(IAgent inner, int blindTicks)
            {
                this.inner = inner;
                this.blindTicks = blindTicks;
            }

            public void OnEpisodeStart(int seed) => inner.OnEpisodeStart(seed);

            public PlayerAction Act(Observation obs, int tick)
            {
                PlayerAction a = inner.Act(obs, tick);
                if (tick >= blindTicks)
                    return a;
                PlayerAction left = PlayerAction.Neutral;
                left.MoveX = -1f;
                return left;
            }
        }
    }
}
