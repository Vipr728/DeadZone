using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PlatformerPlaytest.Solver;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    /// <summary>
    /// T12: exercises the exact non-UI path the Run tab's SampleScene batch button uses
    /// (ArenaManager.LoadSceneArena + solve-or-cache + BatchRunnerCore.Run), proving a real ~628-tick traverse
    /// gets recorded, not a demo-length run. Solves fresh on a cache miss (~116s, matches
    /// SampleSceneTests.SampleScene_SegmentedSolverCompletes' config) or loads instantly on a hit.
    /// </summary>
    public class SampleSceneBatchTests
    {
        ArenaManager arenaManager;

        [SetUp]
        public void SetUp() => arenaManager = new ArenaManager();

        [TearDown]
        public void TearDown() => arenaManager.UnloadAll();

        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator SampleSceneBatch_RecordsRun()
        {
            Arena arena = null;
            yield return arenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, a => arena = a);
            Assert.IsNotNull(arena);

            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);

            string scenarioId = PlaytestLevel.SampleScene.ScenarioId();
            string key = SolutionCache.MakeKey(
                scenario.CacheIdentity(scenarioId),
                SampleSceneScenario.SolverConfig);

            List<PlayerAction> basePlan;
            long solveMs = 0;
            if (SolutionCache.TryLoad(key, out basePlan))
            {
                Debug.Log("[T12] SampleSceneBatch_RecordsRun: cache hit, skipped solving.");
            }
            else
            {
                SegmentedSolveResult result = new SegmentedSolver().Solve(adapter, scenario, SampleSceneScenario.SolverConfig);
                solveMs = result.ElapsedMs;
                Assert.IsTrue(result.Solved,
                    $"segmented solver did not clear SampleScene: {result.Diagnostic}");
                SolutionCache.Save(key, result.ActionStream);
                basePlan = result.ActionStream;
            }

            Debug.Log($"[T12] SampleSceneBatch_RecordsRun: basePlan has {basePlan.Count} ticks, solve took {solveMs} ms.");
            Assert.Greater(basePlan.Count, 100, "solved SampleScene plan is suspiciously short (demo-length?)");

            BatchRunnerCore.BatchResult batch = BatchRunnerCore.Run(
                adapter, scenario, scenarioId, basePlan, episodesPerProfile: 2, profileNames: new[] { "Expert" });

            Assert.IsTrue(Directory.Exists(batch.RunDir), $"run dir not written: {batch.RunDir}");

            string headerPath = Path.Combine(batch.RunDir, "run.json");
            Assert.IsTrue(File.Exists(headerPath), "run.json missing");
            RunHeader header = JsonUtility.FromJson<RunHeader>(File.ReadAllText(headerPath));
            Assert.AreEqual("sample-scene", header.scenarioId, "run.json scenarioId must mark this as a real-level run");

            string episodesPath = Path.Combine(batch.RunDir, "episodes.jsonl");
            Assert.IsTrue(File.Exists(episodesPath), "episodes.jsonl missing");
            string[] lines = File.ReadAllLines(episodesPath);
            List<EpisodeSummary> episodes = new List<EpisodeSummary>();
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                episodes.Add(JsonUtility.FromJson<EpisodeSummary>(line));
            }

            Assert.AreEqual(2, episodes.Count, "expected 2 episodes (2 seeds x Expert)");
            foreach (EpisodeSummary ep in episodes)
            {
                Assert.AreEqual("sample-scene", ep.scenarioId);
                Assert.Greater(ep.steps, 100,
                    $"episode {ep.episodeId} ran only {ep.steps} steps — expected a real ~628-tick SampleScene traverse, not a demo-length run");
                Debug.Log($"[T12] episode {ep.episodeId}: steps={ep.steps} outcome={ep.outcome} deaths={ep.deaths}");
            }
        }
    }
}
