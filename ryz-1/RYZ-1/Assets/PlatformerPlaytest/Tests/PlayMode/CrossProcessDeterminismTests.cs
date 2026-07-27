using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    /// <summary>
    /// Cross-process determinism probe (T14). Replays a HARDCODED action stream on a cold-loaded SampleScene and
    /// writes the final position + an FNV-1a hash over the whole position trace to the file named by the
    /// PPT_DETERMINISM_OUT environment variable (skipped when unset, so it costs nothing in the normal suite).
    ///
    /// The standard test suite cannot assert cross-process exactness — that needs two Unity invocations. Run:
    ///   PPT_DETERMINISM_OUT=/tmp/a.txt Unity -batchmode -runTests -testPlatform PlayMode \
    ///     -testFilter PlatformerPlaytest.Tests.PlayMode.CrossProcessDeterminismTests
    /// twice with different output files and diff them.
    /// </summary>
    public class CrossProcessDeterminismTests
    {
        const int Ticks = 400;

        ArenaManager arenaManager;

        [SetUp]
        public void SetUp() => arenaManager = new ArenaManager();

        [TearDown]
        public void TearDown() => arenaManager.UnloadAll();

        /// <summary>Hold right; tap jump every 20 ticks. No agent logic, no solver — a fixed stream.</summary>
        static PlayerAction ActionAt(int t) => new PlayerAction
        {
            MoveX = 1f,
            JumpPressed = t % 20 == 0,
            JumpHeld = t % 20 < 8
        };

        [UnityTest]
        public IEnumerator SampleScene_FixedStream_WritesTraceFingerprint()
        {
            string outPath = System.Environment.GetEnvironmentVariable("PPT_DETERMINISM_OUT");
            if (string.IsNullOrEmpty(outPath))
                Assert.Ignore("PPT_DETERMINISM_OUT not set — cross-process probe skipped.");

            Arena arena = null;
            yield return arenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, a => arena = a);

            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);
            adapter.ResetEpisode(0);

            Observation obs = new Observation();
            List<Vector2> trace = new List<Vector2>(Ticks);
            for (int t = 0; t < Ticks; t++)
            {
                adapter.ReadObservation(obs);
                PlayerAction action = ActionAt(t);
                adapter.ApplyAction(in action);
                adapter.TickSimulation(scenario.fixedDeltaTime);
                adapter.AfterStep(t);
                trace.Add(obs.Position);
            }

            ulong h = 14695981039346656037UL;
            System.Text.StringBuilder lines = new System.Text.StringBuilder();
            for (int i = 0; i < trace.Count; i++)
            {
                h = Mix(Mix(h, BitConverter64(trace[i].x)), BitConverter64(trace[i].y));
                lines.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}\t{1:R}\t{2:R}", i, trace[i].x, trace[i].y));
            }

            Vector2 final = trace[trace.Count - 1];
            File.WriteAllText(outPath, string.Format(CultureInfo.InvariantCulture,
                "final={0:R},{1:R}\nhash={2:X16}\n", final.x, final.y, h));
            File.WriteAllText(outPath + ".trace", lines.ToString());
            Debug.Log($"[T14] final={final.x:R},{final.y:R} hash={h:X16} -> {outPath}");
        }

        /// <summary>
        /// Diagnostic: two copies of the SAME scene, same fixed stream, same process. Identical scene content,
        /// different allocation order / instance IDs. If these diverge, the divergence tracks memory layout
        /// rather than anything about the process boundary.
        /// </summary>
        [UnityTest]
        public IEnumerator SampleScene_TwoArenasSameProcess_SameStream()
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("PPT_DETERMINISM_OUT")))
                Assert.Ignore("PPT_DETERMINISM_OUT not set — cross-process probe skipped.");

            LogAssert.ignoreFailingMessages = true;
            Arena a = null, b = null;
            yield return arenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, x => a = x);
            yield return arenaManager.LoadSceneArena(SampleSceneScenario.ScenePath, x => b = x);
            LogAssert.ignoreFailingMessages = false;

            List<Vector2> ta = RunFixed(a);
            List<Vector2> tb = RunFixed(b);
            int firstDiff = -1;
            for (int i = 0; i < ta.Count && firstDiff < 0; i++)
                if (ta[i] != tb[i])
                    firstDiff = i;
            Debug.Log($"[T14] two-arena firstDiff={firstDiff} a={ta[ta.Count - 1]:R} b={tb[tb.Count - 1]:R}");
            Assert.AreEqual(-1, firstDiff, "two arenas of the same scene diverged in the same process");
        }

        List<Vector2> RunFixed(Arena arena)
        {
            ScenarioConfig scenario = SampleSceneScenario.Create(arena, 0);
            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);
            adapter.ResetEpisode(0);
            Observation obs = new Observation();
            List<Vector2> trace = new List<Vector2>(Ticks);
            for (int t = 0; t < Ticks; t++)
            {
                adapter.ReadObservation(obs);
                PlayerAction action = ActionAt(t);
                adapter.ApplyAction(in action);
                adapter.TickSimulation(scenario.fixedDeltaTime);
                adapter.AfterStep(t);
                trace.Add(obs.Position);
            }
            return trace;
        }

        // Raw bit hash — no quantization, so ANY float difference shows up.
        static ulong BitConverter64(float f) => (ulong)(uint)System.BitConverter.SingleToInt32Bits(f);

        static ulong Mix(ulong hash, ulong u)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= u & 0xFFUL;
                hash *= 1099511628211UL;
                u >>= 8;
            }
            return hash;
        }
    }
}
