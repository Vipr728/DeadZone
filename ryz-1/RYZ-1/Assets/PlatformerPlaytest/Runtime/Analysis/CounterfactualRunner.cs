using System;
using System.Collections.Generic;
using PlatformerPlaytest.Profiles;
using PlatformerPlaytest.Solver;

namespace PlatformerPlaytest.Analysis
{
    /// <summary>A named synthetic profile used as a counterfactual variant's agent population.</summary>
    public struct NamedProfile
    {
        public string Name;
        public ProfileParams Params;
        public NamedProfile(string name, ProfileParams p) { Name = name; Params = p; }
    }

    /// <summary>Per-profile aggregation within one variant. Mirrors <see cref="EpisodeStats"/> fields.</summary>
    public struct ProfileOutcome
    {
        public string ProfileId;
        public float CompletionRate;
        public float MedianDeaths;
        public float MeanFurthestProgress;
    }

    /// <summary>One override value's aggregated result across all seeds and profiles.</summary>
    public struct VariantResult
    {
        public float Value;
        public bool Solved;               // did the expert solver find a plan under this override
        public float CompletionRate;      // across all profile*seed episodes
        public float MedianDeaths;
        public float MeanFurthestProgress;
        public List<ProfileOutcome> Profiles;
        public List<string> EpisodeIds;
    }

    /// <summary>Paired-experiment report: same seeds/agents across variants, only the tunable differs.</summary>
    public struct CounterfactualReport
    {
        public string TargetId;
        public string Field;
        public int[] Seeds;
        public List<VariantResult> Variants;
    }

    /// <summary>
    /// Counterfactual runner (ADR-008 / T8): sweeps ONE tunable override across ≥3 candidate values with paired
    /// seeds and profiles held fixed, so the only thing that changes between variants is the tunable. Per variant:
    /// bind a fresh adapter with the override in effect, solve once (expert / full-strength), then run every
    /// (profile, seed) pair by degrading that plan through a <see cref="ProfiledAgent"/>. Produces a comparison
    /// report; it NEVER writes the override back to any asset — variants live only in cloned scenarios inside
    /// ephemeral arenas.
    ///
    /// The caller supplies <c>bindVariant</c>: given a scenario (override already merged in) it must create a
    /// fresh arena, bind an adapter, and return it. The caller owns arena teardown (e.g. ArenaManager.UnloadAll)
    /// and any per-frame yield needed for physics-scene registration.
    /// </summary>
    public static class CounterfactualRunner
    {
        public static CounterfactualReport Run(
            Func<ScenarioConfig, IGameAdapter> bindVariant,
            ScenarioConfig baseScenario,
            string targetId, string field, float[] candidateValues,
            int[] seeds, NamedProfile[] profiles, SolverConfig solverConfig)
        {
            if (candidateValues == null || candidateValues.Length < 3)
                throw new ArgumentException("Counterfactual needs at least 3 candidate values.", nameof(candidateValues));
            if (seeds == null || seeds.Length == 0)
                throw new ArgumentException("Counterfactual needs at least one seed.", nameof(seeds));
            if (profiles == null || profiles.Length == 0)
                throw new ArgumentException("Counterfactual needs at least one profile.", nameof(profiles));

            CounterfactualReport report = new CounterfactualReport
            {
                TargetId = targetId,
                Field = field,
                Seeds = seeds,
                Variants = new List<VariantResult>(candidateValues.Length)
            };

            BeamSearchSolver solver = new BeamSearchSolver();

            for (int v = 0; v < candidateValues.Length; v++)
            {
                ScenarioConfig variantScenario = CloneWithOverride(baseScenario, targetId, field, candidateValues[v]);
                IGameAdapter adapter = bindVariant(variantScenario);

                SolveResult solve = solver.Solve(adapter, variantScenario, solverConfig);

                List<EpisodeSummary> episodes = new List<EpisodeSummary>();
                if (solve.Solved)
                {
                    List<UnityEngine.Vector2> reference =
                        ProfiledAgent.RecordTrajectory(adapter, variantScenario, solve.ActionStream);
                    for (int p = 0; p < profiles.Length; p++)
                    {
                        for (int s = 0; s < seeds.Length; s++)
                        {
                            // Same seeds and same profiles for every variant — assured by iterating the identical
                            // seeds[]/profiles[] arrays here. profileSalt = p keeps profiles distinguishable but stable.
                            ProfiledAgent agent = new ProfiledAgent(solve.ActionStream, profiles[p].Params, profileSalt: p);
                            agent.SetReferenceTrajectory(reference);
                            EpisodeRunner runner = new EpisodeRunner(adapter, agent, variantScenario, seeds[s]);
                            EpisodeResult result = runner.Run();
                            episodes.Add(ToSummary(v, profiles[p].Name, seeds[s], result));
                        }
                    }
                }

                report.Variants.Add(AggregateVariant(candidateValues[v], solve.Solved, episodes));
                adapter.RestoreOverrides();
            }

            return report;
        }

        /// <summary>Deep-ish clone of the scenario with the variable override merged (replacing any existing entry
        /// for the same target/field). Never mutates <paramref name="baseScenario"/>.</summary>
        static ScenarioConfig CloneWithOverride(ScenarioConfig baseScenario, string targetId, string field, float value)
        {
            ScenarioConfig clone = UnityEngine.ScriptableObject.CreateInstance<ScenarioConfig>();
            clone.scenarioId = baseScenario.scenarioId;
            clone.layoutSeed = baseScenario.layoutSeed;
            clone.spawnPosition = baseScenario.spawnPosition;
            clone.goalRect = baseScenario.goalRect;
            clone.sectionBoundariesX = baseScenario.sectionBoundariesX;
            clone.sectionBoundariesY = baseScenario.sectionBoundariesY;
            clone.sectionBoundaryYTolerance = baseScenario.sectionBoundaryYTolerance;
            clone.stepBudget = baseScenario.stepBudget;
            clone.fixedDeltaTime = baseScenario.fixedDeltaTime;

            clone.overrides = new List<TunableOverride>();
            if (baseScenario.overrides != null)
            {
                for (int i = 0; i < baseScenario.overrides.Count; i++)
                {
                    TunableOverride o = baseScenario.overrides[i];
                    if (o.targetId == targetId && o.field == field)
                        continue; // will be replaced by the variant value
                    clone.overrides.Add(new TunableOverride { targetId = o.targetId, field = o.field, value = o.value });
                }
            }
            clone.overrides.Add(new TunableOverride { targetId = targetId, field = field, value = value });
            return clone;
        }

        static EpisodeSummary ToSummary(int variantIndex, string profileName, int seed, EpisodeResult result)
        {
            return new EpisodeSummary
            {
                episodeId = $"cf{variantIndex}-{profileName}-s{seed}",
                agentId = "profiled",
                profileId = profileName,
                seed = seed,
                outcome = result.Outcome.ToString(),
                steps = result.Steps,
                deaths = result.Deaths,
                furthestProgress = result.FurthestProgress,
                completionTick = result.CompletionTick
            };
        }

        /// <summary>Aggregate one variant's episode summaries (pure; exposed for EditMode fixture tests).</summary>
        public static VariantResult AggregateVariant(float value, bool solved, List<EpisodeSummary> episodes)
        {
            VariantResult vr = new VariantResult
            {
                Value = value,
                Solved = solved,
                Profiles = new List<ProfileOutcome>(),
                EpisodeIds = new List<string>()
            };

            for (int i = 0; i < episodes.Count; i++)
                vr.EpisodeIds.Add(episodes[i].episodeId);

            // Per-profile breakdown reuses the existing (agentId, profileId) aggregator.
            List<EpisodeStats> byProfile = EpisodeStatsCalculator.Compute(episodes);
            for (int i = 0; i < byProfile.Count; i++)
            {
                vr.Profiles.Add(new ProfileOutcome
                {
                    ProfileId = byProfile[i].ProfileId,
                    CompletionRate = byProfile[i].CompletionRate,
                    MedianDeaths = byProfile[i].MedianDeaths,
                    MeanFurthestProgress = byProfile[i].MeanFurthestProgress
                });
            }

            // Variant-level (across all profiles).
            if (episodes.Count > 0)
            {
                int completed = 0;
                float progressSum = 0f;
                List<int> deaths = new List<int>(episodes.Count);
                for (int i = 0; i < episodes.Count; i++)
                {
                    if (episodes[i].outcome == "Completed") completed++;
                    progressSum += episodes[i].furthestProgress;
                    deaths.Add(episodes[i].deaths);
                }
                vr.CompletionRate = (float)completed / episodes.Count;
                vr.MeanFurthestProgress = progressSum / episodes.Count;
                vr.MedianDeaths = MedianInt(deaths);
            }

            return vr;
        }

        static float MedianInt(List<int> values)
        {
            List<int> sorted = new List<int>(values);
            sorted.Sort();
            int n = sorted.Count;
            if (n == 0) return 0f;
            if (n % 2 == 1) return sorted[n / 2];
            return (sorted[n / 2 - 1] + sorted[n / 2]) / 2f;
        }
    }
}
