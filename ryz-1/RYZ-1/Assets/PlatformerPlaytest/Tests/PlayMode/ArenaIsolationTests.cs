using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    /// <summary>
    /// Cross-arena isolation: independent additive physics scenes must not leak state into each other, even when
    /// ticked interleaved on the same manual clock. Drives adapters directly (not EpisodeRunner) so multiple
    /// arenas can advance one tick at a time in lockstep.
    /// </summary>
    public class ArenaIsolationTests
    {
        ArenaManager arenaManager;

        [SetUp]
        public void SetUp() => arenaManager = new ArenaManager();

        [TearDown]
        public void TearDown() => arenaManager.UnloadAll();

        // ---- interleave helpers -------------------------------------------------

        sealed class Ctx
        {
            public CelesteBenchmarkAdapter adapter;
            public IAgent agent;
            public ScenarioConfig scenario;
            public readonly Observation obs = new Observation();
            public readonly List<Vector2> trace = new List<Vector2>();

            public void Init(int seed)
            {
                adapter.ResetEpisode(seed);
                agent.OnEpisodeStart(seed);
            }

            public void Step(int tick)
            {
                adapter.ReadObservation(obs);
                PlayerAction action = agent.Act(obs, tick);
                adapter.ApplyAction(in action);
                adapter.TickSimulation(scenario.fixedDeltaTime);
                adapter.AfterStep(tick);
                trace.Add(obs.Position);
            }
        }

        static void Interleave(int ticks, params Ctx[] ctxs)
        {
            foreach (Ctx c in ctxs) c.Init(0);
            for (int t = 0; t < ticks; t++)
                foreach (Ctx c in ctxs) c.Step(t);
        }

        // A scripted agent variant that holds LEFT — a deliberately different action stream from ScriptedAgent.
        sealed class HoldLeftAgent : IAgent
        {
            public void OnEpisodeStart(int seed) { }
            public PlayerAction Act(Observation obs, int tick) => new PlayerAction { MoveX = -1f };
        }

        // Does nothing: no movement input at all.
        sealed class IdleAgent : IAgent
        {
            public void OnEpisodeStart(int seed) { }
            public PlayerAction Act(Observation obs, int tick) => default;
        }

        // Holds down; presses jump exactly once on the first grounded tick to trigger one-way drop-through.
        // (Re-pressing every tick would make the still-"grounded" player jump back up instead of sinking
        // through during the drop window.)
        sealed class DropThroughAgent : IAgent
        {
            bool jumped;
            public void OnEpisodeStart(int seed) { jumped = false; }
            public PlayerAction Act(Observation obs, int tick)
            {
                bool press = !jumped && obs.IsGrounded;
                if (press) jumped = true;
                return new PlayerAction { MoveX = 0f, MoveY = -1f, JumpPressed = press, JumpHeld = false };
            }
        }

        // ---- tests --------------------------------------------------------------

        [UnityTest]
        public IEnumerator TwoArenas_IndependentTraces()
        {
            Ctx a = MakeCtxDeferred("iso-A", new ScriptedAgent(), TestLevelBuilder.MakeScenario(),
                scene => TestLevelBuilder.Build(scene, false), out Arena arenaA);
            Ctx b = MakeCtxDeferred("iso-B", new IdleAgent(), TestLevelBuilder.MakeScenario(),
                scene => TestLevelBuilder.Build(scene, false), out Arena arenaB);
            yield return null;
            a.adapter.Bind(arenaA, a.scenario);
            b.adapter.Bind(arenaB, b.scenario);

            Interleave(200, a, b);

            // B was idle the whole time while A ran a scripted rightward traversal beside it: B's player must
            // never have moved horizontally (any x drift would be cross-arena contamination).
            float spawnX = TestLevelBuilder.SpawnPosition.x;
            for (int i = 0; i < b.trace.Count; i++)
                Assert.AreEqual(spawnX, b.trace[i].x, 1e-4f, $"idle arena B drifted in x at tick {i}");

            // A actually moved (sanity: the neighbour was doing something).
            Assert.Greater(a.trace[a.trace.Count - 1].x, spawnX + 1f, "scripted arena A did not advance");
        }

        [UnityTest]
        public IEnumerator IdenticalSeedArenas_MatchWhileNeighbourDiffers()
        {
            Ctx a = MakeCtxDeferred("iso-A2", new ScriptedAgent(), TestLevelBuilder.MakeScenario(),
                scene => TestLevelBuilder.Build(scene, false), out Arena arenaA);
            Ctx c = MakeCtxDeferred("iso-C2", new ScriptedAgent(), TestLevelBuilder.MakeScenario(),
                scene => TestLevelBuilder.Build(scene, false), out Arena arenaC);
            Ctx b = MakeCtxDeferred("iso-B2", new HoldLeftAgent(), TestLevelBuilder.MakeScenario(),
                scene => TestLevelBuilder.Build(scene, false), out Arena arenaB);
            yield return null;
            a.adapter.Bind(arenaA, a.scenario);
            c.adapter.Bind(arenaC, c.scenario);
            b.adapter.Bind(arenaB, b.scenario);

            foreach (Ctx x in new[] { a, b, c }) x.Init(seed: 42);
            for (int t = 0; t < 200; t++)
            {
                a.Step(t); b.Step(t); c.Step(t); // interleaved, B differs
            }

            Assert.AreEqual(a.trace.Count, c.trace.Count);
            for (int i = 0; i < a.trace.Count; i++)
                Assert.AreEqual(a.trace[i], c.trace[i], $"identical-seed arenas A/C diverged at tick {i}");

            // B ran a different stream: its trace must genuinely differ (proves A/C match wasn't a fluke).
            bool bDiffers = false;
            for (int i = 0; i < b.trace.Count && !bDiffers; i++)
                if (b.trace[i] != a.trace[i]) bDiffers = true;
            Assert.IsTrue(bDiffers, "neighbour arena B produced the same trace despite a different action stream");
        }

        [UnityTest]
        public IEnumerator DropThrough_DoesNotLeakAcrossArenas()
        {
            Ctx a = MakeCtxDeferred("iso-drop-A", new DropThroughAgent(), TestLevelBuilder.MakeOneWayScenario(),
                TestLevelBuilder.BuildOneWay, out Arena arenaA);
            Ctx b = MakeCtxDeferred("iso-drop-B", new IdleAgent(), TestLevelBuilder.MakeOneWayScenario(),
                TestLevelBuilder.BuildOneWay, out Arena arenaB);
            yield return null;
            a.adapter.Bind(arenaA, a.scenario);
            b.adapter.Bind(arenaB, b.scenario);

            Interleave(120, a, b);

            float minA = float.MaxValue, minB = float.MaxValue;
            for (int i = 0; i < a.trace.Count; i++) minA = Mathf.Min(minA, a.trace[i].y);
            for (int i = 0; i < b.trace.Count; i++) minB = Mathf.Min(minB, b.trace[i].y);

            // A dropped through its one-way platform and kept falling.
            Assert.Less(minA, TestLevelBuilder.OneWayPlatformY - 1f,
                "arena A never dropped through its one-way platform");
            // B, standing idle on its own one-way platform, stayed up — A's per-collider IgnoreCollision
            // (T1) did not leak into B's physics world.
            Assert.Greater(minB, TestLevelBuilder.OneWayPlatformY,
                "arena B fell through its one-way platform — drop-through leaked across arenas");
        }

        [UnityTest]
        public IEnumerator ManyArenas_NoCrossContamination()
        {
            const int n = 8;
            Ctx[] ctxs = new Ctx[n];
            Arena[] arenas = new Arena[n];
            for (int i = 0; i < n; i++)
                ctxs[i] = MakeCtxDeferred($"iso-many-{i}", new ScriptedAgent(), TestLevelBuilder.MakeScenario(),
                    scene => TestLevelBuilder.Build(scene, false), out arenas[i]);
            yield return null;
            for (int i = 0; i < n; i++)
                ctxs[i].adapter.Bind(arenas[i], ctxs[i].scenario);

            foreach (Ctx x in ctxs) x.Init(seed: 5);
            for (int t = 0; t < 200; t++)
                for (int i = 0; i < n; i++) ctxs[i].Step(t);

            for (int i = 1; i < n; i++)
            {
                Assert.AreEqual(ctxs[0].trace.Count, ctxs[i].trace.Count);
                for (int t = 0; t < ctxs[0].trace.Count; t++)
                    Assert.AreEqual(ctxs[0].trace[t], ctxs[i].trace[t],
                        $"arena {i} diverged from arena 0 at tick {t}");
            }
        }

        // Creates the arena + ctx but leaves Bind to the caller (after the one-frame physics-scene yield).
        Ctx MakeCtxDeferred(string name, IAgent agent, ScenarioConfig scenario, System.Action<Scene> build, out Arena arena)
        {
            arena = arenaManager.CreateArena(name, build);
            return new Ctx { agent = agent, scenario = scenario, adapter = new CelesteBenchmarkAdapter() };
        }
    }
}
