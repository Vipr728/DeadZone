using System.Collections;
using CelesteBenchmark;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    public sealed class ScenarioDiscoveryPlayModeTests
    {
        ArenaManager arenas;

        [SetUp]
        public void SetUp() => arenas = new ArenaManager();

        [TearDown]
        public void TearDown() => arenas.UnloadAll();

        [UnityTest]
        public IEnumerator ProceduralGoal_DrivesScenarioAndCompletionWithoutCoordinateConstants()
        {
            RandomLevelGenerator generator = null;
            Arena arena = arenas.CreateArena("discovered-procedural-level", scene =>
            {
                DemoLevel.Build(scene);
                GameObject host = new GameObject("Procedural Level");
                SceneManager.MoveGameObjectToScene(host, scene);
                generator = host.AddComponent<RandomLevelGenerator>();
                generator.GenerateOnStart = false;
                generator.RandomizeSeed = false;
                generator.PlatformCount = 8;
                generator.Origin = new Vector2(20f, -2f);

                GameObject authoredCheckpoint = new GameObject("Authored Checkpoint");
                authoredCheckpoint.transform.position = new Vector2(5f, 1f);
                BoxCollider2D checkpointTrigger = authoredCheckpoint.AddComponent<BoxCollider2D>();
                checkpointTrigger.isTrigger = true;
                authoredCheckpoint.AddComponent<BenchmarkCheckpoint>();
                SceneManager.MoveGameObjectToScene(authoredCheckpoint, scene);
            });

            CelesteBenchmarkScenarioProvider provider =
                new CelesteBenchmarkScenarioProvider("procedural-fixture");
            ScenarioConfig scenario = provider.CreateScenario(arena, 731);

            Assert.IsNotNull(generator.GeneratedGoal);
            Assert.AreEqual(generator.GeneratedGoal.WorldRect, scenario.goalRect);
            Assert.AreEqual(731, scenario.layoutSeed);
            Assert.AreEqual("procedural-fixture", scenario.scenarioId);
            Assert.AreEqual(2, scenario.sectionBoundariesX.Length);
            Assert.Less(scenario.sectionBoundariesX[0], scenario.sectionBoundariesX[1],
                "mixed authored/generated checkpoints were not ordered along the discovered route");

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);
            adapter.ResetEpisode(0);

            CelesteBenchmarkPlayer player = FindPlayer(arena.Scene);
            Vector2 goal = generator.GeneratedGoal.WorldRect.center;
            player.transform.position = goal;
            player.GetComponent<Rigidbody2D>().position = goal;

            Observation observation = new Observation();
            adapter.ReadObservation(observation);
            Assert.IsTrue(adapter.IsComplete,
                "adapter ignored the discovered goal marker and used unrelated level geometry");
            yield return null;
        }

        [UnityTest]
        public IEnumerator GeometryIdentity_ChangesWithProceduralLayoutSeed()
        {
            Arena arena = arenas.CreateArena("geometry-cache-identity", scene =>
            {
                DemoLevel.Build(scene);
                GameObject host = new GameObject("Procedural Level");
                SceneManager.MoveGameObjectToScene(host, scene);
                RandomLevelGenerator generator = host.AddComponent<RandomLevelGenerator>();
                generator.GenerateOnStart = false;
                generator.PlatformCount = 8;
            });

            CelesteBenchmarkScenarioProvider provider =
                new CelesteBenchmarkScenarioProvider("procedural-fixture");
            ScenarioConfig first = provider.CreateScenario(arena, 1);
            ScenarioConfig second = provider.CreateScenario(arena, 2);

            Assert.AreNotEqual(first.CacheIdentity(), second.CacheIdentity(),
                "different generated layouts could reuse the same cached action stream");
            yield return null;
        }

        static CelesteBenchmarkPlayer FindPlayer(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                CelesteBenchmarkPlayer player =
                    roots[i].GetComponentInChildren<CelesteBenchmarkPlayer>(true);
                if (player)
                    return player;
            }
            return null;
        }
    }
}
