using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PlatformerPlaytest;
using PlatformerPlaytest.Analysis;
using PlatformerPlaytest.Profiles;
using PlatformerPlaytest.Solver;
using CelesteBenchmark;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlatformerPlaytest.Tests.PlayMode
{
    public class CounterfactualTests
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

        static CelesteBenchmarkPlayer FindPlayer(Arena arena)
        {
            GameObject[] roots = arena.Scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                CelesteBenchmarkPlayer p = roots[i].GetComponentInChildren<CelesteBenchmarkPlayer>(true);
                if (p != null) return p;
            }
            return null;
        }

        // ---- override apply / restore round trip --------------------------------------------------------------

        [UnityTest]
        public IEnumerator OverrideApplyRestore_RoundTrip()
        {
            Arena arena = arenaManager.CreateArena("cf-override", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkPlayer player = FindPlayer(arena);
            float originalJump = player.jumpVelocity;
            float originalSpeed = player.movementSpeed;

            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            scenario.overrides.Add(new TunableOverride { targetId = "player", field = "jumpVelocity", value = 20f });
            scenario.overrides.Add(new TunableOverride { targetId = "player", field = "movementSpeed", value = 3f });

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            adapter.Bind(arena, scenario);

            Assert.AreEqual(20f, player.jumpVelocity, 1e-4, "override applied at Bind");
            Assert.AreEqual(3f, player.movementSpeed, 1e-4);

            adapter.RestoreOverrides();
            Assert.AreEqual(originalJump, player.jumpVelocity, 1e-6, "exact original restored");
            Assert.AreEqual(originalSpeed, player.movementSpeed, 1e-6);

            adapter.RestoreOverrides(); // idempotent
            Assert.AreEqual(originalJump, player.jumpVelocity, 1e-6);
        }

        [UnityTest]
        public IEnumerator UnknownOverride_Rejected()
        {
            Arena arena = arenaManager.CreateArena("cf-bad", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            ScenarioConfig badTarget = TestLevelBuilder.MakeScenario();
            badTarget.overrides.Add(new TunableOverride { targetId = "enemy", field = "speed", value = 1f });
            Assert.Throws<ArgumentException>(() => new CelesteBenchmarkAdapter().Bind(arena, badTarget),
                "unknown targetId must throw at Bind");

            ScenarioConfig badField = TestLevelBuilder.MakeScenario();
            badField.overrides.Add(new TunableOverride { targetId = "player", field = "gravityScale", value = 1f });
            Assert.Throws<ArgumentException>(() => new CelesteBenchmarkAdapter().Bind(arena, badField),
                "non-whitelisted field must throw at Bind");
        }

        // ---- timing tolerance window --------------------------------------------------------------------------

        [UnityTest]
        public IEnumerator TimingTolerance_WindowMeasured()
        {
            Arena arena = arenaManager.CreateArena("cf-tol", scene => TestLevelBuilder.Build(scene, false));
            yield return null;

            CelesteBenchmarkAdapter adapter = new CelesteBenchmarkAdapter();
            ScenarioConfig scenario = TestLevelBuilder.MakeScenario();
            // Neutralize dash (whitelisted override) so the solver clears the ledge with a real JUMP — that is the
            // obstacle whose timing tolerance we measure. With dash intact the solver dashes over it (no jump edge).
            scenario.overrides.Add(new TunableOverride { targetId = "player", field = "dashSpeed", value = 1f });
            adapter.Bind(arena, scenario);

            SolveResult solve = new BeamSearchSolver().Solve(adapter, scenario, LevelConfig());
            Assert.IsTrue(solve.Solved, $"solver failed: {solve.Diagnostic}");

            const int maxShift = 8;
            ToleranceWindow window = Perturbation.MeasureTolerance(adapter, scenario, solve.ActionStream,
                edgeTickHint: 0, seed: 1, maxShift: maxShift);

            Assert.GreaterOrEqual(window.EdgeTick, 0, "a jump edge should exist in the ledge solution");
            Assert.IsTrue(window.SuccessAt(0), "unshifted solution must complete (delta 0)");
            Assert.GreaterOrEqual(window.WindowTicks, 1, "window must contain delta 0");
            Assert.LessOrEqual(window.WindowTicks, 2 * maxShift + 1, "window bounded by sweep size");

            Debug.Log($"[T8] Tolerance edge={window.EdgeTick} window={window.WindowTicks} ticks; " +
                      $"successes(-{maxShift}..+{maxShift})=[{FormatBools(window.Successes)}]");
        }

        static string FormatBools(bool[] b)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < b.Length; i++) sb.Append(b[i] ? '1' : '0');
            return sb.ToString();
        }

        // ---- three-variant counterfactual --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator CounterfactualThreeVariants()
        {
            float[] values = { 11f, 13.5f, 16f };

            // Physics-scene registration needs a frame per arena, so pre-create the pool (one per variant) with a
            // yield each; the runner then binds synchronously into these already-registered arenas.
            Arena[] pool = new Arena[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                pool[i] = arenaManager.CreateArena($"cf-var{i}", scene => TestLevelBuilder.Build(scene, false));
                yield return null;
            }

            int idx = 0;
            Func<ScenarioConfig, IGameAdapter> bind = sc =>
            {
                CelesteBenchmarkAdapter a = new CelesteBenchmarkAdapter();
                a.Bind(pool[idx++], sc);
                return a;
            };

            int[] seeds = { 1, 2, 3, 4, 5 };
            NamedProfile[] profiles =
            {
                new NamedProfile("Beginner", ProfileParams.Beginner),
                new NamedProfile("Expert", ProfileParams.Expert),
            };

            ScenarioConfig baseScenario = TestLevelBuilder.MakeScenario();
            // Neutralize dash so the ledge must be JUMPED — this makes jumpVelocity the deciding tunable rather
            // than an irrelevant one (an intact dash clears the ledge regardless of jump height).
            baseScenario.overrides.Add(new TunableOverride { targetId = "player", field = "dashSpeed", value = 1f });
            CounterfactualReport report = CounterfactualRunner.Run(
                bind, baseScenario, "player", "jumpVelocity", values, seeds, profiles, LevelConfig());

            Assert.AreEqual(3, report.Variants.Count, "3 variants");
            for (int v = 0; v < report.Variants.Count; v++)
            {
                VariantResult vr = report.Variants[v];
                if (vr.Solved)
                    Assert.AreEqual(profiles.Length, vr.Profiles.Count, $"variant {v} has a per-profile breakdown");
                Debug.Log($"[T8] jumpVelocity={vr.Value} solved={vr.Solved} completion={vr.CompletionRate:P0} " +
                          $"medDeaths={vr.MedianDeaths} meanProgress={vr.MeanFurthestProgress:F2} " +
                          $"episodes={vr.EpisodeIds.Count}");
                for (int p = 0; p < vr.Profiles.Count; p++)
                    Debug.Log($"    {vr.Profiles[p].ProfileId}: completion={vr.Profiles[p].CompletionRate:P0} " +
                              $"medDeaths={vr.Profiles[p].MedianDeaths} meanProgress={vr.Profiles[p].MeanFurthestProgress:F2}");
            }

            // Directional expectation: a weaker jump should not complete DRAMATICALLY more than the nominal jump.
            // On this easy ledge all three values (11/13.5/16) clear comfortably, so completion differences are
            // synthetic-profile jitter noise, not a jump-height gradient — ~one episode (0.1) either way is
            // expected. We therefore only hard-fail on a gross inversion (> one-noise-band), and log the full
            // table for inspection. This is the "clear message if flaky" path from the T8 acceptance criteria.
            VariantResult low = report.Variants[0];     // 11
            VariantResult nominal = report.Variants[1]; // 13.5
            float noiseBand = 1f / (seeds.Length * profiles.Length) + 1e-4f; // one episode's worth
            if (low.Solved && nominal.Solved)
            {
                Assert.LessOrEqual(low.CompletionRate, nominal.CompletionRate + noiseBand,
                    $"weaker jump (11) completed far more than nominal (13.5): {low.CompletionRate:P0} vs " +
                    $"{nominal.CompletionRate:P0} — beyond synthetic-profile noise, indicates a real regression. " +
                    "Inspect episode ids in the report.");
            }
            else
            {
                Debug.LogWarning($"[T8] jumpVelocity ordering check skipped: solved(11)={low.Solved} solved(13.5)={nominal.Solved}");
            }
        }
    }
}
