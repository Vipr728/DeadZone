using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Playtester.Gym;
using UnityEngine;

namespace Playtester.Telemetry
{
    public sealed class TelemetryRecorder : MonoBehaviour
    {
        [SerializeField] private string levelId = string.Empty;
        [SerializeField] private string stage = "stage2";
        [SerializeField] private string checkpointPath = string.Empty;
        [SerializeField] private string telemetryDirectory = string.Empty;

        private readonly List<EpisodeSummary> episodes = new();
        private EpisodeSummary currentEpisode = null!;
        private DateTime timestampStart;

        public void BeginRun(string runLevelId, string runStage, string runCheckpointPath)
        {
            levelId = runLevelId;
            stage = runStage;
            checkpointPath = runCheckpointPath;
            timestampStart = DateTime.UtcNow;
            episodes.Clear();
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] == "--telemetry-dir")
                {
                    telemetryDirectory = arguments[index + 1];
                    break;
                }
            }
        }

        public void BeginEpisode(int episodeIndex)
        {
            currentEpisode = new EpisodeSummary { episode_index = episodeIndex };
        }

        public void RecordPosition(float time, Vector2 position)
        {
            if (currentEpisode == null) return;
            currentEpisode.path_trace.Add(new PathPoint { t = time, x = position.x, y = position.y });
        }

        public void RecordPieceResult(string pieceId, string pieceType, PieceParams parameters, int attempts, float? clearTime, Vector2? deathPosition, bool seenInStage1Range)
        {
            float? width = pieceType switch
            {
                "move_to_goal" => parameters.Distance,
                "gap_jump" => parameters.Width,
                _ => null,
            };
            float? height = pieceType == "elevation" ? parameters.Height : null;
            currentEpisode.piece_results.Add(new PieceResult
            {
                piece_id = pieceId,
                piece_type = pieceType,
                parameters = new PieceParameters { width = width, height = height },
                attempts = attempts,
                time_to_clear_seconds = clearTime,
                death_position = deathPosition.HasValue ? new Position { x = deathPosition.Value.x, y = deathPosition.Value.y } : null,
                seen_in_stage1_range = seenInStage1Range,
            });
        }

        public void EndEpisode(string outcome, float totalReward, float? clearTime)
        {
            if (currentEpisode == null) return;
            currentEpisode.outcome = outcome;
            currentEpisode.total_reward = totalReward;
            currentEpisode.time_to_clear_seconds = clearTime;
            episodes.Add(currentEpisode);
            currentEpisode = null!;
        }

        /// <summary>Produces a bounded, scene-backed timeout for unattended smoke playback.</summary>
        public void RecordStandaloneTimeout()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] == "--level-id")
                    levelId = arguments[index + 1];
                else if (arguments[index] == "--checkpoint")
                    checkpointPath = arguments[index + 1];
            }
            if (timestampStart == default)
                timestampStart = DateTime.UtcNow;
            if (currentEpisode == null)
                BeginEpisode(episodes.Count);
            EndEpisode("timeout", 0f, null);
            Debug.Log($"PLAYTESTER_TELEMETRY_SMOKE_EPISODE level={levelId}");
        }

        public string WriteRun(string telemetryDirectory)
        {
            Directory.CreateDirectory(telemetryDirectory);
            TelemetryDocument document = new()
            {
                run_id = Guid.NewGuid().ToString(),
                level_id = levelId,
                stage = stage,
                checkpoint_path = checkpointPath,
                timestamp_start = timestampStart.ToString("O"),
                episode_summaries = episodes,
            };
            string outputPath = Path.Combine(telemetryDirectory, $"{levelId}_{document.run_id}.json");
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(document, Formatting.Indented));
            return outputPath;
        }

        private void OnApplicationQuit()
        {
            if (episodes.Count == 0 || string.IsNullOrWhiteSpace(levelId)) return;
            string output = telemetryDirectory;
            if (string.IsNullOrWhiteSpace(output))
                output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Telemetry"));
            WriteRun(output);
        }

        [Serializable] private sealed class TelemetryDocument { public string run_id = string.Empty; public string level_id = string.Empty; public string stage = string.Empty; public string checkpoint_path = string.Empty; public string timestamp_start = string.Empty; public List<EpisodeSummary> episode_summaries = new(); }
        [Serializable] private sealed class EpisodeSummary { public int episode_index; public string outcome = string.Empty; public float total_reward; public float? time_to_clear_seconds; public List<PathPoint> path_trace = new(); public List<PieceResult> piece_results = new(); }
        [Serializable] private sealed class PathPoint { public float t; public float x; public float y; }
        [Serializable] private sealed class PieceResult { public string piece_id = string.Empty; public string piece_type = string.Empty; [JsonProperty("params")] public PieceParameters parameters = new(); public int attempts; public float? time_to_clear_seconds; public Position? death_position; public bool seen_in_stage1_range; }
        [Serializable] private sealed class PieceParameters { public float? width; public float? height; }
        [Serializable] private sealed class Position { public float x; public float y; }
    }
}
