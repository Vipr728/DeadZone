#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Playtester.Agent;
using Playtester.Gym;
using UnityEditor;
using UnityEngine;

namespace Playtester.EditorTool
{
    /// <summary>Imports the locked RL YAML source into generated Unity assets.</summary>
    public static class SyncConfigFromYaml
    {
        private const string ConfigDirectory = "Assets/Configs";
        private const string PieceAssetPath = ConfigDirectory + "/PieceLibraryConfig.asset";
        private const string RewardAssetPath = ConfigDirectory + "/RewardConfig.asset";
        private const string ObservationAssetPath = ConfigDirectory + "/ObservationConfig.asset";

        [MenuItem("Playtester/Sync Config From RL YAML")]
        public static void Sync()
        {
            string repoRoot = Directory.GetParent(Application.dataPath)!.Parent!.Parent!.FullName;
            string[] pieceLines = File.ReadAllLines(Path.Combine(repoRoot, "rl/configs/piece_config.yaml"));
            string[] rewardLines = File.ReadAllLines(Path.Combine(repoRoot, "rl/configs/reward_config.yaml"));
            string[] observationLines = File.ReadAllLines(Path.Combine(repoRoot, "rl/configs/observation_config.yaml"));
            PieceLibraryConfig pieceAsset = LoadOrCreate<PieceLibraryConfig>(PieceAssetPath);
            pieceAsset.SetGeneratedValues(
                ReadBool(pieceLines, "enabled", occurrence: 2),
                ReadInt(pieceLines, "pieces_per_episode"),
                ReadBool(pieceLines, "boundary_velocity_reset"),
                ReadRange(pieceLines, "width_range"),
                ReadRange(pieceLines, "distance_range"),
                ReadRange(pieceLines, "height_range"));
            RewardConfigAsset rewardAsset = LoadOrCreate<RewardConfigAsset>(RewardAssetPath);
            rewardAsset.SetGeneratedValues(
                ReadString(rewardLines, "active_strategy"),
                ReadFloat(rewardLines, "progress_reward_scale", occurrence: 1),
                ReadFloat(rewardLines, "time_penalty", occurrence: 1),
                ReadFloat(rewardLines, "piece_completion_bonus"),
                ReadFloat(rewardLines, "final_sequence_bonus"),
                ReadFloat(rewardLines, "death_penalty", occurrence: 1),
                ReadInt(rewardLines, "max_steps"));
            ObservationConfigAsset observationAsset = LoadOrCreate<ObservationConfigAsset>(ObservationAssetPath);
            observationAsset.SetGeneratedValues(
                ReadInt(observationLines, "grid_size"),
                ReadBool(observationLines, "include_velocity"),
                ReadBool(observationLines, "include_grounded_flag"));
            EditorUtility.SetDirty(pieceAsset);
            EditorUtility.SetDirty(rewardAsset);
            EditorUtility.SetDirty(observationAsset);
            AssetDatabase.SaveAssets();
            Debug.Log("Synced rl/configs YAML into generated Unity configuration assets.");
        }

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            T? asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null)
            {
                return asset;
            }
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static string ReadString(string[] lines, string key, int occurrence = 0)
        {
            string line = FindLine(lines, key, occurrence);
            return line.Split(':', 2)[1].Split('#')[0].Trim().Trim('"');
        }

        private static bool ReadBool(string[] lines, string key, int occurrence = 0) =>
            bool.Parse(ReadString(lines, key, occurrence));

        private static int ReadInt(string[] lines, string key, int occurrence = 0) =>
            int.Parse(ReadString(lines, key, occurrence), System.Globalization.CultureInfo.InvariantCulture);

        private static float ReadFloat(string[] lines, string key, int occurrence = 0) =>
            float.Parse(ReadString(lines, key, occurrence), System.Globalization.CultureInfo.InvariantCulture);

        private static Vector2 ReadRange(string[] lines, string key)
        {
            Match match = Regex.Match(FindLine(lines, key, 0), @"\[\s*(?<min>-?[\d.]+)\s*,\s*(?<max>-?[\d.]+)\s*\]");
            if (!match.Success)
            {
                throw new InvalidDataException($"Could not parse {key} range from RL YAML.");
            }
            return new Vector2(
                float.Parse(match.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(match.Groups["max"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        private static string FindLine(string[] lines, string key, int occurrence)
        {
            string[] matches = lines.Where(line => line.TrimStart().StartsWith(key + ":", StringComparison.Ordinal)).ToArray();
            if (matches.Length <= occurrence)
            {
                throw new InvalidDataException($"Missing {key} in RL YAML.");
            }
            return matches[occurrence];
        }
    }
}
#endif
