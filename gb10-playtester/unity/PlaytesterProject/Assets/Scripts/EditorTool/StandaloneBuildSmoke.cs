#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Playtester.EditorTool
{
    /// <summary>Builds real level players at the locked build-layout paths.</summary>
    public static class StandaloneBuildSmoke
    {
        [MenuItem("Playtester/Build Stage 1 Gym Player")]
        public static void BuildGym()
        {
            Build("gym", "Assets/Scenes/GymScene.unity");
        }

        [MenuItem("Playtester/Build Level A Smoke Player")]
        public static void BuildLevelA()
        {
            Build("level_a", "Assets/Scenes/LevelA.unity");
        }

        [MenuItem("Playtester/Build Level B Smoke Player")]
        public static void BuildLevelB()
        {
            Build("level_b", "Assets/Scenes/LevelB.unity");
        }

        private static void Build(string levelId, string scenePath)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
                throw new InvalidOperationException("Run with -buildTarget StandaloneOSX for the laptop smoke build.");
            if (!ExportPanel.ExportLevelForTraining(levelId, scenePath))
                throw new InvalidOperationException($"{levelId} standalone build failed.");
            Debug.Log($"PLAYTESTER_STANDALONE_BUILD_PASS level={levelId}");
        }
    }
}
#endif
