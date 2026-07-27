using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PlatformerPlaytest.Profiles;

namespace PlatformerPlaytest
{
    /// <summary>
    /// T12: the non-UI core of a batch run — N episodes per profile through a ProfiledAgent, driven off a single
    /// already-solved base plan, with telemetry written via RunWriter. Extracted from the Editor's
    /// DemoBatchRunnerImpl so PlayMode tests can call the exact same code path the Run tab button uses (no
    /// UnityEditor reference, no ArenaManager/solving concerns — caller owns the arena/adapter/plan).
    /// </summary>
    public static class BatchRunnerCore
    {
        public struct BatchResult
        {
            public string RunId;
            public string RunDir;
            public string Summary;
        }

        /// <summary>Runs episodesPerProfile episodes for each name in profileNames through a ProfiledAgent seeded
        /// from basePlan, recording telemetry under Library/PlatformerPlaytest/runs/&lt;runId&gt;/ with the given
        /// scenarioId in run.json.</summary>
        public static BatchResult Run(IGameAdapter adapter, ScenarioConfig scenario, string scenarioId,
            List<PlayerAction> basePlan, int episodesPerProfile, string[] profileNames)
        {
            if (episodesPerProfile < 1)
                throw new ArgumentException("episodesPerProfile must be >= 1.", nameof(episodesPerProfile));
            if (profileNames == null || profileNames.Length == 0)
                throw new ArgumentException("profileNames must be non-empty.", nameof(profileNames));

            string runId = $"{scenarioId}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}";
            RunWriter writer = new RunWriter(runId);
            writer.WriteHeader(new RunHeader
            {
                runId = runId,
                createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                scenarioId = scenarioId,
                layoutSeed = scenario.layoutSeed,
                unityVersion = UnityEngine.Application.unityVersion,
                fixedDeltaTime = scenario.fixedDeltaTime,
                profileIds = profileNames
            });

            // Closed-loop reference: the positions the base plan passes through, recorded once per batch (one
            // extra episode of simulation) and shared by every ProfiledAgent below.
            List<UnityEngine.Vector2> reference = ProfiledAgent.RecordTrajectory(adapter, scenario, basePlan);

            StringBuilder summary = new StringBuilder();
            summary.Append("Run ").Append(runId).Append('\n');

            for (int p = 0; p < profileNames.Length; p++)
            {
                ProfileParams profile = ProfileFor(profileNames[p]);
                int completed = 0, deathTotal = 0;
                for (int e = 0; e < episodesPerProfile; e++)
                {
                    int seed = e + 1;
                    string episodeId = $"{profileNames[p]}-s{seed}";
                    ProfiledAgent agent = new ProfiledAgent(basePlan, profile, profileSalt: p);
                    agent.SetReferenceTrajectory(reference);
                    TelemetryRecorder recorder = new TelemetryRecorder(runId, episodeId, recordFullTrajectory: true);

                    EpisodeRunner runner = new EpisodeRunner(adapter, agent, scenario, seed);
                    EpisodeResult result = runner.Run((tick, action, obs) =>
                        recorder.OnStep(tick, in action, obs,
                            adapter.IsDead ? "Death" : adapter.IsComplete ? "GoalReached" : null));
                    recorder.Flush();

                    if (result.Outcome == Outcome.Completed) completed++;
                    deathTotal += result.Deaths;

                    writer.AppendEpisode(new EpisodeSummary
                    {
                        episodeId = episodeId,
                        scenarioId = scenarioId,
                        layoutSeed = scenario.layoutSeed,
                        agentId = "profiled",
                        profileId = profileNames[p],
                        seed = seed,
                        outcome = result.Outcome.ToString(),
                        steps = result.Steps,
                        deaths = result.Deaths,
                        furthestProgress = result.FurthestProgress,
                        completionTick = result.CompletionTick,
                        hasFullTrajectory = true
                    });
                }
                summary.Append(profileNames[p]).Append(": ")
                    .Append(completed).Append('/').Append(episodesPerProfile).Append(" completed, ")
                    .Append(deathTotal).Append(" deaths total\n");
            }

            string runDir = PlaytestPaths.RunDir(runId);
            summary.Append("Load this run in the Results tab: ").Append(runDir);
            return new BatchResult { RunId = runId, RunDir = runDir, Summary = summary.ToString() };
        }

        static ProfileParams ProfileFor(string name)
        {
            switch (name)
            {
                case "Beginner": return ProfileParams.Beginner;
                case "Intermediate": return ProfileParams.Intermediate;
                case "Expert": return ProfileParams.Expert;
                default: return ProfileParams.Intermediate;
            }
        }
    }
}
