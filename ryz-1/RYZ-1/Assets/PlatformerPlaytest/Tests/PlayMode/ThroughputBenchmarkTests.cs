using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace PlatformerPlaytest.Tests.PlayMode
{
    /// <summary>
    /// Multi-arena throughput benchmark. For each arena count it runs scripted episodes (one per arena),
    /// sequentially, and records aggregate simulated-ticks/sec, episodes/min, peak arenas alive, reset latency,
    /// and memory. Numbers are emitted as a markdown table via Debug.Log (prefix "BENCH|") and TestContext so a
    /// batch-mode log can be parsed into docs/benchmarks/arena-throughput.md.
    ///
    /// This is a normal (non-[Explicit]) test but time-bounded: the whole sweep runs well under the 3-min budget.
    /// Assertions are sanity-only (ticks/sec &gt; 0, isolation spot-check); the real deliverable is the table.
    /// </summary>
    public class ThroughputBenchmarkTests
    {
        static readonly int[] ArenaCounts = { 1, 2, 4, 8, 16, 32 };
        const int StepBudget = 500;

        [UnityTest]
        public IEnumerator Throughput_Sweep()
        {
            Debug.Log($"BENCH|META|cpus={UnityEngine.SystemInfo.processorCount}|cpu={UnityEngine.SystemInfo.processorType}|os={UnityEngine.SystemInfo.operatingSystem}|unity={UnityEngine.Application.unityVersion}");
            Debug.Log("BENCH|HEADER|arenas|simTicks|wallSec|ticksPerSec|episodesPerMin|peakArenas|resetMs|gcMB|profilerMB");

            foreach (int count in ArenaCounts)
            {
                ArenaManager manager = new ArenaManager();
                Arena[] arenas = new Arena[count];
                CelesteBenchmarkAdapter[] adapters = new CelesteBenchmarkAdapter[count];
                ScenarioConfig[] scenarios = new ScenarioConfig[count];

                for (int i = 0; i < count; i++)
                {
                    scenarios[i] = TestLevelBuilder.MakeScenario();
                    scenarios[i].stepBudget = StepBudget;
                    arenas[i] = manager.CreateArena($"bench-{count}-{i}", scene => TestLevelBuilder.Build(scene, false));
                }
                yield return null; // physics-scene registration

                for (int i = 0; i < count; i++)
                {
                    adapters[i] = new CelesteBenchmarkAdapter();
                    adapters[i].Bind(arenas[i], scenarios[i]);
                }

                int peakArenas = manager.Count;

                // Reset latency: average wall time of one ResetEpisode across all arenas.
                Stopwatch resetSw = Stopwatch.StartNew();
                for (int i = 0; i < count; i++)
                    adapters[i].ResetEpisode(seed: 1);
                resetSw.Stop();
                double resetMs = resetSw.Elapsed.TotalMilliseconds / count;

                // Run one episode per arena, sequentially; aggregate simulated ticks over wall time.
                long simTicks = 0;
                var finalProgress = new List<float>(count);
                Stopwatch runSw = Stopwatch.StartNew();
                for (int i = 0; i < count; i++)
                {
                    EpisodeRunner runner = new EpisodeRunner(adapters[i], new ScriptedAgent(), scenarios[i], seed: 1);
                    EpisodeResult result = runner.Run();
                    simTicks += result.Steps;
                    finalProgress.Add(result.FurthestProgress);
                }
                runSw.Stop();

                double wallSec = runSw.Elapsed.TotalSeconds;
                double ticksPerSec = wallSec > 0 ? simTicks / wallSec : 0;
                double episodesPerMin = wallSec > 0 ? count / (wallSec / 60.0) : 0;

                long gcBytes = System.GC.GetTotalMemory(false);
                long profilerBytes = Profiler.GetTotalAllocatedMemoryLong();
                double gcMB = gcBytes / (1024.0 * 1024.0);
                double profilerMB = profilerBytes / (1024.0 * 1024.0);

                string row = $"BENCH|ROW|{count}|{simTicks}|{wallSec:F3}|{ticksPerSec:F0}|{episodesPerMin:F1}|{peakArenas}|{resetMs:F3}|{gcMB:F1}|{profilerMB:F1}";
                Debug.Log(row);
                TestContext.Out.WriteLine(row);

                // Sanity + isolation spot-check.
                Assert.Greater(ticksPerSec, 0, $"count {count}: no throughput measured");
                Assert.AreEqual(count, peakArenas, "peak arenas alive mismatch");
                if (count >= 2)
                    Assert.AreEqual(finalProgress[0], finalProgress[1], 1e-4f,
                        $"count {count}: same-seed arenas 0 and 1 produced different progress (isolation broken)");

                manager.UnloadAll();
                yield return null;
            }
        }
    }
}
