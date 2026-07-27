#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Playtester.EditorTool
{
    /// <summary>
    /// The Unity half of the fixed export-marker contract. A marker exists only
    /// after BuildPipeline succeeds, so infra never polls for a partial build.
    /// </summary>
    public static class ExportPanel
    {
        private static readonly IReadOnlyDictionary<string, string> SceneLevelIds =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GymScene"] = "gym",
                ["LevelA"] = "level_a",
                ["LevelB"] = "level_b",
            };

        [MenuItem("Playtester/Export Active Level For Training")]
        private static void ExportActiveLevelForTraining()
        {
            string scenePath = EditorSceneManager.GetActiveScene().path;
            string sceneName = Path.GetFileNameWithoutExtension(scenePath);
            if (string.IsNullOrEmpty(scenePath) || !SceneLevelIds.TryGetValue(sceneName, out string? levelId))
            {
                Debug.LogError("Open GymScene, LevelA, or LevelB before exporting for training.");
                return;
            }
            ExportLevelForTraining(levelId, scenePath);
        }

        public static bool ExportLevelForTraining(string levelId, string scenePath)
        {
            if (!IsSafeLevelId(levelId) || !scenePath.EndsWith(".unity", StringComparison.Ordinal))
            {
                Debug.LogError("The level ID or scene path does not satisfy the export contract.");
                return false;
            }

            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string extension = BuildExtension(EditorUserBuildSettings.activeBuildTarget);
            string buildPath = Path.Combine(projectRoot, "Builds", levelId, levelId + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(buildPath)!);
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = buildPath,
                target = EditorUserBuildSettings.activeBuildTarget,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"Build failed for {levelId}; no export marker was written.");
                return false;
            }

            string markerDirectory = Path.Combine(projectRoot, "Exports", levelId);
            Directory.CreateDirectory(markerDirectory);
            string markerPath = Path.Combine(markerDirectory, "level_export.json");
            string temporaryMarkerPath = markerPath + ".tmp";
            string repositoryRelativeBuild = $"unity/PlaytesterProject/Builds/{levelId}/{levelId}{extension}";
            string marker = JsonUtility.ToJson(new LevelExportMarker
            {
                level_id = levelId,
                build_path = repositoryRelativeBuild,
                scene_path = scenePath,
                exported_at = DateTime.UtcNow.ToString("O"),
            }, true);
            File.WriteAllText(temporaryMarkerPath, marker);
            if (File.Exists(markerPath))
            {
                File.Replace(temporaryMarkerPath, markerPath, null);
            }
            else
            {
                File.Move(temporaryMarkerPath, markerPath);
            }
            AssetDatabase.Refresh();
            Debug.Log($"Exported {levelId} for training: {markerPath}");
            return true;
        }

        private static bool IsSafeLevelId(string value)
        {
            foreach (char character in value)
            {
                if (!(char.IsLetterOrDigit(character) || character == '_' || character == '-'))
                {
                    return false;
                }
            }
            return !string.IsNullOrEmpty(value);
        }

        private static string BuildExtension(BuildTarget target)
        {
            return target switch
            {
                BuildTarget.StandaloneWindows or BuildTarget.StandaloneWindows64 => ".exe",
                BuildTarget.StandaloneLinux64 => ".x86_64",
                BuildTarget.StandaloneOSX => ".app",
                _ => throw new NotSupportedException($"No locked build extension for target {target}."),
            };
        }

        [Serializable]
        private sealed class LevelExportMarker
        {
            public string level_id = string.Empty;
            public string build_path = string.Empty;
            public string scene_path = string.Empty;
            public string exported_at = string.Empty;
        }
    }
}
#endif
